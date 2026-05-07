using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

public class ExternalFolderScanner : IGameLibraryScanner
{
    public GamePlatform Platform => GamePlatform.External;

    private readonly List<string> _scanErrors = [];
    public IReadOnlyList<string> ScanErrors => _scanErrors;

    private readonly AppSettings _settings;

    private static readonly JsonSerializerOptions MetaJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ExternalFolderScanner(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<List<GameInfo>> ScanGamesAsync()
    {
        return await Task.Run(() =>
        {
            _scanErrors.Clear();
            var games = new List<GameInfo>();

            foreach (var lib in _settings.ExternalLibraries)
            {
                if (!Directory.Exists(lib.RootPath))
                {
                    _scanErrors.Add($"Library '{lib.DisplayName}' path not found: {lib.RootPath}");
                    continue;
                }

                try
                {
                    var steamappsPath = Path.Combine(lib.RootPath, "steamapps");
                    if (Directory.Exists(steamappsPath))
                    {
                        ScanSteamLibrary(steamappsPath, games);
                    }
                    else
                    {
                        // Generic mode: enumerate top-level subfolders. Skip Windows
                        // system folders, and recurse into nested Steam libraries
                        // (any subfolder that itself contains a steamapps directory).
                        foreach (var dir in Directory.GetDirectories(lib.RootPath))
                        {
                            if (IsSystemFolder(dir)) continue;

                            var nestedSteamapps = Path.Combine(dir, "steamapps");
                            if (Directory.Exists(nestedSteamapps))
                            {
                                ScanSteamLibrary(nestedSteamapps, games);
                                continue;
                            }

                            var game = ScanGenericFolder(dir);
                            if (game != null) games.Add(game);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _scanErrors.Add($"Error scanning library '{lib.DisplayName}': {ex.Message}");
                }
            }

            return games.OrderBy(g => g.Name).ToList();
        });
    }

    private static readonly HashSet<string> SystemFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN",
        "System Volume Information",
        "$WinREAgent",
        "Recovery",
        "Config.Msi",
        "PerfLogs",
        "ProgramData",
    };

    private static bool IsSystemFolder(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name)) return false;
        if (SystemFolderNames.Contains(name)) return true;
        try
        {
            var attrs = new DirectoryInfo(dir).Attributes;
            // Hidden + System combination is the strongest signal for OS-managed folders.
            if ((attrs & FileAttributes.System) != 0 && (attrs & FileAttributes.Hidden) != 0) return true;
        }
        catch { }
        return false;
    }

    private void ScanSteamLibrary(string steamappsPath, List<GameInfo> games)
    {
        try
        {
            foreach (var acf in Directory.GetFiles(steamappsPath, "appmanifest_*.acf"))
            {
                try
                {
                    var game = SteamManifestParser.ParseAppManifest(acf, steamappsPath, _scanErrors);
                    if (game != null)
                    {
                        game.IsExternal = true;
                        games.Add(game);
                    }
                }
                catch (Exception ex)
                {
                    _scanErrors.Add($"Error parsing {Path.GetFileName(acf)}: {ex.Message}");
                }
            }

            var commonPath = Path.Combine(steamappsPath, "common");
            if (Directory.Exists(commonPath))
            {
                foreach (var dir in Directory.GetDirectories(commonPath))
                {
                    if (games.Any(g => g.InstallPath.Equals(dir, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var game = ScanGenericFolder(dir);
                    if (game != null) games.Add(game);
                }
            }
        }
        catch (Exception ex)
        {
            _scanErrors.Add($"Error scanning Steam library at {steamappsPath}: {ex.Message}");
        }
    }

    private GameInfo? ScanGenericFolder(string dir)
    {
        try
        {
            var dirInfo = new DirectoryInfo(dir);
            long size = 0;
            try
            {
                size = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch { }

            // Check for sidecar written by IncrementalSyncEngine after a successful copy
            var metaPath = Path.Combine(dir, ".gamesync_meta");
            if (File.Exists(metaPath))
            {
                try
                {
                    var json = File.ReadAllText(metaPath);
                    var meta = JsonSerializer.Deserialize<SyncMeta>(json, MetaJsonOptions);
                    if (meta != null && !string.IsNullOrEmpty(meta.AppId))
                    {
                        var platform = Enum.TryParse<GamePlatform>(meta.Platform, ignoreCase: true, out var p)
                            ? p : GamePlatform.External;
                        return new GameInfo
                        {
                            AppId = meta.AppId,
                            Name = dirInfo.Name,
                            InstallPath = dir,
                            BuildId = meta.BuildId ?? string.Empty,
                            SizeOnDisk = size,
                            LastUpdated = string.IsNullOrEmpty(meta.LastUpdated)
                                ? dirInfo.LastWriteTimeUtc.ToLocalTime()
                                : DateTime.Parse(meta.LastUpdated, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime(),
                            Platform = platform,
                            IsInstalled = true,
                            IsExternal = true,
                        };
                    }
                }
                catch (Exception ex)
                {
                    _scanErrors.Add($"Error reading .gamesync_meta in {dir}: {ex.Message}");
                }
            }

            // No sidecar — use folder name and directory mtime
            return new GameInfo
            {
                AppId = "ext:" + ComputePathId(dir),
                Name = dirInfo.Name,
                InstallPath = dir,
                BuildId = string.Empty,
                SizeOnDisk = size,
                LastUpdated = dirInfo.LastWriteTimeUtc.ToLocalTime(),
                Platform = GamePlatform.External,
                IsInstalled = true,
                IsExternal = true,
            };
        }
        catch (Exception ex)
        {
            _scanErrors.Add($"Error scanning {dir}: {ex.Message}");
            return null;
        }
    }

    public List<string> GetLibraryFolders()
    {
        return _settings.ExternalLibraries
            .Where(lib => Directory.Exists(lib.RootPath))
            .Select(lib => lib.RootPath)
            .ToList();
    }

    public Task LoadCoverImageAsync(GameInfo game)
    {
        return Task.CompletedTask;
    }

    private static string ComputePathId(string path)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..12];
    }

    private sealed class SyncMeta
    {
        public string? AppId { get; set; }
        public string? BuildId { get; set; }
        public string? LastUpdated { get; set; }
        public string? Platform { get; set; }
    }
}
