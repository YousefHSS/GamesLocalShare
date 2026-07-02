# GamesLocalShare

A cross-platform desktop app for sharing and syncing game installs across your devices and external drives. Built with Avalonia and a React-based WebUI.

## What's New in 3.0.0

The Xbox release. Highlights since 2.0.0:

- **Xbox PC Game Pass (MSIXVC) transfer** *(Windows only)* — move installed Game Pass / Microsoft Store games between your PCs over the LAN without re-downloading them from Microsoft. Pre-staged files are overlaid onto an app-initiated paused install; when you Resume, Gaming Services sees the bytes are already present and skips the download (measured ~8 MB of network traffic to place a 7.7 GB title).
- **Single-copy storage** — keep just one copy of an Xbox title on disk. Instead of storing a second full encrypted package, the app captures a compact (~17 MB) *skeleton* and rebuilds the byte-identical `.msixvc` on demand from the skeleton + the installed files — a ~99% space saving with no re-download. Decryption/encryption are performed on the fly by **xvdtool** using your own device keys.
- **LAN cache proxy** — a local proxy that intercepts this PC's Xbox download requests and serves cached/peer content on your network, so repeat downloads across your machines stay off the internet.
- **CIK integration** — Content Instance Keys are supplied by **CikExtractor**, which derives your device key locally so xvdtool can decrypt *your own licensed content*. The app never implements the cipher itself.
- **Prompt-free elevated startup** — "Start with Windows" now registers a Task Scheduler logon task with highest privileges, so the app starts already elevated and the Xbox flows run without a UAC prompt every session. The installer registers this task directly; if elevation is declined it falls back to a normal (non-elevated) startup entry.
- **Friendlier UAC** — on-demand elevation now relaunches the app's own signed-name executable instead of `powershell.exe`, so UAC prompts show the app's name and icon.
- **Desktop notifications** — actionable notifications for transfer progress, Xbox copy results, and setup steps that need your attention.

### Previously, in 2.0.0

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
- **Xbox / Game Pass transfer** *(Windows)* — transfer installed MSIXVC titles between PCs without re-downloading, plus single-copy skeleton storage and a LAN download cache. See [Xbox / Game Pass (MSIXVC) transfer](#xbox--game-pass-msixvc-transfer).
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

### Xbox / Game Pass (MSIXVC) transfer

*Windows only. Xbox PC Game Pass is a Windows-exclusive platform.*

Installed Xbox / Microsoft Store (MSIXVC) games are encrypted per-device, so they can't just be copied like a Steam folder. GamesLocalShare works within that model to let you move a title between your own PCs — and keep a single copy on disk — without re-downloading gigabytes from Microsoft:

1. **Transfer** — the receiver starts a paused install from the Xbox app; GamesLocalShare overlays the pre-staged files onto it. On Resume, Gaming Services validates the bytes are already present and skips the download.
2. **Single-copy storage** — rather than keeping a second full encrypted package, the app stores a compact skeleton and reconstructs the byte-identical package on demand from the skeleton plus your installed files.

Both of these need the encrypted package to be read and rebuilt, which is done **on the fly, on your own machine, against your own licensed content**, using two external tools:

- **[xvdtool](https://github.com/emoose/xvdtool)** by emoose — the XVD/XVC container tool that GamesLocalShare bundles and shells out to for on-the-fly decrypt, skeleton capture, and byte-exact reconstruction of the package.
- **[CikExtractor](https://github.com/LukeFZ/CikExtractor)** by LukeFZ — dumps the packed Content Instance Keys (CIK) from the local registry and derives your device key, producing the `.cik` files xvdtool selects by GUID. You point GamesLocalShare at it in Settings; it is run only when a key is needed, elevated, and its keys never leave your machine.

GamesLocalShare itself implements no cryptography — it orchestrates these tools to decrypt and re-encrypt content you already own. Both require administrator rights (hence the elevated startup task described above).

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
- **Xbox MSIXVC decrypt/reconstruct:** [xvdtool](https://github.com/emoose/xvdtool) (emoose), with keys from [CikExtractor](https://github.com/LukeFZ/CikExtractor) (LukeFZ)
- **Tests:** xUnit + FluentAssertions (C#), Vitest + React Testing Library (TS)
- **Target:** .NET 8.0

## Contributing

PRs welcome. Run `dotnet test` and `npm test` (in `WebUI/`) before submitting.

## License

MIT — see [LICENSE](LICENSE).

## Acknowledgments

The Xbox / Game Pass transfer and single-copy features are built on two excellent
open-source tools. GamesLocalShare uses them to decrypt and rebuild content you
already own; huge thanks to their authors:

- **[xvdtool](https://github.com/emoose/xvdtool)** by [emoose](https://github.com/emoose) — XVD/XVC container tool, bundled and used for on-the-fly MSIXVC decrypt, skeleton capture, and byte-exact reconstruction.
- **[CikExtractor](https://github.com/LukeFZ/CikExtractor)** by [LukeFZ](https://github.com/LukeFZ) — used to dump packed CIK data and derive the device key so xvdtool can decrypt your licensed content.

Also thanks to:

- [Avalonia UI](https://avaloniaui.net/)
- [Gameloop.Vdf](https://github.com/shravan2x/Gameloop.Vdf)
