using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text.Json;
using System.Text.RegularExpressions;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

/// <summary>
/// Sender side of the Xbox MSIXVC transfer: stages an installed Game Pass
/// title to a portable destination so a receiver can overlay it onto a
/// paused install.
///
/// This is a thin wrapper around the proven xbox-transfer-sender.ps1 script
/// (see PLANNING/xbox-validation/MSIXVC-TRANSFER-SOLVED.md). The script
/// self-elevates, re-launches as SYSTEM via PsExec, stops Gaming Services,
/// robocopies the install, and rescues content-protected executables via the
/// game's package context. The app must already be elevated so the script's
/// Assert-Elevated is a no-op and its stdout stays capturable.
/// </summary>
[SupportedOSPlatform("windows")]
public class XboxSenderService
{
    // Matches the script's source-scan line, e.g.
    //   [SYSTEM phase] Files: 198, Bytes: 12,345,678 (11.8 MB), Unreadable: 2
    private static readonly Regex SourceScanLine =
        new(@"Bytes:\s*([\d,]+)", RegexOptions.Compiled);

    private CancellationTokenSource? _cts;
    private long _totalSourceBytes;
    private long _stagingBaselineBytes = -1;

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

        // MSIXVC indicators: .xvi/.xvs/.xct envelope files and a Content\ subfolder.
        var xviFiles = Directory.GetFiles(sourcePath, "*.xvi");
        var xvsFiles = Directory.GetFiles(sourcePath, "*.xvs");
        var xctFiles = Directory.GetFiles(sourcePath, "*.xct");
        var contentDir = Path.Combine(sourcePath, "Content");

        if (xviFiles.Length == 0 && xvsFiles.Length == 0 && xctFiles.Length == 0)
            return "No MSIXVC envelope files (.xvi/.xvs/.xct) found - not a valid Xbox install";

        if (!Directory.Exists(contentDir))
            return "Content subfolder not found - not a valid Xbox install";

        _state.SourcePath = sourcePath;
        _state.GameName = Path.GetFileName(sourcePath);

        if (xviFiles.Length > 0)
            _state.ContentGuid = Path.GetFileNameWithoutExtension(xviFiles[0]);

        _state.PackageFamilyName = ExtractPfnFromAcl(sourcePath) ?? "";

        // Best-effort size estimate. Protected installs may deny enumeration;
        // the script does the authoritative scan as SYSTEM.
        try
        {
            long totalBytes = 0;
            int totalFiles = 0;
            foreach (var f in new DirectoryInfo(sourcePath)
                         .EnumerateFiles("*", SearchOption.AllDirectories))
            {
                totalBytes += f.Length;
                totalFiles++;
            }
            _state.SourceFileCount = totalFiles;
            _state.SourceBytes = totalBytes;
        }
        catch
        {
            _state.SourceFileCount = 0;
            _state.SourceBytes = 0;
        }

        return null;
    }

    /// <summary>
    /// Stages the Xbox game to a local/shared drive by running
    /// xbox-transfer-sender.ps1. The script appends the game folder name to
    /// <paramref name="destinationPath"/> and writes transfer-summary.json
    /// into the staged folder.
    /// </summary>
    public async Task StageToFolderAsync(string destinationPath, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        try
        {
            _state.DestinationPath = destinationPath;
            _state.CurrentStep = XboxTransferStep.CopyingFiles;
            _state.OverlayProgress = 0;
            _state.StatusMessage = "Staging via xbox-transfer-sender.ps1...";
            RaiseStateChanged();

            XboxScriptHost host;
            try
            {
                host = XboxScriptHost.Deploy();
            }
            catch (Exception ex)
            {
                Fail($"Could not deploy transfer scripts: {ex.Message}");
                return;
            }

            var args = new List<string>
            {
                "-GameFolder", _state.SourcePath,
                "-Destination", destinationPath,
            };

            var stagedDir = Path.Combine(destinationPath, _state.GameName);
            _totalSourceBytes = _state.SourceBytes;
            _stagingBaselineBytes = -1;

            // Robocopy runs with /NP, so it emits no percentage. Poll the staged
            // folder's size against the source total to drive a real progress bar.
            using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var progressTask = PollStagingProgressAsync(stagedDir, progressCts.Token);

            int exitCode = await host.RunAsync(host.SenderScript, args, line =>
            {
                Log(line);
                var scan = SourceScanLine.Match(line);
                if (scan.Success &&
                    long.TryParse(scan.Groups[1].Value.Replace(",", ""), out var total) && total > 0)
                {
                    _totalSourceBytes = total;
                }
                if (line.Contains("robocopy exit"))
                {
                    _state.OverlayProgress = 100;
                    progressCts.Cancel();
                }
                ApplyProgressLine(line);
            }, confirmPausePrompt: false, ct: token);

            progressCts.Cancel();
            try { await progressTask; } catch { }

            Log($"Sender script exit code: {exitCode}");

            var summaryPath = Path.Combine(stagedDir, "transfer-summary.json");

            if (exitCode != 0 || !File.Exists(summaryPath))
            {
                var reason = exitCode == 10
                    ? "Staged copy failed its integrity check - files are missing or mismatched. " +
                      "Close the Xbox app and the game, then retry."
                    : exitCode >= 8
                        ? $"Robocopy reported a fatal error (exit {exitCode})."
                        : $"Sender script exited with code {exitCode}.";
                Fail(reason);
                return;
            }

            ApplySummary(summaryPath, stagedDir);
        }
        catch (OperationCanceledException)
        {
            Fail("Staging cancelled");
        }
        catch (Exception ex)
        {
            Fail($"Staging error: {ex.Message}");
        }
    }

    private void ApplySummary(string summaryPath, string stagedDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(summaryPath));
            var root = doc.RootElement;

            long bytesCopied = GetLong(root, "BytesCopied");
            int filesCopied = GetInt(root, "FilesCopied");
            bool integrityOk = root.TryGetProperty("IntegrityOk", out var iok) &&
                               iok.ValueKind == JsonValueKind.True;

            int receiverProvided = root.TryGetProperty("ReceiverProvidedFiles", out var rp) &&
                                   rp.ValueKind == JsonValueKind.Array
                ? rp.GetArrayLength()
                : 0;
            int stagedProtected = root.TryGetProperty("StagedProtectedFiles", out var sp) &&
                                  sp.ValueKind == JsonValueKind.Array
                ? sp.GetArrayLength()
                : 0;

            if (root.TryGetProperty("PackageFamilyName", out var pfn) && pfn.ValueKind == JsonValueKind.String)
                _state.PackageFamilyName = pfn.GetString() ?? _state.PackageFamilyName;

            _state.DestinationPath = stagedDir;

            if (!integrityOk)
            {
                Fail("Staged copy is incomplete (integrity check failed). " +
                     "Close the Xbox app and the game on this PC, then retry.");
                return;
            }

            _state.CurrentStep = XboxTransferStep.Complete;
            var mb = bytesCopied / 1024d / 1024d;

            if (receiverProvided > 0)
            {
                _state.Verdict = XboxTransferVerdict.DeltaOnly;
                _state.StatusMessage =
                    $"Staged {filesCopied} files ({mb:N1} MB). {receiverProvided} protected " +
                    "executable(s) could not be rescued - the receiver's Xbox app must " +
                    "download those before pausing.";
            }
            else
            {
                _state.Verdict = XboxTransferVerdict.FullSkip;
                var rescued = stagedProtected > 0 ? $" ({stagedProtected} protected EXE(s) rescued)" : "";
                _state.StatusMessage =
                    $"Staging complete: {filesCopied} files, {mb:N1} MB{rescued}. " +
                    "The staged copy is complete - move it to the receiver.";
            }

            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            Fail($"Could not read transfer-summary.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the live status from a notable script output line.
    /// </summary>
    private void ApplyProgressLine(string line)
    {
        string? status = line switch
        {
            _ when line.Contains("Scanning source") => "Scanning the installed game...",
            _ when line.Contains("robocopy starting") => "Copying game files...",
            _ when line.Contains("Verifying staged copy integrity") => "Verifying the staged copy...",
            _ when line.Contains("Rescuing") && line.Contains("protected") =>
                "Rescuing protected executables via package context...",
            _ when line.Contains("Stopping Gaming Services") => "Stopping Gaming Services...",
            _ => null,
        };

        if (status != null)
        {
            _state.StatusMessage = status;
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// While the sender script copies, periodically measures the staged folder's
    /// size and reports it as a percentage of the source total.
    /// </summary>
    private async Task PollStagingProgressAsync(string stagedDir, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                if (!Directory.Exists(stagedDir))
                    continue;

                long bytes = 0;
                int files = 0;
                foreach (var f in new DirectoryInfo(stagedDir)
                             .EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    bytes += f.Length;
                    files++;
                }

                // First measurement: capture the pre-copy baseline so we track
                // only the delta written by robocopy. Without this, a previous
                // staging to the same destination would make progress jump to
                // ~100% immediately.
                if (_stagingBaselineBytes < 0)
                    _stagingBaselineBytes = bytes;

                long copied = bytes - _stagingBaselineBytes;
                long total  = _totalSourceBytes > 0
                    ? _totalSourceBytes - _stagingBaselineBytes
                    : 0;

                double copiedMb = copied / 1024d / 1024d;
                if (total > 0)
                {
                    _state.OverlayProgress = Math.Min(100.0, Math.Max(0, copied * 100.0 / total));
                    _state.StatusMessage =
                        $"Copying: {copiedMb:N0} / {total / 1024d / 1024d:N0} MB " +
                        $"({_state.OverlayProgress:N0}%)";
                }
                else if (_totalSourceBytes > 0)
                {
                    // Re-stage: baseline ≈ total, so delta tracking isn't useful.
                    // Show overall bytes as indeterminate progress.
                    _state.StatusMessage = $"Re-staging: {bytes / 1024d / 1024d:N0} MB ({files} files)...";
                }
                else
                {
                    _state.StatusMessage = $"Copying: {files} files, {bytes / 1024d / 1024d:N0} MB...";
                }
                RaiseStateChanged();
            }
            catch
            {
                // Enumeration can race with robocopy writing files; ignore.
            }
        }
    }

    /// <summary>
    /// Builds the overlay manifest for a network transfer. Network streaming
    /// is deferred (see UI-INTEGRATION.md) - this remains for a future phase.
    /// </summary>
    public Task<XboxOverlayManifest> PrepareForNetworkAsync(CancellationToken ct = default)
    {
        _state.IsNetwork = true;
        var contentDir = Path.Combine(_state.SourcePath, "Content");
        if (!Directory.Exists(contentDir))
            throw new InvalidOperationException("Content subfolder not found; cannot build manifest");

        var entries = new List<XboxOverlayManifestEntry>();
        long totalBytes = 0;
        int totalFiles = 0;

        foreach (var file in new DirectoryInfo(contentDir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(new XboxOverlayManifestEntry
            {
                RelativePath = Path.GetRelativePath(_state.SourcePath, file.FullName),
                Size = file.Length,
                LastModifiedUtc = file.LastWriteTimeUtc,
            });
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
            Entries = entries,
        };

        _state.SourceFileCount = totalFiles;
        _state.SourceBytes = totalBytes;
        _state.CurrentStep = XboxTransferStep.Complete;
        RaiseStateChanged();
        return Task.FromResult(manifest);
    }

    public void Cancel() => _cts?.Cancel();

    public void Reset()
    {
        _state = new XboxTransferState();
        _cts?.Dispose();
        _cts = null;
        _stagingBaselineBytes = -1;
    }

    private void Fail(string message)
    {
        _state.CurrentStep = XboxTransferStep.Failed;
        _state.ErrorMessage = message;
        _state.StatusMessage = message;
        _state.Verdict = XboxTransferVerdict.Error;
        RaiseStateChanged();
    }

    private static long GetLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;

    private static int GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 0;

    [SupportedOSPlatform("windows")]
    private static string? ExtractPfnFromAcl(string dir)
    {
        try
        {
            var security = new DirectoryInfo(dir).GetAccessControl();
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

    private void Log(string message) => LogMessage?.Invoke(this, $"[Xbox Sender] {message}");

    private void RaiseStateChanged() => StateChanged?.Invoke(this, _state);
}
