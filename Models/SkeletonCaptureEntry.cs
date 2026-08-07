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
/// Live progress of a skeleton capture in flight, surfaced to the WebUI so the user sees a progress bar.
/// <para>Two producers feed this. The <b>streaming</b> path (<c>XboxCacheProxyService</c>) captures the
/// skeleton from the Store's own install download and reports exact byte counts through
/// <see cref="Stage"/> / <see cref="BytesDone"/> / <see cref="BytesTotal"/>. The <b>legacy batch</b> path
/// (xvdtool over a cached package) only prints named stage markers, so it reports a monotonic
/// <see cref="Step"/> out of <see cref="TotalSteps"/> plus a human phase label and the bar fills by step.</para>
/// </summary>
public sealed class SkeletonCaptureProgress
{
    /// <summary>Stable key for this capture — the streaming path uses the package cache path, the batch path
    /// the title name. Lets the client keep list rows identified across pushes.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Lifecycle stage, driving which bar the client draws. One of:
    /// <c>buffering</c> (assembling the front before the capture can arm),
    /// <c>capturing</c> (armed, consuming the install download — the long phase),
    /// <c>waiting</c> (capture complete, parked until the game finishes installing),
    /// <c>restored</c> (a park recovered from a previous run, same wait),
    /// <c>finalizing</c> (matching the install and refetching missing ranges), or
    /// <c>preparing</c> (legacy batch capture, step-based).</summary>
    public string Stage { get; set; } = "preparing";

    /// <summary>Bytes completed within the current <see cref="Stage"/>; 0 when the stage has no byte measure.</summary>
    public long BytesDone { get; set; }

    /// <summary>Bytes the current <see cref="Stage"/> is working towards; 0 when unknown.</summary>
    public long BytesTotal { get; set; }

    /// <summary>Title being prepared (the installed folder / skeleton name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Current phase index, 1..<see cref="TotalSteps"/>. Only ever advances.</summary>
    public int Step { get; set; }

    /// <summary>Total number of phases (fixed at 5).</summary>
    public int TotalSteps { get; set; } = 5;

    /// <summary>Human-readable current phase, e.g. "Matching installed files".</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>Exact progress 0-100 <b>within the current <see cref="Stage"/></b> — from the streaming path's
    /// byte counts, or the engine's "PROGRESS n" during a finalize. -1 when unknown, in which case the client
    /// falls back to the coarse <see cref="Step"/> bar. Monotonic within a stage, and reset by a stage change
    /// (buffering reaching 100% is the start of capturing, not the end of the job).</summary>
    public int Percent { get; set; } = -1;

    /// <summary>Unix epoch ms when the capture started, so the client can show elapsed time.</summary>
    public long StartedAtMs { get; set; }
}
