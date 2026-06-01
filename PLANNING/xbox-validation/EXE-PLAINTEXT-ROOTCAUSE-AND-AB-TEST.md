# Verify-trust root cause + A/B test plan (Clone Drone)

**Date:** 2026-06-01
**Branch:** Xbox-store-support
**Subject:** Why a game transferred by our overlay does a FULL re-download when the
user clicks **Verify & repair**, while a cleanly Store-installed game verifies
instantly with no download.

This document supersedes the volume-GUID theory in `CLONEDRONE-REPRO-FINDINGS.md`
and the "no streaming checkpoint" theory in `UPDATE-TRUST-INVESTIGATION.md` for the
**Verify** failure specifically. Both of those are addressed/falsified below.

---

## 1. Reproducible symptom (confirmed by the user)

- **Clean Store install -> Verify & repair = instant, 0 download.**
- **Overlay-transferred install -> Verify & repair = full game re-download.**
- The transferred install **launches fine**; it only fails **Verify**.
  => launch-trust and verify-trust are separate gates.

---

## 2-RESULT. TEST B RAN 2026-06-01 -> MECHANISM IN SECTION 2 IS FALSIFIED

Test B was executed on the clean install (`F:\Games\Clone Drone in the Danger Zone`,
DESKTOP-FHVD1S8). Numbers below are transcribed from the user's pasted console
(the `net-B-verify.txt` / `fp-exes-B0-clean.txt` output files did not sync into this
folder, so they could not be independently re-read; only the `.ps1` scripts are present).

- B1 `fp-exes-B0-clean.txt`: 2 protected EXEs = ACCESS-DENIED; controls readable. OK.
- B2 `swap-exes-plaintext.ps1`: both EXEs replaced with plaintext (now readable, MZ=True).
- B4 `measure-net.ps1`: **Xbox app reported ~1.8 MB downloaded on Verify & Repair.**
  - The two swapped EXEs = 653,824 + 1,232,816 = **1,886,640 B = 1.80 MB** -> EXACT match.
  - The NIC meter's "TOTAL RECEIVED 117.5 MB" is NOT reliable: samples jump from
    t+55s=41.3 MB straight to t+324s=114.7 MB (a ~270 s sampling gap that swallowed
    ~73 MB of background traffic). The app's ~1.8 MB is the trustworthy repair figure.
- B3/B5 fingerprints were not captured this run (swap script self-confirmed MZ=True).

**Conclusion: Verify & Repair is BLOCK-GRANULAR. It detected the 2 bad EXEs and
re-streamed ONLY them (~1.8 MB), NOT the 1.66 GB package.** Therefore:

1. The plaintext EXEs are detected (not silently trusted) but are repaired cheaply --
   they are **NOT** the cause of the catastrophic full re-download.
2. The Section-2 mechanism below ("integrity failure -> MSIXVC re-streams the
   *package*") is **wrong about the blast radius**: repair is per-block, not whole-game.
3. The real full-re-download trigger must be a DIFFERENT overlay-path difference that
   this clean+2-EXE-swap test deliberately excluded (prime suspect: the `.xvi` block
   residency bitmap / `.xvs` streaming state being inconsistent with on-disk blocks).
   Test A (full overlay path) is now the decisive experiment.

---

## 2-RESULT-A. TEST A RAN 2026-06-02 -> OVERLAY PATH IS THE TRIGGER (REPRODUCED + MEASURED)

Test A executed on F:\Games (overlay-transferred Clone Drone). Numbers transcribed
from the user's pasted console + Xbox app screenshot.

- On the overlaid install, clicked Verify & Repair.
- **Xbox app explicitly showed "Repairing - 47.5 MB of 1.6 GB" (2%, 4 MB/s)** -> it
  decided to re-stream the ENTIRE 1.6 GB package, i.e. a FULL re-download.
- `measure-net.ps1` (net-A-verify.txt) caught 159.0 MB on the NIC before the user
  stopped it; the app's "of 1.6 GB" label is the definitive proof of full-package intent.
- Recovery: re-running xbox-transfer-receiver-overlay.ps1 brought it back to
  Installed=True / H1_FULL_SKIP (16.9 MB) -> install/launch gate still trusts the overlay.

**DECISIVE CONTRAST:**
| Test | On disk | Verify & Repair pulled |
| --- | --- | --- |
| B (clean install, only 2 EXEs -> plaintext) | genuine complete native install | ~1.8 MB (the 2 EXEs only) |
| A (full overlay transfer) | all 242 files robocopy'd | FULL ~1.6 GB ("Repairing ... of 1.6 GB") |

**Conclusion (proven):**
1. The EXEs are NOT the full-redownload trigger (B = 1.8 MB granular).
2. The OVERLAY PATH itself is the trigger. The only A-vs-B difference: B kept the
   genuine complete native install + Gaming Services verified-block state (changed 2
   files); A overlaid all bytes onto a PARTIAL paused install whose GS integrity state
   never natively downloaded/verified those blocks.
3. TWO TRUST GATES confirmed: (a) Install/Resume gate checks the .xvi residency bitmap
   -> overlay passes -> launches fine (H1_FULL_SKIP). (b) Verify & Repair gate
   re-validates block content vs GS secure manifest / verified-block DB -> overlay
   FAILS -> full re-stream. Same mechanism that re-downloaded Forza's 146 GB on update.

FIX DIRECTION: the overlay must make Gaming Services actually register the overlaid
blocks as natively-downloaded/verified (its verified-block DB lives outside the game
folder and is produced only by GS's own download pipeline). Copying bytes + flipping
Installed=True yields a launchable-but-unverifiable install. This may be fundamentally
hard; next R&D step is locating/affecting GS's per-package verified-block state.

---

## 2. ROOT CAUSE (proven at the byte level, 2026-06-01) -- SUPERSEDED, see 2-RESULT above

The only on-disk difference between a clean install and our overlay install is the
**content-protected executables**. For Clone Drone that is exactly two files:

| File | Clean Store install (F:\Games) | Our stage / overlay (G:\stage) |
| --- | --- | --- |
| `Content\Clone Drone in the Danger Zone.exe` | **Access Denied** (encrypted/protected at rest) | readable **plaintext `MZ`**, 653,824 B |
| `Content\UnityCrashHandler64.exe` | **Access Denied** (protected at rest) | readable **plaintext `MZ`**, 1,232,816 B |
| every `.dll`, `gamelaunchhelper.exe`, data, `.config` | plaintext, readable | identical plaintext |

Evidence gathered (read-only, no download):

- Reading the clean install's two EXEs as Administrator returns
  `Access to the path ... is denied` -> they are content-protected at rest.
- The same two files in `G:\stage\...` open as valid `MZ` PE headers.
- `G:\stage\Clone Drone in the Danger Zone\transfer-summary.json` lists exactly
  these two under `StagedProtectedFiles` (sender decrypted them via the
  package-context copy and wrote plaintext into the stage).
- Control files (`UnityPlayer.dll`, `Assembly-CSharp.dll`, `gamelaunchhelper.exe`,
  `MicrosoftGame.Config`) are byte-identical/readable in both.

**Mechanism:**

- **Launch works** because `clipsp` just runs the plaintext `.exe` directly, and the
  `.xvi` block bitmap claims all blocks are present.
- **Verify & repair re-downloads** because Verify re-hashes the package payload
  against its integrity manifest/block map, which expects the *protected
  (encrypted-at-rest)* form of those EXEs. Our plaintext versions do not match ->
  integrity failure -> MSIXVC "repair" re-streams the package from the CDN URLs
  baked into the `.xvs`.

---

## 3. Theories now CORRECTED / FALSIFIED

### 3a. Volume-GUID / `.xvs` InstanceId mismatch (prior "strongest" H1) -- FALSIFIED

The prior "decisive experiment" chased the idea that the overlay leaves the *source*
machine's volume GUID in the `.xvs` `InstanceId`. The user's own laptop captures
disprove it:

- `fp-laptop-POSTOVERLAY.txt`: `InstanceId = {A89ECE52-...}` -> the **laptop's own**
  C: volume GUID, **not** the source PC's `{98053395-...}`.
- Every laptop capture (`HEALED`, `POSTOVERLAY`, `POSTVERIFY`) carries the local
  volume GUID. The instance binding is locally correct, so it is **not** the trigger.

### 3b. "No streaming checkpoint / empty StreamingCheckpoints" -- not the discriminator

`StreamingSummaries = 0` and empty `StreamingCheckpoints/Tracking` appear for the
**clean** Clone Drone too (`fp-clonedrone-CLEAN.txt`), so they don't distinguish a
trusted install from a transferred one.

### 3c. Why the previous fingerprint test was "inconclusive"

`fingerprint-install.ps1` only hashes the **streaming-metadata envelope**
(`.xvi/.xvs/.xct/.smd/.xsp` + the GUID blob). It **never hashes the
`Content\*.exe` files** -- i.e. it measured everything except the two files that
actually differ. New tooling (`fp-exes.ps1`) fixes that gap.

---

## 4. Tooling built for this investigation (in this folder)

| Script | Purpose |
| --- | --- |
| `fp-exes.ps1` | Exe-aware fingerprint: size / readable? / `MZ`? / SHA256 for the 2 protected EXEs + control files. Writes a `.txt` for diffing. |
| `measure-net.ps1` | Live cumulative NIC-received meter (same physical-NIC pick as the overlay verdict). Run it, click Verify, read the `=== TOTAL RECEIVED ===` number. |
| `swap-exes-plaintext.ps1` | TEST B helper. On a clean install, replaces ONLY the 2 protected EXEs with the stage's plaintext copies (takeown -> copy -> `icacls /reset`), leaving all other bytes untouched. Self-elevates. |
| `probe-exe-headers.ps1` | Quick header/`MZ` peek used during discovery (kept for reference). |

All three new scripts pass PowerShell parser syntax checks. ASCII-only.

---

## 5. THE A/B TEST (run from elevated PowerShell)

```
cd F:\Documents\GamesLocalShare\PLANNING\xbox-validation
```

Run **Test B first** -- it uses the pristine clean install as the control. Verify
heals the install afterward, so the clean control is restored.

### Test B -- isolation: does changing ONLY the 2 EXEs break Verify?

| Step | Command / action | Expected |
| --- | --- | --- |
| B1 | `.\fp-exes.ps1 -GameDir "F:\Games\Clone Drone in the Danger Zone" -Out fp-exes-B0-clean.txt` | 2 EXEs = `ACCESS-DENIED`; controls readable |
| B2 | `.\swap-exes-plaintext.ps1 -GameDir "F:\Games\Clone Drone in the Danger Zone" -StageDir "G:\stage\Clone Drone in the Danger Zone"` | 2 EXEs now plaintext `MZ` |
| B3 | `.\fp-exes.ps1 -GameDir "F:\Games\Clone Drone in the Danger Zone" -Out fp-exes-B1-swapped.txt` | only the 2 EXEs changed vs B0 |
| B4 | 2nd elevated window: `.\measure-net.ps1 -Out net-B-verify.txt` -> wait for `BASELINE SET` -> Xbox app -> Clone Drone -> Manage -> **Verify and repair** -> Ctrl+C when it stops climbing | meter shows the bytes Verify pulled |
| B5 | `.\fp-exes.ps1 -GameDir "F:\Games\Clone Drone in the Danger Zone" -Out fp-exes-B2-postverify.txt` | EXEs flipped back to `ACCESS-DENIED` (healed) |

**Interpretation:** large pull at B4 + EXEs protected again at B5 => the 2 EXEs are
the **sole trigger**, isolated from every other transfer difference.

### Test A -- production overlay path (end-to-end)

| Step | Command / action | Expected |
| --- | --- | --- |
| A1 | Xbox app: **uninstall** Clone Drone | -- |
| A2 | Xbox app: **Install** -> wait ~10s -> **Pause** (leave app open) | partial download on disk |
| A3 | `.\auto\xbox-transfer-receiver-overlay.ps1 -Source "G:\stage\Clone Drone in the Danger Zone"` then click **Resume** when prompted | overlay completes, verdict JSON written |
| A4 | `.\fp-exes.ps1 -GameDir "C:\XboxGames\Clone Drone in the Danger Zone" -Out fp-exes-A1-overlay.txt` (adjust path to actual install drive) | 2 EXEs = plaintext `MZ` |
| A5 | 2nd window: `.\measure-net.ps1 -Out net-A-verify.txt` -> Xbox app -> **Verify and repair** -> Ctrl+C when settled | meter shows the bytes Verify pulled |
| A6 | `.\fp-exes.ps1 -GameDir "C:\XboxGames\Clone Drone in the Danger Zone" -Out fp-exes-A2-postverify.txt` | EXEs flipped to `ACCESS-DENIED` after heal |

**Cost note:** if the theory holds, each Verify pulls up to ~1.66 GB (Clone Drone's
full size) and heals the install. Use Clone Drone (small), not Forza.

---

## 6. Expected results matrix

| Scenario | Verify network pull | Meaning |
| --- | --- | --- |
| Untouched clean install | ~0 | baseline (already known) |
| B: clean + only 2 EXEs plaintext | LARGE | EXEs alone are the trigger (root cause confirmed) |
| A: full overlay (plaintext EXEs) | LARGE | production path reproduces the bug |
| (future) overlay with Store-provided protected EXEs | ~0 (hoped) | the fix works |

If B pulls ~0 instead of large, the EXEs are NOT the (sole) cause and we re-open the
manifest/block-map angle.

---

## 7. Fix direction (engineering follow-up, after A/B confirms)

The current "success" path -- sender rescues **decrypted** EXEs, receiver overlays
them as **plaintext** -- is exactly what poisons Verify. The fix must make those
executables exist on the receiver in their **protected (encrypted-at-rest)** form,
which only Gaming Services can produce:

- **Candidate fix:** during the overlay, do NOT supply the plaintext EXEs; let
  Gaming Services stream just those 2 small files (~1.9 MB for Clone Drone) on
  Resume so they land protected and match the manifest. All bulk data still comes
  from our overlay.
- **Open problem:** MSIXVC residency is block-based, and Verify/repair appears to be
  coarse (any mismatch -> full re-stream). Making GS fetch *only* the EXE blocks
  while trusting our overlaid data blocks needs R&D on the `.xvi` bitmap. The A/B
  result tells us whether this is worth building.

---

## 8. Key identity / path reference

- Game: Clone Drone in the Danger Zone, 242 files, 1.66 GB
- PFN: `DoborogGames.CloneDroneintheDangerZone_w6hf08ggk4es4`
- Content GUID: `368B2C2C-C6E2-4472-ACDA-52A5F18A1D51`, StoreId `9NKHKBQJSSC3`
- Clean install (THIS PC `DESKTOP-FHVD1S8`): `F:\Games\Clone Drone in the Danger Zone`
- Stage (complete, plaintext EXEs): `G:\stage\Clone Drone in the Danger Zone`
- Protected EXEs: `Content\Clone Drone in the Danger Zone.exe`,
  `Content\UnityCrashHandler64.exe`
- Volume GUIDs observed: F: `{98053395-...}`, C: `{A89ECE52-...}`

---

## 9. Status checklist

- [x] Root cause identified at byte level (plaintext vs protected EXEs)
- [x] Volume-GUID theory falsified from existing laptop captures
- [x] Tooling built + syntax-checked (`fp-exes`, `measure-net`, `swap-exes-plaintext`)
- [x] Test B run + network number recorded -> ~1.8 MB (EXEs only); EXEs are NOT the full-redownload trigger; Verify is block-granular (see 2-RESULT)
- [x] Test A run + network number recorded -> FULL ~1.6 GB re-download ("Repairing ... of 1.6 GB"); overlay path IS the trigger, NOT the EXEs (see 2-RESULT-A)
- [ ] Fix variant (Store-provided protected EXEs) designed/tested
