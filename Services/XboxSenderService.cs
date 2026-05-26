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
    /// <param name="pfnHint">
    /// Optional PFN from GameInfo (extracted during library scan). Used as
    /// primary source; ACL and PowerShell fallbacks are tried if empty.
    /// </param>
    public string? ValidateSource(string sourcePath, string? pfnHint = null)
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

        // PFN resolution chain: caller hint → ACL extraction → PowerShell fallback
        _state.PackageFamilyName =
            (!string.IsNullOrEmpty(pfnHint) ? pfnHint : null)
            ?? ExtractPfnFromAcl(sourcePath)
            ?? ExtractPfnViaAppxPackage(sourcePath)
            ?? "";

        if (!string.IsNullOrEmpty(_state.PackageFamilyName))
            Log($"PFN resolved: {_state.PackageFamilyName}");
        else
            Log("WARNING: Could not resolve PackageFamilyName — protected exe rescue will be skipped");

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
    /// Extensions that are MSIXVC-protected and need SYSTEM + backup mode to copy.
    /// Everything else can be copied directly with normal File.Copy.
    /// </summary>
    private static readonly HashSet<string> ProtectedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll" };

    /// <summary>
    /// Stages the Xbox game to a local/shared drive. Non-exe/dll files are
    /// copied directly via File.Copy (fast, buffered I/O), then the
    /// xbox-transfer-sender.ps1 script handles the protected executables
    /// via SYSTEM + backup mode.
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
            RaiseStateChanged();

            var stagedDir = Path.Combine(destinationPath, _state.GameName);
            _totalSourceBytes = _state.SourceBytes;
            _stagingBaselineBytes = -1;

            // Phase 1: Fast direct copy of non-protected files
            _state.StatusMessage = "Copying game assets (fast copy)...";
            RaiseStateChanged();
            var preCopyResult = await PreCopyNonProtectedFilesAsync(
                _state.SourcePath, stagedDir, token);
            Log($"Fast copy done: {preCopyResult.CopiedFiles} files, " +
                $"{preCopyResult.CopiedBytes / 1024 / 1024} MB copied, " +
                $"{preCopyResult.SkippedFiles} skipped (already up to date), " +
                $"{preCopyResult.FailedFiles} failed (will retry via script)");

            // Phase 2: Run the script for protected exe/dll files + integrity check
            _state.StatusMessage = "Handling protected executables via script...";
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

            // Robocopy runs with /NP, so it emits no percentage. Poll the staged
            // folder's size against the source total to drive a real progress bar.
            using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var progressTask = PollStagingProgressAsync(stagedDir, progressCts.Token);

            int exitCode = await host.RunAsync(host.SenderScript, args,
                cancelSentinelName: "cancel-sender.sentinel",
                onOutput: line =>
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

    private record PreCopyResult(int CopiedFiles, long CopiedBytes, int SkippedFiles, int FailedFiles);

    /// <summary>
    /// Copies all non-exe/dll files from source to destination using normal
    /// File.Copy (buffered I/O). This is much faster than robocopy /B /J for
    /// large asset files since it uses the filesystem cache.
    /// Files that already exist with matching size are skipped.
    /// </summary>
    private async Task<PreCopyResult> PreCopyNonProtectedFilesAsync(
        string sourcePath, string destPath, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            int copied = 0, skipped = 0, failed = 0;
            long copiedBytes = 0;

            Directory.CreateDirectory(destPath);

            var sourceDir = new DirectoryInfo(sourcePath);
            FileInfo[] allFiles;
            try
            {
                allFiles = sourceDir.GetFiles("*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                Log($"Could not enumerate source: {ex.Message}");
                return new PreCopyResult(0, 0, 0, 0);
            }

            long totalNonProtectedBytes = 0;
            int totalNonProtected = 0;
            foreach (var f in allFiles)
            {
                if (!ProtectedExtensions.Contains(f.Extension))
                {
                    totalNonProtectedBytes += f.Length;
                    totalNonProtected++;
                }
            }

            foreach (var sourceFile in allFiles)
            {
                ct.ThrowIfCancellationRequested();

                // Skip exe/dll — those need SYSTEM + backup mode
                if (ProtectedExtensions.Contains(sourceFile.Extension))
                    continue;

                var relativePath = Path.GetRelativePath(sourcePath, sourceFile.FullName);
                var destFile = Path.Combine(destPath, relativePath);
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                // Skip if destination already matches (same size)
                if (File.Exists(destFile))
                {
                    var destInfo = new FileInfo(destFile);
                    if (destInfo.Length == sourceFile.Length)
                    {
                        skipped++;
                        copiedBytes += sourceFile.Length;
                        continue;
                    }
                }

                try
                {
                    File.Copy(sourceFile.FullName, destFile, overwrite: true);
                    copiedBytes += sourceFile.Length;
                    copied++;
                }
                catch (Exception ex)
                {
                    // File might be locked or ACL-protected — the script will handle it
                    System.Diagnostics.Debug.WriteLine(
                        $"PreCopy skip {relativePath}: {ex.Message}");
                    failed++;
                }

                // Byte-based progress — accurate even with a mix of tiny and huge files
                if (totalNonProtectedBytes > 0)
                {
                    _state.OverlayProgress = (double)copiedBytes / totalNonProtectedBytes * 80; // Reserve 20% for script
                    _state.StatusMessage =
                        $"Fast copy: {copiedBytes / 1024 / 1024} / {totalNonProtectedBytes / 1024 / 1024} MB";
                    RaiseStateChanged();
                }
            }

            return new PreCopyResult(copied, copiedBytes, skipped, failed);
        }, ct);
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
    /// Files currently being written are excluded via a lock check — their full
    /// size is pre-allocated on disk but data hasn't been written yet.
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
                    // Large files being written are pre-allocated to full
                    // size instantly — detect via file lock check.
                    if (f.Length >= 1024 * 1024 && IsFileBeingWritten(f.FullName))
                        continue;

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

    /// <summary>
    /// Builds a manifest directly from the Xbox install folder for network
    /// streaming — no full staging step required. Non-exe/dll files are read
    /// directly. Protected exe/dll files are probed and, if unreadable,
    /// rescued via the package-context helper into a small temp directory.
    /// Returns the manifest and a dictionary of path overrides for rescued files.
    /// </summary>
    public async Task<(XboxOverlayManifest Manifest, Dictionary<string, string> PathOverrides)>
        PrepareForDirectNetworkAsync(CancellationToken ct = default)
    {
        _state.IsNetwork = true;
        _state.CurrentStep = XboxTransferStep.CopyingFiles;
        _state.StatusMessage = "Scanning install folder...";
        RaiseStateChanged();

        var sourcePath = _state.SourcePath;
        var entries = new List<XboxOverlayManifestEntry>();
        var skipped = new List<SkippedProtectedFile>();
        var pathOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        int totalFiles = 0;

        // Enumerate all files
        var allFiles = new DirectoryInfo(sourcePath)
            .EnumerateFiles("*", SearchOption.AllDirectories).ToList();

        // Separate protected vs normal files and probe readability
        var protectedUnreadable = new List<(FileInfo File, string RelPath)>();

        foreach (var file in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            var relPath = Path.GetRelativePath(sourcePath, file.FullName);
            totalBytes += file.Length;
            totalFiles++;

            bool isProtected = ProtectedExtensions.Contains(file.Extension);

            if (isProtected)
            {
                // Probe: can we read this file?
                bool readable = false;
                try
                {
                    using var fs = File.Open(file.FullName, FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    // Try reading first 2 bytes to verify real access
                    var buf = new byte[2];
                    readable = fs.Read(buf, 0, 2) == 2;
                }
                catch { }

                if (readable)
                {
                    // Exe/dll readable directly (rare but possible)
                    entries.Add(new XboxOverlayManifestEntry
                    {
                        RelativePath = relPath,
                        Size = file.Length,
                        LastModifiedUtc = file.LastWriteTimeUtc,
                        IsProtected = true,
                    });
                }
                else
                {
                    protectedUnreadable.Add((file, relPath));
                }
            }
            else
            {
                entries.Add(new XboxOverlayManifestEntry
                {
                    RelativePath = relPath,
                    Size = file.Length,
                    LastModifiedUtc = file.LastWriteTimeUtc,
                });
            }
        }

        Log($"Scan complete: {totalFiles} files, {entries.Count} streamable, " +
            $"{protectedUnreadable.Count} protected-unreadable");

        // Attempt rescue of unreadable protected files via package context
        if (protectedUnreadable.Count > 0 && !string.IsNullOrEmpty(_state.PackageFamilyName))
        {
            _state.StatusMessage = $"Rescuing {protectedUnreadable.Count} protected executable(s)...";
            RaiseStateChanged();

            var rescueDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GamesLocalShare", "xbox-network-rescued",
                _state.GameName);
            Directory.CreateDirectory(rescueDir);

            var relPaths = protectedUnreadable.Select(p => p.RelPath).ToList();
            var rescued = await RescueProtectedFilesAsync(
                _state.PackageFamilyName, sourcePath, rescueDir, relPaths, ct);

            foreach (var (file, relPath) in protectedUnreadable)
            {
                var rescuedPath = Path.Combine(rescueDir, relPath);
                if (rescued.Contains(relPath) && File.Exists(rescuedPath))
                {
                    // Rescued successfully — stream from rescue dir
                    entries.Add(new XboxOverlayManifestEntry
                    {
                        RelativePath = relPath,
                        Size = new FileInfo(rescuedPath).Length,
                        LastModifiedUtc = file.LastWriteTimeUtc,
                        IsProtected = true,
                    });
                    pathOverrides[relPath] = rescuedPath;
                    Log($"Rescued: {relPath}");
                }
                else
                {
                    // Could not rescue — receiver must download via Xbox
                    skipped.Add(new SkippedProtectedFile
                    {
                        RelativePath = relPath,
                        ExpectedSize = file.Length,
                    });
                    Log($"Skipped (receiver must download): {relPath}");
                }
            }
        }
        else if (protectedUnreadable.Count > 0)
        {
            // No PFN — can't rescue, skip all
            foreach (var (file, relPath) in protectedUnreadable)
            {
                skipped.Add(new SkippedProtectedFile
                {
                    RelativePath = relPath,
                    ExpectedSize = file.Length,
                });
            }
            Log($"No PackageFamilyName — {protectedUnreadable.Count} protected files skipped");
        }

        long streamBytes = entries.Sum(e => e.Size);
        var manifest = new XboxOverlayManifest
        {
            ContentGuid = _state.ContentGuid,
            PackageFamilyName = _state.PackageFamilyName,
            SourcePath = sourcePath,
            GameName = _state.GameName,
            SenderHost = Environment.MachineName,
            TotalFiles = totalFiles,
            TotalBytes = totalBytes,
            Entries = entries,
            SkippedProtectedFiles = skipped,
        };

        _state.SourceFileCount = totalFiles;
        _state.SourceBytes = totalBytes;
        _state.CurrentStep = XboxTransferStep.Complete;
        _state.StatusMessage = $"Ready: {entries.Count} files to stream ({streamBytes / 1024 / 1024} MB)" +
            (skipped.Count > 0 ? $", {skipped.Count} exe(s) skipped" : "");
        RaiseStateChanged();

        return (manifest, pathOverrides);
    }

    /// <summary>
    /// Rescues protected exe/dll files by shelling out to the proven PS1
    /// Copy-ProtectedFilesViaPackage helper. Returns the set of relative
    /// paths that were successfully rescued.
    /// </summary>
    private async Task<HashSet<string>> RescueProtectedFilesAsync(
        string pfn, string gameFolder, string destDir,
        List<string> relativePaths, CancellationToken ct)
    {
        var rescued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var host = XboxScriptHost.Deploy();

            // Build a small inline PS1 that calls the common helper
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var argsPath = Path.Combine(host.RunsDir, $"rescue-args-{stamp}.json");
            var resultPath = Path.Combine(host.RunsDir, $"rescue-result-{stamp}.json");

            var argsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                PackageFamilyName = pfn,
                GameFolder = gameFolder,
                DestGame = destDir,
                RelativePaths = relativePaths,
                RunsDir = host.RunsDir,
                ResultPath = resultPath,
            });
            await File.WriteAllTextAsync(argsPath, argsJson, ct);

            // Inline script that loads _common.ps1 and calls the rescue function
            var scriptPath = Path.Combine(host.RunsDir, $"rescue-{stamp}.ps1");
            var script = $@"
$ErrorActionPreference = 'Stop'
. (Join-Path '{host.WorkDir.Replace("'", "''")}' '_common.ps1')
$a = Get-Content -LiteralPath '{argsPath.Replace("'", "''")}' -Raw | ConvertFrom-Json
$result = Copy-ProtectedFilesViaPackage `
    -PackageFamilyName $a.PackageFamilyName `
    -GameFolder $a.GameFolder `
    -DestGame $a.DestGame `
    -RelativePaths @($a.RelativePaths) `
    -RunsDir $a.RunsDir
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $a.ResultPath -Encoding UTF8
if ($result.Ok) {{ exit 0 }} else {{ exit 1 }}
";
            await File.WriteAllTextAsync(scriptPath, script, ct);

            var exitCode = await host.RunAsync(scriptPath, Array.Empty<string>(),
                onOutput: line => Log($"[Rescue] {line}"),
                ct: ct);

            if (File.Exists(resultPath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(
                    await File.ReadAllTextAsync(resultPath, ct));
                var root = doc.RootElement;
                if (root.TryGetProperty("Files", out var filesEl) &&
                    filesEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var f in filesEl.EnumerateArray())
                    {
                        var path = f.TryGetProperty("Path", out var p) ? p.GetString() : null;
                        var copied = f.TryGetProperty("Copied", out var c) &&
                                     c.ValueKind == System.Text.Json.JsonValueKind.True;
                        var header = f.TryGetProperty("Header", out var h) ? h.GetString() : "";
                        if (path != null && copied && header == "4d5a")
                            rescued.Add(path);
                    }
                }
            }

            Log($"Rescue complete: {rescued.Count}/{relativePaths.Count} succeeded (exit {exitCode})");
        }
        catch (Exception ex)
        {
            Log($"Rescue failed: {ex.Message}");
        }
        return rescued;
    }

    /// <summary>
    /// Builds an overlay manifest from an already-staged folder (output of
    /// <see cref="StageToFolderAsync"/>). Unlike <see cref="PrepareForNetworkAsync"/>
    /// which reads from the raw install, this enumerates ALL files in the staged
    /// folder (Content\, .xvi, transfer-summary.json) so the receiver can
    /// reconstruct a complete staged copy over the network.
    /// </summary>
    public Task<XboxOverlayManifest> PrepareForNetworkFromStagedAsync(
        string stagedFolderPath, CancellationToken ct = default)
    {
        var summaryPath = Path.Combine(stagedFolderPath, "transfer-summary.json");
        if (!File.Exists(summaryPath))
            throw new InvalidOperationException(
                $"transfer-summary.json not found in {stagedFolderPath} — not a valid staged copy");

        var xviFiles = Directory.GetFiles(stagedFolderPath, "*.xvi");
        if (xviFiles.Length == 0)
            throw new InvalidOperationException(
                $"No .xvi envelope file found in {stagedFolderPath} — not a valid staged copy");

        string contentGuid = Path.GetFileNameWithoutExtension(xviFiles[0]);
        string pfn = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(summaryPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("PackageFamilyName", out var pfnEl) &&
                pfnEl.ValueKind == JsonValueKind.String)
            {
                pfn = pfnEl.GetString() ?? string.Empty;
            }
            if (root.TryGetProperty("GameName", out var gnEl) &&
                gnEl.ValueKind == JsonValueKind.String)
            {
                _state.GameName = gnEl.GetString() ?? _state.GameName;
            }
        }
        catch { /* best effort */ }

        var entries = new List<XboxOverlayManifestEntry>();
        long totalBytes = 0;
        int totalFiles = 0;

        foreach (var file in new DirectoryInfo(stagedFolderPath)
                     .EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(new XboxOverlayManifestEntry
            {
                RelativePath = Path.GetRelativePath(stagedFolderPath, file.FullName),
                Size = file.Length,
                LastModifiedUtc = file.LastWriteTimeUtc,
            });
            totalBytes += file.Length;
            totalFiles++;
        }

        var manifest = new XboxOverlayManifest
        {
            ContentGuid = contentGuid,
            PackageFamilyName = pfn,
            SourcePath = stagedFolderPath,
            TotalFiles = totalFiles,
            TotalBytes = totalBytes,
            Entries = entries,
        };

        _state.IsNetwork = true;
        _state.ContentGuid = contentGuid;
        _state.PackageFamilyName = pfn;
        _state.SourceFileCount = totalFiles;
        _state.SourceBytes = totalBytes;
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

    /// <summary>
    /// Fallback PFN discovery via PowerShell Get-AppxPackage. Finds the
    /// installed AppX package whose InstallLocation matches the game folder.
    /// Slower than ACL extraction (~1-2s) but works even if ACLs were reset.
    /// </summary>
    private string? ExtractPfnViaAppxPackage(string dir)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            // Search all packages for one whose InstallLocation is under the
            // same XboxGames root as our game folder.
            var gameName = Path.GetFileName(dir);
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(
                "Get-AppxPackage -AllUsers -PackageTypeFilter Main 2>$null | " +
                $"Where-Object {{ $_.Name -like '*{gameName.Replace("'", "''")}*' }} | " +
                "Select-Object -First 1 -ExpandProperty PackageFamilyName");

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10_000);

            if (!string.IsNullOrEmpty(output) && output.Contains('_'))
            {
                Log($"PFN resolved via Get-AppxPackage: {output}");
                return output;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Returns true if another process currently holds the file open for writing.
    /// Used to detect in-progress copies whose full size is pre-allocated on disk.
    /// </summary>
    private static bool IsFileBeingWritten(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true; // Sharing violation → another process is writing
        }
        catch
        {
            return false; // Permission error etc. → not a write lock
        }
    }

    private void Log(string message) => LogMessage?.Invoke(this, $"[Xbox Sender] {message}");

    private void RaiseStateChanged() => StateChanged?.Invoke(this, _state);
}
