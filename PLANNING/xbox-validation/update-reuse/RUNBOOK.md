# Runbook — settle the update-reuse question (run on the Windows machine)

**For:** an agent or human with the repo checked out on the machine that actually has Xbox games
installed, the cache proxy configured, and `tools\xvdtool\XVDTool.exe` present.
**Branch:** `claude/xbox-transfer-bugs-vx39tx` (PR #3). Commit any fixes there.

## Why this exists

We want an Xbox game *update* to re-capture its skeleton **without re-downloading the game**. The
Store only sends the changed blocks during an update, and today capture refuses to fetch the rest
(`MaxCompleteFraction`, 15%) — correctly, since that would re-download most of the package. So an
updated title silently loses its updatable/single-copy status until a full reinstall.

The fix would be to fill those gaps from **local** data (the installed files, which are already on
disk). That is only safe if a filled range can be **verified**. And that is the open question:

- Today's gate is `rebuilt SHA == genuine gsha`, which needs the whole genuine package — the exact
  download we are trying to avoid. Nothing locally-filled can ever pass it.
- The only replacement is the package's own signed hash tree. It commits to the encrypted
  ciphertext, and `--restore` already reproduces genuine ciphertext byte-for-byte, so per-range
  verification *should* work.
- But the streaming-skeletonizer spike logged `"XVC Data Hash Valid: False"` on packages that were
  otherwise fine, and nobody followed it up.

**Step 1 below decides whether any of this is buildable.** Do not skip to step 2.

## Before you start

- Close **GamesLocalShare** and make sure the **cache proxy is stopped**. It may hold a package
  open, and step 1 writes one byte to a package file.
- Find a package to test. Default cache root is `%LOCALAPPDATA%\GamesLocalShare\xbox-cache`
  (Settings → Xbox may point elsewhere); packages live under
  `<cache>\assets1.xboxlive.com\...\<PackageFullName>.msixvc`. Any one will do.
- `cd` into `PLANNING\xbox-validation\update-reuse`.

**These scripts have never been executed** — they were written in a Linux container with no
PowerShell. Expect to fix something small on first run. If you do, keep the fix minimal, explain
it, and commit it to the branch above.

## Step 1 — is the hash tree a usable verification oracle?

```powershell
.\hashtree-oracle-check.ps1 -Package "<cache>\assets1.xboxlive.com\...\<Something>.msixvc"
```

It prints the pristine hash verdict, flips ONE byte 75% into the file, re-reads the verdict, then
restores the byte (verified) and writes a full report next to you.

| Exit | Meaning | Do next |
|---|---|---|
| `0` | Verdict changed under corruption — **oracle works** | Go to step 2. The design is live. |
| `3` | Verdict unchanged (or invalid even when pristine) — **blocked** | Stop. Report it. The reuse path (and any old-skeleton merge) cannot be made safe until the check is fixed in the xvdtool fork at `D:\xvdtool`. |
| `4` | No hash verdict reported at all — **inconclusive** | Read the captured `--help` in the report. If a verify/info flag exists that we did not use, re-run with `-InfoArgs <flag>`. If none exists, the fork needs a verify entry point. |
| `1` | Error (usually the package is locked) | Close the app/proxy and retry, or use `-WorkCopy <path>` to test a copy. |

Useful switches: `-InfoOnly` (touch nothing, just dump info), `-WorkCopy <path>` (corrupt a copy),
`-CorruptOffset <n>` (choose the byte), `-CikFolder <dir>` (defaults to
`%LOCALAPPDATA%\GamesLocalShare\xbox-skeleton\cik`), `-InfoArgs -i` (override the info flag).

**Safety note:** the byte is restored in a `finally` and read back to confirm. The offset and
original value are printed *before* the write. If the script dies in between, restore that byte by
hand — do not leave a corrupted package in the cache.

## Step 2 — only if step 1 returned 0: how much is actually reusable?

We do **not** need to wait for a real update. The CDN serves packages to a plain unauthenticated
ranged GET, and old-version URLs are already recorded in your captured skeletons.

```powershell
.\fetch-cdn-package.ps1 -List
```

Lists every old-version URL derivable from `<skeletons>\*.skl.json`. Pick one whose
**captured version is older than what is installed now** — that is the "old package".

```powershell
.\fetch-cdn-package.ps1 -Manifest "<...>\skeletons\<Title>.skl.json" -DiffAgainst "<current>.msixvc"
```

Downloads the old version (resumable) and runs `diff-packages.ps1` against the current one.

Read the result:

- **High `IDENTICAL @ offset` %** (say >70%) — same-offset reuse is valid; that number is the
  ceiling on the download an update could avoid. Worth designing.
- **~0%** — the content key rotated or the layout shifted. Simple same-offset reuse is dead;
  report the number and stop.
- The **identity map** shows where changes cluster; the **largest changed regions** show whether
  the update patches in place or reshuffles.

If `-List` yields nothing usable (no skeleton predates an update), fall back to the slow route in
`README.md`: turn on "Also cache the full package to disk" and wait for one real update.

## Troubleshooting

- **`$CdnHost resolved to 127.0.0.1`** — the proxy's hosts redirect is active. Stop the proxy, or
  pass `-Ip <real address>`.
- **HTTP 403/404 from the CDN** — that version is no longer served. Try another manifest.
- **Disk space** — an old package is as large as the game. Check before fetching.
- **`Resolve-DnsName` missing** (non-Windows PowerShell) — pass `-Ip` explicitly.

## Report back with

1. The **exit code** of step 1 and the report file it wrote (paste the hash verdict lines and the
   `--help` section verbatim — the exact wording matters).
2. Whether the byte was confirmed restored.
3. If you reached step 2: the `IDENTICAL @ offset` percentage, the size delta, the identity map,
   and which two versions were compared.
4. Any edits you had to make to the scripts, and the commit.

Do **not** "fix" a blocked result by loosening the scripts' verdict logic. A negative answer here
is a real and useful result — it kills a design that would otherwise be built on sand.
