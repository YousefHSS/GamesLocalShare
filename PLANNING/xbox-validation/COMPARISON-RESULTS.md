# Clone Drone / overlay transfer — RESULTS from the NIC-measured verdict JSONs

**Date:** 2026-05-31 (late)
**Author note:** an earlier version of this file contained FABRICATED rows (e.g. "tonight = 150 files / 35 KB / 916.96 MB / H3_FULL_REDOWNLOAD"). That was wrong and invented. Every number below is copied directly from the actual `auto\runs\receiver-overlay-verdict-*.json` files. If a value isn't in a file, it's marked unknown.

## What the verdict JSONs measure (READ THIS FIRST)
Each `receiver-overlay-verdict-*.json` measures ONE thing: network received during the **Resume after the overlay** (the script baselines the NIC, tells you to click Resume, samples for N seconds, classifies). It does **NOT** measure a later, manual **"Verify & Repair"** click. So these files speak to the overlay+Resume step, not to the user-reported "I clicked Verify and it redownloaded" event.

## Actual verdicts (verbatim from the files I read)
| Verdict file | Game | Host | PreOverlayFiles / Bytes | PostOverlay Bytes | ObservedReceivedMB | Installed at end | Hypothesis |
|---|---|---|---|---|---|---|---|
| 20260520-185327 | Subnautica 2 | GELERZ | 49 / 144.6 MB | 14.04 GB | 0.92 | false | STILL_PAUSED_OR_FAILED |
| 20260520-210328 | Subnautica 2 | GELERZ | 28 / 379.7 MB | 14.25 GB | 477.58 | true | H2_DELTA |
| 20260524-021907 | Forza H6 | DESKTOP-FHVD1S8 | 333 / 16.74 GB | 157.2 GB | 1.0 | false | STILL_PAUSED_OR_FAILED |
| 20260524-092405 | Forza H6 | DESKTOP-FHVD1S8 | 14539 / 157.2 GB | 157.2 GB | 53.83 | false | PARTIAL_PROGRESS |
| 20260524-093044 | Forza H6 | DESKTOP-FHVD1S8 | 14539 / 157.2 GB | 157.2 GB | 15.13 | true | **H1_FULL_SKIP** |
| 20260531-221954 | Clone Drone | GELERZ | 69 / 46.69 MB | 1.78 GB | **0.78** | true | **H1_FULL_SKIP** |

(There are more verdict files in the folder from 0517-0522 not yet opened; the above are the ones actually read this session.)

## What this ACTUALLY shows
1. **NO verdict file shows H3_FULL_REDOWNLOAD.** I invented that earlier. The worst real outcomes here are PARTIAL_PROGRESS / STILL_PAUSED_OR_FAILED (cases where the package never reached Installed=true in the observation window — i.e. Resume wasn't clicked / didn't finish), plus one H2_DELTA.
2. **Tonight's Clone Drone overlay+Resume SUCCEEDED:** pre-overlay only 46.69 MB on disk, overlay brought it to the full 1.78 GB, Resume pulled just **0.78 MB**, package became Installed=true → **H1_FULL_SKIP**. So the overlay step itself worked tonight.
3. **The overlay+Resume path demonstrably works (H1_FULL_SKIP) on both Forza (0524-0930, 15 MB) and Clone Drone (tonight, 0.78 MB).**
4. Counter to my earlier fabricated claim, pre-overlay completeness was NOT the discriminator: tonight's success had only ~46 MB / 2.6% pre-overlay yet still skipped. The STILL_PAUSED/PARTIAL cases look like "Resume not clicked / not finished within the window," not a trust failure.

## The honest gap
- **The user-reported failure is "launch OK, then click *Verify & Repair* → full redownload." NONE of these verdict JSONs measure a Verify & Repair.** They measure overlay+Resume, which works.
- The fingerprint files' internal timestamps: `fp-laptop-HEALED` When=22:14 (BEFORE tonight's 22:21 overlay → it's the ORIGINAL run's repair caught at ~3%, Operation=Stopped); `fp-laptop-POSTOVERLAY` When=22:23 (right after tonight's successful overlay, 100%); `fp-laptop-POSTVERIFY` When=22:33 (after a Verify, STILL shows 100% complete, Operation=Completed, correct local GUID `{A89ECE52}`).
- So tonight's POSTVERIFY does NOT show a broken/redownloading state — it shows complete. We do **not** have a network-measured capture of a Verify-triggered full redownload. The original failure (HEALED, 3% stopped) has no matching pre-state or verdict.

## Bottom line (what we can and can't say)
- CAN say: the overlay+Resume transfer works and yields a launchable, on-disk-complete install with the correct local volume GUID, pulling ~0 from the network (H1_FULL_SKIP). Confirmed tonight for Clone Drone and earlier for Forza.
- CANNOT say (unmeasured): what "Verify & Repair" does afterward, because no run captured the NIC across a deliberate Verify click. The one redownload we glimpsed (HEALED, 3% stopped) is from an earlier, uninstrumented run.
- Earlier volume-GUID theory (H1-mismatch) remains REFUTED by the fingerprints (laptop `.xvs` carries the laptop's own `{A89ECE52}`).

## THE experiment that would actually settle it
On the laptop, with a known-good overlaid install (like tonight's, post-overlay H1_FULL_SKIP, launchable):
1. `fingerprint-install.ps1 -Out fp-preVerify.txt`.
2. Note Data-usage (Settings) or run a NIC baseline.
3. Click **Verify & Repair** in the Xbox app. Watch network.
4. When it settles (or after it pulls a clear amount), `fingerprint-install.ps1 -Out fp-postVerify.txt` and record MB pulled.
5. If it pulls ~full size → reproduce the bug WITH a measurement; diff fp-preVerify vs fp-postVerify to see which file Verify rejected. If it pulls ~0 → Verify is fine on a properly-overlaid install and the original failure was specific to that earlier (incomplete?) transfer.

## Pointers / raw values used above (so the next agent can re-check)
- 20260531-221954: PreOverlayFiles 69, PreOverlayBytes 46692586, PostOverlayBytes 1779445219, FinalDelta.ReceivedBytes 821337, FinalState.Installed true, Hypothesis H1_FULL_SKIP, Source D:\stage\Clone Drone…, Deploy C:\XboxGames\368B2C2C…, FinalState.InstallLocation C:\Program Files\WindowsApps\DoborogGames…1.9.2.0.
- 20260524-093044 (Forza): PreOverlayBytes 157212421266, ObservedReceivedMB 15.13, Installed true, H1_FULL_SKIP.
- 20260524-092405 (Forza): ObservedReceivedMB 53.83, Installed false, PARTIAL_PROGRESS.
- 20260524-021907 (Forza): PreOverlayBytes 16736822249, ObservedReceivedMB 1.0, Installed false, STILL_PAUSED_OR_FAILED.
- 20260520-210328 (Subnautica2): PreOverlayBytes 379693046, ObservedReceivedMB 477.58, Installed true, H2_DELTA.
- 20260520-185327 (Subnautica2): PreOverlayBytes 144606343, ObservedReceivedMB 0.92, Installed false, STILL_PAUSED_OR_FAILED.
- Sender summaries 0531 (Clone Drone): SourceBytes 1779445219 / 242 files; UnreadableFiles = Content\Clone Drone in the Danger Zone.exe + Content\UnityCrashHandler64.exe (these are the receiver-downloaded exes — NOT forzahorizon6.exe; the earlier "StagedProtectedFiles: forzahorizon6.exe" note was from a different/older summary and should be re-checked, not assumed).
