namespace GamesLocalShare.Models;

/// <summary>
/// Represents information about an installed game
/// </summary>
public class GameInfo
{
    /// <summary>
    /// Unique identifier for the game (Steam AppId)
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the game
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the game installation directory
    /// </summary>
    public string InstallPath { get; set; } = string.Empty;

    /// <summary>
    /// Size of the game in bytes
    /// </summary>
    public long SizeOnDisk { get; set; }

    /// <summary>
    /// Last time the game was updated (manifest timestamp)
    /// </summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// Build ID / Version identifier from Steam
    /// </summary>
    public string BuildId { get; set; } = string.Empty;

    /// <summary>
    /// The platform this game is from (Steam, Epic, Xbox)
    /// </summary>
    public GamePlatform Platform { get; set; } = GamePlatform.Steam;

    /// <summary>
    /// Whether this game is fully installed or just available on peer
    /// </summary>
    public bool IsInstalled { get; set; } = true;

    /// <summary>
    /// Whether this is a new game available from a peer (not in local library)
    /// </summary>
    public bool IsAvailableFromPeer { get; set; } = false;

    /// <summary>
    /// Formatted size for display
    /// </summary>
    public string FormattedSize => FormatBytes(SizeOnDisk);

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}

public enum GamePlatform
{
    Steam,
    EpicGames,
    Xbox
}
