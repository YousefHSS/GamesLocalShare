using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

/// <summary>
/// Service for discovering other clients on the local network using UDP broadcast
/// </summary>
public class NetworkDiscoveryService : IDisposable
{
    private const int BroadcastPort = 45677;
    private const int TcpPort = 45678;
    private const string DiscoveryMessage = "GAMESYNC_DISCOVERY";
    private const string DiscoveryResponse = "GAMESYNC_RESPONSE";
    
    private UdpClient? _udpClient;
    private TcpListener? _tcpListener;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, NetworkPeer> _peers = new();
    private readonly object _peersLock = new();
    
    // JSON serializer options configured for .NET 8
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
    
    /// <summary>
    /// Our local peer information
    /// </summary>
    public NetworkPeer LocalPeer { get; private set; }

    /// <summary>
    /// The file transfer port we're advertising to peers (set by FileTransferService)
    /// </summary>
    public int LocalFileTransferPort { get; set; } = 45679;

    /// <summary>
    /// Event raised when a new peer is discovered
    /// </summary>
    public event EventHandler<NetworkPeer>? PeerDiscovered;

    /// <summary>
    /// Event raised when a peer goes offline
    /// </summary>
    public event EventHandler<NetworkPeer>? PeerLost;

    /// <summary>
    /// Event raised when peer's game list is updated
    /// </summary>
    public event EventHandler<NetworkPeer>? PeerGamesUpdated;

    /// <summary>
    /// Event raised during scanning progress
    /// </summary>
    public event EventHandler<string>? ScanProgress;

    /// <summary>
    /// Event raised for connection errors (for debugging)
    /// </summary>
    public event EventHandler<string>? ConnectionError;

    /// <summary>
    /// Event raised when games are requested but LocalPeer.Games is empty
    /// This allows the ViewModel to trigger a scan
    /// </summary>
    public event EventHandler? GamesRequestedButEmpty;

    public NetworkDiscoveryService()
    {
        LocalPeer = new NetworkPeer
        {
            DisplayName = Environment.MachineName,
            Port = TcpPort,
            IpAddress = GetLocalIPAddress()
        };
    }

    /// <summary>
    /// Starts the discovery service
    /// </summary>
    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();

        try
        {
            // Start UDP listener for discovery
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, BroadcastPort));
            _udpClient.EnableBroadcast = true;
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Failed to start UDP listener on port {BroadcastPort}: {ex.Message}");
            throw;
        }

        try
        {
            // Start TCP listener for game list exchange
            _tcpListener = new TcpListener(IPAddress.Any, TcpPort);
            _tcpListener.Start();
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Failed to start TCP listener on port {TcpPort}: {ex.Message}");
            throw;
        }

        // Start background tasks
        _ = ListenForDiscoveryAsync(_cts.Token);
        _ = ListenForTcpConnectionsAsync(_cts.Token);
        _ = BroadcastPresenceAsync(_cts.Token);
        _ = CleanupStalePeersAsync(_cts.Token);
        _ = KeepPeersAliveAsync(_cts.Token);  // NEW: Keep-alive task

        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops the discovery service
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _udpClient?.Close();
        _tcpListener?.Stop();
    }

    /// <summary>
    /// Gets all currently known peers
    /// </summary>
    public List<NetworkPeer> GetPeers()
    {
        lock (_peersLock)
        {
            return _peers.Values.Where(p => p.IsOnline).ToList();
        }
    }

    /// <summary>
    /// Manually scans the local network for peers by trying to connect to each IP
    /// </summary>
    public async Task<int> ScanNetworkAsync(CancellationToken ct = default)
    {
        var foundPeers = 0;
        var localIp = GetLocalIPAddress();
        
        // Get subnet base (e.g., 192.168.1.x)
        var ipParts = localIp.Split('.');
        if (ipParts.Length != 4)
        {
            ScanProgress?.Invoke(this, "Invalid IP address format");
            return 0;
        }

        var subnetBase = $"{ipParts[0]}.{ipParts[1]}.{ipParts[2]}";
        ScanProgress?.Invoke(this, $"Scanning subnet {subnetBase}.0/24...");

        // Also send UDP broadcasts to all network interfaces
        await SendBroadcastToAllInterfacesAsync();

        // Scan common IP ranges in parallel
        var tasks = new List<Task<NetworkPeer?>>();
        
        for (int i = 1; i <= 254; i++)
        {
            var ip = $"{subnetBase}.{i}";
            
            // Skip our own IP
            if (ip == localIp)
                continue;

            tasks.Add(TryConnectToPeerAsync(ip, ct));
            
            // Limit concurrent connections
            if (tasks.Count >= 50)
            {
                var completed = await Task.WhenAny(tasks);
                tasks.Remove(completed);
                
                var peer = await completed;
                if (peer != null)
                {
                    foundPeers++;
                    ScanProgress?.Invoke(this, $"Found peer: {peer.DisplayName} ({peer.IpAddress})");
                }
            }
        }

        // Wait for remaining tasks
        var remainingResults = await Task.WhenAll(tasks);
        foreach (var peer in remainingResults.Where(p => p != null))
        {
            foundPeers++;
            ScanProgress?.Invoke(this, $"Found peer: {peer!.DisplayName} ({peer.IpAddress})");
        }

        ScanProgress?.Invoke(this, $"Scan complete. Found {foundPeers} peer(s).");
        return foundPeers;
    }

    /// <summary>
    /// Tries to connect to a specific IP address to check if it's running our app
    /// </summary>
    public async Task<NetworkPeer?> TryConnectToPeerAsync(string ipAddress, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            
            // Set a timeout for connection attempts
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(3000);
            
            try
            {
                await client.ConnectAsync(ipAddress, TcpPort, connectCts.Token);
            }
            catch (OperationCanceledException)
            {
                return null; // Connection timed out
            }
            
            if (!client.Connected)
            {
                return null;
            }

            // Connected! Now request peer info
            using var stream = client.GetStream();
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 5000;
            
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Send discovery request - also include OUR games so they know about us
            var request = new NetworkMessage
            {
                Type = MessageType.RequestGameList,
                SenderId = LocalPeer.PeerId,
                SenderName = LocalPeer.DisplayName,
                SenderPort = LocalPeer.Port,
                SenderFileTransferPort = LocalFileTransferPort,
                Games = LocalPeer.Games // Include our games in the request!
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));

            // Read response with timeout
            using var responseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            responseCts.CancelAfter(5000);
            
            var responseLine = await reader.ReadLineAsync(responseCts.Token);
            if (!string.IsNullOrEmpty(responseLine))
            {
                var response = JsonSerializer.Deserialize<NetworkMessage>(responseLine, JsonOptions);
                if (response != null)
                {
                    System.Diagnostics.Debug.WriteLine($"Received response from {ipAddress}: {response.Type} with {response.Games?.Count ?? 0} games");

                    var peer = new NetworkPeer
                    {
                        PeerId = response.SenderId,
                        DisplayName = response.SenderName ?? ipAddress,
                        IpAddress = ipAddress,
                        Port = response.SenderPort > 0 ? response.SenderPort : TcpPort,
                        FileTransferPort = response.SenderFileTransferPort > 0 ? response.SenderFileTransferPort : 45679,
                        Games = new ObservableCollection<GameInfo>(response.Games ?? []),
                        LastSeen = DateTime.Now
                    };

                    // Add to peers dictionary
                    lock (_peersLock)
                    {
                        if (!_peers.ContainsKey(peer.PeerId))
                        {
                            _peers[peer.PeerId] = peer;
                            PeerDiscovered?.Invoke(this, peer);
                            
                            // Also notify about games if we got them
                            if (peer.Games.Count > 0)
                            {
                                PeerGamesUpdated?.Invoke(this, peer);
                            }
                        }
                        else
                        {
                            _peers[peer.PeerId].LastSeen = DateTime.Now;
                            _peers[peer.PeerId].Games = new ObservableCollection<GameInfo>(peer.Games);
                            
                            if (peer.Games.Count > 0)
                            {
                                PeerGamesUpdated?.Invoke(this, _peers[peer.PeerId]);
                            }
                        }
                    }

                    return peer;
                }
            }
        }
        catch (SocketException ex)
        {
            ConnectionError?.Invoke(this, $"Socket error connecting to {ipAddress}: {ex.Message}");
        }
        catch (IOException ex)
        {
            ConnectionError?.Invoke(this, $"IO error connecting to {ipAddress}: {ex.Message}");
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Error connecting to {ipAddress}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Connect to a specific peer by IP address
    /// </summary>
    public async Task<bool> ConnectToPeerByIpAsync(string ipAddress)
    {
        ScanProgress?.Invoke(this, $"Attempting to connect to {ipAddress}...");
        var peer = await TryConnectToPeerAsync(ipAddress);
        
        if (peer != null)
        {
            ScanProgress?.Invoke(this, $"Connected to {peer.DisplayName} with {peer.Games.Count} games");
            return true;
        }
        
        // Also try requesting game list separately in case the peer exists but returned empty games
        var existingPeer = GetPeers().FirstOrDefault(p => p.IpAddress == ipAddress);
        if (existingPeer != null)
        {
            await RequestGameListAsync(existingPeer);
            return true;
        }
        
        return false;
    }

    /// <summary>
    /// Sends UDP broadcast to all network interfaces
    /// </summary>
    private async Task SendBroadcastToAllInterfacesAsync()
    {
        var message = $"{DiscoveryMessage}|{LocalPeer.PeerId}|{LocalPeer.DisplayName}|{LocalPeer.Port}";
        var bytes = Encoding.UTF8.GetBytes(message);

        try
        {
            // Get all network interfaces
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                            ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var ni in interfaces)
            {
                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        try
                        {
                            // Calculate broadcast address for this interface
                            var broadcastAddr = GetBroadcastAddress(addr.Address, addr.IPv4Mask);
                            
                            using var client = new UdpClient();
                            client.EnableBroadcast = true;
                            await client.SendAsync(bytes, bytes.Length, new IPEndPoint(broadcastAddr, BroadcastPort));
                            
                            // Also send to 255.255.255.255
                            await client.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, BroadcastPort));
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Broadcast error on {ni.Name}: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting network interfaces: {ex.Message}");
        }
    }

    /// <summary>
    /// Calculates the broadcast address for a given IP and subnet mask
    /// </summary>
    private static IPAddress GetBroadcastAddress(IPAddress address, IPAddress subnetMask)
    {
        var ipBytes = address.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        var broadcastBytes = new byte[ipBytes.Length];
        
        for (int i = 0; i < broadcastBytes.Length; i++)
        {
            broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
        }
        
        return new IPAddress(broadcastBytes);
    }

    /// <summary>
    /// Updates our local game list and broadcasts to peers
    /// </summary>
    public async Task UpdateLocalGamesAsync(List<GameInfo> games)
    {
        LocalPeer.Games = new ObservableCollection<GameInfo>(games);
        
        System.Diagnostics.Debug.WriteLine($"LocalPeer.Games updated with {games.Count} games");
        
        // Notify all peers of our updated game list
        foreach (var peer in GetPeers())
        {
            await SendGameListToPeerAsync(peer);
        }
    }

    /// <summary>
    /// Requests game list from a specific peer
    /// </summary>
    public async Task RequestGameListAsync(NetworkPeer peer)
    {
        try
        {
            ScanProgress?.Invoke(this, $"Requesting game list from {peer.DisplayName}...");
            
            using var client = new TcpClient();
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 5000;
            
            using var cts = new CancellationTokenSource(10000);
            await client.ConnectAsync(peer.IpAddress, peer.Port, cts.Token);
            
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Send request - include our games so they can update their view of us
            var request = new NetworkMessage
            {
                Type = MessageType.RequestGameList,
                SenderId = LocalPeer.PeerId,
                SenderName = LocalPeer.DisplayName,
                SenderPort = LocalPeer.Port,
                SenderFileTransferPort = LocalFileTransferPort,
                Games = LocalPeer.Games // Include our games!
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));

            // Read response
            var responseLine = await reader.ReadLineAsync(cts.Token);
            if (!string.IsNullOrEmpty(responseLine))
            {
                var response = JsonSerializer.Deserialize<NetworkMessage>(responseLine, JsonOptions);
                if (response != null)
                {
                    peer.Games = response.Games ?? [];
                    peer.LastSeen = DateTime.Now;
                    // Update file transfer port if provided
                    if (response.SenderFileTransferPort > 0)
                    {
                        peer.FileTransferPort = response.SenderFileTransferPort;
                        System.Diagnostics.Debug.WriteLine($"Updated {peer.DisplayName}'s FileTransferPort to {peer.FileTransferPort}");
                    }
                    
                    ScanProgress?.Invoke(this, $"Received {peer.Games.Count} games from {peer.DisplayName}");
                    PeerGamesUpdated?.Invoke(this, peer);
                }
            }
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Error requesting game list from {peer.DisplayName}: {ex.Message}");
            ScanProgress?.Invoke(this, $"Failed to get games from {peer.DisplayName}: {ex.Message}");
        }
    }

    private async Task ListenForDiscoveryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient!.ReceiveAsync(ct);
                var message = Encoding.UTF8.GetString(result.Buffer);

                if (message.StartsWith(DiscoveryMessage))
                {
                    var parts = message.Split('|');
                    if (parts.Length >= 4)
                    {
                        var peerId = parts[1];
                        var peerName = parts[2];
                        var peerPort = int.Parse(parts[3]);

                        // Don't add ourselves
                        if (peerId == LocalPeer.PeerId)
                            continue;

                        var peerIp = result.RemoteEndPoint.Address.ToString();

                        bool isNew = false;
                        NetworkPeer? newPeer = null;
                        
                        lock (_peersLock)
                        {
                            if (!_peers.TryGetValue(peerId, out var existingPeer))
                            {
                                newPeer = new NetworkPeer
                                {
                                    PeerId = peerId,
                                    DisplayName = peerName,
                                    IpAddress = peerIp,
                                    Port = peerPort,
                                    LastSeen = DateTime.Now
                                };
                                _peers[peerId] = newPeer;
                                isNew = true;
                            }
                            else
                            {
                                existingPeer.LastSeen = DateTime.Now;
                                existingPeer.IpAddress = peerIp;
                            }
                        }

                        if (isNew && newPeer != null)
                        {
                            PeerDiscovered?.Invoke(this, newPeer);
                            // Request their game list
                            _ = RequestGameListAsync(newPeer);
                        }

                        // Send a response back so they know we exist
                        await SendDiscoveryResponseAsync(result.RemoteEndPoint);
                    }
                }
                else if (message.StartsWith(DiscoveryResponse))
                {
                    // Handle response from another peer
                    var parts = message.Split('|');
                    if (parts.Length >= 4)
                    {
                        var peerId = parts[1];
                        var peerName = parts[2];
                        var peerPort = int.Parse(parts[3]);

                        if (peerId == LocalPeer.PeerId)
                            continue;

                        var peerIp = result.RemoteEndPoint.Address.ToString();

                        bool isNew = false;
                        NetworkPeer? newPeer = null;
                        
                        lock (_peersLock)
                        {
                            if (!_peers.TryGetValue(peerId, out var existingPeer))
                            {
                                newPeer = new NetworkPeer
                                {
                                    PeerId = peerId,
                                    DisplayName = peerName,
                                    IpAddress = peerIp,
                                    Port = peerPort,
                                    LastSeen = DateTime.Now
                                };
                                _peers[peerId] = newPeer;
                                isNew = true;
                            }
                            else
                            {
                                existingPeer.LastSeen = DateTime.Now;
                            }
                        }

                        if (isNew && newPeer != null)
                        {
                            PeerDiscovered?.Invoke(this, newPeer);
                            _ = RequestGameListAsync(newPeer);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Discovery error: {ex.Message}");
            }
        }
    }

    private async Task SendDiscoveryResponseAsync(IPEndPoint remoteEndPoint)
    {
        try
        {
            var message = $"{DiscoveryResponse}|{LocalPeer.PeerId}|{LocalPeer.DisplayName}|{LocalPeer.Port}";
            var bytes = Encoding.UTF8.GetBytes(message);
            
            using var client = new UdpClient();
            await client.SendAsync(bytes, bytes.Length, remoteEndPoint);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error sending discovery response: {ex.Message}");
        }
    }

    private async Task ListenForTcpConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpListener!.AcceptTcpClientAsync(ct);
                _ = HandleTcpConnectionAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TCP listener error: {ex.Message}");
            }
        }
    }

    private async Task HandleTcpConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                
                var remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var requestLine = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(requestLine))
                    return;

                var request = JsonSerializer.Deserialize<NetworkMessage>(requestLine, JsonOptions);
                if (request == null)
                    return;

                System.Diagnostics.Debug.WriteLine($"Received {request.Type} from {request.SenderName} ({remoteIp}) with {request.Games?.Count ?? 0} games");

                // If we received a request, add/update the sender as a peer
                bool shouldNotifyGamesUpdated = false;
                NetworkPeer? peerToNotify = null;
                
                if (!string.IsNullOrEmpty(request.SenderId) && request.SenderId != LocalPeer.PeerId)
                {
                    bool isNew = false;
                    
                    lock (_peersLock)
                    {
                        if (!_peers.ContainsKey(request.SenderId))
                        {
                            peerToNotify = new NetworkPeer
                            {
                                PeerId = request.SenderId,
                                DisplayName = request.SenderName ?? remoteIp,
                                IpAddress = remoteIp,
                                Port = request.SenderPort > 0 ? request.SenderPort : TcpPort,
                                FileTransferPort = request.SenderFileTransferPort > 0 ? request.SenderFileTransferPort : 45679,
                                Games = request.Games ?? [], // Capture their games from the request!
                                LastSeen = DateTime.Now
                            };
                            _peers[request.SenderId] = peerToNotify;
                            isNew = true;
                            shouldNotifyGamesUpdated = request.Games != null && request.Games.Count > 0;
                        }
                        else
                        {
                            peerToNotify = _peers[request.SenderId];
                            peerToNotify.LastSeen = DateTime.Now;
                            peerToNotify.IpAddress = remoteIp; // Update IP in case it changed
                            
                            // Update file transfer port if provided
                            if (request.SenderFileTransferPort > 0)
                            {
                                peerToNotify.FileTransferPort = request.SenderFileTransferPort;
                            }
                            
                            // Update their games if they sent them
                            if (request.Games != null && request.Games.Count > 0)
                            {
                                peerToNotify.Games = new ObservableCollection<GameInfo>(request.Games);
                                shouldNotifyGamesUpdated = true;
                            }
                        }
                    }
                    
                    // Fire events outside the lock to prevent deadlocks
                    if (isNew && peerToNotify != null)
                    {
                        PeerDiscovered?.Invoke(this, peerToNotify);
                    }
                    
                    // If they sent games with the request, notify about the update
                    if (shouldNotifyGamesUpdated && peerToNotify != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"Notifying PeerGamesUpdated for {peerToNotify.DisplayName} with {peerToNotify.Games.Count} games");
                        PeerGamesUpdated?.Invoke(this, peerToNotify);
                    }
                }

                switch (request.Type)
                {
                    case MessageType.RequestGameList:
                        // Log what we're about to send
                        System.Diagnostics.Debug.WriteLine($"Responding with {LocalPeer.Games.Count} games to {request.SenderName}");
                        
                        // If we have no games, raise an event so the UI can notify the user
                        if (LocalPeer.Games.Count == 0)
                        {
                            GamesRequestedButEmpty?.Invoke(this, EventArgs.Empty);
                        }
                        
                        var response = new NetworkMessage
                        {
                            Type = MessageType.GameList,
                            SenderId = LocalPeer.PeerId,
                            SenderName = LocalPeer.DisplayName,
                            SenderPort = LocalPeer.Port,
                            SenderFileTransferPort = LocalFileTransferPort,
                            Games = LocalPeer.Games
                        };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
                        break;

                    case MessageType.GameList:
                        System.Diagnostics.Debug.WriteLine($"Received GameList from {request.SenderName} with {request.Games?.Count ?? 0} games");
                        if (request.Games != null)
                        {
                            NetworkPeer? gameListPeer = null;
                            lock (_peersLock)
                            {
                                if (_peers.TryGetValue(request.SenderId, out var peer))
                                {
                                    peer.Games = request.Games;
                                    peer.LastSeen = DateTime.Now;
                                    // Update file transfer port if provided
                                    if (request.SenderFileTransferPort > 0)
                                    {
                                        peer.FileTransferPort = request.SenderFileTransferPort;
                                    }
                                    gameListPeer = peer;
                                }
                            }
                            // Invoke event outside of lock to prevent deadlocks
                            if (gameListPeer != null)
                            {
                                PeerGamesUpdated?.Invoke(this, gameListPeer);
                            }
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TCP connection error: {ex.Message}");
        }
    }

    private async Task BroadcastPresenceAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SendBroadcastToAllInterfacesAsync();
                await Task.Delay(5000, ct); // Broadcast every 5 seconds
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Broadcast error: {ex.Message}");
            }
        }
    }

    private async Task CleanupStalePeersAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(30000, ct); // Check every 30 seconds (was 15)

                List<NetworkPeer> stalePeers;
                lock (_peersLock)
                {
                    stalePeers = _peers.Values.Where(p => !p.IsOnline).ToList();
                    foreach (var peer in stalePeers)
                    {
                        System.Diagnostics.Debug.WriteLine($"Removing stale peer: {peer.DisplayName} (last seen {(DateTime.Now - peer.LastSeen).TotalSeconds:F0}s ago)");
                        _peers.Remove(peer.PeerId);
                    }
                }

                foreach (var peer in stalePeers)
                {
                    PeerLost?.Invoke(this, peer);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Periodically pings known peers to keep them alive and verify they're still reachable
    /// </summary>
    private async Task KeepPeersAliveAsync(CancellationToken ct)
    {
        // Wait a bit before starting keep-alive
        await Task.Delay(10000, ct);
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var peers = GetPeers();
                
                foreach (var peer in peers)
                {
                    // If we haven't heard from this peer in 30 seconds, try to ping them
                    if ((DateTime.Now - peer.LastSeen).TotalSeconds > 30)
                    {
                        System.Diagnostics.Debug.WriteLine($"Keep-alive: Pinging {peer.DisplayName}...");
                        
                        // Try a quick TCP connection to verify they're still there
                        var stillAlive = await PingPeerAsync(peer);
                        
                        if (stillAlive)
                        {
                            peer.MarkAsSeen();
                            System.Diagnostics.Debug.WriteLine($"Keep-alive: {peer.DisplayName} is still alive");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"Keep-alive: {peer.DisplayName} did not respond");
                        }
                    }
                }
                
                await Task.Delay(20000, ct); // Check every 20 seconds
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Keep-alive error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Quick ping to check if a peer is still reachable
    /// </summary>
    private async Task<bool> PingPeerAsync(NetworkPeer peer)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(3000);
            
            await client.ConnectAsync(peer.IpAddress, peer.Port, cts.Token);
            
            if (!client.Connected)
                return false;

            using var stream = client.GetStream();
            stream.ReadTimeout = 3000;
            stream.WriteTimeout = 3000;
            
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Send a game list request (this also updates our games on their side)
            var request = new NetworkMessage
            {
                Type = MessageType.RequestGameList,
                SenderId = LocalPeer.PeerId,
                SenderName = LocalPeer.DisplayName,
                SenderPort = LocalPeer.Port,
                SenderFileTransferPort = LocalFileTransferPort,
                Games = LocalPeer.Games
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));

            // Wait for response
            var responseLine = await reader.ReadLineAsync(cts.Token);
            if (!string.IsNullOrEmpty(responseLine))
            {
                var response = JsonSerializer.Deserialize<NetworkMessage>(responseLine, JsonOptions);
                if (response != null)
                {
                    // Update their games if they changed
                    if (response.Games != null)
                    {
                        peer.Games = new ObservableCollection<GameInfo>(response.Games);
                        // Update file transfer port if provided
                        if (response.SenderFileTransferPort > 0)
                        {
                            peer.FileTransferPort = response.SenderFileTransferPort;
                        }
                        PeerGamesUpdated?.Invoke(this, peer);
                    }
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Ping failed for {peer.DisplayName}: {ex.Message}");
        }

        return false;
    }

    private async Task SendGameListToPeerAsync(NetworkPeer peer)
    {
        try
        {
            using var client = new TcpClient();
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            
            using var cts = new CancellationTokenSource(5000);
            await client.ConnectAsync(peer.IpAddress, peer.Port, cts.Token);
            
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            var message = new NetworkMessage
            {
                Type = MessageType.GameList,
                SenderId = LocalPeer.PeerId,
                SenderName = LocalPeer.DisplayName,
                SenderPort = LocalPeer.Port,
                SenderFileTransferPort = LocalFileTransferPort,
                Games = LocalPeer.Games
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(message, JsonOptions));
            
            System.Diagnostics.Debug.WriteLine($"Sent {LocalPeer.Games.Count} games to {peer.DisplayName}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error sending game list to {peer.DisplayName}: {ex.Message}");
        }
    }

    private static string GetLocalIPAddress()
    {
        try
        {
            // Try to get the IP from a connected socket
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            var endPoint = socket.LocalEndPoint as IPEndPoint;
            return endPoint?.Address.ToString() ?? GetFallbackIP();
        }
        catch
        {
            return GetFallbackIP();
        }
    }

    private static string GetFallbackIP()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    return ip.ToString();
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _udpClient?.Dispose();
    }
}


/// <summary>
    /// Message types for network communication
    /// </summary>
    public enum MessageType
    {
        RequestGameList,
        GameList,
        RequestFileTransfer,
        FileTransferResponse
    }

    /// <summary>
    /// Network message structure
    /// </summary>
    public class NetworkMessage
    {
        public MessageType Type { get; set; }
        public string SenderId { get; set; } = string.Empty;
        public string? SenderName { get; set; }
        public int SenderPort { get; set; }
        /// <summary>
        /// The port the sender is listening on for file transfers (may differ from default 45679)
        /// </summary>
        public int SenderFileTransferPort { get; set; } = 45679;
        public ObservableCollection<GameInfo>? Games { get; set; }
        public string? GameAppId { get; set; }
    }
