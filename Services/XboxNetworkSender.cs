using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

/// <summary>
/// TCP server that streams Xbox MSIXVC files to a receiver peer.
/// Listens on a dedicated port (default 45680), isolated from FileTransferService.
/// </summary>
public class XboxNetworkSender : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private readonly object _manifestLock = new();
    private XboxOverlayManifest? _manifest;
    private Dictionary<string, string>? _pathOverrides;

    public int Port { get; set; } = 45680;

    private long _bytesStreamed;

    /// <summary>
    /// Total bytes streamed since the last Start().
    /// </summary>
    public long BytesStreamed => _bytesStreamed;

    public event EventHandler<string>? LogMessage;
    public event EventHandler<long>? BytesStreamedChanged;

    /// <summary>
    /// Raised when a receiver peer has pushed a log bundle. Payload is the
    /// absolute path to the extracted folder, suffixed with " (attempt: STATUS)".
    /// </summary>
    public event EventHandler<string>? LogsReceived;

    public void SetManifest(XboxOverlayManifest manifest)
    {
        lock (_manifestLock)
        {
            _manifest = manifest;
        }
    }

    /// <summary>
    /// Sets path overrides for files that should be read from a different
    /// location than manifest.SourcePath (e.g. rescued protected executables
    /// stored in a temp directory).
    /// Key = relative path, Value = actual full path on disk.
    /// </summary>
    public void SetPathOverrides(Dictionary<string, string> overrides)
    {
        _pathOverrides = overrides;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _bytesStreamed = 0;

        try
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            if (Port == 0)
            {
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            }
            _listenTask = Task.Run(() => ListenAsync(_cts.Token));
            Log($"XboxNetworkSender started on port {Port}");
        }
        catch (Exception ex)
        {
            Log($"Failed to start XboxNetworkSender on port {Port}: {ex.Message}");
            throw;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_listenTask != null)
        {
            try { _listenTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
        _listener = null;
        _listenTask = null;
        Log("XboxNetworkSender stopped");
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_listener == null) break;
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            // Tune TCP for LAN throughput
            client.NoDelay = true;
            client.SendBufferSize = 1024 * 1024;   // 1 MB socket buffer
            client.ReceiveBufferSize = 256 * 1024;  // 256 KB for commands

            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;

                    if (line.Trim().Equals("MANIFEST", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendManifestAsync(stream, ct);
                    }
                    else if (line.StartsWith("GET ", StringComparison.OrdinalIgnoreCase))
                    {
                        var relPath = line.Substring(4).Trim();
                        await SendFileAsync(stream, relPath, ct);
                    }
                    else if (line.StartsWith("PUSH_LOGS ", StringComparison.OrdinalIgnoreCase))
                    {
                        // After PUSH_LOGS the stream contains raw bytes — we
                        // cannot use the StreamReader (which has already
                        // buffered ahead) for further reads on this connection.
                        // Process and then break out so the connection closes.
                        await ReceivePushedLogsAsync(stream, reader, line, ct);
                        break;
                    }
                    else if (line.Trim().Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"Client handler error: {ex.Message}");
            }
        }
    }

    private async Task SendManifestAsync(NetworkStream stream, CancellationToken ct)
    {
        XboxOverlayManifest? manifest;
        lock (_manifestLock)
        {
            manifest = _manifest;
        }

        if (manifest == null)
        {
            await SendLineAsync(stream, "ERROR No manifest set", ct);
            return;
        }

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = false });
        await SendLineAsync(stream, $"MANIFEST {json.Length}", ct);
        await SendLineAsync(stream, json, ct);
    }

    private async Task SendFileAsync(NetworkStream stream, string relativePath, CancellationToken ct)
    {
        XboxOverlayManifest? manifest;
        lock (_manifestLock)
        {
            manifest = _manifest;
        }

        if (manifest == null)
        {
            await SendLineAsync(stream, "ERROR No manifest set", ct);
            return;
        }

        var entry = manifest.Entries.FirstOrDefault(e =>
            e.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            await SendLineAsync(stream, "ERROR File not found in manifest", ct);
            return;
        }

        // Check for path override (e.g. rescued protected exe in temp dir)
        var fullPath = _pathOverrides != null &&
                       _pathOverrides.TryGetValue(relativePath, out var overridePath)
            ? overridePath
            : Path.Combine(manifest.SourcePath, relativePath);
        if (!File.Exists(fullPath))
        {
            await SendLineAsync(stream, "ERROR File not found on disk", ct);
            return;
        }

        var fileInfo = new FileInfo(fullPath);
        await SendLineAsync(stream, $"FILE {fileInfo.Length}", ct);

        await using var fs = File.OpenRead(fullPath);
        var buffer = ArrayPool<byte>.Shared.Rent(1048576); // 1MB buffer for better throughput
        try
        {
            int read;
            while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                Interlocked.Add(ref _bytesStreamed, read);
            }
            await stream.FlushAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        BytesStreamedChanged?.Invoke(this, BytesStreamed);
        Log($"Sent {relativePath} ({fileInfo.Length} bytes)");
    }

    private static async Task SendLineAsync(NetworkStream stream, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes.AsMemory(), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// Handles a PUSH_LOGS command. Protocol:
    ///   client: PUSH_LOGS &lt;len&gt; &lt;host&gt; &lt;status&gt;\n
    ///   server: READY\n
    ///   client: &lt;len&gt; bytes of ZIP
    /// The ACK ensures the StreamReader on this side has not buffered the
    /// binary payload — the client only sends bytes after seeing READY.
    /// </summary>
    private async Task ReceivePushedLogsAsync(
        NetworkStream stream, StreamReader reader, string headerLine, CancellationToken ct)
    {
        try
        {
            // PUSH_LOGS <len> <host> <status>
            var parts = headerLine.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !int.TryParse(parts[1], out var len) || len <= 0 || len > 256 * 1024 * 1024)
            {
                Log($"PUSH_LOGS: malformed header: {headerLine}");
                return;
            }
            var receiverHost = SanitizeToken(parts[2]);
            var status = SanitizeToken(parts[3]);

            // ACK so the client can start streaming the binary body.
            await SendLineAsync(stream, "READY", ct);

            // From here on, the StreamReader has no buffered data ahead (the
            // client waited for READY before sending more). Read raw bytes
            // off the underlying NetworkStream directly.
            var buf = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            byte[] payload;
            try
            {
                payload = new byte[len];
                var read = 0;
                while (read < len)
                {
                    var toRead = Math.Min(buf.Length, len - read);
                    var n = await stream.ReadAsync(buf.AsMemory(0, toRead), ct);
                    if (n == 0)
                    {
                        Log($"PUSH_LOGS: connection closed at {read}/{len} bytes");
                        return;
                    }
                    Buffer.BlockCopy(buf, 0, payload, read, n);
                    read += n;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }

            // Write zip + extract under receiver-logs root.
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GamesLocalShare", "xbox-transfer", "receiver-logs");
            Directory.CreateDirectory(root);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var folder = Path.Combine(root, $"{stamp}-{receiverHost}");
            Directory.CreateDirectory(folder);

            var zipPath = Path.Combine(folder, "receiver-logs.zip");
            await File.WriteAllBytesAsync(zipPath, payload, ct);

            try
            {
                using var ms = new MemoryStream(payload, writable: false);
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // skip directories
                    var safeName = entry.FullName.Replace('\\', '/');
                    var dest = Path.GetFullPath(Path.Combine(folder, safeName));
                    if (!dest.StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
                        continue; // zip-slip guard
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using var entryStream = entry.Open();
                    using var outFs = File.Create(dest);
                    await entryStream.CopyToAsync(outFs, ct);
                }
            }
            catch (Exception ex)
            {
                Log($"PUSH_LOGS: zip extract failed: {ex.Message} (raw zip kept at {zipPath})");
            }

            Log($"PUSH_LOGS: stored {payload.Length} bytes under {folder}");
            LogsReceived?.Invoke(this, $"{folder} (attempt: {status})");
        }
        catch (Exception ex)
        {
            Log($"PUSH_LOGS error: {ex.Message}");
        }
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

    private void Log(string message)
    {
        LogMessage?.Invoke(this, $"[XboxNetworkSender] {message}");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
