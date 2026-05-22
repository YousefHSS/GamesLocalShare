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
    private long _overlayBaselineBytes = -1;

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
        // Overall timeout: the script has a 90s polling phase, a robocopy
        // phase, and up to 300s of NIC observation. 10 minutes covers all
        // phases with margin; if we're still running after that something
        // is stuck (PsExec download hang, SYSTEM child never started, etc).
        _cts.CancelAfter(TimeSpan.FromMinutes(10));
        var token = _cts.Token;

        try
        {
            _state.CurrentStep = XboxTransferStep.PollingForFolder;
            _state.OverlayProgress = 0;
            _state.StatusMessage = "Starting overlay transfer...";
            _overlayDestPath = null;
            _overlayBaselineBytes = -1;
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

            // Launch the script in a visible PowerShell window so the user
            // can follow all output (robocopy, ACL reset, "click Resume"
            // prompt) with full colors.  Progress in the UI is driven by
            // tailing the SYSTEM-phase log file that the script writes to
            // disk — this avoids the PsExec stdout-piping issues that made
            // the hidden-process approach unreliable.
            _state.StatusMessage = "Script running in PowerShell window...";
            RaiseStateChanged();

            var transferStartedAt = DateTime.UtcNow;
            using var logPollCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var logPollTask = PollSystemLogAsync(host.RunsDir, transferStartedAt, logPollCts.Token);
            var progressTask = PollOverlayProgressAsync(logPollCts.Token);

            int exitCode = await host.RunVisibleAsync(
                host.ReceiverScript, args, ct: token);

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
        catch (OperationCanceledException)
        {
            return Fail(ct.IsCancellationRequested
                ? "Transfer cancelled."
                : "Transfer timed out. The script may be stuck (check network " +
                  "connectivity for PsExec download, or verify the Xbox app " +
                  "install was started and paused).");
        }
        catch (Exception ex)
        {
            return Fail($"Transfer error: {ex.Message}");
        }
    }

    /// <summary>
    /// Network (LAN peer) overlay. Deferred to a later phase - see
    /// UI-INTEGRATION.md. Raw streaming of Content files cannot transfer
    /// content-protected executables; it needs the package-context rescue first.
    /// </summary>
    public Task<XboxTransferVerdict> RunNetworkOverlayAsync(
        string peerHost, int peerPort, CancellationToken ct = default)
    {
        return Task.FromResult(Fail(
            "Network (peer) Xbox transfer is not available yet. Use a staged copy " +
            "on a drive or shared folder instead."));
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
            try { await Task.Delay(1500, ct); }
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

                long bytes = 0;
                foreach (var f in new DirectoryInfo(_overlayDestPath)
                             .EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    bytes += f.Length;
                }

                // First measurement: capture the pre-overlay baseline so
                // we track only the delta written by robocopy.
                if (_overlayBaselineBytes < 0)
                    _overlayBaselineBytes = bytes;

                long copied = bytes - _overlayBaselineBytes;
                long total  = _state.SourceBytes - _overlayBaselineBytes;
                if (total <= 0) total = 1; // avoid div-by-zero

                _state.OverlayProgress = Math.Min(100.0, copied * 100.0 / total);
                _state.StatusMessage =
                    $"Overlaying: {copied / 1024d / 1024d:N0} / " +
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
