using System.IO;
using System.Text.Json;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

/// <summary>
/// Scans Epic Games Launcher manifests to detect installed games.
/// Windows-only for now; Linux/Heroic support is stubbed for the future.
/// </summary>
public class EpicGamesLibraryScanner : IGameLibraryScanner
{
    private readonly List<string> _scanErrors = [];
    private readonly TitleCoverArtService _coverArt = new();

    public GamePlatform Platform => GamePlatform.EpicGames;

    public IReadOnlyList<string> ScanErrors => _scanErrors;

    /// <summary>
    /// Returns the directory containing Epic's per-game *.item manifest files,
    /// or null if Epic is not installed / not supported on this OS.
    /// </summary>
    public string? GetManifestsDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var dir = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
            return Directory.Exists(dir) ? dir : null;
        }

        // TODO: Linux (Heroic / Legendary) and macOS support.
        return null;
    }

    /// <summary>
    /// Returns distinct parent folders of installed Epic games. Used to
    /// auto-detect a default install root for incoming transfers.
    /// </summary>
    public List<string> GetLibraryFolders()
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestsDir = GetManifestsDirectory();
        if (manifestsDir == null)
            return new List<string>();

        foreach (var item in EnumerateManifests(manifestsDir))
        {
            if (string.IsNullOrEmpty(item.InstallLocation))
                continue;

            var parent = Path.GetDirectoryName(item.InstallLocation.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                folders.Add(parent);
        }

        return folders.ToList();
    }

    public async Task<List<GameInfo>> ScanGamesAsync()
    {
        return await Task.Run(() =>
        {
            _scanErrors.Clear();
            var games = new List<GameInfo>();
            var manifestsDir = GetManifestsDirectory();

            if (manifestsDir == null)
            {
                if (OperatingSystem.IsWindows())
                    _scanErrors.Add("Epic Games manifests directory not found");
                return games;
            }

            foreach (var item in EnumerateManifests(manifestsDir))
            {
                if (string.IsNullOrWhiteSpace(item.AppName) ||
                    string.IsNullOrWhiteSpace(item.DisplayName) ||
                    string.IsNullOrWhiteSpace(item.InstallLocation))
                {
                    continue;
                }

                // Skip non-game entries (engines, plugins, etc.)
                if (item.AppCategories != null &&
                    !item.AppCategories.Any(c => string.Equals(c, "games", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!Directory.Exists(item.InstallLocation))
                {
                    _scanErrors.Add($"Skipping {item.DisplayName} - install dir missing: {item.InstallLocation}");
                    continue;
                }

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(item.InstallLocation).Any())
                    {
                        _scanErrors.Add($"Skipping {item.DisplayName} - install dir empty");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _scanErrors.Add($"Skipping {item.DisplayName} - {ex.Message}");
                    continue;
                }

                var size = item.InstallSize > 0 ? item.InstallSize : SafeDirectorySize(item.InstallLocation);
                var lastUpdated = SafeLastWrite(item.InstallLocation);

                var game = new GameInfo
                {
                    AppId = item.CatalogItemId ?? item.AppName!,
                    Name = item.DisplayName!,
                    InstallPath = item.InstallLocation!,
                    BuildId = item.AppVersionString ?? "unknown",
                    SizeOnDisk = size,
                    LastUpdated = lastUpdated,
                    Platform = GamePlatform.EpicGames,
                    IsInstalled = true,
                };

                games.Add(game);
                _ = LoadCoverImageAsync(game);
            }

            return games.OrderBy(g => g.Name).ToList();
        });
    }

    public Task LoadCoverImageAsync(GameInfo game) => _coverArt.LoadAsync(game);

    private IEnumerable<EpicManifestItem> EnumerateManifests(string manifestsDir)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(manifestsDir, "*.item");
        }
        catch (Exception ex)
        {
            _scanErrors.Add($"Cannot list Epic manifests: {ex.Message}");
            yield break;
        }

        foreach (var path in files)
        {
            EpicManifestItem? item = null;
            try
            {
                var json = File.ReadAllText(path);
                item = JsonSerializer.Deserialize<EpicManifestItem>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                _scanErrors.Add($"Error parsing {Path.GetFileName(path)}: {ex.Message}");
            }

            if (item != null)
                yield return item;
        }
    }

    private static long SafeDirectorySize(string path)
    {
        try
        {
            long total = 0;
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
                total += f.Length;
            return total;
        }
        catch { return 0; }
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return Directory.GetLastWriteTime(path); }
        catch { return DateTime.MinValue; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class EpicManifestItem
    {
        public string? AppName { get; set; }
        public string? DisplayName { get; set; }
        public string? InstallLocation { get; set; }
        public string? CatalogItemId { get; set; }
        public string? CatalogNamespace { get; set; }
        public string? AppVersionString { get; set; }
        public long InstallSize { get; set; }
        public List<string>? AppCategories { get; set; }
    }
}
