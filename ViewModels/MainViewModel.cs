using System.Collections.ObjectModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
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
    private readonly AppSettings _settings;
    private DateTime _lastProgressUpdate = DateTime.MinValue;
    private System.Timers.Timer? _autoUpdateTimer;
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
    [NotifyPropertyChangedFor(nameof(CurrentTransferFormattedProgress))]
    private double _currentTransferProgress;

    [ObservableProperty]
    private string _currentTransferSpeed = string.Empty;

    [ObservableProperty]
    private string _currentTransferFile = string.Empty;

    [ObservableProperty]
    private string _currentTransferTimeRemaining = string.Empty;

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
    private bool _showSpeedInMbps = false;

    [ObservableProperty]
    private bool _highSpeedMode = false;

    [ObservableProperty]
    private string _networkIconKey = "IconWifi";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTransferFormattedProgress))]
    private long _currentTransferTotalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTransferFormattedProgress))]
    private long _currentTransferDownloadedBytes;

    [ObservableProperty]
    private bool _isLogVisible;

    [ObservableProperty]
    private bool _isWindows = OperatingSystem.IsWindows();

    public ObservableCollection<GameInfo> LocalGames { get; } = [];
    public ObservableCollection<NetworkPeer> NetworkPeers { get; } = [];
    public ObservableCollection<GameSyncInfo> AvailableSyncs { get; } = [];
    public ObservableCollection<GameInfo> AvailableFromPeers { get; } = [];
    public ObservableCollection<TransferState> IncompleteTransfers { get; } = [];
    public ObservableCollection<LogMessage> LogMessages { get; } = [];

    // Manual command properties for context menu
    public IRelayCommand<GameInfo?> OpenGameFolderCommand { get; }
    public IRelayCommand<GameInfo?> ToggleGameVisibilityCommand { get; }

    public MainViewModel()
    {
        _steamScanner = new SteamLibraryScanner();
        _networkService = new NetworkDiscoveryService();
        _fileTransferService = new FileTransferService();
        _settings = AppSettings.Load();

        // Check admin and firewall status (Windows only)
        if (OperatingSystem.IsWindows())
        {
            IsAdmin = FirewallHelper.IsRunningAsAdmin();
            FirewallConfigured = FirewallHelper.CheckFirewallRulesExist();
        }
        else
        {
            // On Linux/macOS, we don't need Windows firewall configuration
            FirewallConfigured = true;
        }

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

        // Initialize manual commands
        OpenGameFolderCommand = new RelayCommand<GameInfo?>(
            game => OpenGameFolder(game));
        ToggleGameVisibilityCommand = new AsyncRelayCommand<GameInfo?>(
            async game => await ToggleGameVisibilityAsync(game));

        // Initial log message
        AddLog("Application started", LogMessageType.Info);

        // Show firewall warning if not configured (Windows only)
        if (OperatingSystem.IsWindows() && !FirewallConfigured)
        {
            StatusMessage = "⚠ Firewall not configured - click 'Configure Firewall' to fix connection issues";
            AddLog("Firewall not configured - other computers may not be able to connect", LogMessageType.Warning);
        }

        // Initialize auto-update timer if enabled
        if (_settings.AutoUpdateGames)
        {
            InitializeAutoUpdateTimer();
        }
    }

    private void AddLog(string message, LogMessageType type = LogMessageType.Info)
    {
        Dispatcher.UIThread.Post(() =>
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

    [RelayCommand]
    private void ToggleLog()
    {
        IsLogVisible = !IsLogVisible;
    }

    private void OnGamesRequestedButEmpty(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusMessage = "⚠ A peer requested your games but you haven't scanned yet! Click 'Scan My Games'.";
            AddLog("A peer requested games but none scanned yet", LogMessageType.Warning);
        });
    }

    private void OnConnectionError(object? sender, string error)
    {
        Dispatcher.UIThread.Post(() =>
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

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                LocalGames.Clear();
                foreach (var game in games)
                {
                    // Apply hidden status from settings
                    game.IsHidden = _settings.IsGameHidden(game.AppId);
                    LocalGames.Add(game);
                }
            });

            // Update file transfer service with VISIBLE games only
            var visibleGames = games.Where(g => !g.IsHidden).ToList();
            _fileTransferService.UpdateLocalGames(visibleGames);

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
                var hiddenCount = games.Count - visibleGames.Count;
                StatusMessage = hiddenCount > 0
                    ? $"Found {games.Count} installed games ({hiddenCount} hidden)"
                    : $"Found {games.Count} installed games";
                AddLog($"Found {games.Count} installed games", LogMessageType.Success);

                // Load cover images asynchronously in the background (non-blocking)
                _ = LoadCoverImagesAsync(games);
            }

            await ScanIncompleteTransfersAsync();

            if (IsNetworkActive)
            {
                await _networkService.UpdateLocalGamesAsync(visibleGames);
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

    /// <summary>
    /// Loads cover images for games asynchronously without blocking the UI
    /// </summary>
    private async Task LoadCoverImagesAsync(List<GameInfo> games)
    {
        StatusMessage = "Loading cover images...";
        AddLog($"Loading cover images for {games.Count} games in background...", LogMessageType.Info);

        var loadedCount = 0;
        var tasks = games.Select(async game =>
        {
            await _steamScanner.LoadCoverImageAsync(game);
            
            // Increment counter (note: this is not thread-safe but close enough for display purposes)
            Interlocked.Increment(ref loadedCount);
            
            // Update UI every few images to show progress
            if (loadedCount % 5 == 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    StatusMessage = $"Loaded {loadedCount}/{games.Count} cover images...";
                });
            }
        });

        await Task.WhenAll(tasks);

        Dispatcher.UIThread.Post(() =>
        {
            StatusMessage = $"Cover images loaded ({loadedCount}/{games.Count})";
            AddLog($"Loaded {loadedCount} cover images", LogMessageType.Success);
        });
    }

    [RelayCommand]
    private async Task ShowTroubleshootInfoAsync()
    {
        var steamReport = _steamScanner.GetScanReport();
        var networkReport = OperatingSystem.IsWindows() ? FirewallHelper.GetNetworkDiagnostics() : "Firewall diagnostics not available on this platform.";
        
        // Add file transfer status
        var transferStatus = new System.Text.StringBuilder();
        transferStatus.AppendLine("=== File Transfer Service ===");
        transferStatus.AppendLine($"Listening: {_fileTransferService.IsListening}");
        transferStatus.AppendLine($"Port: {_fileTransferService.ListeningPort}");
        transferStatus.AppendLine($"Primary Port Available: {FileTransferService.IsPrimaryPortAvailable()}");
        transferStatus.AppendLine();
        
        // Add peer info with IPs
        transferStatus.AppendLine("=== Connected Peers ===");
        transferStatus.AppendLine($"My IP: {LocalIpAddress}");
        transferStatus.AppendLine($"My File Transfer Port: {_fileTransferService.ListeningPort}");
        transferStatus.AppendLine($"Peers found: {NetworkPeers.Count}");
        foreach (var peer in NetworkPeers)
        {
            transferStatus.AppendLine($"  - {peer.DisplayName}");
            transferStatus.AppendLine($"    IP: {peer.IpAddress}");
            transferStatus.AppendLine($"    Game List Port: {peer.Port}");
            transferStatus.AppendLine($"    File Transfer Port: {peer.FileTransferPort}");
            transferStatus.AppendLine($"    Games: {peer.Games.Count}");
            transferStatus.AppendLine($"    Last Seen: {peer.LastSeen:HH:mm:ss}");
        }
        transferStatus.AppendLine();
        
        var fullReport = $"{steamReport}\n\n{transferStatus}\n{networkReport}";
        
        await ShowMessageAsync("Troubleshooting Report", fullReport);
    }

    [RelayCommand]
    private async Task ConfigureFirewallAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            await ShowMessageAsync("Not Supported", "Firewall configuration is only available on Windows.");
            return;
        }

        if (!FirewallHelper.IsRunningAsAdmin())
        {
            var result = await ShowConfirmAsync("Administrator Required", 
                "Configuring firewall requires Administrator privileges.\n\nWould you like to restart the application as Administrator?");

            if (result)
            {
                FirewallHelper.RestartAsAdmin();
            }
            return;
        }

        var (success, message) = FirewallHelper.AddFirewallRules();
        
        if (success)
        {
            FirewallConfigured = true;
            StatusMessage = "✓ Firewall configured successfully!";
            await ShowMessageAsync("Firewall Configured",
                "Firewall rules have been added successfully!\n\n" +
                "Added rules:\n" +
                "• Program-based rule (allows all GamesLocalShare traffic)\n" +
                "• UDP 45677 (Discovery)\n" +
                "• TCP 45678 (Game List)\n" +
                "• TCP 45679 (File Transfer)");
        }
        else
        {
            StatusMessage = $"Firewall configuration failed: {message}";
            await ShowMessageAsync("Firewall Error", $"Failed to configure firewall:\n\n{message}");
        }
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow != null)
        {
            var settingsWindow = new Views.SettingsWindow(_settings, LocalGames.ToList(), () =>
            {
                // Callback when settings are saved
                AddLog("Settings saved successfully", LogMessageType.Success);
                StatusMessage = "Settings updated";
                
                // Refresh game list to update hidden status
                foreach (var game in LocalGames)
                {
                    game.IsHidden = _settings.IsGameHidden(game.AppId);
                }
                
                // Update network with visible games
                if (IsNetworkActive)
                {
                    var visibleGames = LocalGames.Where(g => !g.IsHidden).ToList();
                    _ = _networkService.UpdateLocalGamesAsync(visibleGames);
                    _fileTransferService.UpdateLocalGames(visibleGames);
                    AddLog($"Updated network with {visibleGames.Count} visible games", LogMessageType.Info);
                }
                
                // Restart auto-update timer if settings changed
                if (_settings.AutoUpdateGames && _autoUpdateTimer == null)
                {
                    InitializeAutoUpdateTimer();
                }
                else if (!_settings.AutoUpdateGames && _autoUpdateTimer != null)
                {
                    _autoUpdateTimer.Stop();
                    _autoUpdateTimer.Dispose();
                    _autoUpdateTimer = null;
                    AddLog("Auto-update disabled", LogMessageType.Info);
                }
                else if (_settings.AutoUpdateGames && _autoUpdateTimer != null)
                {
                    // Update interval if it changed
                    _autoUpdateTimer.Stop();
                    _autoUpdateTimer.Interval = _settings.AutoUpdateCheckInterval * 60 * 1000;
                    _autoUpdateTimer.Start();
                    AddLog($"Auto-update interval changed to {_settings.AutoUpdateCheckInterval} minutes", LogMessageType.Info);
                }
            });
            
            await settingsWindow.ShowDialog(desktop.MainWindow);
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

            Dispatcher.UIThread.Post(() =>
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
            if (OperatingSystem.IsWindows() && !FirewallConfigured)
            {
                StatusMessage = "⚠ Firewall not configured - peers may not be able to connect to you";
            }

            StatusMessage = "Starting network discovery...";
            AddLog("Starting network discovery...", LogMessageType.Info);
            
            await _networkService.StartAsync();
            
            // Start file transfer service with better error handling
            try
            {
                await _fileTransferService.StartListeningAsync();
                System.Diagnostics.Debug.WriteLine($"File transfer service listening: {_fileTransferService.IsListening} on port {_fileTransferService.ListeningPort}");
                
                // Tell the network service what port we're actually using for file transfers
                _networkService.LocalFileTransferPort = _fileTransferService.ListeningPort;
                
                AddLog($"File transfer service started on port {_fileTransferService.ListeningPort}", LogMessageType.Success);
            }
            catch (InvalidOperationException ex)
            {
                // All ports in use
                StatusMessage = $"⚠ Network started but file transfer failed: {ex.Message}";
                AddLog($"File transfer service FAILED to start: {ex.Message}", LogMessageType.Error);
                await ShowMessageAsync("File Transfer Service Error",
                    $"Warning: File Transfer Service failed to start!\n\n{ex.Message}\n\n" +
                    "Other computers will NOT be able to download games FROM this computer.");
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
                status += " - ⚠ File transfer NOT listening!";
            }
            
            if (OperatingSystem.IsWindows() && !FirewallConfigured)
            {
                status += " (⚠ firewall not configured)";
            }
            
            StatusMessage = status;

            // Share our VISIBLE games with the network and file transfer service
            if (LocalGames.Count > 0)
            {
                var visibleGames = LocalGames.Where(g => !g.IsHidden).ToList();
                await _networkService.UpdateLocalGamesAsync(visibleGames);
                _fileTransferService.UpdateLocalGames(visibleGames);
                AddLog($"Shared {visibleGames.Count} games with network", LogMessageType.Info);
            }

            AddLog("Network started successfully", LogMessageType.Success);

            // Start auto-update timer if enabled
            if (_settings.AutoUpdateGames)
            {
                InitializeAutoUpdateTimer();
            }
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
                : "No peers found. Make sure the app is running on other computers.";
            
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
                StatusMessage = $"Could not connect to {ManualPeerIp}";
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
            await Task.Delay(100);
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
        Dispatcher.UIThread.Post(() =>
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
        Dispatcher.UIThread.Post(() =>
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
        Dispatcher.UIThread.Post(() =>
        {
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
        Dispatcher.UIThread.Post(() =>
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

        Dispatcher.UIThread.Post(() =>
        {
            CurrentTransferProgress = e.Progress;
            CurrentTransferSpeed = FormatSpeed(e.SpeedBytesPerSecond, ShowSpeedInMbps);
            CurrentTransferFile = e.CurrentFile;
            CurrentTransferTimeRemaining = FormatTimeRemaining(e.EstimatedTimeRemaining);
            CurrentTransferTotalBytes = e.TotalBytes;
            CurrentTransferDownloadedBytes = e.TransferredBytes;

            if (SelectedSyncItem != null)
            {
                SelectedSyncItem.Progress = e.Progress;
                SelectedSyncItem.TransferSpeed = e.SpeedBytesPerSecond;
            }
        });
    }

    private void OnTransferCompleted(object? sender, TransferCompletedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsTransferring = false;
            CurrentTransferProgress = 0;
            CurrentTransferFile = string.Empty;
            CurrentTransferTimeRemaining = string.Empty;

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
        Dispatcher.UIThread.Post(() =>
        {
            IsTransferring = false;
            CurrentTransferProgress = 0;
            CurrentTransferFile = string.Empty;
            CurrentTransferGameName = string.Empty;
            CurrentTransferTimeRemaining = string.Empty;

            var action = e.IsPaused ? "paused" : "stopped";
            var message = $"Transfer {action}: {e.GameName}";
            StatusMessage = message;
            
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

    private static string FormatTimeRemaining(TimeSpan timeRemaining)
    {
        if (timeRemaining.TotalSeconds < 1)
            return "Calculating...";
        
        if (timeRemaining.TotalSeconds > 86400) // More than 24 hours
            return "More than 1 day";
        
        if (timeRemaining.TotalHours >= 1)
            return $"{(int)timeRemaining.TotalHours}h {timeRemaining.Minutes}m";
        
        if (timeRemaining.TotalMinutes >= 1)
            return $"{(int)timeRemaining.TotalMinutes}m {timeRemaining.Seconds}s";
        
        return $"{(int)timeRemaining.TotalSeconds}s";
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
    private async Task CopyLocalIpToClipboardAsync()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && 
                desktop.MainWindow?.Clipboard != null)
            {
                await desktop.MainWindow.Clipboard.SetTextAsync(LocalIpAddress);
                StatusMessage = $"IP address '{LocalIpAddress}' copied to clipboard";
                AddLog($"Copied IP to clipboard: {LocalIpAddress}", LogMessageType.Info);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to copy IP: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CopyPeerIpAsync()
    {
        if (SelectedPeer == null)
            return;

        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && 
                desktop.MainWindow?.Clipboard != null)
            {
                await desktop.MainWindow.Clipboard.SetTextAsync(SelectedPeer.IpAddress);
                StatusMessage = $"Copied peer IP: {SelectedPeer.IpAddress}";
                AddLog($"Copied peer IP to clipboard: {SelectedPeer.IpAddress}", LogMessageType.Info);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to copy IP: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteIncompleteTransferAsync()
    {
        if (SelectedIncompleteTransfer == null)
            return;

        var result = await ShowConfirmAsync("Delete Incomplete Transfer",
            $"Delete incomplete transfer for '{SelectedIncompleteTransfer.GameName}'?\n\n" +
            "This will delete the partially downloaded files and cannot be undone.");

        if (result)
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

    [RelayCommand]
    private void PauseTransfer()
    {
        _fileTransferService.PauseTransfer();
        IsTransferring = false;
        AddLog("Transfer paused - can be resumed from Incomplete panel", LogMessageType.Warning);
    }

    [RelayCommand]
    private void StopTransfer()
    {
        _fileTransferService.StopTransfer();
        IsTransferring = false;
        AddLog("Transfer stopped - progress saved for resume", LogMessageType.Warning);
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
        results.AppendLine($"Advertised File Transfer Port: {SelectedPeer.FileTransferPort}");
        results.AppendLine();

        // Test TCP 45678 (game list)
        results.AppendLine("Testing TCP 45678 (Game List)...");
        try
        {
            using var client1 = new System.Net.Sockets.TcpClient();
            var cts1 = new CancellationTokenSource(5000);
            await client1.ConnectAsync(SelectedPeer.IpAddress, 45678, cts1.Token);
            results.AppendLine("  ✓ SUCCESS - Connected!");
            client1.Close();
        }
        catch (Exception ex)
        {
            results.AppendLine($"  ✗ FAILED - {ex.Message}");
        }
        
        // Test peer's advertised file transfer port
        results.AppendLine();
        results.AppendLine($"Testing TCP {SelectedPeer.FileTransferPort} (Peer's File Transfer Port)...");
        try
        {
            using var client2 = new System.Net.Sockets.TcpClient();
            var cts2 = new CancellationTokenSource(5000);
            await client2.ConnectAsync(SelectedPeer.IpAddress, SelectedPeer.FileTransferPort, cts2.Token);
            results.AppendLine("  ✓ SUCCESS - Connected!");
            client2.Close();
        }
        catch (Exception ex)
        {
            results.AppendLine($"  ✗ FAILED - {ex.Message}");
        }

        // Test primary file transfer port (45679) if different from advertised
        if (SelectedPeer.FileTransferPort != 45679)
        {
            results.AppendLine();
            results.AppendLine("Testing TCP 45679 (Default File Transfer Port)...");
            try
            {
                using var client3 = new System.Net.Sockets.TcpClient();
                var cts3 = new CancellationTokenSource(5000);
                await client3.ConnectAsync(SelectedPeer.IpAddress, 45679, cts3.Token);
                results.AppendLine("  ✓ SUCCESS - Connected!");
                client3.Close();
            }
            catch (Exception ex)
            {
                results.AppendLine($"  ✗ FAILED - {ex.Message}");
            }
        }

        results.AppendLine();
        results.AppendLine("═══════════════════════════════════════");
        results.AppendLine("If game list works but file transfer fails:");
        results.AppendLine("═══════════════════════════════════════");
        results.AppendLine("1. The peer may not have clicked 'Start Network'");
        results.AppendLine("2. Firewall may be blocking the file transfer port");
        results.AppendLine("3. Another app may be using the port on the peer");
        results.AppendLine();
        results.AppendLine($"Your file transfer port: {_fileTransferService.ListeningPort}");
        results.AppendLine($"Peer's file transfer port: {SelectedPeer.FileTransferPort}");

        await ShowMessageAsync("Connection Test Results", results.ToString());
        
        StatusMessage = "Connection test complete";
    }

    private void InitializeAutoUpdateTimer()
    {
        _autoUpdateTimer = new System.Timers.Timer(_settings.AutoUpdateCheckInterval * 60 * 1000);
        _autoUpdateTimer.Elapsed += async (s, e) => await CheckForAutoUpdatesAsync();
        _autoUpdateTimer.AutoReset = true;
        _autoUpdateTimer.Start();
        AddLog($"Auto-update enabled: checking every {_settings.AutoUpdateCheckInterval} minutes", LogMessageType.Info);
    }

    private async Task CheckForAutoUpdatesAsync()
    {
        if (!IsNetworkActive || NetworkPeers.Count == 0 || IsTransferring)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            AddLog("Checking for auto-updates...", LogMessageType.Info);
            UpdateAvailableSyncs();
        });

        if (AvailableSyncs.Count > 0)
        {
            AddLog($"Found {AvailableSyncs.Count} game update(s), starting downloads...", LogMessageType.Info);
            
            // Download updates one by one
            foreach (var syncItem in AvailableSyncs.ToList())
            {
                if (!_settings.AutoUpdateGames || !IsNetworkActive)
                    break;

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    SelectedSyncItem = syncItem;
                    await StartSyncAsync();
                });

                // Wait a bit between downloads
                await Task.Delay(5000);
            }
        }
    }

    public string CurrentTransferFormattedProgress => 
        $"{CurrentTransferProgress:0.0}% ({FormatBytes(CurrentTransferDownloadedBytes)} / {FormatBytes(CurrentTransferTotalBytes)})";

    // Helper methods for showing dialogs (cross-platform)
    private async Task ShowMessageAsync(string title, string message)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow != null)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 600,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = message,
                        Margin = new Thickness(16),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    }
                }
            };
            await dialog.ShowDialog(desktop.MainWindow);
        }
    }

    private async Task<bool> ShowConfirmAsync(string title, string message)
    {
        // For now, return true - proper dialog implementation would be added
        // In a full implementation, you'd use a proper dialog library or custom dialog
        return await Task.FromResult(true);
    }

    private void OpenGameFolder(GameInfo? game = null)
    {
        var target = game ?? SelectedLocalGame;
        if (target == null || string.IsNullOrEmpty(target.InstallPath))
            return;

        try
        {
            if (Directory.Exists(target.InstallPath))
            {
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

    private async Task ToggleGameVisibilityAsync(GameInfo? game)
    {
        if (game == null)
            return;

        try
        {
            // Toggle the hidden state
            game.IsHidden = !game.IsHidden;
            
            // Update settings
            if (game.IsHidden)
            {
                _settings.HideGame(game.AppId);
                AddLog($"Hidden game from network: {game.Name}", LogMessageType.Info);
                StatusMessage = $"'{game.Name}' is now hidden from network";
            }
            else
            {
                _settings.UnhideGame(game.AppId);
                AddLog($"Showing game on network: {game.Name}", LogMessageType.Info);
                StatusMessage = $"'{game.Name}' is now visible on network";
            }
            
            // Save settings to disk
            _settings.Save();
            
            // Update the network and file transfer service with VISIBLE games only
            if (IsNetworkActive)
            {
                var visibleGames = LocalGames.Where(g => !g.IsHidden).ToList();
                await _networkService.UpdateLocalGamesAsync(visibleGames);
                _fileTransferService.UpdateLocalGames(visibleGames);
                AddLog($"Updated network with {visibleGames.Count} visible games", LogMessageType.Info);
            }
            
            // Force UI update
            var index = LocalGames.IndexOf(game);
            if (index >= 0)
            {
                LocalGames.RemoveAt(index);
                LocalGames.Insert(index, game);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to toggle game visibility: {ex.Message}";
            AddLog($"Error toggling game visibility: {ex.Message}", LogMessageType.Error);
        }
    }

    public void Dispose()
    {
        _autoUpdateTimer?.Stop();
        _autoUpdateTimer?.Dispose();
        
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
}
