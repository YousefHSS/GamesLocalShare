using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using GamesLocalShare.Models;
using Gameloop.Vdf;
using Gameloop.Vdf.Linq;

namespace GamesLocalShare.Services;

/// <summary>
/// Service to scan Steam library folders and detect installed games
/// </summary>
public class SteamLibraryScanner
{
    private string? _steamPath;
    private readonly List<string> _scanErrors = [];
    private static readonly HttpClient _httpClient = new HttpClient();

    /// <summary>
    /// Gets any errors that occurred during the last scan
    /// </summary>
    public IReadOnlyList<string> ScanErrors => _scanErrors;

    /// <summary>
    /// Gets the last detected Steam path
    /// </summary>
    public string? LastSteamPath => _steamPath;

    /// <summary>
    /// Gets the Steam installation path from registry (Windows) or common locations (cross-platform)
    /// </summary>
    public string? GetSteamPath()
    {
        if (_steamPath != null)
            return _steamPath;

        _scanErrors.Clear();

        // Platform-specific Steam path detection
        if (OperatingSystem.IsWindows())
        {
            _steamPath = GetSteamPathWindows();
        }
        else if (OperatingSystem.IsLinux())
        {
            _steamPath = GetSteamPathLinux();
        }
        else if (OperatingSystem.IsMacOS())
        {
            _steamPath = GetSteamPathMacOS();
        }

        if (string.IsNullOrEmpty(_steamPath))
        {
            _scanErrors.Add("Could not find Steam installation");
        }

        return _steamPath;
    }

    [SupportedOSPlatform("windows")]
    private string? GetSteamPathWindows()
    {
        // Try registry (Windows only)
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            var path = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return path;
        }
        catch (Exception ex)
        {
            _scanErrors.Add($"Registry (64-bit) access failed: {ex.Message}");
        }

        try
        {
            using var key32 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            var path = key32?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return path;
        }
        catch (Exception ex)
        {
            _scanErrors.Add($"Registry (32-bit) access failed: {ex.Message}");
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return path;
        }
        catch (Exception ex)
        {
            _scanErrors.Add($"Registry (CurrentUser) access failed: {ex.Message}");
        }

        // Try common Windows paths
        var commonPaths = new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"D:\Program Files (x86)\Steam",
            @"E:\Steam",
            @"E:\Program Files (x86)\Steam",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        };

        foreach (var path in commonPaths)
        {
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, "steam.exe")))
                return path;
        }

        return null;
    }

    private string? GetSteamPathLinux()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var commonPaths = new[]
        {
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "debian-installation"),
            "/usr/share/steam",
            "/usr/local/share/steam"
        };

        foreach (var path in commonPaths)
        {
            if (Directory.Exists(path) && Directory.Exists(Path.Combine(path, "steamapps")))
                return path;
        }

        return null;
    }

    private string? GetSteamPathMacOS()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var steamPath = Path.Combine(home, "Library", "Application Support", "Steam");
        
        if (Directory.Exists(steamPath) && Directory.Exists(Path.Combine(steamPath, "steamapps")))
            return steamPath;

        return null;
    }

    /// <summary>
    /// Gets all Steam library folders (Steam can have multiple library locations)
    /// </summary>
    public List<string> GetLibraryFolders()
    {
        var folders = new List<string>();
        var steamPath = GetSteamPath();

        if (string.IsNullOrEmpty(steamPath))
        {
            _scanErrors.Add("Steam path not found - cannot get library folders");
            return folders;
        }

        // The main steamapps folder
        var mainSteamApps = Path.Combine(steamPath, "steamapps");
        if (Directory.Exists(mainSteamApps))
        {
            folders.Add(mainSteamApps);
        }
        else
        {
            _scanErrors.Add($"Main steamapps folder not found: {mainSteamApps}");
        }

        // Parse libraryfolders.vdf for additional library locations
        var libraryFoldersPath = Path.Combine(mainSteamApps, "libraryfolders.vdf");
        if (File.Exists(libraryFoldersPath))
        {
            try
            {
                var vdf = VdfConvert.Deserialize(File.ReadAllText(libraryFoldersPath));
                var libraryFoldersNode = vdf.Value;

                foreach (var child in libraryFoldersNode.Children())
                {
                    if (child is VProperty prop && prop.Value is VObject obj)
                    {
                        var pathProperty = obj["path"];
                        if (pathProperty != null)
                        {
                            var libraryPath = pathProperty.ToString();
                            var steamAppsPath = Path.Combine(libraryPath, "steamapps");
                            if (Directory.Exists(steamAppsPath) && !folders.Contains(steamAppsPath))
                            {
                                folders.Add(steamAppsPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _scanErrors.Add($"Error parsing libraryfolders.vdf: {ex.Message}");
            }
        }
        else
        {
            _scanErrors.Add($"libraryfolders.vdf not found: {libraryFoldersPath}");
        }

        return folders;
    }

    /// <summary>
    /// Scans all Steam library folders and returns installed games
    /// </summary>
    public async Task<List<GameInfo>> ScanGamesAsync()
    {
        return await Task.Run(() =>
        {
            _scanErrors.Clear();
            var games = new List<GameInfo>();
            var libraryFolders = GetLibraryFolders();

            if (libraryFolders.Count == 0)
            {
                _scanErrors.Add("No Steam library folders found");
                return games;
            }

            foreach (var folder in libraryFolders)
            {
                try
                {
                    var manifestFiles = Directory.GetFiles(folder, "appmanifest_*.acf");
                    
                    if (manifestFiles.Length == 0)
                    {
                        _scanErrors.Add($"No app manifests found in: {folder}");
                        continue;
                    }

                    foreach (var manifestPath in manifestFiles)
                    {
                        try
                        {
                            var game = ParseAppManifest(manifestPath, folder);
                            if (game != null && !string.IsNullOrEmpty(game.Name))
                            {
                                games.Add(game);
                            }
                        }
                        catch (Exception ex)
                        {
                            _scanErrors.Add($"Error parsing {Path.GetFileName(manifestPath)}: {ex.Message}");
                        }
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    _scanErrors.Add($"Access denied to folder {folder}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    _scanErrors.Add($"Error scanning folder {folder}: {ex.Message}");
                }
            }

            return games.OrderBy(g => g.Name).ToList();
        });
    }

    /// <summary>
    /// Gets a detailed scan report for troubleshooting
    /// </summary>
    public string GetScanReport()
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine("=== Steam Library Scan Report ===");
        report.AppendLine($"Steam Path: {_steamPath ?? "NOT FOUND"}");
        report.AppendLine($"Platform: {GetPlatformName()}");
        report.AppendLine($"Library Folders Found: {GetLibraryFolders().Count}");
        
        foreach (var folder in GetLibraryFolders())
        {
            report.AppendLine($"  - {folder}");
        }

        if (_scanErrors.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("Errors/Warnings:");
            foreach (var error in _scanErrors)
            {
                report.AppendLine($"  ! {error}");
            }
        }

        return report.ToString();
    }

    private static string GetPlatformName()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsLinux()) return "Linux";
        if (OperatingSystem.IsMacOS()) return "macOS";
        return "Unknown";
    }

    /// <summary>
    /// Parses a Steam app manifest file to extract game information
    /// </summary>
    private GameInfo? ParseAppManifest(string manifestPath, string libraryFolder)
    {
        var content = File.ReadAllText(manifestPath);
        var vdf = VdfConvert.Deserialize(content);
        var appState = vdf.Value;

        var appId = appState["appid"]?.ToString();
        var name = appState["name"]?.ToString();
        var installDir = appState["installdir"]?.ToString();
        var buildId = appState["buildid"]?.ToString();
        var sizeOnDiskStr = appState["SizeOnDisk"]?.ToString();
        var lastUpdatedStr = appState["LastUpdated"]?.ToString();
        var stateFlags = appState["StateFlags"]?.ToString();

        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name))
            return null;

        var installPath = Path.Combine(libraryFolder, "common", installDir ?? name);
        
        // Check if the game directory actually exists
        // Steam keeps manifest files for uninstalled games, so we need to verify the folder exists
        if (!Directory.Exists(installPath))
        {
            _scanErrors.Add($"Skipping {name} (AppId: {appId}) - install directory not found: {installPath}");
            return null;
        }

        // Check if the directory has any content (not just an empty folder)
        try
        {
            var hasFiles = Directory.EnumerateFileSystemEntries(installPath).Any();
            if (!hasFiles)
            {
                _scanErrors.Add($"Skipping {name} (AppId: {appId}) - install directory is empty: {installPath}");
                return null;
            }
        }
        catch (Exception ex)
        {
            _scanErrors.Add($"Skipping {name} (AppId: {appId}) - cannot access directory: {ex.Message}");
            return null;
        }

        // Check StateFlags - 4 means fully installed, other values indicate incomplete/updating
        // StateFlags: 4 = Fully Installed, 2 = Update Required, 1026 = Updating, etc.
        if (!string.IsNullOrEmpty(stateFlags) && int.TryParse(stateFlags, out int flags))
        {
            // Skip if state indicates the game is not fully installed
            // State 4 = fully installed, State 6 = fully installed + needs update
            // We want to include games that are at least partially installed (have files)
            if (flags == 0)
            {
                _scanErrors.Add($"Skipping {name} (AppId: {appId}) - StateFlags indicates not installed (flags={flags})");
                return null;
            }
        }

        long.TryParse(sizeOnDiskStr, out long sizeOnDisk);
        
        DateTime lastUpdated = DateTime.MinValue;
        if (long.TryParse(lastUpdatedStr, out long unixTimestamp))
        {
            lastUpdated = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).LocalDateTime;
        }

        // If we don't have size from manifest, calculate from directory
        if (sizeOnDisk == 0)
        {
            try
            {
                sizeOnDisk = GetDirectorySize(installPath);
            }
            catch { }
        }

        var game = new GameInfo
        {
            AppId = appId,
            Name = name,
            InstallPath = installPath,
            BuildId = buildId ?? "unknown",
            SizeOnDisk = sizeOnDisk,
            LastUpdated = lastUpdated,
            Platform = GamePlatform.Steam,
            IsInstalled = true // We've verified the directory exists and has content
        };

        return game;
    }

    /// <summary>
    /// Loads cover image for a game asynchronously (non-blocking)
    /// </summary>
    public async Task LoadCoverImageAsync(GameInfo game)
    {
        await Task.Run(() =>
        {
            try
            {
                TryLoadSteamCover(game);
            }
            catch (Exception ex)
            {
                _scanErrors.Add($"Cover load failed for {game.Name}: {ex.Message}");
            }
        });

        // For Avalonia, we need to ensure the image is set on the UI thread
        if (game.CoverImage != null)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Force property change notification by setting the property again
                var image = game.CoverImage;
                game.CoverImage = null;
                game.CoverImage = image;
            });
        }
    }

    /// <summary>
    /// Attempts to download a cover image for the given game using the Steam CDN (falls back to other sizes).
    /// </summary>
    private void TryLoadSteamCover(GameInfo game)
    {
        if (string.IsNullOrWhiteSpace(game.AppId))
            return;

        if (!int.TryParse(game.AppId, out var appIdNumeric))
            return;

        // List of candidate image URLs (try higher-res images first)
        var candidates = new[]
        {
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appIdNumeric}/library_600x900.jpg",
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appIdNumeric}/header.jpg",
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appIdNumeric}/capsule_184x69.jpg",
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appIdNumeric}/capsule_231x87.jpg"
        };

        foreach (var url in candidates)
        {
            try
            {
                using var resp = _httpClient.Send(new HttpRequestMessage(HttpMethod.Get, url));
                if (!resp.IsSuccessStatusCode)
                    continue;

                var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (bytes == null || bytes.Length == 0)
                    continue;

                // Create Avalonia Bitmap from bytes
                using var ms = new MemoryStream(bytes);
                var bmp = new Bitmap(ms);
                game.CoverImage = bmp;
                return;
            }
            catch
            {
                // ignore and try next
            }
        }
    }

    /// <summary>
    /// Calculates the total size of a directory
    /// </summary>
    private long GetDirectorySize(string path)
    {
        long size = 0;
        var dirInfo = new DirectoryInfo(path);
        
        foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            size += file.Length;
        }
        
        return size;
    }
}
