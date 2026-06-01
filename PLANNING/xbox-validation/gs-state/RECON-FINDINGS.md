# Gaming Services state recon — where does Verify's "trust" live?

**Date:** 2026-06-02  **Game:** Clone Drone (content GUID 368B2C2C, on F:, vol {98053395})
**Goal:** find the state that makes Verify & Repair re-download an overlay install (full)
while a native install verifies for ~0.

## Method
Read-only recon of `HKLM\SOFTWARE\Microsoft\GamingServices` + the on-disk install,
comparing the OVERLAY Clone Drone against NATIVE titles on the same PC. Overlay registry
baseline exported to `gs-state\overlay\gamingservices.reg`.

## Findings

### 1. Trust is NOT in the GamingServices registry  (ELIMINATED)
`PackageRepository\Metadata\{vol}#{368B2C2C}` holds only:
`InitialInstallTime`, `UsingAppLicensing=1`.
Native titles (No Man's Sky, Goat Sim 3, Stardew, Silksong, Subnautica 2, Expedition 33...)
have the SAME two values, plus only usage telemetry that appears after you play/update
(`LastActivationTime`, `LastSessionTimeMs`, `LastUpdatedTime`). Structurally identical.
- `StreamingCheckpoints`, `StreamingTracking`, `StreamingRequests` = EMPTY (no block ledger).
- `StreamingSummaries` = 3 session GUIDs, each `{vol}#{368B2C2C} = 0` (0 bytes streamed).
- `Store\ContentId\{368B2C2C}` = VersionId 1.9.2.0.cf92e7f7..., ProductId 9NKHKBQJSSC3.
- `PackageBackups` empty; no LicenseManager ref.
=> We CANNOT fix this with a registry flag; the registry doesn't distinguish trusted vs not.

### 2. The install layout
- `F:\WindowsApps\DoborogGames...CloneDrone..._1.9.2.0...` is a JUNCTION ->
  `F:\Games\Clone Drone in the Danger Zone\Content\`.
- Real install: `F:\Games\Clone Drone in the Danger Zone\` (metadata) + `\Content\` (1.6 GB).
- The transient GUID-named streaming folder `F:\Games\368B2C2C-...` is gone after finalize.

### 3. Metadata file SET is complete & normal (NOT a missing-file bug)
`F:\Games\<Game>\` for each title (all hidden -a-h--):
| file | CloneDrone (overlay) | Silksong (native) | Subnautica2 |
| --- | --- | --- | --- |
| `<GUID>` data file | 14,303,232 | 62,103,552 | 88,535,040 |
| `<GUID>.<g>.xsp` | 7,904 | 40,608 | 18,720 |
| `<GUID>.smd` (signed manifest) | 35,078 | 531,242 | 34,760 |
| `<GUID>.xct` | 4,096 | 4,096 | 4,096 |
| `<GUID>.xvi` (residency bitmap) | 4,096 | 12,288 | 8,192 |
| `<GUID>.xvs` | 10,118 | 10,400 | 13,998 |
| `<GUID>.ffs` | (none) | (none) | 876 |
Clone Drone has the full set; `.ffs` is not the marker (Silksong lacks it too).

## Conclusion so far
The trust signal Verify uses is NOT the registry and NOT a missing metadata file. It must be
in the *content* of the integrity metadata / the at-rest content blocks. Two live hypotheses,
indistinguishable from read-only recon:
- **H-encrypt:** native Content is encrypted-at-rest (transparently decrypted on read);
  our overlay wrote DECRYPTED/plaintext content, so the at-rest bytes don't match the signed
  `.smd`/`<GUID>` hash table -> Verify re-streams everything. (Test B = only 2 swapped files
  mismatched on a native base -> 1.8 MB, fits.)
- **H-ledger:** GS trusts only blocks IT streamed (StreamingSummaries=0 for us); Verify
  ignores on-disk bytes and re-acquires. (User saw the Verify bar RESET after a mid-verify
  overlay -> Verify decides up-front, doesn't read our bytes.)

Both predict: **not fixable by copying files** unless we can reproduce GS's at-rest form
(needs its per-install key) OR make GS itself lay the bytes down.

## Decisive next step (costs one ~1.6 GB Verify)
1. (DONE) overlay baseline: `gs-state\overlay\gamingservices.reg` + metadata sizes above.
2. Run Verify & Repair to COMPLETION on the current overlay Clone Drone.
3. Capture `gs-state\verified\gamingservices.reg` + re-list `F:\Games\Clone Drone...\` metadata
   sizes/hashes.
4. DIFF. The delta = exactly what "GS-trusted" looks like. If it's reproducible state we can
   write -> a fix exists. If the `<GUID>`/`.smd`/Content bytes themselves change (re-encrypted
   by GS) -> not fixable by overlay; product answer = "launch-only, expect full re-download
   on Verify/update."

## Fallback fix architecture (if not patchable)
Make the RECEIVER install through GS's own pipeline so GS produces correct at-rest content +
ledger, while our app only accelerates the bulk byte transfer (LAN-seed the blocks GS asks
for). Needs R&D on whether GS can be fed local block sources.
