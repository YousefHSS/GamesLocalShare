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
    /// Loads settings from disk, or returns defaults if file doesn't exist
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                return settings ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex.Message}");
        }

        return new AppSettings();
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
            }

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
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
