using System.Buffers;
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

    public long BytesReceived { get; private set; }

    public event EventHandler<string>? LogMessage;
    public event EventHandler<double>? ProgressChanged;
    public event EventHandler<long>? BytesReceivedChanged;

    public void Cancel()
    {
        _cts?.Cancel();
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

        using var client = new TcpClient();
        await client.ConnectAsync(host, port, token);
        Log($"Connected to {host}:{port}");

        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true, NewLine = "\n" };

        // Request manifest
        await writer.WriteLineAsync("MANIFEST");
        var manifest = await ReadManifestAsync(reader, token);
        if (manifest == null)
            throw new InvalidOperationException("Failed to receive manifest from sender");

        Directory.CreateDirectory(destinationPath);
        long totalBytes = manifest.TotalBytes;

        // Request each file
        foreach (var entry in manifest.Entries)
        {
            token.ThrowIfCancellationRequested();

            var destFile = Path.Combine(destinationPath, entry.RelativePath);
            var destDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            await writer.WriteLineAsync($"GET {entry.RelativePath}");

            var header = await reader.ReadLineAsync(token);
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
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
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
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            BytesReceivedChanged?.Invoke(this, BytesReceived);
            var progress = totalBytes > 0 ? (double)BytesReceived / totalBytes * 100 : 0;
            ProgressChanged?.Invoke(this, Math.Min(progress, 100));
            Log($"Received {entry.RelativePath} ({fileSize} bytes)");
        }

        await writer.WriteLineAsync("QUIT");
        Log($"Overlay complete: {BytesReceived} bytes received");
    }

    private static async Task<XboxOverlayManifest?> ReadManifestAsync(StreamReader reader, CancellationToken ct)
    {
        var header = await reader.ReadLineAsync(ct);
        if (header == null || !header.StartsWith("MANIFEST ", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!int.TryParse(header.Substring(9).Trim(), out var jsonLength))
            return null;

        var jsonBuilder = new StringBuilder(jsonLength);
        while (jsonBuilder.Length < jsonLength)
        {
            var line = await reader.ReadLineAsync(ct);
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
        LogMessage?.Invoke(this, $"[XboxNetworkReceiver] {message}");
    }
}
