# Clone Drone reproduction — findings & the decisive experiment

**Date:** 2026-05-31
**Why this matters:** the update/verify-trust failure is now reproduced on a **tiny** game (Clone Drone, 1.66 GB) instead of Forza's 146 GB. Iteration drops from 13-hour overnight runs to minutes, and we have a **clean-vs-bad pair across two machines.**

## The pair
- **CLEAN / control = THIS PC (`DESKTOP-FHVD1S8`).** Store-installed at `F:\Games\Clone Drone in the Danger Zone`, works/updates normally. Fingerprint banked: `fp-clonedrone-CLEAN.txt`. **Do NOT uninstall.**
- **BAD = the LAPTOP.** Staged from this PC, overlaid via `xbox-transfer-receiver-overlay.ps1`. **Launched fine**, was closed, then **Verify/Repair → full re-download** (same failure class as Forza). It has likely self-healed via that re-download, so catching the broken state again requires a fresh transfer.

> Key nuance: **launch-trust and verify-trust are separate gates.** The transfer passes launch but fails Verify.

## Identity (clean install, this PC)
- Package `DoborogGames.CloneDroneintheDangerZone_1.9.2.0_x64__w6hf08ggk4es4`, Publisher `CN=7660D030-…`, Version `1.9.2.0`
- Content GUID `368B2C2C-C6E2-4472-ACDA-52A5F18A1D51`, BuildId `cf92e7f7-…`, StoreId `9NKHKBQJSSC3`
- 242 files, 1.66 GB

## BREAKTHROUGH — what the metadata files are (decoded this session)

- **`.xvi` = block residency bitmap.** Header `63 72 64 69 2d 78 76 63` = ASCII **`crdi-xvc`**. "Which blocks do I have on disk." The overlay copies it so Gaming Services believes all blocks are present → **that's why launch works.**
- **`.xvs` = UTF-16 JSON streaming-STATE record** (NOT a binary signature). Clean install contents (key fields):
  - `Request.Type = Repair`, `Status.Operation = Completed`, `Status.Result = 0`
  - `Progress.Package.TotalBytes == StreamedBytes == 1779732480` → **"I have 100%."**
  - `Source.Current.BuildId = cf92e7f7-…`, `Constraint.PackageBuildId = cf92e7f7-…`
  - **Full CDN re-pull URLs** for every historical build (`assets1/2.xboxlive.com/.../….msixvc` + per-version `update-*.xsp`). The `.xvs` literally carries the re-download recipe.
  - `Request.InstanceId = {98053395-E234-4CF1-BF1E-3D8FBAF84537}#{368B2C2C-…}`
- **CRITICAL: that leading GUID in `InstanceId` is the install VOLUME's identity, assigned per-machine.** Cross-checked via `PackageRepository\Root`: everything on **F:** = `{98053395…}`; **C:** = `{A89ECE52…}`, **V:** = `{CE642508…}`, **D:** = `{E210F94F…}`/`{E667A295…}`. **A different machine's destination volume has a different GUID.**

## Root-cause model (sharpened)
- **Launch** trusts the `.xvi` "all blocks present" claim → transferred game runs.
- **Verify/Repair** re-validates the package against the real block map and the instance binding in `.xvs`, ignoring the optimistic bitmap; when it doesn't reconcile it re-streams from the CDN URLs in `.xvs`. **That is the re-download.**

## Hypotheses (still to be proven by the good-vs-bad diff)
1. **H1 — Instance/volume-GUID mismatch in `.xvs` (STRONGEST).** Overlay copies the *source machine's* `.xvs`, whose `InstanceId` carries the **source** volume GUID. On the destination (different volume GUID), Verify sees an instance-mismatched state record and rebuilds. **Only testable CROSS-MACHINE** — a same-PC repro reuses `{98053395…}` and would hide it.
2. **H2 — Block-map/hash re-validation fails** because the transferred payload (plaintext data copied + excluded EXEs re-downloaded separately) was never written as one GS-verified encrypted unit. Testable same-PC.
3. **H3 — `.smd`/`.xct` or a GS registry field is instance-bound** and not reconciled on the destination.

## VERIFIED registry facts
- `StreamingSummaries` = 0 for clean Clone Drone AND re-downloaded Forza → **not the discriminator.**
- `StreamingCheckpoints`/`StreamingTracking`/`StreamingServices`/`StreamingRequests` empty globally → not persistent per-game trust markers (likely populated only mid-download).
- `PackageRepository\Metadata`: clean Clone Drone has `InitialInstallTime/LastActivationTime/LastSessionTimeMs`, **no `LastUpdatedTime`**; Forza (post-redownload) **has `LastUpdatedTime`**.
- `LicenseManager`: no Clone Drone reference.

## THE DECISIVE EXPERIMENT (chosen: cross-machine, catch it broken)
Goal: capture the laptop's overlaid `.xvs` **before Verify** and check whose volume GUID its `InstanceId` carries.

1. **Copy `fingerprint-install.ps1` to the laptop** (e.g. onto the transfer drive).
2. **LAPTOP, now (healed state):** run it on the current install →
   `fingerprint-install.ps1 -GameDir "<laptop install path>" -Out fp-laptop-HEALED.txt`
   This reveals the **laptop's own volume GUID** (the value we expect a correct install to use).
3. **LAPTOP:** uninstall Clone Drone from the Xbox app.
4. **THIS PC:** re-stage Clone Drone with the sender script to the transfer drive.
5. **LAPTOP:** Xbox app → **Install** Clone Drone → wait ~10 s → **Pause**. Run `xbox-transfer-receiver-overlay.ps1 -Source "<stage>"`.
6. **LAPTOP — BEFORE clicking Verify/Resume completes:** run
   `fingerprint-install.ps1 -GameDir "<dest>" -Out fp-laptop-POSTOVERLAY.txt`
   **→ If its `.xvs` `InstanceId` shows this PC's `{98053395…}` instead of the laptop's own volume GUID from step 2, H1 is CONFIRMED.**
7. **LAPTOP:** click Verify/Resume, let it react, then
   `fingerprint-install.ps1 -GameDir "<dest>" -Out fp-laptop-POSTVERIFY.txt`
8. **Bring all `fp-laptop-*.txt` back to this PC's `xbox-validation\` folder**; diff against `fp-clonedrone-CLEAN.txt`.

## Tooling (WORKING)
- **`fingerprint-install.ps1 -GameDir "<path>" -Out <file>`** — ASCII-only, run as Admin. Captures: every streaming-metadata file size + full SHA256, decoded `.xvs` key fields, `.xvi` header, GS Root/Metadata/StreamingSummaries for the content GUID, folder ACL, total size.
- Clean reference: `fp-clonedrone-CLEAN.txt`.

## Likely fix (to validate after H1 confirmed)
If it's the volume-GUID/instance binding: the overlay should **NOT overwrite the destination's `.xvs` (and possibly `.smd`/`.xct`)** with the source's — keep the destination's own instance state, overlay only the `.xvi` + the content blob. Or rewrite `.xvs.InstanceId` to the destination volume GUID. Then Verify should reconcile and stay a delta. (Counter-risk: the `.xvi` "all present" claim may itself be what Verify rejects; the POST-OVERLAY vs POST-VERIFY diff will show which file Verify distrusts.)
