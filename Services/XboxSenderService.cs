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
    /// Stages the Xbox game to a local/shared drive destination.
    /// Performs a post-copy integrity walk and writes transfer-summary.json.
    /// </summary>
    public async Task StageToFolderAsync(string destinationPath, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        try
        {
            _state.DestinationPath = destinationPath;
            _state.CurrentStep = XboxTransferStep.ValidatingSource;
            _state.StatusMessage = "Validating source...";
            RaiseStateChanged();

            Directory.CreateDirectory(destinationPath);

            _state.CurrentStep = XboxTransferStep.CopyingFiles;
            _state.StatusMessage = $"Staging {_state.SourceFileCount} files...";
            RaiseStateChanged();

            // Run robocopy with backup privileges (/B)
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

            var progressTask = MonitorProgressAsync(destinationPath, token);
            await proc.WaitForExitAsync(token);
            var exitCode = proc.ExitCode;
            Log($"robocopy exit code: {exitCode}");
            await progressTask;

            // Post-copy integrity walk
            _state.CurrentStep = XboxTransferStep.CopyingFiles;
            _state.StatusMessage = "Verifying integrity...";
            RaiseStateChanged();

            var integrity = RunIntegrityWalk(_state.SourcePath, destinationPath);
            int destFileCount = integrity.DestFileCount;
            long destBytes = integrity.DestBytes;

            bool integrityOk = integrity.MissingFiles.Count == 0 && integrity.MismatchFiles.Count == 0;
            WriteTransferSummary(destinationPath, destFileCount, destBytes, exitCode, integrity);

            if (exitCode >= 8)
            {
                _state.CurrentStep = XboxTransferStep.Failed;
                _state.ErrorMessage = $"Robocopy failed with exit code {exitCode}";
                _state.Verdict = XboxTransferVerdict.Error;
            }
            else if (!integrityOk)
            {
                _state.CurrentStep = XboxTransferStep.Complete;
                _state.StatusMessage = $"Staging complete with integrity warnings: {integrity.MissingFiles.Count} missing, {integrity.MismatchFiles.Count} mismatched";
                _state.Verdict = XboxTransferVerdict.DeltaOnly; // partial success
            }
            else
            {
                _state.CurrentStep = XboxTransferStep.Complete;
                _state.StatusMessage = $"Staging complete: {destFileCount} files, {destBytes / 1024 / 1024:N1} MB (integrity OK)";
                _state.Verdict = XboxTransferVerdict.FullSkip; // success
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

    /// <summary>
    /// Prepares the game for network overlay transfer by building the manifest.
    /// Returns the overlay manifest that describes every file relative to the Content\ folder.
    /// Does NOT copy files; streaming is handled by XboxNetworkSender.
    /// </summary>
    public Task<XboxOverlayManifest> PrepareForNetworkAsync(CancellationToken ct = default)
    {
        _state.IsNetwork = true;
        _state.CurrentStep = XboxTransferStep.ValidatingSource;
        _state.StatusMessage = "Building overlay manifest...";
        RaiseStateChanged();

        var contentDir = Path.Combine(_state.SourcePath, "Content");
        if (!Directory.Exists(contentDir))
        {
            throw new InvalidOperationException("Content subfolder not found; cannot build manifest");
        }

        var entries = new List<XboxOverlayManifestEntry>();
        var sourceFiles = new List<FileInfo>();
        long totalBytes = 0;
        int totalFiles = 0;

        foreach (var file in new DirectoryInfo(contentDir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(_state.SourcePath, file.FullName);
            entries.Add(new XboxOverlayManifestEntry
            {
                RelativePath = relativePath,
                Size = file.Length,
                LastModifiedUtc = file.LastWriteTimeUtc
            });
            sourceFiles.Add(file);
            totalBytes += file.Length;
            totalFiles++;
        }

        var manifest = new XboxOverlayManifest
        {
            ContentGuid = _state.ContentGuid,
            PackageFamilyName = _state.PackageFamilyName,
            SourcePath = _state.SourcePath,
            TotalFiles = totalFiles,
            TotalBytes = totalBytes,
            Entries = entries
        };

        _state.SourceFileCount = totalFiles;
        _state.SourceBytes = totalBytes;
        _state.CurrentStep = XboxTransferStep.Complete;
        _state.StatusMessage = $"Manifest ready: {totalFiles} files, {totalBytes / 1024 / 1024:N1} MB";
        RaiseStateChanged();

        return Task.FromResult(manifest);
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

    private void WriteTransferSummary(string destPath, int filesCopied, long bytesCopied, int robocopyExit, IntegrityWalkResult? integrity = null)
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
            UnreadableFiles = Array.Empty<string>(),
            SkippedFiles = integrity?.SkippedFiles ?? 0,
            MissingFiles = integrity?.MissingFiles ?? new List<string>(),
            MismatchFiles = integrity?.MismatchFiles ?? new List<string>(),
            IntegrityOk = integrity?.IntegrityOk ?? false,
            FilesCopied = filesCopied,
            BytesCopied = bytesCopied,
            RobocopyExit = robocopyExit,
            RobocopyLog = ""
        };

        var summaryPath = Path.Combine(destPath, "transfer-summary.json");
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(summaryPath, json);
        Log($"Wrote transfer-summary.json to {summaryPath}");
    }

    private IntegrityWalkResult RunIntegrityWalk(string sourcePath, string destPath)
    {
        var result = new IntegrityWalkResult();
        var sourceFiles = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var file in new DirectoryInfo(sourcePath).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourcePath, file.FullName);
                sourceFiles[rel] = file.Length;
            }
        }
        catch (UnauthorizedAccessException)
        {
            result.SkippedFiles++;
        }

        try
        {
            foreach (var file in new DirectoryInfo(destPath).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(destPath, file.FullName);
                if (rel.Equals("transfer-summary.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                result.DestFileCount++;
                result.DestBytes += file.Length;

                if (!sourceFiles.TryGetValue(rel, out var expectedSize))
                {
                    result.SkippedFiles++;
                    continue;
                }

                if (file.Length != expectedSize)
                {
                    result.MismatchFiles.Add(rel);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            result.SkippedFiles++;
        }

        foreach (var kvp in sourceFiles)
        {
            var destFile = Path.Combine(destPath, kvp.Key);
            if (!File.Exists(destFile))
            {
                result.MissingFiles.Add(kvp.Key);
            }
        }

        result.IntegrityOk = result.MissingFiles.Count == 0 && result.MismatchFiles.Count == 0;
        return result;
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

    private class IntegrityWalkResult
    {
        public int DestFileCount { get; set; }
        public long DestBytes { get; set; }
        public int SkippedFiles { get; set; }
        public List<string> MissingFiles { get; set; } = new();
        public List<string> MismatchFiles { get; set; } = new();
        public bool IntegrityOk { get; set; }
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