namespace GamesLocalShare.Models;

/// <summary>
/// Represents a peer on the local network running the application
/// </summary>
public class NetworkPeer
{
    /// <summary>
    /// Unique identifier for this peer
    /// </summary>
    public string PeerId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Display name of the peer (computer name)
    /// </summary>
    public string DisplayName { get; set; } = Environment.MachineName;

    /// <summary>
    /// IP Address of the peer
    /// </summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>
    /// Port the peer is listening on for file transfers
    /// </summary>
    public int Port { get; set; } = 45678;

    /// <summary>
    /// List of games available on this peer
    /// </summary>
    public List<GameInfo> Games { get; set; } = [];

    /// <summary>
    /// Last time we heard from this peer
    /// </summary>
    public DateTime LastSeen { get; set; } = DateTime.Now;

    /// <summary>
    /// Whether this peer is currently online
    /// </summary>
    public bool IsOnline => (DateTime.Now - LastSeen).TotalSeconds < 30;
}
