namespace GamesLocalShare.Models;

/// <summary>
/// Represents a potential sync operation between local and remote game versions
/// </summary>
public class GameSyncInfo
{
    /// <summary>
    /// The game being synced (can be null for new downloads)
    /// </summary>
    public GameInfo? LocalGame { get; set; }

    /// <summary>
    /// The remote version of the game
    /// </summary>
    public GameInfo RemoteGame { get; set; } = null!;

    /// <summary>
    /// The peer that has the game
    /// </summary>
    public NetworkPeer RemotePeer { get; set; } = null!;

    /// <summary>
    /// Whether this is a new game download (not installed locally)
    /// </summary>
    public bool IsNewDownload => LocalGame == null || !LocalGame.IsInstalled;

    /// <summary>
    /// Whether the local version is older than remote
    /// </summary>
    public bool LocalIsOlder => LocalGame == null || 
                                 string.Compare(LocalGame.BuildId, RemoteGame.BuildId, StringComparison.Ordinal) < 0
                                 || LocalGame.LastUpdated < RemoteGame.LastUpdated;

    /// <summary>
    /// Whether the remote version is older than local
    /// </summary>
    public bool RemoteIsOlder => LocalGame != null && !LocalIsOlder && LocalGame.BuildId != RemoteGame.BuildId;

    /// <summary>
    /// Current sync status
    /// </summary>
    public SyncStatus Status { get; set; } = SyncStatus.Pending;

    /// <summary>
    /// Progress percentage (0-100)
    /// </summary>
    public double Progress { get; set; }

    /// <summary>
    /// Transfer speed in bytes per second
    /// </summary>
    public long TransferSpeed { get; set; }

    /// <summary>
    /// Formatted transfer speed
    /// </summary>
    public string FormattedSpeed => $"{FormatBytes(TransferSpeed)}/s";

    /// <summary>
    /// Display name for the sync operation
    /// </summary>
    public string DisplayName => RemoteGame.Name;

    /// <summary>
    /// Description of what this sync will do
    /// </summary>
    public string SyncDescription => IsNewDownload 
        ? "New Download" 
        : $"Update: {LocalGame?.BuildId} -> {RemoteGame.BuildId}";

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
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

public enum SyncStatus
{
    Pending,
    Syncing,
    Completed,
    Failed,
    Cancelled,
    Incomplete  // For resumed downloads
}
