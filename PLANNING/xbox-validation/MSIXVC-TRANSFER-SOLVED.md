# MSIXVC Game Transfer - SOLVED

> **Status: WORKING, proven end-to-end on 2026-05-21.**
> Subnautica 2 (13.6 GB, MSIXVC) transferred from sender to receiver. The
> receiver finalized the install with no meaningful CDN download and the
> game's own executable launched (reached Subnautica 2's in-game DirectX 12
> error - a game/hardware message, which only the real executable can
> produce; a corrupt EXE fails earlier at the OS level).
>
> This document is the authoritative record. The older `HANDOFF.md` and
> `SENDER_HANDOFF.md` predate the solution and are superseded by this file.

## 1. Goal

Move an Xbox PC (MSIXVC / Game Pass) game from one PC to another without
re-downloading the multi-GB payload from Microsoft's CDN. The receiving
account must legitimately own / have Game Pass for the title - we only
shortcut the byte transfer, never licensing.

- **Sender** - has the game installed (e.g. `F:\Games\<Title>\`).
- **Receiver** - owns the title, installs via the Xbox app.

## 2. The core problem

An MSIXVC game install is mostly plain files, **except its executables**.
The `.exe` files are content-protected: on disk they are encrypted, and the
`clipsp` ("Client License System Platform") component transparently decrypts
them **only for a process carrying the game's package identity**.

Consequences:

- A normal copy - even `NT AUTHORITY\SYSTEM` with robocopy `/B` backup mode -
  cannot read these executables. The read returns encrypted bytes, or
  outright `ERROR 5 Access Denied`.
- For Subnautica 2 the 4 protected files are:
  - `Content\Subnautica2.exe`
  - `Content\Engine\Binaries\Win64\CrashReportClient.exe`
  - `Content\Subnautica2\Binaries\WinGDK\Subnautica2-WinGDK-Shipping.exe`
  - `Content\Subnautica2\Plugins\Sentry\Binaries\Win64\crashpad_handler.exe`
- Everything else (assets, data, the XVC envelope files) copies fine.

If the encrypted/garbage executables are staged and overlaid onto the
receiver, the game install completes but the EXE is corrupt - Windows
reports "Unsupported 16-Bit Application" / `0x800700d8`
(`ERROR_EXE_MACHINE_TYPE_MISMATCH`) on launch.

## 3. Dead ends (do not retry these)

| Attempt | Result | Why |
|---|---|---|
| `fltmc detach/unload clipsp` | Fails, `0x801f0013` `FLT_FILTER_NOT_FOUND` | `clipsp` is **not** a Filter Manager minifilter. `fltmc` can never touch it. |
| Disable a decrypting filter to "read plaintext" | Wrong model | Disabling a decrypt filter yields **ciphertext**, not plaintext. |
| Robocopy `/B` as SYSTEM | Copies encrypted/garbage bytes, or `ERROR 5` | Backup privilege bypasses ACLs, not content protection. |
| Overlay the sender's **encrypted** EXE copies | Game crashes: "16-Bit Application" | Encryption keys are not portable across machines/installs. |

## 4. The breakthrough - package-context copy

`clipsp` decrypts protected executables for any process that carries the
**game's package identity**. We do not need to defeat the protection - we
borrow the identity.

`Invoke-CommandInDesktopPackage` (PowerShell, `Appx` module) launches a
process inside an installed package's context. A helper script launched this
way runs with the game's package identity, so when it reads the protected
EXEs, `clipsp` hands back **fully decrypted, valid executables** - something
even SYSTEM cannot get.

Verified on the sender: all 4 Subnautica 2 executables came out with valid
`MZ` (`4d5a`) PE headers and correct sizes.

Crucially, the **plaintext** executables produced this way DO work when
overlaid onto the receiver (unlike the old encrypted copies). The receiver's
overlay uses `/COPY:DAT`, which does not carry extended attributes, so the
plaintext file lands with no content-protection marker and the game reads it
directly. Proven 2026-05-21.

## 5. Final architecture

### Sender - `auto\xbox-transfer-sender.ps1`

1. Self-elevates, downloads PsExec on first run.
2. Re-launches as `NT AUTHORITY\SYSTEM` via PsExec.
3. **SYSTEM phase**: stops Gaming Services (releases file locks), scans the
   source, detects which files are unreadable (the protected EXEs), robocopies
   everything else with `/E /COPYALL /B`, excluding the unreadable files via
   `/XF`. Removes any stale (prior-run) copies of the excluded files from the
   stage. Writes an honest `transfer-summary.json` (integrity fails on
   `robocopy exit >= 8`; excluded files are not counted as missing).
4. **User phase (after SYSTEM returns)**: if `transfer-summary.json` lists
   `ReceiverProvidedFiles`, it calls `Copy-ProtectedFilesViaPackage`
   (`_common.ps1`) to rescue those executables via the package context and
   write them straight into the stage. On success it clears
   `ReceiverProvidedFiles` and records `StagedProtectedFiles`.

Result: a **complete** stage - all files including real, decrypted EXEs.

### Receiver - `auto\xbox-transfer-receiver-overlay.ps1`

1. User clicks **Install** in the Xbox app, waits ~10 s, clicks **Pause**.
2. Script (as SYSTEM) locates the in-progress install folder
   (`<XboxRoot>\<FriendlyName>` or `<XboxRoot>\<ContentGUID>`).
3. **Pre-overlay check**: if `transfer-summary.json` still lists
   `ReceiverProvidedFiles` (i.e. the sender's package-context rescue failed),
   it verifies those EXEs exist on disk with valid `MZ` headers BEFORE
   overlaying. If not, it aborts cleanly (exit 12) so the user can let the
   genuine download continue. When the sender rescue succeeded this list is
   empty and the check is skipped - nothing to wait for.
4. Robocopies the stage over the install with `/E /COPY:DAT /IS /IT` (no
   `/MIR`). Resets ACLs with `icacls /reset /T`.
5. User clicks **Resume**. Script measures NIC traffic + package state and
   writes a verdict JSON.

## 6. How to use it

### On the sender

```powershell
cd F:\Documents\GamesLocalShare\PLANNING\xbox-validation\auto
.\xbox-transfer-sender.ps1 -GameFolder "F:\Games\<Title>" -Destination "G:\stage"
```

Expect, near the end:

```
Rescuing 4 protected executable(s) via package context...
  OK   Content\...
  ...
All protected executables staged - receiver needs no extra download.
  Integrity OK:      True
```

The complete stage is at `G:\stage\<Title>`.

### On the receiver

1. Move `G:\stage\<Title>` to the receiver.
2. Xbox app: **Install** the title, wait ~10 s, **Pause**.
3. Run:
   ```powershell
   .\xbox-transfer-receiver-overlay.ps1 -Source "<path>\<Title>"
   ```
   Add `-XboxRoot "<Drive>:\XboxGames"` if the title installs off `C:`.
4. Click **Resume** when prompted.

No watching or guessing on the receiver - the sender provides everything.

## 7. Key technical facts

- **clipsp** decrypts protected EXEs only for processes with the game's
  package identity. It is not a minifilter; `fltmc` cannot touch it.
- **Package-context copy** (`Invoke-CommandInDesktopPackage`) is the only
  known way to extract decrypted executables from the sender.
- The sender's package context must have the game's license active (the
  sender account owns / has Game Pass for the title).
- Plaintext EXEs overlaid with robocopy `/COPY:DAT` work on the receiver;
  encrypted copies (old approach) do not - keys are not portable.
- MSIXVC vs plain MSIX: only MSIXVC titles (`<Drive>:\XboxGames\` or
  `<Drive>:\Games\`, with `.xvi/.xvs/.xct` envelope + `Content\` subfolder)
  need this transfer. Plain MSIX uses standard AppX deployment.
- Gaming Services downloads to a folder named after the **content GUID**,
  renaming it to the friendly title only after install completes. The GUID
  is the basename of the `.xvi` file.

## 8. Key code

- `auto\_common.ps1` - `Copy-ProtectedFilesViaPackage` (package-context copy),
  `Invoke-AsSystem`, `Ensure-PsExec`, etc.
- `auto\xbox-transfer-sender.ps1` - staging + package-context rescue.
- `auto\xbox-transfer-receiver-overlay.ps1` - overlay-on-paused-install +
  pre-overlay EXE verification.

## 9. Known issues / TODO

- **Cosmetic**: `transfer-summary.json` `FilesCopied` / `BytesCopied` are
  measured in the SYSTEM phase before the package-context rescue adds the
  protected EXEs, so they under-count (e.g. 289 instead of 293). Correctness
  is unaffected - the receiver keys off `ReceiverProvidedFiles`.
- **Cosmetic**: the parent prints a blank "SYSTEM phase exited with code"
  when PsExec's `$proc.ExitCode` comes back null; the real exit code is in
  `<log>.err` ("exited with error code N").
- **Cosmetic**: `Invoke-AsSystem` log tailing can print the final lines
  twice.
- Not yet tested on a second MSIXVC title to confirm generality.
- The receiver still does a brief (~10 s) genuine download for the Xbox app
  to create the StateRepository row; this is required and intentional.

## 10. Working constraints

- Both PCs: PowerShell 5.1+, admin access.
- PsExec64.exe auto-downloads to `auto\tools\` on first run.
- The receiving account must own / have Game Pass for the title.
- Never run the sender/receiver as SYSTEM in the parent branch (recursion
  guard aborts).
