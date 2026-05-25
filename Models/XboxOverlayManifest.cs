namespace GamesLocalShare.Models;

/// <summary>
/// Describes every file in an Xbox MSIXVC install for overlay transfer.
/// Sent from the XboxNetworkSender to the receiver before streaming begins.
/// </summary>
public class XboxOverlayManifest
{
    /// <summary>
    /// Content GUID extracted from the .xvi filename.
    /// </summary>
    public string ContentGuid { get; set; } = string.Empty;

    /// <summary>
    /// Package Family Name extracted from the folder ACL.
    /// </summary>
    public string PackageFamilyName { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the source game folder on the sender.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the game (folder name of the install).
    /// </summary>
    public string GameName { get; set; } = string.Empty;

    /// <summary>
    /// Hostname of the sender PC.
    /// </summary>
    public string SenderHost { get; set; } = string.Empty;

    /// <summary>
    /// Total number of files in the install (including skipped).
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// Total size in bytes of all files (including skipped).
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Files that will be streamed over TCP.
    /// </summary>
    public List<XboxOverlayManifestEntry> Entries { get; set; } = new();

    /// <summary>
    /// Protected exe/dll files that could not be rescued and are excluded
    /// from streaming. The receiver's Xbox app must download these itself.
    /// </summary>
    public List<SkippedProtectedFile> SkippedProtectedFiles { get; set; } = new();
}

/// <summary>
/// A protected file that the sender could not read and excluded from the transfer.
/// </summary>
public class SkippedProtectedFile
{
    public string RelativePath { get; set; } = string.Empty;
    public long ExpectedSize { get; set; }
}

/// <summary>
/// Describes a single file entry in the Xbox overlay manifest.
/// </summary>
public class XboxOverlayManifestEntry
{
    /// <summary>
    /// Path relative to the game install root.
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Last modified timestamp (UTC).
    /// </summary>
    public DateTime LastModifiedUtc { get; set; }

    /// <summary>
    /// True for .exe/.dll files that needed special handling (rescue via package context).
    /// </summary>
    public bool IsProtected { get; set; }
}
