# GamesLocalShare

A cross-platform desktop application for sharing and syncing game files over your local network (LAN). Built with Avalonia UI for Windows, Linux, and macOS support.

## Features

- ?? **Steam Library Scanning** - Automatically detects installed Steam games
- ?? **Network Discovery** - Find other GamesLocalShare instances on your LAN
- ?? **Game Updates** - Download game updates from peers who have newer versions
- ?? **New Game Downloads** - Download games you don't have from peers
- ?? **Resume Support** - Interrupted transfers can be resumed
- ??? **Cross-Platform** - Works on Windows, Linux, and macOS

## Screenshots

*Coming soon*

## Installation

### Pre-built Releases

Download the latest release for your platform from the [Releases](https://github.com/YousefHSS/GamesLocalShare/releases) page.

### Build from Source

**Prerequisites:**
- .NET 8.0 SDK or later

```bash
# Clone the repository
git clone https://github.com/YousefHSS/GamesLocalShare.git
cd GamesLocalShare

# Build
dotnet build

# Run
dotnet run
```

### Docker

```bash
# Build the container
docker build -t gameslocalshare .

# Run with X11 forwarding (Linux)
docker run -e DISPLAY=$DISPLAY -v /tmp/.X11-unix:/tmp/.X11-unix gameslocalshare
```

## Usage

1. **Scan My Games** - Click to detect your installed Steam games
2. **Start Network** - Enable network discovery and file sharing
3. **Scan for Peers** - Find other computers running GamesLocalShare
4. **Download/Update** - Transfer games from peers

### Network Ports

GamesLocalShare uses the following ports:
- **UDP 45677** - Network discovery
- **TCP 45678** - Game list exchange
- **TCP 45679** - File transfers

Make sure these ports are allowed through your firewall.

## Platform Support

| Platform | Status |
|----------|--------|
| Windows 10/11 | ? Full support |
| Linux (X11) | ? Full support |
| Linux (Wayland) | ?? May require XWayland |
| macOS | ? Full support |
| Docker | ? X11 forwarding required |

## Tech Stack

- **UI Framework:** [Avalonia UI](https://avaloniaui.net/) 11.3
- **MVVM:** CommunityToolkit.Mvvm
- **Target:** .NET 8.0

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- [Avalonia UI](https://avaloniaui.net/) - Cross-platform .NET UI framework
- [Gameloop.Vdf](https://github.com/shravan2x/Gameloop.Vdf) - Steam VDF parser