using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

/// <summary>
/// Service for transferring game files between peers
/// </summary>
public class FileTransferService : IDisposable
{
    private const int TransferPort = 45679;
    private const int BufferSize = 1024 * 1024; // 1MB buffer for fast transfers

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Event raised when transfer progress updates
    /// </summary>
    public event EventHandler<TransferProgressEventArgs>? ProgressChanged;

    /// <summary>
    /// Event raised when a transfer completes
    /// </summary>
    public event EventHandler<TransferCompletedEventArgs>? TransferCompleted;

    /// <summary>
    /// Starts listening for incoming file transfer requests
    /// </summary>
    public async Task StartListeningAsync()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, TransferPort);
        _listener.Start();

        _ = AcceptConnectionsAsync(_cts.Token);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops listening for file transfers
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
    }

    /// <summary>
    /// Requests a game transfer from a remote peer
    /// </summary>
    public async Task<bool> RequestGameTransferAsync(NetworkPeer peer, GameInfo remoteGame, GameInfo localGame, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(peer.IpAddress, TransferPort, ct);

            using var stream = client.GetStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            // Send transfer request
            var request = new FileTransferRequest
            {
                GameAppId = remoteGame.AppId,
                GamePath = remoteGame.InstallPath
            };
            var requestJson = JsonSerializer.Serialize(request);
            writer.Write(requestJson);
            writer.Flush();

            // Read file manifest
            var manifestJson = reader.ReadString();
            var manifest = JsonSerializer.Deserialize<FileManifest>(manifestJson);

            if (manifest == null || manifest.Files.Count == 0)
            {
                return false;
            }

            // Calculate what we need to download (differential sync)
            var filesToDownload = await GetFilesToDownloadAsync(localGame.InstallPath, manifest.Files);
            
            long totalBytes = filesToDownload.Sum(f => f.Size);
            long transferredBytes = 0;
            var startTime = DateTime.Now;

            // Request each file
            foreach (var fileInfo in filesToDownload)
            {
                if (ct.IsCancellationRequested)
                    break;

                // Send file request
                writer.Write(fileInfo.RelativePath);
                writer.Flush();

                // Read file size
                var fileSize = reader.ReadInt64();
                if (fileSize < 0)
                    continue; // File not available

                // Prepare local file path
                var localFilePath = Path.Combine(localGame.InstallPath, fileInfo.RelativePath);
                var localDir = Path.GetDirectoryName(localFilePath);
                if (!string.IsNullOrEmpty(localDir) && !Directory.Exists(localDir))
                {
                    Directory.CreateDirectory(localDir);
                }

                // Download file
                using var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
                var buffer = new byte[BufferSize];
                long remaining = fileSize;

                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(remaining, buffer.Length);
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                    
                    if (bytesRead == 0)
                        break;

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    remaining -= bytesRead;
                    transferredBytes += bytesRead;

                    // Calculate speed and report progress
                    var elapsed = (DateTime.Now - startTime).TotalSeconds;
                    var speed = elapsed > 0 ? (long)(transferredBytes / elapsed) : 0;
                    var progress = totalBytes > 0 ? (double)transferredBytes / totalBytes * 100 : 0;

                    ProgressChanged?.Invoke(this, new TransferProgressEventArgs
                    {
                        GameAppId = remoteGame.AppId,
                        Progress = progress,
                        TransferredBytes = transferredBytes,
                        TotalBytes = totalBytes,
                        SpeedBytesPerSecond = speed,
                        CurrentFile = fileInfo.RelativePath
                    });
                }
            }

            // Signal end of transfer
            writer.Write(string.Empty);

            TransferCompleted?.Invoke(this, new TransferCompletedEventArgs
            {
                GameAppId = remoteGame.AppId,
                Success = true,
                TotalBytesTransferred = transferredBytes
            });

            return true;
        }
        catch (Exception ex)
        {
            TransferCompleted?.Invoke(this, new TransferCompletedEventArgs
            {
                GameAppId = remoteGame.AppId,
                Success = false,
                ErrorMessage = ex.Message
            });
            return false;
        }
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleTransferRequestAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Accept connection error: {ex.Message}");
            }
        }
    }

    private async Task HandleTransferRequestAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

                // Read transfer request
                var requestJson = reader.ReadString();
                var request = JsonSerializer.Deserialize<FileTransferRequest>(requestJson);

                if (request == null || !Directory.Exists(request.GamePath))
                {
                    writer.Write("{}"); // Empty manifest
                    return;
                }

                // Build and send file manifest
                var manifest = await BuildFileManifestAsync(request.GamePath);
                var manifestJson = JsonSerializer.Serialize(manifest);
                writer.Write(manifestJson);
                writer.Flush();

                // Handle file requests
                while (!ct.IsCancellationRequested)
                {
                    var relativePath = reader.ReadString();
                    
                    if (string.IsNullOrEmpty(relativePath))
                        break; // End of transfer

                    var fullPath = Path.Combine(request.GamePath, relativePath);
                    
                    if (!File.Exists(fullPath))
                    {
                        writer.Write(-1L); // File not available
                        continue;
                    }

                    var fileInfo = new FileInfo(fullPath);
                    writer.Write(fileInfo.Length);
                    writer.Flush();

                    // Stream file content
                    using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
                    var buffer = new byte[BufferSize];
                    int bytesRead;

                    while ((bytesRead = await fileStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await stream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    }

                    await stream.FlushAsync(ct);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Transfer request error: {ex.Message}");
        }
    }

    private async Task<FileManifest> BuildFileManifestAsync(string gamePath)
    {
        var manifest = new FileManifest { GamePath = gamePath };

        await Task.Run(() =>
        {
            var dirInfo = new DirectoryInfo(gamePath);
            foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    var relativePath = Path.GetRelativePath(gamePath, file.FullName);
                    manifest.Files.Add(new FileTransferInfo
                    {
                        RelativePath = relativePath,
                        Size = file.Length,
                        LastModified = file.LastWriteTimeUtc,
                        Hash = ComputeQuickHash(file.FullName, file.Length)
                    });
                }
                catch { }
            }
        });

        return manifest;
    }

    private async Task<List<FileTransferInfo>> GetFilesToDownloadAsync(string localPath, List<FileTransferInfo> remoteFiles)
    {
        var filesToDownload = new List<FileTransferInfo>();

        await Task.Run(() =>
        {
            foreach (var remoteFile in remoteFiles)
            {
                var localFilePath = Path.Combine(localPath, remoteFile.RelativePath);
                
                if (!File.Exists(localFilePath))
                {
                    filesToDownload.Add(remoteFile);
                    continue;
                }

                var localInfo = new FileInfo(localFilePath);
                
                // Check if file is different (by size or hash)
                if (localInfo.Length != remoteFile.Size)
                {
                    filesToDownload.Add(remoteFile);
                    continue;
                }

                // Quick hash comparison
                var localHash = ComputeQuickHash(localFilePath, localInfo.Length);
                if (localHash != remoteFile.Hash)
                {
                    filesToDownload.Add(remoteFile);
                }
            }
        });

        return filesToDownload;
    }

    /// <summary>
    /// Computes a quick hash for file comparison (first/last 1MB + size)
    /// </summary>
    private static string ComputeQuickHash(string filePath, long fileSize)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var md5 = MD5.Create();

            // For small files, hash entire content
            if (fileSize <= 2 * 1024 * 1024)
            {
                var hash = md5.ComputeHash(stream);
                return Convert.ToHexString(hash);
            }

            // For large files, hash first 1MB + last 1MB + size
            var buffer = new byte[1024 * 1024];
            
            // First MB
            stream.Read(buffer, 0, buffer.Length);
            md5.TransformBlock(buffer, 0, buffer.Length, buffer, 0);

            // Last MB
            stream.Seek(-buffer.Length, SeekOrigin.End);
            stream.Read(buffer, 0, buffer.Length);
            md5.TransformBlock(buffer, 0, buffer.Length, buffer, 0);

            // File size
            var sizeBytes = BitConverter.GetBytes(fileSize);
            md5.TransformFinalBlock(sizeBytes, 0, sizeBytes.Length);

            return Convert.ToHexString(md5.Hash!);
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}

public class FileTransferRequest
{
    public string GameAppId { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
}

public class FileManifest
{
    public string GamePath { get; set; } = string.Empty;
    public List<FileTransferInfo> Files { get; set; } = [];
}

public class FileTransferInfo
{
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string Hash { get; set; } = string.Empty;
}

public class TransferProgressEventArgs : EventArgs
{
    public string GameAppId { get; set; } = string.Empty;
    public double Progress { get; set; }
    public long TransferredBytes { get; set; }
    public long TotalBytes { get; set; }
    public long SpeedBytesPerSecond { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
}

public class TransferCompletedEventArgs : EventArgs
{
    public string GameAppId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public long TotalBytesTransferred { get; set; }
    public string? ErrorMessage { get; set; }
}
