# Xbox Network Transfer - Resume Context

## Current Branch: `Xbox-store-support`

## Problem
Xbox network transfers complete the download but the game never actually installs. Clicking Resume in the Xbox app either does nothing or says "files are corrupted."

## Root Causes Found (3 bugs)

### Bug 1: `ReceiverProvidedFiles` format mismatch (FIXED)
**File:** `Services/XboxTransferService.cs` line ~226

The PS1 overlay script (`xbox-transfer-receiver-overlay.ps1`) expects `ReceiverProvidedFiles` in transfer-summary.json to be objects with `Path` and `Size`:
```json
[{"Path": "Content\\Rematch-Win64-Shipping.exe", "Size": 123456}]
```

But the C# code was writing flat strings:
```json
["Content\\Rematch-Win64-Shipping.exe"]
```

PowerShell iterating over strings: `$rp.Path` returns `$null` -> `Join-Path $destGame $null` = just the directory -> `OpenRead` fails -> exit code 12 -> **overlay never runs**.

**Fix applied:**
```csharp
// BEFORE (wrong)
["ReceiverProvidedFiles"] = manifest.SkippedProtectedFiles
    .Select(s => s.RelativePath).ToArray(),

// AFTER (correct)
["ReceiverProvidedFiles"] = manifest.SkippedProtectedFiles
    .Select(s => new { Path = s.RelativePath, Size = s.ExpectedSize })
    .ToArray(),
```

### Bug 2: Network transfers should force past exe verification (FIXED)
**File:** `Services/XboxTransferService.cs` line ~248

The user pauses the Xbox download very early (~61 MB of metadata, no game content). Protected exes haven't been downloaded by the Xbox app yet. Even with correct paths (Bug 1 fixed), the verification correctly fails -> exit code 12 -> overlay blocked.

For network transfers, forcing past this check is safe. Gaming Services will re-download just the missing exes during Resume (small delta, not the full game).

**Fix applied:**
```csharp
// Changed from:
return await RunOverlayScriptAsync(xboxRoot, force, token);
// To:
return await RunOverlayScriptAsync(xboxRoot, force: true, token);
```

### Bug 3: Sender can't rescue protected exes because PFN is empty (FIXED)
**Files:** `Models/GameInfo.cs`, `Services/XboxLibraryScanner.cs`, `Services/XboxSenderService.cs`, `ViewModels/MainViewModel.cs`

The sender needs the PackageFamilyName (PFN) to rescue MSIXVC-protected executables via `Invoke-CommandInDesktopPackage`. Without PFN, all protected exes are skipped and must be re-downloaded from CDN by the receiver.

`ExtractPfnFromAcl()` was silently failing (returns null). The Xbox library scanner already extracts PFN during scan but threw it away (never stored in GameInfo).

**Fixes applied:**
1. Added `PackageFamilyName` property to `GameInfo`
2. `XboxLibraryScanner` now stores extracted PFN in `GameInfo.PackageFamilyName`
3. `XboxSenderService.ValidateSource()` now accepts a `pfnHint` parameter
4. Added `ExtractPfnViaAppxPackage()` PowerShell fallback method
5. PFN resolution chain: GameInfo hint -> ACL extraction -> PowerShell `Get-AppxPackage` fallback
6. `MainViewModel.PrepareXboxStreamingAsync()` passes `game.PackageFamilyName` to `ValidateSource()`

## Evidence from Logs

All logs at: `%LOCALAPPDATA%\GamesLocalShare\xbox-transfer\runs\`

**May 24 (drive-based transfer - WORKED):**
- Source: `E:\stage\Rematch` (pre-staged copy with PFN in transfer-summary.json)
- Verdict: `H1_FULL_SKIP` - only 0.83 MB re-downloaded from CDN
- PFN: `SLOCLAP.ProjectRuntime_cse8z5zpmcvkt` (correctly populated)
- Proves the overlay strategy works when the script actually runs

**May 26 (network transfers - ALL FAILED):**
- All 3 runs failed at exe verification with empty paths
- Error: `"Could not find a part of the path 'C:\XboxGames\D889719C-...\'"` (no filename!)
- PFN: empty in all runs
- No verdict files produced (script exited before overlay)
- No rescue directory exists (`%LOCALAPPDATA%\GamesLocalShare\xbox-network-rescued/` missing)

## Key Files

| File | Role |
|------|------|
| `Services/XboxTransferService.cs` | Receiver: downloads from peer, generates transfer-summary.json, runs overlay script |
| `Services/XboxSenderService.cs` | Sender: scans install folder, rescues protected exes, builds manifest |
| `Services/XboxNetworkSender.cs` | TCP server: streams files from manifest to receiver |
| `Services/XboxNetworkReceiver.cs` | TCP client: downloads files from sender |
| `Services/XboxScriptHost.cs` | Deploys and runs PS1 scripts via PsExec as SYSTEM |
| `Models/XboxOverlayManifest.cs` | Manifest model with Entries and SkippedProtectedFiles |
| `Models/GameInfo.cs` | Game model - now includes PackageFamilyName |
| `Services/XboxLibraryScanner.cs` | Scans XboxGames folders, extracts PFN from ACLs |
| `ViewModels/MainViewModel.cs` | Orchestrates sender/receiver, line ~2762 for network transfer, ~2900 for sender prep |
| `%LOCALAPPDATA%\GamesLocalShare\xbox-transfer\xbox-transfer-receiver-overlay.ps1` | The overlay PS1 script (deployed, not in repo) |
| `%LOCALAPPDATA%\GamesLocalShare\xbox-transfer\_common.ps1` | Shared PS1 helpers (Get-XboxPackageState, Copy-ProtectedFilesViaPackage, etc.) |

## Overlay Flow (end-to-end)

1. Receiver asks sender to prepare game (`RequestXboxStreamingAsync`)
2. Sender: `ValidateSource` -> `PrepareForDirectNetworkAsync` (scans files, rescues exes, builds manifest)
3. Sender: starts `XboxNetworkSender` TCP server with manifest
4. Receiver: `RunNetworkOverlayAsync` -> `XboxNetworkReceiver.ReceiveAsync` downloads all files to temp folder
5. Receiver: generates `transfer-summary.json` from manifest
6. Receiver: `ValidateSource(tempFolder)` reads the summary
7. Receiver: `RunOverlayScriptAsync` deploys and runs `xbox-transfer-receiver-overlay.ps1`
8. PS1 script (as SYSTEM via PsExec):
   - Reads transfer-summary.json
   - Polls for Xbox install folder (by game name or content GUID under XboxGames\)
   - Verifies receiver-provided exes (if any, unless -Force)
   - Robocopy overlays temp folder -> Xbox install folder (including .xvi metadata)
   - `icacls /reset /T` to fix ACLs
   - Tells user to click Resume in Xbox app
   - Monitors NIC traffic for 5 minutes to determine verdict

## What Needs Testing

1. **Close the running app** (PID 7928 is locking the exe)
2. **Rebuild** on BOTH sender and receiver PCs
3. On receiver: start Xbox app install, wait ~10s, pause
4. Initiate network transfer from the app
5. After download completes, the overlay should now actually run (no more exit 12)
6. When prompted, click Resume in Xbox app
7. Game should install with minimal CDN re-download

## If It Still Fails

- Check `%LOCALAPPDATA%\GamesLocalShare\xbox-transfer\runs\` for the latest logs
- Look for `receiver-overlay-system-*.log` - does it get past the exe verification?
- Look for `receiver-overlay-verdict-*.json` - what hypothesis?
- Check if `receiver-overlay-robocopy-*.log` exists (means robocopy ran)
- Check the sender logs for PFN resolution messages
- If PFN is still empty on sender: check if `Get-AppxPackage -AllUsers | Where { $_.Name -like '*Rematch*' }` returns anything on the sender PC

## Previous Fixes (from earlier sessions)

- StreamReader deadlock at 0.7% -> replaced with `ReadLineRawAsync` byte-by-byte reader
- Progress bar stuck on large files -> added mid-file progress every 4MB
- Slow TCP -> tuned socket buffers and disabled Nagle
- False "transfer complete" with 0.8 MB -> added 95% completeness sanity check
- Full re-download every retry -> stable gameAppId-based temp folder + file skipping
- Missing pause/resume/speed/ETA UI -> added to WebUI and wired through InteropBridge
