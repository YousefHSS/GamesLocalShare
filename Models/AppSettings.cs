using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamesLocalShare.Models;

/// <summary>
/// JSON serialization context to support trimming/AOT
/// </summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<ExternalLibrary>))]
[JsonSerializable(typeof(ExternalLibrary))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class AppSettingsJsonContext : JsonSerializerContext
{
}

/// <summary>Preferred Xbox game transfer method (drive or network).</summary>
public enum XboxTransferMethod
{
    /// <summary>Smart when the game qualifies, otherwise Basic (with a warning). Recommended default.</summary>
    Auto = 0,
    /// <summary>Reconstruct the genuine package — game stays updatable; needs a capture.</summary>
    Smart = 1,
    /// <summary>Overlay — works on any install but the game can't be updated afterward.</summary>
    Basic = 2,
}

/// <summary>How much CPU a skeleton capture may consume (trades speed for system responsiveness).</summary>
public enum CaptureCpuLimit
{
    /// <summary>Full speed, normal priority — fastest capture, uses a full core.</summary>
    Full = 0,
    /// <summary>Below-normal priority + light throttle — stays responsive (default).</summary>
    Balanced = 1,
    /// <summary>Idle priority + heavier throttle — slowest, minimal CPU impact.</summary>
    Low = 2,
}

/// <summary>Maps a <see cref="CaptureCpuLimit"/> to the xvdtool per-chunk throttle (ms) and process priority.</summary>
public static class CaptureCpuLimitExtensions
{
    public static int ThrottleMs(this CaptureCpuLimit l) => l switch
    {
        CaptureCpuLimit.Balanced => 2,
        CaptureCpuLimit.Low => 8,
        _ => 0,
    };

    public static System.Diagnostics.ProcessPriorityClass Priority(this CaptureCpuLimit l) => l switch
    {
        CaptureCpuLimit.Balanced => System.Diagnostics.ProcessPriorityClass.BelowNormal,
        CaptureCpuLimit.Low => System.Diagnostics.ProcessPriorityClass.Idle,
        _ => System.Diagnostics.ProcessPriorityClass.Normal,
    };
}

/// <summary>
/// Application settings that persist across sessions
/// </summary>
public class AppSettings
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GamesLocalShare");
    
    private static readonly string SettingsPath = Path.Combine(SettingsFolder, "settings.json");
    private static readonly string SettingsBackupPath = Path.Combine(SettingsFolder, "settings.txt");

    private static AppSettings? _instance;
    private static readonly object _lock = new object();

    /// <summary>
    /// Whether to start the application with Windows
    /// </summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Whether to automatically check for and download game updates
    /// </summary>
    public bool AutoUpdateGames { get; set; }

    /// <summary>
    /// List of game AppIds that are hidden from peers
    /// </summary>
    public HashSet<string> HiddenGameIds { get; set; } = [];

    /// <summary>
    /// Whether to minimize to system tray on close
    /// </summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>
    /// Whether to start network automatically on startup
    /// </summary>
    public bool AutoStartNetwork { get; set; }

    /// <summary>
    /// Interval in minutes to check for game updates (default: 30)
    /// </summary>
    public int AutoUpdateCheckInterval { get; set; } = 30;

    /// <summary>
    /// Whether to automatically resume all pending downloads on startup
    /// </summary>
    public bool AutoResumeDownloads { get; set; }

    /// <summary>
    /// Default install folder for incoming Epic Games transfers (where game
    /// directories are placed). When null/empty the receiver auto-detects from
    /// existing Epic installs at transfer time.
    /// </summary>
    public string? EpicInstallRoot { get; set; }

    /// <summary>
    /// Default path for Xbox game staged transfers (where sender output is stored).
    /// Used by the Xbox transfer wizard as the default browse location.
    /// </summary>
    public string? XboxStagePath { get; set; }

    /// <summary>
    /// Xbox install root override (e.g. "D:\XboxGames"). When set, the overlay
    /// script looks for paused installs here instead of the default location.
    /// </summary>
    public string? XboxRootPath { get; set; }

    /// <summary>
    /// Root folder searched (recursively) for cached encrypted Xbox packages
    /// (&lt;PackageFullName&gt;.msixvc). The skeleton-capture watcher uses it to
    /// auto-locate a detected title's package. When null/empty it defaults to the
    /// application's own program folder.
    /// </summary>
    public string? XboxPackageCacheRoot { get; set; }

    /// <summary>
    /// Path to the CikExtractor tool (its repo root or CikExtractor.exe). The
    /// watcher invokes it on demand (elevated) to populate the CIK store used by
    /// skeleton capture. When null/empty, capture relies on a pre-populated store.
    /// </summary>
    public string? CikExtractorPath { get; set; }

    /// <summary>
    /// Whether to automatically start the Xbox single-copy engine (skeleton-capture watcher + LAN cache
    /// proxy) on launch, so Xbox games captured automatically with no manual Start Watching / Start Proxy.
    /// Defaults to true. Disable to require the manual toggles in the Skeletons panel.
    /// </summary>
    public bool XboxSingleCopyAutoStart { get; set; } = true;

    /// <summary>
    /// Preferred method for transferring Xbox games (drive or network). <see cref="XboxTransferMethod.Smart"/>
    /// reconstructs the genuine package so the game stays updatable (needs a capture); <see cref="XboxTransferMethod.Basic"/>
    /// uses the overlay (works on any install but the game won't update); <see cref="XboxTransferMethod.Auto"/>
    /// (default) uses Smart when available and falls back to Basic with a warning.
    /// </summary>
    public XboxTransferMethod XboxTransferMethod { get; set; } = XboxTransferMethod.Auto;

    /// <summary>
    /// How much CPU skeleton capture may use. <see cref="CaptureCpuLimit.Full"/> runs at full speed;
    /// <see cref="CaptureCpuLimit.Balanced"/> (default) runs at below-normal priority and throttles the
    /// hash/decrypt loops so the machine stays responsive; <see cref="CaptureCpuLimit.Low"/> throttles
    /// harder at idle priority (slower capture, minimal CPU impact).
    /// </summary>
    public CaptureCpuLimit CaptureCpuLimit { get; set; } = CaptureCpuLimit.Balanced;

    /// <summary>
    /// Whether the watcher automatically captures skeletons for installed titles whose package is already in
    /// the cache but which have no skeleton yet (the startup + periodic sweep). Default true. When false,
    /// existing cached packages are only captured on demand via the "Capture from Cache" action; capturing a
    /// game as it installs is unaffected.
    /// </summary>
    public bool CaptureFromCache { get; set; } = true;

    /// <summary>
    /// EXPERIMENTAL. When true, the LAN cache proxy captures a title's skeleton by <b>streaming</b> the install
    /// download — decrypting and classifying pages as they pass through the proxy so the full encrypted package
    /// is never written to disk (avoids the download-time 2× storage peak). Requires the matching CIK to already
    /// be in the store when the download starts; when it isn't (or anything goes wrong) the proxy falls back to
    /// the normal sparse-.part tee + capture-after-install, so this is strictly a best-effort optimization with
    /// no effect on the download. Default false (off) — the proven tee path is unchanged until enabled.
    /// </summary>
    public bool XboxStreamingCapture { get; set; }

    /// <summary>
    /// List of external drive libraries to scan for games
    /// </summary>
    public List<ExternalLibrary> ExternalLibraries { get; set; } = [];

    /// <summary>
    /// Optional SteamGridDB API key. When set, External (non-store) games whose cover can't
    /// be found on Steam/Epic are looked up on SteamGridDB. Null/empty disables that source.
    /// </summary>
    public string? SteamGridDbApiKey { get; set; }

    /// <summary>
    /// Loads settings from disk, or returns defaults if file doesn't exist.
    /// Returns a singleton instance to ensure all parts of the app use the same settings.
    /// </summary>
    public static AppSettings Load()
    {
        lock (_lock)
        {
            if (_instance != null)
            {
                return _instance;
            }

            try
            {
                // Ensure directory exists
                if (!Directory.Exists(SettingsFolder))
                {
                    Directory.CreateDirectory(SettingsFolder);
                }

                // Try to load from JSON first
                if (File.Exists(SettingsPath))
                {
                    try
                    {
                        var json = File.ReadAllText(SettingsPath);
                        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                        if (settings != null)
                        {
                            _instance = settings;
                            return _instance;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"JSON load failed: {ex.Message}");
                    }
                }

                // Fallback: try to load from text file
                if (File.Exists(SettingsBackupPath))
                {
                    try
                    {
                        var settings = LoadFromTextFile();
                        if (settings != null)
                        {
                            _instance = settings;
                            // Re-save as JSON
                            _instance.Save();
                            return _instance;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Text file load failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Settings load error: {ex.Message}");
            }

            _instance = new AppSettings();
            return _instance;
        }
    }

    /// <summary>
    /// Reloads settings from disk, replacing the cached instance
    /// </summary>
    public static void Reload()
    {
        lock (_lock)
        {
            _instance = null;
            Load();
        }
    }

    /// <summary>
    /// Saves settings to disk (both JSON and text backup)
    /// </summary>
    public void Save()
    {
        try
        {
            // Ensure directory exists
            if (!Directory.Exists(SettingsFolder))
            {
                Directory.CreateDirectory(SettingsFolder);
            }

            // Save as JSON using source-generated serializer
            var json = JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings);
            File.WriteAllText(SettingsPath, json);

            // Also save as plain text backup (guaranteed to work with trimming)
            SaveToTextFile();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Settings save error: {ex.Message}");
            
            // Try text-only save as fallback
            try
            {
                SaveToTextFile();
            }
            catch (Exception ex2)
            {
                System.Diagnostics.Debug.WriteLine($"Text backup save also failed: {ex2.Message}");
            }
        }
    }

    /// <summary>
    /// Saves settings to a plain text file (fallback for trimmed builds)
    /// </summary>
    private void SaveToTextFile()
    {
        var lines = new List<string>
        {
            $"StartWithWindows={StartWithWindows}",
            $"AutoUpdateGames={AutoUpdateGames}",
            $"MinimizeToTray={MinimizeToTray}",
            $"AutoStartNetwork={AutoStartNetwork}",
            $"AutoUpdateCheckInterval={AutoUpdateCheckInterval}",
            $"AutoResumeDownloads={AutoResumeDownloads}",
            $"HiddenGameIds={string.Join(",", HiddenGameIds)}",
            $"EpicInstallRoot={EpicInstallRoot ?? string.Empty}",
            $"XboxPackageCacheRoot={XboxPackageCacheRoot ?? string.Empty}",
            $"CikExtractorPath={CikExtractorPath ?? string.Empty}",
            $"XboxSingleCopyAutoStart={XboxSingleCopyAutoStart}",
            $"XboxTransferMethod={XboxTransferMethod}",
            $"XboxStreamingCapture={XboxStreamingCapture}"
        };
        for (int i = 0; i < ExternalLibraries.Count; i++)
        {
            var lib = ExternalLibraries[i];
            lines.Add($"ExternalLibrary.{i}={lib.Id}|{lib.DisplayName}|{lib.RootPath}|{lib.DriveSerial}|{lib.IsRemovable}|{lib.ScanSubfolders}");
        }
        File.WriteAllLines(SettingsBackupPath, lines);
    }

    /// <summary>
    /// Loads settings from a plain text file
    /// </summary>
    private static AppSettings? LoadFromTextFile()
    {
        if (!File.Exists(SettingsBackupPath))
            return null;

        var settings = new AppSettings();
        var lines = File.ReadAllLines(SettingsBackupPath);
        
        foreach (var line in lines)
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim();
            var value = parts[1].Trim();

            switch (key)
            {
                case "StartWithWindows":
                    settings.StartWithWindows = bool.TryParse(value, out var sw) && sw;
                    break;
                case "AutoUpdateGames":
                    settings.AutoUpdateGames = bool.TryParse(value, out var aug) && aug;
                    break;
                case "MinimizeToTray":
                    settings.MinimizeToTray = bool.TryParse(value, out var mtt) && mtt;
                    break;
                case "AutoStartNetwork":
                    settings.AutoStartNetwork = bool.TryParse(value, out var asn) && asn;
                    break;
                case "AutoUpdateCheckInterval":
                    settings.AutoUpdateCheckInterval = int.TryParse(value, out var auci) ? auci : 30;
                    break;
                case "AutoResumeDownloads":
                    settings.AutoResumeDownloads = bool.TryParse(value, out var ard) && ard;
                    break;
                case "EpicInstallRoot":
                    settings.EpicInstallRoot = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;
                case "XboxPackageCacheRoot":
                    settings.XboxPackageCacheRoot = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;
                case "CikExtractorPath":
                    settings.CikExtractorPath = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;
                case "XboxSingleCopyAutoStart":
                    settings.XboxSingleCopyAutoStart = !bool.TryParse(value, out var scas) || scas;
                    break;
                case "XboxTransferMethod":
                    settings.XboxTransferMethod = Enum.TryParse<XboxTransferMethod>(value, out var xtm)
                        ? xtm : XboxTransferMethod.Auto;
                    break;
                case "XboxStreamingCapture":
                    settings.XboxStreamingCapture = bool.TryParse(value, out var xsc) && xsc;
                    break;
                case "HiddenGameIds":
                    if (!string.IsNullOrEmpty(value))
                    {
                        settings.HiddenGameIds = new HashSet<string>(value.Split(',', StringSplitOptions.RemoveEmptyEntries));
                    }
                    break;
                default:
                    if (key.StartsWith("ExternalLibrary.", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts2 = value.Split('|');
                        if (parts2.Length >= 6 && Guid.TryParse(parts2[0], out var libId))
                        {
                            settings.ExternalLibraries.Add(new ExternalLibrary
                            {
                                Id = libId,
                                DisplayName = parts2[1],
                                RootPath = parts2[2],
                                DriveSerial = parts2[3],
                                IsRemovable = bool.TryParse(parts2[4], out var ir) && ir,
                                ScanSubfolders = !bool.TryParse(parts2[5], out var ss) || ss,
                            });
                        }
                    }
                    break;
            }
        }

        return settings;
    }

    /// <summary>
    /// Checks if a game is hidden from peers
    /// </summary>
    public bool IsGameHidden(string appId)
    {
        return HiddenGameIds.Contains(appId);
    }

    /// <summary>
    /// Hides a game from peers
    /// </summary>
    public void HideGame(string appId)
    {
        HiddenGameIds.Add(appId);
        Save();
    }

    /// <summary>
    /// Un-hides a game from peers
    /// </summary>
    public void UnhideGame(string appId)
    {
        HiddenGameIds.Remove(appId);
        Save();
    }

    /// <summary>
    /// Gets the settings file path (for display in UI)
    /// </summary>
    public static string GetSettingsFilePath() => SettingsPath;

    /// <summary>
    /// Per-user default folder for the Xbox LAN cache / package cache, used when
    /// <see cref="XboxPackageCacheRoot"/> isn't set. Lives under LocalApplicationData so it
    /// exists on any machine (replaces the old hardcoded <c>F:\xbox-cache</c>).
    /// </summary>
    public static string DefaultXboxCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamesLocalShare", "xbox-cache");
}
