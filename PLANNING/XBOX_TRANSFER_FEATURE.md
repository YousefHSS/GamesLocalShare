# Xbox Transfer Feature Implementation

## Overview

Xbox Game Pass (MSIXVC) games can now be transferred between PCs via the
GamesLocalShare app using the **overlay-on-paused-install** strategy proven
during validation.

This avoids re-downloading the full game from Microsoft servers -- instead,
pre-staged game files are overlaid onto an Xbox app-initiated paused install.
When Resume is clicked, Gaming Services detects the bytes are already present
and skips the download (proved: ~8 MB network vs 7.7 GB source = 99.9% saving).

---

## Files Created

### New Services

- **`Services/XboxLibraryScanner.cs`**
  - Implements `IGameLibraryScanner`
  - Scans all drives for `XboxGames\` folders
  - Detects MSIXVC titles by `.xvi/.xvs/.xct` envelope files at the root
  - Extracts content GUID (from `.xvi` filename) and PFN (from folder ACL)
  - Returns `GameInfo` with `Platform = GamePlatform.Xbox`
  - Handles both completed installs (`XboxGames\GameName\`) and in-progress
    GUID folders (`XboxGames\807C7D6A-...\`)
  - **Windows only** (Xbox PC Game Pass is Windows-exclusive)

- **`Services/XboxTransferService.cs`**
  - Core receiver overlay workflow
  - Steps: `ValidateSource` -> `PollForInstallFolder` -> `OverlayFiles` ->
    `ResetAcls` -> `MonitorResume`
  - Uses robocopy `/E /COPY:DAT /IS /IT /B` (backup privilege, admin required)
  - After overlay: `icacls /reset /T` to fix ACLs so Gaming Services can read
    the files during package registration
  - Verdicts: `FullSkip`, `DeltaOnly`, `FullRedownload`, `StillPaused`, `Error`

### New Models

- **`Models/XboxTransferState.cs`**
  - `XboxTransferStep` enum: tracks wizard progress (SelectSource,
    ValidatingSource, PollingForFolder, Overlaying, ResettingAcls,
    WaitingForResume, Monitoring, Complete, Failed)
  - `XboxTransferVerdict` enum: result classification
  - `XboxTransferState` class: step, progress, NIC stats, package state

---

## Files Modified

| File | Change |
|---|---|
| `ViewModels/MainViewModel.cs` | Added `XboxTransferService` field, `XboxTransfer`/`IsXboxTransferActive` observable properties, `StartXboxTransferCommand`, `CancelXboxTransferCommand` |
| `Services/InteropBridge.cs` | Added `StartXboxTransfer`, `CancelXboxTransfer`, `BrowseXboxSource` message handlers; added Xbox state to `GetFullState()` |
| `Models/AppSettings.cs` | Added `XboxStagePath` property for default staged transfer browse location |

---

## Key Technical Decisions

### 1. No PsExec / SYSTEM dependency

The original validation scripts used `PsExec -s` to run robocopy as SYSTEM.
The app instead requires **Administrator** and uses robocopy `/B` (backup
privilege), which is sufficient for reading MSIXVC encrypted files on the
sender side and overlaying on the receiver side.

### 2. `icacls /reset /T` after overlay

During validation, the first overlay succeeded (0.8 MB traffic, installed in
30s) but hit `0x80070005` (ACCESS_DENIED) at the `MRTDataPopulated` step of
package registration.

Root cause: robocopy `/COPY:DAT` copied data without ACLs. New files didn't
inherit the parent folder's security descriptors that Gaming Services set up.
`icacls /reset /T` forces all files to re-inherit ACLs, fixing this.

### 3. Envelope files must be included in overlay

The `.xvi`, `.xvs`, `.xct` envelope files contain content hashes/manifests
that Gaming Services uses to validate overlaid data. When excluded (second
validation attempt), Gaming Services re-downloaded everything because its own
checksums didn't match the data. They must be overlaid.

### 4. GUID folder detection with `@()` array wrapper

PowerShell trap discovered during validation: when `Find-Destination` returned
a single string, `$candidates[0]` indexed into the **string** (returning the
first character "C") instead of the first array element. The fix was wrapping
in `@(...)` to force array context. This is baked into `XboxTransferService`.

---

## Usage Flow

### Sender Side (separate from app for now)

1. Use the existing PowerShell script `auto/xbox-transfer-sender.ps1` to stage
the game from the sending PC's `XboxGames\` folder to a portable location
(USB/shared drive/network share).

2. The script runs as SYSTEM via PsExec and produces a `transfer-summary.json`
plus the full game data including envelope files.

### Receiver Side (in the app)

1. Copy the staged folder to the receiving PC.
2. In the **Xbox app**, click **Install** on the game, wait for bytes to start
counting, then click **Pause**.
3. In GamesLocalShare, run the Xbox transfer wizard (browse to the staged
source folder).
4. The app will:
   - Validate the source
   - Wait for the GUID folder to appear
   - Overlay the staged files
   - Reset ACLs
   - Prompt you to click **Resume** in the Xbox app
   - Monitor network traffic and package state
5. Verdict is reported: FullSkip, DeltaOnly, FullRedownload, or Error.

---

## Interop Messages (WebUI -> C#)

| Command | Payload | Action |
|---|---|---|
| `StartXboxTransfer` | `{ sourcePath: string }` | Begin the overlay workflow |
| `CancelXboxTransfer` | - | Cancel the active transfer |
| `BrowseXboxSource` | - | Open folder picker for staged source |

---

## Future Work

- **Sender side in-app**: Add a sender UI that stages Xbox games directly
from the app (requires SYSTEM elevation, possibly via a helper service).
- **Network transfer of Xbox games**: Reuse `FileTransferService` TCP protocol
to stream Xbox game data between peers, then perform the overlay.
- **Sender metadata in transfer-summary.json**: Include content GUID, PFN,
expected install size, and file manifest for validation.
- **Xbox cover art**: Fetch cover images from Microsoft Store API.

---

## Validation History

The overlay strategy was validated on 2026-05-18 with Hollow Knight: Silksong
(7.7 GB, 2286 files):

| Run | Envelope files overlaid? | icacls reset? | Traffic | Result |
|---|---|---|---|---|
| 1 | Yes | No | 0.8 MB | **Installed** but `0x80070005` at MRTDataPopulated |
| 2 | No | No | ~7 GB | Full re-download (excluded envelopes) |
| 3 | Yes | Yes | ~8 MB | **Success** -- installed and playable |

The production implementation includes both fixes proven by run 3.
