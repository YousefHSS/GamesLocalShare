# XAML to HTML/React Migration Guide

## What Changed

- **MainWindow.axaml**: Replaced all XAML panels with a single `<WebView>` control
- **MainWindow.axaml.cs**: Now initializes the WebView and InteropBridge
- **InteropBridge.cs** (NEW): Two-way message bridge between C# backend and JavaScript frontend
- **WebUI/** (NEW): React + Vite + Tailwind web application that replaces XAML UI
- **GamesLocalShare.csproj**: Added WebView packages and build automation for WebUI

## Setup Instructions

### 1. Install WebUI Dependencies
```bash
cd WebUI
npm install
```

### 2. Build the Project
```bash
# From project root
dotnet build
```

This will:
1. Build the React WebUI (`npm run build` in WebUI folder)
2. Copy built assets to `Assets/webui/`
3. Build the C# application with embedded WebView

### 3. Run the Application
```bash
dotnet run
```

The app will launch with the React UI embedded in Avalonia WebView.

## Architecture

### C# → JavaScript (State Push)
- InteropBridge subscribes to `MainViewModel` property changes
- When state changes, it calls `window.__updateState(stateJson)` with updated properties
- Zustand store receives updates and re-renders React components

### JavaScript → C# (Commands)
- React components call `sendCommand('CommandName', payload)` 
- This posts a message to the WebView bridge
- InteropBridge receives the message and executes the corresponding ViewModel command

## File Structure

```
GamesLocalShare/
├── Services/
│   └── InteropBridge.cs           (NEW - C# ↔ JS bridge)
├── Views/
│   ├── MainWindow.axaml           (UPDATED - simplified)
│   └── MainWindow.axaml.cs        (UPDATED - WebView init)
├── WebUI/                         (NEW - React app)
│   ├── src/
│   │   ├── main.tsx              (entry point)
│   │   ├── App.tsx               (main layout)
│   │   ├── store.ts              (Zustand state store)
│   │   ├── bridge.ts             (C# ↔ JS bridge)
│   │   ├── index.css             (Tailwind)
│   │   └── components/
│   │       ├── GameLogo.tsx
│   │       ├── Toolbar.tsx
│   │       ├── InstructionsBanner.tsx
│   │       ├── ContentPanels.tsx
│   │       ├── StatusBar.tsx
│   │       ├── LogOverlay.tsx
│   │       └── panels/
│   │           ├── MyGamesPanel.tsx
│   │           ├── PeersPanel.tsx
│   │           ├── UpdatesPanel.tsx
│   │           └── QueuePanel.tsx
│   ├── vite.config.ts
│   ├── tailwind.config.js
│   ├── tsconfig.json
│   └── package.json
└── Assets/
    └── webui/                    (Generated - built React app)
```

## Development Workflow

### Frontend Development
While developing the React UI:
```bash
cd WebUI
npm run dev
```
This starts a Vite dev server at `http://localhost:5173`. However, it won't have backend access.

### Build for Desktop App
```bash
cd WebUI
npm run build
```
Then rebuild the C# app to embed the new assets.

## What Still Works

- System tray minimize/restore (native code, unchanged)
- All C# backend services (SteamLibraryScanner, NetworkDiscoveryService, FileTransferService)
- All ViewModel commands and data
- Firewall configuration (Windows-only)

## Customization

### Colors
Edit `WebUI/tailwind.config.js` to customize the dark theme colors. Currently uses:
- `#1E1E1E` - Main background
- `#2D2D30` - Panel background  
- `#0078D4` - Accent blue
- `#8B5CF6` - Accent purple

### Adding New Panels
1. Create new component in `WebUI/src/components/panels/`
2. Import in `ContentPanels.tsx`
3. Add to the flex layout

### Adding New Commands
1. Add command to `sendCommand()` calls in components
2. Add handler in `InteropBridge.cs` HandleCommandAsync method
3. Route to appropriate ViewModel command

## Troubleshooting

### "Web UI not found" message
- Ensure `npm run build` completed successfully
- Check that `WebUI/dist/index.html` exists
- Rebuild the C# project

### Commands not working
- Check browser console (if running in dev mode)
- Verify InteropBridge is initialized (`await _bridge.InitializeAsync()`)
- Check that command name matches both C# and JavaScript sides

### Build fails
- Delete `WebUI/node_modules` and `WebUI/dist`
- Run `npm install` again
- Ensure Node.js 16+ is installed
