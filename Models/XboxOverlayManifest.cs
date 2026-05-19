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
    /// Total number of files in the install.
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// Total size in bytes of all files.
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Per-file metadata.
    /// </summary>
    public List<XboxOverlayManifestEntry> Entries { get; set; } = new();
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
}
