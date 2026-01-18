using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

/// <summary>
/// JSON serialization context for FileTransfer types
/// </summary>
[JsonSerializable(typeof(FileTransferRequest))]
[JsonSerializable(typeof(FileManifest))]
[JsonSerializable(typeof(FileTransferInfo))]
[JsonSerializable(typeof(List<FileTransferInfo>))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class FileTransferJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Service for transferring game files between peers
/// </summary>
public class FileTransferService : IDisposable
{
    private const int PrimaryTransferPort = 45679;
    private static readonly int[] FallbackPorts = [45680, 45681, 45682, 45683];
    private const int ConnectionTimeoutMs = 10000;
    private const int MaxParallelFiles = 4;
    
    // Adaptive buffer sizes - smaller for WiFi, larger for wired
    // WiFi typically benefits from smaller buffers due to latency/jitter
    private const int DefaultBufferSize = 256 * 1024; // 256KB - good balance for WiFi
    private const int DefaultSocketBufferSize = 512 * 1024; // 512KB socket buffer
    private const int LargeBufferSize = 4 * 1024 * 1024; // 4MB for high-speed wired
    private const int LargeSocketBufferSize = 8 * 1024 * 1024; // 8MB for high-speed wired

    // Current buffer sizes (can be adjusted)
    private int _bufferSize = DefaultBufferSize;
    private int _socketBufferSize = DefaultSocketBufferSize;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _transferCts;
    private TransferState? _currentTransferState;
    private bool _isListening;
    private bool _isPaused;
    private ObservableCollection<GameInfo> _localGames = [];
    private int _actualListeningPort = PrimaryTransferPort;

    /// <summary>
    /// Whether the file transfer service is actively listening
    /// </summary>
    public bool IsListening => _isListening;

    /// <summary>
    /// The port the service is actually listening on (may differ from primary if fallback was used)
    /// </summary>
    public int ListeningPort => _actualListeningPort;

    /// <summary>
    /// Whether a transfer is currently paused
    /// </summary>
    public bool IsPaused => _isPaused;

    /// <summary>
    /// Whether high-speed mode is enabled (for wired connections)
    /// </summary>
    public bool IsHighSpeedMode => _bufferSize == LargeBufferSize;

    /// <summary>
    /// Sets transfer mode for optimal performance based on connection type.
    /// Enable for wired/high-speed connections, disable for WiFi.
    /// </summary>
    public void SetHighSpeedMode(bool enabled)
    {
        if (enabled)
        {
            _bufferSize = LargeBufferSize;
            _socketBufferSize = LargeSocketBufferSize;
            System.Diagnostics.Debug.WriteLine($"High-speed mode ENABLED: Buffer={_bufferSize / 1024}KB, Socket={_socketBufferSize / 1024}KB");
        }
        else
        {
            _bufferSize = DefaultBufferSize;
            _socketBufferSize = DefaultSocketBufferSize;
            System.Diagnostics.Debug.WriteLine($"WiFi-optimized mode: Buffer={_bufferSize / 1024}KB, Socket={_socketBufferSize / 1024}KB");
        }
    }

    /// <summary>
    /// Updates the list of local games that can be served to peers
    /// </summary>
    public void UpdateLocalGames(IEnumerable<GameInfo> games)
    {
        _localGames = new ObservableCollection<GameInfo>(games);
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
        
        // Try primary port first, then fallback ports
        var portsToTry = new List<int> { PrimaryTransferPort };
        portsToTry.AddRange(FallbackPorts);
        
        Exception? lastException = null;
        
        foreach (var port in portsToTry)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _isListening = true;
                _actualListeningPort = port;
                
                if (port != PrimaryTransferPort)
                {
                    System.Diagnostics.Debug.WriteLine($"FileTransferService: Primary port {PrimaryTransferPort} unavailable, using fallback port {port}");
                }
                
                System.Diagnostics.Debug.WriteLine($"FileTransferService SUCCESSFULLY listening on port {port}");
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                System.Diagnostics.Debug.WriteLine($"FileTransferService: Port {port} is already in use, trying next port...");
                lastException = ex;
                _listener?.Stop();
                _listener = null;
                continue;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FileTransferService: Error on port {port}: {ex.Message}");
                lastException = ex;
                _listener?.Stop();
                _listener = null;
                continue;
            }
        }
        
        if (!_isListening)
        {
            var errorMsg = $"Could not start file transfer service. All ports ({PrimaryTransferPort}, {string.Join(", ", FallbackPorts)}) are in use or blocked.";
            System.Diagnostics.Debug.WriteLine($"FileTransferService FAILED: {errorMsg}");
            throw new InvalidOperationException(errorMsg, lastException);
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
    /// Tests if a specific port is available
    /// </summary>
    public static bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
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
    /// Tests if the primary file transfer port is available
    /// </summary>
    public static bool IsPrimaryPortAvailable() => IsPortAvailable(PrimaryTransferPort);

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
        System.Diagnostics.Debug.WriteLine($"=== RequestNewGameDownloadAsync ===");
        System.Diagnostics.Debug.WriteLine($"  Peer: {peer.DisplayName} ({peer.IpAddress})");
        System.Diagnostics.Debug.WriteLine($"  Remote Game: {remoteGame.Name} (AppId: {remoteGame.AppId})");
        System.Diagnostics.Debug.WriteLine($"  Remote Game InstallPath: {remoteGame.InstallPath}");
        System.Diagnostics.Debug.WriteLine($"  Target Path (local): {targetPath}");
        
        var localGame = new GameInfo
        {
            AppId = remoteGame.AppId,
            Name = remoteGame.Name,
            InstallPath = targetPath,
            BuildId = "0"
        };

        // For new downloads, we need to tell the peer which game we want by AppId
        // The InstallPath should be empty so the peer uses their own game's path
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
            InstallPath = state.TargetPath,
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
        _transferCts?.Dispose();
        _transferCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isPaused = false;
        
        // Run the entire transfer operation on a background thread to avoid blocking the UI
        return await Task.Run(async () =>
        {
            string? appManifestContent = null;
            
            // Determine which port to use - prefer peer's advertised FileTransferPort, fallback to default
            var transferPort = peer.FileTransferPort > 0 ? peer.FileTransferPort : PrimaryTransferPort;
            
            try
            {
                System.Diagnostics.Debug.WriteLine($"=== FILE TRANSFER REQUEST ===");
                System.Diagnostics.Debug.WriteLine($"Target: {peer.DisplayName} ({peer.IpAddress}:{transferPort})");
                System.Diagnostics.Debug.WriteLine($"Peer's advertised FileTransferPort: {peer.FileTransferPort}");
                System.Diagnostics.Debug.WriteLine($"Game: {remoteGame.Name} (AppId: {remoteGame.AppId})");
                System.Diagnostics.Debug.WriteLine($"Buffer: {_bufferSize / 1024}KB, Socket: {_socketBufferSize / 1024}KB");
                System.Diagnostics.Debug.WriteLine($"Resume: {resumeState != null}, CompletedFiles: {resumeState?.CompletedFiles.Count ?? 0}");
                
                using var client = new TcpClient();
                
                ConfigureSocketForHighPerformance(client);
                
                // Try the peer's advertised port first, then try fallback ports
                var portsToTry = new List<int> { transferPort };
                if (transferPort != PrimaryTransferPort)
                {
                    portsToTry.Add(PrimaryTransferPort);
                }
                portsToTry.AddRange(FallbackPorts.Where(p => p != transferPort));
                
                bool connected = false;
                int connectedPort = 0;
                
                foreach (var port in portsToTry)
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"Trying to connect to {peer.IpAddress}:{port}...");
                        
                        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_transferCts.Token);
                        connectCts.CancelAfter(ConnectionTimeoutMs / portsToTry.Count); // Divide timeout among ports
                        
                        // Need to create new client for each attempt
                        if (connected) break;
                        
                        var testClient = new TcpClient();
                        ConfigureSocketForHighPerformance(testClient);
                        
                        await testClient.ConnectAsync(peer.IpAddress, port, connectCts.Token);
                        
                        if (testClient.Connected)
                        {
                            connected = true;
                            connectedPort = port;
                            testClient.Close();
                            System.Diagnostics.Debug.WriteLine($"✓ Successfully connected to port {port}");
                            break;
                        }
                        testClient.Close();
                    }
                    catch (OperationCanceledException)
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ Connection to port {port} timed out");
                        continue;
                    }
                    catch (SocketException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ Connection to port {port} failed: {ex.SocketErrorCode}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ Connection to port {port} failed: {ex.Message}");
                        continue;
                    }
                }
                
                if (!connected)
                {
                    throw new Exception(
                        $"Connection to {peer.DisplayName} ({peer.IpAddress}) failed on all ports.\n\n" +
                        $"Tried ports: {string.Join(", ", portsToTry)}\n\n" +
                        "Possible causes:\n" +
                        "1. The peer computer hasn't clicked 'Start Network' yet\n" +
                        "   → Ask them to click 'Start Network' and look for 'File transfer ready' in their log\n" +
                        "2. Firewall on the peer computer is blocking the connection\n" +
                        "   → They need to click 'Configure Firewall' as Administrator\n" +
                        "3. Antivirus/security software is blocking the connection\n" +
                        "   → Check Windows Security or third-party antivirus settings");
                }
                
                // Now connect for real with the working port
                using var connectCts2 = CancellationTokenSource.CreateLinkedTokenSource(_transferCts.Token);
                connectCts2.CancelAfter(ConnectionTimeoutMs);
                await client.ConnectAsync(peer.IpAddress, connectedPort, connectCts2.Token);

                if (!client.Connected)
                {
                    throw new Exception($"Could not connect to {peer.DisplayName}.");
                }

                System.Diagnostics.Debug.WriteLine($"Connected successfully to {peer.DisplayName} on port {connectedPort} for file transfer");

                using var stream = client.GetStream();
                stream.ReadTimeout = 60000;
                stream.WriteTimeout = 60000;
                
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

                var request = new FileTransferRequest
                {
                    GameAppId = remoteGame.AppId,
                    GamePath = isNewDownload ? string.Empty : remoteGame.InstallPath, // For new downloads, let the peer use their own path
                    IsNewDownload = isNewDownload,
                    IncludeManifest = true
                };
                
                System.Diagnostics.Debug.WriteLine($"=== Sending Transfer Request ===");
                System.Diagnostics.Debug.WriteLine($"  GameAppId: {request.GameAppId}");
                System.Diagnostics.Debug.WriteLine($"  GamePath: '{request.GamePath}' (empty = let peer decide)");
                System.Diagnostics.Debug.WriteLine($"  IsNewDownload: {request.IsNewDownload}");
                System.Diagnostics.Debug.WriteLine($"  IncludeManifest: {request.IncludeManifest}");
                
                var requestJson = JsonSerializer.Serialize(request, FileTransferJsonContext.Default.FileTransferRequest);
                System.Diagnostics.Debug.WriteLine($"  Request JSON: {requestJson}");
                
                writer.Write(requestJson);
                writer.Flush();

                var manifestJson = reader.ReadString();
                var manifest = JsonSerializer.Deserialize(manifestJson, FileTransferJsonContext.Default.FileManifest);

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

                System.Diagnostics.Debug.WriteLine($"Starting download of {filesToDownload.Count} files ({totalBytes / 1024 / 1024}MB)");

                // Pre-allocate buffer for reuse
                var buffer = new byte[_bufferSize];

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

                        // Update progress every 1MB for WiFi (more responsive) or 5MB for wired
                        var progressInterval = IsHighSpeedMode ? 5 * 1024 * 1024 : 1 * 1024 * 1024;
                        if (transferredBytes - lastProgressUpdate > progressInterval)
                        {
                            lastProgressUpdate = transferredBytes;
                            
                            var elapsed = (DateTime.Now - startTime).TotalSeconds;
                            var speed = elapsed > 0 ? (long)((transferredBytes - alreadyTransferred) / elapsed) : 0;
                            var progress = _currentTransferState.TotalBytes > 0 
                                ? (double)transferredBytes / _currentTransferState.TotalBytes * 100 
                                : 0;

                            // Calculate estimated time remaining
                            var bytesRemaining = _currentTransferState.TotalBytes - transferredBytes;
                            var estimatedSecondsRemaining = speed > 0 ? bytesRemaining / (double)speed : 0;
                            var timeRemaining = TimeSpan.FromSeconds(estimatedSecondsRemaining);

                            ProgressChanged?.Invoke(this, new TransferProgressEventArgs
                            {
                                GameAppId = remoteGame.AppId,
                                Progress = progress,
                                TransferredBytes = transferredBytes,
                                TotalBytes = _currentTransferState.TotalBytes,
                                SpeedBytesPerSecond = speed,
                                CurrentFile = fileInfo.RelativePath,
                                EstimatedTimeRemaining = timeRemaining
                            });
                        }

                        // Save state every 50MB
                        if (transferredBytes % (50 * 1024 * 1024) < _bufferSize)
                        {
                            _currentTransferState.TransferredBytes = transferredBytes;
                            _currentTransferState.Save();
                        }
                    }

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
                var avgSpeedMbps = avgSpeed * 8 / 1_000_000;
                System.Diagnostics.Debug.WriteLine($"Transfer completed: success={success}, transferred={totalTransferred / 1024 / 1024}MB, avgSpeed={avgSpeed / 1024 / 1024:F1}MB/s ({avgSpeedMbps:F1} Mbps)");

                if (success)
                {
                    TransferCompleted?.Invoke(this, new TransferCompletedEventArgs
                    {
                        GameAppId = remoteGame.AppId,
                        GameName = remoteGame.Name,
                        Success = true,
                        TotalBytesTransferred = totalTransferred,
                        IsNewDownload = isNewDownload
                    });
                }
                else if (_isPaused)
                {
                    TransferStopped?.Invoke(this, new TransferStoppedEventArgs
                    {
                        GameName = remoteGame.Name,
                        IsPaused = true,
                        TransferredBytes = totalTransferred
                    });
                }
                else
                {
                    TransferCompleted?.Invoke(this, new TransferCompletedEventArgs
                    {
                        GameAppId = remoteGame.AppId,
                        GameName = remoteGame.Name,
                        Success = false,
                        TotalBytesTransferred = totalTransferred,
                        ErrorMessage = "Transfer failed",
                        IsNewDownload = isNewDownload
                    });
                }

                _currentTransferState = null;
                return success;
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("Transfer cancelled (OperationCanceledException)");
                _currentTransferState?.Save();
                
                if (_isPaused)
                {
                    TransferStopped?.Invoke(this, new TransferStoppedEventArgs
                    {
                        GameName = remoteGame.Name,
                        IsPaused = true,
                        TransferredBytes = _currentTransferState?.TransferredBytes ?? 0
                    });
                }
                else
                {
                    TransferCompleted?.Invoke(this, new TransferCompletedEventArgs
                    {
                        GameAppId = remoteGame.AppId,
                        GameName = remoteGame.Name,
                        Success = false,
                        ErrorMessage = "Transfer cancelled",
                        IsNewDownload = isNewDownload
                    });
                }
                
                _currentTransferState = null;
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
        }, _transferCts.Token).ConfigureAwait(false);
    }

    private async Task WriteAppManifestAsync(string gameInstallPath, string appId, string manifestContent)
    {
        try
        {
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
    
    private string UpdateInstallDirInManifest(string manifestContent, string newInstallDir)
    {
        var lines = manifestContent.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("\"installdir\""))
            {
                var indent = lines[i].Substring(0, lines[i].Length - trimmed.Length);
                lines[i] = $"{indent}\"installdir\"\t\t\"{newInstallDir}\"";
                break;
            }
        }
        return string.Join('\n', lines);
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        System.Diagnostics.Debug.WriteLine($"FileTransferService: Starting to accept connections on port {PrimaryTransferPort}");
        
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

                var request = JsonSerializer.Deserialize(requestJson, FileTransferJsonContext.Default.FileTransferRequest);

                System.Diagnostics.Debug.WriteLine($"=== Processing Transfer Request ===");
                System.Diagnostics.Debug.WriteLine($"  Received JSON: {requestJson}");

                if (request == null)
                {
                    System.Diagnostics.Debug.WriteLine("ERROR: Invalid request (null after deserialization)");
                    writer.Write("{}");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"  GameAppId: {request.GameAppId}");
                System.Diagnostics.Debug.WriteLine($"  GamePath: '{request.GamePath}'");
                System.Diagnostics.Debug.WriteLine($"  IsNewDownload: {request.IsNewDownload}");
                System.Diagnostics.Debug.WriteLine($"  IncludeManifest: {request.IncludeManifest}");
                System.Diagnostics.Debug.WriteLine($"  Local games in memory: {_localGames.Count}");

                var localGame = _localGames.FirstOrDefault(g => g.AppId == request.GameAppId);
                
                System.Diagnostics.Debug.WriteLine($"  Lookup by AppId '{request.GameAppId}': {(localGame != null ? "FOUND" : "NOT FOUND")}");
                if (localGame != null)
                {
                    System.Diagnostics.Debug.WriteLine($"    Found: {localGame.Name} at {localGame.InstallPath}");
                }
                
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
                    System.Diagnostics.Debug.WriteLine($"✓ SUCCESS: Found game by AppId: {request.GameAppId} at {gamePath}");
                }
                else if (!string.IsNullOrEmpty(request.GamePath) && Directory.Exists(request.GamePath))
                {
                    gamePath = request.GamePath;
                    var commonFolder = Directory.GetParent(gamePath);
                    if (commonFolder?.Name == "common")
                    {
                        steamAppsFolder = commonFolder.Parent?.FullName;
                    }
                    System.Diagnostics.Debug.WriteLine($"✓ Using fallback path from request: {gamePath}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"✗ ERROR: Game not found!");
                    System.Diagnostics.Debug.WriteLine($"  Requested AppId: {request.GameAppId}");
                    System.Diagnostics.Debug.WriteLine($"  Requested GamePath: '{request.GamePath}'");
                    System.Diagnostics.Debug.WriteLine($"  Local games count: {_localGames.Count}");
                    System.Diagnostics.Debug.WriteLine($"  Available games:");
                    foreach (var g in _localGames.Take(10))
                    {
                        System.Diagnostics.Debug.WriteLine($"    - AppId: {g.AppId}, Name: {g.Name}, Path: {g.InstallPath}");
                    }
                    writer.Write("{}");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"Building manifest for: {gamePath}");

                var manifest = await BuildFileManifestAsync(gamePath).ConfigureAwait(false);
                
                if (request.IncludeManifest && steamAppsFolder != null)
                {
                    var appManifestPath = Path.Combine(steamAppsFolder, $"appmanifest_{request.GameAppId}.acf");
                    if (File.Exists(appManifestPath))
                    {
                        manifest.AppManifestContent = await File.ReadAllTextAsync(appManifestPath, ct).ConfigureAwait(false);
                        System.Diagnostics.Debug.WriteLine($"Including app manifest: {appManifestPath}");
                    }
                }
                
                var manifestJson = JsonSerializer.Serialize(manifest, FileTransferJsonContext.Default.FileManifest);
                writer.Write(manifestJson);
                writer.Flush();

                System.Diagnostics.Debug.WriteLine($"Sent manifest with {manifest.Files.Count} files");

                // Pre-allocate buffer for transfer
                var buffer = new byte[_bufferSize];
                
                // Track statistics for periodic logging (avoid per-file logging)
                int filesSent = 0;
                long bytesSent = 0;
                var lastLogTime = DateTime.Now;
                const int LogIntervalMs = 2000; // Log every 2 seconds instead of every file

                while (!ct.IsCancellationRequested)
                {
                    string relativePath;
                    try
                    {
                        relativePath = reader.ReadString();
                    }
                    catch (EndOfStreamException)
                    {
                        System.Diagnostics.Debug.WriteLine($"Transfer complete: sent {filesSent} files, {bytesSent / 1024 / 1024}MB total");
                        break;
                    }
                    catch (IOException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Client disconnected: {ex.Message}");
                        break;
                    }
                    
                    if (string.IsNullOrEmpty(relativePath))
                    {
                        System.Diagnostics.Debug.WriteLine($"Transfer complete (client signaled end): sent {filesSent} files, {bytesSent / 1024 / 1024}MB total");
                        break;
                    }

                    var fullPath = Path.Combine(gamePath, relativePath);
                    
                    if (!File.Exists(fullPath))
                    {
                        // Only log file not found errors (these are unusual)
                        System.Diagnostics.Debug.WriteLine($"File not found: {relativePath}");
                        writer.Write(-1L);
                        continue;
                    }

                    var fileInfo = new FileInfo(fullPath);
                    writer.Write(fileInfo.Length);
                    writer.Flush();

                    try
                    {
                        await using var fileStream = CreateOptimizedReadStream(fullPath);
                        int bytesRead;

                        while ((bytesRead = await fileStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await stream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                            bytesSent += bytesRead;
                        }
                        
                        filesSent++;
                        
                        // Periodic logging instead of per-file logging
                        var now = DateTime.Now;
                        if ((now - lastLogTime).TotalMilliseconds > LogIntervalMs)
                        {
                            lastLogTime = now;
                            System.Diagnostics.Debug.WriteLine($"Sending progress: {filesSent} files, {bytesSent / 1024 / 1024}MB sent");
                        }
                        
                        // For very small files, yield to prevent UI starvation
                        // This allows the UI thread to process events between files
                        if (fileInfo.Length < 64 * 1024) // Files smaller than 64KB
                        {
                            await Task.Yield();
                        }
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
            var files = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories);
            
            foreach (var file in files)
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
                        // Skip hash computation for manifest building - it's slow for many files
                        // The receiver will compare by size first, then hash only if sizes match
                        Hash = string.Empty
                    });
                }
                catch { }
            }
        }).ConfigureAwait(false);

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

                // If remote hash is empty (skipped for performance), assume file is OK if size matches
                // This is a reasonable trade-off for games with many small files
                if (string.IsNullOrEmpty(remoteFile.Hash))
                {
                    // Size matches, no hash to compare - assume file is OK
                    continue;
                }

                var localHash = ComputeQuickHash(localFilePath, localInfo.Length);
                if (localHash != remoteFile.Hash)
                {
                    filesToDownload.Add(remoteFile);
                }
            }
        }).ConfigureAwait(false);

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
    /// Configures a TcpClient for optimal throughput
    /// </summary>
    private void ConfigureSocketForHighPerformance(TcpClient client)
    {
        client.SendBufferSize = _socketBufferSize;
        client.ReceiveBufferSize = _socketBufferSize;
        
        // For WiFi: enable Nagle to reduce packet overhead
        // For wired: disable Nagle for lower latency
        client.NoDelay = IsHighSpeedMode;
        
        client.LingerState = new LingerOption(true, 10);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        
        System.Diagnostics.Debug.WriteLine($"Socket configured: SendBuffer={client.SendBufferSize / 1024}KB, ReceiveBuffer={client.ReceiveBufferSize / 1024}KB, NoDelay={client.NoDelay}");
    }

    /// <summary>
    /// Creates a FileStream optimized for high-performance sequential reading
    /// </summary>
    private FileStream CreateOptimizedReadStream(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            _bufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
    }

    /// <summary>
    /// Creates a FileStream optimized for high-performance sequential writing
    /// </summary>
    private FileStream CreateOptimizedWriteStream(string path)
    {
        return new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            _bufferSize,
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
    public bool IncludeManifest { get; set; } = true;
}

public class FileManifest
{
    public string GamePath { get; set; } = string.Empty;
    public List<FileTransferInfo> Files { get; set; } = [];
    public string? AppManifestContent { get; set; }
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
    public TimeSpan EstimatedTimeRemaining { get; set; }
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
