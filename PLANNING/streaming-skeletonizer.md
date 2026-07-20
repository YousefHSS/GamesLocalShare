# Streaming skeletonizer — build the skeleton from the single download, no 2× peak

**Status:** design (agreed 2026-07-18). Supersedes the batch capture path for Xbox titles.
**Owner files (this repo):** `Services/XboxCacheProxyService.cs`, `Services/SkeletonService.cs`,
`Services/SkeletonWatcherService.cs`.
**Owner files (xvdtool, `D:\xvdtool`):** `LibXboxOne/XVD/XvcDecryptedUStream.cs` (done),
`LibXboxOne/XVD/XVDFile.cs`, `LibXboxOne/XVD/XvdFilesystem.cs` (streaming additions), + a new
`StreamingSkeletonizer` in LibXboxOne.

## Goal & the storage invariant

Build a title's skeleton **from the bytes of the one install download**, so we never keep the multi-GB
encrypted package. The invariant we are buying:

> At no moment do the full encrypted package AND the installed files both exist on disk.

Peak *extra* storage (beyond the installed files, which the Store writes regardless) =
skeleton (small) + a bounded in-flight page window + a small fingerprint store.

## Why this is possible (the enablers)

1. **Page-local decryption.** A user-data page decrypts from the header key material (buffered up front by
   `BuildDecryptedStructuralRegionOnDemand`) + that page's own ciphertext. No random access over the whole
   file is required to decrypt a page — so pages can be processed as they stream in.
2. **Structural region arrives first.** `[0, UserDataOffset)` (header + XvcInfo + data hash tree) is the
   front of the package and is requested early by the Store; it is small and is kept verbatim in the
   skeleton.
3. **File↔range mapping already exists.** The XVC user-data is an NTFS image; `XvdFilesystem` (DiscUtils.Ntfs)
   maps files to clusters, and `ReverseExtractFilesystem` already rebuilds file bytes into the U image at the
   right offsets (the reconstruct path). Classification reuses this.

## CORRECTION after recovering the batch algorithm (decompiled XVDTool.dll)

The `.skl` format + capture/reconstruct were recovered by decompiling the shipped `XVDTool.dll`
(`D:\xvdtool\_recovered\XVDTool\XVDTool.decompiled.cs`, `internal static class Skeleton`). The exact binary
format is documented in the `skl-format-and-capture-algorithm` memory. Two consequences for this plan:

- **The batch matcher is pure CONTENT matching, not FS-aware.** It FNV-indexes every U block, then for each
  installed file finds where its bytes sit in U by hash+full-compare. It needs the **full decrypted U with
  random access** (⇒ the full encrypted package on disk). So the streaming skeletonizer is **not a port** of
  it — it must be a new **structure-aware classifier** (DiscUtils NTFS) that, during the forward stream,
  predicts which U regions are file-data clusters (droppable) vs metadata/slack/hash-tree (keep). The batch
  content-matcher becomes the **verification oracle / fallback**, not the streaming path.
- **Skeleton is not always tiny.** Buckshot v1.0.3.0 = 889 MB skel from a 1505 MB package (~59%). So a naive
  "discard everything, refetch unmatched later" would refetch a lot. The design must **predict-and-keep**
  unmatched regions during the stream, and only refetch the rare post-install verify mismatch.

**Reconstruct is content-agnostic** (it just tiles skeleton-ranges + installed-file-extents into U'), so any
byte-format-compatible `.skl` we emit restores through the existing path unchanged. Output compatibility is
the hard requirement; the *producer* algorithm is free to change.

## Architecture: skeletonize in-process, fed by the proxy tee

Reference **`LibXboxOne.dll`** directly from GamesLocalShare (it is already bundled with its deps —
BouncyCastle, DiscUtils) and run the skeletonizer **in-process**, fed page-by-page from the proxy tee. This
removes the subprocess + the full-package temp file entirely.

```
Store install ── ranged GETs ──► XboxCacheProxyService.Handle (MISS)
                                      │  (forward to Store, unchanged)
                                      └─► StreamingSkeletonizer.Feed(absOffset, plaintextPage?)
                                             │ decrypt page (page-local)
                                             │ classify via NTFS map
                                             ├─ maps to a present installed file ─► DROP + fingerprint
                                             └─ structural / protected / unmatched ─► APPEND to .skl
```

Alternative if in-process proves awkward: a new `XVDTool --capture-stream` that reads the package from a
named pipe the proxy writes to. In-process is preferred (no IPC, direct backpressure).

## Components (build order)

### A. LibXboxOne: `StreamingSkeletonizer` (the core, xvdtool repo)
- `Feed(long absOffset, ReadOnlySpan<byte> encryptedBytes)` — accepts pages in **arbitrary order** (the Store
  requests out of order / in parallel).
- Buffers the structural region until `[0, UserDataOffset)` is complete, then finalizes header key material +
  hash tree (kept in the skeleton verbatim).
- For each user-data page: decrypt (`ReadDecryptedRange` logic, page-local), then classify using the NTFS
  map. **If the classifying MFT metadata for a page hasn't arrived yet, hold the page in a bounded buffer;**
  when the buffer is full, spill held pages into the skeleton as candidates (correct, just larger).
- Emits the `.skl` incrementally (same on-disk skeleton format the reconstruct path already reads).
- Tracks running totals: kept-bytes, dropped-bytes, coverage of `[0, Total)`.

### B. LibXboxOne: fingerprint + post-install verify
- While streaming, record a compact fingerprint (hash + file/offset) for every **dropped** page.
- `VerifyAgainstInstall(installDir)` — after install completes, hash the installed file bytes for each
  fingerprint; confirm match. Returns the list of **mismatched** pages (should be empty for a healthy
  matched-version install).

### C. GamesLocalShare proxy integration (`XboxCacheProxyService.cs`)
- Replace the `FillState` sparse-`.part` tee with a per-object `StreamingSkeletonizer` instance
  (`GetOrCreateFill` → `GetOrCreateSkeletonizer`). The forward loop still writes to the Store; the tee call
  `TeeWrite` becomes `skeletonizer.Feed(absOffset, buf, rn)`.
- **No `.part`, no `SetLength(Total)`, no full-package promote.** The gap-fill sweep stays but now fetches
  only the ranges the skeletonizer still needs for a complete skeleton (structural region + any unclassified
  gaps), not the whole object.
- On completion: the skeletonizer has the skeleton minus the post-install verify. Hand off to the watcher.

### D. Capture wiring (`SkeletonWatcherService.cs` / `SkeletonService.cs`)
- Drop `LocatePackage` as the capture trigger for streamed titles. Instead the proxy hands the watcher a
  **finished-streaming skeleton keyed to the title's content GUID**. On install-complete, run
  `VerifyAgainstInstall`; ranged-refetch any mismatched page (KB); finalize + write the `.skl` + manifest.
- Keep `LocatePackage`/batch capture as a **fallback** for a dropped `.msixvc` (manual capture path) and for
  titles not installed through the proxy.

## Robustness rules (must implement, not optional)

1. **Skeleton + fingerprints on a configurable drive** — small, and independent of the game drive, so a
   separate download filling the game drive can't stall capture.
2. **Bounded in-flight buffer** (fixed cap, e.g. 64–128 MB). Overflow → spill to skeleton candidates; never
   grow unbounded.
3. **ENOSPC-clean abort** — a failed skeleton write aborts the capture cleanly: no half-written `.skl` is
   ever promoted, title stays "not captured, retry later", the Store's install is untouched.
4. **Early-bloat abort** — if kept/candidate skeleton bytes cross ~30–40% of `Total` mid-stream, the input is
   almost certainly a **version mismatch** (the original 10 GB-Rematch bug). Abort with a clear reason instead
   of filling the disk. This also kills the wrong-bytes bug at the source.

## Post-install verify + refetch fallback

The install-files lag is handled *after* the stream, not by racing it:
1. Stream drops file-pages + keeps fingerprints (no installed files needed yet).
2. On install-complete, `VerifyAgainstInstall` hashes installed bytes vs fingerprints.
3. For each mismatch (rare): ranged-refetch that page from the CDN (KB, via the existing gap-fill path) and
   append it to the skeleton. Then finalize.

## Correctness invariants

- **Skeleton + installed files must rebuild the genuine encrypted package byte-identically** (same SELF-VERIFY
  gate as today: rebuilt SHA == genuine gsha). No streamed skeleton is promoted until it self-verifies.
- **Never promote on incomplete coverage.** Finalize only when structural region is whole AND every
  user-data page is either kept or fingerprint-verified against an installed file.
- **Out-of-order & duplicate pages are safe** (Store retries): re-feeding a page is idempotent; classification
  is by absolute offset.
- **Version mismatch fails fast** (early-bloat abort), never produces a giant skeleton.

## Open questions / risks

- **NTFS metadata ordering.** If a data page routinely arrives far before its MFT metadata, the bounded buffer
  spills more to skeleton candidates (larger skeleton, still correct). Measure on a real install; raise the
  cap or pre-fetch the MFT region first if needed.
- **Skeleton on-disk format parity.** The streaming skeletonizer must emit exactly the format the reconstruct
  path reads. Reuse the existing writer, don't fork it.
- **Where the batch `--capture` source is.** The shipped `XVDTool.dll` has a capture harness not present in the
  current `D:\xvdtool` tree. We are rebuilding capture as the streaming pass in `LibXboxOne`; confirm we don't
  need to recover the old harness (fallback path D above can keep shelling the shipped exe until parity).

## Phase B (streaming) de-risk — RESULT: PASS

The make-or-break question for streaming — *can the file→U map be built from metadata alone, so we can drop
file-data pages as they stream?* — is answered **yes**. `--metatest` (xvdtool `skeleton-streaming` @68c81b9)
maps every file's U-extents, zeroes all file DATA, re-maps, and compares. On Stardew (3822 files):
**3822/3822 identical, 0 differing.** The map lives entirely in the NTFS metadata (MFT), which a streaming
skeletonizer keeps — so dropping file-data pages during the download is sound.

**Remaining Phase B unknowns (operational, not algorithmic):** (1) out-of-order arrival — how much must be
buffered before the MFT is parseable (mitigate by having the proxy prefetch/prioritise the metadata region so
the buffer stays bounded); (2) the `StreamingSkeletonizer` build + proxy-tee integration; (3) reconstruct-on-
demand LAN serving. Build order per the Phase B plan in `~/.claude/plans/`.

## Feasibility spike — RESULT (2026-07-18): validated

Added `--fsmap` to xvdtool (`XvdFilesystem.EnumerateFileSizes` + `Program.FsMapReport`) and ran it on three
decrypted packages, comparing structure-predicted KEEP (U − embedded-FS file bytes) to the batch matcher's
known skeleton size from `capture.log`:

| Title | U | predicted KEEP | actual skeleton | gap (verify+refetch) |
|---|---|---|---|---|
| Donut County | 301.7 MB | 6.1 MB (2.0%) | 8.7 MB | 2.6 MB |
| Stardew Valley | 694.2 MB | 31.0 MB (4.5%) | 40.99 MB | ~10 MB |
| Buckshot Roulette 1.0.2 | 1505 MB | 16.3 MB (1.1%) | 27.68 MB | ~11 MB |

**Conclusion:** DiscUtils reliably parses the embedded NTFS from the decrypted-U view (235 / 3822 / 289
files); structure predicts the content-matcher's droppable set within ~1% of the package. Predicted-KEEP is
consistently *optimistic* by ~1% of U — that delta is the bounded verify+refetch cost (0.8–1.4% of the
package). Storage win confirmed: materialize ~1–4.5% of the package, not ~200%.

**Open items surfaced by the spike:**
- **RESOLVED — Store MSIXVC is uniformly Fixed.** Classified every package on disk: all six distinct titles
  (Donut, Stardew, Buckshot, Rematch, Digging A Hole, Clone Drone — six publishers, 300 MB–10.5 GB) are
  `XvdType: Fixed`. Dynamic/BAT is a sparse/on-demand format for system/local images, not Store content. So
  the classifier targets **Fixed** (linear fs→U map: `U_off = DriveDataOffset + partition_off + fs_off`) and
  **guards non-Fixed by falling back to the batch-capture path** (today's behavior — graceful, no regression).
  A Dynamic fixture can be synthesized later for belt-and-suspenders, but no real Dynamic package exists in
  practice.
- `-eu` on the cached packages logged "Error during decryption!" yet the FS still parsed (these packages show
  "XVC Data Hash Valid: False" — likely a post-decrypt data-integrity check, not a crypto failure). Confirm
  the decryption is byte-correct (compare against a known-good `-eu`) before relying on it in the real path.
- **RESOLVED — extent→U mapping proven byte-correct.** Added `XvdFilesystem.ValidateUExtentMapping` +
  `--smap`. On decrypted Donut: 235 files mapped, **0 mismatches** — U's bytes at every mapped extent
  (`U = DriveDataOffset + partitionStart + fsExtentStart`, clipped to file size to drop cluster slack) equal
  the embedded file content. Structure skeleton = 6.1 MB (2.0%), consistent with `--fsmap`. So the linear
  Fixed mapping and the positional ranges are validated; the classifier can trust these offsets.

## Component A — build progress

- **Step 1 DONE (extent→U mapper + validator).** `XvdFilesystem.ValidateUExtentMapping(readU)` maps every
  embedded-FS file to its clipped U-ranges and byte-verifies them against the file content; `--smap` reports
  it. Foundation proven on Donut (0 mismatches).
- **Step 2 DONE (XSKL emission + reconstruct round-trip).** Restored the lost `Skeleton` class into source
  (`XVDTool/Skeleton.cs`, from the decompiled DLL — capture/reconstruct/UStream + XSKL format). Added
  `--sctest`: extract embedded FS as a synthetic install root → build a byte-compatible XSKL v1 from the
  structure map → `Skeleton.Reconstruct` → SHA-256 compare. **All three titles rebuild U byte-identically:**
  Donut 235 files/6.14 MB skel, Stardew 3822 files/31.45 MB, Buckshot 289 files/16.36 MB. Structure `.skl`
  is *smaller* than the batch matcher's (it maps tiny/contiguous files the content-matcher kept). Fragmented
  files (extent count ≠ 1) are safely left to the skeleton.
- **Step 3a DONE (size+SHA install matcher).** `--sccap --scinstall <dir> --scskel <out>`: maps embedded-FS
  files → U-ranges, matches each to an installed file by **size+SHA** (path-agnostic), emits XSKL, and
  reconstruct-verifies against the real install. Validated on Donut: 235 matched / 0 unmatched, byte-identical
  — and still byte-identical against a **reshuffled/nested** install layout (proves content-matching, not
  path-matching). **Robust to ACL-locked install files** (Xbox lockdown): unreadable candidates are skipped
  (→ skeleton), no crash. (Standalone tool as a plain user can't read `C:\XboxGames\...` installs; the app
  reads them in its own context — capture.log confirms it captures from real installs.)
- **Step 3b DONE (XSKL v2 + encrypted-package integration).** Made `XvdFilesystemStream.InternalReadAbsolute`
  decrypt on the fly when the package is encrypted (`ReadDecryptedRange`), so `XvdFilesystem` parses NTFS
  straight from the genuine `.msixvc` with no decrypted copy. `--sccap` now auto-detects: on an encrypted
  package it reads U decrypted-on-the-fly and emits **XSKL v2** (genuine encrypted structural `[0,UDO)` +
  `gsha` = SHA of the whole `.msixvc` + `cik` = XvcInfo key id). Validated on **encrypted Donut**: NTFS
  parsed, 234/235 matched, `.skl` header = `XSKL v2`, **reconstruct byte-identical to U**.
  - *Follow-up (spike item #2):* the encrypted path matched 234 vs 235 on the decrypted path — 1 file went to
    skeleton (harmless; reconstruct still byte-identical). Likely a decrypt-on-the-fly vs `-eu` discrepancy on
    one page; confirm with the existing `SelfTestFullUOnDemand` (`--verify-full <ref-eu>`), fix if real.
## App integration (component B)

- **B1 DONE (drop-in capture engine).** Added a production `--capture --cikfolder <flat *.cik dir>
  --install <dir> --skel <out> <pkg>` to XVDTool that structure-captures the genuine encrypted package and
  writes the app's `result.json` (`ok/verified/uSize/filesMatched/foundBytes/skeletonBytes/skelFileSize/
  skelPath`). Same CLI `SkeletonService.CaptureAsync` already shells, so the engine swaps in with **zero app
  code change**. Validated app-style on encrypted Donut: `Loaded 11 CIK(s)`, VERIFIED byte-identical, XSKL v2,
  valid `result.json` (234 files matched, 1→skeleton). CIK-folder loads each `*.cik` via `LoadKey(KeyType.Cik)`
  (`LoadKeysRecursive` only scans `Cik/Odk/` subdirs, not a flat store).
- **B1 packaging (next): bundle the rebuilt XVDTool** — copy the new `XVDTool.dll` + `LibXboxOne.dll` (deps
  BouncyCastle/DiscUtils already bundled) into `GamesLocalShare/tools/xvdtool/`, then run the app to confirm
  capture works E2E. (Modifies shipping binaries — reversible via git.)
- **B2a DONE (bind capture to the matched version).** `SkeletonWatcherService.LocatePackage` now prefers the
  cached `.msixvc` whose version **exactly matches the installed version** (`Content\appxmanifest.xml`), and
  **refuses to capture from a mismatched version** (returns null → waits for the matching package) instead of
  grabbing the highest version. This is the direct wrong-bytes fix (Rematch: was capturing v1.204.5.0 pkg vs
  v1.204.6.0 install → 10 GB skeleton). Falls back to legacy highest-version only when the install version
  can't be read. Version compare normalised to 4 components. App builds.
- **ACL/elevated-read DONE.** Capture reads ACL-protected install files (high-integrity Xbox game files)
  by running xvdtool elevated (runas) when >32 MB of the install is unreadable by the non-elevated process
  (`SkeletonWatcherService.UnreadableInstallBytes`); falls back to non-elevated on UAC decline. Fixes
  GameMaker-style titles (whole game in one protected .exe → was a whole-package skeleton). Elevated capture
  reads its outcome from result.json (no stdout streaming → no live progress bar for those). Commit 8a41296.
- **B2b DONE (tee promotion validated).** Live-tested a real cache MISS: uninstalled Donut + deleted its
  cached package, Started the proxy, reinstalled through it — the proxy teed the fresh download, promoted a
  matched-version `.msixvc`, and it captured to a small skeleton. Repeated successfully. The fresh-download /
  update path works end to end.
- **B2 safety net (optional): early-bloat abort in the capture engine** — if matched files cover < ~15% of U
  (skeleton > ~85%), abort with a clear reason rather than writing a near-useless skeleton (threshold high
  enough to not false-positive legit large skeletons, e.g. Buckshot 59%).
- **B3 (later): streaming** — drop file-pages during download to avoid the double-storage peak.

### B3 Milestone 0 — proxy-bridge de-risk (RESULT 2026-07-19: PASS)

The make-or-break unknown for the proxy integration — *can the Store's OUT-OF-ORDER, encrypted, package-space
ranged GETs be turned into the strictly-ascending decrypted feed `StreamingSkeletonizer` needs, within a
bounded buffer, and still reconstruct byte-identically?* — is answered **yes**. Built (xvdtool
`skeleton-streaming`):
- `LibXboxOne/StreamingPackageDecryptor.cs` — bounded reorder buffer keyed by absolute offset; page-local
  decrypt via an injected delegate (`XvdFile.ReadDecryptedRange` — needs only the buffered structural front +
  the page); drains ascending into `StreamingSkeletonizer.Feed`; tracks peak-resident + overflow.
- `StreamingSkeletonizer.Finalize` — added an optional `refetchU` delegate: unmatched dropped files are pulled
  back into the skeleton as kept ranges (the post-install ranged-refetch). Byte-identical to the old path when
  nothing needs refetching, so `--streamsim` is unaffected.
- `--streambridge` harness — feeds the genuine ENCRYPTED `.msixvc` out-of-order (64 MB ahead-window modelling
  parallel ranged GETs + backpressure), self-extracts the embedded FS as a synthetic install, reconstruct-
  verifies. On encrypted **CloneDrone** (1697 MB U, Fixed, real CIK):
  - **byte-identical** reconstruct (`U' sha == U sha`), skeleton ~15–30 MB;
  - **peak resident ≈ 89 MB** (64 MB reorder buf + ~25 MB skeleton) vs the 1697 MB package — full package
    never held;
  - **overflow → fallback** fires correctly when the reorder buffer exceeds its cap (a too-aggressive random
    model without backpressure hit 257 MB > 256 MB cap → the proxy would abort streaming and fall back to the
    tee);
  - **refetch path** exercised by deleting one dropped file from the install → "refetched 1 … Assembly-CSharp.dll",
    reconstruct still byte-identical.

Next: B3 M1 (finalize `StreamingPackageDecryptor` API + in-process `LibXboxOne.dll` reference from the app),
then B3 M2 (wire into `XboxCacheProxyService` behind an off-by-default `XboxStreamingCapture` setting: front
prefetch + CIK acquisition/fallback-to-tee + finalize handoff). Serving of streamed titles (reconstruct-on-
demand) is a deferred follow-up. Plan: `~/.claude/plans/wild-wishing-milner.md`.

### B3 M1 — in-process LibXboxOne (RESULT 2026-07-20: PASS; app `eb849d1`)

The batch capture path shells `XVDTool.exe`; streaming needs the skeletonizer in-process (direct backpressure
from the proxy tee). `Services/XvdInProcess.cs` installs an `AssemblyLoadContext.Default.Resolving` handler
that loads `LibXboxOne.dll` + its DiscUtils/BouncyCastle deps from the existing `tools\xvdtool\` folder (single
copy, shared with the subprocess; loads lazily only when a LibXboxOne type is first touched). csproj gets a
compile-time `<Reference>` (`Private=false`). Register the resolver at the very top of `Program.Main` (before
any LibXboxOne-referencing method is JIT-compiled). `--selftest-xvd-inprocess` PASS: LibXboxOne + all deps
load in-process; app builds clean; normal startup unaffected (resolver is a no-op until used).

### B3 M2a/M2b — streaming-capture engine (RESULT 2026-07-20: PASS; app `411f5f8`, xvdtool `236b56b`)

- **M2a:** off-by-default `AppSettings.XboxStreamingCapture` (best-effort; falls back to the tee when off/no CIK).
- **M2b:** the engine the proxy will run in-process — `LibXboxOne.StreamingCaptureController` + `PartialPackageStream`
  + `XvdFile(Stream)` ctor + `StreamingSkeletonizer.SetGenuineSha`. An `XvdFile` over a `PartialPackageStream`
  (prefetched front + last sector + on-demand pages) parses the header, builds the file→U map from the front,
  decrypts each arriving page page-locally, reorders (`StreamingPackageDecryptor`), skeletonizes, and hashes the
  genuine `gsha` in drain order — the full encrypted package is never held. Overflow + early-bloat aborts →
  caller falls back to the tee. `--streamctl` offline test on encrypted CloneDrone (1697 MB): armed 229 files,
  **byte-identical reconstruct** (`U' sha == U sha`), **gsha MATCH**, refetch path fired, **peak 63 MB**. This is
  the exact pipeline the proxy will run, minus the live socket.

  *Note (M2b gotcha):* `XvdFile`'s IO wrapper builds a `BinaryWriter`, which rejects a non-writable stream, so
  `PartialPackageStream.CanWrite` must report true (Write still throws — the read path never writes, and
  `DisableSaveAfterModification` is set).

### B3 M2c — proxy wiring (IMPLEMENTED 2026-07-20; needs live E2E validation; app `9e9bed5`, xvdtool `aeb9f19`)

Wired `StreamingCaptureController` into `XboxCacheProxyService` behind `XboxStreamingCapture` (default off):
- **Arm:** first MISS for an object → background-arm thread prefetches the header front (64 MB) + last sector via
  `FetchRangeBytes`, `TryBegin`s the controller (builds the map, loads CIKs). Arm fail (no CIK / non-Fixed /
  parse) → **abandon + `StartFill`** (normal full download, no regression).
- **Feed:** the forward loop feeds each arriving page to the controller (once armed) instead of the tee; the
  streaming gap-fill sweep fetches ranges the Store never requested (incl. the pre-arm front) in 8 MB chunks and
  feeds them. Overflow / version-bloat → `StreamAbort` → `StartFill` fallback.
- **Park + finalize:** on complete coverage the controller is parked keyed by cache path (contains the content
  GUID). `SkeletonWatcherService.TryStreamedFinalize` (set by `MainViewModel`) runs on install-detect *before*
  the batch `LocatePackage` path: matches the parked controller by the install's `.xvi` content GUID, calls
  `Complete(installDir, skelPath, fetchEncrypted)` (CDN ranged GET for unmatched-drop refetch), writes the
  restore manifest + fires `CaptureCompleted`. `Complete` refactored to take `fetchEncrypted` (raw CDN bytes;
  the controller stages + decrypts internally).

The flag-off path is byte-for-byte today's tee behavior. **Live E2E not yet run** — needs a real install through
the proxy. Test prep done: Donut County cached package + skeleton deleted, `XboxStreamingCapture=true` set.
Expected on reinstall: `STREAM armed …Donut…`, no `.part`, `STREAM ready …`, then on install-complete a
`✓ Donut County: streamed skeleton … MB captured (no package stored)` and a small `.skl` in the store, with the
package cache drive never gaining ~300 MB.

#### (superseded design note) M2c was:
Remaining: wire `StreamingCaptureController` into `XboxCacheProxyService` behind the flag. Live-path decisions
that want real-machine feedback: (1) front-prefetch strategy on first MISS (synchronous ~64 MB vs background-arm
+ tee-until-armed); (2) coverage-completion detection + gap-fill feeding the controller; (3) the finalize handoff
— proxy parks the armed+fed controller keyed by content GUID; the watcher, on install-complete, calls
`Complete(installRoot, skelPath, refetchU)` where `refetchU` fetches the rare unmatched-drop bytes from the CDN
(via the proxy's `FetchRange`) and decrypts them (stage into the controller's page cache → `ReadDecryptedRange`).
CIK-not-ready and any abort → fall back to today's tee (no regression). The flag-off path is byte-for-byte today's
behavior.

Open follow-up: encrypted path matches 234/235 (1→skeleton, harmless) — likely decrypt-on-the-fly vs `-eu`
page discrepancy; confirm via `--verify-full`.

Spike/build code lives in `D:\xvdtool` (uncommitted): `XVDTool/Skeleton.cs` (restored), `--fsmap`, `--smap`,
`--sctest`, and the `XvdFilesystem` additions (`ValidateUExtentMapping`, `EnumerateFileSizes`).

## Test plan

1. **Small matched install (Donut County ~302 MB):** install through the proxy. Expect no `.part`, tiny
   skeleton (~8.7 MB), self-verify OK, peak extra storage ≪ package size.
2. **Big matched install (Rematch ~10.5 GB, correct version):** expect small skeleton, no full package ever on
   disk, self-verify OK.
3. **Version-mismatch install:** expect the early-bloat abort to fire with a clear reason, not a 10 GB skeleton.
4. **Disk pressure:** run a second large download on the game drive during capture; skeleton (on the separate
   drive) still completes; ENOSPC on the game drive doesn't corrupt the skeleton.
5. **Out-of-order / interrupted:** kill and resume the install; bounded buffer holds; refetch fills gaps; final
   skeleton self-verifies.
6. **Second LAN PC:** restore package from the streamed skeleton + install; Gaming Services Verify HITs.
