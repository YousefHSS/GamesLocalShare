namespace GamesLocalShare.Models;

/// <summary>
/// Tracks the state of an Xbox MSIXVC transfer (sender staging or receiver overlay).
/// </summary>
public class XboxTransferState
{
    public XboxTransferStep CurrentStep { get; set; } = XboxTransferStep.SelectSource;
    public string GameName { get; set; } = string.Empty;
    public string PackageFamilyName { get; set; } = string.Empty;
    public string ContentGuid { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public long SourceBytes { get; set; }
    public int SourceFileCount { get; set; }

    // Progress
    public double OverlayProgress { get; set; }
    public string StatusMessage { get; set; } = string.Empty;

    // Monitoring results
    public double NetworkReceivedMB { get; set; }
    public bool PackageInstalled { get; set; }
    public string PackageStatus { get; set; } = string.Empty;

    // Verdict
    public XboxTransferVerdict Verdict { get; set; } = XboxTransferVerdict.Pending;
    public string? ErrorMessage { get; set; }
}

public enum XboxTransferStep
{
    // Sender steps
    SelectGame,
    SelectDestination,
    ValidatingSource,
    CopyingFiles,
    // Receiver steps
    SelectSource,
    WaitingForInstallPause,
    PollingForFolder,
    Overlaying,
    ResettingAcls,
    WaitingForResume,
    Monitoring,
    // Common
    Complete,
    Failed
}

public enum XboxTransferVerdict
{
    Pending,
    FullSkip,       // < 100 MB network, package installed
    DeltaOnly,      // 100 MB to 80% source, package installed
    FullRedownload, // >= 80% source re-downloaded
    StillPaused,    // User didn't click Resume
    Error           // Something went wrong
}
