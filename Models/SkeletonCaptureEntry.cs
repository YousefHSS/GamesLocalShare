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

/// <summary>
/// Live progress of a skeleton capture in flight, surfaced to the WebUI so the user sees a "Preparing…"
/// progress bar. xvdtool reports named phases rather than an exact percentage, so progress is a monotonic
/// <see cref="Step"/> out of <see cref="TotalSteps"/> plus a human phase label; the bar fills by step.
/// </summary>
public sealed class SkeletonCaptureProgress
{
    /// <summary>Title being prepared (the installed folder / skeleton name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Current phase index, 1..<see cref="TotalSteps"/>. Only ever advances.</summary>
    public int Step { get; set; }

    /// <summary>Total number of phases (fixed at 5).</summary>
    public int TotalSteps { get; set; } = 5;

    /// <summary>Human-readable current phase, e.g. "Matching installed files".</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>Exact overall progress 0-100 when the engine reports it (xvdtool "PROGRESS n"); -1 when
    /// unknown, in which case the client falls back to the coarse <see cref="Step"/> bar. Monotonic.</summary>
    public int Percent { get; set; } = -1;

    /// <summary>Unix epoch ms when the capture started, so the client can show elapsed time.</summary>
    public long StartedAtMs { get; set; }
}
