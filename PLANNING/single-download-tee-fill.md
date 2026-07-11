# Single-download cache fill (tee the install stream)

**Status:** implemented in `XboxCacheProxyService.cs` (compiles clean) — awaiting live test (reinstall Donut County then Rematch through the proxy)
**Owner file:** `Services/XboxCacheProxyService.cs`
**Goal:** On the seeding PC, stop downloading each Xbox package twice. Capture the encrypted
`.msixvc` *during* the install download (tee it to disk), so the skeleton is built from that
single download and the package **always matches the installed version**.

## Problem (confirmed against current code)

On a cache MISS, `Handle` (`XboxCacheProxyService.cs:255`) does **two independent internet downloads**
of the same package:

1. **Live forward** (`:329`–`:381`): streams CDN → the Xbox Store so the install proceeds. The body
   is written **only** to `res.OutputStream` (loop `:368`–`:375`) — never to disk.
2. **`StartFill`** (called at `:343`, defined `:399`–`:455`): opens a **separate** connection and
   re-downloads the **entire** `.msixvc` into `<file>.part`, promoting to the final cache file only when
   `written == Content-Length` (`:428`–`:437`).

So the seeder pulls each package from the internet **~2×**. Observed consequences on this machine:

- **Unreliable for big packages.** The fill uses `_bg` with a **10-minute timeout** (`:81`). A 10.5 GB
  package can't finish in 10 min → the fill errors/stalls, leaving orphaned `.part` files
  (`SLOCLAP.ProjectRuntime_1.204.6.0_…msixvc.part` at 422 MB and 6585 MB were both stuck this way).
- **Version drift → useless skeleton.** Because the fill is a *second, later* download, it can land a
  different version than what was installed (or never complete), so the watcher falls back to a
  *different-version* complete package. Rematch captured a **v1.204.5.0** package against a **v1.204.6.0**
  install → almost nothing reconstructible → **10,065 MB skeleton from a 10,567 MB package (~5% saved)**,
  vs Donut County's matched-version **8.7 MB from 302 MB (97% saved)**.

The install stream in (1) already carries the exact encrypted bytes we want to cache in (2) — we throw
them away instead of teeing them to disk.

## Why it was originally built with a separate fill

The install download uses **HTTP Range requests**: the Store asks for byte ranges, possibly out of order,
across many parallel connections, and is **not guaranteed to request every byte** (trailing padding,
skipped regions). `StartFill` sidesteps this by doing one clean sequential `GET` and only publishing a
**complete, Content-Length-verified** file. A truncated cache entry would break installs/Verify on every
other LAN PC, so completeness is non-negotiable. The revised design keeps that guarantee.

## Design: tee-with-gap-fill

Capture the package *during* the install; only promote when provably complete.

### Per-object fill state
Keyed by the final cache path (`file`). Created on the first MISS for the object, shared by all concurrent
ranged requests for it.

```csharp
private sealed class FillState {
    public string File = "", Part = "", Host = "", RawPath = "", Ip = "";
    public long Total = -1;                 // learned from first response (Content-Range total, or 200 Content-Length)
    public FileStream? Sparse;              // single owner; all tee-writes go through Lock
    public readonly List<(long s, long e)> Filled = new(); // merged, sorted, half-open [s,e)
    public bool Promoted;
    public DateTime LastWriteUtc = DateTime.UtcNow;
    public readonly object Lock = new();
}
private readonly Dictionary<string, FillState> _fills = new(StringComparer.OrdinalIgnoreCase);
```

### 1. Tee the live (ranged) stream
In the MISS non-peer branch, replace the `StartFill(...)` call (`:343`) with `GetOrCreateFill(file, host,
rawPath, ip)`. From the upstream response, learn `Total` once: from `Content-Range: bytes s-e/total`
(206) or `Content-Length` (200 → base offset 0). In the forward loop (`:368`–`:375`), after
`res.OutputStream.Write(buf,0,rn)`, also tee to disk:

```csharp
lock(st.Lock){
    if(!st.Promoted && st.Sparse!=null){
        st.Sparse.Seek(absOffset, SeekOrigin.Begin);
        st.Sparse.Write(buf, 0, rn);
    }
}
absOffset += rn;
```
where `absOffset` starts at the range start (206) or 0 (200). After the loop, `AddRange(st, start, sent)`
(merge interval), set `LastWriteUtc`, then `TryPromote(st)`.

**Single shared `Sparse` handle guarded by `Lock`** (not a handle per request): simpler and correct; the
bottleneck is the CDN/network, not the local seek+write. Create the file once under `_gate`
(`SetLength(Total)`; NTFS zero-fills lazily, so a 10 GB `SetLength` is cheap).

### 2. Completeness check → atomic promote
`Filled` is kept merged. When it is the single interval `[0, Total)`, promote under `Lock`:
flush + close `Sparse`, `File.Move(part, file)` (atomic, same volume), set `Promoted=true`, remove from
`_fills`, `Interlocked.Increment(ref Cached)`, log `FILL DONE (tee)`. The existing cache
`FileSystemWatcher` → `SkeletonWatcherService.OnCachePackage` then fires auto-capture. Once the final file
exists, later requests hit the HIT branch (`:273`) automatically.

### 3. Idle gap-fill fallback
A background sweep (single timer thread started in `StartAsync`, stopped in `StopAsync`) scans `_fills`
every ~10 s. For any object with `Total>0`, not promoted, and `now-LastWriteUtc > 30 s` (install went
quiet) but incomplete: compute the **complement** of `Filled` within `[0,Total)` and fetch **only those
gaps** with ranged `GET`s to the CDN (`Range: bytes=gs-ge`), teeing them into `Sparse` and `AddRange`.
When complete, `TryPromote`. This is the generalized replacement for `StartFill` — it fills *missing
ranges* instead of the whole object, so it typically transfers KB–MB, not GB.

## Correctness invariants (must hold)

- **Never promote while any byte in `[0,Total)` is unfilled.** Promotion is gated solely on the merged
  `Filled` covering `[0,Total)`. This preserves today's completeness guarantee.
- **Never serve from `.part`.** HIT branch only serves the final `file`. Promotion is an atomic rename.
- **`Total` unknown → fall back to old `StartFill`.** If neither `Content-Range` total nor `Content-Length`
  is present, we can't size the sparse file safely; do the current whole-object fill instead.
- **Thread-safe.** `_fills` get-or-create under `_gate`; per-object `Sparse`+`Filled` under `st.Lock`.
- **Corruption-safe on failure.** A request that errors mid-write just doesn't mark its range filled; the
  gap-fill or a later request covers it. Nothing is promoted early.

## Edge cases

- **App restart mid-fill:** in-memory `FillState` is lost and a stale `.part` on disk has unknown-valid
  ranges. On `GetOrCreateFill`, if a `.part` exists from a prior run, **delete it** and start fresh (v1).
  (v2 nicety: persist `Filled` to a `.part.ranges` sidecar for resumable fills.)
- **Peer-origin path unchanged** (`viaPeer`, `:334`–`:339`): transient, no disk fill, no tee.
- **Already cached:** if `file` exists, it's a HIT — no tee.
- **Duplicate/overlapping ranges** (Store retries): re-writing identical bytes is harmless; interval merge
  dedups the range set.
- **`_bg` timeout:** gap-fill requests are per-range (small) so the 10-min timeout is a non-issue; the main
  path is the install itself (no artificial timeout on the Store's own connection).

## Touch points (current line numbers)

- `XboxCacheProxyService.cs`
  - Fields near `:43`–`:69`: add `_fills`, and a gap-fill timer/CancellationToken.
  - `StartAsync` `:119`–`:139`: start the gap-fill sweep thread. `StopAsync` `:144`–`:157`: stop it + close
    any open `Sparse` handles, discard in-flight `.part`s.
  - `Handle` MISS non-peer branch `:340`–`:345`: replace `StartFill(...)` with `GetOrCreateFill(...)`;
    capture `Total` from the response headers already parsed at `:354`–`:359`.
  - Forward loop `:368`–`:375`: add the tee write.
  - After the loop `:376`: `AddRange` + `TryPromote`.
  - `StartFill` `:399`: keep as the **unknown-total fallback**; add a sibling `GapFill(st)` (ranged) for the
    idle fallback. Remove the unconditional `StartFill` on every MISS.
- `SkeletonWatcherService`: **no change** — it already reacts to a completed `.msixvc` in the cache root.

## Test plan

1. **Small game, sequential (Donut County ~302 MB):** reinstall through the proxy. Expect one download,
   `FILL DONE (tee)`, auto-capture, tiny skeleton. Confirm the cached `.msixvc` size == Content-Length and
   `xvdtool` opens it (hash-valid).
2. **Big game (Rematch ~10.5 GB):** uninstall + reinstall through the proxy. Expect **no second 10 GB pull**
   (watch proxy `Bytes`/network), a complete promote, and a matched-version capture → **small** skeleton.
3. **Sparse-request game:** verify the idle gap-fill triggers only for the unrequested tail and the file
   still promotes complete.
4. **Interrupted install:** kill the install mid-way → `.part` stays unpromoted, no corruption; resuming or
   re-installing completes it; a stale `.part` from a prior run is discarded on the next MISS.
5. **Other LAN PC HIT:** after a tee-promoted file exists, a second PC installs entirely from cache (HITs),
   Gaming Services Verify passes.

## Rollout / safety

This touches the path where a bug **corrupts the cache** and breaks installs/updates on every LAN PC. Ship
behind the existing completeness gate, keep the atomic rename, and validate with tests 1–2 before relying on
it. Keep `StartFill` as the unknown-total fallback so nothing regresses when headers are missing.
