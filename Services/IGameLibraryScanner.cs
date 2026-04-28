using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

/// <summary>
/// Common contract for store-specific game library scanners (Steam, Epic, etc.).
/// </summary>
public interface IGameLibraryScanner
{
    GamePlatform Platform { get; }

    IReadOnlyList<string> ScanErrors { get; }

    Task<List<GameInfo>> ScanGamesAsync();

    /// <summary>
    /// Returns the root folders where games for this platform are installed.
    /// For Steam these are steamapps directories; for Epic these are install
    /// roots (the parent folder game directories live under).
    /// </summary>
    List<string> GetLibraryFolders();

    Task LoadCoverImageAsync(GameInfo game);
}
