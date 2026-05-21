# Xbox Transfer Sender - Conversation Handoff (2026-05-21)

## What we're doing

Transferring Xbox (MSIXVC) games between two PCs using an overlay-on-paused-install
strategy. The sender stages a copy of the game, the receiver clicks Install+Pause in
the Xbox app, overlays the staged files, then clicks Resume. This saves 95-99% of
bandwidth vs downloading from CDN.

## The problem we just solved (partially)

**Hollow Knight Silksong** transfers fine. **Subnautica 2** installs but crashes on
launch with "Unsupported 16-Bit Application" / error `0x800700d8`
(`ERROR_EXE_MACHINE_TYPE_MISMATCH`).

### Root cause

`clipsp.sys` (Client License Content Protection) is a **kernel minifilter driver**
that transparently decrypts MSIXVC-protected game executables for authorized processes.
When an unauthorized process reads those files (even NT AUTHORITY\SYSTEM with backup
privilege), it gets the **raw encrypted bytes** instead of the decrypted executable.

The sender script:
- Stops GamingServices (releases user-mode locks) ✅
- Runs robocopy as SYSTEM with `/B` backup mode ✅
- But clipsp.sys stays loaded and serves encrypted content ❌

Result: EXE files are "copied" with correct size but encrypted content. The integrity
check passes (file exists, size matches) but the bytes are garbage. On the receiver,
Windows sees a corrupted PE header and reports "16-bit application".

**Hollow Knight worked** because Gaming Services downloaded its 2 small EXEs (~2.2 MB)
during the Install+Pause phase on the receiver. Those Gaming-Services-provided copies
were real decrypted files, and robocopy preserved them as `*EXTRA` files during overlay.
Subnautica's 4 EXEs (totaling ~238 MB, including a 209 MB shipping binary) weren't
downloaded before the user paused.

### What was tried

1. `fltmc unload clipsp` → **Failed** (exit code 1). Minifilter may be mandatory.
2. Robocopy `/B` (backup privilege) → Bypasses ACLs but NOT minifilter interception.
   Files copied but content is encrypted.

### What to try next (already in the updated script)

The sender script (`auto/xbox-transfer-sender.ps1`) has been updated to try, in order:

1. **`fltmc detach clipsp <volume>`** — detach clipsp from just the game's volume
   (e.g., `F:`). Less disruptive than full unload, may succeed where unload failed.
2. **`fltmc unload clipsp`** — full unload as fallback.
3. If both fail, continues with old behavior and warns.

After robocopy, the script restores clipsp (`fltmc attach` or `fltmc load`).

The script also now dumps diagnostic info:
- `fltmc` (list all loaded minifilters)
- `fltmc instances -f clipsp` (show volumes clipsp is attached to)

### If detach/unload both fail

Other approaches to investigate:
- **Volume Shadow Copy (VSS)**: Create a snapshot, read from
  `\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\...` — minifilter may not be
  attached to the shadow device.
- **`sc config clipsp start= disabled`** + reboot — nuclear option, disables the
  driver entirely until re-enabled.
- **Stop ClipSVC** (`Stop-Service ClipSVC`) before fltmc unload — the user-mode
  service might hold references that prevent unloading.

## How to test

On the sender PC:

```powershell
# Make sure the script is up to date (git pull or copy from repo)
.\auto\xbox-transfer-sender.ps1 -GameFolder "F:\Games\Subnautica 2" -Destination "G:\stage"
```

**What to look for in the output:**
1. `fltmc` listing — shows all loaded minifilters and their altitudes
2. `fltmc instances -f clipsp` — shows which volumes clipsp is attached to
3. `clipsp detached from F:` (GREEN) → success, files should be decrypted
4. `Unreadable: 0` → confirms all files readable after bypass
5. `IntegrityOk: true` + `MissingFiles: []` at the end

**How to verify the EXEs are actually decrypted (not just present):**
After staging, check the PE header of the main EXE:
```powershell
$bytes = [System.IO.File]::ReadAllBytes("G:\stage\Subnautica 2\Content\Subnautica2.exe")
# First two bytes should be 0x4D 0x5A ("MZ") for a valid PE executable
[char]$bytes[0], [char]$bytes[1]  # Should output: M Z
```
If you see `M Z`, the file is a real executable. If not, clipsp was still intercepting.

## Key files

- `auto/xbox-transfer-sender.ps1` — the sender script (MODIFIED)
- `auto/xbox-transfer-receiver-overlay.ps1` — the receiver script (unchanged)
- `auto/_common.ps1` — shared helpers
- `PLANNING/xbox-validation/HANDOFF.md` — original experiment handoff doc

## Constraints

- Both PCs need admin access and PowerShell 5.1+
- PsExec64.exe is auto-downloaded on first run
- The receiving PC's MS account must own/have Game Pass for the title
- Game must be MSIXVC type (has `.xvi/.xvs/.xct` envelope files + `Content\` subfolder)
- Never run as SYSTEM in the parent branch (recursion guard will abort)

## User preferences

- Terse, direct communication
- No emojis
- No unrequested code comments or fluff
- User makes changes between prompts — always re-read files before editing
- File citations: absolute-path-with-line-numbers format
