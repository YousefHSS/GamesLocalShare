namespace GamesLocalShare.Models;

/// <summary>
/// Represents a potential sync operation between local and remote game versions
/// </summary>
public class GameSyncInfo
{
    /// <summary>
    /// The game being synced
    /// </summary>
    public GameInfo LocalGame { get; set; } = null!;

    /// <summary>
    /// The remote version of the game
    /// </summary>
    public GameInfo RemoteGame { get; set; } = null!;

    /// <summary>
    /// The peer that has the game
    /// </summary>
    public NetworkPeer RemotePeer { get; set; } = null!;

    /// <summary>
    /// Whether the local version is older than remote
    /// </summary>
    public bool LocalIsOlder => string.Compare(LocalGame.BuildId, RemoteGame.BuildId, StringComparison.Ordinal) < 0
                                 || LocalGame.LastUpdated < RemoteGame.LastUpdated;

    /// <summary>
    /// Whether the remote version is older than local
    /// </summary>
    public bool RemoteIsOlder => !LocalIsOlder && LocalGame.BuildId != RemoteGame.BuildId;

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
    Cancelled
}
