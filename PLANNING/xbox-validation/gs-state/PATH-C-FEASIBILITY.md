# Path C (LAN download caching) — feasibility verdict

**Date:** 2026-06-02  **Goal:** make the 2nd PC get an Xbox game over LAN (not internet)
so GS installs it natively (trusted, delta-updatable), avoiding the at-rest-encryption
problem entirely. No DRM/key manipulation.

## Finding 1 — Free Delivery Optimization peer-to-peer: NOT supported for Xbox games
Microsoft Learn "Types of download content supported by Delivery Optimization"
(waas-delivery-optimization) content table:
| Content | HTTP | Peer-to-Peer | Connected Cache |
| --- | --- | --- | --- |
| Win10/11 UWP Store apps | yes | YES | yes |
| Win32 Store apps | yes | no | no |
| **Xbox Game Pass (PC)** | yes | **NO** | **yes** |
=> Xbox/Gaming-Services game payloads do NOT peer over LAN, by design. Confirms the prior
empirical note that Xbox downloads bypassed Delivery Optimization. The free, two-home-PCs
option does not exist for games.

## Finding 2 — Microsoft Connected Cache (MCC): works for Xbox content, but enterprise-gated
MCC for Enterprise and Education prerequisites (mcc-ent-prerequisites), exact wording:
- "Valid Azure subscription" REQUIRED (pay-as-you-go ok; "the Connected Cache Azure resource
  does not incur any Azure cost", but it is an Azure-managed control plane; credit-card acct).
- "Your organization must have one of the following license subscriptions for EACH Windows
  Desktop device that downloads content from a Connected Cache node: Windows Enterprise E3 or
  E5 ... / Windows Education A3 or A5 / Windows Enterprise per device."
  => This machine is **Windows 11 Pro** -> does NOT qualify.
- Host needs Win11 (>=22631.3296) or Win Server 2022+, **nested virtualization**, **WSL2**,
  **Hyper-V mgmt tools**, **Azure IoT Edge container**, IP Helper svc, port 80/443, 4GB free
  RAM, 100GB free disk. PowerShell 5.1 for deploy.
- Clients pointed at the node via `DOCacheHost` (Intune, DHCP option, or registry key).
- Content types listed for MCC ent-edu overview: Windows updates, M365/Office, Intune/store
  apps, Defender. Xbox game payload caching is asserted by the DO table (Game Pass PC = MCC
  yes) but NOT restated in the MCC content list; non-GamePass purchased-game caching is
  UNCONFIRMED.

## Verdict
For consumer Windows (Pro/Home) there is NO clean, supported way to serve Xbox game
downloads from a local source:
- DO P2P excludes games.
- MCC requires Windows Enterprise/Education licensing (we have Pro) + Azure + server-grade
  infra, and may not even cache purchased game payloads.
Pursuing MCC on Win 11 Pro would be a licensing-compliance gray area (the user explicitly
wants to stay legitimate) and a heavy, uncertain setup. Not recommended as a product path.

## OPTION 2 DEEP-DIVE: self-hosted DOCacheHost cache (no Azure) — research verdict 2026-06-02
Goal: run a plain HTTP caching reverse-proxy on PC1, point PC2's Delivery Optimization at it
via DOCacheHost, so GS downloads the game over LAN (correct ciphertext -> trusted+updatable),
no DRM, no Azure.

What the docs SUPPORT (plausible):
- DOCacheHost is a client-side policy at HKLM\SOFTWARE\Policies\Microsoft\Windows\
  DeliveryOptimization, supported since Win10 1809. Reference lists NO edition (Pro/Ent)
  restriction on the SETTING itself. It's a static "use this cache host IP/FQDN" override;
  docs: "clients use the static cache host list."
- Cache-host transport baseline is plain HTTP on PORT 80 (the HTTPS doc's connectivity check
  tests port 80; HTTPS is an OPT-IN add-on via generateCsr). So a homemade HTTP cache on :80
  is protocol-plausible; no mandatory mutual-TLS / Azure node cert for HTTP.
- Games are cache-eligible: DO content table lists "Xbox Game Pass (PC)" with MCC = YES
  (P2P = NO). MCC == the DOCacheHost path.
- Precedent: DOINC (Delivery Optimization In-Network Cache) was a self-hosted cache on a
  ConfigMgr distribution point (pre-Azure-MCC), i.e. non-Azure caches have existed.
- Foreground tuning: a game install is a FOREGROUND download; must set
  DODelayCacheServerFallbackForeground (secs) or DO falls back to CDN immediately.

UNRESOLVED RISKS (only a test settles these):
1. Does the DO client actually route XBOX GAME content requests to DOCacheHost in practice?
   (Our earlier empirical note: Forza's download showed under "Gaming Services" and bypassed
   DO P2P. P2P != cache-host path, but it's a warning sign. MUST verify games hit the cache.)
2. Will a NON-registered homemade cache be ACCEPTED, or does the DO client validate the cache
   node against the DO cloud service before using it? (DOCacheHost is a static override, so it
   MAY connect directly to IP:80 with no cloud validation — but unconfirmed.)
3. Engineering: need a DO-compatible caching reverse-proxy (DO rewrites CDN URLs with a
   specific origin/host format). Generic nginx/Squid needs the right config; no turnkey
   consumer tool found in search.

LEGITIMACY NOTE: MCC prereqs say Enterprise E3/E5 is required "for each Windows Desktop device
that downloads content from a Connected Cache node." A homemade cache is either "not MCC"
(unsupported hack) or "MCC-like" (licensing nominally applies). Gray area to weigh given the
user's stay-legitimate stance.

CHEAP DECISIVE PROBE (before building anything): stand up a simple HTTP reverse-proxy on :80
that LOGS requests + forwards to CDN on PC1; set DOCacheHost=PC1-IP and
DODelayCacheServerFallbackForeground=high on a TEST PC; install a small game; watch the log.
- DO sends game-content GETs to our host => path works, non-Azure host contacted => worth
  building a real cache.
- DO never contacts our host for game content => games bypass DOCacheHost => Option 2 DEAD,
  Family 1 exhausted.

## OPTION 2 PROBE RESULT (2026-06-03) -- DEAD: games are NOT Delivery Optimization jobs
Ran the cheap decisive probe on ONE PC (DESKTOP-FHVD1S8): proxy on :80, DOCacheHost=192.168.1.244,
DODelayCacheServerFallback*=70, DoSvc restarted. Started an Xbox game install. Q1 = "is the game
even a DO job?" answered FIRST: Task Manager showed "Gaming Services (2)" actively downloading at
13.9 Mbps (real network pull), but `Get-DeliveryOptimizationStatus` returned NOTHING game-sized.
=> Xbox GAME payloads do not pass through Delivery Optimization / DoSvc at all; Gaming Services
uses its OWN downloader. DOCacheHost only redirects DO traffic, so it can never intercept game
content. The disconnect-internet step (step 4) was unnecessary -- Q1 already settled it.
This empirically confirms Finding 1 (DO content table: Xbox Game Pass PC P2P=No) and the earlier
"Forza bypassed DO" note. OPTION 2 IS DEAD. Family 1 (LAN caching to obtain a native trusted
install) is EXHAUSTED on consumer Windows: DO-P2P excluded, DO-cache-host bypassed, MCC enterprise-gated.

## Where that leaves us (realistic options)
1. ACCEPT the overlay app as launch-only: it gives a playable install and saves the FIRST
   download; Verify/updates re-download (at-rest ciphertext can't be reproduced by copy).
   Best for single-player titles finished before an update. Document as a known limitation.
2. Best-effort MCC home-lab EXPERIMENT (only if user accepts the Pro-licensing gray area +
   Azure account + setup): stand up MCC on one PC, set DOCacheHost on the other via registry,
   download once on PC1 to fill cache, install on PC2 from cache. Verify whether GS game
   payloads actually cache. High effort, uncertain payoff, compliance caveat.
3. Stop; the question "why did Xbox re-download" is fully answered (at-rest encryption);
   ship the overlay app honestly labeled.
