using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace GamesLocalShare.Models;

/// <summary>
/// Represents information about an installed game
/// </summary>
public class GameInfo : INotifyPropertyChanged
{
    private Bitmap? _coverImage;

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
    /// Steam's StateFlags bitmask from the .acf manifest. 4 means StateInstalled with no
    /// pending update/download/repair; any other value (e.g. 16=UpdateRequired, 1026=update
    /// queued, 0=uninstalled) indicates Steam thinks something is in flight or wrong.
    /// 0 for non-Steam games or when the flag is absent.
    /// </summary>
    public int StateFlags { get; set; }

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
    /// Whether this game is located on an external drive/library
    /// </summary>
    public bool IsExternal { get; set; } = false;

    /// <summary>
    /// Whether this Xbox game uses the MSIXVC package layout and supports overlay transfer.
    /// Only meaningful for Xbox platform games. Populated by XboxLibraryScanner.
    /// </summary>
    public bool IsOverlaySupported { get; set; } = false;

    /// <summary>
    /// Package Family Name for Xbox games, extracted from the folder's ACL.
    /// Used for protected-exe rescue during network transfers.
    /// </summary>
    public string? PackageFamilyName { get; set; }

    /// <summary>
    /// Xbox only: true when this game has a capture, so it can be transferred with the Smart (updatable)
    /// method. False means only the Basic (overlay, non-updatable) method is available.
    /// </summary>
    public bool XboxSmartReady { get; set; }

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
    public Bitmap? CoverImage
    {
        get => _coverImage;
        set
        {
            if (_coverImage != value)
            {
                _coverImage = value;
                OnPropertyChanged();
                // Send notification to update property specifically to the JS proxy mechanism if it relies on string properties
                CoverImagePath = _coverImage != null ? $"loaded_{AppId}" : null;
            }
        }
    }
    
    private string? _coverUrl;
    /// <summary>
    /// Direct URL the WebUI can use as an &lt;img src&gt;. Set by scanners.
    /// For Steam this is the deterministic CDN header URL; for Epic it is
    /// resolved asynchronously via the storefront catalog.
    /// </summary>
    public string? CoverUrl
    {
        get => _coverUrl;
        set
        {
            if (_coverUrl != value)
            {
                _coverUrl = value;
                OnPropertyChanged();
            }
        }
    }

    private string? _coverImagePath;
    /// <summary>
    /// Optional field used in JS data representation or internal tracking
    /// </summary>
    public string? CoverImagePath
    {
        get => _coverImagePath;
        set
        {
            if (_coverImagePath != value)
            {
                _coverImagePath = value;
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
    Xbox,
    External
}
