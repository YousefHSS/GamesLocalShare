# Xbox MSIXVC Transfer — App Implementation Plan

Wire the validated overlay-on-paused-install workflow (H1: ~8 MB instead of 7.7 GB) into the Avalonia app, supporting both LAN and external-drive paths, with on-demand admin elevation and an in-app receiver wizard for the Xbox app's Install/Pause/Resume dance.

---

## Scope summary (decisions confirmed)

- **Elevation:** on-demand relaunch as admin only when starting an Xbox transfer. After relaunch, app opens to the main window (not directly back to the modal). Wizard shows a visible "Running as Administrator ✓" confirmation badge when elevated.
- **Network path:** sender streams files directly into the receiver's Xbox install folder — no sender-side staging copy, no 2× disk usage. PC A's Xbox install must remain intact and accessible for the full transfer duration.
- **Drive path:** sender stages to external folder; receiver opens the folder manually (sneakernet). Before staging, app prompts the user: "This requires temporarily stopping Gaming Services (Xbox app will close). Continue?" — stops services, stages, restarts services.
- **Receiver wizard:** minimal text-only step list.
- **Game selection:** receiver picks the Xbox MSIXVC title from the sender's library (network); sender picks (drive).
- **Pause/Resume:** reuse `FileTransferService.PauseTransfer` / `ResumeTransferAsync` for the network leg. Xbox app's own Install/Pause/Resume buttons stay manual on the receiver.
- **Discovery:** existing UDP `NetworkDiscoveryService` (no change).
- **ACL reset:** `icacls /reset /T` runs once on the install folder after the overlay finishes.
- **Scope:** both network (LAN) and drive (sneakernet) paths ship in v1.
- **Locked-exe fix:** stop Gaming Services before staging (drive path). VSS approach is under investigation — decision deferred until testing confirms whether stopping services alone is sufficient.

---

## Architecture

```
┌────────── SENDER (PC A) ──────────┐                ┌────────── RECEIVER (PC B) ──────────┐
│  XboxLibraryScanner               │                │  Receiver Wizard UI                  │
│  XboxSenderService (refactored)   │  TCP 45679 →   │   1. Pick peer + Xbox title          │
│   - enumerate MSIXVC source       │   manifest +   │   2. Click Install in Xbox app       │
│   - stream files on demand        │   chunks       │   3. Click Pause in Xbox app         │
│                                   │  ←  resume     │   4. Click Continue (overlay starts) │
│  (no local staging copy)          │   offsets      │   5. Click Resume in Xbox app        │
│                                                    │  XboxReceiverService                 │
│                                                    │   - poll for install folder          │
│                                                    │   - request stream from sender       │
│                                                    │   - write directly into folder       │
│                                                    │   - icacls /reset /T at end          │
│                                                    │   - monitor NIC + package state      │
└────────────────────────────────────┘                └──────────────────────────────────────┘
```

External-drive path bypasses the network: `XboxSenderService.StageAsync` → folder; receiver opens that folder via the same wizard.

---

## Step-by-step plan

### 1. Admin elevation (on-demand)

- Add `Services/ElevationHelper.cs`:
  - `IsElevated()` — `WindowsIdentity.GetCurrent()` / `WindowsBuiltInRole.Administrator`.
  - `RelaunchAsAdminAsync(string[] forwardArgs)` — `ProcessStartInfo { Verb = "runas", UseShellExecute = true }` on current exe, then graceful shutdown. Catch UAC-cancelled `Win32Exception` (1223) and surface as user-cancelled.
  - Optional flag `--xbox-elevated` so the new instance jumps straight back to the Xbox transfer modal.
- `app.manifest`: leave at `asInvoker` (no global UAC prompt).
- Block both `StartXboxTransferAsync` and `StartXboxStageAsync` early when not elevated; show modal "Admin required → Relaunch" button that calls `RelaunchAsAdminAsync`.

### 2. Sender side (rework `XboxSenderService` + new transport)

- Keep `ValidateSource` and `ExtractPfnFromAcl` (already correct).
- Split into two modes:
  - **`StageToFolderAsync(dest)`** — existing robocopy flow, unchanged. Writes `transfer-summary.json`.
  - **`PrepareForNetworkAsync()`** — produces the `transfer-summary` payload in memory plus a file enumeration (relative path, size, last-write). No copy.
- Add `Services/XboxNetworkSender.cs`:
  - Reuses `FileTransferService`'s TCP listener (port 45679) by registering a new request kind `XBOX_OVERLAY_MANIFEST` / `XBOX_OVERLAY_CHUNK` alongside the existing transfers, **or** stand up a sibling listener on 45680 only when an Xbox session is offered. Recommend: extend `FileTransferService` request types (less port juggling, reuses pause/resume + buffer logic).
  - On connect, send `XboxOverlayManifest { GameName, PackageFamilyName, ContentGuid, SourceBytes, SourceFileCount, Files[] }`.
  - Stream files in manifest order, supporting per-file offset (so resume = "start file N at offset O").
- Sender exposes Xbox titles in the broadcast `GameInfo` set with `Platform = Xbox` (already exists via `XboxLibraryScanner`); no new discovery work.
- **MSIXVC layout detection:** not all Xbox PC games use the MSIXVC container layout — some ship as loose files without a Content GUID folder or `.xvi/.xvs/.xct` envelope files, making the overlay method impossible. `XboxLibraryScanner` must tag each title with `IsOverlaySupported: bool` by checking for the presence of a GUID-named subfolder and at least one `.xvi` file inside the install root. Games that fail this check are included in the game list but **greyed out** in the UI with a tooltip: "Transfer not supported — this game does not use the MSIXVC package layout." This check runs at scan time on the sender; the flag is included in the `GameInfo` broadcast and the `XboxOverlayManifest`.

### 3. Receiver side (rework `XboxTransferService` → `XboxReceiverService`)

- Existing `RunOverlayAsync` becomes `RunFolderOverlayAsync(string sourceFolder)` (drive path).
- Add `RunNetworkOverlayAsync(NetworkPeer peer, GameInfo game)`:
  1. Connect to peer, request `XBOX_OVERLAY_MANIFEST` for `appId`.
  2. Populate `XboxTransferState` from manifest (no local `transfer-summary.json` needed yet).
  3. `PollForInstallFolderAsync(90s)` — unchanged.
  4. Snapshot `Get-AppxPackage` state (pre-overlay) via `IsPackageInstalled` + extra `PackageStatus` field.
  5. **Stream into `DestinationPath` directly.** For each file in manifest: open `FileStream` with `FileShare.ReadWrite`, request bytes from sender, write, fsync. Track total bytes for progress + pause/resume offset.
     - On `PauseTransfer`: persist `XboxResumeState` next to dest (`.gls-xbox-resume.json`: file index + offset). Drop TCP. State retained in `_transferCts` like `FileTransferService`.
     - On resume: reopen connection, send last `(fileIndex, offset)`, sender seeks and continues.
  6. After last byte: run `icacls.exe "<dest>" /reset /T /Q /C` once.
  7. Prompt user to click Resume in Xbox app (`XboxTransferStep.WaitingForResume`).
  8. `MonitorResumeAsync(300)` unchanged.
- Delete the old `*.bak` and stub `NetworkDiscoveryService_fixed.cs` if untouched — out of scope, leave alone.

### 4. ViewModel + Bridge wiring (`MainViewModel`, `InteropBridge`)

- Replace stub `StartXboxTransferAsync(sourcePath)` with two entry points:
  - `StartXboxDriveTransferAsync(string sourceFolder)` — drive sneakernet (current behavior, kept).
  - `StartXboxNetworkTransferAsync(string peerId, string appId)` — new.
- New commands: `BeginXboxNetworkSessionCommand`, `PauseXboxTransferCommand`, `ResumeXboxTransferCommand`. Pause/Resume just delegate to `FileTransferService.PauseTransfer/ResumeTransferAsync` against the active Xbox state.
- Add `SelectedPeerXboxGames` derived list filtered to `GamePlatform.Xbox` titles for the active peer.
- `InteropBridge` new commands: `BeginXboxNetworkSession`, `PauseXboxTransfer`, `ResumeXboxTransfer`, `RequestXboxElevation`. Existing `StartXboxTransfer`, `StartXboxStage`, etc. stay for drive flow.
- Push `xboxTransfer.currentStep`, `xboxTransfer.overlayProgress`, `xboxTransfer.networkReceivedMB`, and `requiresElevation: bool` to the WebUI state object already used by `App.tsx`.

### 5. WebUI — receiver wizard (`XboxTransferModal.tsx` rewrite)

Single modal, switches steps by `xboxTransfer.currentStep`:

1. **Choose source** — radio: "From peer (network)" vs "From folder (USB)". Network → peer dropdown + Xbox-only game list from selected peer. Folder → existing folder picker.
2. **Elevation gate** — if `!isElevated`, show "Run as administrator" button → `RequestXboxElevation`. Modal closes; new elevated process opens to main window. If already elevated, show a visible "Running as Administrator ✓" badge (green) and auto-advance.
3. **Install in Xbox app** — text steps:
   - Open Xbox app → find `<game>` → click Install.
   - Wait ~10 s for download to start, then click Pause.
   - Click "Continue" below when paused.
4. **Overlay running** — progress bar (`overlayProgress`), bytes/files counters, Pause/Resume/Cancel buttons (network only).
5. **Click Resume in Xbox app** — text instruction + "Monitoring…" indicator.
6. **Verdict** — render `Verdict` + `NetworkReceivedMB`. Show success (`FullSkip`/`DeltaOnly`), warning (`FullRedownload`), or error.

Use existing primitives in `WebUI/src/components` for buttons/progress; no new framework deps.

### 6. Tests + verification

- C# unit tests under `tests/` (mirror existing pattern):
  - `XboxSenderServiceTests` — manifest generation from a faked folder.
  - `XboxNetworkProtocolTests` — round-trip a manifest + small chunked file end-to-end on loopback (skip files; just verify offsets + resume).
  - `ElevationHelperTests` — `IsElevated()` returns sane value (smoke).
- Manual checklist (in plan, not committed):
  - Drive flow on a small MSIXVC title (Stardew Valley if available; else simulated source).
  - Network flow loopback to itself: pause mid-file, resume, finish, ACL reset, monitor.
  - Verify the `0x80070005` regression doesn't return (icacls step always runs even on pause-cancel-resume).

### 7. Cleanups (low-risk)

- Delete `Services/FileTransferService.cs.bak` and the empty `Services/NetworkDiscoveryService_fixed.cs` — only if untouched at implementation time; will confirm before deletion.
- Move existing Xbox PowerShell validation scripts under `PLANNING/xbox-validation/` are kept as reference; not deleted.

---

## File touch list

| File | Action |
|------|--------|
| `app.manifest` | unchanged (stay `asInvoker`) |
| `Services/ElevationHelper.cs` | **new** |
| `Services/XboxSenderService.cs` | refactor: split drive vs network, expose manifest |
| `Services/XboxNetworkSender.cs` | **new** (or extend `FileTransferService`) |
| `Services/XboxTransferService.cs` → `XboxReceiverService.cs` | rename + add network overlay path + streaming writer |
| `Services/FileTransferService.cs` | add `XBOX_OVERLAY_*` request kinds + offset-resume support for them |
| `Models/XboxTransferState.cs` | add `RequiresElevation`, `IsNetwork`, `PeerId`, `AppId`, `OverlayProgress` already exists |
| `Models/GameInfo.cs` (or equivalent) | add `IsOverlaySupported: bool` field, populated by `XboxLibraryScanner` |
| `ViewModels/MainViewModel.cs` | new commands, peer-Xbox-games filter |
| `Services/InteropBridge.cs` | new bridge commands + state fields |
| `WebUI/src/components/XboxTransferModal.tsx` | full rewrite (wizard) |
| `WebUI/src/store.ts` / `App.tsx` | add Xbox transfer state + dispatch |
| `tests/XboxSenderServiceTests.cs` etc. | **new** |

---

## Open risks / notes

- **Streaming writer + Xbox-folder ACLs.** Writing as SYSTEM/admin into the Gaming Services-owned folder works in the PowerShell repro because robocopy preserves the folder ACL and we run icacls /reset afterwards. The C# `FileStream` write must (a) not change file ownership/ACL on create (it inherits parent by default — good), (b) keep file timestamps reasonable (set `LastWriteTime` from manifest to mirror `/DCOPY:DAT`), and (c) include the envelope files (`.xvi/.xvs/.xct`). The icacls step at the end is the safety net.
- **Pause-during-overlay edge case.** If the user pauses the Xbox-side install before our overlay finishes, Gaming Services may rewrite a file we already overlaid. Mitigation: receiver detects Xbox app re-touching the dest mid-stream (FileShare conflict or unexpected timestamp) and re-runs the overlay for that file. Initial implementation: warn user not to touch Xbox app during step 4.
- **Resume across UAC restart.** If the elevated app instance dies, we lose the in-memory streaming session; the network leg restarts from the persisted offset file. Acceptable.
- **Single-active-transfer constraint.** `FileTransferService` only tracks one transfer at a time. Xbox transfer reserves the same slot — no concurrency change in this plan.
- **SYSAPPID-locked executables not copied during staging (CONFIRMED BUG — Subnautica 2, 2026-05-19).** Certain MSIXVC game executables (e.g. `Subnautica2-WinGDK-Shipping.exe`, `CrashReportClient.exe`, `crashpad_handler.exe`, `Subnautica2.exe`) carry SYSAPPID-conditional ACLs that prevent reading even as SYSTEM while the Xbox app or Gaming Services holds them open. The sender's robocopy `/B` flag should bypass this via backup privilege, but in practice those files were absent from the staged copy (`transfer-summary.json` reported `SkippedFiles: 4`). The receiver overlay proceeded anyway (pre-patch), leaving the Xbox app's incomplete partial downloads of those 4 files in place. Gaming Services then detected hash mismatches against the `.xvi` envelope and triggered a full repair re-download.
  - **Mitigation already applied (PS scripts):** sender now does a post-copy integrity walk (size comparison every file); prints a red banner and exits code 10 if anything is missing or wrong size; writes `IntegrityOk: false` to `transfer-summary.json`. Receiver reads this field and exits code 11 before prompting Install/Pause if `IntegrityOk` is false.
  - **Root cause still open:** why does robocopy `/B` fail to copy those files? Two candidates: (a) Gaming Services holds an exclusive `FileShare.None` handle on them while the Xbox app is open; (b) the backup privilege token granted to PsExec SYSTEM child is insufficient for the SYSAPPID ACL entry. Need to test with Xbox app fully closed + `GamingServices` service stopped before staging.
  - **Required fix for the app implementation:** `XboxSenderService.StageToFolderAsync` must (1) stop Gaming Services before robocopy (`Stop-Service GamingServices,GamingServicesNet`), (2) perform the same post-copy integrity walk as the patched PS script, (3) restart services after, and (4) surface a blocking error to the UI if any files are missing rather than silently producing a broken stage. Alternatively investigate whether `esentutl /y` or `Volume Shadow Copy` can read locked MSIXVC files without stopping services.
