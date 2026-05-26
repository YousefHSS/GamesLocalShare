using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

/// <summary>
/// Connects to a remote XboxNetworkSender and streams the overlay files
/// into the local destination folder.
/// </summary>
public class XboxNetworkReceiver
{
    private CancellationTokenSource? _cts;
    private DateTime _transferStartTime;
    private long _lastSpeedBytes;
    private DateTime _lastSpeedTime;
    private volatile bool _isPaused;
    private readonly SemaphoreSlim _pauseGate = new(1, 1);

    // Bounded tail of recent log lines, captured for log-push diagnostics.
    private const int LogTailCapacity = 500;
    private readonly ConcurrentQueue<string> _recentLogs = new();

    public long BytesReceived { get; private set; }
    public long TotalBytes { get; private set; }

    /// <summary>
    /// Current transfer speed in bytes per second.
    /// </summary>
    public long SpeedBytesPerSecond { get; private set; }

    public bool IsPaused => _isPaused;

    /// <summary>
    /// The manifest received from the sender. Available after ReceiveAsync completes.
    /// </summary>
    public XboxOverlayManifest? ReceivedManifest { get; private set; }

    public event EventHandler<string>? LogMessage;
    public event EventHandler<double>? ProgressChanged;
    public event EventHandler<long>? BytesReceivedChanged;
    public event EventHandler<long>? SpeedChanged;

    public void Cancel()
    {
        _cts?.Cancel();
    }

    public void Pause()
    {
        if (!_isPaused)
        {
            _isPaused = true;
            _pauseGate.Wait();  // Acquire — the download loop will block on this
            Log("Transfer paused");
        }
    }

    public void Resume()
    {
        if (_isPaused)
        {
            _isPaused = false;
            _pauseGate.Release();
            // Reset speed counters so we don't show stale speed from before pause
            _lastSpeedBytes = BytesReceived;
            _lastSpeedTime = DateTime.UtcNow;
            Log("Transfer resumed");
        }
    }

    /// <summary>
    /// Connects to the sender, downloads the manifest, then requests every file
    /// and writes it to the destination path.
    /// </summary>
    public async Task ReceiveAsync(string host, int port, string destinationPath, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        BytesReceived = 0;
        ReceivedManifest = null;

        using var client = new TcpClient();
        await client.ConnectAsync(host, port, token);

        // Tune TCP for LAN throughput
        client.NoDelay = true;
        client.ReceiveBufferSize = 1024 * 1024;   // 1 MB socket buffer
        client.SendBufferSize = 256 * 1024;        // 256 KB for commands

        Log($"Connected to {host}:{port}");

        _transferStartTime = DateTime.UtcNow;
        _lastSpeedTime = _transferStartTime;
        _lastSpeedBytes = 0;

        var stream = client.GetStream();
        var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true, NewLine = "\n" };

        // Request manifest (uses its own StreamReader — safe because no binary
        // data follows the manifest until WE send the first GET command)
        await writer.WriteLineAsync("MANIFEST");
        var manifest = await ReadManifestAsync(stream, token);
        if (manifest == null)
            throw new InvalidOperationException("Failed to receive manifest from sender");

        ReceivedManifest = manifest;
        Directory.CreateDirectory(destinationPath);

        // Total bytes across all manifest entries
        long streamableBytes = manifest.Entries.Sum(e => e.Size);
        TotalBytes = streamableBytes;

        // Check which files already exist from a previous partial download
        // so we can skip them and only request what's missing.
        long preExistingBytes = 0;
        var entriesToDownload = new List<XboxOverlayManifestEntry>();
        foreach (var entry in manifest.Entries)
        {
            var destFile = Path.Combine(destinationPath, entry.RelativePath);
            if (File.Exists(destFile) && new FileInfo(destFile).Length == entry.Size)
            {
                preExistingBytes += entry.Size;
            }
            else
            {
                entriesToDownload.Add(entry);
            }
        }

        // Count pre-existing bytes as already received for progress tracking
        BytesReceived = preExistingBytes;

        if (preExistingBytes > 0)
        {
            Log($"Manifest: {manifest.Entries.Count} files ({streamableBytes / 1024 / 1024} MB total), " +
                $"{manifest.Entries.Count - entriesToDownload.Count} already cached ({preExistingBytes / 1024 / 1024} MB), " +
                $"{entriesToDownload.Count} to download, " +
                $"{manifest.SkippedProtectedFiles.Count} skipped exe(s)");
        }
        else
        {
            Log($"Manifest: {manifest.Entries.Count} files to stream ({streamableBytes / 1024 / 1024} MB), " +
                $"{manifest.SkippedProtectedFiles.Count} skipped exe(s)");
        }

        // Request each file that still needs downloading
        foreach (var entry in entriesToDownload)
        {
            token.ThrowIfCancellationRequested();

            // Honor pause
            if (_isPaused)
            {
                await _pauseGate.WaitAsync(token);
                _pauseGate.Release();
            }

            var destFile = Path.Combine(destinationPath, entry.RelativePath);
            var destDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            await writer.WriteLineAsync($"GET {entry.RelativePath}");

            // Read the header line directly from the raw stream — do NOT use
            // StreamReader here because its internal buffer would consume
            // binary file data that follows, causing stream.ReadAsync to deadlock.
            var header = await ReadLineRawAsync(stream, token);
            if (header == null || !header.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
            {
                Log($"Unexpected response for {entry.RelativePath}: {header}");
                continue;
            }

            if (!long.TryParse(header.Substring(5).Trim(), out var fileSize))
            {
                Log($"Invalid file size for {entry.RelativePath}: {header}");
                continue;
            }

            await using var fs = File.Create(destFile);
            var remaining = fileSize;
            long bytesSinceLastProgress = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(1048576); // 1MB buffer
            try
            {
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), token);
                    if (read == 0)
                        throw new IOException("Connection closed unexpectedly while receiving file");

                    await fs.WriteAsync(buffer.AsMemory(0, read), token);
                    remaining -= read;
                    BytesReceived += read;
                    bytesSinceLastProgress += read;

                    // Report progress every ~4 MB so large files don't
                    // cause the progress bar to appear stuck.
                    if (bytesSinceLastProgress >= 4 * 1024 * 1024)
                    {
                        bytesSinceLastProgress = 0;
                        UpdateSpeed();
                        BytesReceivedChanged?.Invoke(this, BytesReceived);
                        var midProgress = streamableBytes > 0
                            ? (double)BytesReceived / streamableBytes * 100 : 0;
                        ProgressChanged?.Invoke(this, Math.Min(midProgress, 100));
                        SpeedChanged?.Invoke(this, SpeedBytesPerSecond);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // Final event for this file (ensures 100% on last file)
            UpdateSpeed();
            BytesReceivedChanged?.Invoke(this, BytesReceived);
            var progress = streamableBytes > 0 ? (double)BytesReceived / streamableBytes * 100 : 0;
            ProgressChanged?.Invoke(this, Math.Min(progress, 100));
            SpeedChanged?.Invoke(this, SpeedBytesPerSecond);
        }

        await writer.WriteLineAsync("QUIT");
        Log($"Download complete: {BytesReceived} bytes received");
    }

    private void UpdateSpeed()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSpeedTime).TotalSeconds;
        if (elapsed >= 0.5)
        {
            var deltaBytes = BytesReceived - _lastSpeedBytes;
            SpeedBytesPerSecond = (long)(deltaBytes / elapsed);
            _lastSpeedBytes = BytesReceived;
            _lastSpeedTime = now;
        }
    }

    /// <summary>
    /// Reads a single UTF-8 line from the raw network stream without buffering
    /// ahead, so subsequent binary reads from the same stream are not affected.
    /// </summary>
    private static async Task<string?> ReadLineRawAsync(NetworkStream stream, CancellationToken ct)
    {
        var sb = new StringBuilder(128);
        var buf = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buf.AsMemory(), ct);
            if (read == 0) return sb.Length > 0 ? sb.ToString() : null;
            var ch = (char)buf[0];
            if (ch == '\n') return sb.ToString();
            if (ch != '\r') sb.Append(ch);
        }
    }

    private static async Task<XboxOverlayManifest?> ReadManifestAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = await ReadLineRawAsync(stream, ct);
        if (header == null || !header.StartsWith("MANIFEST ", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!int.TryParse(header.Substring(9).Trim(), out var jsonLength))
            return null;

        // The sender writes the JSON as line(s) via SendLineAsync.
        // Read lines until we have enough characters (matches the sender's
        // json.Length which is a character count, not byte count).
        var jsonBuilder = new StringBuilder(jsonLength);
        while (jsonBuilder.Length < jsonLength)
        {
            var line = await ReadLineRawAsync(stream, ct);
            if (line == null) break;
            jsonBuilder.AppendLine(line);
        }

        var json = jsonBuilder.ToString();
        return JsonSerializer.Deserialize<XboxOverlayManifest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private void Log(string message)
    {
        var line = $"[XboxNetworkReceiver] {message}";
        // Keep a bounded tail for log-push diagnostics.
        _recentLogs.Enqueue($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {line}");
        while (_recentLogs.Count > LogTailCapacity && _recentLogs.TryDequeue(out _)) { }
        LogMessage?.Invoke(this, line);
    }

    /// <summary>
    /// Bundles the recent receiver-side diagnostic artifacts into a ZIP and
    /// pushes it back to the sender over a fresh TCP connection using the
    /// new PUSH_LOGS command. Fire-and-forget: any error is logged locally
    /// and swallowed — log push must never disturb the calling flow.
    /// </summary>
    public async Task PushLogsAsync(
        string host,
        int port,
        string attemptStatus,
        string? extraTempFolder,
        Exception? lastError = null,
        CancellationToken ct = default)
    {
        try
        {
            using var pushTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pushTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var token = pushTimeout.Token;

            byte[] zipBytes;
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    // 1. transfer-summary.json from the receiver's temp folder
                    if (!string.IsNullOrEmpty(extraTempFolder))
                    {
                        var summary = Path.Combine(extraTempFolder, "transfer-summary.json");
                        TryAddFile(zip, summary, "transfer-summary.json");
                    }

                    // 2. Recent receiver-* files from the runs directory
                    var runsDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "GamesLocalShare", "xbox-transfer", "runs");
                    if (Directory.Exists(runsDir))
                    {
                        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(30);
                        foreach (var f in Directory.EnumerateFiles(runsDir, "receiver-*", SearchOption.TopDirectoryOnly))
                        {
                            try
                            {
                                var fi = new FileInfo(f);
                                if (fi.LastWriteTimeUtc < cutoff) continue;
                                TryAddFile(zip, fi.FullName, "runs/" + fi.Name);
                            }
                            catch { }
                        }
                    }

                    // 3. In-memory tail of receiver log messages
                    var tail = string.Join("\n", _recentLogs.ToArray());
                    AddTextEntry(zip, "client-log.txt", tail);

                    // 4. Meta
                    var meta = new
                    {
                        ReceiverHost = Environment.MachineName,
                        ReceiverUser = Environment.UserName,
                        UtcTime = DateTime.UtcNow.ToString("o"),
                        PeerHost = host,
                        PeerPort = port,
                        AttemptStatus = attemptStatus,
                        BytesReceived,
                        TotalBytes,
                        LastError = lastError?.ToString() ?? "",
                    };
                    AddTextEntry(zip, "meta.json",
                        JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
                }
                zipBytes = ms.ToArray();
            }

            using var client = new TcpClient();
            await client.ConnectAsync(host, port, token);
            client.NoDelay = true;

            var stream = client.GetStream();
            var receiverHost = SanitizeToken(Environment.MachineName);
            var status = SanitizeToken(attemptStatus);
            var header = $"PUSH_LOGS {zipBytes.Length} {receiverHost} {status}\n";
            var headerBytes = Encoding.UTF8.GetBytes(header);
            await stream.WriteAsync(headerBytes, token);
            await stream.FlushAsync(token);

            // Wait for READY before sending the binary body, so the sender's
            // StreamReader has no chance to buffer ahead into our bytes.
            var ready = await ReadLineRawAsync(stream, token);
            if (ready == null || !ready.Trim().Equals("READY", StringComparison.OrdinalIgnoreCase))
            {
                Log($"PushLogs: did not get READY (got '{ready}')");
                return;
            }

            await stream.WriteAsync(zipBytes, token);
            await stream.FlushAsync(token);
            Log($"Pushed {zipBytes.Length} bytes of logs to {host}:{port} (status={status})");
        }
        catch (Exception ex)
        {
            // Never propagate — log push is best-effort.
            Log($"PushLogsAsync failed: {ex.Message}");
        }
    }

    private static void TryAddFile(ZipArchive zip, string path, string entryName)
    {
        try
        {
            if (!File.Exists(path)) return;
            // Read with shared read so we can grab files the script may still hold open.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var es = entry.Open();
            fs.CopyTo(es);
        }
        catch { /* best-effort */ }
    }

    private static void AddTextEntry(ZipArchive zip, string entryName, string content)
    {
        try
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var es = entry.Open();
            using var sw = new StreamWriter(es, Encoding.UTF8);
            sw.Write(content);
        }
        catch { }
    }

    private static string SanitizeToken(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        var sb = new StringBuilder(Math.Min(s.Length, 64));
        foreach (var c in s.Take(64))
        {
            if (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }
}
