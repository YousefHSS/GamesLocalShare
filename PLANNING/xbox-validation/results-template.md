# Xbox PC Pre-Staged Content — Validation Results

Fill one section per test title. Copy numbers from `runs\recognition-*.json`
and the deploy/stage summary JSON files.

## Title 1: `<Game name here>`

- **Package family name**: `<from baseline.json>`
- **Signature kind**: `Store` / `Developer` / ...
- **Layout**: `XboxGames\<Game>\Content` (or `WindowsApps\...`)
- **Baseline total size**: `<bytes>` (`<GB>` GB) across `<N>` files
- **PC A**: `<hostname>` — MS account `<account 1>`
- **PC B**: `<hostname>` — MS account `<account 2>`
- **NIC alias measured on PC B**: `<Wi-Fi / Ethernet>`

### Per-step measurements

| Step               | Bytes received | Disk Δ | Time to Play | Playable? | Notes |
|--------------------|---------------:|-------:|-------------:|:---------:|-------|
| 1. Passive open    |                |        |              |           |       |
| 2. Repair / Verify |                |        |              |           |       |
| 3. Add-AppxPackage |                |        |              |           |       |
| 4. Install fallback|                |        |              |           |       |

### Secondary checks

- **Sign out → offline launch**: works / fails with license error / fails other → `<observation>`
- **Different drive letters (stage E:, deploy F:)**: pass / fail → `<observation>`
- **Re-stage over partial install**: pass / fail → `<observation>`
- **Non-elevated detection** (`Get-AppxPackage` + `XboxGames` walk): works without admin? `<yes / no>`

### Verdict

Pick one:

- [ ] **H1**: pre-staged content fully recognised; downloads < 5% of baseline. Ship Phase 2 as designed.
- [ ] **H2**: works after Repair / Register; downloads < 30% of baseline. Ship Phase 2 + documented post-transfer step.
- [ ] **H3**: Xbox app forces full re-download. Scope Xbox transfer to modifiable-apps only; ship detection for everything else.

**Notes / next actions**:

> `<free-form summary, anything weird, follow-up questions>`

---

## Title 2 (optional, modifiable-apps category): `<Game name here>`

(Duplicate the section above.)
