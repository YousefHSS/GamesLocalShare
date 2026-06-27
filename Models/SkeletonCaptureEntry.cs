namespace GamesLocalShare.Models;

/// <summary>
/// A verified skeleton capture, surfaced to the WebUI so the user can see the
/// storage saving (a ~17 MB .skl replaces a multi-GB decrypted package).
/// </summary>
public sealed class SkeletonCaptureEntry
{
    /// <summary>Installed title folder name the skeleton was captured for.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Path of the written .skl file.</summary>
    public string SkeletonPath { get; set; } = string.Empty;

    /// <summary>Skeleton (.skl) file size in bytes.</summary>
    public long SkeletonBytes { get; set; }

    /// <summary>Original decrypted package (U) size in bytes.</summary>
    public long PackageBytes { get; set; }

    /// <summary>Bytes saved versus keeping the full package (PackageBytes - SkeletonBytes).</summary>
    public long SavedBytes { get; set; }

    /// <summary>Local time the capture completed (round-trip "o" format).</summary>
    public string CapturedAt { get; set; } = string.Empty;
}
