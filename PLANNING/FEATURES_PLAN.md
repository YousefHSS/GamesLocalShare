# GamesLocalShare - Features Plan

Purpose
- A living planning document to collect, prioritize and track feature requests for the GamesLocalShare project.
- Use this file to add the features you want; each feature entry should include description, acceptance criteria and priority.

How to use
- Edit this file and add your requested features under "Requested Features".
- For each requested feature, we include suggested implementation steps and files that will likely require changes.

Requested Features (add yours below)

1) Improve Steam Library Scanning
- Description: Make Steam library scanning more robust (multiple libraryfolders, VDF parsing, exclude small files, progress updates).
- Acceptance criteria: Detect all installed games across library folders and report counts; UI shows progress and errors.
- Files likely affected: Services/SteamLibraryScanner.cs, ViewModels/MainViewModel.cs, Views/MainWindow.xaml
- Implementation steps:
  1. Parse libraryfolders.vdf using Gameloop.Vdf or similar.
  2. Enumerate steamapps/common and appmanifest files.
  3. Report progress via IProgress or events.
- Priority: Medium
- Est. effort: 1-2 days

2) Harden Network Discovery (mDNS/SSDP)
- Description: Use mDNS or SSDP to discover peers reliably across LAN (UDP broadcast fallback).
- Acceptance criteria: Peers list updates quickly; discovery works on multi-subnet setups when possible.
- Files likely affected: Services/NetworkDiscoveryService.cs, Models/NetworkPeer.cs, ViewModels/MainViewModel.cs
- Implementation steps:
  1. Add mDNS responder/client or use UDP multicast.
  2. Add retry/backoff and service verification (TCP port check).
- Priority: High
- Est. effort: 2-3 days

3) Encrypted & Authenticated Transfers (TLS)
- Description: Add optional TLS wrapping for file transfer channel and basic peer verification.
- Acceptance criteria: Transfers can be negotiated with TLS; fallback option remains plaintext.
- Files likely affected: Services/FileTransferService.cs, Models/TransferState.cs
- Implementation steps:
  1. Add TLS stream negotiation using SslStream.
  2. Provide optional certs or use ephemeral self-signed certs with user confirmation.
- Priority: Low
- Est. effort: 2-4 days

4) Resume, Integrity Checks & Faster Hashing
- Description: Improve resume reliability and add SHA-256 verification for completed files with faster partial-hash strategy.
- Acceptance criteria: Resumed downloads match remote file hashes; corrupted files are detected and re-downloaded.
- Files likely affected: Services/FileTransferService.cs, Models/TransferState.cs
- Implementation steps:
  1. Save per-file checksum (partial + final) in transfer state.
  2. Use concurrent hashing where appropriate.
- Priority: High
- Est. effort: 2-3 days

5) Better Firewall Handling & Installer Integration
- Description: Improve firewall setup experience; optionally include installer that requests admin once for full setup.
- Acceptance criteria: Users can automatically add rules or are guided step-by-step; rules are tested after configuration.
- Files likely affected: Services/FirewallHelper.cs, App.xaml.cs
- Implementation steps:
  1. Validate rules after creation.
  2. Provide instructions and fallback manual steps.
- Priority: Medium
- Est. effort: 1-2 days

6) UI/UX Improvements
- Description: Improve status messages, icon loading reliability, and accessibility (keyboard navigation, high contrast).
- Acceptance criteria: Smooth icon loading, clear status messages, accessible controls.
- Files likely affected: Views/MainWindow.xaml, Views/MainWindow.xaml.cs, Converters/ValueConverters.cs
- Implementation steps:
  1. Centralize icon loader with fallbacks.
  2. Add keyboard shortcuts and accessibility names.
- Priority: Medium
- Est. effort: 1-2 days

7) Settings Page & Persistence
- Description: Add a settings dialog to allow users to change ports, buffer sizes, library paths, and toggle high-speed mode persistently.
- Acceptance criteria: Settings persist between runs; validated input.
- Files likely affected: ViewModels/MainViewModel.cs, Views/SettingsWindow.xaml (new), App.xaml.cs
- Implementation steps:
  1. Add Settings model and persistence (JSON config in AppData).
  2. Create UI and bind to settings.
- Priority: High
- Est. effort: 1-2 days

8) Logging & Diagnostics
- Description: Structured logging with levels (Info/Warning/Error) and option to export logs for troubleshooting.
- Acceptance criteria: Logs can be filtered and exported; errors captured for common failure modes.
- Files likely affected: Models/LogMessage.cs, Services/*, ViewModels/MainViewModel.cs
- Implementation steps:
  1. Add structured logger (e.g., Microsoft.Extensions.Logging or lightweight wrapper).
  2. Hook events to UI.
- Priority: Medium
- Est. effort: 1-2 days

9) Unit & Integration Tests
- Description: Add tests for library scanning, manifest generation, and transfer state logic.
- Acceptance criteria: CI runs tests; critical logic has unit coverage.
- Files likely affected: Create Tests project (GamesLocalShare.Tests)
- Implementation steps:
  1. Add test project and basic tests.
- Priority: Low
- Est. effort: 2-3 days

10) Packaging & Auto-update
- Description: Provide installer (MSIX or Inno Setup) and auto-update checks.
- Acceptance criteria: Users can install and upgrade; updates checked safely.
- Files likely affected: Build/Packaging scripts, App update logic.
- Priority: Low
- Est. effort: 3-5 days

Custom Features
- Add your features below. For each feature, provide:
  - Short title
  - Description
  - Why it matters
  - Acceptance criteria
  - Priority (Low/Medium/High)

Example:
- Title: "Delta updates for games"
- Description: "Only download changed files between builds to reduce bandwidth."
- Why: "Huge bandwidth savings for small updates."
- Acceptance criteria: "Remote manifest diffing works and only changed files are transmitted."
- Priority: High

Implementation workflow suggestions
1. Add feature entry to this file.
2. I can implement a small, testable PR for each high-priority feature.
3. After implementation, run manual tests on LAN or add automated integration tests where possible.

Next steps for me
- Tell me which features from this list (or custom ones you add) you want implemented first.
- I can then create a scoped plan and start implementing changes in the codebase.


---
Generated by automated planning helper. Edit this file to add or rearrange features.