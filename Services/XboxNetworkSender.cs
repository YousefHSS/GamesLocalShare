using System.Buffers;
using System.Diagnostics;
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
