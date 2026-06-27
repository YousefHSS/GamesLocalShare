# Xbox PC (MSIXVC / Game Pass) Transfer — Master Findings

**Goal of the whole effort:** move an Xbox PC game between PCs (or reinstall it) **without
re-downloading the multi-GB payload from Microsoft's CDN**, while keeping the install
"trusted" so the Store still treats it as legitimate (Verify passes, future updates stay
small deltas). **Constraint held throughout: stay legitimate** — only the *byte transport* is
shortcut; licensing is never touched, and the receiving account must legitimately own / have
Game Pass for the title.

**Status (2026-06-19): SOLVED, with a hard floor.** The legitimate, working solution is the
**LAN cache** (serve Microsoft's own encrypted bytes from a local source). Every "shortcut"
that avoids obtaining the genuine bytes (file-copy overlay, reconstruction, metadata/ledger
forging) is a **dead end** — proven, not assumed. The fundamental floor: **a game's genuine
bytes must come from Microsoft exactly once, and only those exact bytes are ever trusted.**

Confidence tags: **[PROVEN]** = directly measured; **[STRONG]** = multiple consistent signals;
**[INFERRED]** = best explanation, not isolated by a control test.

---

## 1. The system under test

- **Game:** Clone Drone in the Danger Zone (small → cheap to iterate).
  - Package: `DoborogGames.CloneDroneintheDangerZone_1.9.2.0_x64__w6hf08ggk4es4`
  - Content GUID: `368B2C2C-C6E2-4472-ACDA-52A5F18A1D51`
  - `.msixvc` size: **1,779,732,480 bytes** (~1.7 GB)
- **Also referenced:** Forza Horizon 6 (`Microsoft.ForteBaseGame`, ~146 GB) — the original
  "transferred game re-downloaded everything on update" case.

### On-disk anatomy of an installed Xbox PC game  [PROVEN]
Lives at `<drive>\Games\<FriendlyName>\` (the `WindowsApps\<pkg>` path is a **symlink/junction**
to it):

| Item | What it is |
|---|---|
| `Content\…` data files | **PLAINTEXT** on disk (raw-volume read == filtered read) |
| `Content\*.exe`, `*.dll` (the 2 EXEs) | **ENCRYPTED at rest** (clipsp-protected; ACCESS-DENIED to normal read, high-entropy raw) |
| `<GUID>` blob (~13.64 MB) | XVC **header + Merkle hash-tree region** (real on-disk bytes) |
| `<GUID>.smd` | signed manifest |
| `<GUID>.xvi` | **block-residency bitmap** ("which blocks are present") |
| `<GUID>.xvs` | license / signature envelope — **device + license bound** |
| `<GUID>.<sub>.xsp` | per-install instance GUID |

- **Genuine boundary:** first **14,303,232 bytes (13.6 MiB)** = genuine header + hash tree.
  Header layout: 512-byte **RSA-4096 signature** at offset 0; `msft-xvd` magic at 0x200;
  content-id GUID at 0x220.
- **Download transport:** the whole game is **ONE `.msixvc` object over plain HTTP (port 80)**,
  pulled via ~90+ Range GETs, keyed by content-GUID+version+package (identical on every machine
  for a version → cacheable & cross-machine reusable). Example host family: `assets1/assets2/
  xvcf1/xvcf2.xboxlive.com` (Akamai). Control-plane (auth/store/license/manifest) is separate on 443.

---

## 2. The methods we tried, and their verdicts

| Method | What it does | Verdict |
|---|---|---|
| **LAN cache** (serve genuine `.msixvc` from local) | hosts-redirect content host → local proxy serving the byte-identical encrypted blob | **WORKS — the fix.** Install, Verify, updates all pass. GS does its *own* trusted download, just LAN-sourced. [PROVEN] |
| **Overlay / transfer** (robocopy files + transplant `.xvi/.xvs/.xsp`) | hand-place a copied install + flip "all blocks present" | **Launch-only.** Installs & launches (~0 download), but **Verify/Update → full re-download.** [PROVEN] |
| **Reconstruction** (xvdtool) | rebuild a trusted `.msixvc` from the install | **DEAD.** xvdtool has no packer; the "reconstruction that passed" was **byte-identical to genuine** (debunked, §5). [PROVEN] |
| **Header-graft** (genuine header + self-built body) | transplant genuine signed header onto own content | **REJECTED.** GS spot-hashes content samples vs the HTTPS-fetched manifest, mismatches, falls back to real CDN. [PROVEN] |
| **Path B** (genuine header + zeroed/fake content) | overlay real content *after* a fast fake install | **FALSIFIED.** Aborts exactly at the 13.6 MB genuine boundary. [PROVEN] |
| **Move / Change-drive** (Xbox app) | relocate an overlaid install | Re-downloads on Verify (raw-copies bytes; doesn't re-establish trust). [PROVEN] |
| **`Add-AppxPackage <file>.msixvc`** | install package directly from a local file | Fails `0x80073CF6` — can't read the encrypted streaming container; only GS can stream a `.msixvc`. [PROVEN] |
| **Delivery Optimization / `DOCacheHost`** | LAN-cache via DO | **DEAD.** Xbox game payloads bypass DO entirely (GS uses its own downloader). [PROVEN] |
| **Microsoft Connected Cache (MCC)** | official LAN cache for Xbox content | Enterprise/Education-licensed + Azure + server infra. This box is **Win 11 Pro → does not qualify.** Gray area; not pursued. [STRONG] |
| **Makepkg (GDK) from loose files** | build `.msixvc` from the GDK layout | Produces a **dev/test-key** package → installs only in **Developer Mode**, never retail-trusted. [STRONG] |

---

## 3. Architecture — who does what, and where (module-level, xperf-proven)

Measured 2026-06-19 with xperf CPU-sampled stackwalks during a genuine streaming install
(0→1.7 GB) and a Verify, plus ProcMon and raw-volume reads.

```
  CDN  ──or── LAN cache proxy           (game payload = plain HTTP byte-ranges)
   │
 USER MODE
   GamingServices.exe (SYSTEM)
     • winhttp / webio  → fetch ranges
     • checks RSA-4096 HEADER SIGNATURE        ◄ cheap EARLY gate
     • writes payload via MEMORY-MAPPED I/O
     • does NO content crypto   (0 SHA, no bulk AES — only ProcessPrng/AES-RNG + 1 ECC)
   camsvc (svchost)  → wintrust!DigestFile → SymCryptSha256   = AUTHENTICODE on the EXEs only
                       (the ONLY SHA-256 in the system; a RED HERRING for content)
   clipsp / ClipSVC  → license; decrypts the protected EXEs for processes w/ package identity
 ──────────────────────────────────────────────────────────────────────────────
 KERNEL MODE
   xvdd.sys   *** THE CONTENT ENGINE ***
     • mounts the XVD; all container writes pass through it
     • SELF-CONTAINED crypto — 0 of 6192 xvdd stack-events touch Windows CNG
       (bcrypt/symcrypt/cng/ksecdd). That is WHY no SymCryptSha256 ever shows for content.
     • per-page on read: AES-decrypt + SHA-256 vs the RSA-SIGNED Merkle tree
     • NO public PDB (frames stay raw addresses) → can name the module, not its functions
   System (PID 4): cache-manager flush of GS's mapped writes (why bulk writes show as "System")
```

**Key module-level facts** [PROVEN unless noted]:
- During a 1.7 GB streaming install: `xvdd.sys` = the dominant content-path component
  (~6,000 CPU samples); GamingServices spends CPU on **network + memory-mapped writes**, not crypto.
- The only sustained `SymCryptSha256` (user-mode SymCrypt, SHA-NI) is **camsvc doing
  Authenticode** on the game's PE files — full stack:
  `CapabilityAccessManager → wintrust!WTGetSignatureInfo → CryptSIPVerifyIndirectData →
  SIPObjectPE_::DigestFile → BCryptHashData → SymCryptSha256`. Unrelated to XVC content.
- `xvdd.sys` performs its crypto **internally**, never via the shared Windows crypto stack.
- Content-integrity verification is therefore **kernel-enforced** by `xvdd` against the
  Microsoft-signed manifest/tree. [STRONG — module identified; exact xvdd functions not named
  due to missing symbols]

---

## 4. The trust model — why a copy isn't trusted, in plain terms

**Analogy:** the game files are a *ticket*; the Xbox app checks your ticket against its **own
guest list**, and your name only lands on that list when **Gaming Services itself** ran the
download. A perfect photocopy of the ticket isn't on the list → you're turned away (re-download).

**What the trust gate is NOT** (each disproven by placing byte-perfect copies and still
re-downloading) [PROVEN]:
- **Not the registry** — `StreamingSummaries/Checkpoints/Tracking` were identical (and "complete")
  between a trusted install and an overlay; both empty/idle when not downloading.
- **Not the on-disk metadata** — overlaying **byte-perfect RAW** `.smd/.xvi/.xvs` made no difference.
- **Not the `.xvi` "all present" bitmap** — it gets you *launchable*, not *trusted*.

**What it IS** [STRONG / INFERRED]: a verified-block trust state that is **external to the game
folder and/or kernel-enforced via the signed manifest**, and is produced **only by GS's own
download pipeline**. Its exact storage was never located (registry and folder both excluded),
so it is GS-internal and/or held by the kernel driver.

**Block-granular vs wholesale** — the decisive contrast [PROVEN]:
- **Test B:** genuine CDN install, then corrupt **only the 2 EXEs** → Verify repairs **~1.8 MB**
  (just those blocks). A trusted baseline exists, so repair is surgical.
- **Test A:** **overlay** install → Verify = **"Repairing 47.5 MB of 1.6 GB"** = full re-download.
  No trusted baseline exists for *any* block → everything is distrusted.

**Why overlay content fails even though it's genuine plaintext** [INFERRED, best explanation]:
the overlay robocopies the **filter's projected view** of the files, not the **at-rest XVD block
layout** the signed manifest commits to. GS's own download writes content in exactly that layout;
a file-copy can't reproduce it → kernel re-hash mismatches → re-download.

**Verify ≈ Update gate** [STRONG]: both ask "do I have a GS-trusted baseline for these blocks?"
The overlay never earns one, so both Verify and the update-delta engine re-acquire wholesale.
(This is the mechanism behind Forza's 146 GB update re-download.)

---

## 5. The reconstruction debunk (2026-06-19)

A memory note claimed an **xvdtool-reconstructed `final.msixvc` (non-genuine bytes) passed
launch + Verify** — implying the trust gate accepts reconstructions. **This is FALSE.** [PROVEN]

- `final.msixvc` is **byte-for-byte identical to the genuine package**: same SHA256
  (`0C4E52212D55BE9ED9763AC7896866FF22270EED3BC37BC0E4E2179FE5543638`), `cmp` = 0 differing bytes.
- It was produced by xvdtool's `-pdu` (decrypt→re-encrypt preserving data units), which **by
  design reproduces the original bytes** — a no-op on the genuine package, not a reconstruction.
- It passed Verify because **it is the genuine package.**

**Why single-copy reconstruction (drop the package, rebuild from the install) is off the table:**

**The real wall is ENCRYPTION** [PROVEN, 2026-06-19 via `xvdtool -i` on the genuine blob]. The
header flags are `Encrypted` + `Data integrity enabled (uses hash tree)`; content is keyed by
**CIK `1ff1b973-31bf-9449-8ba9-de1454f64985`**, wrapped by the **StandardODK**. So the signed
hash tree commits to the **encrypted ciphertext**, not the plaintext files. (The installed files
are plaintext only because `xvdd` decrypts on projection; the package body is AES-XTS ciphertext.)
- To rebuild a body that matches the tree you must reproduce the genuine **ciphertext** → re-encrypt
  the plaintext with the genuine CIK → **you need Microsoft's content key = the DRM line.**
- **No best-effort exists:** AES-XTS output is pseudorandom; without the key you can't produce even
  ONE correct ciphertext block. A "90% reconstruction" matches **0 blocks**. It's all-or-nothing.
- This **supersedes the "perfect NTFS image" framing** — that layout problem was downstream; even a
  byte-perfect plaintext image would still need CIK-encryption to match the tree. Encryption is the floor.

Secondary walls (moot given the above, but confirmed):
1. **xvdtool has no XVC writer** — only edits an existing container (`-xri` inject — "hashes will
   no longer match"; `-tph` transplant header — "layout must match"; `-rs` resign — uses a **test**
   key `rsa3_key.bin`, not Microsoft's, so it fails the enforced genuine-header check).
2. **GDK `makepkg`** can build from loose files but signs with a **dev/test key** → Developer-Mode
   only, never retail-trusted.

→ Reconstruction bottoms out at the same place as every other shortcut: **needing Microsoft's
content key (CIK/ODK).** Only the genuine ciphertext bytes are ever trusted.

---

## 6. Reverse-engineering assessment (the "can I patch user-mode?" question)

Framing: stay above the kernel/DRM line — patch only user-mode `GamingServices.exe` so an overlay
install becomes Verify-trusted.

**The decisive fork (RE must answer this first):** on Verify, is the *decision* ("which blocks to
re-download") made in **user-mode GS**, or does GS **relay a verdict the kernel computed**?
- User-mode decides → a patchable decision point exists.
- Kernel relays → no user-mode patch helps; the driver re-checks on-disk content regardless.

**Evidence leans kernel-relay** [STRONG]: GS does no content hashing (xperf); overlay Verify
re-downloads despite "complete" registry flags and genuine plaintext content; the integrity work
is in `xvdd`. Also note: **"zeroed content with the correct hash" is impossible** — a hash is a
function of the actual bytes, so no ledger entry makes fake content pass a real re-hash.

**Concrete investigation path (legit, on your own box), if pursued:**
1. `Get-AppxPackage Microsoft.GamingServices` → `InstallLocation`; targets are
   `GamingServices.exe`, `GamingServicesNet.exe`, and the streaming/installer DLLs.
2. Static (IDA/Ghidra): find the verify/repair path; look for **`DeviceIoControl`** (IOCTLs into
   the driver) vs in-process `BCryptHashData` (user-mode comparator).
3. Dynamic (Frida / WinDbg user-mode): hook `DeviceIoControl` + `bcrypt!*` during an overlay
   Verify. If one IOCTL returns a "bad blocks" list → verdict is the driver's (dead end above the
   line). If GS computes the comparison itself → that's the decision point.

**Honest prediction:** GS is a **relay**; the real gate is the kernel re-hashing at-rest content
vs the signed manifest, and the deeper reason overlay fails is **at-rest content form**, not a
forgeable ledger. The only user-mode "patch" that changes the outcome is making GS lie and request
nothing — which is defeating the integrity check (circumvention line), and still wouldn't fix the
update delta. **Not solvable above the line.**

---

## 7. What actually works (the realistic options)

1. **LAN cache (the fix).** Serve Microsoft's byte-identical encrypted `.msixvc` from a local
   proxy. GS does its own trusted download, LAN-sourced → install/Verify/updates all pass.
   - First acquisition of a version is paid **once** (someone downloads it to fill the cache);
     every reinstall / Verify / additional PC / update after that is local, **0 internet**.
   - Cost: keeping the `.msixvc` package. To avoid double storage, **keep only the package** and
     **install-on-demand from cache** (instant, local), uninstalling when done = single 1.7 GB copy.
2. **Overlay = launch-only.** Saves the first download and gives a playable install; Verify and
   updates will re-download. Fine for single-player titles you finish before an update.

**The floor (unavoidable):** the genuine bytes come from Microsoft once; only those exact bytes
are trusted; everything legitimate is a variation on "obtain the genuine bytes locally once, then
serve/keep them."

---

## 8. Tooling produced (in `PLANNING/xbox-validation/`)

- `lan-cache/xbox-cache-proxy.ps1` — compiled-C# HttpListener cache; HIT serves byte-Range from
  disk, MISS forwards/auto-fills. `-NoOrigin` strict mode. (Design lessons: must be compiled C#,
  high `DefaultConnectionLimit`, filter non-origin hosts, single-writer background fill.)
- `lan-cache/xbox-cache-hosts.ps1` — add/remove the content-host → proxy redirect (marker block,
  flushes DNS). **Remove when the proxy isn't running or all Xbox downloads break.**
- `lan-cache/prime-blob.ps1` — pre-fill a game's `.msixvc` into cache (`-LocalFile`/`-Force` to
  seed any local file at a CDN path).
- `auto/xbox-transfer-sender.ps1` / `…-receiver-overlay.ps1` — the overlay transfer (launch-only);
  sender rescues the 2 encrypted EXEs via **package context** (`Copy-ProtectedFilesViaPackage` →
  `Invoke-CommandInDesktopPackage` → clipsp decrypts for the package identity).
- `snapshot-trust-state.ps1` — dump GS registry + streaming-metadata hashes + ACLs; diff before/after.
- `who-verifies/capture-stacks.ps1` (xperf), `capture-procmon.ps1` — module-level attribution.
- `reconstruct/` — `probe-rawread*.ps1` (raw `\\.\vol` reads), `apply-overlay.ps1`,
  `pathb-zero-content.ps1`, `probe-tamper.ps1`, `test-meta-write.ps1`.
- `cdn-probe/` — HTTP host/URL sniffers that captured the content URL.

---

## 9. Dead ends — do NOT re-litigate

- Forging the trust (registry, on-disk metadata, `.xvi`) — disproven with byte-perfect copies.
- Reconstruction / header-graft / from-install repack — needs genuine signed header + exact
  original image; the one "success" was the genuine bytes.
- Move/Change-drive — raw-copies, doesn't re-establish trust.
- `Add-AppxPackage` of a local `.msixvc` — can't stream the encrypted container.
- Delivery Optimization / `DOCacheHost` — Xbox games bypass DO.
- User-mode patch to bless an overlay — gate is kernel-enforced content re-hash; not solvable
  above the DRM line (and "zeroed content + correct hash" is a contradiction).

---

## 10. Process notes (lessons that cost time)

- **Xbox/Gaming Services downloads BYPASS Delivery Optimization** → `Get-DeliveryOptimization*`
  massively under-reports. Trust NIC counters / Task Manager / Settings→Data usage instead.
- **Hash-match ≠ no-download** for *installs/transfers*: end-state byte-identity proves "same
  version," not "wasn't downloaded." (It *does* hold for the cache path, which serves the
  identical CIPHERTEXT blob.)
- Verify against an **independent signal** before concluding; earlier sessions repeatedly fooled
  themselves with disk-only or DO counters, and wrote memory with fabricated numbers — quote raw
  measured values.
- The repo owner edits files between prompts — append/scan, don't clobber.

*Compiled 2026-06-19 from the session investigation + the memory notes
(`xbox-who-verifies-content`, `xbox-pathb-fake-content-falsified`, `xbox-header-signature-enforced`,
`xbox-reconstructed-msixvc-passes` [debunked], `xbox-transfer-clonedrone-repro`,
`xbox-transfer-content-byte-perfect`) and the planning docs (`UPDATE-TRUST-INVESTIGATION.md`,
`gs-state/PATH-C-FEASIBILITY.md`, `XVDTool/Sample/REPACK-WITH-MAKEPKG.md`).*
