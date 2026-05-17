# Xbox PC Pre-Staged Content Validation

Tooling that automates the manual 2-PC experiment described in
`C:\Users\SIGMA\.windsurf\plans\xbox-prestaged-content-validation-5b063f.md`.

The experiment answers a single question:

> If we copy `<Drive>:\XboxGames\<Game>\Content` from PC A to PC B, will the
> Xbox app on PC B skip the download and only do a license activation?

The answer gates whether we ship MSIXVC transfer in Phase 2 of the Xbox PC
store support, or restrict Xbox support to detection + modifiable-apps only.

## Prerequisites

- Two Windows PCs ("PC A" sender, "PC B" receiver), Xbox app + Gaming Services up to date.
- A different Microsoft account signed into each PC, **both owning** the same test title.
- An external SSD (or SMB share) reachable from both PCs.
- All scripts run in an **elevated PowerShell** (admin). Scripts `02` and `03`
  fail fast if not elevated (pass `-SkipElevationCheck` to override). `01` and
  `04` only warn — they still produce partial output without admin.
- Test titles, pick at least one MSIXVC-encrypted game (< 10 GB ideal). A second
  modifiable-apps title is nice-to-have.

## Script flow

| Step | Script                       | Run on | Purpose                                                                |
|------|------------------------------|--------|------------------------------------------------------------------------|
| 1    | `01-capture-baseline.ps1`    | PC A   | Record package identity, file tree, total size before staging.         |
| 2    | `02-stage-copy.ps1`          | PC A   | `robocopy /B /COPYALL` the `XboxGames\<Game>` tree to the SSD.         |
| 3    | `03-deploy-to-receiver.ps1`  | PC B   | `robocopy` from SSD back into `<Drive>:\XboxGames\<Game>` on PC B.     |
| 4    | `04-try-recognition.ps1`     | PC B   | Run the four escalating recognition triggers + measure bytes.          |
| 5    | `measure-network.ps1`        | PC B   | Standalone helper to sample total NIC bytes over a window.             |
| 6    | `05-system-verify.ps1`       | PC A   | One-off: verify NT AUTHORITY\SYSTEM bypasses MSIX package guard ACLs.  |

Each script writes a JSON log into `.\runs\<timestamp>\` so results are
reproducible. The results table in `results-template.md` is populated from
those logs.

## Typical session

```powershell
# === PC A ===
.\01-capture-baseline.ps1 -GameFolder "D:\XboxGames\<Game>" -OutDir .\runs\baseline
.\02-stage-copy.ps1       -GameFolder "D:\XboxGames\<Game>" -StagingRoot "E:\stage"

# Physically move the SSD to PC B (or copy over SMB).

# === PC B ===
.\03-deploy-to-receiver.ps1 -StagingRoot "E:\stage" -DestRoot "D:\XboxGames"
.\04-try-recognition.ps1    -GameFolder "D:\XboxGames\<Game>" -PackageFamilyName "<from baseline>"
```

After step 4 finishes, transcribe its summary block into `results-template.md`,
sign out of the Xbox app and confirm the offline-launch behaviour for the
"license-locked launch" secondary check.

## Handling MSIX-protected files

MSIX / Xbox PC gaming packages protect their executables with SYSAPPID-
conditional ACEs that even an **elevated admin** process cannot read. `robocopy
/B` with `SeBackupPrivilege` is not enough. The canonical way to read those
files is to run as `NT AUTHORITY\SYSTEM`, which is what the Xbox app /
Gaming Services do internally.

For the experiment you have two options:

- **Quick** (proxy validation): pass `-SkipLockedFiles` to `02-stage-copy.ps1`
  / `03-deploy-to-receiver.ps1`. The script pre-scans the source, finds files
  that aren't readable from the current identity, excludes them from robocopy,
  and records them in `runs\stage-*.skipped.txt` + the summary JSON.
- **Canonical**: launch `05-system-verify.ps1` inside a SYSTEM shell
  (PsExec64 `-s -i`). Confirms that SYSTEM reads every file - the design
  assumption for the production Xbox transfer feature, which will need a
  Windows-service-based helper for file I/O.

## Important safety notes

- **Do not touch `C:\Program Files\WindowsApps`** for the first pass. ACLs there
  are much harder to unwind; validate `XboxGames` first.
- The scripts never delete files outside the explicit `Destination` paths you
  pass them. Robocopy mirroring (`/MIR`) is gated behind a `-Mirror` switch and
  off by default.
- Stop the Xbox app and Gaming Services before deploying on PC B; the scripts
  prompt before doing so.
- Pause Windows Update during the experiment so background package updates
  don't pollute the byte counters.
