using System.IO;
using System.Net;
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
                SenderId = LocalPeer.PeerId
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
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var requestLine = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(requestLine))
                    return;

                var request = JsonSerializer.Deserialize<NetworkMessage>(requestLine);
                if (request == null)
                    return;

                switch (request.Type)
                {
                    case MessageType.RequestGameList:
                        var response = new NetworkMessage
                        {
                            Type = MessageType.GameList,
                            SenderId = LocalPeer.PeerId,
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
                var message = $"{DiscoveryMessage}|{LocalPeer.PeerId}|{LocalPeer.DisplayName}|{LocalPeer.Port}";
                var bytes = Encoding.UTF8.GetBytes(message);

                // Broadcast to all network interfaces
                using var broadcastClient = new UdpClient();
                broadcastClient.EnableBroadcast = true;
                await broadcastClient.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, BroadcastPort));

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
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            var endPoint = socket.LocalEndPoint as IPEndPoint;
            return endPoint?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
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
    public List<GameInfo>? Games { get; set; }
    public string? GameAppId { get; set; }
}
