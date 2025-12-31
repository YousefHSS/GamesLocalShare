using System.Collections.ObjectModel;
using System.IO;
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
    private DateTime _lastProgressUpdate = DateTime.MinValue;
    private const int MaxLogMessages = 100;

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
    private GameInfo? _selectedPeerGame;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeTransferCommand))]
    private TransferState? _selectedIncompleteTransfer;

    [ObservableProperty]
    private double _currentTransferProgress;

    [ObservableProperty]
    private string _currentTransferSpeed = string.Empty;

    [ObservableProperty]
    private string _currentTransferFile = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeTransferCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartSyncCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadNewGameCommand))]
    private bool _isTransferring;

    [ObservableProperty]
    private string _lastError = string.Empty;

    [ObservableProperty]
    private bool _isAdmin;

    [ObservableProperty]
    private bool _firewallConfigured;

    [ObservableProperty]
    private string _currentTransferGameName = string.Empty;

    [ObservableProperty]
    private bool _showSpeedInMbps = false; // Toggle between MB/s and Mbps

    [ObservableProperty]
    private bool _highSpeedMode = false; // For wired connections

    [ObservableProperty]
    private string _networkIconKey = "IconWifi";

    public ObservableCollection<GameInfo> LocalGames { get; } = [];
    public ObservableCollection<NetworkPeer> NetworkPeers { get; } = [];
    public ObservableCollection<GameSyncInfo> AvailableSyncs { get; } = [];
    public ObservableCollection<GameInfo> AvailableFromPeers { get; } = [];
    public ObservableCollection<TransferState> IncompleteTransfers { get; } = [];
    public ObservableCollection<LogMessage> LogMessages { get; } = [];

    public MainViewModel()
    {
        _steamScanner = new SteamLibraryScanner();
        _networkService = new NetworkDiscoveryService();
        _fileTransferService = new FileTransferService();

        // Check admin and firewall status
        IsAdmin = FirewallHelper.IsRunningAsAdmin();
        FirewallConfigured = FirewallHelper.CheckFirewallRulesExist();

        // Subscribe to network events
        _networkService.PeerDiscovered += OnPeerDiscovered;
        _networkService.PeerLost += OnPeerLost;
        _networkService.PeerGamesUpdated += OnPeerGamesUpdated;
        _networkService.ScanProgress += OnScanProgress;
        _networkService.ConnectionError += OnConnectionError;
        _networkService.GamesRequestedButEmpty += OnGamesRequestedButEmpty;

        // Subscribe to transfer events
        _fileTransferService.ProgressChanged += OnTransferProgress;
        _fileTransferService.TransferCompleted += OnTransferCompleted;
        _fileTransferService.TransferStopped += OnTransferStopped;

        LocalIpAddress = _networkService.LocalPeer.IpAddress;

        // Initial log message
        AddLog("Application started", LogMessageType.Info);

        // Show firewall warning if not configured
        if (!FirewallConfigured)
        {
            StatusMessage = "?? Firewall not configured - click 'Configure Firewall' to fix connection issues";
            AddLog("Firewall not configured - other computers may not be able to connect", LogMessageType.Warning);
        }
    }

    private void AddLog(string message, LogMessageType type = LogMessageType.Info)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            LogMessages.Insert(0, new LogMessage(message, type));
            
            // Keep log size manageable
            while (LogMessages.Count > MaxLogMessages)
            {
                LogMessages.RemoveAt(LogMessages.Count - 1);
            }
        });
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogMessages.Clear();
        AddLog("Log cleared", LogMessageType.Info);
    }

    private void OnGamesRequestedButEmpty(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            StatusMessage = "?? A peer requested your games but you haven't scanned yet! Click 'Scan My Games'.";
            AddLog("A peer requested games but none scanned yet", LogMessageType.Warning);
        });
    }

    private void OnConnectionError(object? sender, string error)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            LastError = error;
            AddLog($"Connection error: {error}", LogMessageType.Error);
            System.Diagnostics.Debug.WriteLine($"Connection Error: {error}");
        });
    }

    [RelayCommand]
    private async Task ScanLocalGamesAsync()
    {
        try
        {
            IsScanning = true;
            StatusMessage = "Scanning Steam library...";
            AddLog("Scanning Steam library...", LogMessageType.Info);

            var games = await _steamScanner.ScanGamesAsync();

            Application.Current.Dispatcher.Invoke(() =>
            {
                LocalGames.Clear();
                foreach (var game in games)
                {
                    LocalGames.Add(game);
                }
            });

            // Update file transfer service with local games
            _fileTransferService.UpdateLocalGames(games);

            if (games.Count == 0)
            {
                var errors = _steamScanner.ScanErrors;
                if (errors.Count > 0)
                {
                    StatusMessage = $"No games found. Steam path: {_steamScanner.LastSteamPath ?? "NOT FOUND"}. Click Troubleshoot.";
                    LastError = string.Join("\n", errors);
                    AddLog("No games found - check Steam installation", LogMessageType.Warning);
                }
                else
                {
                    StatusMessage = "No games found. Make sure Steam is installed and has games.";
                    AddLog("No games found", LogMessageType.Warning);
                }
            }
            else
            {
                StatusMessage = $"Found {games.Count} installed games";
                AddLog($"Found {games.Count} installed games", LogMessageType.Success);
            }

            await ScanIncompleteTransfersAsync();

            if (IsNetworkActive)
            {
                await _networkService.UpdateLocalGamesAsync(games);
            }

            if (NetworkPeers.Count > 0)
            {
                UpdateAvailableSyncs();
                UpdateAvailableFromPeers();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error scanning: {ex.Message}";
            LastError = ex.ToString();
            AddLog($"Scan error: {ex.Message}", LogMessageType.Error);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void ShowTroubleshootInfo()
    {
        var steamReport = _steamScanner.GetScanReport();
        var networkReport = FirewallHelper.GetNetworkDiagnostics();
        
        // Add file transfer status
        var transferStatus = new System.Text.StringBuilder();
        transferStatus.AppendLine("=== File Transfer Service ===");
        transferStatus.AppendLine($"Listening: {_fileTransferService.IsListening}");
        transferStatus.AppendLine($"Port: {_fileTransferService.ListeningPort}");
        transferStatus.AppendLine($"Port Available: {FileTransferService.IsPortAvailable()}");
        transferStatus.AppendLine();
        
        // Add peer info with IPs
        transferStatus.AppendLine("=== Connected Peers ===");
        transferStatus.AppendLine($"My IP: {LocalIpAddress}");
        transferStatus.AppendLine($"Peers found: {NetworkPeers.Count}");
        foreach (var peer in NetworkPeers)
        {
            transferStatus.AppendLine($"  - {peer.DisplayName}");
            transferStatus.AppendLine($"    IP: {peer.IpAddress}");
            transferStatus.AppendLine($"    Port: {peer.Port}");
            transferStatus.AppendLine($"    Games: {peer.Games.Count}");
            transferStatus.AppendLine($"    Last Seen: {peer.LastSeen:HH:mm:ss}");
        }
        transferStatus.AppendLine();
        
        var fullReport = $"{steamReport}\n\n{transferStatus}\n{networkReport}";
        
        MessageBox.Show(fullReport, "Troubleshooting Report", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void ConfigureFirewall()
    {
        if (!FirewallHelper.IsRunningAsAdmin())
        {
            var result = MessageBox.Show(
                "Configuring firewall requires Administrator privileges.\n\n" +
                "Would you like to restart the application as Administrator?",
                "Administrator Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                FirewallHelper.RestartAsAdmin();
            }
            return;
        }

        // Show options
        var choice = MessageBox.Show(
            "Firewall Configuration Options:\n\n" +
            "YES = Add firewall rules (recommended)\n" +
            "NO = Show detailed firewall diagnostics\n" +
            "CANCEL = Cancel",
            "Configure Firewall",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (choice == MessageBoxResult.Yes)
        {
            var (success, message) = FirewallHelper.AddFirewallRules();
            
            if (success)
            {
                FirewallConfigured = true;
                StatusMessage = "? Firewall configured successfully!";
                MessageBox.Show(
                    "Firewall rules have been added successfully!\n\n" +
                    "Added rules:\n" +
                    "・Program-based rule (allows all GamesLocalShare traffic)\n" +
                    "・UDP 45677 (Discovery)\n" +
                    "・TCP 45678 (Game List)\n" +
                    "・TCP 45679 (File Transfer)\n\n" +
                    "If connections STILL fail, you may have third-party security software\n" +
                    "(antivirus/firewall) that needs separate configuration.",
                    "Firewall Configured",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                StatusMessage = $"Firewall configuration failed: {message}";
                MessageBox.Show(
                    $"Failed to configure firewall:\n\n{message}",
                    "Firewall Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        else if (choice == MessageBoxResult.No)
        {
            ShowTroubleshootInfo();
        }
    }

    private async Task ScanIncompleteTransfersAsync()
    {
        await Task.Run(() =>
        {
            var libraryPaths = _steamScanner.GetLibraryFolders()
                .Select(f => Path.Combine(f, "common"))
                .Where(Directory.Exists)
                .ToList();

            var incomplete = _fileTransferService.FindIncompleteTransfers(libraryPaths);

            Application.Current.Dispatcher.Invoke(() =>
            {
                // Remember the currently selected game's AppId
                var previousSelectedAppId = SelectedIncompleteTransfer?.GameAppId;
                
                IncompleteTransfers.Clear();
                foreach (var transfer in incomplete)
                {
                    IncompleteTransfers.Add(transfer);
                }

                // Try to re-select the previously selected item, or select the first one
                if (IncompleteTransfers.Count > 0)
                {
                    if (previousSelectedAppId != null)
                    {
                        SelectedIncompleteTransfer = IncompleteTransfers.FirstOrDefault(t => t.GameAppId == previousSelectedAppId);
                    }
                    
                    // If previous selection not found, select the first item
                    if (SelectedIncompleteTransfer == null)
                    {
                        SelectedIncompleteTransfer = IncompleteTransfers.First();
                    }
                    
                    StatusMessage = $"Found {incomplete.Count} incomplete transfer(s) that can be resumed";
                }
                else
                {
                    SelectedIncompleteTransfer = null;
                }
                
                // Force command to re-evaluate CanExecute
                ResumeTransferCommand.NotifyCanExecuteChanged();
            });
        });
    }

    [RelayCommand]
    private async Task StartNetworkAsync()
    {
        try
        {
            // Check firewall first
            if (!FirewallConfigured)
            {
                StatusMessage = "?? Firewall not configured - peers may not be able to connect to you";
            }

            StatusMessage = "Starting network discovery...";
            
            await _networkService.StartAsync();
            
            // Start file transfer service with better error handling
            try
            {
                await _fileTransferService.StartListeningAsync();
                System.Diagnostics.Debug.WriteLine($"File transfer service listening: {_fileTransferService.IsListening}");
            }
            catch (InvalidOperationException ex)
            {
                // Port already in use
                StatusMessage = $"?? Network started but file transfer failed: {ex.Message}";
                MessageBox.Show(
                    $"Warning: File Transfer Service failed to start!\n\n{ex.Message}\n\n" +
                    "Other computers will NOT be able to download games FROM this computer.\n\n" +
                    "Please close any other instances of GamesLocalShare and try again.",
                    "File Transfer Service Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            
            IsNetworkActive = true;
            
            // Build status message
            var status = "Network discovery active";
            if (_fileTransferService.IsListening)
            {
                status += $" - File transfer ready (port {_fileTransferService.ListeningPort})";
            }
            else
            {
                status += " - ?? File transfer NOT listening!";
            }
            
            if (!FirewallConfigured)
            {
                status += " (?? firewall not configured)";
            }
            
            StatusMessage = status;

            // Share our games with the network and file transfer service
            if (LocalGames.Count > 0)
            {
                var gamesList = LocalGames.ToList();
                await _networkService.UpdateLocalGamesAsync(gamesList);
                _fileTransferService.UpdateLocalGames(gamesList);
            }

            AddLog("Network discovery started", LogMessageType.Info);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Network error: {ex.Message}";
            IsNetworkActive = false;
            AddLog($"Network start error: {ex.Message}", LogMessageType.Error);
        }
    }

    [RelayCommand]
    private void StopNetwork()
    {
        _networkService.Stop();
        _fileTransferService.Stop();
        
        NetworkPeers.Clear();
        AvailableSyncs.Clear();
        AvailableFromPeers.Clear();
        
        IsNetworkActive = false;
        StatusMessage = "Network discovery stopped";

        AddLog("Network discovery stopped", LogMessageType.Info);
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
                : "No peers found. Make sure the app is running on other computers and firewall is configured on BOTH computers.";
            
            AddLog($"Scan for peers completed: {foundCount} found", LogMessageType.Info);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
            AddLog($"Scan error: {ex.Message}", LogMessageType.Error);
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
                AddLog($"Connected to peer {ManualPeerIp}", LogMessageType.Info);
            }
            else
            {
                StatusMessage = $"Could not connect to {ManualPeerIp}. Make sure firewall is configured on BOTH computers!";
                AddLog($"Failed to connect to {ManualPeerIp}", LogMessageType.Warning);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connection error: {ex.Message}";
            AddLog($"Connection error: {ex.Message}", LogMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task RefreshPeerGamesAsync()
    {
        if (SelectedPeer == null)
        {
            StatusMessage = "Please select a peer first";
            return;
        }

        StatusMessage = $"Requesting game list from {SelectedPeer.DisplayName}...";
        await _networkService.RequestGameListAsync(SelectedPeer);
    }

    [RelayCommand]
    private async Task RefreshAllPeersAsync()
    {
        if (NetworkPeers.Count == 0)
        {
            StatusMessage = "No peers to refresh";
            return;
        }

        StatusMessage = "Refreshing all peer game lists...";
        
        foreach (var peer in NetworkPeers.ToList())
        {
            await _networkService.RequestGameListAsync(peer);
            await Task.Delay(100); // Small delay between requests
        }

        StatusMessage = $"Refreshed game lists from {NetworkPeers.Count} peer(s)";
        AddLog("Refreshed game lists from all peers", LogMessageType.Info);
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

            if (SelectedSyncItem.IsNewDownload)
            {
                CurrentTransferGameName = SelectedSyncItem.RemoteGame.Name;
                StatusMessage = $"Downloading {SelectedSyncItem.RemoteGame.Name}...";
                AddLog($"Starting download: {SelectedSyncItem.RemoteGame.Name}", LogMessageType.Transfer);
                
                // Get a valid Steam library path for new downloads
                var targetPath = GetTargetPathForNewGame(SelectedSyncItem.RemoteGame);
                
                var success = await _fileTransferService.RequestNewGameDownloadAsync(
                    SelectedSyncItem.RemotePeer,
                    SelectedSyncItem.RemoteGame,
                    targetPath);

                SelectedSyncItem.Status = success ? SyncStatus.Completed : SyncStatus.Failed;
            }
            else
            {
                CurrentTransferGameName = SelectedSyncItem.LocalGame!.Name;
                StatusMessage = $"Updating {SelectedSyncItem.LocalGame!.Name}...";
                AddLog($"Starting update: {SelectedSyncItem.LocalGame!.Name}", LogMessageType.Transfer);

                var success = await _fileTransferService.RequestGameTransferAsync(
                    SelectedSyncItem.RemotePeer,
                    SelectedSyncItem.RemoteGame,
                    SelectedSyncItem.LocalGame!);

                SelectedSyncItem.Status = success ? SyncStatus.Completed : SyncStatus.Failed;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sync error: {ex.Message}";
            SelectedSyncItem.Status = SyncStatus.Failed;
            AddLog($"Sync error: {ex.Message}", LogMessageType.Error);
        }
        finally
        {
            IsTransferring = false;
            CurrentTransferGameName = string.Empty;
        }
    }

    [RelayCommand]
    private async Task DownloadNewGameAsync()
    {
        if (SelectedPeerGame == null || IsTransferring)
            return;

        // Find a peer that has this game
        var peer = NetworkPeers.FirstOrDefault(p => p.Games.Any(g => g.AppId == SelectedPeerGame.AppId));
        if (peer == null)
        {
            StatusMessage = "No peer found with this game";
            AddLog("Download failed: No peer found with this game", LogMessageType.Error);
            return;
        }

        // Check if we already have this game
        if (LocalGames.Any(g => g.AppId == SelectedPeerGame.AppId))
        {
            StatusMessage = "You already have this game installed. Check the Updates panel.";
            AddLog("Download skipped: Game already installed", LogMessageType.Warning);
            return;
        }

        try
        {
            IsTransferring = true;
            CurrentTransferGameName = SelectedPeerGame.Name;
            StatusMessage = $"Downloading {SelectedPeerGame.Name} from {peer.DisplayName}...";
            AddLog($"Starting download: {SelectedPeerGame.Name} from {peer.DisplayName}", LogMessageType.Transfer);

            var targetPath = GetTargetPathForNewGame(SelectedPeerGame);

            var success = await _fileTransferService.RequestNewGameDownloadAsync(
                peer,
                SelectedPeerGame,
                targetPath);

            if (success)
            {
                // Refresh local games to include the new one
                await ScanLocalGamesAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download error: {ex.Message}";
            AddLog($"Download error: {ex.Message}", LogMessageType.Error);
        }
        finally
        {
            IsTransferring = false;
            CurrentTransferGameName = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanResumeTransfer))]
    private async Task ResumeTransferAsync()
    {
        if (SelectedIncompleteTransfer == null || IsTransferring)
            return;

        // Find a peer that has this game
        var peer = NetworkPeers.FirstOrDefault(p => 
            p.Games.Any(g => g.AppId == SelectedIncompleteTransfer.GameAppId));

        if (peer == null)
        {
            // Try to connect to the original peer
            var connected = await _networkService.ConnectToPeerByIpAsync(SelectedIncompleteTransfer.SourcePeerIp);
            if (connected)
            {
                peer = NetworkPeers.FirstOrDefault(p => p.IpAddress == SelectedIncompleteTransfer.SourcePeerIp);
            }
        }

        if (peer == null)
        {
            StatusMessage = $"Cannot find a peer with {SelectedIncompleteTransfer.GameName}. " +
                           $"Try connecting to {SelectedIncompleteTransfer.SourcePeerIp}";
            return;
        }

        try
        {
            IsTransferring = true;
            CurrentTransferGameName = SelectedIncompleteTransfer.GameName;
            StatusMessage = $"Resuming download of {SelectedIncompleteTransfer.GameName}...";
            AddLog($"Resuming download: {SelectedIncompleteTransfer.GameName}", LogMessageType.Transfer);

            var success = await _fileTransferService.ResumeTransferAsync(SelectedIncompleteTransfer, peer);

            if (success)
            {
                IncompleteTransfers.Remove(SelectedIncompleteTransfer);
                await ScanLocalGamesAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Resume error: {ex.Message}";
            AddLog($"Resume error: {ex.Message}", LogMessageType.Error);
        }
        finally
        {
            IsTransferring = false;
            CurrentTransferGameName = string.Empty;
        }
    }

    private bool CanResumeTransfer() => SelectedIncompleteTransfer != null && !IsTransferring;

    [RelayCommand]
    private void CancelTransfer()
    {
        // TODO: Implement cancellation
        StatusMessage = "Transfer cancelled (will be saved for resume)";
        IsTransferring = false;
    }

    private string GetTargetPathForNewGame(GameInfo game)
    {
        // Get the first Steam library path
        var libraryFolders = _steamScanner.GetLibraryFolders();
        if (libraryFolders.Count == 0)
        {
            throw new InvalidOperationException("No Steam library folders found");
        }

        var commonPath = Path.Combine(libraryFolders[0], "common");
        if (!Directory.Exists(commonPath))
        {
            Directory.CreateDirectory(commonPath);
        }

        // Create a safe folder name from the game name
        var safeName = string.Join("_", game.Name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(commonPath, safeName);
    }

    private void OnPeerDiscovered(object? sender, NetworkPeer peer)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!NetworkPeers.Any(p => p.PeerId == peer.PeerId))
            {
                NetworkPeers.Add(peer);
                StatusMessage = $"Discovered peer: {peer.DisplayName} ({peer.IpAddress}) - requesting games...";
                AddLog($"Discovered new peer: {peer.DisplayName}", LogMessageType.Info);
            }
        });
    }

    private void OnPeerLost(object? sender, NetworkPeer peer)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var existing = NetworkPeers.FirstOrDefault(p => p.PeerId == peer.PeerId);
            if (existing != null)
            {
                NetworkPeers.Remove(existing);
                StatusMessage = $"Peer offline: {peer.DisplayName}";
                
                UpdateAvailableSyncs();
                UpdateAvailableFromPeers();

                AddLog($"Peer lost: {peer.DisplayName}", LogMessageType.Warning);
            }
        });
    }

    private void OnPeerGamesUpdated(object? sender, NetworkPeer peer)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // Update the peer in our collection
            var existing = NetworkPeers.FirstOrDefault(p => p.PeerId == peer.PeerId);
            if (existing != null)
            {
                existing.Games = peer.Games;
                
                // Force UI update by removing and re-adding
                var index = NetworkPeers.IndexOf(existing);
                NetworkPeers.RemoveAt(index);
                NetworkPeers.Insert(index, existing);
            }

            // Recalculate available syncs and new games
            UpdateAvailableSyncs();
            UpdateAvailableFromPeers();
            
            StatusMessage = $"Received {peer.Games.Count} games from {peer.DisplayName}";
            AddLog($"Updated game list from {peer.DisplayName}", LogMessageType.Info);
        });
    }

    private void OnScanProgress(object? sender, string message)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
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
            AddLog($"Found {AvailableSyncs.Count} games with available updates", LogMessageType.Info);
        }
    }

    private void UpdateAvailableFromPeers()
    {
        AvailableFromPeers.Clear();

        // Find games that peers have but we don't
        var localAppIds = LocalGames.Select(g => g.AppId).ToHashSet();

        foreach (var peer in NetworkPeers)
        {
            foreach (var remoteGame in peer.Games)
            {
                // Skip if we already have this game or already added it
                if (localAppIds.Contains(remoteGame.AppId))
                    continue;

                if (AvailableFromPeers.Any(g => g.AppId == remoteGame.AppId))
                    continue;

                var gameWithPeerInfo = new GameInfo
                {
                    AppId = remoteGame.AppId,
                    Name = remoteGame.Name,
                    InstallPath = remoteGame.InstallPath,
                    SizeOnDisk = remoteGame.SizeOnDisk,
                    BuildId = remoteGame.BuildId,
                    Platform = remoteGame.Platform,
                    IsInstalled = false,
                    IsAvailableFromPeer = true
                };

                AvailableFromPeers.Add(gameWithPeerInfo);
            }
        }

        if (AvailableFromPeers.Count > 0)
        {
            StatusMessage = $"Found {AvailableFromPeers.Count} games available from peers";
            AddLog($"Found {AvailableFromPeers.Count} games available from peers", LogMessageType.Info);
        }
    }

    private void OnTransferProgress(object? sender, TransferProgressEventArgs e)
    {
        // Throttle UI updates to max 10 per second to prevent UI freeze
        var now = DateTime.Now;
        if ((now - _lastProgressUpdate).TotalMilliseconds < 100)
            return;
        _lastProgressUpdate = now;

        // Use BeginInvoke (async) instead of Invoke (sync) to prevent blocking
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            CurrentTransferProgress = e.Progress;
            CurrentTransferSpeed = FormatSpeed(e.SpeedBytesPerSecond, ShowSpeedInMbps);
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
        // Use BeginInvoke for consistency
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            IsTransferring = false;
            CurrentTransferProgress = 0;
            CurrentTransferFile = string.Empty;

            if (e.Success)
            {
                var action = e.IsNewDownload ? "Download" : "Update";
                var gameName = !string.IsNullOrEmpty(e.GameName) ? e.GameName : "Game";
                
                string message;
                if (e.TotalBytesTransferred > 0)
                {
                    message = $"{action} complete: {gameName} ({FormatBytes(e.TotalBytesTransferred)})";
                }
                else
                {
                    message = $"{action} complete: {gameName} (already up to date)";
                }
                
                StatusMessage = message;
                AddLog(message, LogMessageType.Success);
            }
            else
            {
                var gameName = !string.IsNullOrEmpty(e.GameName) ? e.GameName : "Game";
                var message = $"Transfer failed for {gameName}: {e.ErrorMessage}";
                StatusMessage = message + " Progress saved for resume.";
                AddLog(message, LogMessageType.Error);
                // Refresh incomplete transfers
                _ = ScanIncompleteTransfersAsync();
            }
        });
    }

    private void OnTransferStopped(object? sender, TransferStoppedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"OnTransferStopped event received. GameName={e.GameName}, IsPaused={e.IsPaused}");
        
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            System.Diagnostics.Debug.WriteLine($"OnTransferStopped dispatcher. IsTransferring before={IsTransferring}");
            
            IsTransferring = false;
            CurrentTransferProgress = 0;
            CurrentTransferFile = string.Empty;
            CurrentTransferGameName = string.Empty;

            var action = e.IsPaused ? "paused" : "stopped";
            var message = $"Transfer {action}: {e.GameName}";
            StatusMessage = message;
            
            System.Diagnostics.Debug.WriteLine($"OnTransferStopped: IsTransferring after={IsTransferring}, calling ScanIncompleteTransfersAsync");
            
            // Refresh incomplete transfers to show the paused/stopped one
            _ = ScanIncompleteTransfersAsync();
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

    private static string FormatSpeed(long bytesPerSecond, bool inMbps = false)
    {
        if (inMbps)
        {
            // Convert bytes/sec to megabits/sec (1 byte = 8 bits)
            double mbps = (bytesPerSecond * 8.0) / (1000.0 * 1000.0); // Using 1000 for network units
            return $"{mbps:0.#} Mbps";
        }
        else
        {
            return $"{FormatBytes(bytesPerSecond)}/s";
        }
    }

    [RelayCommand]
    private void ToggleSpeedUnit()
    {
        ShowSpeedInMbps = !ShowSpeedInMbps;
        AddLog($"Speed display changed to {(ShowSpeedInMbps ? "Mbps" : "MB/s")}", LogMessageType.Info);
    }

    [RelayCommand]
    private void ToggleHighSpeedMode()
    {
        HighSpeedMode = !HighSpeedMode;
        _fileTransferService.SetHighSpeedMode(HighSpeedMode);
        NetworkIconKey = HighSpeedMode ? "IconWired" : "IconWifi";
        
        if (HighSpeedMode)
        {
            StatusMessage = "High-speed mode enabled (optimized for wired/Gigabit connections)";
            AddLog("High-speed mode enabled - larger buffers for wired connections", LogMessageType.Info);
        }
        else
        {
            StatusMessage = "WiFi mode enabled (optimized for wireless connections)";
            AddLog("WiFi mode enabled - smaller buffers for better wireless performance", LogMessageType.Info);
        }
    }

    [RelayCommand]
    private void CopyLocalIpToClipboard()
    {
        try
        {
            Clipboard.SetText(LocalIpAddress);
            StatusMessage = $"IP address '{LocalIpAddress}' copied to clipboard";
            AddLog($"Copied IP to clipboard: {LocalIpAddress}", LogMessageType.Info);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to copy IP: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenGameFolder(GameInfo? game = null)
    {
        var target = game ?? SelectedLocalGame;
        if (target == null || string.IsNullOrEmpty(target.InstallPath))
            return;

        try
        {
            if (Directory.Exists(target.InstallPath))
            {
                // Open the folder using the system shell. UseShellExecute = true lets the OS handle the path and spaces.
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = target.InstallPath,
                    UseShellExecute = true
                };

                System.Diagnostics.Process.Start(psi);
            }
            else
            {
                StatusMessage = $"Folder does not exist: {target.InstallPath}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyPeerIp()
    {
        if (SelectedPeer == null)
            return;

        try
        {
            Clipboard.SetText(SelectedPeer.IpAddress);
            StatusMessage = $"Copied peer IP: {SelectedPeer.IpAddress}";
            AddLog($"Copied peer IP to clipboard: {SelectedPeer.IpAddress}", LogMessageType.Info);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to copy IP: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteIncompleteTransfer()
    {
        if (SelectedIncompleteTransfer == null)
            return;

        var result = MessageBox.Show(
            $"Delete incomplete transfer for '{SelectedIncompleteTransfer.GameName}'?\n\n" +
            "This will delete the partially downloaded files and cannot be undone.",
            "Delete Incomplete Transfer",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                // Delete the transfer state file
                SelectedIncompleteTransfer.Delete();
                
                // Try to delete the partial game folder if it exists
                if (Directory.Exists(SelectedIncompleteTransfer.TargetPath))
                {
                    Directory.Delete(SelectedIncompleteTransfer.TargetPath, true);
                }

                IncompleteTransfers.Remove(SelectedIncompleteTransfer);
                StatusMessage = $"Deleted incomplete transfer: {SelectedIncompleteTransfer.GameName}";
                AddLog($"Deleted incomplete transfer: {SelectedIncompleteTransfer.GameName}", LogMessageType.Warning);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to delete: {ex.Message}";
                AddLog($"Failed to delete incomplete transfer: {ex.Message}", LogMessageType.Error);
            }
        }
    }

    public void Dispose()
    {
        _networkService.PeerDiscovered -= OnPeerDiscovered;
        _networkService.PeerLost -= OnPeerLost;
        _networkService.PeerGamesUpdated -= OnPeerGamesUpdated;
        _networkService.ScanProgress -= OnScanProgress;
        _networkService.ConnectionError -= OnConnectionError;
        _networkService.GamesRequestedButEmpty -= OnGamesRequestedButEmpty;
        _fileTransferService.ProgressChanged -= OnTransferProgress;
        _fileTransferService.TransferCompleted -= OnTransferCompleted;
        _fileTransferService.TransferStopped -= OnTransferStopped;

        _networkService.Dispose();
        _fileTransferService.Dispose();
    }

    [RelayCommand]
    private async Task TestConnectionToPeerAsync()
    {
        if (SelectedPeer == null)
        {
            StatusMessage = "Please select a peer first";
            return;
        }

        StatusMessage = $"Testing connection to {SelectedPeer.DisplayName}...";
        
        var results = new System.Text.StringBuilder();
        results.AppendLine($"=== Connection Test to {SelectedPeer.DisplayName} ===");
        results.AppendLine($"Target IP: {SelectedPeer.IpAddress}");
        results.AppendLine();

        // Test TCP 45678 (game list)
        results.AppendLine("Testing TCP 45678 (Game List)...");
        try
        {
            using var client1 = new System.Net.Sockets.TcpClient();
            var cts1 = new CancellationTokenSource(5000);
            await client1.ConnectAsync(SelectedPeer.IpAddress, 45678, cts1.Token);
            results.AppendLine("  ? SUCCESS - Connected!");
            client1.Close();
        }
        catch (Exception ex)
        {
            results.AppendLine($"  ? FAILED - {ex.Message}");
        }
        
        // Test TCP 45679 (file transfer)
        results.AppendLine();
        results.AppendLine("Testing TCP 45679 (File Transfer)...");
        try
        {
            using var client2 = new System.Net.Sockets.TcpClient();
            var cts2 = new CancellationTokenSource(5000);
            await client2.ConnectAsync(SelectedPeer.IpAddress, 45679, cts2.Token);
            results.AppendLine("  ? SUCCESS - Connected!");
            client2.Close();
        }
        catch (Exception ex)
        {
            results.AppendLine($"  ? FAILED - {ex.Message}");
        }

        results.AppendLine();
        results.AppendLine("If port 45678 works but 45679 fails:");
        results.AppendLine("- The peer may not have clicked 'Start Network'");
        results.AppendLine("- Another app may be using port 45679 on the peer");
        results.AppendLine("- Firewall may be blocking port 45679 specifically");

        MessageBox.Show(results.ToString(), "Connection Test Results", MessageBoxButton.OK, MessageBoxImage.Information);
        
        StatusMessage = "Connection test complete";
    }

    [RelayCommand]
    private void PauseTransfer()
    {
        System.Diagnostics.Debug.WriteLine($"PauseTransfer command called. IsTransferring={IsTransferring}");
        _fileTransferService.PauseTransfer();
        
        // Force IsTransferring to false immediately in case event doesn't fire
        IsTransferring = false;
        AddLog("Transfer paused - can be resumed from Incomplete panel", LogMessageType.Warning);
    }

    [RelayCommand]
    private void StopTransfer()
    {
        System.Diagnostics.Debug.WriteLine($"StopTransfer command called. IsTransferring={IsTransferring}");
        _fileTransferService.StopTransfer();
        
        // Force IsTransferring to false immediately in case event doesn't fire
        IsTransferring = false;
        AddLog("Transfer stopped - progress saved for resume", LogMessageType.Warning);
    }
}
