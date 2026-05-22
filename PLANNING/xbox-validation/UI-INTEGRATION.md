# Xbox MSIXVC Transfer - UI Integration Plan

> Status: Phases 1-5 implemented 2026-05-22 (pending manual end-to-end test
> on real hardware). Connects the proven transfer (see
> `MSIXVC-TRANSFER-SOLVED.md`) to the GamesLocalShare app UI.

## Progress

- [x] Phase 1 - Script hosting (`Services/XboxScriptHost.cs`, csproj Content).
- [x] Phase 2 - Elevation gate (`MainViewModel` hard guards + modal gate).
- [x] Phase 3 - C# services rewritten as script wrappers
      (`XboxSenderService`, `XboxTransferService`).
- [x] Phase 4 - Modal / WebUI (`XboxTransferModal.tsx`, `store.ts`).
- [x] Phase 5 - Unit tests (`XboxTransferServiceTests`, sender tests trimmed).
- [ ] Manual end-to-end test on a real MSIXVC title.

## Approach

Wrap the three proven PowerShell scripts. The C# services become thin
process-launchers that stream script stdout into the app log and parse the
JSON the scripts already emit. **No edits to the `.ps1` scripts** - the only
interactive prompt (`Read-Host` in the receiver) is cleared by writing a
newline to redirected stdin.

Scope: drive / USB staging path only. LAN peer-to-peer streaming is deferred
to a later phase (the existing `XboxNetworkSender/Receiver` streams raw
`Content\` files and would hit the same encrypted-EXE failure - it needs the
package-context rescue first).

## Critical integration facts (from the scripts)

1. `Assert-Elevated` (`_common.ps1`) re-launches via UAC and the original
   process exits. If the app launches a script **unelevated**, the real work
   detaches into a process whose stdout cannot be captured. **The app must
   already be elevated** so `Assert-Elevated` is a no-op and the script runs
   in-process with capturable stdio.
2. `Invoke-AsSystem` redirects the PsExec/SYSTEM child to a log file and tails
   it to its own stdout - capturing the script's stdout yields the full
   SYSTEM-phase output in one stream.
3. The receiver's only `Read-Host` ("Press Enter when paused") is in the user
   phase, before the SYSTEM launch - cleared with a single newline on stdin.
4. Scripts write `runs/` and `tools/` next to `$PSScriptRoot`. Under Program
   Files that fails - the app must run them from a writable copy.
5. After overlay the receiver prints `NOW: click Resume` and observes the NIC
   for `ObserveSeconds`. Resume is a UI display concern, not a stdin handshake.

## Exit codes / outputs

Sender (`xbox-transfer-sender.ps1`):
- Output: `<Destination>\<GameName>\transfer-summary.json`
- Exit 0 = ok; 10 = integrity check failed; >=8 = robocopy failure.
- Summary fields used: `GameName`, `PackageFamilyName`, `SourceBytes`,
  `SourceFileCount`, `FilesCopied`, `BytesCopied`, `IntegrityOk`,
  `ReceiverProvidedFiles`, `StagedProtectedFiles`, `MissingFiles`,
  `MismatchFiles`, `RobocopyExit`.

Receiver (`xbox-transfer-receiver-overlay.ps1`):
- Output: `runs\receiver-overlay-verdict-<stamp>.json` (path echoed on stdout).
- Exit 0 = ran (verdict may still be H3); 2 = destination empty (wrong
  drive / plain MSIX); 11 = staged copy incomplete; 12 = receiver-provided
  EXEs not downloaded yet.
- `Hypothesis` -> `XboxTransferVerdict`: `H1_FULL_SKIP`->FullSkip,
  `H2_DELTA`->DeltaOnly, `H3_FULL_REDOWNLOAD`->FullRedownload,
  `STILL_PAUSED_OR_FAILED`->StillPaused, `PARTIAL_PROGRESS`->Pending,
  `INCONCLUSIVE`->Error.

## Phases

### Phase 1 - Script hosting
- `PLANNING/xbox-validation/auto/*.ps1` stays the source of truth; csproj adds
  them as `<Content>` copied to output as `xbox-scripts/`.
- New `Services/XboxScriptHost.cs`: on first use copies the 3 scripts to
  `%LOCALAPPDATA%\GamesLocalShare\xbox-transfer\` (writable; `runs/`, `tools/`
  created there). Exposes script paths + a `RunAsync` helper that launches
  `powershell.exe`, streams stdout/stderr line-by-line, and optionally feeds
  stdin. PsExec keeps auto-downloading via `Ensure-PsExec`.

### Phase 2 - Elevation gate
- Sender staging and receiver overlay both require the app elevated. Reuse
  `isElevated` / `RequestElevation`. Add the gate to the sender modal flow
  (the receiver flow already has it).

### Phase 3 - Rewrite C# services as wrappers
- `XboxSenderService.StageToFolderAsync` -> launch the sender script; stream
  stdout to `LogMessage`; parse progress; on exit read `transfer-summary.json`
  and map to state + verdict.
- `XboxTransferService.RunOverlayAsync` -> launch the receiver script; write a
  newline to stdin; stream stdout; detect `NOW: click Resume` to flip the UI;
  parse `t+Ns rx=` for live MB; on exit read the verdict JSON. Handle exits
  2/11/12 with specific guidance.
- Drop the superseded naive robocopy/icacls/NIC code; keep `ValidateSource`
  and `FindDestinationCandidates`.
- `XboxNetworkSender/Receiver` and network commands stay compiled but the UI
  disables them.

### Phase 4 - Modal / WebUI
- `XboxTransferModal.tsx`: sender elevation gate; package-context rescue +
  integrity result surfacing; receiver "Click Resume NOW" callout; Xbox
  install-drive override; receiver exit-12 actionable guidance; network
  buttons disabled ("Coming soon").
- `store.ts` / `bridge.ts`: any new state fields.

### Phase 5 - Tests & docs
- Unit tests: `transfer-summary.json` parsing, exit-code mapping, verdict
  mapping.
- Manual end-to-end on a real MSIXVC title.
- Keep this doc updated.

## Risks

- App must be elevated or script stdout is lost (Phase 2 handles it).
- PsExec / package-context copy / SYSTEM robocopy may trip antivirus.
- The user still physically clicks Install / Pause / Resume in the Xbox app -
  inherent, not automatable.
- PsExec auto-download needs internet on first run (may pre-bundle later).
