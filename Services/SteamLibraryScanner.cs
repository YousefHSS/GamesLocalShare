using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using GamesLocalShare.Models;
using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using Microsoft.Win32;

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
    /// Gets the Steam installation path from registry or common locations
    /// </summary>
    public string? GetSteamPath()
    {
        if (_steamPath != null)
            return _steamPath;

        _scanErrors.Clear();

        // Method 1: Try registry (64-bit)
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            _steamPath = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(_steamPath) && Directory.Exists(_steamPath))
            {
                return _steamPath;
            }
        }
        catch (Exception ex)
        {
            _scanErrors.Add($"Registry (64-bit) access failed: {ex.Message}");
        }

        // Method 2: Try registry (32-bit)
        try
        {
            using var key32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            _steamPath = key32?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(_steamPath) && Directory.Exists(_steamPath))
            {
                return _steamPath;
            }
        }
        catch (Exception ex)
        {
            _scanErrors.Add($"Registry (32-bit) access failed: {ex.Message}");
        }

        // Method 3: Try current user registry
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            _steamPath = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(_steamPath) && Directory.Exists(_steamPath))
            {
                return _steamPath;
            }
        }
        catch (Exception ex)
        {
            _scanErrors.Add($"Registry (CurrentUser) access failed: {ex.Message}");
        }

        // Method 4: Try common installation paths
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
            {
                _steamPath = path;
                return _steamPath;
            }
        }

        _scanErrors.Add("Could not find Steam installation in registry or common locations");
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

        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name))
            return null;

        var installPath = Path.Combine(libraryFolder, "common", installDir ?? name);
        
        long.TryParse(sizeOnDiskStr, out long sizeOnDisk);
        
        DateTime lastUpdated = DateTime.MinValue;
        if (long.TryParse(lastUpdatedStr, out long unixTimestamp))
        {
            lastUpdated = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).LocalDateTime;
        }

        // If we don't have size from manifest, calculate from directory
        if (sizeOnDisk == 0 && Directory.Exists(installPath))
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
            IsInstalled = Directory.Exists(installPath)
        };

        // Try to load a cover image (non-blocking best-effort) - run synchronously on background thread
        try
        {
            TryLoadSteamCover(game);
        }
        catch (Exception ex)
        {
            _scanErrors.Add($"Cover load failed for {game.Name}: {ex.Message}");
        }

        return game;
    }

    /// <summary>
    /// Attempts to download a cover image for the given game using the Steam CDN (falls back to other sizes).
    /// This runs synchronously and is intended to be called from the background scanner thread.
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

                // Create BitmapImage from bytes
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze(); // make it cross-thread accessible

                game.CoverImage = bmp;
                return;
            }
            catch
            {
                // ignore and try next
            }
        }

        // CDN attempts failed - fall back to scraping SteamDB search results for first image
        TryScrapeSteamDbCover(game);
    }

    /// <summary>
    /// Try to scrape steamdb.info search page for the game's first result image and download it.
    /// Best-effort; may fail silently.
    /// </summary>
    private void TryScrapeSteamDbCover(GameInfo game)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(game.Name))
                return;

            var query = System.Uri.EscapeDataString(game.Name);
            var url = $"https://steamdb.info/search/?a=all&q={query}";
            var html = _httpClient.GetStringAsync(url).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(html))
                return;

            // Find first <img ... src="..."> in the page
            var m = Regex.Match(html, "<img[^>]+src=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!m.Success)
                return;

            var imgUrl = m.Groups[1].Value;
            if (imgUrl.StartsWith("//"))
                imgUrl = "https:" + imgUrl;
            else if (imgUrl.StartsWith("/"))
                imgUrl = "https://steamdb.info" + imgUrl;

            using var resp = _httpClient.Send(new HttpRequestMessage(HttpMethod.Get, imgUrl));
            if (!resp.IsSuccessStatusCode)
                return;

            var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (bytes == null || bytes.Length == 0)
                return;

            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            game.CoverImage = bmp;
        }
        catch (Exception ex)
        {
            _scanErrors.Add($"SteamDB scrape failed for {game.Name}: {ex.Message}");
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
