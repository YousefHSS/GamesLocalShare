using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

/// <summary>
/// Scans Xbox Game Pass (MSIXVC) install folders to detect installed games.
/// MSIXVC titles live under &lt;Drive&gt;:\XboxGames\&lt;Name&gt;\ with .xvi/.xvs/.xct
/// envelope files at the root and a Content\ subfolder containing game data.
/// Windows-only (Xbox PC Game Pass is Windows-exclusive).
/// </summary>
public class XboxLibraryScanner : IGameLibraryScanner
{
    private readonly List<string> _scanErrors = [];
    private readonly TitleCoverArtService _coverArt = new();

    public GamePlatform Platform => GamePlatform.Xbox;
    public IReadOnlyList<string> ScanErrors => _scanErrors;

    public async Task<List<GameInfo>> ScanGamesAsync()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        return await Task.Run(ScanGamesInternal);
    }

    [SupportedOSPlatform("windows")]
    private List<GameInfo> ScanGamesInternal()
    {
        _scanErrors.Clear();
        var games = new List<GameInfo>();
        var xboxRoots = GetXboxGamesRoots();

        if (xboxRoots.Count == 0)
        {
            _scanErrors.Add("No XboxGames folders found on any drive");
            return games;
        }

        foreach (var root in xboxRoots)
        {
            try
            {
                var dirs = Directory.GetDirectories(root);
                foreach (var dir in dirs)
                {
                    var game = TryParseXboxGame(dir);
                    if (game != null)
                    {
                        games.Add(game);
                        _ = LoadCoverImageAsync(game);
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _scanErrors.Add($"Access denied scanning {root}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _scanErrors.Add($"Error scanning {root}: {ex.Message}");
            }
        }

        return games.OrderBy(g => g.Name).ToList();
    }

    /// <summary>
    /// Returns all XboxGames root folders found across all drives.
    /// </summary>
    public List<string> GetLibraryFolders()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        return GetXboxGamesRoots();
    }

    public Task LoadCoverImageAsync(GameInfo game)
    {
        // Try fetching online cover art by title matching using the Epic storefront catalog
        return _coverArt.LoadAsync(game);
    }

    /// <summary>
    /// Finds all XboxGames\ directories across filesystem drives.
    /// </summary>
    private static List<string> GetXboxGamesRoots()
    {
        var roots = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;
                if (!drive.IsReady)
                    continue;

                // Check .GamingRoot file created by Xbox app
                var gamingRootPath = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
                if (File.Exists(gamingRootPath))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(gamingRootPath);
                        // Magic bytes: 'R' (0x52), 'G' (0x47), 'B' (0x42), 'X' (0x58)
                        if (bytes.Length >= 10 && bytes[0] == 0x52 && bytes[1] == 0x47 && bytes[2] == 0x42 && bytes[3] == 0x58)
                        {
                            var folderName = System.Text.Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8).TrimEnd('\0');
                            if (!string.IsNullOrWhiteSpace(folderName))
                            {
                                var customXboxPath = Path.Combine(drive.RootDirectory.FullName, folderName);
                                if (Directory.Exists(customXboxPath))
                                {
                                    roots.Add(customXboxPath);
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Fallback to default XboxGames
                var xboxPath = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                if (Directory.Exists(xboxPath) && !roots.Contains(xboxPath))
                    roots.Add(xboxPath);
            }
        }
        catch { }
        return roots;
    }

    /// <summary>
    /// Attempts to parse a directory as an MSIXVC Xbox game install.
    /// Returns null if the directory is not a valid MSIXVC install.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private GameInfo? TryParseXboxGame(string dir)
    {
        var dirName = Path.GetFileName(dir);

        // Skip known non-game folders
        if (string.Equals(dirName, "GameSave", StringComparison.OrdinalIgnoreCase))
            return null;

        // MSIXVC titles have .xvi envelope files at the root
        string? xviFile = null;
        try
        {
            var envelopeFiles = Directory.GetFiles(dir, "*.xvi");
            if (envelopeFiles.Length == 0)
            {
                // Not an MSIXVC install - might be a GUID folder (in-progress) or other
                // Check for .xct or .xvs as fallback indicators
                var xctFiles = Directory.GetFiles(dir, "*.xct");
                var xvsFiles = Directory.GetFiles(dir, "*.xvs");
                if (xctFiles.Length == 0 && xvsFiles.Length == 0)
                    return null;
            }
            else
            {
                xviFile = envelopeFiles[0];
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Can't read the folder - likely MSIXVC with restrictive ACLs
            // Still report it as a game with limited info
        }
        catch
        {
            return null;
        }

        // Extract content GUID from .xvi filename (e.g. "807C7D6A-...-30B09BAF7CD4.xvi")
        string? contentGuid = null;
        if (xviFile != null)
            contentGuid = Path.GetFileNameWithoutExtension(xviFile);

        // Determine the display name
        // If the folder name looks like a GUID, it's an in-progress install
        bool isGuidFolder = IsGuidString(dirName);
        string displayName = isGuidFolder ? $"[Installing] {dirName}" : dirName;

        // Calculate size
        long sizeOnDisk = 0;
        int fileCount = 0;
        try
        {
            var files = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories);
            foreach (var f in files)
            {
                sizeOnDisk += f.Length;
                fileCount++;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Partial access - size will be incomplete
            _scanErrors.Add($"Partial access to {dirName} - size may be inaccurate");
        }
        catch { }

        // Try to extract PFN from folder ACL (conditional SYSAPPID ACE)
        string? pfn = TryExtractPfnFromAcl(dir);

        // Determine overlay support: requires both envelope files at root
        // AND a GUID-named subfolder (the MSIXVC Content container layout).
        bool hasGuidSubfolder = false;
        try
        {
            hasGuidSubfolder = Directory.GetDirectories(dir)
                .Any(sub => Guid.TryParse(Path.GetFileName(sub), out _));
        }
        catch { }

        bool isOverlaySupported = (xviFile != null || Directory.GetFiles(dir, "*.xvi").Length > 0)
            && hasGuidSubfolder;

        var game = new GameInfo
        {
            AppId = contentGuid ?? dirName,
            Name = displayName,
            InstallPath = dir,
            SizeOnDisk = sizeOnDisk,
            LastUpdated = SafeLastWrite(dir),
            BuildId = contentGuid ?? "unknown",
            Platform = GamePlatform.Xbox,
            IsInstalled = !isGuidFolder,
            IsOverlaySupported = isOverlaySupported,
            PackageFamilyName = pfn,
        };

        return game;
    }

    /// <summary>
    /// Attempts to extract the Package Family Name from the folder's ACL.
    /// MSIXVC folders have a conditional ACE: WIN://SYSAPPID Contains "PFN"
    /// </summary>
    [SupportedOSPlatform("windows")]
    private string? TryExtractPfnFromAcl(string dir)
    {
        try
        {
            var dirInfo = new DirectoryInfo(dir);
            var security = dirInfo.GetAccessControl();
            var sddl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            // Look for the SYSAPPID conditional ACE pattern
            // The SDDL contains something like: ...;WIN://SYSAPPID Contains "TeamCherry.HollowKnightSilksong_y4jvztpgccj42"...
            const string marker = "SYSAPPID";
            var idx = sddl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            // Find the PFN in quotes after the marker
            var quoteStart = sddl.IndexOf('"', idx);
            if (quoteStart < 0) return null;
            var quoteEnd = sddl.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0) return null;

            return sddl.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsGuidString(string s)
    {
        return Guid.TryParse(s, out _);
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return Directory.GetLastWriteTime(path); }
        catch { return DateTime.MinValue; }
    }
}
