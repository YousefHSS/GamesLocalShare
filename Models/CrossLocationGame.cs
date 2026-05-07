namespace GamesLocalShare.Models;

public enum CopyDirection
{
    None,
    InSync,
    DeviceToDrive,
    DriveToDevice,
    OnlyOnDevice,
    OnlyOnDrive,
}

public class CrossLocationGame
{
    public GameInfo? DeviceCopy { get; set; }
    public GameInfo? ExternalCopy { get; set; }
    public ExternalLibrary Library { get; set; } = null!;
    public CopyDirection Direction { get; set; }

    public string DisplayName => DeviceCopy?.Name ?? ExternalCopy?.Name ?? "Unknown";
    public string AppId => DeviceCopy?.AppId ?? ExternalCopy?.AppId ?? string.Empty;

    public string StatusText => Direction switch
    {
        CopyDirection.InSync => "In sync",
        CopyDirection.DeviceToDrive => "Newer on device → copy to drive",
        CopyDirection.DriveToDevice => "Newer on drive → copy to device",
        CopyDirection.OnlyOnDevice => "Only on device",
        CopyDirection.OnlyOnDrive => "Only on drive",
        _ => "Unknown"
    };

    public string StatusColor => Direction switch
    {
        CopyDirection.InSync => "green",
        CopyDirection.DeviceToDrive or CopyDirection.DriveToDevice => "yellow",
        CopyDirection.OnlyOnDevice or CopyDirection.OnlyOnDrive => "slate",
        _ => "slate"
    };
}
