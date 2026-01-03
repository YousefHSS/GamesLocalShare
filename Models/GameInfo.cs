using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace GamesLocalShare.Models;

/// <summary>
/// Represents information about an installed game
/// </summary>
public class GameInfo : INotifyPropertyChanged
{
    private ImageSource? _coverImage;

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
    /// Whether this game is hidden from peers (not shared on network)
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (_isHidden != value)
            {
                _isHidden = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isHidden = false;

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

    /// <summary>
    /// Runtime-only cover image to display in the UI. Not serialized.
    /// Notifies UI when the image is loaded.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ImageSource? CoverImage
    {
        get => _coverImage;
        set
        {
            if (_coverImage != value)
            {
                _coverImage = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum GamePlatform
{
    Steam,
    EpicGames,
    Xbox
}
