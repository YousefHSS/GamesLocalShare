using System.Collections.ObjectModel;
using System.ComponentModel;

namespace GamesLocalShare.Models;

/// <summary>
/// Represents a peer on the local network running the application
/// </summary>
public class NetworkPeer : INotifyPropertyChanged
{
    private ObservableCollection<GameInfo> _games = [];

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
    /// Port the peer is listening on for game list exchange (TCP 45678)
    /// </summary>
    public int Port { get; set; } = 45678;

    /// <summary>
    /// Port the peer is listening on for file transfers (default TCP 45679)
    /// This can be different from the default if the primary port was unavailable
    /// </summary>
    public int FileTransferPort { get; set; } = 45679;

    /// <summary>
    /// List of games available on this peer
    /// </summary>
    public ObservableCollection<GameInfo> Games
    {
        get => _games;
        set
        {
            if (_games != value)
            {
                _games = value;
                OnPropertyChanged(nameof(Games));
            }
        }
    }

    /// <summary>
    /// Last time we heard from this peer
    /// </summary>
    public DateTime LastSeen { get; set; } = DateTime.Now;

    /// <summary>
    /// Whether this peer is currently online (within last 2 minutes)
    /// </summary>
    public bool IsOnline => (DateTime.Now - LastSeen).TotalSeconds < 120;

    /// <summary>
    /// Updates the LastSeen timestamp to now
    /// </summary>
    public void MarkAsSeen()
    {
        LastSeen = DateTime.Now;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
