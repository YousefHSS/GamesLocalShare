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

## The experiment

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

## Where the reuse would eventually live (design phase, after measuring)

- **This repo:** `Services/XboxCacheProxyService.cs` — `GapFillSweep` currently fetches un-sent
  ranges from the CDN (`FetchRangeBytes`). Reuse would try a local source first.
- **External fork (`D:\xvdtool`):** `LibXboxOne/StreamingCaptureController.cs` (Feed/Complete) and
  `XVDTool/Skeleton.cs` (capture/reconstruct) — where the U↔file offset mapping and per-range
  verification live, so "source this range from the install, verified" belongs there.

Everything is SHA-verified end-to-end (`Usha`, per-file `sha`, self-verify rebuild), so a wrong
reuse can never silently ship a bad skeleton — it fails verify and falls back to the CDN.
