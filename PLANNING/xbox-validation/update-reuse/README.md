# Update-time gap reuse — measurement first

**Goal:** on a game *update*, fill the ranges the Xbox Store does **not** re-download from
*local* data instead of re-fetching them from the CDN — so an update doesn't quietly download
most of the package again just to (re)capture a skeleton.

Before building anything, we measure **one real update** to answer the make-or-break question:

> Are the bytes the Store doesn't re-download **byte-identical at the same offset** between the
> old and new package versions?

If yes → same-offset reuse is valid and worth building. If no (the content key rotated, or the
layout shifted) → simple same-offset reuse is impossible and reuse would have to move to the
decrypt + re-encrypt layer (much harder). Measuring first avoids building the wrong thing.

## Why a skeleton can't just "fill its own gaps" from the old skeleton

A `.skl` does **not** contain the game's content. It stores pointers to the **installed files on
disk** (`relPath, size, sha, U-offset`) plus a small blob of the *non-file* bytes (NTFS metadata,
slack, the hash tree). Reconstruct = install files tiled at their offsets + the blob poured into
the gaps. The old skeleton's blob is metadata/hash-tree — the part that changes *most* between
versions — so it's the least reusable thing. The genuinely reusable data across an update is the
**unchanged installed files**, which an update leaves on disk. That's the encrypted content the
diff below measures.

## Step 0 (do this first): is there an oracle at all?

Measuring reuse is pointless if a reused range can never be **verified**. Today a skeleton is
only promoted when the rebuild matches the genuine package's SHA — which requires the whole
genuine package, i.e. the download we are trying to avoid. The only replacement is the package's
own signed hash tree, and the streaming-skeletonizer spike left an unresolved warning about it
(`"XVC Data Hash Valid: False"` on packages that were otherwise fine).

This needs **one** package you already have — no old version, no update:

```powershell
.\hashtree-oracle-check.ps1 -Package <any.msixvc>
```

It reports the hash verdict on the pristine package, flips a single byte deep in user data,
asks again, and restores the byte. If the verdict changes, the tree can verify bytes we produce
locally and the whole design is live. If it does not, everything below is moot until the check
is fixed in the xvdtool fork — including merging an old skeleton into a new capture.

Exit codes: `0` oracle works · `3` blocked · `4` inconclusive.

## The experiment

You do **not** have to wait for a real update. The CDN serves these packages to a plain
unauthenticated ranged GET (the app's own gap-fill does exactly that — `FetchRange` sends only
`Host:` and `Range:`), so any version can be pulled on demand — the only trick is knowing its
URL, since the version is baked into the path and the Store only ever asks for the current one.

Captured skeletons record those URLs for you: every capture writes `<name>.skl.json` with
`CachePath` + `CacheRoot`, and the relative part *is* the CDN URL path. A skeleton captured
before a game updated therefore hands you a working old-version URL.

```powershell
# what old versions can I fetch right now?
.\fetch-cdn-package.ps1 -List

# pull one, then diff it against the version currently cached
.\fetch-cdn-package.ps1 -Manifest "<...>\skeletons\<Title>.skl.json" -DiffAgainst <new.msixvc>
```

The download resumes if interrupted, and it resolves the real CDN IP through a public resolver
so it still works while the proxy's hosts redirect is active.

**If you have no usable old URL** (nothing captured before an update), fall back to catching one
update the slow way:

1. Settings → Xbox → turn ON **"Also cache the full package to disk"** (`XboxCacheFullPackage`).
2. Have the game installed at version **N** → its full `.msixvc` lands in the cache.
3. Let it update to version **N+1** → the new version (a different content GUID = a different
   file) is cached too; the old one stays.
4. Locate both under `<cache>\assets1.xboxlive.com\...` (two `*.msixvc` for the same title) and:

```powershell
.\diff-packages.ps1 -Old <old.msixvc> -New <new.msixvc>
```

## Reading the result

- **`IDENTICAL @ offset`** is the headline: the % of the package that is byte-identical at the
  same offset = the **ceiling** on download we could avoid on an update.
- **`identity map`** shows *where* changes cluster (`#`=identical, `.`=changed, digits=partial).
- **Size delta / largest changed regions** show whether the update mostly appends/patches in
  place (reuse-friendly) or reshuffles layout (reuse-hostile).

### Decision

| Result | Meaning | Next |
|---|---|---|
| High identical % (e.g. >70%) | Same-offset reuse is valid; CIK stable, layout stable for unchanged parts | Design the reuse path (proxy gap-fill sources from local install; verify via existing SHA) |
| Low / ~0% identical | CIK rotated or layout shifted | Same-offset reuse dead; reconsider (decrypt+re-encrypt layer, or drop the idea) |

## Note on merging an OLD SKELETON into a new capture

A `.skl` is a range map over U and reconstruct is content-agnostic, so two of them union
cleanly — merging is not the hard part. But the old skeleton can only contribute the **non-file**
bytes (~1–4.5% of the package per the feasibility table in `streaming-skeletonizer.md`);
everything else comes off the installed files, which are on disk anyway. And that slice is
header / hash tree / MFT metadata — precisely what an update rewrites. So order the byte sources
by value: **installed files first** (proven byte-identical by `--restore`), **the new front
second** (the Store usually sends it; ≤64 MB from the CDN if not), and the **old skeleton last**.
The merge is an optimization, not the unlock. The unlock is Step 0.

## Where the reuse would eventually live (design phase, after measuring)

- **This repo:** `Services/XboxCacheProxyService.cs` — `GapFillSweep` currently fetches un-sent
  ranges from the CDN (`FetchRangeBytes`). Reuse would try a local source first.
- **External fork (`D:\xvdtool`):** `LibXboxOne/StreamingCaptureController.cs` (Feed/Complete) and
  `XVDTool/Skeleton.cs` (capture/reconstruct) — where the U↔file offset mapping and per-range
  verification live, so "source this range from the install, verified" belongs there.

Everything is SHA-verified end-to-end (`Usha`, per-file `sha`, self-verify rebuild), so a wrong
reuse can never silently ship a bad skeleton — it fails verify and falls back to the CDN.
