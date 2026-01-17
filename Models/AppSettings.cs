using System.IO;
using System.Text.Json;

namespace GamesLocalShare.Models;

/// <summary>
/// Application settings that persist across sessions
/// </summary>
public class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GamesLocalShare",
        "settings.json");

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
                System.Diagnostics.Debug.WriteLine("Returning cached settings instance");
                return _instance;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"Loading settings from: {SettingsPath}");
                
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    System.Diagnostics.Debug.WriteLine($"? Settings file found, content: {json}");
                    
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"? Settings loaded successfully");
                        _instance = settings;
                        return _instance;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"? Settings deserialized to null, using defaults");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"? Settings file not found, using defaults");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"? ERROR loading settings: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"  Stack: {ex.StackTrace}");
            }

            System.Diagnostics.Debug.WriteLine($"Creating new default settings instance");
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
    /// Saves settings to disk
    /// </summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                System.Diagnostics.Debug.WriteLine($"Created settings directory: {directory}");
            }

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(SettingsPath, json);
            System.Diagnostics.Debug.WriteLine($"? Settings saved successfully to: {SettingsPath}");
            System.Diagnostics.Debug.WriteLine($"  Settings content: {json}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"? ERROR saving settings to {SettingsPath}");
            System.Diagnostics.Debug.WriteLine($"  Exception: {ex.GetType().Name}");
            System.Diagnostics.Debug.WriteLine($"  Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"  Stack: {ex.StackTrace}");
            
            // Try to get more details about the error
            if (ex is UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"  ? Access denied - check folder permissions");
            }
            else if (ex is IOException)
            {
                System.Diagnostics.Debug.WriteLine($"  ? IO error - file may be locked");
            }
        }
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
}
