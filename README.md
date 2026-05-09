# GamesLocalShare

A cross-platform desktop app for sharing and syncing game installs across your devices and external drives. Built with Avalonia and a React-based WebUI.

## What's New in 2.0.0

A major release. Highlights since 1.3.0:

- **New WebUI** — React + Tailwind front-end embedded via WebView, replacing the legacy Avalonia panels. Faster, more flexible, easier to iterate on.
- **Epic Games support** — automatic detection of Epic Games Launcher installs alongside Steam.
- **Multi-drive / external library support** — register external drives or arbitrary folders as game libraries; compare what's on each drive vs what's installed locally.
- **Incremental transfer engine** — only changed files cross the wire. Files matched by size + mtime are skipped instantly; mtime-drift cases fall back to a sampled-MD5 quick-hash. Resumable across app restarts.
- **Smarter sync detection** — uses Steam's `BuildId` and `StateFlags` from `appmanifest_*.acf`, plus a `.gamesync_meta` sidecar this app writes after each copy. When a drive folder has no version info and Steam has a pending update, the app shows "Unknown version — choose direction" with manual override buttons instead of guessing.
- **Settings modal** — manage external libraries, sync preferences, and drive registrations from a single panel.
- **Cross-location compare** — visual side-by-side of every game across device + each registered drive, grouped by library, with status (InSync / DeviceToDrive / DriveToDevice / OnlyOnDevice / OnlyOnDrive / UnknownVersion).
- **Drive-as-Steam-library detection** — when Steam's `libraryfolders.vdf` registers an external drive as a Steam library, the comparator now correctly distinguishes the on-device install from the drive copy instead of pairing the drive entry with itself.
- **Test suite** — 159 unit tests (xUnit) covering matcher logic, manifest parsing, sync engine, transfer state, and WebUI components (Vitest).
- **Build/release** — PowerShell hook checks; GitHub Actions release workflow (tag `v*` to publish framework-dependent + self-contained ZIPs and an Inno Setup installer).

## Features

- **Steam library scanning** — detects installed Steam games via Steam's manifest files and `libraryfolders.vdf`.
- **Epic Games library scanning** — detects installed Epic Games Launcher titles.
- **External drive support** — register any folder or drive as an external library and compare against your local installs.
- **Network discovery** — find other GamesLocalShare instances on your LAN.
- **Peer-to-peer transfer** — pull games or updates from peers who have newer versions.
- **Incremental sync** — only copy what actually changed (size + mtime + sampled hash).
- **Resume support** — interrupted transfers pick up where they left off.
- **Cross-platform** — Windows, Linux, macOS.

## Installation

### Pre-built Releases

Download the latest release from the [Releases](https://github.com/YousefHSS/GamesLocalShare/releases) page. Three artifacts per release:

- **GamesLocalShare-Setup-X.Y.Z.exe** — Windows installer (recommended)
- **GamesLocalShare-win-x64-sc.zip** — self-contained ZIP, no .NET install needed (~60 MB)
- **GamesLocalShare-win-x64-fd.zip** — framework-dependent ZIP, requires .NET 8 Desktop Runtime (~15 MB)

### Build from Source

**Prerequisites:**
- .NET 8.0 SDK
- Node.js 18+ (for the WebUI bundle)

```bash
git clone https://github.com/YousefHSS/GamesLocalShare.git
cd GamesLocalShare

# Build (the csproj automatically runs `npm run build` for the WebUI)
dotnet build

# Run
dotnet run
```

### Docker

```bash
docker build -t gameslocalshare .
docker run -e DISPLAY=$DISPLAY -v /tmp/.X11-unix:/tmp/.X11-unix gameslocalshare
```

## Usage

1. **Scan Steam / Epic** — auto-detects installed games on your internal drives.
2. **Add external library** — point at a drive or folder you want to compare against.
3. **Compare locations** — see which games are in sync, which are newer on device vs drive, and which need a human decision.
4. **Copy** — transfer in either direction, with incremental sync skipping files that already match.
5. **Network mode** *(optional)* — start network discovery to share with peers on your LAN.

### Network Ports

GamesLocalShare uses the following ports for LAN sharing:
- **UDP 45677** — discovery
- **TCP 45678** — game list exchange
- **TCP 45679** — file transfers

Allow these through your firewall if you want LAN sharing.

## Platform Support

| Platform | Status |
|----------|--------|
| Windows 10/11 | Full support |
| Linux (X11) | Full support |
| Linux (Wayland) | May require XWayland |
| macOS | Full support |
| Docker | X11 forwarding required |

## Tech Stack

- **UI:** [Avalonia 11.3](https://avaloniaui.net/) + embedded WebView with React/Tailwind front-end
- **MVVM:** CommunityToolkit.Mvvm
- **WebUI:** React 18, Vite, Tailwind, Zustand
- **Steam manifest parsing:** [Gameloop.Vdf](https://github.com/shravan2x/Gameloop.Vdf)
- **Tests:** xUnit + FluentAssertions (C#), Vitest + React Testing Library (TS)
- **Target:** .NET 8.0

## Contributing

PRs welcome. Run `dotnet test` and `npm test` (in `WebUI/`) before submitting.

## License

MIT — see [LICENSE](LICENSE).

## Acknowledgments

- [Avalonia UI](https://avaloniaui.net/)
- [Gameloop.Vdf](https://github.com/shravan2x/Gameloop.Vdf)
