using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GamesLocalShare.Models;
using GamesLocalShare.Services;

namespace GamesLocalShare.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SteamLibraryScanner _steamScanner;
    private readonly NetworkDiscoveryService _networkService;
    private readonly FileTransferService _fileTransferService;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isNetworkActive;

    [ObservableProperty]
    private bool _isScanningPeers;

    [ObservableProperty]
    private string _localIpAddress = string.Empty;

    [ObservableProperty]
    private string _manualPeerIp = string.Empty;

    [ObservableProperty]
    private GameInfo? _selectedLocalGame;

    [ObservableProperty]
    private NetworkPeer? _selectedPeer;

    [ObservableProperty]
    private GameSyncInfo? _selectedSyncItem;

    [ObservableProperty]
    private double _currentTransferProgress;

    [ObservableProperty]
    private string _currentTransferSpeed = string.Empty;

    [ObservableProperty]
    private string _currentTransferFile = string.Empty;

    [ObservableProperty]
    private bool _isTransferring;

    public ObservableCollection<GameInfo> LocalGames { get; } = [];
    public ObservableCollection<NetworkPeer> NetworkPeers { get; } = [];
    public ObservableCollection<GameSyncInfo> AvailableSyncs { get; } = [];

    public MainViewModel()
    {
        _steamScanner = new SteamLibraryScanner();
        _networkService = new NetworkDiscoveryService();
        _fileTransferService = new FileTransferService();

        // Subscribe to network events
        _networkService.PeerDiscovered += OnPeerDiscovered;
        _networkService.PeerLost += OnPeerLost;
        _networkService.PeerGamesUpdated += OnPeerGamesUpdated;
        _networkService.ScanProgress += OnScanProgress;

        // Subscribe to transfer events
        _fileTransferService.ProgressChanged += OnTransferProgress;
        _fileTransferService.TransferCompleted += OnTransferCompleted;

        LocalIpAddress = _networkService.LocalPeer.IpAddress;
    }

    [RelayCommand]
    private async Task ScanLocalGamesAsync()
    {
        try
        {
            IsScanning = true;
            StatusMessage = "Scanning Steam library...";

            var games = await _steamScanner.ScanGamesAsync();

            Application.Current.Dispatcher.Invoke(() =>
            {
                LocalGames.Clear();
                foreach (var game in games)
                {
                    LocalGames.Add(game);
                }
            });

            StatusMessage = $"Found {games.Count} installed games";

            // Update network peers with our game list
            if (IsNetworkActive)
            {
                await _networkService.UpdateLocalGamesAsync(games);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error scanning: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task StartNetworkAsync()
    {
        try
        {
            StatusMessage = "Starting network discovery...";
            
            await _networkService.StartAsync();
            await _fileTransferService.StartListeningAsync();
            
            IsNetworkActive = true;
            StatusMessage = "Network discovery active - Looking for peers...";

            // Share our games with the network
            if (LocalGames.Count > 0)
            {
                await _networkService.UpdateLocalGamesAsync(LocalGames.ToList());
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Network error: {ex.Message}";
            IsNetworkActive = false;
        }
    }

    [RelayCommand]
    private void StopNetwork()
    {
        _networkService.Stop();
        _fileTransferService.Stop();
        
        NetworkPeers.Clear();
        AvailableSyncs.Clear();
        
        IsNetworkActive = false;
        StatusMessage = "Network discovery stopped";
    }

    [RelayCommand]
    private async Task ScanForPeersAsync()
    {
        if (!IsNetworkActive)
        {
            StatusMessage = "Please start the network first";
            return;
        }

        if (IsScanningPeers)
            return;

        try
        {
            IsScanningPeers = true;
            StatusMessage = "Scanning local network for peers...";

            var foundCount = await _networkService.ScanNetworkAsync();
            
            StatusMessage = foundCount > 0 
                ? $"Scan complete. Found {foundCount} peer(s)." 
                : "Scan complete. No peers found. Make sure the app is running on other computers.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsScanningPeers = false;
        }
    }

    [RelayCommand]
    private async Task ConnectToManualIpAsync()
    {
        if (string.IsNullOrWhiteSpace(ManualPeerIp))
        {
            StatusMessage = "Please enter an IP address";
            return;
        }

        if (!IsNetworkActive)
        {
            StatusMessage = "Please start the network first";
            return;
        }

        try
        {
            StatusMessage = $"Connecting to {ManualPeerIp}...";
            
            var success = await _networkService.ConnectToPeerByIpAsync(ManualPeerIp.Trim());
            
            if (success)
            {
                StatusMessage = $"Connected to peer at {ManualPeerIp}";
                ManualPeerIp = string.Empty;
            }
            else
            {
                StatusMessage = $"Could not connect to {ManualPeerIp}. Make sure the app is running on that computer.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connection error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshPeerGamesAsync()
    {
        if (SelectedPeer == null)
            return;

        StatusMessage = $"Requesting game list from {SelectedPeer.DisplayName}...";
        await _networkService.RequestGameListAsync(SelectedPeer);
    }

    [RelayCommand]
    private async Task StartSyncAsync()
    {
        if (SelectedSyncItem == null || IsTransferring)
            return;

        try
        {
            IsTransferring = true;
            SelectedSyncItem.Status = SyncStatus.Syncing;
            StatusMessage = $"Syncing {SelectedSyncItem.LocalGame.Name}...";

            var success = await _fileTransferService.RequestGameTransferAsync(
                SelectedSyncItem.RemotePeer,
                SelectedSyncItem.RemoteGame,
                SelectedSyncItem.LocalGame);

            SelectedSyncItem.Status = success ? SyncStatus.Completed : SyncStatus.Failed;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sync error: {ex.Message}";
            SelectedSyncItem.Status = SyncStatus.Failed;
        }
        finally
        {
            IsTransferring = false;
        }
    }

    [RelayCommand]
    private void CancelTransfer()
    {
        // TODO: Implement cancellation
        StatusMessage = "Transfer cancelled";
        IsTransferring = false;
    }

    private void OnPeerDiscovered(object? sender, NetworkPeer peer)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!NetworkPeers.Any(p => p.PeerId == peer.PeerId))
            {
                NetworkPeers.Add(peer);
                StatusMessage = $"Discovered peer: {peer.DisplayName} ({peer.IpAddress})";
            }
        });
    }

    private void OnPeerLost(object? sender, NetworkPeer peer)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = NetworkPeers.FirstOrDefault(p => p.PeerId == peer.PeerId);
            if (existing != null)
            {
                NetworkPeers.Remove(existing);
                StatusMessage = $"Peer offline: {peer.DisplayName}";
            }
        });
    }

    private void OnPeerGamesUpdated(object? sender, NetworkPeer peer)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            // Update the peer in our collection
            var existing = NetworkPeers.FirstOrDefault(p => p.PeerId == peer.PeerId);
            if (existing != null)
            {
                existing.Games = peer.Games;
            }

            // Recalculate available syncs
            UpdateAvailableSyncs();
            
            StatusMessage = $"Received {peer.Games.Count} games from {peer.DisplayName}";
        });
    }

    private void OnScanProgress(object? sender, string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusMessage = message;
        });
    }

    private void UpdateAvailableSyncs()
    {
        AvailableSyncs.Clear();

        foreach (var peer in NetworkPeers)
        {
            foreach (var remoteGame in peer.Games)
            {
                var localGame = LocalGames.FirstOrDefault(g => g.AppId == remoteGame.AppId);
                
                if (localGame != null && localGame.BuildId != remoteGame.BuildId)
                {
                    var syncInfo = new GameSyncInfo
                    {
                        LocalGame = localGame,
                        RemoteGame = remoteGame,
                        RemotePeer = peer
                    };

                    // Only show if we have an older version
                    if (syncInfo.LocalIsOlder)
                    {
                        AvailableSyncs.Add(syncInfo);
                    }
                }
            }
        }

        if (AvailableSyncs.Count > 0)
        {
            StatusMessage = $"Found {AvailableSyncs.Count} games with available updates";
        }
    }

    private void OnTransferProgress(object? sender, TransferProgressEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            CurrentTransferProgress = e.Progress;
            CurrentTransferSpeed = FormatSpeed(e.SpeedBytesPerSecond);
            CurrentTransferFile = e.CurrentFile;

            if (SelectedSyncItem != null)
            {
                SelectedSyncItem.Progress = e.Progress;
                SelectedSyncItem.TransferSpeed = e.SpeedBytesPerSecond;
            }
        });
    }

    private void OnTransferCompleted(object? sender, TransferCompletedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsTransferring = false;
            CurrentTransferProgress = 0;
            CurrentTransferFile = string.Empty;

            if (e.Success)
            {
                StatusMessage = $"Transfer completed! {FormatBytes(e.TotalBytesTransferred)} transferred";
            }
            else
            {
                StatusMessage = $"Transfer failed: {e.ErrorMessage}";
            }
        });
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    private static string FormatSpeed(long bytesPerSecond)
    {
        return $"{FormatBytes(bytesPerSecond)}/s";
    }

    public void Dispose()
    {
        _networkService.PeerDiscovered -= OnPeerDiscovered;
        _networkService.PeerLost -= OnPeerLost;
        _networkService.PeerGamesUpdated -= OnPeerGamesUpdated;
        _networkService.ScanProgress -= OnScanProgress;
        _fileTransferService.ProgressChanged -= OnTransferProgress;
        _fileTransferService.TransferCompleted -= OnTransferCompleted;

        _networkService.Dispose();
        _fileTransferService.Dispose();
    }
}
