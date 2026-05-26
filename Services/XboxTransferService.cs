using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

/// <summary>
/// Receiver side of the Xbox MSIXVC transfer: overlays a pre-staged copy onto
/// an Xbox app-initiated, paused install, then measures the cost of Resume.
///
/// This is a thin wrapper around the proven xbox-transfer-receiver-overlay.ps1
/// script (see PLANNING/xbox-validation/MSIXVC-TRANSFER-SOLVED.md). The script
/// self-elevates, re-launches as SYSTEM via PsExec, locates the in-progress
/// install folder, verifies receiver-provided executables, overlays the stage,
/// resets ACLs, and observes the NIC after Resume. The app must already be
/// elevated so the script's Assert-Elevated is a no-op and stdout stays
/// capturable.
/// </summary>
[SupportedOSPlatform("windows")]
public class XboxTransferService
{
    private static readonly Regex SampleLine =
        new(@"t\+\s*(\d+)s\s+rx=\s*([\d.,]+)\s*MB\s+installed=(\w+)", RegexOptions.Compiled);

    private CancellationTokenSource? _cts;
    private string? _overlayDestPath;
    private XboxNetworkReceiver? _activeReceiver;

    /// <summary>
    /// When true, the overlay script runs in a visible PowerShell window
    /// (useful for debugging). When false (default), the script runs hidden
    /// and output is captured to the app log.
    /// </summary>
    public bool ShowScriptWindow { get; set; }
#if DEBUG
        = true;
#endif

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
            return "transfer-summary.json not found - this folder is not a staged Xbox copy";

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(summaryPath));
            var root = doc.RootElement;

            _state.GameName = GetString(root, "GameName");
            _state.PackageFamilyName = GetString(root, "PackageFamilyName");
            _state.SourceBytes = GetLong(root, "SourceBytes");
            _state.SourceFileCount = GetInt(root, "SourceFileCount");
            _state.SourcePath = sourcePath;
        }
        catch (Exception ex)
        {
            return $"Error reading transfer-summary.json: {ex.Message}";
        }

        var xviFiles = Directory.GetFiles(sourcePath, "*.xvi");
        if (xviFiles.Length == 0)
            return "No .xvi envelope file found - not a valid MSIXVC staged copy";
        _state.ContentGuid = Path.GetFileNameWithoutExtension(xviFiles[0]);

        return null;
    }

    /// <summary>
    /// Runs the overlay workflow by invoking xbox-transfer-receiver-overlay.ps1.
    /// The caller must have had the user click Install then Pause in the Xbox
    /// app before calling this.
    /// </summary>
    /// <param name="xboxRoot">
    /// Optional override for the Xbox install root (e.g. "D:\XboxGames") when
    /// the title is being installed off the system drive.
    /// </param>
    /// <param name="force">
    /// When true, passes -Force to the script so it overlays even if the staged
    /// copy looks incomplete or receiver-provided executables are not ready.
    /// </param>
    public async Task<XboxTransferVerdict> RunOverlayAsync(
        string? xboxRoot = null, bool force = false, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Safety-net timeout only — large titles (50+ GB) can take a long
        // time to robocopy, so this is generous. Cancel via the UI if the
        // script is actually wedged.
        _cts.CancelAfter(TimeSpan.FromHours(6));

        try
        {
            return await RunOverlayScriptAsync(xboxRoot, force, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return Fail(ct.IsCancellationRequested
                ? "Transfer cancelled."
                : "Transfer exceeded the 6-hour safety timeout. If the PowerShell " +
                  "window is still making progress, re-run with a larger window; " +
                  "otherwise check the script log for the actual stall point.");
        }
        catch (Exception ex)
        {
            return Fail($"Transfer error: {ex.Message}");
        }
    }

    /// <summary>
    /// Network (LAN peer) overlay. Downloads the staged game from a peer over
    /// TCP, then runs the same overlay script as the drive-based flow.
    /// The sender must have staged the game first (to rescue content-protected
    /// executables) and be serving it via <see cref="XboxNetworkSender"/>.
    /// </summary>
    public async Task<XboxTransferVerdict> RunNetworkOverlayAsync(
        string peerHost, int peerPort, string? xboxRoot = null,
        bool force = false, CancellationToken ct = default,
        string? gameAppId = null)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Network downloads can be slow on WiFi; 60-minute timeout.
        _cts.CancelAfter(TimeSpan.FromMinutes(60));
        var token = _cts.Token;

        string? tempFolder = null;
        try
        {
            // Phase 1: Download from peer
            _state.CurrentStep = XboxTransferStep.DownloadingFromPeer;
            _state.IsNetwork = true;
            _state.OverlayProgress = 0;
            _state.StatusMessage = $"Connecting to {peerHost}:{peerPort}...";
            RaiseStateChanged();

            // Use a stable folder keyed by game AppId so partial downloads
            // survive across retries and the receiver can skip existing files.
            var folderKey = gameAppId ?? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            tempFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GamesLocalShare", "xbox-network-download",
                folderKey);
            Directory.CreateDirectory(tempFolder);

            var receiver = new XboxNetworkReceiver();
            _activeReceiver = receiver;
            receiver.ProgressChanged += (_, pct) =>
            {
                _state.OverlayProgress = pct;
                var speedText = _state.NetworkSpeedMBps > 0
                    ? $" @ {_state.NetworkSpeedMBps:N1} MB/s"
                    : "";
                _state.StatusMessage = $"Downloading from peer: {pct:N1}%{speedText}";
                RaiseStateChanged();
            };
            receiver.BytesReceivedChanged += (_, bytes) =>
            {
                _state.NetworkReceivedMB = bytes / 1024.0 / 1024.0;
            };
            receiver.SpeedChanged += (_, bps) =>
            {
                _state.NetworkSpeedMBps = bps / 1024.0 / 1024.0;
                // Compute ETA from speed and remaining bytes
                if (bps > 0 && receiver.TotalBytes > 0)
                {
                    var remainingBytes = receiver.TotalBytes - receiver.BytesReceived;
                    var etaSeconds = (double)remainingBytes / bps;
                    if (etaSeconds < 3600)
                        _state.NetworkEta = TimeSpan.FromSeconds(etaSeconds).ToString(@"mm\:ss");
                    else
                        _state.NetworkEta = TimeSpan.FromSeconds(etaSeconds).ToString(@"h\:mm\:ss");
                }
                else
                {
                    _state.NetworkEta = "";
                }
            };
            receiver.LogMessage += (_, msg) => Log(msg);

            await receiver.ReceiveAsync(peerHost, peerPort, tempFolder, token);
            _activeReceiver = null;

            Log($"Network download complete: {receiver.BytesReceived} bytes to {tempFolder}");

            // Sanity check: if we received far less than expected, the download
            // was truncated (e.g. connection lost, protocol error). Don't proceed
            // to the overlay script with incomplete data.
            if (receiver.TotalBytes > 0 && receiver.BytesReceived < receiver.TotalBytes * 0.95)
            {
                var pct = receiver.TotalBytes > 0
                    ? (double)receiver.BytesReceived / receiver.TotalBytes * 100 : 0;
                return Fail(
                    $"Network download incomplete: received {receiver.BytesReceived / 1024.0 / 1024.0:N1} MB " +
                    $"of {receiver.TotalBytes / 1024.0 / 1024.0:N1} MB ({pct:N1}%). " +
                    "Check your network connection and try again.");
            }

            // Generate transfer-summary.json from the received manifest so
            // ValidateSource and the overlay PS1 script find the metadata they expect.
            var manifest = receiver.ReceivedManifest;
            if (manifest != null)
            {
                var summary = new Dictionary<string, object>
                {
                    ["GameName"] = manifest.GameName,
                    ["PackageFamilyName"] = manifest.PackageFamilyName,
                    ["SourceBytes"] = manifest.TotalBytes,
                    ["SourceFileCount"] = manifest.TotalFiles,
                    ["FilesCopied"] = manifest.Entries.Count,
                    ["BytesCopied"] = receiver.BytesReceived,
                    ["IntegrityOk"] = true,
                    ["SkippedFiles"] = 0,
                    ["ReceiverProvidedFiles"] = manifest.SkippedProtectedFiles
                        .Select(s => new { Path = s.RelativePath, Size = s.ExpectedSize })
                        .ToArray(),
                };
                var summaryJson = JsonSerializer.Serialize(summary,
                    new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(
                    Path.Combine(tempFolder, "transfer-summary.json"), summaryJson, token);
                Log($"Generated transfer-summary.json from manifest");
            }

            // Phase 2: Validate downloaded folder (sets _state.SourcePath etc.)
            _state.StatusMessage = "Validating downloaded files...";
            RaiseStateChanged();

            var validationError = ValidateSource(tempFolder);
            if (validationError != null)
                return Fail($"Downloaded files invalid: {validationError}");

            // Phase 3: Run the overlay script. For network transfers, always
            // force the overlay past the receiver-provided-exe check. The sender
            // may have been unable to rescue protected EXEs, and the receiver's
            // Xbox download is typically paused very early (before those EXEs
            // arrive). Forcing lets the overlay proceed; Gaming Services will
            // re-download just the missing EXEs during Resume (small delta).
            return await RunOverlayScriptAsync(xboxRoot, force: true, token);
        }
        catch (OperationCanceledException)
        {
            return Fail(ct.IsCancellationRequested
                ? "Transfer cancelled."
                : "Network transfer timed out. Check that the sender is still " +
                  "running and both PCs are on the same network.");
        }
        catch (IOException ex)
        {
            return Fail($"Connection error: {ex.Message}. Make sure the sender is " +
                        "still running and the firewall allows port {peerPort}.");
        }
        catch (Exception ex)
        {
            return Fail($"Network transfer error: {ex.Message}");
        }
        finally
        {
            // Clean up temp folder on success
            if (tempFolder != null &&
                _state.Verdict is XboxTransferVerdict.FullSkip or XboxTransferVerdict.DeltaOnly)
            {
                try { Directory.Delete(tempFolder, true); }
                catch { Log($"Could not clean up temp folder: {tempFolder}"); }
            }
        }
    }

    /// <summary>
    /// Core overlay script execution shared by <see cref="RunOverlayAsync"/> and
    /// <see cref="RunNetworkOverlayAsync"/>. Expects <see cref="_state"/> to
    /// already have SourcePath, GameName, ContentGuid populated (via
    /// <see cref="ValidateSource"/>).
    /// </summary>
    private async Task<XboxTransferVerdict> RunOverlayScriptAsync(
        string? xboxRoot, bool force, CancellationToken token)
    {
        _state.CurrentStep = XboxTransferStep.PollingForFolder;
        _state.OverlayProgress = 0;
        _state.StatusMessage = "Starting overlay transfer...";
        _overlayDestPath = null;
        RaiseStateChanged();

        XboxScriptHost host;
        try
        {
            host = XboxScriptHost.Deploy();
        }
        catch (Exception ex)
        {
            return Fail($"Could not deploy transfer scripts: {ex.Message}");
        }

        var args = new List<string> { "-Source", _state.SourcePath };
        if (!string.IsNullOrWhiteSpace(xboxRoot))
        {
            args.Add("-XboxRoot");
            args.Add(xboxRoot!);
        }
        if (force)
            args.Add("-Force");
        args.Add("-AutoConfirm");

        _state.StatusMessage = ShowScriptWindow
            ? "Script running in PowerShell window..."
            : "Running overlay script...";
        RaiseStateChanged();

        var transferStartedAt = DateTime.UtcNow;
        using var logPollCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var logPollTask = PollSystemLogAsync(host.RunsDir, transferStartedAt, logPollCts.Token);
        var progressTask = PollOverlayProgressAsync(logPollCts.Token);

        int exitCode;
        if (ShowScriptWindow)
        {
            exitCode = await host.RunVisibleAsync(
                host.ReceiverScript, args, ct: token,
                cancelSentinelName: "cancel-receiver-overlay.sentinel");
        }
        else
        {
            exitCode = await host.RunAsync(
                host.ReceiverScript, args,
                onOutput: line => Log(line),
                confirmPausePrompt: true,
                ct: token,
                cancelSentinelName: "cancel-receiver-overlay.sentinel");
        }

        logPollCts.Cancel();
        try { await logPollTask; } catch { }
        try { await progressTask; } catch { }

        Log($"Receiver script exit code: {exitCode}");

        string Detail()
        {
            var err = host.LatestSystemError("receiver-overlay");
            return err == null ? "" : $"\n\nScript output:\n{err}";
        }

        return exitCode switch
        {
            0 => ApplyVerdict(host.LatestVerdictFile()),
            2 => Fail("The Xbox install folder never appeared. Make sure you clicked " +
                      "Install in the Xbox app on this PC, and that the title is an " +
                      "MSIXVC game (its Manage > Files menu shows an install-drive " +
                      "picker). If it installs to a non-system drive, set the Xbox " +
                      "install drive in Advanced options."),
            11 => Fail("The staged copy is incomplete - the sender's integrity check " +
                       "failed. Re-stage the game on the sender PC, or enable 'Force " +
                       "overlay' to proceed anyway."),
            12 => Fail("Some protected executables have not downloaded yet. In the Xbox " +
                       "app: click Resume, let the install grow a few hundred MB, click " +
                       "Pause, then retry - or enable 'Force overlay' to proceed anyway."),
            99 => Fail("Elevation error. Restart the app as Administrator and retry."),
            _ => Fail($"Receiver script exited with code {exitCode}.{Detail()}"),
        };
    }

    private XboxTransferVerdict ApplyVerdict(string? verdictPath)
    {
        if (verdictPath == null || !File.Exists(verdictPath))
            return Fail("The transfer ran but produced no verdict file.");

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(verdictPath));
            var root = doc.RootElement;

            var hypothesis = GetString(root, "Hypothesis");
            double rxMb = root.TryGetProperty("ObservedReceivedMB", out var rx) &&
                          rx.ValueKind == JsonValueKind.Number
                ? rx.GetDouble()
                : _state.NetworkReceivedMB;

            bool installed = false;
            if (root.TryGetProperty("FinalState", out var fs) &&
                fs.ValueKind == JsonValueKind.Object &&
                fs.TryGetProperty("Installed", out var inst))
            {
                installed = inst.ValueKind == JsonValueKind.True;
            }

            _state.NetworkReceivedMB = rxMb;
            _state.PackageInstalled = installed;

            _state.Verdict = MapHypothesis(hypothesis);

            bool success = _state.Verdict is XboxTransferVerdict.FullSkip or XboxTransferVerdict.DeltaOnly;
            _state.CurrentStep = success ? XboxTransferStep.Complete : XboxTransferStep.Failed;
            _state.StatusMessage = success
                ? $"Transfer complete - {rxMb:N1} MB downloaded, package installed={installed}."
                : $"Transfer result: {hypothesis} ({rxMb:N1} MB downloaded).";

            if (!success)
                _state.ErrorMessage = _state.StatusMessage;

            RaiseStateChanged();
            return _state.Verdict;
        }
        catch (Exception ex)
        {
            return Fail($"Could not read the verdict file: {ex.Message}");
        }
    }

    /// <summary>
    /// Maps notable script output lines onto the live transfer state so the UI
    /// can show progress and prompt the user to click Resume.
    /// </summary>
    private void ApplyProgressLine(string line)
    {
        var sample = SampleLine.Match(line);
        if (sample.Success)
        {
            if (double.TryParse(sample.Groups[2].Value.Replace(",", ""),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var mb))
            {
                _state.NetworkReceivedMB = mb;
            }
            _state.PackageInstalled = string.Equals(sample.Groups[3].Value, "True",
                StringComparison.OrdinalIgnoreCase);
            _state.CurrentStep = XboxTransferStep.Monitoring;
            _state.StatusMessage =
                $"Monitoring install: t+{sample.Groups[1].Value}s, " +
                $"{_state.NetworkReceivedMB:N1} MB downloaded.";
            RaiseStateChanged();
            return;
        }

        // Capture the install folder the script settled on, so the progress
        // poller knows which directory to measure.
        const string destMarker = "Final destination:";
        var destIdx = line.IndexOf(destMarker, StringComparison.Ordinal);
        if (destIdx >= 0)
        {
            var p = line[(destIdx + destMarker.Length)..].Trim();
            if (p.Length > 0)
                _overlayDestPath = p;
        }

        XboxTransferStep? step = null;
        string? status = null;

        // [STATUS] lines from the parent (User) phase — these use
        // [Console]::Out.WriteLine so they reach our stdout pipe even
        // though Write-Host does not in Windows PowerShell 5.1.
        if (line.Contains("[STATUS] Validating staged source"))
        {
            step = XboxTransferStep.PollingForFolder;
            status = "Validating staged source...";
        }
        else if (line.Contains("[STATUS] Preparing PsExec"))
        {
            step = XboxTransferStep.PollingForFolder;
            status = "Preparing helper tools...";
        }
        else if (line.Contains("[STATUS] Launching SYSTEM child"))
        {
            step = XboxTransferStep.PollingForFolder;
            status = "Launching transfer as SYSTEM...";
        }
        // Lines from the SYSTEM child (tailed via [Console]::Out.Write)
        else if (line.Contains("Polling for in-progress install"))
        {
            step = XboxTransferStep.PollingForFolder;
            status = "Waiting for the Xbox install folder to appear...";
        }
        else if (line.Contains("Overlay robocopy starting"))
        {
            step = XboxTransferStep.Overlaying;
            status = "Overlaying staged files onto the install...";
        }
        else if (line.Contains("Resetting ACLs"))
        {
            step = XboxTransferStep.ResettingAcls;
            status = "Resetting file permissions...";
        }
        else if (line.Contains("click Resume in the Xbox app"))
        {
            step = XboxTransferStep.WaitingForResume;
            status = "Overlay done. Click RESUME in the Xbox app now.";
            _state.OverlayProgress = 0;
        }

        if (step != null)
        {
            _state.CurrentStep = step.Value;
            _state.StatusMessage = status ?? _state.StatusMessage;
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// While the overlay robocopy runs, measures the install folder against the
    /// staged source size to drive a progress bar.
    /// </summary>
    private async Task PollOverlayProgressAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { return; }

            if (_overlayDestPath == null ||
                _state.CurrentStep != XboxTransferStep.Overlaying ||
                _state.SourceBytes <= 0)
            {
                continue;
            }

            try
            {
                if (!Directory.Exists(_overlayDestPath))
                    continue;

                long completedBytes = 0;
                foreach (var f in new DirectoryInfo(_overlayDestPath)
                             .EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    // Large files being written by robocopy are pre-allocated
                    // to their full size instantly. Detect in-progress files
                    // by checking if another process holds them open.
                    if (f.Length >= 1024 * 1024 && IsFileBeingWritten(f.FullName))
                        continue;
                    completedBytes += f.Length;
                }

                // The overlay robocopy uses /IS (include Same) — it re-copies
                // ALL files, so a baseline delta approach doesn't work (existing
                // files get locked and drop out of the count temporarily).
                // Instead, compare completed bytes directly against source total.
                long total = _state.SourceBytes;
                if (total <= 0) total = 1;

                _state.OverlayProgress = Math.Min(100.0, completedBytes * 100.0 / total);
                _state.StatusMessage =
                    $"Overlaying: {completedBytes / 1024d / 1024d:N0} / " +
                    $"{total / 1024d / 1024d:N0} MB ({_state.OverlayProgress:N0}%)";
                RaiseStateChanged();
            }
            catch
            {
                // Enumeration can race with robocopy writing files; ignore.
            }
        }
    }

    /// <summary>
    /// Returns true if another process (e.g. robocopy) currently holds the
    /// file open for writing. Robocopy pre-allocates the full file size
    /// before writing data, so FileInfo.Length is misleading for in-progress
    /// files — this lock check is the only reliable way to tell.
    /// </summary>
    private static bool IsFileBeingWritten(string path)
    {
        try
        {
            // Request exclusive access — fails if any other process has the file open
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false; // Got exclusive access → file is complete
        }
        catch (IOException)
        {
            return true; // Sharing violation → another process is writing
        }
        catch
        {
            return false; // Permission error etc. → not a write lock, count normally
        }
    }

    /// <summary>
    /// Tails the most recent SYSTEM-phase log file in <paramref name="runsDir"/>
    /// and feeds new lines through <see cref="ApplyProgressLine"/> so the UI
    /// updates even when the script runs in a visible window with no stdout
    /// redirection.
    /// </summary>
    private async Task PollSystemLogAsync(string runsDir, DateTime startedAfterUtc, CancellationToken ct)
    {
        string? logPath = null;
        long lastLen = 0;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(800, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                // Find the latest log file created AFTER this transfer started,
                // so we never pick up stale output from a previous run.
                if (logPath == null || !File.Exists(logPath))
                {
                    logPath = Directory.Exists(runsDir)
                        ? Directory.EnumerateFiles(runsDir, "receiver-overlay-system-*.log")
                            .Select(f => new FileInfo(f))
                            .Where(fi => fi.CreationTimeUtc >= startedAfterUtc.AddSeconds(-5))
                            .OrderByDescending(fi => fi.LastWriteTimeUtc)
                            .Select(fi => fi.FullName)
                            .FirstOrDefault()
                        : null;
                    lastLen = 0;
                    if (logPath == null) continue;
                }

                var fi = new FileInfo(logPath);
                if (fi.Length <= lastLen) continue;

                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(lastLen, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                var chunk = await sr.ReadToEndAsync(ct);
                lastLen = fi.Length;

                if (string.IsNullOrEmpty(chunk)) continue;

                foreach (var line in chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.TrimEnd('\r');
                    if (trimmed.Length > 0)
                    {
                        Log(trimmed);
                        ApplyProgressLine(trimmed);
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // File may be locked or not yet created; retry next cycle.
            }
        }
    }

    /// <summary>
    /// Maps a receiver-overlay-verdict.json <c>Hypothesis</c> value onto a
    /// <see cref="XboxTransferVerdict"/>.
    /// </summary>
    public static XboxTransferVerdict MapHypothesis(string hypothesis) => hypothesis switch
    {
        "H1_FULL_SKIP" => XboxTransferVerdict.FullSkip,
        "H2_DELTA" => XboxTransferVerdict.DeltaOnly,
        "H3_FULL_REDOWNLOAD" => XboxTransferVerdict.FullRedownload,
        "STILL_PAUSED_OR_FAILED" => XboxTransferVerdict.StillPaused,
        "PARTIAL_PROGRESS" => XboxTransferVerdict.Pending,
        _ => XboxTransferVerdict.Error,
    };

    public void Cancel() => _cts?.Cancel();

    public void PauseDownload()
    {
        _activeReceiver?.Pause();
        _state.IsPaused = true;
        _state.StatusMessage = "Download paused";
        RaiseStateChanged();
    }

    public void ResumeDownload()
    {
        _activeReceiver?.Resume();
        _state.IsPaused = false;
        RaiseStateChanged();
    }

    public void Reset()
    {
        _state = new XboxTransferState();
        _cts?.Dispose();
        _cts = null;
    }

    private XboxTransferVerdict Fail(string message)
    {
        _state.CurrentStep = XboxTransferStep.Failed;
        _state.ErrorMessage = message;
        _state.StatusMessage = message;
        _state.Verdict = XboxTransferVerdict.Error;
        RaiseStateChanged();
        return XboxTransferVerdict.Error;
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? string.Empty
            : string.Empty;

    private static long GetLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;

    private static int GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 0;

    private void Log(string message) => LogMessage?.Invoke(this, $"[Xbox] {message}");

    private void RaiseStateChanged() => StateChanged?.Invoke(this, _state);
}
