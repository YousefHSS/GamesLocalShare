using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text.Json;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

/// <summary>
/// Handles the Xbox MSIXVC overlay-on-paused-install transfer workflow.
/// This is the receiver side: given a pre-staged source, it overlays the
/// files onto an Xbox app-initiated paused install and monitors the result.
/// Requires running as Administrator (robocopy /B for backup privileges).
/// </summary>
[SupportedOSPlatform("windows")]
public class XboxTransferService
{
    private CancellationTokenSource? _cts;

    public event EventHandler<XboxTransferState>? StateChanged;
    public event EventHandler<string>? LogMessage;

    private XboxTransferState _state = new();
    public XboxTransferState State => _state;

    /// <summary>
    /// Validates a staged source directory. Returns null on success, error message on failure.
    /// </summary>
    public string? ValidateSource(string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
            return $"Source directory not found: {sourcePath}";

        var summaryPath = Path.Combine(sourcePath, "transfer-summary.json");
        if (!File.Exists(summaryPath))
            return "transfer-summary.json not found in source directory";

        try
        {
            var json = File.ReadAllText(summaryPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            _state.GameName = root.GetProperty("GameName").GetString() ?? "";
            _state.PackageFamilyName = root.GetProperty("PackageFamilyName").GetString() ?? "";
            _state.SourceBytes = root.GetProperty("SourceBytes").GetInt64();
            _state.SourceFileCount = root.GetProperty("SourceFileCount").GetInt32();
            _state.SourcePath = sourcePath;
        }
        catch (Exception ex)
        {
            return $"Error reading transfer-summary.json: {ex.Message}";
        }

        // Extract content GUID from .xvi file
        var xviFiles = Directory.GetFiles(sourcePath, "*.xvi");
        if (xviFiles.Length > 0)
        {
            _state.ContentGuid = Path.GetFileNameWithoutExtension(xviFiles[0]);
        }
        else
        {
            return "No .xvi envelope file found in source - not a valid MSIXVC staged copy";
        }

        return null;
    }

    /// <summary>
    /// Searches all drives for the Xbox install destination folder (by GUID or game name).
    /// </summary>
    public List<string> FindDestinationCandidates()
    {
        var candidates = new List<string>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                continue;
            if (!drive.IsReady)
                continue;

            var xg = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(xg))
                continue;

            var byName = Path.Combine(xg, _state.GameName);
            if (Directory.Exists(byName))
                candidates.Add(byName);

            if (!string.IsNullOrEmpty(_state.ContentGuid))
            {
                var byGuid = Path.Combine(xg, _state.ContentGuid);
                if (Directory.Exists(byGuid))
                    candidates.Add(byGuid);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Polls for the install folder to appear (Gaming Services creates it after Install is clicked).
    /// </summary>
    public async Task<string?> PollForInstallFolderAsync(int timeoutSeconds = 90, CancellationToken ct = default)
    {
        _state.CurrentStep = XboxTransferStep.PollingForFolder;
        _state.StatusMessage = "Waiting for Xbox install folder to appear...";
        RaiseStateChanged();

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var candidates = FindDestinationCandidates();
            foreach (var c in candidates)
            {
                try
                {
                    var files = Directory.GetFiles(c, "*", SearchOption.AllDirectories);
                    if (files.Length > 0)
                    {
                        _state.DestinationPath = c;
                        Log($"Found install folder: {c} ({files.Length} files)");
                        return c;
                    }
                }
                catch { }
            }

            await Task.Delay(3000, ct);
        }

        return null;
    }

    /// <summary>
    /// Performs the overlay: robocopy source into destination with /COPY:DAT /IS /IT.
    /// Must run as Administrator for /B (backup privilege) to access all files.
    /// </summary>
    public async Task<int> OverlayFilesAsync(CancellationToken ct = default)
    {
        _state.CurrentStep = XboxTransferStep.Overlaying;
        _state.StatusMessage = $"Overlaying {_state.SourceFileCount} files...";
        RaiseStateChanged();

        var args = $"\"{_state.SourcePath}\" \"{_state.DestinationPath}\" /E /COPY:DAT /DCOPY:DAT /IS /IT /R:1 /W:2 /MT:8 /NP /NDL /B /XF transfer-summary.json";

        Log($"robocopy {args}");

        var psi = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            return -1;

        // Read output async
        var outputTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errorTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);

        var output = await outputTask;
        if (!string.IsNullOrWhiteSpace(output))
            Log($"robocopy output (last 500 chars): ...{(output.Length > 500 ? output[^500..] : output)}");

        var exitCode = proc.ExitCode;
        Log($"robocopy exit code: {exitCode}");

        // Robocopy exit codes: 0-7 are success (bits: 1=copied, 2=extras, 4=mismatches)
        if (exitCode >= 8)
        {
            var error = await errorTask;
            _state.ErrorMessage = $"robocopy failed with exit code {exitCode}: {error}";
        }

        return exitCode;
    }

    /// <summary>
    /// Resets ACLs on the destination so files inherit from parent folder.
    /// This fixes the 0x80070005 (ACCESS_DENIED) error during MRTDataPopulated.
    /// </summary>
    public async Task<int> ResetAclsAsync(CancellationToken ct = default)
    {
        _state.CurrentStep = XboxTransferStep.ResettingAcls;
        _state.StatusMessage = "Resetting file permissions...";
        RaiseStateChanged();

        var psi = new ProcessStartInfo
        {
            FileName = "icacls.exe",
            Arguments = $"\"{_state.DestinationPath}\" /reset /T /Q /C",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            return -1;

        await proc.WaitForExitAsync(ct);
        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        Log($"icacls: {output.Trim()}");

        return proc.ExitCode;
    }

    /// <summary>
    /// Monitors NIC traffic and package state after Resume is clicked.
    /// </summary>
    public async Task MonitorResumeAsync(int observeSeconds = 300, CancellationToken ct = default)
    {
        _state.CurrentStep = XboxTransferStep.Monitoring;
        _state.StatusMessage = "Monitoring installation progress...";
        RaiseStateChanged();

        var baseline = GetNicReceivedBytes();
        var startTime = DateTime.UtcNow;
        var step = TimeSpan.FromSeconds(15);

        while ((DateTime.UtcNow - startTime).TotalSeconds < observeSeconds && !ct.IsCancellationRequested)
        {
            await Task.Delay(step, ct);

            var currentRx = GetNicReceivedBytes();
            var rxDelta = currentRx - baseline;
            var rxMB = rxDelta / (1024.0 * 1024.0);

            _state.NetworkReceivedMB = rxMB;
            _state.PackageInstalled = IsPackageInstalled(_state.PackageFamilyName);

            var elapsed = (int)(DateTime.UtcNow - startTime).TotalSeconds;
            _state.StatusMessage = $"t+{elapsed}s  rx={rxMB:N1} MB  installed={_state.PackageInstalled}";
            Log(_state.StatusMessage);
            RaiseStateChanged();

            // Early exit if installed with minimal traffic
            if (_state.PackageInstalled && rxMB < 50 && elapsed >= 60)
            {
                Log("Package installed with minimal network traffic - success!");
                break;
            }
        }

        // Determine verdict
        var finalRx = GetNicReceivedBytes() - baseline;
        var finalRxMB = finalRx / (1024.0 * 1024.0);
        _state.NetworkReceivedMB = finalRxMB;
        _state.PackageInstalled = IsPackageInstalled(_state.PackageFamilyName);

        if (_state.PackageInstalled)
        {
            if (finalRx < 100 * 1024 * 1024)
                _state.Verdict = XboxTransferVerdict.FullSkip;
            else if (finalRx < _state.SourceBytes * 0.8)
                _state.Verdict = XboxTransferVerdict.DeltaOnly;
            else
                _state.Verdict = XboxTransferVerdict.FullRedownload;
        }
        else
        {
            if (finalRx < 50 * 1024 * 1024)
                _state.Verdict = XboxTransferVerdict.StillPaused;
            else if (finalRx >= _state.SourceBytes * 0.8)
                _state.Verdict = XboxTransferVerdict.FullRedownload;
            else
                _state.Verdict = XboxTransferVerdict.Pending;
        }

        _state.CurrentStep = _state.Verdict == XboxTransferVerdict.FullSkip || _state.Verdict == XboxTransferVerdict.DeltaOnly
            ? XboxTransferStep.Complete
            : XboxTransferStep.Failed;

        _state.StatusMessage = $"Verdict: {_state.Verdict} (rx={finalRxMB:N1} MB, installed={_state.PackageInstalled})";
        RaiseStateChanged();
    }

    /// <summary>
    /// Runs the full overlay workflow end-to-end.
    /// Call this after the user confirms they've paused the install.
    /// </summary>
    public async Task<XboxTransferVerdict> RunOverlayAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        try
        {
            // Step 1: Poll for install folder
            var dest = await PollForInstallFolderAsync(90, token);
            if (dest == null)
            {
                _state.CurrentStep = XboxTransferStep.Failed;
                _state.ErrorMessage = "Install folder did not appear within 90 seconds. Ensure you clicked Install and Pause in the Xbox app.";
                _state.Verdict = XboxTransferVerdict.Error;
                RaiseStateChanged();
                return XboxTransferVerdict.Error;
            }

            // Step 2: Overlay files
            var rcExit = await OverlayFilesAsync(token);
            if (rcExit >= 8)
            {
                _state.CurrentStep = XboxTransferStep.Failed;
                _state.Verdict = XboxTransferVerdict.Error;
                RaiseStateChanged();
                return XboxTransferVerdict.Error;
            }

            // Step 3: Reset ACLs
            await ResetAclsAsync(token);

            // Step 4: Wait for user to click Resume
            _state.CurrentStep = XboxTransferStep.WaitingForResume;
            _state.StatusMessage = "Overlay complete! Click Resume in the Xbox app now.";
            RaiseStateChanged();

            // Give user time to click Resume, then start monitoring
            await Task.Delay(5000, token);

            // Step 5: Monitor
            await MonitorResumeAsync(300, token);

            return _state.Verdict;
        }
        catch (OperationCanceledException)
        {
            _state.CurrentStep = XboxTransferStep.Failed;
            _state.ErrorMessage = "Transfer cancelled";
            _state.Verdict = XboxTransferVerdict.Error;
            RaiseStateChanged();
            return XboxTransferVerdict.Error;
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    public void Reset()
    {
        _state = new XboxTransferState();
        _cts?.Dispose();
        _cts = null;
    }

    private long GetNicReceivedBytes()
    {
        try
        {
            var stats = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPv4Statistics())
                .OrderByDescending(s => s.BytesReceived)
                .FirstOrDefault();
            return stats?.BytesReceived ?? 0;
        }
        catch { return 0; }
    }

    private static bool IsPackageInstalled(string pfn)
    {
        if (string.IsNullOrEmpty(pfn))
            return false;

        try
        {
            // Use PowerShell to check package state (Get-AppxPackage is not directly
            // accessible from C#, but we can use the Windows.Management.Deployment API)
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"if (Get-AppxPackage -Name '{pfn.Split('_')[0]}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return string.Equals(output, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void Log(string message)
    {
        LogMessage?.Invoke(this, $"[Xbox] {message}");
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, _state);
    }
}
