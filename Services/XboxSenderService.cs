using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

/// <summary>
/// Handles the Xbox MSIXVC sender staging workflow.
/// Copies an installed Xbox game to a portable destination (USB/shared drive)
/// with proper metadata for the receiver overlay process.
/// Requires running as Administrator (robocopy /B for backup privileges).
/// </summary>
[SupportedOSPlatform("windows")]
public class XboxSenderService
{
    private CancellationTokenSource? _cts;

    public event EventHandler<XboxTransferState>? StateChanged;
    public event EventHandler<string>? LogMessage;

    private XboxTransferState _state = new();
    public XboxTransferState State => _state;

    /// <summary>
    /// Validates that the source is a valid Xbox MSIXVC install.
    /// Returns null on success, error message on failure.
    /// </summary>
    public string? ValidateSource(string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
            return $"Source directory not found: {sourcePath}";

        // Check for MSIXVC indicators: .xvi/.xvs/.xct files and Content\ subfolder
        var xviFiles = Directory.GetFiles(sourcePath, "*.xvi");
        var xvsFiles = Directory.GetFiles(sourcePath, "*.xvs");
        var xctFiles = Directory.GetFiles(sourcePath, "*.xct");
        var contentDir = Path.Combine(sourcePath, "Content");

        if (xviFiles.Length == 0 && xvsFiles.Length == 0 && xctFiles.Length == 0)
            return "No MSIXVC envelope files (.xvi/.xvs/.xct) found - not a valid Xbox install";

        if (!Directory.Exists(contentDir))
            return "Content subfolder not found - not a valid Xbox install";

        // Extract metadata
        _state.SourcePath = sourcePath;
        _state.GameName = Path.GetFileName(sourcePath);

        // Extract content GUID from .xvi filename
        if (xviFiles.Length > 0)
        {
            _state.ContentGuid = Path.GetFileNameWithoutExtension(xviFiles[0]);
        }

        // Extract PFN from ACL
        _state.PackageFamilyName = ExtractPfnFromAcl(sourcePath) ?? "";

        // Count source files and size
        try
        {
            var files = new DirectoryInfo(sourcePath).EnumerateFiles("*", SearchOption.AllDirectories);
            _state.SourceFileCount = files.Count();
            _state.SourceBytes = files.Sum(f => f.Length);
        }
        catch (UnauthorizedAccessException)
        {
            _state.SourceFileCount = 0;
            _state.SourceBytes = 0;
        }
        catch
        {
            return "Could not count source files";
        }

        return null;
    }

    /// <summary>
    /// Stages the Xbox game to the destination directory.
    /// </summary>
    public async Task StageAsync(string destinationPath, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        try
        {
            _state.DestinationPath = destinationPath;
            _state.CurrentStep = XboxTransferStep.ValidatingSource;
            _state.StatusMessage = "Validating source...";
            RaiseStateChanged();

            // Ensure destination exists
            Directory.CreateDirectory(destinationPath);

            _state.CurrentStep = XboxTransferStep.Overlaying;
            _state.StatusMessage = $"Staging {_state.SourceFileCount} files...";
            RaiseStateChanged();

            // Run robocopy
            var args = $"\"{_state.SourcePath}\" \"{destinationPath}\" /E /COPY:DAT /DCOPY:DAT /IS /IT /R:1 /W:2 /MT:8 /NP /NDL /B /XF transfer-summary.json";
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
            {
                _state.CurrentStep = XboxTransferStep.Failed;
                _state.ErrorMessage = "Failed to start robocopy";
                RaiseStateChanged();
                return;
            }

            // Monitor progress by polling destination file count
            var progressTask = MonitorProgressAsync(destinationPath, token);

            await proc.WaitForExitAsync(token);

            var exitCode = proc.ExitCode;
            Log($"robocopy exit code: {exitCode}");

            // Wait for progress monitoring to finish
            await progressTask;

            // Count final destination files
            int destFileCount = 0;
            long destBytes = 0;
            try
            {
                var destFiles = new DirectoryInfo(destinationPath).EnumerateFiles("*", SearchOption.AllDirectories);
                destFileCount = destFiles.Count();
                destBytes = destFiles.Sum(f => f.Length);
            }
            catch { }

            // Write transfer-summary.json
            WriteTransferSummary(destinationPath, destFileCount, destBytes, exitCode);

            if (exitCode >= 8)
            {
                _state.CurrentStep = XboxTransferStep.Failed;
                _state.ErrorMessage = $"Robocopy failed with exit code {exitCode}";
                _state.Verdict = XboxTransferVerdict.Error;
            }
            else
            {
                _state.CurrentStep = XboxTransferStep.Complete;
                _state.StatusMessage = $"Staging complete: {destFileCount} files, {destBytes / 1024 / 1024:N1} MB";
                _state.Verdict = XboxTransferVerdict.FullSkip; // Reuse this as "success"
            }

            RaiseStateChanged();
        }
        catch (OperationCanceledException)
        {
            _state.CurrentStep = XboxTransferStep.Failed;
            _state.ErrorMessage = "Staging cancelled";
            _state.Verdict = XboxTransferVerdict.Error;
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            _state.CurrentStep = XboxTransferStep.Failed;
            _state.ErrorMessage = $"Staging error: {ex.Message}";
            _state.Verdict = XboxTransferVerdict.Error;
            RaiseStateChanged();
        }
    }

    private async Task MonitorProgressAsync(string destPath, CancellationToken ct)
    {
        var checkInterval = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var destFiles = Directory.GetFiles(destPath, "*", SearchOption.AllDirectories);
                var progress = (double)destFiles.Length / Math.Max(1, _state.SourceFileCount);
                _state.OverlayProgress = Math.Min(progress * 100, 100);
                _state.StatusMessage = $"Staging... {destFiles.Length}/{_state.SourceFileCount} files ({_state.OverlayProgress:N0}%)";
                RaiseStateChanged();
            }
            catch { }

            await Task.Delay(checkInterval, ct);
        }
    }

    private void WriteTransferSummary(string destPath, int filesCopied, long bytesCopied, int robocopyExit)
    {
        var summary = new
        {
            StartedAtUtc = DateTime.UtcNow.ToString("o"),
            SenderHost = Environment.MachineName,
            Identity = WindowsIdentity.GetCurrent().Name,
            GameFolder = _state.SourcePath,
            GameName = _state.GameName,
            Destination = destPath,
            PackageFamilyName = _state.PackageFamilyName,
            SourceFileCount = _state.SourceFileCount,
            SourceBytes = _state.SourceBytes,
            UnreadableFiles = new string[0], // TODO: track files that couldn't be copied
            SkippedFiles = 0,
            FilesCopied = filesCopied,
            BytesCopied = bytesCopied,
            RobocopyExit = robocopyExit,
            RobocopyLog = "" // Could capture log if needed
        };

        var summaryPath = Path.Combine(destPath, "transfer-summary.json");
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(summaryPath, json);
        Log($"Wrote transfer-summary.json to {summaryPath}");
    }

    private static string? ExtractPfnFromAcl(string dir)
    {
        try
        {
            var dirInfo = new DirectoryInfo(dir);
            var security = dirInfo.GetAccessControl();
            var sddl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            const string marker = "SYSAPPID";
            var idx = sddl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            var quoteStart = sddl.IndexOf('"', idx);
            if (quoteStart < 0) return null;
            var quoteEnd = sddl.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0) return null;

            return sddl.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }
        catch { return null; }
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

    private void Log(string message)
    {
        LogMessage?.Invoke(this, $"[Xbox Sender] {message}");
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, _state);
    }
}