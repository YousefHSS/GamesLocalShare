using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
    
    /// <summary>
    /// Our local peer information
    /// </summary>
    public NetworkPeer LocalPeer { get; private set; }

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

        // Start UDP listener for discovery
        _udpClient = new UdpClient();
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, BroadcastPort));
        _udpClient.EnableBroadcast = true;

        // Start TCP listener for game list exchange
        _tcpListener = new TcpListener(IPAddress.Any, TcpPort);
        _tcpListener.Start();

        // Start background tasks
        _ = ListenForDiscoveryAsync(_cts.Token);
        _ = ListenForTcpConnectionsAsync(_cts.Token);
        _ = BroadcastPresenceAsync(_cts.Token);
        _ = CleanupStalePeersAsync(_cts.Token);

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
            
            // Set a short timeout for connection attempts
            var connectTask = client.ConnectAsync(ipAddress, TcpPort, ct).AsTask();
            var timeoutTask = Task.Delay(1000, ct);
            
            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            
            if (completedTask == timeoutTask || !client.Connected)
            {
                return null;
            }

            // Connected! Now request peer info
            using var stream = client.GetStream();
            stream.ReadTimeout = 2000;
            stream.WriteTimeout = 2000;
            
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Send discovery request
            var request = new NetworkMessage
            {
                Type = MessageType.RequestGameList,
                SenderId = LocalPeer.PeerId,
                SenderName = LocalPeer.DisplayName,
                SenderPort = LocalPeer.Port
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request));

            // Read response with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2000);
            
            var responseLine = await reader.ReadLineAsync(cts.Token);
            if (!string.IsNullOrEmpty(responseLine))
            {
                var response = JsonSerializer.Deserialize<NetworkMessage>(responseLine);
                if (response != null)
                {
                    var peer = new NetworkPeer
                    {
                        PeerId = response.SenderId,
                        DisplayName = response.SenderName ?? ipAddress,
                        IpAddress = ipAddress,
                        Port = response.SenderPort > 0 ? response.SenderPort : TcpPort,
                        Games = response.Games ?? [],
                        LastSeen = DateTime.Now
                    };

                    // Add to peers dictionary
                    lock (_peersLock)
                    {
                        if (!_peers.ContainsKey(peer.PeerId))
                        {
                            _peers[peer.PeerId] = peer;
                            PeerDiscovered?.Invoke(this, peer);
                        }
                        else
                        {
                            _peers[peer.PeerId].LastSeen = DateTime.Now;
                            _peers[peer.PeerId].Games = peer.Games;
                        }
                    }

                    return peer;
                }
            }
        }
        catch
        {
            // Connection failed - this IP is not running our app
        }

        return null;
    }

    /// <summary>
    /// Connect to a specific peer by IP address
    /// </summary>
    public async Task<bool> ConnectToPeerByIpAsync(string ipAddress)
    {
        var peer = await TryConnectToPeerAsync(ipAddress);
        return peer != null;
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
        LocalPeer.Games = games;
        
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
            using var client = new TcpClient();
            await client.ConnectAsync(peer.IpAddress, peer.Port);
            
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Send request
            var request = new NetworkMessage
            {
                Type = MessageType.RequestGameList,
                SenderId = LocalPeer.PeerId,
                SenderName = LocalPeer.DisplayName,
                SenderPort = LocalPeer.Port
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request));

            // Read response
            var responseLine = await reader.ReadLineAsync();
            if (!string.IsNullOrEmpty(responseLine))
            {
                var response = JsonSerializer.Deserialize<NetworkMessage>(responseLine);
                if (response?.Type == MessageType.GameList && response.Games != null)
                {
                    peer.Games = response.Games;
                    peer.LastSeen = DateTime.Now;
                    PeerGamesUpdated?.Invoke(this, peer);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error requesting game list from {peer.DisplayName}: {ex.Message}");
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

                        lock (_peersLock)
                        {
                            if (!_peers.TryGetValue(peerId, out var existingPeer))
                            {
                                var newPeer = new NetworkPeer
                                {
                                    PeerId = peerId,
                                    DisplayName = peerName,
                                    IpAddress = peerIp,
                                    Port = peerPort,
                                    LastSeen = DateTime.Now
                                };
                                _peers[peerId] = newPeer;
                                PeerDiscovered?.Invoke(this, newPeer);

                                // Request their game list
                                _ = RequestGameListAsync(newPeer);
                            }
                            else
                            {
                                existingPeer.LastSeen = DateTime.Now;
                                existingPeer.IpAddress = peerIp;
                            }
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

                        lock (_peersLock)
                        {
                            if (!_peers.TryGetValue(peerId, out var existingPeer))
                            {
                                var newPeer = new NetworkPeer
                                {
                                    PeerId = peerId,
                                    DisplayName = peerName,
                                    IpAddress = peerIp,
                                    Port = peerPort,
                                    LastSeen = DateTime.Now
                                };
                                _peers[peerId] = newPeer;
                                PeerDiscovered?.Invoke(this, newPeer);

                                _ = RequestGameListAsync(newPeer);
                            }
                            else
                            {
                                existingPeer.LastSeen = DateTime.Now;
                            }
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
                var remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var requestLine = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(requestLine))
                    return;

                var request = JsonSerializer.Deserialize<NetworkMessage>(requestLine);
                if (request == null)
                    return;

                // If we received a request, add the sender as a peer
                if (!string.IsNullOrEmpty(request.SenderId) && request.SenderId != LocalPeer.PeerId)
                {
                    lock (_peersLock)
                    {
                        if (!_peers.ContainsKey(request.SenderId))
                        {
                            var newPeer = new NetworkPeer
                            {
                                PeerId = request.SenderId,
                                DisplayName = request.SenderName ?? remoteIp,
                                IpAddress = remoteIp,
                                Port = request.SenderPort > 0 ? request.SenderPort : TcpPort,
                                LastSeen = DateTime.Now
                            };
                            _peers[request.SenderId] = newPeer;
                            PeerDiscovered?.Invoke(this, newPeer);
                        }
                        else
                        {
                            _peers[request.SenderId].LastSeen = DateTime.Now;
                        }
                    }
                }

                switch (request.Type)
                {
                    case MessageType.RequestGameList:
                        var response = new NetworkMessage
                        {
                            Type = MessageType.GameList,
                            SenderId = LocalPeer.PeerId,
                            SenderName = LocalPeer.DisplayName,
                            SenderPort = LocalPeer.Port,
                            Games = LocalPeer.Games
                        };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response));
                        break;

                    case MessageType.GameList:
                        if (request.Games != null)
                        {
                            lock (_peersLock)
                            {
                                if (_peers.TryGetValue(request.SenderId, out var peer))
                                {
                                    peer.Games = request.Games;
                                    peer.LastSeen = DateTime.Now;
                                    PeerGamesUpdated?.Invoke(this, peer);
                                }
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
                await Task.Delay(10000, ct); // Check every 10 seconds

                List<NetworkPeer> stalePeers;
                lock (_peersLock)
                {
                    stalePeers = _peers.Values.Where(p => !p.IsOnline).ToList();
                    foreach (var peer in stalePeers)
                    {
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

    private async Task SendGameListToPeerAsync(NetworkPeer peer)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(peer.IpAddress, peer.Port);
            
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            var message = new NetworkMessage
            {
                Type = MessageType.GameList,
                SenderId = LocalPeer.PeerId,
                SenderName = LocalPeer.DisplayName,
                SenderPort = LocalPeer.Port,
                Games = LocalPeer.Games
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(message));
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
    public List<GameInfo>? Games { get; set; }
    public string? GameAppId { get; set; }
}
