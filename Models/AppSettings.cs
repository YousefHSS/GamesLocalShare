using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamesLocalShare.Models;

/// <summary>
/// JSON serialization context to support trimming/AOT
/// </summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class AppSettingsJsonContext : JsonSerializerContext
{
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
            $"HiddenGameIds={string.Join(",", HiddenGameIds)}"
        };
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
                case "HiddenGameIds":
                    if (!string.IsNullOrEmpty(value))
                    {
                        settings.HiddenGameIds = new HashSet<string>(value.Split(',', StringSplitOptions.RemoveEmptyEntries));
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
}
