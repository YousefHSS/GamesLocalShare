# Why a transferred Xbox game does a FULL re-download on its next update — Investigation Handoff

**Date:** 2026-05-31
**Branch:** Xbox-store-support
**Subject:** Forza Horizon 6, transferred by our app, re-downloaded the ENTIRE ~146 GB game when a small (~4 GB) update arrived. Goal of this investigation: understand *why the Xbox app didn't trust the transferred files* and fell back to a full re-acquire, so we can mitigate it.

---

## TL;DR for the next agent

- **Confirmed fact:** the game really did re-download the whole thing. Windows Settings → Data usage (last 24h) shows **Gaming Services = 159.3 GB**. The user also watched it download ~13 h overnight at ~3.1 MB/s (≈146 GB). Not a disk reshuffle — real CDN traffic.
- **Confirmed fact:** our app's byte copy is **faithful** (content is fine). The problem is NOT corruption.
- **Best current root-cause hypothesis (strong, not yet definitively proven):** Gaming Services re-acquired because it had **no trusted streaming checkpoint / verified baseline** for the hand-placed package. On this machine, `GamingServices\StreamingCheckpoints` and `StreamingTracking` are **completely empty**. Without a trusted "I downloaded & block-verified version X" record, the Store cannot compute a delta and falls back to full re-acquire. This would happen **even with a 100% byte-perfect copy** — the missing thing is the *trust record*, not the bytes.
- **Immediate next steps:** (1) read the existing docs in this folder (`MSIXVC-TRANSFER-SOLVED.md`, `HANDOFF.md`, `SENDER_HANDOFF.md`) — they predate this session and may already contain relevant conclusions; (2) compare a *cleanly Store-installed* game's `StreamingSummaries`/`StreamingCheckpoints` values against Forza's to learn the value semantics and confirm transferred installs uniquely lack a checkpoint; (3) read the production receiver flow (`auto/xbox-transfer-receiver-overlay.ps1` and the C# `XboxReceiverService`/`XboxSenderService`).

---

## The system under test

- Game: **Forza Horizon 6**
- Package identity: **`Microsoft.ForteBaseGame`**, Publisher `CN=Microsoft Corporation…`
  - Version **3.360.259.0** (pre-update, what was transferred) → **3.364.933.0** (post-update)
- Content/instance GUID: **`AB03F40C-E85D-467B-8C67-3870B89BD2D1`** (this is also the name of the ~1 GB GUID blob file at the package root, and the key under Gaming Services)
- StoreId `9NR1R1XWLCNB`, TitleId `7bf69384`
- Live install tree: **`F:\Games\Forza Horizon 6\`** (`Content\` has 56 entries)
  - Registered install root **`F:\WindowsApps\Microsoft.ForteBaseGame_3.364.933.0_x64__8wekyb3d8bbwe`** is a **reparse point/junction** (its own `\Content` probed empty; the real data is under `F:\Games\…`).
- Pre-update backup of the transferred copy: **`G:\stage\Forza Horizon 6\`** (146.42 GB, 14,542 files) — captured by the user BEFORE the Store update. This is gold; do not delete.
- Note: `Get-AppxPackage *Forza*` returns **nothing** — these MSIXVC games are tracked by **Gaming Services**, not the normal AppX repository. Use the GamingServices registry, not Get-AppxPackage.

---

## What was VERIFIED (trust these)

1. **Full re-download happened.** Data usage: Gaming Services **159.3 GB / 24h**. Xbox UI mid-update: "Updating **20.2 GB of 146.4 GB**", 3.1 MB/s, Task Manager showed Gaming Services pulling ~29.6 Mbps. ~13 h overnight × ~3.1 MB/s ≈ 146 GB.
2. **Content copy is faithful.** SHA256 diff of backup (A=`G:\stage`) vs post-update install (B=`F:\Games`), both 146.42 GB:
   - **97.83% byte-identical.** Only 3.18 GB / 1,217 files differ (mostly the regenerated streaming container + the genuine update's media/exe).
   - ⚠️ **This 97.83% is a RED HERRING for the trust question.** Re-downloading the *same version* naturally reproduces identical bytes. The hash match proves "same version content," NOT "wasn't downloaded." Do not cite it as evidence the copy was trusted.
3. **Gaming Services registry state for Forza (post-update):**
   - `PackageRepository\Root\{98053395-…}#{AB03F40C-…}` → registered, points at the WindowsApps junction. (`{98053395-E234-4CF1-BF1E-3D8FBAF84537}` is the F: volume GUID; other drives have their own volume GUIDs.)
   - `PackageRepository\Metadata\{…AB03F40C}` → `InitialInstallTime`, `LastActivationTime`, `LastSessionTimeMs=11947` (~12 s), `LastUpdatedTime` present. So it WAS registered and launched.
   - **`StreamingCheckpoints` → EMPTY (no subkeys).**
   - **`StreamingTracking` → EMPTY.**
   - **`PackageBackups` → EMPTY.**
   - `StreamingSummaries\{AE2E2DC7-9693-4332-B993-FA5F6A56882F}` → Forza is the ONLY listed package; value `{…AB03F40C}=0`.
     - ⚠️ **Ambiguous:** `=0` might mean "0 blocks verified-present" (supports the no-baseline theory) OR "0 bytes remaining = complete" written AFTER the re-download. Unconfirmed. Don't over-claim it.

---

## How our transfer works (from the scripts — confirmed by reading them)

Source: `PLANNING/xbox-validation/auto/xbox-transfer-sender.ps1` and `…-receiver.ps1` (and `…-receiver-overlay.ps1`, not yet read this session).

- **Copy method:** `robocopy /E /COPYALL /B /J /DCOPY:DAT` (backup mode — copies data + ACLs). Runs as **NT AUTHORITY\SYSTEM** via PsExec to read ACL-protected files.
- **PFN discovery:** sniffed from the source folder's **SYSAPPID conditional ACE** (`Get-SysAppIdFromAcl`).
- **The critical hybrid behavior:** MSIXVC **encrypts only executables** (`.exe`/`.dll`); data files are plaintext on disk. The sender **cannot read the encrypted executables**, so it **excludes them** from the stage and records them in `transfer-summary.json` as `ReceiverProvidedFiles`. The **receiver's own Gaming Services downloads those executables** during an Install/repair.
  - (There's also a "rescue" path: copy protected exes from inside the game's package context via `Copy-ProtectedFilesViaPackage`. When it works, the stage is complete; when not, the receiver downloads them.)
- **Net effect:** the final install is a **hybrid** — data files robocopied from the source machine + executables fetched from CDN on the destination + a hand-registration. It was **never downloaded/verified as one coherent unit** by Gaming Services, so no single block-map verification pass ever covered the whole package.

---

## Root-cause analysis (ranked, with confidence)

**Unifying theory (HIGH confidence on mechanism, MEDIUM on it being the *sole* trigger):**
Gaming Services trusts only its **own** install pipeline to reach a "known-good, block-verified, fully-installed" **streaming checkpoint**. A hand-placed + re-registered package has enough metadata to **launch**, but no trusted checkpoint. When an update arrives, the delta engine needs a trusted "I have exactly version X, block-verified" baseline; with none, it re-acquires the whole package (and writes its own checkpoint as it goes). **Independent of byte-fidelity.**

Contributing hypotheses (not individually proven):
- **H1 — Missing/invalid streaming checkpoint** for the manually-placed package. *Strongest.* Directly supported by empty `StreamingCheckpoints`/`StreamingTracking`.
- **H2 — Hybrid assembly never matched one coherent block-map** (data copied + exes re-downloaded separately). Strong aggravator; possibly sufficient on its own.
- **H3 — Instance-bound streaming metadata mismatch** (`.xvi/.xvs/.xct/.smd` + the `AB03F40C` GUID blob copied from the *source* machine don't reconcile with the destination's fresh Gaming Services records). These were exactly the files the Store regenerated (in the diff they showed up as "changed").

---

## What is NOT yet done / open questions

- [ ] **Read the pre-existing docs in this folder** — they may already solve part of this:
  - `PLANNING/xbox-validation/MSIXVC-TRANSFER-SOLVED.md`
  - `PLANNING/xbox-validation/HANDOFF.md`
  - `PLANNING/xbox-validation/SENDER_HANDOFF.md`
  - `PLANNING/xbox-validation/UI-INTEGRATION.md`
- [ ] **Read the production receiver flow:** `auto/xbox-transfer-receiver-overlay.ps1`. Also the C# services — `Glob **/*.cs` returned NOTHING this session even though git history has `XboxSenderService.cs`/`XboxReceiverService.cs`; figure out where the C# lives (maybe excluded/relocated). `XboxReceiverService.cs` was referenced at `F:\Documents\GamesLocalShare\Services\XboxReceiverService.cs` but Read said it doesn't exist — verify.
- [ ] **Confirm checkpoint semantics:** dump `StreamingCheckpoints`/`StreamingSummaries` for a **cleanly Store-installed** game (e.g. one of the other registered titles: No Man's Sky, Subnautica 2, Silksong, Expedition 33) and compare. If clean installs have a populated checkpoint and Forza doesn't, H1 is confirmed and the `=0` meaning is resolved.
- [ ] **Control experiment:** clean Store install of a small Xbox game → let it update (expect small delta). Then transfer the same game with our app → let it update (expect full re-acquire). Confirms transfer is the trigger and measures the penalty cleanly.
- [ ] **Mitigation R&D** (see below).

---

## Mitigation direction (for when cause is confirmed)

The fix must make the receiver reach a **Gaming-Services-trusted installed state**, NOT just a launchable one. Target the **streaming-checkpoint / registration layer** — the byte copy is already sound. Ideas to investigate:
- Let Gaming Services perform a real **Install/repair/verify** over the pre-placed files so it writes its *own* checkpoint, instead of hand-registering a robocopied tree. (Does GS have a "verify local files" path like Steam? If a repair reuses on-disk blocks, the transfer still saves the download and the next update stays a delta.)
- Investigate the undocumented Gaming Services IPC used by the Store's built-in "move to another drive" (which DOES preserve delta updates) and whether an "import existing install" handshake can be driven.
- Worst case, document the limitation in the transfer/resume UI: "the first Store update after transfer may re-download the full game."

---

## Tooling produced this session (reusable, in this folder)

- **`hash-package.ps1`** — `-Root <dir> -Out <manifest.tsv>`; streams SHA256 per file → TSV (`relpath<TAB>sha256<TAB>size<TAB>mtime_utc`). ~20 min for 146 GB on F:, ~66 min on G:.
- **`diff-manifests.ps1`** — `-A a.tsv -B b.tsv [-Csv report.csv]`; classifies identical / identical_rewritten / changed / added / removed, and buckets CHANGED by intent (content_chunk / streaming_container / binary / metadata).
- Artifacts present: `manifest-A.tsv` (G:\stage backup), `manifest-B.tsv` (F:\Games post-update), `report.csv`.

### ⚠️ PowerShell 5.1 gotchas (these wasted hours — don't repeat)
- An `if` expression used **directly as a hashtable-literal value** yields `$null`. Compute into a plain variable first.
- `Dictionary.TryGetValue([ref]$x)` was **unreliable** in the compare loop. Use plain `@{}` hashtables (case-insensitive string keys — good for NTFS paths) with `ContainsKey` + indexer; store values as a small array `@(hash,size,mtime,displayPath)`.
- **Always validate a diff with a known-answer probe AND a counts-sum check BEFORE believing/printing numbers.** The diff script silently produced "0% / all identical" three times before it was correct.

---

## Process warning / things I got WRONG this session (so you can distrust stale claims)

The investigation was messy. Corrections, in order:
1. First claimed the Store **stages side-by-side then atomic-swaps** (true for normal MSIX/UWP, **false** for these MSIXVC Xbox games — they update in place). 
2. Then claimed it was a **local in-place re-layout, NOT a download** (~10 GB), based on disk signature + Delivery Optimization counters. **Wrong.** Xbox/Gaming Services downloads **bypass Delivery Optimization**, so `Get-DeliveryOptimizationPerfSnap*` massively under-reports them. The user's 13 h / internet-speed observation + the 159.3 GB Data-usage screenshot are the truth.
3. Conflated "end-state hashes identical" with "nothing downloaded." A fresh download of the same version is also byte-identical.
4. **Wrote memory files with fabricated/incorrect numbers TWICE before the diff actually worked.** The single source of truth now is the memory note `xbox-transfer-content-byte-perfect.md` (corrected) and this file.

**Lesson for the next agent:** verify every measurement against an independent signal (network meter, known-answer probe) before drawing conclusions. The disk alone could not distinguish "downloaded + overwritten in place" from "reorganized in place."

---

## Pointers

- Memory note (corrected, authoritative): `…/.claude/projects/F--Documents-GamesLocalShare/memory/xbox-transfer-content-byte-perfect.md` and `xbox-transfer-resume.md`.
- Key registry root: `HKLM:\SOFTWARE\Microsoft\GamingServices\` — children of interest: `PackageRepository\{Root,Metadata,Package}`, `StreamingCheckpoints`, `StreamingTracking`, `StreamingSummaries`, `PackageBackups`, `PackageRegistration`.
- All registered Xbox games on this PC (useful as clean-install controls): No Man's Sky, Slay the Spire, Minecraft (Java/UWP), Subnautica 2, Undertale, Hollow Knight Silksong, Goat Simulator 3, Sifu/ProjectRuntime, Stardew Valley, Loop Hero, Danganronpa V3, Expedition 33.

---

# SESSION 2 FINDINGS (2026-05-31) — checkpoint theory REFUTED, trust anchor re-identified

This session did the empirical work the section above flagged as "not yet done."
Net result: **the leading hypothesis (H1, "Forza uniquely lacks a streaming
checkpoint") is WRONG**, and the real trust anchor is the on-disk MSIXVC
streaming metadata, not a registry checkpoint.

## 1. The streaming-checkpoint theory is refuted (HIGH confidence)

Live dump of `HKLM:\SOFTWARE\Microsoft\GamingServices`:

- `StreamingCheckpoints` → **EMPTY**
- `StreamingTracking` → **EMPTY**
- `PackageBackups` → **EMPTY**
- `StreamingServices`, `StreamingRequests` → **EMPTY**

This is true for **ALL ~16 registered packages on the machine**, not just Forza.
So an empty checkpoint cannot be what singled Forza out — every clean Store
install also has none. These keys are **transient working areas** used only
during an *active* download/streaming op and cleared on completion; they are
NOT a persistent per-game "trust ledger."

`StreamingSummaries` contains exactly **one** entry — Forza
(`{…AB03F40C}=0`) — because Forza is the most-recently-streamed title. The `=0`
is residual "0 bytes remaining = complete" state from its re-download, not a
baseline other games keep. So H1 / H3 as written are dead ends; don't chase them.

`PackageRepository\Metadata` shows Forza registered **normally** (correct version,
`UsingAppLicensing=1`, install/activation/update times) — structurally identical
to the other transferred title (Silksong) and to clean installs. No "trust" flag
distinguishes it.

## 2. The real trust anchor: on-disk MSIXVC streaming metadata (.xvi/.xvs/.xsp)

The production receiver (`auto/xbox-transfer-receiver-overlay.ps1`, lines ~508-561)
already encodes the mechanism: the **`.xvi` file is the block-presence map**.
Overlaying the sender's `.xvi` makes Gaming Services treat *all blocks as already
downloaded* and finalize the install instead of pulling from CDN. The script
copies `.xvi/.xct/.xvs/.smd/.xsp` when the `.xvi` size (=version) matches, and
excludes them on a size mismatch (the earlier "13 GB re-download" was a version
mismatch). So the trust signal lives in these files, NOT the registry.

These files are **instance- and license-bound**. Disk comparison of the
pre-update transferred copy (`G:\stage`) vs the current post-update install
(`F:\Games`) proves Gaming Services **regenerated** them:

| File | G:\stage (transferred, pre-update) | F:\Games (post-update) |
|---|---|---|
| `…AB03F40C.xsp` | secondary GUID **`118796C1-…`** (sender's instance), 36368 B | secondary GUID **`59D87DA4-…`** (receiver-generated), 34096 B |
| `…AB03F40C.xvs` (license/signature envelope) | **34374 B** | **37202 B** (re-signed) |
| `…AB03F40C.xvi` (block map) | 16384 B | 16384 B |
| `…AB03F40C.smd` | 2270010 B | 2270010 B (same) |
| `AB03F40C` GUID blob | 1074126848 B | 1074122752 B |

The transferred install carried the **sender's** instance `.xsp` and license
`.xvs`; after the update GS rewrote both with the receiver's own identity. Caveat:
GS likely regenerates `.xsp`/`.xvs` on *any* version change, so this alone does
not *prove* the transfer caused the full re-acquire — but it confirms the
transferred metadata was foreign and was not retained.

## 3. Event-log corroboration

`Microsoft-Windows-AppXDeploymentServer/Operational` around the update:
- Event 855: "updateList: `…ForteBaseGame_3.360.259.0` is updating to `…3.364.933.0`."
- Event 603: deployment options `ForceApplicationShutdown, **ForceUpdateFromAnyVersion**, RetainFilesOnFailure`.
- The whole **AppX Add/registration took ~24 s** (3:36:31 → 3:36:55 PM) and just
  swapped `3.360.259.0` → `3.364.933.0` (old folder moved to `WindowsApps\Deleted\`).

So the ~146 GB was streamed by **Gaming Services BEFORE** this 24 s registration —
the AppX log does NOT capture the block download, and the GS text traces
(`…GamingServices…\LocalState\Logs\…`) are tiny UI traces that stop at 5/30 16:33
(the byte-level "why re-acquire" decision is in non-retained ETL).

## 4. Best current root-cause statement (revised)

Gaming Services finalized our install because the overlaid **`.xvi` falsely
asserts "all blocks present,"** and it launches fine. But the install was never
produced by GS's own verified streaming pipeline *for this device/license*: the
content blocks were robocopied and the `.xvi/.xvs/.xsp` were the **sender's**.
When the update arrived, MSIXVC differential update needs a GS-trusted baseline
of the currently-installed version's blocks for this license; with foreign
streaming metadata and a hand-asserted block map, GS could not trust a delta and
**re-acquired the full package**, regenerating `.xsp`/`.xvs` as it went.
Confidence: HIGH on the refutation + trust-anchor; MEDIUM-HIGH that the foreign
metadata is the trigger (needs the control experiment below).

## 5. The decisive experiment still owed

1. Clean Store-install a **small** Xbox MSIXVC game, let it take one update →
   expect a small delta (baseline that GS-native installs DO delta-update).
2. Transfer the same game with our app, let it update → expect full re-acquire.
   This isolates "transfer is the trigger" and rules out "FH6 updates are just big."

## 6. Mitigation — the core tension to solve

The overlay copies the sender's `.xvi` (works now, **poisons future updates**).
If we DON'T copy it, GS re-downloads immediately (script comment confirms GS won't
adopt overlaid blocks without the `.xvi`). So the fix is to find/drive a GS
**repair/verify pass** that re-hashes the on-disk blocks and writes GS-NATIVE
`.xvi/.xvs/.xsp` for the receiver's license — i.e. reach a GS-*verified* state,
not just a *launchable* one. That is the R&D target; the byte copy is already sound.

---

# SESSION 2b — first-launch is inert; generic Xbox bug is now the lead (2026-05-31)

## Launch A/B test (decisive)
Captured `runs/trust-snapshots/<ts>-pre-launch` and `<ts>-post-launch` around a
FULL real session of the re-downloaded clean install (game launched, a race
finished, closed, 20 s idle; `LastSessionTimeMs` 11,947 ms -> 893,459 ms ~= 14.9 min).
Tool: `snapshot-trust-state.ps1` (registry .reg + focused JSON + SHA256 of all
streaming files + ACLs + AppX/AppRepository state).

**Diff = activation bookkeeping ONLY:** `LastActivationTime`, `LastSessionTimeMs`,
a global `LastSequenceId` (+2). Everything trust-relevant is UNCHANGED:
- `.xvi/.xvs/.xsp/.xct/.smd` + GUID blob: **byte-identical SHA256** pre vs post.
- `StreamingCheckpoints/StreamingTracking/PackageBackups`: still EMPTY (no new keys).
- `StreamingSummaries`: Forza still `=0`, unchanged.
- Root + file ACLs: unchanged.

**Conclusion:** launching writes NO verification baseline. The transferred copy's
12 s launch was, for trust purposes, identical to a 15 min session. "Never
properly launched/committed" is REFUTED as a cause. We have now eliminated both
the streaming-checkpoint theory (S2.1) and the first-launch-commit theory.

## Revised lead: the generic Gaming Services failed-delta -> full-reacquire bug
User reports the full-redownload-on-update is a KNOWN Xbox app failure mode on
clean installs too (e.g. ARK: Survival Ascended / "Ark Raiders"): a small update
glitches/errors, the user hits Retry, and GS re-acquires the whole game. So the
146 GB was most likely this generic escalation, not a transfer-specific "untrusted"
verdict. The foreign `.xvs/.xsp` may raise the odds the FIRST delta hiccups, but
they are regenerated on any update and are not a permanent mark.

Implication for the product: we cannot fix a Microsoft reliability bug, but we can
(a) avoid making the first post-transfer delta more likely to fail, and (b) tell
the user the known workaround when a delta does glitch: do NOT click Retry (that
triggers full re-acquire); instead use pause/resume or the Store "move to another
drive" path, which preserve the delta.

## Open / next
- [ ] Research the generic bug + its community workarounds; map to our flow.
- [ ] Control experiment still owed (small game: clean-update delta vs transfer-update).

---

# SESSION 2c — generic-bug theory REFUTED; up-front re-acquire confirmed (2026-05-31)

**User confirms:** the original FH6 update showed **NO error and NO retry** — the
Xbox app silently presented the full 146 GB re-download AS the update ("Updating
20.2 GB of 146.4 GB"). So the generic ARK-style "failed delta -> Retry ->
re-acquire" escalation is REFUTED for this case. This was an **up-front** decision:
GS computed the update delta as ~100% of the package.

## Eliminations so far (all REFUTED)
1. Missing streaming checkpoint (S2.1) — every game has empty checkpoints.
2. First-launch commit (S2b) — launching is inert; writes no trust state.
3. Generic retry-escalation bug (S2c) — no error/retry occurred.

## Standing conclusion (best-supported, MEDIUM-HIGH)
GS's differential-update engine could not use our **assembled** install
(robocopied blocks + the sender's transplanted `.xvi/.xvs/.xsp`) as a trusted
delta baseline, so it re-acquired the whole package up front. The bytes are
perfect and the game launches (launch only needs decryptable content), but the
install was never *streamed + block-verified* by GS on this device/license.

### Sharp sub-hypothesis (testable, drives mitigation)
The overlay's `.xvi` falsely asserts "all blocks present," which makes GS **skip
its own block-verification at finalize** — so GS never records the per-block hash
baseline it later needs to compute a delta. Install works; update path has nothing
to reuse => full re-acquire. Fits "silent / up-front / full" exactly.

## The now-critical R&D question
Does Gaming Services (PC) expose a **verify/repair** operation that re-hashes
on-disk blocks and writes GS-native, device-bound `.xvi/.xvs/.xsp` (like Steam
"verify integrity of game files")? If yes, running it after a transfer should
convert our launchable-but-untrusted install into a GS-verified baseline, making
the next update a real delta. Investigate: Xbox app Manage/Repair UI; AppX
Add-with-repair (CDN-bound?); GamingServices COM/IPC; the "move to another drive"
flow (known to preserve deltas) and whether it can be driven for "import existing".

## Truth-check still required
Control experiment: clean-install a SMALL MSIXVC game, take one update (expect
delta, measure bytes), then transfer + update the same title (measure). Confirms
"transfer is the trigger" and rules out "FH6/this title just always full-updates."

---

# ============================================================
# NEXT-AGENT HANDOFF (READ THIS FIRST) — state as of 2026-05-31, late session
# ============================================================

You are picking up a root-cause investigation: *why a game transferred by our app
re-downloads in full on its next Store update.* Everything above is the evidence
trail. This block is the **current state + exactly how to continue.** Read the
SESSION 2 / 2b / 2c sections above for the proof behind each claim below.

## Where we landed (don't re-litigate these — they're settled by evidence)
- **Root cause is NOT a missing registry "streaming checkpoint."** `StreamingCheckpoints`,
  `StreamingTracking`, `PackageBackups` are EMPTY for ALL ~16 games on this PC. (S2.1)
- **Root cause is NOT a missing first-launch "commit."** A full real session of the
  clean install changed only activation bookkeeping; `.xvi/.xvs/.xsp/.xct/.smd`
  byte-identical, no new registry trust state. (S2b — see `runs/trust-snapshots/`)
- **It is NOT the generic ARK-style failed-delta→Retry→re-acquire bug.** User
  confirms FH6 showed NO error and NO retry: it **silently re-acquired the full
  146 GB as the update, up front.** (S2c)
- **Standing conclusion (MEDIUM-HIGH):** GS's differential-update engine could not
  use our **assembled** install (robocopied data blocks + the sender's transplanted
  `.xvi/.xvs/.xsp`) as a trusted delta baseline, so it re-acquired everything.
  The install launches fine (launch only needs decryptable content) but was never
  *streamed + block-verified by GS on this device/license*, so the update path has
  no verified per-block baseline to reuse.
- **Sharp sub-hypothesis (the thing to prove/disprove next):** the overlay's `.xvi`
  falsely asserts "all blocks present," so GS **skips block verification at finalize**
  and never records the hash baseline a future delta needs.

## LIVE EXPERIMENT IN PROGRESS (user is running this right now)
The user proposed and is testing this theory:
> Run a Gaming Services **"verify"** (the Xbox-app / GS content verify+repair,
> NOT `05-system-verify.ps1`, which is only a SYSTEM file-readability probe) on:
>  (a) an **assembled/overlaid** install → PREDICT: GS re-downloads the whole game.
>  (b) a **clean** GS-streamed install → PREDICT: GS verifies with ~no download.
> If true, this confirms the standing conclusion AND tells us "verify" is a
> diagnostic that EXPOSES the missing baseline, not a fix that rebuilds it.

- **Test game chosen:** `Clone Drone in the Danger Zone` — confirmed MSIXVC,
  small (GUID blob only 14 MB, so a full re-download is cheap to iterate).
  - Path: `F:\Games\Clone Drone in the Danger Zone`
  - Content GUID: `368B2C2C-C6E2-4472-ACDA-52A5F18A1D51`
  - `.xsp` secondary GUID: `CF92E7F7-F498-4CD6-9652-9619111E3B2F`
- When the user reports results, capture them and update this doc.

## Tooling (all in PLANNING/xbox-validation/)
- **`snapshot-trust-state.ps1 -Label <name> [-ContentGuid <g> -GameRoot <path>]`** —
  NEW this session. Dumps full GamingServices registry (.reg + focused JSON) +
  SHA256 of all streaming-metadata files + ACLs + AppX/AppRepository state into
  `runs/trust-snapshots/<ts>-<label>/`. Run before+after any operation and diff.
  Defaults target Forza; pass `-ContentGuid 368B2C2C-... -GameRoot "F:\Games\Clone Drone in the Danger Zone"` for the test game.
- **`measure-network.ps1`** — check whether it samples NIC-level bytes. CRITICAL:
  **Gaming Services downloads BYPASS Delivery Optimization**, so DO perf counters
  massively under-report. Trust only: NIC byte counters, Task Manager, or
  Windows Settings → Network → Data usage → Gaming Services.
- **`auto/xbox-transfer-sender.ps1` + `auto/xbox-transfer-receiver-overlay.ps1`** —
  the production transfer (SOLVED — see `MSIXVC-TRANSFER-SOLVED.md`). The overlay
  copies the sender's `.xvi/.xvs/.xct/.smd/.xsp` only when `.xvi` sizes match; the
  `.xvi` is what makes GS treat blocks as "downloaded" and finalize (lines ~508-561).
- `05-system-verify.ps1` is NOT relevant to the GS "verify" theory (it only proves
  SYSTEM can read package-guarded files).

## How to read the registry quickly
`HKLM:\SOFTWARE\Microsoft\GamingServices` — `PackageRepository\{Metadata,Root}`
hold per-package version/state (Forza content GUID `AB03F40C-...`; Silksong
`807C7D6A-...`). `Get-AppxPackage *Forza*` returns nothing — MSIXVC titles live in
GamingServices, not the AppX repo.

## Next steps (priority order)
1. **Record the user's verify-experiment result** (assembled vs clean). This likely
   settles the sub-hypothesis. Snapshot before/after + measure NIC bytes.
2. **Control / truth-check:** clean-install Clone Drone, take one update (measure →
   expect delta), then transfer it via our app and update (measure → full or delta?).
   Isolates "transfer is the trigger" vs "this title just always full-updates."
3. **Mitigation R&D:** find a GS operation that re-adopts on-disk files into a
   *verified* baseline. Leads: Store "Move to another drive" (reported to preserve
   deltas — does it rebuild native `.xvi/.xvs/.xsp`?); whether forcing a verify on
   an assembled install ever RESOLVES to no-download after one full re-acquire
   (i.e. re-acquire once → trusted thereafter). NOTE: web research was DOWN this
   session — re-attempt to confirm the "Move drive preserves updates" claim and
   whether the Xbox PC app exposes any local verify/repair.
4. If no GS "re-adopt" path exists, the honest fallback is to document the limitation
   in the transfer/resume UI: "the first Store update after transfer may re-download
   the full game," and advise users NOT to click Retry on a glitched update.

## Process rules for this repo
- The repo owner edits files between prompts. **Append/scan for their changes;
  do not clobber.** (global rule)
- Verify every measurement against an independent signal (NIC vs Data-usage) before
  drawing conclusions — earlier sessions repeatedly fooled themselves with disk-only
  or Delivery-Optimization signals.
