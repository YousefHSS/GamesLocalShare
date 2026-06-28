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
    private readonly TitleCoverArtService _coverArt = new();
    private readonly SteamGridDbCoverService _sgdb = new();
    private readonly WikipediaCoverService _wiki = new();

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
                            // An external copy carrying a numeric Steam appid gets the same
                            // deterministic Steam CDN cover as a real Steam-library install.
                            // Without this it would fall to the Steam scanner's bitmap-only
                            // cover path (CoverImage), which the WebUI — which renders CoverUrl —
                            // never shows, so the copy appeared cover-less.
                            CoverUrl = platform == GamePlatform.Steam && int.TryParse(meta.AppId, out var steamId)
                                ? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steamId}/header.jpg"
                                : null,
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

    public async Task LoadCoverImageAsync(GameInfo game)
    {
        // External (non-store) games have no local art, so resolve a cover online by title —
        // the same high-confidence matcher used for Xbox/Epic titles. Messy folder names are
        // cleaned before searching; an unconfident match stays blank.
        await _coverArt.LoadAsync(game);

        // Steam/Epic don't carry console/EA/Konami/retro catalogs. For titles still missing a
        // cover, fall back to broader sources (same confidence guard on every result):
        //   1. SteamGridDB — best game-shaped art, but only when a (free, optional) key is set.
        //   2. Wikipedia — keyless: the article lead image, no key or hosting required.
        if (string.IsNullOrEmpty(game.CoverUrl))
        {
            var searchTitle = TitleCoverArtService.CleanSearchTitle(game.Name);
            string? url = null;
            if (!string.IsNullOrWhiteSpace(_settings.SteamGridDbApiKey))
                url = await _sgdb.ResolveCoverAsync(searchTitle, _settings.SteamGridDbApiKey!);
            if (string.IsNullOrEmpty(url))
                url = await _wiki.ResolveCoverAsync(searchTitle);
            if (!string.IsNullOrEmpty(url))
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => game.CoverUrl = url);
        }
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
