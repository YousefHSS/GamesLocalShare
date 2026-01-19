using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GamesLocalShare.Models;

/// <summary>
/// Represents an item in the download queue
/// </summary>
public class DownloadQueueItem : INotifyPropertyChanged
{
    private DownloadQueueItemType _type;
    private string _gameName = string.Empty;
    private string _gameAppId = string.Empty;
    private string _sourcePeerName = string.Empty;
    private long _totalBytes;
    private long _downloadedBytes;
    private DownloadQueueStatus _status = DownloadQueueStatus.Queued;
    private double _progress;

    /// <summary>
    /// Type of download (Update or Incomplete)
    /// </summary>
    public DownloadQueueItemType Type
    {
        get => _type;
        set { _type = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Game name
    /// </summary>
    public string GameName
    {
        get => _gameName;
        set { _gameName = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Game AppId
    /// </summary>
    public string GameAppId
    {
        get => _gameAppId;
        set { _gameAppId = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Name of the peer providing the game
    /// </summary>
    public string SourcePeerName
    {
        get => _sourcePeerName;
        set { _sourcePeerName = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Total bytes to download
    /// </summary>
    public long TotalBytes
    {
        get => _totalBytes;
        set { _totalBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedSize)); }
    }

    /// <summary>
    /// Bytes already downloaded (for incomplete transfers)
    /// </summary>
    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set { _downloadedBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedProgress)); }
    }

    /// <summary>
    /// Current status of the item
    /// </summary>
    public DownloadQueueStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusColor)); }
    }

    /// <summary>
    /// Download progress (0-100)
    /// </summary>
    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedProgress)); }
    }

    /// <summary>
    /// Reference to the sync info (for updates)
    /// </summary>
    public GameSyncInfo? SyncInfo { get; set; }

    /// <summary>
    /// Reference to the transfer state (for incomplete transfers)
    /// </summary>
    public TransferState? TransferState { get; set; }

    /// <summary>
    /// Formatted size display
    /// </summary>
    public string FormattedSize => FormatBytes(TotalBytes);

    /// <summary>
    /// Formatted progress display
    /// </summary>
    public string FormattedProgress => $"{Progress:0.0}% ({FormatBytes(DownloadedBytes)} / {FormattedSize})";

    /// <summary>
    /// Status text for display
    /// </summary>
    public string StatusText => Status switch
    {
        DownloadQueueStatus.Queued => "Queued",
        DownloadQueueStatus.Downloading => "Downloading",
        DownloadQueueStatus.Completed => "Completed",
        DownloadQueueStatus.Failed => "Failed",
        DownloadQueueStatus.Paused => "Paused",
        _ => "Unknown"
    };

    /// <summary>
    /// Status color for display
    /// </summary>
    public string StatusColor => Status switch
    {
        DownloadQueueStatus.Queued => "#6B7280",
        DownloadQueueStatus.Downloading => "#3B82F6",
        DownloadQueueStatus.Completed => "#10B981",
        DownloadQueueStatus.Failed => "#EF4444",
        DownloadQueueStatus.Paused => "#F59E0B",
        _ => "#6B7280"
    };

    /// <summary>
    /// Type icon for display
    /// </summary>
    public string TypeIcon => Type switch
    {
        DownloadQueueItemType.Update => "🔄",
        DownloadQueueItemType.Incomplete => "⏸️",
        DownloadQueueItemType.NewDownload => "⬇️",
        _ => "📦"
    };

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

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Type of download queue item
/// </summary>
public enum DownloadQueueItemType
{
    Update,
    Incomplete,
    NewDownload
}

/// <summary>
/// Status of a download queue item
/// </summary>
public enum DownloadQueueStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Paused
}
