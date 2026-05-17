# Zero-intervention Xbox PC transfer experiment

Two scripts. Two PCs. No human steps in between.

## What you need first

- **PC A (sender)** and **PC B (receiver)** with PowerShell 5+ and admin
  rights on each.
- An **Xbox PC / Game Pass title** that both accounts have access to.
  - Recommended: **A Short Hike** (~250 MB), **Stardew Valley** (~500 MB),
    or **Among Us** (~250 MB). All on PC Game Pass, all install fast.
  - Do **not** reuse the Humble Bundle copy of Slay the Spire - it has a
    different PackageFamilyName (`HumbleBundle.SlayTheSpire_*`) than the
    MS Store / Game Pass copy, so the receiver's Xbox app catalog won't
    recognize it. If you want to use Slay the Spire, uninstall the Humble
    version on PC A first and install it via the Xbox app.
- A way for the staged bytes to travel from PC A to PC B. Pick one:
  - External USB/SSD (simplest).
  - SMB share on PC B that PC A can write to (pass UNC path as
    `-Destination`).

## What the scripts do

### `xbox-transfer-sender.ps1` (run on PC A)

1. Self-elevates via UAC.
2. Downloads `PsExec64.exe` (Sysinternals) to `auto\tools\` on first run.
3. Re-launches itself as `NT AUTHORITY\SYSTEM` so MSIX-protected files
   (the `.exe`s with SYSAPPID-conditional ACLs) become readable.
4. Sniffs the `PackageFamilyName` from the source folder's ACL.
5. Robocopy `/E /COPYALL /B` to `-Destination\<GameName>\`.
6. Writes `transfer-summary.json` next to the staged copy so the receiver
   doesn't need any parameters typed in.

### `xbox-transfer-receiver.ps1` (run on PC B)

1. Self-elevates via UAC.
2. Downloads PsExec on first run.
3. Re-launches as SYSTEM so it can write ACL-preserving copies into
   `C:\XboxGames\<GameName>\`.
4. Reads `transfer-summary.json` from `-Source` for game name + PFN.
5. Stops the Xbox app.
6. Robocopy from `-Source` into `C:\XboxGames\<GameName>\`.
7. Baselines the NIC counter, restarts the Xbox app, and samples
   `Get-AppxPackage` + NIC bytes every 15 s for `ObserveSeconds`
   (default 180 s).
8. Emits a verdict JSON in `auto\runs\receiver-verdict-*.json` with one
   of these labels:

| Verdict               | NIC rx (delta)            | Package registered | Meaning                                                         |
|-----------------------|---------------------------|--------------------|-----------------------------------------------------------------|
| `H1_FULL_SKIP`        | < 100 MB                  | Yes                | Pre-staged content fully accepted. Only licensing traffic.      |
| `H2_DELTA`            | 100 MB - 80% of source    | Yes                | Some bytes saved; Xbox re-pulled a fraction.                    |
| `H3_FULL_REDOWNLOAD`  | >= 80% of source bytes    | Either             | Worst case. Pre-staging wasted - app rebuilt from store.        |
| `NOT_DETECTED`        | small                     | No                 | Xbox app didn't pick it up. Needs more wait, or PFN mismatch.   |
| `INCONCLUSIVE`        | counter wrapped           | -                  | NIC counter wrapped (adapter cycled). Re-run.                   |

## Usage

On PC A:

```powershell
cd F:\Documents\GamesLocalShare\PLANNING\xbox-validation\auto
.\xbox-transfer-sender.ps1 `
    -GameFolder "C:\XboxGames\A Short Hike" `
    -Destination "E:\stage"
```

When it finishes, unplug the drive, plug it into PC B (also as `E:\`,
or adjust the path), then on PC B:

```powershell
cd F:\Documents\GamesLocalShare\PLANNING\xbox-validation\auto
.\xbox-transfer-receiver.ps1 -Source "E:\stage\A Short Hike"
```

That's it. The receiver script prints the verdict at the end and stores
the full per-sample log in `auto\runs\`.

## Notes / caveats

- During the receiver's observe window, **do not click anything in the
  Xbox app**. We want passive recognition behaviour, not user-driven
  Install/Repair clicks. (If the app pops a banner saying "Repair", let
  it sit untouched - the script will measure whatever the app does on
  its own.)
- The receiver's Microsoft account on PC B **must already own / have
  Game Pass for** the title. If it doesn't, you'll get
  `NOT_DETECTED` or `H3_FULL_REDOWNLOAD` no matter how perfect the
  staged bytes are.
- Both PCs must have the Xbox app and Gaming Services installed (open
  the Xbox app once on each before running).
- If you re-run the receiver, first uninstall the title from the Xbox
  app on PC B so we measure recognition from a clean state. Otherwise
  the second run will trivially report H1 because the package is
  already registered.
