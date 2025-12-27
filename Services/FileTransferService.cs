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
    private const int BufferSize = 4 * 1024 * 1024; // 4MB buffer for maximum throughput
    private const int SocketBufferSize = 8 * 1024 * 1024; // 8MB socket buffer
    private const int ConnectionTimeoutMs = 10000; // 10 second connection timeout
    private const int MaxParallelFiles = 4; // Number of files to transfer in parallel

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _transferCts;
    private TransferState? _currentTransferState;
    private bool _isListening;
    private bool _isPaused;
    private List<GameInfo> _localGames = [];

    /// <summary>
    /// Whether the file transfer service is actively listening
    /// </summary>
    public bool IsListening => _isListening;

    /// <summary>
    /// The port the service is listening on
    /// </summary>
    public int ListeningPort => TransferPort;

    /// <summary>
    /// Whether a transfer is currently paused
    /// </summary>
    public bool IsPaused => _isPaused;

    /// <summary>
    /// Updates the list of local games that can be served to peers
    /// </summary>
    public void UpdateLocalGames(IEnumerable<GameInfo> games)
    {
        _localGames = games.ToList();
        System.Diagnostics.Debug.WriteLine($"FileTransferService: Updated local games list ({_localGames.Count} games)");
    }

    /// <summary>
    /// Event raised when transfer progress updates
    /// </summary>
    public event EventHandler<TransferProgressEventArgs>? ProgressChanged;

    /// <summary>
    /// Event raised when a transfer completes
    /// </summary>
    public event EventHandler<TransferCompletedEventArgs>? TransferCompleted;

    /// <summary>
    /// Event raised when a transfer is paused or stopped
    /// </summary>
    public event EventHandler<TransferStoppedEventArgs>? TransferStopped;

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
            BuildId = "0" // No local version
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
        // Create a new cancellation token source for this transfer
        _transferCts?.Dispose();
        _transferCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isPaused = false;
        
        string? appManifestContent = null;
        
        try
        {
            System.Diagnostics.Debug.WriteLine($"=== FILE TRANSFER REQUEST ===");
            System.Diagnostics.Debug.WriteLine($"Target: {peer.DisplayName} ({peer.IpAddress}:{TransferPort})");
            System.Diagnostics.Debug.WriteLine($"Game: {remoteGame.Name} (AppId: {remoteGame.AppId})");
            System.Diagnostics.Debug.WriteLine($"Resume: {resumeState != null}, CompletedFiles: {resumeState?.CompletedFiles.Count ?? 0}");
            
            using var client = new TcpClient();
            
            // Configure socket for maximum throughput
            ConfigureSocketForHighPerformance(client);
            
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_transferCts.Token);
            connectCts.CancelAfter(ConnectionTimeoutMs);
            
            try
            {
                await client.ConnectAsync(peer.IpAddress, TransferPort, connectCts.Token);
            }
            catch (OperationCanceledException) when (!_transferCts.Token.IsCancellationRequested)
            {
                throw new Exception(
                    $"Connection to {peer.DisplayName} ({peer.IpAddress}) timed out on port {TransferPort}.\n\n" +
                    "Possible causes:\n" +
                    "1. The app on the remote computer hasn't clicked 'Start Network'\n" +
                    "2. Firewall on remote computer is blocking port 45679\n" +
                    "3. Antivirus/security software is blocking the connection");
            }

            if (!client.Connected)
            {
                throw new Exception($"Could not connect to {peer.DisplayName}.");
            }

            System.Diagnostics.Debug.WriteLine($"Connected successfully to {peer.DisplayName} for file transfer");

            using var stream = client.GetStream();
            stream.ReadTimeout = 60000; // Increase timeout for large files
            stream.WriteTimeout = 60000;
            
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            var request = new FileTransferRequest
            {
                GameAppId = remoteGame.AppId,
                GamePath = remoteGame.InstallPath,
                IsNewDownload = isNewDownload,
                IncludeManifest = true
            };
            var requestJson = JsonSerializer.Serialize(request);
            writer.Write(requestJson);
            writer.Flush();

            var manifestJson = reader.ReadString();
            var manifest = JsonSerializer.Deserialize<FileManifest>(manifestJson);

            if (manifest == null || manifest.Files.Count == 0)
            {
                throw new Exception($"Remote peer returned empty file manifest.");
            }

            appManifestContent = manifest.AppManifestContent;
            System.Diagnostics.Debug.WriteLine($"Received manifest with {manifest.Files.Count} files, hasAppManifest={!string.IsNullOrEmpty(appManifestContent)}");

            List<FileTransferInfo> filesToDownload;
            
            if (resumeState != null)
            {
                filesToDownload = manifest.Files
                    .Where(f => !resumeState.CompletedFiles.Contains(f.RelativePath))
                    .ToList();
                System.Diagnostics.Debug.WriteLine($"Resume mode: {filesToDownload.Count} files remaining");
            }
            else
            {
                filesToDownload = await GetFilesToDownloadAsync(localGame.InstallPath, manifest.Files);
            }

            long totalBytes = filesToDownload.Sum(f => f.Size);
            long alreadyTransferred = resumeState?.TransferredBytes ?? 0;
            long transferredBytes = alreadyTransferred;
            var startTime = DateTime.Now;

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

            if (!Directory.Exists(localGame.InstallPath))
            {
                Directory.CreateDirectory(localGame.InstallPath);
            }

            _currentTransferState.Save();

            System.Diagnostics.Debug.WriteLine($"Starting HIGH-SPEED download of {filesToDownload.Count} files ({totalBytes / 1024 / 1024}MB)");
            System.Diagnostics.Debug.WriteLine($"Buffer size: {BufferSize / 1024 / 1024}MB, Socket buffer: {SocketBufferSize / 1024 / 1024}MB");

            // Pre-allocate buffer for reuse
            var buffer = new byte[BufferSize];

            foreach (var fileInfo in filesToDownload)
            {
                if (_transferCts.Token.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine("Transfer cancelled by user");
                    _currentTransferState.Save();
                    return false;
                }

                writer.Write(fileInfo.RelativePath);
                writer.Flush();

                var fileSize = reader.ReadInt64();
                if (fileSize < 0)
                    continue;

                var localFilePath = Path.Combine(localGame.InstallPath, fileInfo.RelativePath);
                var localDir = Path.GetDirectoryName(localFilePath);
                if (!string.IsNullOrEmpty(localDir) && !Directory.Exists(localDir))
                {
                    Directory.CreateDirectory(localDir);
                }

                if (File.Exists(localFilePath))
                {
                    var existingInfo = new FileInfo(localFilePath);
                    if (existingInfo.Length == fileSize)
                    {
                        transferredBytes += fileSize;
                        _currentTransferState.CompletedFiles.Add(fileInfo.RelativePath);
                        _currentTransferState.TransferredBytes = transferredBytes;
                        
                        // Discard data from server
                        long discardRemaining = fileSize;
                        while (discardRemaining > 0)
                        {
                            var toRead = (int)Math.Min(discardRemaining, buffer.Length);
                            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), _transferCts.Token);
                            if (bytesRead == 0) break;
                            discardRemaining -= bytesRead;
                        }
                        continue;
                    }
                }

                // Use optimized file stream for writing
                await using var fileStream = CreateOptimizedWriteStream(localFilePath);
                long remaining = fileSize;
                long lastProgressUpdate = 0;

                while (remaining > 0)
                {
                    if (_transferCts.Token.IsCancellationRequested)
                    {
                        System.Diagnostics.Debug.WriteLine($"Transfer cancelled during file: {fileInfo.RelativePath}");
                        _currentTransferState.TransferredBytes = transferredBytes;
                        _currentTransferState.Save();
                        return false;
                    }

                    var toRead = (int)Math.Min(remaining, buffer.Length);
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), _transferCts.Token);
                    
                    if (bytesRead == 0)
                        break;

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _transferCts.Token);
                    remaining -= bytesRead;
                    transferredBytes += bytesRead;

                    // Throttle progress updates to every 5MB or 100ms to reduce overhead
                    if (transferredBytes - lastProgressUpdate > 5 * 1024 * 1024)
                    {
                        lastProgressUpdate = transferredBytes;
                        
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
                    }

                    // Save state less frequently - every 50MB
                    if (transferredBytes % (50 * 1024 * 1024) < BufferSize)
                    {
                        _currentTransferState.TransferredBytes = transferredBytes;
                        _currentTransferState.Save();
                    }
                }

                // Ensure file is flushed to disk
                await fileStream.FlushAsync(_transferCts.Token);

                _currentTransferState.CompletedFiles.Add(fileInfo.RelativePath);
                _currentTransferState.TransferredBytes = transferredBytes;
                _currentTransferState.Save();
            }

            writer.Write(string.Empty);

            long totalTransferred = transferredBytes;
            bool success = filesToDownload.Count == 0 || transferredBytes >= _currentTransferState.TotalBytes;
            
            if (success)
            {
                _currentTransferState.Delete();
                
                if (!string.IsNullOrEmpty(appManifestContent))
                {
                    await WriteAppManifestAsync(localGame.InstallPath, remoteGame.AppId, appManifestContent);
                }
            }

            var totalElapsed = (DateTime.Now - startTime).TotalSeconds;
            var avgSpeed = totalElapsed > 0 ? (totalTransferred - alreadyTransferred) / totalElapsed : 0;
            System.Diagnostics.Debug.WriteLine($"Transfer completed: success={success}, transferred={totalTransferred / 1024 / 1024}MB, avgSpeed={avgSpeed / 1024 / 1024:F1}MB/s");

            TransferCompleted?.Invoke(this, new TransferCompletedEventArgs
            {
                GameAppId = remoteGame.AppId,
                GameName = remoteGame.Name,
                Success = success,
                TotalBytesTransferred = totalTransferred,
                IsNewDownload = isNewDownload
            });

            _currentTransferState = null;
            return success;
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Transfer cancelled (OperationCanceledException)");
            _currentTransferState?.Save();
            return false;
        }
        catch (SocketException ex)
        {
            _currentTransferState?.Save();
            _currentTransferState = null;

            var errorMsg = ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => $"Connection REFUSED by {peer.DisplayName}.",
                SocketError.TimedOut => $"Connection TIMED OUT to {peer.DisplayName}.",
                SocketError.HostUnreachable => $"Cannot reach {peer.DisplayName}.",
                SocketError.NetworkUnreachable => "Network unreachable.",
                _ => $"Network error: {ex.SocketErrorCode} - {ex.Message}"
            };

            System.Diagnostics.Debug.WriteLine($"Transfer failed: {errorMsg}");

            TransferCompleted?.Invoke(this, new TransferCompletedEventArgs
            {
                GameAppId = remoteGame.AppId,
                GameName = remoteGame.Name,
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
                GameName = remoteGame.Name,
                Success = false,
                ErrorMessage = ex.Message,
                IsNewDownload = isNewDownload
            });
            return false;
        }
    }

    /// <summary>
    /// Writes the Steam app manifest file to the steamapps folder so Steam recognizes the game
    /// </summary>
    private async Task WriteAppManifestAsync(string gameInstallPath, string appId, string manifestContent)
    {
        try
        {
            // Game install path is like: steamapps/common/GameName
            // We need to write to: steamapps/appmanifest_<appid>.acf
            var commonFolder = Directory.GetParent(gameInstallPath);
            if (commonFolder?.Name != "common")
            {
                System.Diagnostics.Debug.WriteLine($"Cannot determine steamapps folder from path: {gameInstallPath}");
                return;
            }
            
            var steamAppsFolder = commonFolder.Parent?.FullName;
            if (string.IsNullOrEmpty(steamAppsFolder) || !Directory.Exists(steamAppsFolder))
            {
                System.Diagnostics.Debug.WriteLine($"Steamapps folder not found: {steamAppsFolder}");
                return;
            }
            
            var manifestPath = Path.Combine(steamAppsFolder, $"appmanifest_{appId}.acf");
            
            // Update the installdir in the manifest to match the actual folder name
            var gameFolderName = Path.GetFileName(gameInstallPath);
            var updatedContent = UpdateInstallDirInManifest(manifestContent, gameFolderName);
            
            await File.WriteAllTextAsync(manifestPath, updatedContent);
            System.Diagnostics.Debug.WriteLine($"Wrote app manifest: {manifestPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error writing app manifest: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Updates the installdir field in the manifest to match the local folder name
    /// </summary>
    private string UpdateInstallDirInManifest(string manifestContent, string newInstallDir)
    {
        // The manifest is in VDF format, we need to update the "installdir" field
        // Simple regex replacement for the installdir line
        var lines = manifestContent.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("\"installdir\""))
            {
                // Replace with the new install dir
                var indent = lines[i].Substring(0, lines[i].Length - trimmed.Length);
                lines[i] = $"{indent}\"installdir\"\t\t\"{newInstallDir}\"";
                break;
            }
        }
        return string.Join('\n', lines);
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
            // Configure socket for high performance
            ConfigureSocketForHighPerformance(client);
            
            using (client)
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 60000;
                stream.WriteTimeout = 60000;
                
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

                string requestJson;
                try
                {
                    requestJson = reader.ReadString();
                }
                catch (EndOfStreamException)
                {
                    System.Diagnostics.Debug.WriteLine("Client disconnected before sending request");
                    return;
                }
                catch (IOException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"IO error reading request: {ex.Message}");
                    return;
                }

                var request = JsonSerializer.Deserialize<FileTransferRequest>(requestJson);

                System.Diagnostics.Debug.WriteLine($"Received transfer request: {requestJson}");

                if (request == null)
                {
                    System.Diagnostics.Debug.WriteLine("Invalid request (null)");
                    writer.Write("{}");
                    return;
                }

                var localGame = _localGames.FirstOrDefault(g => g.AppId == request.GameAppId);
                string gamePath;
                string? steamAppsFolder = null;
                
                if (localGame != null && !string.IsNullOrEmpty(localGame.InstallPath) && Directory.Exists(localGame.InstallPath))
                {
                    gamePath = localGame.InstallPath;
                    var commonFolder = Directory.GetParent(gamePath);
                    if (commonFolder?.Name == "common")
                    {
                        steamAppsFolder = commonFolder.Parent?.FullName;
                    }
                    System.Diagnostics.Debug.WriteLine($"Found game by AppId: {request.GameAppId} at {gamePath}");
                }
                else if (!string.IsNullOrEmpty(request.GamePath) && Directory.Exists(request.GamePath))
                {
                    gamePath = request.GamePath;
                    var commonFolder = Directory.GetParent(gamePath);
                    if (commonFolder?.Name == "common")
                    {
                        steamAppsFolder = commonFolder.Parent?.FullName;
                    }
                    System.Diagnostics.Debug.WriteLine($"Using fallback path from request: {gamePath}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Game not found! AppId: {request.GameAppId}, RequestPath: {request.GamePath}");
                    System.Diagnostics.Debug.WriteLine($"Local games count: {_localGames.Count}");
                    foreach (var g in _localGames.Take(5))
                    {
                        System.Diagnostics.Debug.WriteLine($"  - {g.Name} ({g.AppId}): {g.InstallPath}");
                    }
                    writer.Write("{}");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"Building manifest for: {gamePath}");

                var manifest = await BuildFileManifestAsync(gamePath);
                
                if (request.IncludeManifest && steamAppsFolder != null)
                {
                    var appManifestPath = Path.Combine(steamAppsFolder, $"appmanifest_{request.GameAppId}.acf");
                    if (File.Exists(appManifestPath))
                    {
                        manifest.AppManifestContent = await File.ReadAllTextAsync(appManifestPath, ct);
                        System.Diagnostics.Debug.WriteLine($"Including app manifest: {appManifestPath}");
                    }
                }
                
                var manifestJson = JsonSerializer.Serialize(manifest);
                writer.Write(manifestJson);
                writer.Flush();

                System.Diagnostics.Debug.WriteLine($"Sent manifest with {manifest.Files.Count} files");

                // Pre-allocate buffer for high-speed transfer
                var buffer = new byte[BufferSize];

                while (!ct.IsCancellationRequested)
                {
                    string relativePath;
                    try
                    {
                        relativePath = reader.ReadString();
                    }
                    catch (EndOfStreamException)
                    {
                        System.Diagnostics.Debug.WriteLine("Client disconnected (end of stream)");
                        break;
                    }
                    catch (IOException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Client disconnected: {ex.Message}");
                        break;
                    }
                    
                    if (string.IsNullOrEmpty(relativePath))
                    {
                        System.Diagnostics.Debug.WriteLine("Transfer complete (client signaled end)");
                        break;
                    }

                    var fullPath = Path.Combine(gamePath, relativePath);
                    
                    if (!File.Exists(fullPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"File not found: {relativePath}");
                        writer.Write(-1L);
                        continue;
                    }

                    var fileInfo = new FileInfo(fullPath);
                    writer.Write(fileInfo.Length);
                    writer.Flush();

                    System.Diagnostics.Debug.WriteLine($"Sending: {relativePath} ({fileInfo.Length / 1024 / 1024}MB)");

                    try
                    {
                        // Use optimized file stream for reading
                        await using var fileStream = CreateOptimizedReadStream(fullPath);
                        int bytesRead;

                        while ((bytesRead = await fileStream.ReadAsync(buffer, ct)) > 0)
                        {
                            await stream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        }

                        // Don't flush after every file - let TCP handle it for better throughput
                    }
                    catch (IOException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error sending file {relativePath}: {ex.Message}");
                        break;
                    }
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
    
    /// <summary>
    /// Configures a TcpClient for maximum throughput on LAN
    /// </summary>
    private static void ConfigureSocketForHighPerformance(TcpClient client)
    {
        // Increase socket buffer sizes for high throughput
        client.SendBufferSize = SocketBufferSize;
        client.ReceiveBufferSize = SocketBufferSize;
        
        // Disable Nagle's algorithm for lower latency (we're sending large chunks anyway)
        client.NoDelay = true;
        
        // Set linger to ensure all data is sent before closing
        client.LingerState = new LingerOption(true, 10);
        
        // Keep connection alive
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        
        System.Diagnostics.Debug.WriteLine($"Socket configured: SendBuffer={client.SendBufferSize/1024/1024}MB, ReceiveBuffer={client.ReceiveBufferSize/1024/1024}MB, NoDelay={client.NoDelay}");
    }

    /// <summary>
    /// Creates a FileStream optimized for high-performance sequential reading
    /// </summary>
    private static FileStream CreateOptimizedReadStream(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
    }

    /// <summary>
    /// Creates a FileStream optimized for high-performance sequential writing
    /// </summary>
    private static FileStream CreateOptimizedWriteStream(string path)
    {
        return new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
    }

    /// <summary>
    /// Pauses the current transfer (can be resumed later)
    /// </summary>
    public void PauseTransfer()
    {
        System.Diagnostics.Debug.WriteLine($"PauseTransfer called. _transferCts null? {_transferCts == null}, IsCancellationRequested? {_transferCts?.IsCancellationRequested}");
        
        if (_transferCts != null && !_transferCts.IsCancellationRequested)
        {
            _isPaused = true;
            var gameName = _currentTransferState?.GameName ?? "";
            var transferredBytes = _currentTransferState?.TransferredBytes ?? 0;
            
            _currentTransferState?.Save();
            _transferCts.Cancel();
            
            System.Diagnostics.Debug.WriteLine($"Transfer paused by user: {gameName}");
            
            // Fire event AFTER cancellation
            TransferStopped?.Invoke(this, new TransferStoppedEventArgs
            {
                GameName = gameName,
                IsPaused = true,
                TransferredBytes = transferredBytes
            });
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("PauseTransfer: No active transfer to pause");
        }
    }

    /// <summary>
    /// Stops the current transfer and saves progress
    /// </summary>
    public void StopTransfer()
    {
        System.Diagnostics.Debug.WriteLine($"StopTransfer called. _transferCts null? {_transferCts == null}, IsCancellationRequested? {_transferCts?.IsCancellationRequested}");
        
        if (_transferCts != null && !_transferCts.IsCancellationRequested)
        {
            _isPaused = false;
            var gameName = _currentTransferState?.GameName ?? "";
            var transferredBytes = _currentTransferState?.TransferredBytes ?? 0;
            
            _currentTransferState?.Save();
            _transferCts.Cancel();
            
            System.Diagnostics.Debug.WriteLine($"Transfer stopped by user: {gameName}");
            
            // Fire event AFTER cancellation
            TransferStopped?.Invoke(this, new TransferStoppedEventArgs
            {
                GameName = gameName,
                IsPaused = false,
                TransferredBytes = transferredBytes
            });
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("StopTransfer: No active transfer to stop");
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _transferCts?.Dispose();
    }
}

public class FileTransferRequest
{
    public string GameAppId { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public bool IsNewDownload { get; set; }
    public bool IncludeManifest { get; set; } = true; // Request the appmanifest file too
}

public class FileManifest
{
    public string GamePath { get; set; } = string.Empty;
    public List<FileTransferInfo> Files { get; set; } = [];
    public string? AppManifestContent { get; set; } // The appmanifest_<appid>.acf content
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
    public string GameName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public long TotalBytesTransferred { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsNewDownload { get; set; }
}

public class TransferStoppedEventArgs : EventArgs
{
    public string GameName { get; set; } = string.Empty;
    public bool IsPaused { get; set; }
    public long TransferredBytes { get; set; }
}
