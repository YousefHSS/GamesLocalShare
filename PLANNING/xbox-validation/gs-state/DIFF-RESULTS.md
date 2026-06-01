# Verify trust diff: overlay (fails) vs verified (trusted) — ROOT CAUSE

**Date:** 2026-06-02  **Game:** Clone Drone (368B2C2C, F:, vol {98053395})
**Method:** capture-state.ps1 before (Tag=overlay) and after a full Verify & Repair
(Tag=verified, user confirmed it re-pulled the whole 1.6 GB, did NOT launch after).
Snapshots: `gs-state\overlay\baseline.txt`, `gs-state\verified\baseline.txt`.

## Diff

### Registry — UNCHANGED by a successful Verify (kills the "ledger flag" theory)
- `PackageRepository\Metadata\{vol}#{368B2C2C}` = same (InitialInstallTime, UsingAppLicensing).
- `Store\ContentId` = same.
- `StreamingSummaries` still `{vol}#{368B2C2C} = 0` (a new verify-session GUID {CB485997}
  appeared, value also 0).
- `StreamingCheckpoints / StreamingTracking / StreamingRequests` = STILL EMPTY after verify.
=> A trusted install is INDISTINGUISHABLE from our overlay in the registry. Not patchable there.

### Metadata files (size + SHA256)
| file | overlay -> verified | changed? |
| --- | --- | --- |
| `<GUID>` (14,303,232) | 20FAB5D9... -> 20FAB5D9... | NO |
| `.smd` (35,078, signed manifest) | 6D12D1F4... -> 6D12D1F4... | NO |
| `.xct` (4,096) | C5408C01... -> C5408C01... | NO |
| `.xvi` (4,096, residency) | 83A0D704 -> FFBB24A6 | yes, TIMESTAMP only* |
| `.xvs` (10,118 -> 10,044) | EDDE8FB2 -> 3FE5C457 | yes (streaming state) |
| `.xsp` (7,904) | 44F0AF85 -> 3EB45782 | yes |
*`.xvi` diff is only an embedded FILETIME (2115 58b0 24f1dc01 -> f4b8 9631 11f2dc01) plus one
byte; the "all blocks present" residency table (03 00 00 01 repeated) and the path
`\??\F:\Games\Clone Drone in the Danger Zone\Content` are byte-identical before and after.

### Content sample (readable? size + SHA256)
| file | overlay | verified |
| --- | --- | --- |
| `Clone Drone in the Danger Zone.exe` | readable PLAINTEXT (F06D3A71) | **ACCESS-DENIED / protected** |
| `UnityCrashHandler64.exe` | readable PLAINTEXT (788DBB1E) | **ACCESS-DENIED / protected** |
| gamelaunchhelper.exe | 5636E55E | 5636E55E (same) |
| UnityPlayer.dll | 94C7A4D7 | 94C7A4D7 (same) |
| MicrosoftGame.Config | 4618F498 | 4618F498 (same) |
| Assembly-CSharp.dll | 0FE2DDF0 | 0FE2DDF0 (same) |
| globalgamemanagers | 33A707D4 | 33A707D4 (same) |
| level0 | 1506949D | 1506949D (same) |

## ROOT CAUSE (now evidence-backed)
The 1.6 GB re-download changed nothing visible EXCEPT the 2 EXEs flipping
plaintext -> encrypted/protected at rest. Reconcile with Test B (native base + only 2
plaintext EXEs => 1.8 MB granular repair) vs Test A (overlay => full 1.6 GB):

=> **Native MSIXVC content is encrypted AT REST. The gaming filter driver decrypts on read**
(so Get-FileHash shows identical hashes and the game launches), **but Verify validates the
at-rest encrypted bytes against the signed `.smd` manifest.** Our overlay writes DECRYPTED
plaintext at rest (sender's robocopy reads through the filter), so every block fails Verify
-> full re-download. The 2 EXEs are the only files where this is visible (the filter refuses
to serve them decrypted at all).

The trust signal is NOT: registry, a flag, the ledger, the .smd, the <GUID> hash table, or
the .xvi residency (all identical / present). It IS the at-rest ciphertext of the payload.

## KEY MODEL CONFIRMED (web research 2026-06-02)
MSIXVC/XVC content = AES-128-XTS encrypted at rest with a Content Instance Key (CIK) stored
OUTSIDE the package, delivered via the per-device LICENSE, held in the XVDD (Xbox Virtual
Disk Driver) keyslot table. AES-XTS is deterministic (key + data + position -> ciphertext).
Because the `.smd` signed manifest is identical for all users and Verify validates on-disk
CIPHERTEXT against it, the at-rest ciphertext MUST be the same on every machine for a given
version => **content-keyed, NOT per-install** (only the CIK-unwrap is per-device).
Refs: xvdtool (emoose), Xbox One Research Wiki (xbox-virtual-drive), XvddKeyslotUtil.

=> The encrypted bytes ARE transferable in principle. Two fix paths:

### Path C (RECOMMENDED, supported, no DRM concerns)
Make GS download over the LAN via Delivery Optimization / Microsoft Connected Cache so the
2nd PC pulls the package from the 1st (or a local cache) instead of the internet. GS lays
the bytes down itself -> correct encryption, fully trusted, delta-updatable. The download
still happens but at LAN speed. Touches no keys/ciphertext. NEXT: verify whether DO/MCC can
cache Xbox-app/Gaming-Services content on Windows (prior note said Xbox bypassed DO -- but
MCC for Enterprise/Education is documented to cache Store/Xbox content; re-verify config).

### Path B (viable but hard + DRM-adjacent; NOT recommended for a bandwidth tool)
Transfer the RAW at-rest ciphertext: read below the XVDD filter (fsutil queryextents + raw
\\.\F: reads) on the sender, write it back on the receiver without the filter re-processing
(replay what GS streaming-install does). Theoretically sound (content-keyed), but deep,
fragile across updates, and edges into content-protection circumvention (xvdtool /
XvddKeyslotUtil are DRM tools). Avoid unless Path C proves impossible.
