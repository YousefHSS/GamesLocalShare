# Quick Installer Build Guide

## ?? TL;DR - Fastest Way

```powershell
# One command to build everything:
.\build-release.ps1 -Version "1.0.0" -BuildInstaller
```

That's it! Your installer will be in `installer_output\`

---

## ?? Prerequisites

### Install Inno Setup (One-time)
1. Download from: https://jrsoftware.org/isinfo.php
2. Run the installer
3. That's all!

---

## ?? Building Step-by-Step

### Step 1: Build the Application
```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

### Step 2: Build the Installer
Open Inno Setup, then:
1. **File** > **Open** > Select `installer.iss`
2. **Build** > **Compile**
3. Done! Installer is in `installer_output\`

**OR** just run:
```powershell
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss
```

---

## ?? What You Get

After building, you'll have:

```
installer_output\
Ñ§ÑüÑü GamesLocalShare-Setup-1.0.0.exe   (~50-70MB)
```

This installer:
- ? Installs to Program Files
- ? Creates Start Menu shortcut
- ? Optional Desktop shortcut
- ? Optional "Start with Windows"
- ? Includes uninstaller
- ? Checks for .NET 8 (optional check in script)

---

## ?? Testing the Installer

```powershell
# Test on your machine
.\installer_output\GamesLocalShare-Setup-1.0.0.exe

# Test uninstall
# Go to: Settings > Apps > Games Local Share > Uninstall
```

---

## ?? Customizing the Installer

Edit `installer.iss` to change:

```ini
#define MyAppName "Games Local Share"       Å© App name
#define MyAppVersion "1.0.0"                Å© Version number
#define MyAppPublisher "Your Name"          Å© Your name/company
#define MyAppURL "https://github.com/..."  Å© Your GitHub URL
```

---

## ?? Troubleshooting

### "Inno Setup not found"
- Install from: https://jrsoftware.org/isinfo.php
- Default location: `C:\Program Files (x86)\Inno Setup 6\`

### "Source file not found"
Build the app first:
```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

### "Installer too large"
Use framework-dependent build instead:
```powershell
# In GamesLocalShare.csproj, change:
<SelfContained>false</SelfContained>

# Then rebuild
dotnet publish -c Release -r win-x64 --self-contained false
```

---

## ?? Releasing

### For Local Testing:
1. Build: `.\build-release.ps1 -Version "1.0.0" -BuildInstaller`
2. Test: `.\installer_output\GamesLocalShare-Setup-1.0.0.exe`

### For GitHub Release:
1. Build installer (above)
2. Go to: https://github.com/YourUsername/GamesLocalShare/releases
3. Click "Draft a new release"
4. Upload `GamesLocalShare-Setup-1.0.0.exe`
5. Publish!

---

## ?? Build Options Comparison

| Build Type | Command | Size | .NET Required |
|------------|---------|------|---------------|
| **Self-Contained** | `--self-contained true` | ~60MB | ? No |
| **Framework-Dependent** | `--self-contained false` | ~15MB | ? Yes |

**Recommendation**: Use Self-Contained for easier deployment (users don't need to install .NET)

---

## ?? Tips

- **Faster builds**: Use framework-dependent (~15MB) if users have .NET 8 installed
- **Universal**: Use self-contained (~60MB) for users without .NET 8
- **Smaller size**: The installer compresses files, final size will be less than publish folder
- **Version numbers**: Remember to update version in `installer.iss` before each release

---

## ? Quick Commands Cheat Sheet

```powershell
# Build app + installer (one command)
.\build-release.ps1 -Version "1.0.0" -BuildInstaller

# Just build app
dotnet publish -c Release -r win-x64 --self-contained true

# Just build installer (app must be built first)
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss

# Test the installer
.\installer_output\GamesLocalShare-Setup-1.0.0.exe

# Check what was built
dir installer_output
dir publish-output
```

---

## ?? Complete Workflow

```powershell
# 1. Update version in installer.iss (open in notepad)
notepad installer.iss

# 2. Build everything
.\build-release.ps1 -Version "1.0.0" -BuildInstaller

# 3. Test locally
.\installer_output\GamesLocalShare-Setup-1.0.0.exe

# 4. Create GitHub release
git tag v1.0.0
git push origin v1.0.0

# 5. Upload installer to GitHub release page
# (Go to GitHub Å® Releases Å® Upload files)
```

Done! ??
