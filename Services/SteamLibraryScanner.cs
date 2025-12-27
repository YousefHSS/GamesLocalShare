using System.IO;
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

    /// <summary>
    /// Gets the Steam installation path from registry
    /// </summary>
    public string? GetSteamPath()
    {
        if (_steamPath != null)
            return _steamPath;

        try
        {
            // Try 64-bit registry first
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            _steamPath = key?.GetValue("InstallPath") as string;

            if (string.IsNullOrEmpty(_steamPath))
            {
                // Try 32-bit registry
                using var key32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
                _steamPath = key32?.GetValue("InstallPath") as string;
            }
        }
        catch
        {
            // Registry access failed
        }

        return _steamPath;
    }

    /// <summary>
    /// Gets all Steam library folders (Steam can have multiple library locations)
    /// </summary>
    public List<string> GetLibraryFolders()
    {
        var folders = new List<string>();
        var steamPath = GetSteamPath();

        if (string.IsNullOrEmpty(steamPath))
            return folders;

        // The main steamapps folder
        var mainSteamApps = Path.Combine(steamPath, "steamapps");
        if (Directory.Exists(mainSteamApps))
            folders.Add(mainSteamApps);

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
                System.Diagnostics.Debug.WriteLine($"Error parsing libraryfolders.vdf: {ex.Message}");
            }
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
            var games = new List<GameInfo>();
            var libraryFolders = GetLibraryFolders();

            foreach (var folder in libraryFolders)
            {
                var manifestFiles = Directory.GetFiles(folder, "appmanifest_*.acf");
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
                        System.Diagnostics.Debug.WriteLine($"Error parsing manifest {manifestPath}: {ex.Message}");
                    }
                }
            }

            return games.OrderBy(g => g.Name).ToList();
        });
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

        return new GameInfo
        {
            AppId = appId,
            Name = name,
            InstallPath = installPath,
            BuildId = buildId ?? "unknown",
            SizeOnDisk = sizeOnDisk,
            LastUpdated = lastUpdated,
            Platform = GamePlatform.Steam
        };
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
