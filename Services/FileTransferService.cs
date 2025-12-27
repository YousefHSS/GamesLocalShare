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
    private const int ConnectionTimeoutMs = 10000; // 10 second connection timeout

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private TransferState? _currentTransferState;
    private bool _isListening;

    /// <summary>
    /// Whether the file transfer service is actively listening
    /// </summary>
    public bool IsListening => _isListening;

    /// <summary>
    /// The port the service is listening on
    /// </summary>
    public int ListeningPort => TransferPort;

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
        if (_isListening)
        {
            System.Diagnostics.Debug.WriteLine("FileTransferService already listening");
            return;
        }

        _cts = new CancellationTokenSource();
        
        try
        {
            _listener = new TcpListener(IPAddress.Any, TransferPort);
            _listener.Start();
            _isListening = true;
            
            System.Diagnostics.Debug.WriteLine($"FileTransferService SUCCESSFULLY listening on port {TransferPort}");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            _isListening = false;
            var errorMsg = $"Port {TransferPort} is already in use! Another instance of the app may be running, or another program is using this port.";
            System.Diagnostics.Debug.WriteLine($"FileTransferService FAILED: {errorMsg}");
            throw new InvalidOperationException(errorMsg, ex);
        }
        catch (Exception ex)
        {
            _isListening = false;
            System.Diagnostics.Debug.WriteLine($"FileTransferService FAILED to start: {ex.Message}");
            throw;
        }

        _ = AcceptConnectionsAsync(_cts.Token);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops listening for file transfers
    /// </summary>
    public void Stop()
    {
        _isListening = false;
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch { }
        _listener = null;
    }

    /// <summary>
    /// Tests if the file transfer port is available
    /// </summary>
    public static bool IsPortAvailable()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, TransferPort);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Scans for incomplete transfers and returns them
    /// </summary>
    public List<TransferState> FindIncompleteTransfers(IEnumerable<string> libraryPaths)
    {
        var incomplete = new List<TransferState>();

        foreach (var libraryPath in libraryPaths)
        {
            try
            {
                if (!Directory.Exists(libraryPath))
                    continue;

                // Look for transfer state files in game directories
                foreach (var gameDir in Directory.GetDirectories(libraryPath))
                {
                    var state = TransferState.Load(gameDir);
                    if (state != null && state.TransferredBytes < state.TotalBytes)
                    {
                        incomplete.Add(state);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error scanning for incomplete transfers: {ex.Message}");
            }
        }

        return incomplete;
    }

    /// <summary>
    /// Requests a new game download from a remote peer (game not installed locally)
    /// </summary>
    public async Task<bool> RequestNewGameDownloadAsync(
        NetworkPeer peer, 
        GameInfo remoteGame, 
        string targetPath,
        CancellationToken ct = default)
    {
        // Create a temporary local game info for the download
        var localGame = new GameInfo
        {
            AppId = remoteGame.AppId,
            Name = remoteGame.Name,
            InstallPath = targetPath,
            BuildId = "0", // No local version
            IsInstalled = false,
            IsAvailableFromPeer = true
        };

        return await RequestGameTransferAsync(peer, remoteGame, localGame, isNewDownload: true, ct: ct);
    }

    /// <summary>
    /// Resumes an incomplete transfer
    /// </summary>
    public async Task<bool> ResumeTransferAsync(TransferState state, NetworkPeer peer, CancellationToken ct = default)
    {
        var remoteGame = new GameInfo
        {
            AppId = state.GameAppId,
            Name = state.GameName,
            InstallPath = state.TargetPath, // Will use source path from peer
            BuildId = state.BuildId
        };

        var localGame = new GameInfo
        {
            AppId = state.GameAppId,
            Name = state.GameName,
            InstallPath = state.TargetPath,
            BuildId = "0",
            IsInstalled = false
        };

        return await RequestGameTransferAsync(peer, remoteGame, localGame, isNewDownload: state.IsNewDownload, resumeState: state, ct: ct);
    }

    /// <summary>
    /// Requests a game transfer from a remote peer
    /// </summary>
    public async Task<bool> RequestGameTransferAsync(
        NetworkPeer peer, 
        GameInfo remoteGame, 
        GameInfo localGame, 
        bool isNewDownload = false,
        TransferState? resumeState = null,
        CancellationToken ct = default)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"=== FILE TRANSFER REQUEST ===");
            System.Diagnostics.Debug.WriteLine($"Target: {peer.DisplayName} ({peer.IpAddress}:{TransferPort})");
            System.Diagnostics.Debug.WriteLine($"Game: {remoteGame.Name} (AppId: {remoteGame.AppId})");
            System.Diagnostics.Debug.WriteLine($"Remote Path: {remoteGame.InstallPath}");
            
            using var client = new TcpClient();
            
            // Use a timeout for connection
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectionTimeoutMs);
            
            try
            {
                await client.ConnectAsync(peer.IpAddress, TransferPort, connectCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Connection timed out (not user cancellation)
                throw new Exception(
                    $"Connection to {peer.DisplayName} ({peer.IpAddress}) timed out on port {TransferPort}.\n\n" +
                    "Possible causes:\n" +
                    "1. The app on the remote computer hasn't clicked 'Start Network'\n" +
                    "2. Firewall on remote computer is blocking port 45679\n" +
                    "3. Antivirus/security software is blocking the connection\n" +
                    "4. Another instance of the app is running on the remote computer");
            }

            if (!client.Connected)
            {
                throw new Exception($"Could not connect to {peer.DisplayName}. The remote computer may not be listening on port {TransferPort}.");
            }

            System.Diagnostics.Debug.WriteLine($"Connected successfully to {peer.DisplayName} for file transfer");

            using var stream = client.GetStream();
            stream.ReadTimeout = 30000; // 30 second read timeout
            stream.WriteTimeout = 30000; // 30 second write timeout
            
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            // Send transfer request
            var request = new FileTransferRequest
            {
                GameAppId = remoteGame.AppId,
                GamePath = remoteGame.InstallPath,
                IsNewDownload = isNewDownload
            };
            var requestJson = JsonSerializer.Serialize(request);
            System.Diagnostics.Debug.WriteLine($"Sending request: {requestJson}");
            writer.Write(requestJson);
            writer.Flush();

            // Read file manifest
            var manifestJson = reader.ReadString();
            var manifest = JsonSerializer.Deserialize<FileManifest>(manifestJson);

            if (manifest == null || manifest.Files.Count == 0)
            {
                throw new Exception($"Remote peer returned empty file manifest. The game folder may not exist at: {remoteGame.InstallPath}");
            }

            System.Diagnostics.Debug.WriteLine($"Received manifest with {manifest.Files.Count} files");

            // Calculate what we need to download
            List<FileTransferInfo> filesToDownload;
            
            if (resumeState != null)
            {
                // Resume: only download files not already completed
                filesToDownload = manifest.Files
                    .Where(f => !resumeState.CompletedFiles.Contains(f.RelativePath))
                    .ToList();
            }
            else
            {
                // Normal: calculate differential
                filesToDownload = await GetFilesToDownloadAsync(localGame.InstallPath, manifest.Files);
            }

            long totalBytes = filesToDownload.Sum(f => f.Size);
            long alreadyTransferred = resumeState?.TransferredBytes ?? 0;
            long transferredBytes = alreadyTransferred;
            var startTime = DateTime.Now;

            // Create/update transfer state
            _currentTransferState = resumeState ?? new TransferState
            {
                GameAppId = remoteGame.AppId,
                GameName = remoteGame.Name,
                TargetPath = localGame.InstallPath,
                SourcePeerIp = peer.IpAddress,
                SourcePeerName = peer.DisplayName,
                BuildId = remoteGame.BuildId,
                TotalBytes = totalBytes + alreadyTransferred,
                IsNewDownload = isNewDownload
            };

            // Create target directory if needed
            if (!Directory.Exists(localGame.InstallPath))
            {
                Directory.CreateDirectory(localGame.InstallPath);
            }

            _currentTransferState.Save();

            System.Diagnostics.Debug.WriteLine($"Starting download of {filesToDownload.Count} files ({totalBytes / 1024 / 1024}MB)");

            // Request each file
            foreach (var fileInfo in filesToDownload)
            {
                if (ct.IsCancellationRequested)
                {
                    _currentTransferState.Save();
                    break;
                }

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

                // Check if we can resume this specific file
                long startOffset = 0;
                FileMode fileMode = FileMode.Create;
                
                if (File.Exists(localFilePath))
                {
                    var existingInfo = new FileInfo(localFilePath);
                    if (existingInfo.Length < fileSize)
                    {
                        fileMode = FileMode.Create;
                    }
                    else if (existingInfo.Length == fileSize)
                    {
                        // File already complete, skip
                        transferredBytes += fileSize;
                        _currentTransferState.CompletedFiles.Add(fileInfo.RelativePath);
                        _currentTransferState.TransferredBytes = transferredBytes;
                        continue;
                    }
                }

                // Download file
                await using var fileStream = new FileStream(localFilePath, fileMode, FileAccess.Write, FileShare.None, BufferSize);
                var buffer = new byte[BufferSize];
                long remaining = fileSize - startOffset;

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
                    var speed = elapsed > 0 ? (long)((transferredBytes - alreadyTransferred) / elapsed) : 0;
                    var progress = _currentTransferState.TotalBytes > 0 
                        ? (double)transferredBytes / _currentTransferState.TotalBytes * 100 
                        : 0;

                    ProgressChanged?.Invoke(this, new TransferProgressEventArgs
                    {
                        GameAppId = remoteGame.AppId,
                        Progress = progress,
                        TransferredBytes = transferredBytes,
                        TotalBytes = _currentTransferState.TotalBytes,
                        SpeedBytesPerSecond = speed,
                        CurrentFile = fileInfo.RelativePath
                    });

                    // Update state periodically (every 10MB)
                    if (transferredBytes % (10 * 1024 * 1024) < BufferSize)
                    {
                        _currentTransferState.TransferredBytes = transferredBytes;
                        _currentTransferState.Save();
                    }
                }

                // Mark file as complete
                _currentTransferState.CompletedFiles.Add(fileInfo.RelativePath);
                _currentTransferState.TransferredBytes = transferredBytes;
                _currentTransferState.Save();
            }

            // Signal end of transfer
            writer.Write(string.Empty);

            // Check if transfer completed
            bool success = transferredBytes >= _currentTransferState.TotalBytes;
            
            if (success)
            {
                _currentTransferState.Delete();
            }

            TransferCompleted?.Invoke(this, new TransferCompletedEventArgs
            {
                GameAppId = remoteGame.AppId,
                Success = success,
                TotalBytesTransferred = transferredBytes,
                IsNewDownload = isNewDownload
            });

            _currentTransferState = null;
            return success;
        }
        catch (SocketException ex)
        {
            _currentTransferState?.Save();
            _currentTransferState = null;

            var errorMsg = ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => $"Connection REFUSED by {peer.DisplayName}. The app may not be running or hasn't clicked 'Start Network'.",
                SocketError.TimedOut => $"Connection TIMED OUT to {peer.DisplayName}. Port {TransferPort} may be blocked.",
                SocketError.HostUnreachable => $"Cannot reach {peer.DisplayName}. Check network connection.",
                SocketError.NetworkUnreachable => "Network unreachable. Check your network connection.",
                _ => $"Network error: {ex.SocketErrorCode} - {ex.Message}"
            };

            System.Diagnostics.Debug.WriteLine($"Transfer failed: {errorMsg}");

            TransferCompleted?.Invoke(this, new TransferCompletedEventArgs
            {
                GameAppId = remoteGame.AppId,
                Success = false,
                ErrorMessage = errorMsg,
                IsNewDownload = isNewDownload
            });
            return false;
        }
        catch (Exception ex)
        {
            _currentTransferState?.Save();
            _currentTransferState = null;

            System.Diagnostics.Debug.WriteLine($"Transfer failed: {ex.Message}");

            TransferCompleted?.Invoke(this, new TransferCompletedEventArgs
            {
                GameAppId = remoteGame.AppId,
                Success = false,
                ErrorMessage = ex.Message,
                IsNewDownload = isNewDownload
            });
            return false;
        }
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        System.Diagnostics.Debug.WriteLine($"FileTransferService: Starting to accept connections on port {TransferPort}");
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                var remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                System.Diagnostics.Debug.WriteLine($"=== INCOMING FILE TRANSFER from {remoteIp} ===");
                _ = HandleTransferRequestAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Accept connection error: {ex.Message}");
            }
        }
        
        System.Diagnostics.Debug.WriteLine("FileTransferService: Stopped accepting connections");
    }

    private async Task HandleTransferRequestAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 30000;
                stream.WriteTimeout = 30000;
                
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

                // Read transfer request
                var requestJson = reader.ReadString();
                var request = JsonSerializer.Deserialize<FileTransferRequest>(requestJson);

                System.Diagnostics.Debug.WriteLine($"Received transfer request: {requestJson}");

                if (request == null)
                {
                    System.Diagnostics.Debug.WriteLine("Invalid request (null)");
                    writer.Write("{}");
                    return;
                }

                if (!Directory.Exists(request.GamePath))
                {
                    System.Diagnostics.Debug.WriteLine($"Game path doesn't exist: {request.GamePath}");
                    writer.Write("{}"); // Empty manifest
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"Building manifest for: {request.GamePath}");

                // Build and send file manifest
                var manifest = await BuildFileManifestAsync(request.GamePath);
                var manifestJson = JsonSerializer.Serialize(manifest);
                writer.Write(manifestJson);
                writer.Flush();

                System.Diagnostics.Debug.WriteLine($"Sent manifest with {manifest.Files.Count} files");

                // Handle file requests
                while (!ct.IsCancellationRequested)
                {
                    var relativePath = reader.ReadString();
                    
                    if (string.IsNullOrEmpty(relativePath))
                    {
                        System.Diagnostics.Debug.WriteLine("Transfer complete (client signaled end)");
                        break;
                    }

                    var fullPath = Path.Combine(request.GamePath, relativePath);
                    
                    if (!File.Exists(fullPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"File not found: {relativePath}");
                        writer.Write(-1L);
                        continue;
                    }

                    var fileInfo = new FileInfo(fullPath);
                    writer.Write(fileInfo.Length);
                    writer.Flush();

                    System.Diagnostics.Debug.WriteLine($"Sending: {relativePath} ({fileInfo.Length / 1024}KB)");

                    // Stream file content
                    await using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
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
            System.Diagnostics.Debug.WriteLine($"Transfer handler error: {ex.Message}");
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
                    if (file.Name == ".gamesync_transfer")
                        continue;

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
                
                if (localInfo.Length != remoteFile.Size)
                {
                    filesToDownload.Add(remoteFile);
                    continue;
                }

                var localHash = ComputeQuickHash(localFilePath, localInfo.Length);
                if (localHash != remoteFile.Hash)
                {
                    filesToDownload.Add(remoteFile);
                }
            }
        });

        return filesToDownload;
    }

    private static string ComputeQuickHash(string filePath, long fileSize)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var md5 = MD5.Create();

            if (fileSize <= 2 * 1024 * 1024)
            {
                var hash = md5.ComputeHash(stream);
                return Convert.ToHexString(hash);
            }

            var buffer = new byte[1024 * 1024];
            
            stream.Read(buffer, 0, buffer.Length);
            md5.TransformBlock(buffer, 0, buffer.Length, buffer, 0);

            stream.Seek(-buffer.Length, SeekOrigin.End);
            stream.Read(buffer, 0, buffer.Length);
            md5.TransformBlock(buffer, 0, buffer.Length, buffer, 0);

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
    public bool IsNewDownload { get; set; }
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
    public bool IsNewDownload { get; set; }
}
