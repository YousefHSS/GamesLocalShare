# Settings Persistence Fix - Summary

## ?? Problem
Settings were not persisting between app restarts in the published build. Each time the app was closed and reopened, all settings reverted to defaults.

## ?? Root Cause
The issue was that **multiple parts of the application were creating separate instances** of `AppSettings`:
- `MainViewModel` called `AppSettings.Load()` Å® Instance A
- `MainWindow` called `AppSettings.Load()` Å® Instance B  
- `SettingsWindow` received settings via constructor Å® Instance A (from MainViewModel)

When settings were changed and saved via Instance A, Instance B (in MainWindow) still had the old values. When the app restarted, it loaded fresh from disk but multiple instances meant changes could be lost.

## ? Solution
Converted `AppSettings` to a **singleton pattern**:

```csharp
private static AppSettings? _instance;
private static readonly object _lock = new object();

public static AppSettings Load()
{
    lock (_lock)
    {
        if (_instance != null)
        {
            return _instance; // Return cached instance
        }
        
        // Load from disk only once
        // ... loading logic ...
        
        _instance = loadedSettings;
        return _instance;
    }
}
```

### Key Changes:
1. **Singleton Instance**: `AppSettings.Load()` now returns the **same instance** every time
2. **Thread-Safe**: Uses `lock` to prevent race conditions
3. **Reload Method**: Added `AppSettings.Reload()` to force refresh from disk if needed
4. **Enhanced Logging**: Added detailed debug output to track save/load operations

## ?? Changes Made

### Files Modified:
1. **Models/AppSettings.cs**
   - Converted to singleton pattern
   - Added `_instance` and `_lock` fields
   - Modified `Load()` to return cached instance
   - Added `Reload()` method
   - Enhanced logging in `Save()` and `Load()`

### Files Created:
1. **test-settings.ps1** - Script to verify settings persistence

## ?? Testing

### Manual Test Steps:
1. Build the release: `.\build-release.ps1 -SelfContained`
2. Run the app: `.\publish-output\GamesLocalShare.exe`
3. Open Settings (click Settings button)
4. Make changes:
   - Toggle "Auto-start network"
   - Toggle "Auto-update games"  
   - Change update interval
   - Toggle "Minimize to tray"
5. Click "Save" and close settings
6. **Close the app completely** (not just minimize)
7. Check if settings were saved: `.\test-settings.ps1`
8. **Restart the app**
9. Open Settings again
10. ? Settings should be **exactly as you left them**

### Expected Behavior:
- Settings file exists at: `%AppData%\GamesLocalShare\settings.json`
- File is updated when you click Save
- Settings persist across app restarts
- All instances of the app use the same settings object

### Debug Output:
When running in Debug mode, you'll see console output like:
```
Loading settings from: C:\Users\...\AppData\Roaming\GamesLocalShare\settings.json
? Settings file found, content: { ... }
? Settings loaded successfully
```

And when saving:
```
? Settings saved successfully to: C:\Users\...\AppData\Roaming\GamesLocalShare\settings.json
  Settings content: { "AutoStartNetwork": true, ... }
```

## ?? Settings Location

Settings are stored in:
```
Windows: %APPDATA%\GamesLocalShare\settings.json
         (e.g., C:\Users\YourName\AppData\Roaming\GamesLocalShare\settings.json)

Linux:   ~/.config/GamesLocalShare/settings.json
macOS:   ~/Library/Application Support/GamesLocalShare/settings.json
```

This location:
- ? Survives app updates
- ? Survives app reinstalls  
- ? Is user-specific (each Windows user has their own settings)
- ? Doesn't require admin permissions
- ? Is the standard location for app data on Windows

## ?? Additional Improvements

### Better Error Handling:
```csharp
catch (Exception ex)
{
    System.Diagnostics.Debug.WriteLine($"? ERROR saving settings");
    System.Diagnostics.Debug.WriteLine($"  Exception: {ex.GetType().Name}");
    System.Diagnostics.Debug.WriteLine($"  Message: {ex.Message}");
    
    if (ex is UnauthorizedAccessException)
    {
        System.Diagnostics.Debug.WriteLine($"  ? Access denied - check folder permissions");
    }
}
```

## ?? Next Steps

1. **Test in Production**:
   - Build release version
   - Test on a clean machine (or clean %AppData%\GamesLocalShare)
   - Verify settings persist across multiple restarts

2. **Monitor Debug Output**:
   - Run app from Visual Studio
   - Open Output window Å® Debug
   - Watch for settings save/load messages

3. **Verify File Permissions**:
   - Run `.\test-settings.ps1`
   - Check if settings file can be created/written

## ?? Verification Checklist

After rebuilding, verify these all work:

- [ ] Settings save when clicking Save button
- [ ] Settings file is created in %AppData%\GamesLocalShare
- [ ] Settings persist after closing and reopening app
- [ ] Auto-start network setting works on restart
- [ ] Auto-update games setting works on restart  
- [ ] Minimize to tray setting works when closing window
- [ ] Hidden games list persists
- [ ] Update interval persists

## ?? Known Limitations

- Settings are **not** synced across machines (each machine has its own settings)
- If settings file is corrupted, app will use defaults (and overwrite corrupted file on next save)
- Settings are loaded **once** at app startup (singleton pattern)

## ?? Troubleshooting

### If settings still don't persist:

1. **Check if file is being created**:
   ```powershell
   Test-Path "$env:APPDATA\GamesLocalShare\settings.json"
   Get-Content "$env:APPDATA\GamesLocalShare\settings.json"
   ```

2. **Check file permissions**:
   ```powershell
   $acl = Get-Acl "$env:APPDATA\GamesLocalShare"
   $acl.Access | Format-Table IdentityReference,FileSystemRights,AccessControlType
   ```

3. **Run as regular user** (not admin):
   - Admin vs regular user have different %AppData% folders
   - Settings saved as admin won't be seen by regular user

4. **Check for multiple app instances**:
   - Settings might be saved by one instance but loaded by another
   - Ensure you're testing the same exe each time

5. **Enable detailed logging**:
   - Run app from Visual Studio
   - Watch Debug output for save/load messages
   - Look for error messages

## ?? Related Files

- `Models/AppSettings.cs` - Settings model and persistence logic
- `Views/SettingsWindow.axaml(.cs)` - Settings UI
- `ViewModels/MainViewModel.cs` - Uses settings throughout app
- `Views/MainWindow.axaml.cs` - Uses settings for minimize to tray
- `test-settings.ps1` - Testing script
