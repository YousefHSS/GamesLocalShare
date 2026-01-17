# Building and Releasing GamesLocalShare

## Quick Release Steps

### 1. Build the Application

```powershell
# Framework-dependent (small, requires .NET 8) - RECOMMENDED
dotnet publish -c Release -r win-x64 --self-contained false

# Self-contained (larger, no dependencies)
dotnet publish -c Release -r win-x64 --self-contained true
```

Output will be in: `bin\Release\net8.0\win-x64\publish\`

### 2. Create Installer (Choose One)

#### Option A: Inno Setup (Easiest - RECOMMENDED)

1. Download and install [Inno Setup](https://jrsoftware.org/isinfo.php)
2. Open `installer.iss` in Inno Setup
3. Click **Build > Compile**
4. Installer will be created in `installer_output\` folder

**OR** use the build script:
```powershell
.\build-release.ps1 -Version "1.0.0" -BuildInstaller
```

#### Option B: WiX Toolset (Advanced - Real MSI)

**Note**: WiX requires manually listing all files or using heat.exe to harvest them. For simplicity, use Inno Setup instead.

If you still want to use WiX:

```powershell
# Install WiX v4
dotnet tool install --global wix

# First, harvest all files from publish folder
wix heat dir bin\Release\net8.0\win-x64\publish -gg -sfrag -srd -sreg -dr INSTALLFOLDER -cg PublishFiles -out publish-files.wxs

# Then build MSI
wix build installer.wxs publish-files.wxs -out GamesLocalShare.msi
```

**Recommended**: Just use Inno Setup - it's simpler and creates a professional installer.

### 3. Create GitHub Release

#### Automatic (Recommended):

1. Commit and push all changes:
```bash
git add .
git commit -m "Prepare v1.0.0 release"
git push
```

2. Create and push a version tag:
```bash
git tag v1.0.0
git push origin v1.0.0
```

3. GitHub Actions will automatically:
   - Build both versions
   - Create installer
   - Create GitHub release with all files

#### Manual Release:

1. Go to your GitHub repo: https://github.com/YousefHSS/GamesLocalShare
2. Click **Releases** > **Draft a new release**
3. Click **Choose a tag** > Type `v1.0.0` > **Create new tag**
4. Fill in:
   - **Release title**: `v1.0.0 - Initial Release`
   - **Description**: See example below
5. Drag and drop these files:
   - The installer `.exe` from `installer_output\`
   - Optional: ZIP of publish folder
6. Click **Publish release**

### Example Release Description:

```markdown
## ?? Games Local Share v1.0.0

Share Steam games over your local network at full speed!

### ?? Downloads

Choose the version that works best for you:

| Package | Size | Requirements | Best For |
|---------|------|--------------|----------|
| **[GamesLocalShare-Setup.exe](link)** | ~15MB | .NET 8 Runtime | Most users (recommended) |
| **[GamesLocalShare-Portable.zip](link)** | ~60MB | None | No installation needed |

### ? Features

- ?? Fast local network game transfers
- ?? Automatic Steam library scanning
- ?? Auto-discovery of nearby computers
- ?? Real-time transfer progress
- ?? Pause and resume downloads
- ?? Queue multiple downloads
- ?? High-speed mode for wired connections
- ?? Windows Firewall configuration helper

### ?? Requirements

- **OS**: Windows 10 or later (64-bit)
- **Runtime**: [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (installer will check)
- **Network**: Local network connection between computers

### ?? Quick Start

1. Install on all computers you want to share games between
2. Click "Start Network" to go online
3. Click "Scan My Games" to share your library
4. Browse available games from connected computers
5. Download and enjoy!

### ?? Known Issues

- First-time startup may require firewall configuration (app will guide you)
- Large games (>100GB) may take time to scan initially

### ?? Tips

- Use wired connections for best performance
- Enable "Start with Windows" in settings for automatic sharing
- Use "High Speed Mode" if both computers are wired

---

**Full Changelog**: https://github.com/YousefHSS/GamesLocalShare/commits/v1.0.0
```

## File Size Comparison

| Build Type | Size | .NET Required | Speed |
|------------|------|---------------|-------|
| Framework-Dependent | ~15-20MB | Yes | Faster startup |
| Self-Contained Trimmed | ~50-80MB | No | Slightly slower |
| Self-Contained Full | ~150-200MB | No | Slower startup |

## Testing Before Release

```powershell
# Test the published build
cd bin\Release\net8.0\win-x64\publish
.\GamesLocalShare.exe

# Test the installer
.\installer_output\GamesLocalShare-Setup-1.0.0.exe
```

## Update Version Numbers

Before releasing, update version in:

1. `installer.iss` - `#define MyAppVersion "1.0.0"`
2. `installer.wxs` - `Version="1.0.0.0"`
3. Git tag - `v1.0.0`

## Troubleshooting

### "File is too large" on GitHub

GitHub has a 2GB file limit per release. If your self-contained build is too large:
- Use framework-dependent build
- Enable more aggressive trimming
- Split into multiple files

### Installer won't build

- **Inno Setup**: 
  - Make sure file paths in `installer.iss` match your publish output
  - Check that `bin\Release\net8.0\win-x64\publish\` exists
  - Run `dotnet publish -c Release -r win-x64` first if folder is missing
  
- **WiX**: 
  - WiX v4 requires explicit file listing (wildcards don't work)
  - Use `heat.exe` to harvest files automatically
  - **Easier option**: Use Inno Setup instead
  - Install WiX v4: `dotnet tool install --global wix`
  
- **Common issue**: "Source file not found"
  - Make sure you've built the release first: `dotnet publish -c Release -r win-x64`
  - Check that the path in the installer script matches your actual publish path

### .NET 8 not found

Install from: https://dotnet.microsoft.com/download/dotnet/8.0

Choose "Desktop Runtime" for end users.
