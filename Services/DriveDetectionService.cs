using System.IO;
using System.Timers;

namespace GamesLocalShare.Services;

public record DriveCandidate(string DriveLetter, string VolumeLabel, string Serial, bool IsRemovable, bool IsAvailable);

public class DriveDetectionService : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private IReadOnlyList<DriveCandidate> _current = [];
    private bool _disposed;

    public event EventHandler<IReadOnlyList<DriveCandidate>>? DrivesChanged;

    public IReadOnlyList<DriveCandidate> CurrentDrives => _current;

    public DriveDetectionService()
    {
        _timer = new System.Timers.Timer(5000);
        _timer.Elapsed += OnTick;
        _timer.AutoReset = true;
        Poll();
        _timer.Start();
    }

    private void OnTick(object? sender, ElapsedEventArgs e) => Poll();

    private void Poll()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Removable or DriveType.Fixed)
                .Where(d => !IsSystemDrive(d))
                .Select(d => new DriveCandidate(
                    DriveLetter: d.Name,
                    VolumeLabel: TryGetLabel(d),
                    Serial: GetSerial(d),
                    IsRemovable: d.DriveType == DriveType.Removable,
                    IsAvailable: d.IsReady))
                .ToList();

            var changed = drives.Count != _current.Count ||
                drives.Zip(_current).Any(pair => pair.First != pair.Second);

            if (changed)
            {
                _current = drives;
                DrivesChanged?.Invoke(this, _current);
            }
        }
        catch { }
    }

    private static bool IsSystemDrive(DriveInfo d)
    {
        if (!d.IsReady) return false;
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(winDir)) return false;
        return string.Equals(Path.GetPathRoot(winDir), d.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string TryGetLabel(DriveInfo d)
    {
        try { return d.IsReady ? (d.VolumeLabel ?? string.Empty) : string.Empty; }
        catch { return string.Empty; }
    }

    private static string GetSerial(DriveInfo d)
    {
        // Use drive letter as identity for simplicity (WMI would be more robust but adds complexity)
        return d.Name.TrimEnd('\\', '/');
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}
