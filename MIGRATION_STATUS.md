# GamesLocalShare - Avalonia UI Migration

## Migration Status: ? Phase 1 Complete

This document tracks the migration from WPF to Avalonia UI for cross-platform support.

## Completed Steps

### Phase 1: Project Setup ?
- [x] Installed Avalonia templates
- [x] Updated `.csproj` to target `net8.0` (cross-platform)
- [x] Added Avalonia NuGet packages:
  - `Avalonia` 11.3.0
  - `Avalonia.Desktop` 11.3.0
  - `Avalonia.Themes.Fluent` 11.3.0
  - `Avalonia.Fonts.Inter` 11.3.0
  - `Avalonia.Diagnostics` 11.3.0 (debug only)
  - `Avalonia.ReactiveUI` 11.3.0
  - `Avalonia.Svg.Skia` 11.3.0
- [x] Created `app.manifest` for Windows compatibility
- [x] Created `Program.cs` entry point

### Phase 2: Core Migration ?
- [x] Created `App.axaml` and `App.axaml.cs`
- [x] Created `Styles/AppStyles.axaml` with custom styles
- [x] Created `Converters/AvaloniaConverters.cs` with cross-platform converters
- [x] Converted `MainWindow.axaml` from WPF XAML
- [x] Created `Views/MainWindow.axaml.cs`
- [x] Removed WPF-specific files:
  - `App.xaml` / `App.xaml.cs`
  - `Views/MainWindow.xaml` / `Views/MainWindow.xaml.cs`
  - `Views/DiagnosticReportDialog.xaml` / `.cs`
  - `Views/InputDialog.xaml` / `.cs`
  - `Views/SettingsDialog.xaml` / `.cs`
  - `Controls/OutlinedText.cs`
  - `Converters/ValueConverters.cs`

### Phase 3: Platform Abstractions ?
- [x] Updated `Models/GameInfo.cs` to use `Avalonia.Media.Imaging.Bitmap`
- [x] Updated `Services/SteamLibraryScanner.cs`:
  - Cross-platform Steam path detection (Windows/Linux/macOS)
  - Avalonia Bitmap for cover images
- [x] Updated `ViewModels/MainViewModel.cs`:
  - `Avalonia.Threading.Dispatcher` instead of WPF Dispatcher
  - Platform-aware clipboard operations
  - Cross-platform firewall handling
- [x] Updated `Services/FirewallHelper.cs`:
  - `[SupportedOSPlatform]` attributes for Windows-specific code
  - Graceful fallbacks for Linux/macOS

### Phase 4: Containerization ?
- [x] Updated `Dockerfile` for Avalonia app:
  - Multi-stage build
  - Linux desktop stage with X11 dependencies
  - Proper port exposure (45677/udp, 45678/tcp, 45679/tcp)

## Build Status

```
? Build succeeded
```

## Running the Application

### Windows
```bash
dotnet run
# or
.\bin\Debug\net8.0\GamesLocalShare.exe
```

### Linux
```bash
dotnet run
# or
./bin/Debug/net8.0/GamesLocalShare
```

### macOS
```bash
dotnet run
# or
./bin/Debug/net8.0/GamesLocalShare
```

### Docker (Linux with X11)
```bash
# Build the container
docker build -t gameslocalshare .

# Run with X11 forwarding (Linux host)
docker run -e DISPLAY=$DISPLAY -v /tmp/.X11-unix:/tmp/.X11-unix gameslocalshare
```

## Key Changes Summary

| Component | WPF | Avalonia |
|-----------|-----|----------|
| Project Target | `net8.0-windows` | `net8.0` |
| UI Framework | WPF + MahApps.Metro | Avalonia + Fluent Theme |
| XAML Extension | `.xaml` | `.axaml` |
| Image Type | `BitmapImage` | `Avalonia.Media.Imaging.Bitmap` |
| Dispatcher | `Application.Current.Dispatcher` | `Avalonia.Threading.Dispatcher.UIThread` |
| Visibility | `Visibility.Visible/Collapsed` | `true/false` on `IsVisible` |
| Clipboard | `System.Windows.Clipboard` | `Window.Clipboard` |
| Registry Access | `Microsoft.Win32.Registry` | Platform-guarded with `[SupportedOSPlatform]` |

## Remaining Work (Optional Enhancements)

### Phase 5: Testing & Polish
- [ ] Test on Linux
- [ ] Test on macOS
- [ ] Add proper dialog windows (InputDialog, SettingsDialog)
- [ ] Add SVG icon loading with Avalonia.Svg.Skia
- [ ] Add game cover image display
- [ ] Performance testing in container environment

### Future Improvements
- [ ] Implement proper confirmation dialogs
- [ ] Add system tray support (cross-platform)
- [ ] Add auto-update mechanism
- [ ] Create platform-specific installers
- [ ] Add CI/CD pipeline for multi-platform builds

## Platform Support Matrix

| Platform | Status | Notes |
|----------|--------|-------|
| Windows 10/11 | ? Ready | Full feature support |
| Linux (X11) | ? Ready | Requires X11 display |
| Linux (Wayland) | ?? Partial | May require XWayland |
| macOS | ? Ready | Native support |
| Docker | ? Ready | X11 forwarding required for GUI |

## File Structure After Migration

```
GamesLocalShare/
??? App.axaml                 # Avalonia Application
??? App.axaml.cs
??? Program.cs                # Entry point
??? app.manifest              # Windows manifest
??? GamesLocalShare.csproj    # Cross-platform project
??? Dockerfile                # Container support
?
??? Assets/
?   ??? avalonia-logo.svg
?
??? Converters/
?   ??? AvaloniaConverters.cs # Cross-platform converters
?
??? Icons/                    # Icon resources
?
??? Models/
?   ??? AppSettings.cs
?   ??? GameInfo.cs           # Updated for Avalonia Bitmap
?   ??? GameSyncInfo.cs
?   ??? LogMessage.cs
?   ??? NetworkPeer.cs
?   ??? TransferState.cs
?
??? Services/
?   ??? FileTransferService.cs
?   ??? FirewallHelper.cs     # Platform-aware
?   ??? NetworkDiagnosticService.cs
?   ??? NetworkDiscoveryService.cs
?   ??? SteamLibraryScanner.cs # Cross-platform Steam detection
?
??? Styles/
?   ??? AppStyles.axaml       # Avalonia styles
?
??? ViewModels/
?   ??? MainViewModel.cs      # Platform-aware
?
??? Views/
    ??? MainWindow.axaml      # Avalonia main window
    ??? MainWindow.axaml.cs
```
