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
    private readonly List<IGameLibraryScanner> _scanners;
    private readonly NetworkDiscoveryService _networkService;
    private readonly FileTransferService _fileTransferService;
    private readonly XboxTransferService _xboxTransferService;
    private readonly XboxSenderService _xboxSenderService;
    private readonly XboxNetworkSender _xboxNetworkSender;
    // Skeleton capture watcher (Windows only; null on other platforms).
    private readonly SkeletonWatcherService? _skeletonWatcher;
    // In-app LAN cache proxy (Windows only; null on other platforms).
    private readonly XboxCacheProxyService? _cacheProxy;
    private readonly AppSettings _settings;
    private readonly DriveDetectionService _driveDetectionService;
    private readonly ExternalFolderScanner _externalScanner;
    private DateTime _lastProgressUpdate = DateTime.MinValue;
    private System.Timers.Timer? _autoUpdateTimer;
    private const int MaxLogMessages = 100;

    [ObservableProperty]
    private XboxTransferState? _xboxTransfer;

    [ObservableProperty]
    private bool _isXboxTransferActive;

    [ObservableProperty]
    private ObservableCollection<GameInfo> _xboxOverlayGames = new();

    [ObservableProperty]
    private string _xboxSourcePath = string.Empty;

    [ObservableProperty]
    private string _xboxDestinationPath = string.Empty;

    [ObservableProperty]
    private string _xboxRootPath = string.Empty;

    [ObservableProperty]
    private bool _isSkeletonWatching;

    [ObservableProperty]
    private string _skeletonDropFolder = string.Empty;

    // LAN cache proxy (Windows only).
    [ObservableProperty]
    private bool _isCacheProxyRunning;

    [ObservableProperty]
    private string _cacheProxyDir = string.Empty;

    /// <summary>One-line stats summary for the cache proxy (hits/misses/filling/cached).</summary>
    [ObservableProperty]
    private string _cacheProxyStats = string.Empty;

    public ObservableCollection<SkeletonCaptureEntry> SkeletonCaptures { get; } = [];
    public ObservableCollection<string> SkeletonLog { get; } = [];

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearQueueCommand))]
    private bool _isQueueProcessing;

    [ObservableProperty]
    private DownloadQueueItem? _currentQueueItem;

    public ObservableCollection<GameInfo> LocalGames { get; } = [];
    public ObservableCollection<NetworkPeer> NetworkPeers { get; } = [];
    public ObservableCollection<GameSyncInfo> AvailableSyncs { get; } = [];
    public ObservableCollection<GameInfo> AvailableFromPeers { get; } = [];
    public ObservableCollection<DriveCandidate> Drives { get; } = [];
    public ObservableCollection<CrossLocationGame> CrossLocationGames { get; } = [];
    [ObservableProperty]
    private ObservableCollection<TransferState> _incompleteTransfers = [];
    public ObservableCollection<LogMessage> LogMessages { get; } = [];
    public ObservableCollection<DownloadQueueItem> DownloadQueue { get; } = [];

    // Manual command properties for context menu
    public IRelayCommand<GameInfo?> OpenGameFolderCommand { get; }
    public IRelayCommand<GameInfo?> ToggleGameVisibilityCommand { get; }
    public IAsyncRelayCommand<LogMessage> CopyLogMessageCommand { get; }

    public MainViewModel()
    {
        _steamScanner = new SteamLibraryScanner();
        _settings = AppSettings.Load();
        _externalScanner = new ExternalFolderScanner(_settings);
        _scanners = new List<IGameLibraryScanner> { _steamScanner, new EpicGamesLibraryScanner(), new XboxLibraryScanner(), _externalScanner };
        _networkService = new NetworkDiscoveryService();
        _fileTransferService = new FileTransferService();
        _xboxTransferService = new XboxTransferService();
        _xboxSenderService = new XboxSenderService();
        _xboxNetworkSender = new XboxNetworkSender();
        _xboxNetworkSender.LogMessage += (_, msg) => AddLog(msg, LogMessageType.Info);
        _xboxNetworkSender.LogsReceived += (_, info) =>
            AddLog($"[Xbox] Receiver logs saved to {info}", LogMessageType.Info);

        // Skeleton capture watcher (Windows only). On install-detect it auto-locates the
        // title's encrypted .msixvc under the configured cache root, populates the CIK store
        // on demand via CikExtractor, and captures a ~17 MB skeleton (xvdtool decrypts on the
        // fly; the app never implements the cipher itself).
        if (OperatingSystem.IsWindows())
        {
            var cikExtractor = string.IsNullOrWhiteSpace(_settings.CikExtractorPath)
                ? @"C:\Users\SIGMA\source\repos\CikExtractor"
                : _settings.CikExtractorPath;
            _skeletonWatcher = new SkeletonWatcherService(
                new SkeletonService(),
                cacheRoot: _settings.XboxPackageCacheRoot,
                cikExtractorPath: cikExtractor);
            SkeletonDropFolder = _skeletonWatcher.DropFolder;
            _skeletonWatcher.Status += OnSkeletonStatus;
            _skeletonWatcher.CaptureCompleted += OnSkeletonCaptureCompleted;
            _skeletonWatcher.CaptureFailed += OnSkeletonCaptureFailed;
            _skeletonWatcher.RestoreCompleted += OnSkeletonRestoreCompleted;
            _skeletonWatcher.RestoreFailed += OnSkeletonCaptureFailed;
            _skeletonWatcher.InstallDetected += OnReceiverInstallDetected;

            // Load skeletons already captured in previous sessions so the UI lists them (with their
            // Restore/Reconstruct actions) on startup — not just ones captured live this session.
            foreach (var s in _skeletonWatcher.GetCapturedSkeletons())
            {
                SkeletonCaptures.Add(new SkeletonCaptureEntry
                {
                    Name = s.Name,
                    SkeletonPath = s.SkeletonPath,
                    SkeletonBytes = s.SkeletonBytes,
                    PackageBytes = s.PackageBytes,
                    SavedBytes = Math.Max(0, s.PackageBytes - s.SkeletonBytes),
                    CapturedAt = s.CapturedAt,
                });
            }

            _cacheProxy = new XboxCacheProxyService();
            CacheProxyDir = string.IsNullOrWhiteSpace(_settings.XboxPackageCacheRoot)
                ? @"F:\xbox-cache" : _settings.XboxPackageCacheRoot!;
            _cacheProxy.Log += OnSkeletonStatus;
            _cacheProxy.StatsChanged += OnCacheProxyStatsChanged;
        }

        // Initialize drive detection
        _driveDetectionService = new DriveDetectionService();
        _driveDetectionService.DrivesChanged += OnDrivesChanged;
        // Populate initial drives
        foreach (var d in _driveDetectionService.CurrentDrives)
            Drives.Add(d);

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
        _networkService.XboxStreamingRequested += OnXboxStreamingRequested;

        // Subscribe to transfer events
        _fileTransferService.ProgressChanged += OnTransferProgress;
        _fileTransferService.TransferCompleted += OnTransferCompleted;
        _fileTransferService.TransferStopped += OnTransferStopped;
        _fileTransferService.LogMessageRaised += (_, msg) => AddLog(msg, LogMessageType.Info);

        LocalIpAddress = _networkService.LocalPeer.IpAddress;

        // Initialize manual commands
        OpenGameFolderCommand = new RelayCommand<GameInfo?>(
            game => OpenGameFolder(game));
        ToggleGameVisibilityCommand = new AsyncRelayCommand<GameInfo?>(
            async game => await ToggleGameVisibilityAsync(game));
        CopyLogMessageCommand = new AsyncRelayCommand<LogMessage>(
            async logMessage => await CopyLogMessageAsync(logMessage));

        // Restore persisted Xbox root path
        if (!string.IsNullOrWhiteSpace(_settings.XboxRootPath))
            XboxRootPath = _settings.XboxRootPath;

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

        // Auto-resume downloads if enabled and network is active
        if (_settings.AutoResumeDownloads)
        {
            _ = AutoResumeDownloadsOnStartupAsync();
        }

        // Auto-start network if enabled
        if (_settings.AutoStartNetwork)
        {
            _ = StartNetworkAsync();
        }

        // Auto-start the Xbox single-copy engine (skeleton watcher + LAN cache proxy) so Xbox games are
        // captured automatically with no manual Start Watching / Start Proxy. One UAC per session.
        if (OperatingSystem.IsWindows() && _settings.XboxSingleCopyAutoStart && _skeletonWatcher != null)
        {
            _ = AutoStartXboxSingleCopyAsync();
        }
    }

    /// <summary>
    /// Brings up the Xbox single-copy engine on launch: starts the skeleton-capture watcher (so installs are
    /// captured automatically) and the LAN cache proxy (so downloads are intercepted/served). Gated by
    /// <see cref="AppSettings.XboxSingleCopyAutoStart"/>; failures are non-fatal (the app still works).
    /// </summary>
    private async Task AutoStartXboxSingleCopyAsync()
    {
        try
        {
            if (_skeletonWatcher != null && !_skeletonWatcher.IsRunning)
            {
                var roots = OperatingSystem.IsWindows()
                    ? new XboxLibraryScanner().GetLibraryFolders()
                    : new List<string>();
                _skeletonWatcher.Start(roots);
                IsSkeletonWatching = _skeletonWatcher.IsRunning;
                AddLog("[Xbox] Auto-capture started (watching for installs)", LogMessageType.Info);
            }

            if (_cacheProxy != null && !_cacheProxy.IsRunning)
            {
                var cacheDir = string.IsNullOrWhiteSpace(CacheProxyDir) ? @"F:\xbox-cache" : CacheProxyDir;
                var ok = await _cacheProxy.StartAsync(cacheDir, new[] { "assets1.xboxlive.com" });
                IsCacheProxyRunning = _cacheProxy.IsRunning;
                AddLog(ok
                    ? "[Xbox] LAN cache proxy started"
                    : "[Xbox] cache proxy not started (elevation declined?) — capture/serve disabled until you start it",
                    ok ? LogMessageType.Info : LogMessageType.Warning);
            }
        }
        catch (Exception ex)
        {
            AddLog($"[Xbox] auto-start failed: {ex.Message}", LogMessageType.Warning);
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

    public void AddLogPublic(string message, LogMessageType type = LogMessageType.Info) => AddLog(message, type);

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
        Dispatcher.UIThread.Post(async () =>
        {
            System.Diagnostics.Debug.WriteLine("OnGamesRequestedButEmpty: Peer requested games but LocalGames.Count = {LocalGames.Count}");
            if (LocalGames.Count == 0)
            {
                StatusMessage = "A peer requested your games - scanning automatically...";
                AddLog("Peer requested games - scanning automatically", LogMessageType.Info);
                await ScanLocalGamesAsync();
            }
            else
            {
                StatusMessage = "⚠ A peer requested your games but you haven't scanned yet! Click 'Scan My Games'.";
                AddLog("A peer requested games but none scanned yet", LogMessageType.Warning);
            }
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
            System.Diagnostics.Debug.WriteLine("ScanLocalGamesAsync: Starting scan...");
            IsScanning = true;
            StatusMessage = "Scanning Steam library...";
            AddLog("Scanning Steam library...", LogMessageType.Info);

            var games = new List<GameInfo>();
            foreach (var scanner in _scanners)
            {
                var scanned = await scanner.ScanGamesAsync();
                AddLog($"{scanner.Platform}: found {scanned.Count} games" +
                    (scanner.ScanErrors.Count > 0 ? $" ({scanner.ScanErrors.Count} warnings)" : ""),
                    scanned.Count > 0 ? LogMessageType.Info : LogMessageType.Warning);
                foreach (var err in scanner.ScanErrors.Take(5))
                {
                    AddLog($"  {scanner.Platform}: {err}", LogMessageType.Warning);
                }
                games.AddRange(scanned);
            }
            games = games.OrderBy(g => g.Name).ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                LocalGames.Clear();
                XboxOverlayGames.Clear();
                foreach (var game in games)
                {
                    // Apply hidden status from settings
                    game.IsHidden = _settings.IsGameHidden(game.AppId);
                    // Xbox: can it use the Smart (updatable) transfer? = a capture exists for it.
                    if (game.Platform == GamePlatform.Xbox)
                        game.XboxSmartReady = _skeletonWatcher?.CanRestore(
                            Path.GetFileName(game.InstallPath.TrimEnd('\\', '/'))) ?? false;
                    LocalGames.Add(game);
                    if (game.Platform == GamePlatform.Xbox && game.IsOverlaySupported)
                    {
                        XboxOverlayGames.Add(game);
                    }
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
            var scanner = _scanners.FirstOrDefault(s => s.Platform == game.Platform);
            if (scanner != null)
            {
                await scanner.LoadCoverImageAsync(game);
            }

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
    private void OpenSettings()
    {
        // The actual UI is now rendered in the React WebUI. The InteropBridge
        // intercepts the "OpenSettings" command from JS and pushes the current
        // settings snapshot via __openSettings(...). This stub exists so any
        // legacy XAML binding still resolves.
    }

    /// <summary>
    /// Applies in-memory settings changes (called after the React modal saves).
    /// Refreshes hidden flags, updates the network, and reconfigures the auto-update timer.
    /// </summary>
    public void ApplySettingsChanges()
    {
        AddLog("Settings saved successfully", LogMessageType.Success);
        StatusMessage = "Settings updated";

        foreach (var game in LocalGames)
        {
            game.IsHidden = _settings.IsGameHidden(game.AppId);
        }

        if (IsNetworkActive)
        {
            var visibleGames = LocalGames.Where(g => !g.IsHidden).ToList();
            _ = _networkService.UpdateLocalGamesAsync(visibleGames);
            _fileTransferService.UpdateLocalGames(visibleGames);
            AddLog($"Updated network with {visibleGames.Count} visible games", LogMessageType.Info);
        }

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
            _autoUpdateTimer.Stop();
            _autoUpdateTimer.Interval = _settings.AutoUpdateCheckInterval * 60 * 1000;
            _autoUpdateTimer.Start();
            AddLog($"Auto-update interval changed to {_settings.AutoUpdateCheckInterval} minutes", LogMessageType.Info);
        }

        // Propagate a changed Xbox package cache path to the live proxy + skeleton watcher. These were
        // captured once at construction, so without this a Settings change never reached them (the proxy kept
        // using the default F:\xbox-cache — "cannot create cache dir" on a PC without that path).
        if (_cacheProxy != null)
        {
            var newCache = string.IsNullOrWhiteSpace(_settings.XboxPackageCacheRoot)
                ? @"F:\xbox-cache" : _settings.XboxPackageCacheRoot!;
            if (!string.Equals(CacheProxyDir, newCache, StringComparison.OrdinalIgnoreCase))
            {
                CacheProxyDir = newCache;
                _skeletonWatcher?.UpdateCacheRoot(newCache);
                AddLog($"Cache path set to {newCache}" +
                    (_cacheProxy.IsRunning ? " — restart the proxy (Stop/Start) to apply" : ""),
                    LogMessageType.Info);
            }
        }
    }

    /// <summary>
    /// Exposes the current AppSettings instance to the InteropBridge.
    /// </summary>
    public AppSettings Settings => _settings;

    private async Task ScanIncompleteTransfersAsync()
    {
        await Task.Run(async () =>
        {
            // Gather library paths from ALL scanners so incomplete transfers
            // for Xbox, Epic, etc. are discovered — not just Steam.
            var libraryPaths = _steamScanner.GetLibraryFolders()
                .Select(f => Path.Combine(f, "common"))
                .Where(Directory.Exists)
                .ToList();

            if (libraryPaths.Count == 0)
            {
                // Try to scan Steam library to populate library folders
                await _steamScanner.ScanGamesAsync();
                libraryPaths = _steamScanner.GetLibraryFolders()
                    .Select(f => Path.Combine(f, "common"))
                    .Where(Directory.Exists)
                    .ToList();
            }

            // Add non-Steam scanner library paths (Xbox, Epic, External).
            // Their GetLibraryFolders() already returns the parent dirs that
            // contain game subdirectories directly (no "common" nesting).
            foreach (var scanner in _scanners)
            {
                if (scanner == _steamScanner)
                    continue;

                foreach (var folder in scanner.GetLibraryFolders())
                {
                    if (Directory.Exists(folder) && !libraryPaths.Contains(folder))
                        libraryPaths.Add(folder);
                }
            }

            var incomplete = _fileTransferService.FindIncompleteTransfers(libraryPaths);

            Dispatcher.UIThread.Post(() =>
            {
                // Remember the currently selected game's AppId
                var previousSelectedAppId = SelectedIncompleteTransfer?.GameAppId;
                
                // Assign new collection to trigger UI update
                IncompleteTransfers = new ObservableCollection<TransferState>(incomplete);
                System.Diagnostics.Debug.WriteLine($"ScanIncompleteTransfersAsync: Assigned {IncompleteTransfers.Count} incomplete transfers to UI");

                // Try to re-select the previously selected item, or select the first one
                if (previousSelectedAppId != null)
                {
                    SelectedIncompleteTransfer = IncompleteTransfers.FirstOrDefault(t => t.GameAppId == previousSelectedAppId);
                }
                
                // If previous selection not found, select the first item
                if (SelectedIncompleteTransfer == null)
                {
                    SelectedIncompleteTransfer = IncompleteTransfers.FirstOrDefault();
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
        AddLog($"Requesting game list from {SelectedPeer.DisplayName}...", LogMessageType.Info);
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
    private void DownloadNewGame()
    {
        if (SelectedPeerGame == null)
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

        // Check if it's already in the queue
        if (DownloadQueue.Any(q => q.GameAppId == SelectedPeerGame.AppId))
        {
            StatusMessage = "Game is already in the download queue";
            AddLog("Game is already in the download queue", LogMessageType.Warning);
            return;
        }

        // Add to queue
        var syncInfo = new GameSyncInfo
        {
            RemoteGame = SelectedPeerGame,
            RemotePeer = peer,
            LocalGame = new GameInfo 
            { 
                AppId = SelectedPeerGame.AppId, 
                Name = SelectedPeerGame.Name, 
                IsInstalled = false 
            }
        };

        var queueItem = new DownloadQueueItem
        {
            GameAppId = SelectedPeerGame.AppId,
            GameName = SelectedPeerGame.Name,
            Type = DownloadQueueItemType.Update, // ProcessNextQueueItemAsync treats IsNewDownload in SyncInfo as Update
            TotalBytes = SelectedPeerGame.SizeOnDisk,
            DownloadedBytes = 0,
            Status = DownloadQueueStatus.Queued,
            Progress = 0,
            SyncInfo = syncInfo
        };

        DownloadQueue.Add(queueItem);

        StatusMessage = "Added new game to download queue";
        AddLog($"Added {SelectedPeerGame.Name} to download queue", LogMessageType.Info);

        // Notify commands
        StartQueueCommand.NotifyCanExecuteChanged();
        ClearQueueCommand.NotifyCanExecuteChanged();

        // Optionally start the queue if it's not processing
        if (!IsQueueProcessing && DownloadQueue.Count > 0)
        {
            StartQueueCommand.Execute(null);
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
                // Remove from incomplete transfers
                var toRemove = IncompleteTransfers.FirstOrDefault(t => t.GameAppId == SelectedIncompleteTransfer.GameAppId);
                if (toRemove != null)
                {
                    IncompleteTransfers.Remove(toRemove);
                }
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

    private bool IsEpicGame(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return false;
        bool Match(GameInfo g) => g.AppId == appId && g.Platform == GamePlatform.EpicGames;
        if (LocalGames.Any(Match)) return true;
        if (AvailableFromPeers.Any(Match)) return true;
        foreach (var peer in NetworkPeers)
        {
            if (peer.Games != null && peer.Games.Any(Match)) return true;
        }
        return false;
    }

    private string GetTargetPathForNewGame(GameInfo game)
    {
        var safeName = string.Join("_", game.Name.Split(Path.GetInvalidFileNameChars()));

        if (game.Platform == GamePlatform.EpicGames)
        {
            var epicRoot = ResolveEpicInstallRoot();
            if (string.IsNullOrEmpty(epicRoot))
            {
                throw new InvalidOperationException(
                    "No Epic Games install folder configured and none could be auto-detected. " +
                    "Set EpicInstallRoot in settings.");
            }
            if (!Directory.Exists(epicRoot)) Directory.CreateDirectory(epicRoot);
            return Path.Combine(epicRoot, safeName);
        }

        // Default: Steam routing
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

        return Path.Combine(commonPath, safeName);
    }

    /// <summary>
    /// Returns the user-configured Epic install root, or auto-detects from
    /// existing Epic installs by picking the most common parent folder.
    /// </summary>
    private string? ResolveEpicInstallRoot()
    {
        if (!string.IsNullOrWhiteSpace(_settings.EpicInstallRoot) && Directory.Exists(_settings.EpicInstallRoot))
            return _settings.EpicInstallRoot;

        var epic = _scanners.OfType<EpicGamesLibraryScanner>().FirstOrDefault();
        if (epic == null) return null;

        var folders = epic.GetLibraryFolders();
        if (folders.Count == 0) return null;

        // Pick the most frequently-used parent folder (already deduplicated, so
        // just take the first existing one — they're all candidates).
        return folders.FirstOrDefault(Directory.Exists);
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
                // Update games on the existing peer object
                existing.Games = new ObservableCollection<GameInfo>(peer.Games);

                // Force a CollectionChanged notification so the InteropBridge
                // re-serializes networkPeers (including the updated games) to
                // the WebView.  Without this, availableFromPeers is refreshed
                // but networkPeers[].games stays stale in JS, causing the
                // "Could not find peer for game" error.
                var idx = NetworkPeers.IndexOf(existing);
                if (idx >= 0)
                    NetworkPeers[idx] = existing;

                System.Diagnostics.Debug.WriteLine($"OnPeerGamesUpdated: {peer.DisplayName} now has {existing.Games.Count} games");
            }
            else
            {
                // Peer not in our collection yet, add it
                System.Diagnostics.Debug.WriteLine($"OnPeerGamesUpdated: Peer {peer.DisplayName} not found in NetworkPeers, adding...");
                NetworkPeers.Add(peer);
            }

            // Recalculate available syncs and new games
            UpdateAvailableSyncs();
            UpdateAvailableFromPeers();
            
            StatusMessage = $"Received {peer.Games.Count} games from {peer.DisplayName}";
            AddLog($"Updated game list from {peer.DisplayName}: {peer.Games.Count} games", LogMessageType.Info);
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

        // Find games that peers have but we don't.
        // Exclude Xbox games that are still installing (GUID folder exists but
        // not fully installed) — the user needs to be able to re-initiate the
        // overlay transfer for those.
        var localAppIds = LocalGames
            .Where(g => g.IsInstalled || g.Platform != GamePlatform.Xbox)
            .Select(g => g.AppId)
            .ToHashSet();

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

            // Update current queue item if processing queue
            if (CurrentQueueItem != null)
            {
                CurrentQueueItem.Progress = e.Progress;
                CurrentQueueItem.DownloadedBytes = e.TransferredBytes;
                CurrentQueueItem.TotalBytes = e.TotalBytes;
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

                // Epic-specific post-transfer hint: the launcher won't see the
                // install until the user imports it. There's no reliable way
                // to register programmatically without writing valid manifest
                // files, so guide the user instead.
                if (IsEpicGame(e.GameAppId))
                {
                    AddLog($"Epic Games: '{gameName}' was copied to disk but Epic Launcher won't see it yet.",
                        LogMessageType.Warning);
                    AddLog("  In Epic Launcher: Library → Install → choose the same folder. EGS will verify the existing files instead of re-downloading.",
                        LogMessageType.Info);
                }
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
            
            // Mark current queue item as paused if this was a pause action
            if (e.IsPaused && CurrentQueueItem != null)
            {
                CurrentQueueItem.Status = DownloadQueueStatus.Paused;
                AddLog($"Queue item marked as paused: {e.GameName}", LogMessageType.Warning);
            }
            
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

    #region Download Queue Management

    /// <summary>
    /// Adds all available updates to the download queue
    /// </summary>
    [RelayCommand]
    private void AddAllUpdatesToQueue()
    {
        if (AvailableSyncs.Count == 0)
        {
            StatusMessage = "No updates available to add to queue";
            return;
        }

        var addedCount = 0;
        foreach (var syncItem in AvailableSyncs)
        {
            // Check if already in queue
            if (DownloadQueue.Any(q => q.GameAppId == syncItem.RemoteGame.AppId))
                continue;

            var queueItem = new DownloadQueueItem
            {
                Type = DownloadQueueItemType.Update,
                GameName = syncItem.RemoteGame.Name,
                GameAppId = syncItem.RemoteGame.AppId,
                SourcePeerName = syncItem.RemotePeer.DisplayName,
                TotalBytes = syncItem.RemoteGame.SizeOnDisk,
                DownloadedBytes = 0,
                Status = DownloadQueueStatus.Queued,
                Progress = 0,
                SyncInfo = syncItem
            };

            DownloadQueue.Add(queueItem);
            addedCount++;
        }

        StatusMessage = $"Added {addedCount} update(s) to download queue";
        AddLog($"Added {addedCount} updates to queue", LogMessageType.Info);
        
        StartQueueCommand.NotifyCanExecuteChanged();
        ClearQueueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Adds all incomplete transfers to the download queue
    /// </summary>
    [RelayCommand]
    private void AddAllIncompleteToQueue()
    {
        if (IncompleteTransfers.Count == 0)
        {
            StatusMessage = "No incomplete transfers to add to queue";
            return;
        }

        var addedCount = 0;
        foreach (var transfer in IncompleteTransfers)
        {
            // Check if already in queue
            if (DownloadQueue.Any(q => q.GameAppId == transfer.GameAppId))
                continue;

            var queueItem = new DownloadQueueItem
            {
                Type = DownloadQueueItemType.Incomplete,
                GameName = transfer.GameName,
                GameAppId = transfer.GameAppId,
                SourcePeerName = transfer.SourcePeerName,
                TotalBytes = transfer.TotalBytes,
                DownloadedBytes = transfer.TransferredBytes,
                Status = DownloadQueueStatus.Queued,
                Progress = transfer.ProgressPercent,
                TransferState = transfer
            };

            DownloadQueue.Add(queueItem);
            addedCount++;
        }

        StatusMessage = $"Added {addedCount} incomplete transfer(s) to download queue";
        AddLog($"Added {addedCount} incomplete transfers to queue", LogMessageType.Info);
        
        StartQueueCommand.NotifyCanExecuteChanged();
        ClearQueueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Moves a queue item up in the list
    /// </summary>
    [RelayCommand]
    private void MoveQueueItemUp(DownloadQueueItem item)
    {
        var index = DownloadQueue.IndexOf(item);
        if (index > 0 && item.Status != DownloadQueueStatus.Downloading)
        {
            DownloadQueue.Move(index, index - 1);
            AddLog($"Moved '{item.GameName}' up in queue", LogMessageType.Info);
        }
    }

    /// <summary>
    /// Moves a queue item down in the list
    /// </summary>
    [RelayCommand]
    private void MoveQueueItemDown(DownloadQueueItem item)
    {
        var index = DownloadQueue.IndexOf(item);
        if (index < DownloadQueue.Count - 1 && item.Status != DownloadQueueStatus.Downloading)
        {
            DownloadQueue.Move(index, index + 1);
            AddLog($"Moved '{item.GameName}' down in queue", LogMessageType.Info);
        }
    }

    /// <summary>
    /// Removes a specific item from the queue
    /// </summary>
    [RelayCommand]
    private void RemoveFromQueue(DownloadQueueItem item)
    {
        if (item.Status == DownloadQueueStatus.Downloading)
        {
            StatusMessage = "Cannot remove an active download. Pause it first.";
            return;
        }

        DownloadQueue.Remove(item);
        AddLog($"Removed '{item.GameName}' from queue", LogMessageType.Info);
        
        StartQueueCommand.NotifyCanExecuteChanged();
        ClearQueueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Clears all queued items (not active or completed)
    /// </summary>
    [RelayCommand]
    private void ClearQueue()
    {
        AddLog("ClearQueue command executed", LogMessageType.Info);
        var clearableItems = DownloadQueue.Where(q => 
            q.Status == DownloadQueueStatus.Queued || 
            q.Status == DownloadQueueStatus.Paused || 
            q.Status == DownloadQueueStatus.Failed).ToList();
        
        foreach (var item in clearableItems)
        {
            DownloadQueue.Remove(item);
        }

        AddLog($"Cleared {clearableItems.Count} item(s) from queue", LogMessageType.Info);
        StatusMessage = $"Cleared {clearableItems.Count} items from queue";
        
        StartQueueCommand.NotifyCanExecuteChanged();
        ClearQueueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Starts processing the download queue
    /// </summary>
    [RelayCommand]
    private async Task StartQueueAsync()
    {
        AddLog("StartQueueAsync command executed", LogMessageType.Info);
        if (IsQueueProcessing || IsTransferring)
            return;

        IsQueueProcessing = true;
        AddLog("Starting download queue processing", LogMessageType.Info);
        StatusMessage = "Processing download queue...";

        await ProcessDownloadQueueAsync();
    }

    private bool CanStartQueue() => !IsQueueProcessing && DownloadQueue.Any(q => 
        q.Status == DownloadQueueStatus.Queued || 
        q.Status == DownloadQueueStatus.Paused);

    /// <summary>
    /// Pauses queue processing
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPauseQueue))]
    private void PauseQueue()
    {
        if (!IsQueueProcessing)
            return;

        AddLog("Pausing download queue...", LogMessageType.Warning);
        IsQueueProcessing = false;
        
        if (IsTransferring)
        {
            _fileTransferService.PauseTransfer();
        }

        // Mark current item as paused if it's downloading
        if (CurrentQueueItem != null && CurrentQueueItem.Status == DownloadQueueStatus.Downloading)
        {
            CurrentQueueItem.Status = DownloadQueueStatus.Paused;
        }

        AddLog("Download queue paused", LogMessageType.Warning);
        StatusMessage = "Download queue paused";
    }

    private bool CanPauseQueue() => IsQueueProcessing;

    /// <summary>
    /// Retries all paused and failed items by resetting their status to Queued
    /// </summary>
    [RelayCommand]
    private void RetryFailedAndPaused()
    {
        var itemsToRetry = DownloadQueue.Where(q => 
            q.Status == DownloadQueueStatus.Paused || 
            q.Status == DownloadQueueStatus.Failed).ToList();

        if (itemsToRetry.Count == 0)
        {
            StatusMessage = "No paused or failed items to retry";
            return;
        }

        foreach (var item in itemsToRetry)
        {
            item.Status = DownloadQueueStatus.Queued;
        }

        AddLog($"Reset {itemsToRetry.Count} item(s) to queued status for retry", LogMessageType.Info);
        StatusMessage = $"Reset {itemsToRetry.Count} item(s) for retry - click Start Queue to begin";
        
        StartQueueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Retries a single queue item by resetting its status to Queued
    /// </summary>
    public void RetryQueueItem(string appId)
    {
        var item = DownloadQueue.FirstOrDefault(q => q.GameAppId == appId);
        if (item == null) return;
        if (item.Status != DownloadQueueStatus.Failed && item.Status != DownloadQueueStatus.Paused)
            return;

        item.Status = DownloadQueueStatus.Queued;
        AddLog($"Reset '{item.GameName}' to queued status for retry", LogMessageType.Info);
        StatusMessage = $"'{item.GameName}' reset for retry - click Start Queue to begin";
        StartQueueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Auto-resumes downloads on startup if enabled in settings
    /// </summary>
    private async Task AutoResumeDownloadsOnStartupAsync()
    {
        // Wait a bit for the UI to initialize
        await Task.Delay(2000);

        // Check if we have any incomplete transfers
        await ScanIncompleteTransfersAsync();

        if (IncompleteTransfers.Count == 0)
            return;

        AddLog($"Auto-resume: Found {IncompleteTransfers.Count} incomplete transfer(s)", LogMessageType.Info);

        // Check if network is active
        if (!IsNetworkActive)
        {
            AddLog("Auto-resume: Starting network...", LogMessageType.Info);
            await StartNetworkAsync();
            
            // Wait for network to start
            await Task.Delay(2000);
        }

        if (!IsNetworkActive)
        {
            AddLog("Auto-resume: Network failed to start", LogMessageType.Warning);
            return;
        }

        // Add all incomplete transfers to queue
        AddAllIncompleteToQueueCommand.Execute(null);

        // Wait for peers to be discovered
        if (NetworkPeers.Count == 0)
        {
            AddLog("Auto-resume: Waiting for peers...", LogMessageType.Info);
            await Task.Delay(5000);
        }

        if (NetworkPeers.Count == 0)
        {
            AddLog("Auto-resume: No peers found, queue will wait", LogMessageType.Warning);
        }

        // Start the queue
        if (DownloadQueue.Count > 0)
        {
            AddLog("Auto-resume: Starting download queue", LogMessageType.Info);
            await StartQueueAsync();
        }
    }

    /// <summary>
    /// Processes the download queue sequentially
    /// </summary>
    private async Task ProcessDownloadQueueAsync()
    {
        while (IsQueueProcessing)
        {
            // Get the next item that is Queued or Paused
            var nextItem = DownloadQueue.FirstOrDefault(q => 
                q.Status == DownloadQueueStatus.Queued || 
                q.Status == DownloadQueueStatus.Paused);
            
            if (nextItem == null)
            {
                // No more items to process
                IsQueueProcessing = false;
                var completedCount = DownloadQueue.Count(q => q.Status == DownloadQueueStatus.Completed);
                var failedCount = DownloadQueue.Count(q => q.Status == DownloadQueueStatus.Failed);
                var pausedCount = DownloadQueue.Count(q => q.Status == DownloadQueueStatus.Paused);
                
                AddLog($"Download queue finished - Completed: {completedCount}, Failed: {failedCount}, Paused: {pausedCount}", LogMessageType.Success);
                
                if (failedCount > 0 || pausedCount > 0)
                {
                    StatusMessage = $"Queue finished: {completedCount} completed, {failedCount} failed, {pausedCount} paused";
                }
                else
                {
                    StatusMessage = "All queued downloads completed successfully";
                }
                break;
            }

            CurrentQueueItem = nextItem;
            nextItem.Status = DownloadQueueStatus.Downloading;

            AddLog($"Starting queued download: {nextItem.GameName}", LogMessageType.Transfer);

            bool success = false;

            try
            {
                IsTransferring = true;
                CurrentTransferGameName = nextItem.GameName;

                if (nextItem.Type == DownloadQueueItemType.Update && nextItem.SyncInfo != null)
                {
                    // Process update
                    var syncItem = nextItem.SyncInfo;
                    
                    if (syncItem.IsNewDownload)
                    {
                        var targetPath = GetTargetPathForNewGame(syncItem.RemoteGame);
                        success = await _fileTransferService.RequestNewGameDownloadAsync(
                            syncItem.RemotePeer,
                            syncItem.RemoteGame,
                            targetPath);
                    }
                    else if (syncItem.LocalGame != null)
                    {
                        success = await _fileTransferService.RequestGameTransferAsync(
                            syncItem.RemotePeer,
                            syncItem.RemoteGame,
                            syncItem.LocalGame);
                    }
                }
                else if (nextItem.Type == DownloadQueueItemType.Incomplete && nextItem.TransferState != null)
                {
                    // Process incomplete transfer
                    var peer = NetworkPeers.FirstOrDefault(p => 
                        p.Games.Any(g => g.AppId == nextItem.TransferState.GameAppId));

                    if (peer == null)
                    {
                        // Try to connect to the original peer
                        var connected = await _networkService.ConnectToPeerByIpAsync(nextItem.TransferState.SourcePeerIp);
                        if (connected)
                        {
                            peer = NetworkPeers.FirstOrDefault(p => p.IpAddress == nextItem.TransferState.SourcePeerIp);
                        }
                    }

                    if (peer != null)
                    {
                        success = await _fileTransferService.ResumeTransferAsync(nextItem.TransferState, peer);
                        
                        if (success)
                        {
                            // Remove from incomplete transfers
                            var toRemove = IncompleteTransfers.FirstOrDefault(t => t.GameAppId == nextItem.TransferState.GameAppId);
                            if (toRemove != null)
                            {
                                IncompleteTransfers.Remove(toRemove);
                            }
                        }
                    }
                    else
                    {
                        AddLog($"Cannot find peer for {nextItem.GameName}", LogMessageType.Error);
                        success = false;
                    }
                }

                // Check if queue was paused during download
                if (!IsQueueProcessing)
                {
                    // Queue was paused - mark item as paused (not failed)
                    nextItem.Status = DownloadQueueStatus.Paused;
                    AddLog($"Download paused: {nextItem.GameName}", LogMessageType.Warning);
                }
                else if (success)
                {
                    nextItem.Status = DownloadQueueStatus.Completed;
                    nextItem.Progress = 100;
                    nextItem.DownloadedBytes = nextItem.TotalBytes;
                    AddLog($"Download completed: {nextItem.GameName}", LogMessageType.Success);
                }
                else
                {
                    nextItem.Status = DownloadQueueStatus.Failed;
                    AddLog($"Download failed: {nextItem.GameName}", LogMessageType.Error);
                }
            }
            catch (Exception ex)
            {
                nextItem.Status = DownloadQueueStatus.Failed;
                AddLog($"Queue download failed: {nextItem.GameName} - {ex.Message}", LogMessageType.Error);
            }
            finally
            {
                IsTransferring = false;
                CurrentTransferGameName = string.Empty;
                CurrentQueueItem = null;
            }

            // Check if we should stop
            if (!IsQueueProcessing)
            {
                AddLog("Queue processing stopped by user", LogMessageType.Warning);
                break;
            }

            // Wait a bit before next download
            await Task.Delay(2000);
        }

        // Refresh game list after queue completes
        if (DownloadQueue.Any(q => q.Status == DownloadQueueStatus.Completed))
        {
            await ScanLocalGamesAsync();
        }

        StartQueueCommand.NotifyCanExecuteChanged();
        PauseQueueCommand.NotifyCanExecuteChanged();
        ClearQueueCommand.NotifyCanExecuteChanged();
    }

    #endregion

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

    private async Task CopyLogMessageAsync(LogMessage logMessage)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && 
                desktop.MainWindow?.Clipboard != null)
            {
                await desktop.MainWindow.Clipboard.SetTextAsync(logMessage.Message);
                StatusMessage = $"Log message copied to clipboard";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to copy log message: {ex.Message}";
        }
    }

    private void OnDrivesChanged(object? sender, IReadOnlyList<DriveCandidate> drives)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Drives.Clear();
            foreach (var d in drives)
                Drives.Add(d);
        });
    }

    [RelayCommand]
    private async Task ScanExternalLibrariesAsync()
    {
        try
        {
            IsScanning = true;
            StatusMessage = "Scanning external libraries...";
            AddLog("Scanning external libraries...", LogMessageType.Info);

            var games = await _externalScanner.ScanGamesAsync();
            AddLog($"External: found {games.Count} games" +
                (_externalScanner.ScanErrors.Count > 0 ? $" ({_externalScanner.ScanErrors.Count} warnings)" : ""),
                games.Count > 0 ? LogMessageType.Info : LogMessageType.Warning);

            StatusMessage = $"External scan complete: {games.Count} games";
        }
        catch (Exception ex)
        {
            StatusMessage = $"External scan error: {ex.Message}";
            AddLog($"External scan error: {ex.Message}", LogMessageType.Error);
        }
        finally
        {
            IsScanning = false;
        }
    }

    public Task AddExternalLibraryAsync(string rootPath, string displayName)
    {
        // Normalise the path so that "E:\stage" and "E:\stage\" are treated as the same library.
        var normPath = rootPath.TrimEnd('\\', '/');

        // Prevent duplicates: skip if a library with the same root already exists.
        if (_settings.ExternalLibraries.Any(l =>
                string.Equals(l.RootPath.TrimEnd('\\', '/'), normPath, StringComparison.OrdinalIgnoreCase)))
        {
            AddLog($"External library already exists: {rootPath}", LogMessageType.Warning);
            return Task.CompletedTask;
        }

        var lib = new ExternalLibrary
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? System.IO.Path.GetFileName(normPath) : displayName,
            RootPath = rootPath,
            DriveSerial = rootPath.Length >= 2 ? rootPath[..2] : string.Empty,
            IsRemovable = false,
            ScanSubfolders = true,
        };
        _settings.ExternalLibraries.Add(lib);
        _settings.Save();
        AddLog($"Added external library: {lib.DisplayName} ({rootPath})", LogMessageType.Info);
        return Task.CompletedTask;
    }

    public Task RemoveExternalLibraryAsync(Guid id)
    {
        var lib = _settings.ExternalLibraries.FirstOrDefault(l => l.Id == id);
        if (lib != null)
        {
            _settings.ExternalLibraries.Remove(lib);
            _settings.Save();
            AddLog($"Removed external library: {lib.DisplayName}", LogMessageType.Info);
        }
        return Task.CompletedTask;
    }

    public async Task<List<CrossLocationGame>> CompareGameLocationsAsync()
    {
        var result = new List<CrossLocationGame>();

        var externalGames = await _externalScanner.ScanGamesAsync();

        var externalRoots = _settings.ExternalLibraries.Select(l => l.RootPath).ToList();
        var localGamesSnapshot = CrossLocationMatcher.FilterDeviceGames(LocalGames, externalRoots);

        foreach (var lib in _settings.ExternalLibraries)
        {
            var externalForLib = externalGames
                .Where(g => g.InstallPath.StartsWith(lib.RootPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Games only on external
            foreach (var extGame in externalForLib)
            {
                var deviceCopy = CrossLocationMatcher.FindDeviceCopy(localGamesSnapshot, extGame);
                var dir = CrossLocationMatcher.ClassifyDirection(deviceCopy, extGame);

                result.Add(new CrossLocationGame
                {
                    DeviceCopy = deviceCopy,
                    ExternalCopy = extGame,
                    Library = lib,
                    Direction = dir,
                });
            }

            // Games only on device (not on this external lib)
            var externalAppIds = externalForLib.Select(g => g.AppId).ToHashSet();
            var externalNames = externalForLib.Select(g => g.Name.ToLowerInvariant()).ToHashSet();
            foreach (var localGame in localGamesSnapshot)
            {
                if (!externalAppIds.Contains(localGame.AppId) &&
                    !externalNames.Contains(localGame.Name.ToLowerInvariant()))
                {
                    result.Add(new CrossLocationGame
                    {
                        DeviceCopy = localGame,
                        ExternalCopy = null,
                        Library = lib,
                        Direction = CopyDirection.OnlyOnDevice,
                    });
                }
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            CrossLocationGames.Clear();
            foreach (var g in result)
                CrossLocationGames.Add(g);
        });

        return result;
    }

    public async Task StartLocalCopyAsync(string appId, Guid libraryId, CopyDirection? overrideDirection = null)
    {
        var lib = _settings.ExternalLibraries.FirstOrDefault(l => l.Id == libraryId);
        if (lib == null)
        {
            AddLog($"StartLocalCopy: library {libraryId} not found", LogMessageType.Error);
            return;
        }

        var crossGame = CrossLocationGames.FirstOrDefault(g => g.AppId == appId && g.Library?.Id == libraryId);
        if (crossGame == null)
        {
            AddLog($"StartLocalCopy: game {appId} not found in cross-location list", LogMessageType.Error);
            return;
        }

        // Xbox (MSIXVC) games cannot be moved with a plain file copy: their
        // executables are content-protected (copying them yields Access Denied
        // or encrypted bytes) and a copied folder is not recognized by the
        // receiving Xbox app. They must go through the Xbox Overlay transfer.
        if (crossGame.DeviceCopy?.Platform == GamePlatform.Xbox
            || crossGame.ExternalCopy?.Platform == GamePlatform.Xbox)
        {
            const string msg = "Xbox (Game Pass / MSIXVC) games cannot be moved with a plain " +
                "copy - use 'Xbox Overlay' transfer instead.";
            AddLog($"StartLocalCopy: {msg}", LogMessageType.Error);
            LastError = msg;
            return;
        }

        // The user can resolve UnknownVersion (and force-override any other direction) by
        // passing an explicit DeviceToDrive / DriveToDevice from the UI.
        var effectiveDirection = overrideDirection ?? crossGame.Direction;
        if (overrideDirection.HasValue && overrideDirection.Value != crossGame.Direction)
        {
            AddLog($"StartLocalCopy: user override direction {overrideDirection.Value} (auto={crossGame.Direction})", LogMessageType.Info);
        }

        GameInfo? source = null;
        string destPath = string.Empty;

        if (effectiveDirection == CopyDirection.DeviceToDrive || effectiveDirection == CopyDirection.OnlyOnDevice)
        {
            source = crossGame.DeviceCopy;
            if (source == null)
            {
                AddLog($"StartLocalCopy: device copy missing for {appId} (direction={effectiveDirection})", LogMessageType.Error);
                return;
            }
            destPath = System.IO.Path.Combine(lib.RootPath, System.IO.Path.GetFileName(source.InstallPath));
        }
        else if (effectiveDirection == CopyDirection.DriveToDevice || effectiveDirection == CopyDirection.OnlyOnDrive)
        {
            source = crossGame.ExternalCopy;
            if (source == null)
            {
                AddLog($"StartLocalCopy: external copy missing for {appId} (direction={effectiveDirection})", LogMessageType.Error);
                return;
            }
            var libraryFolders = _steamScanner.GetLibraryFolders();
            if (libraryFolders.Count == 0)
            {
                AddLog("StartLocalCopy: no Steam library folders found", LogMessageType.Error);
                return;
            }
            destPath = System.IO.Path.Combine(libraryFolders[0], "common", System.IO.Path.GetFileName(source.InstallPath));
        }
        else
        {
            AddLog($"StartLocalCopy: game {appId} direction is {effectiveDirection}, nothing to copy. Pass an explicit direction to resolve.", LogMessageType.Info);
            return;
        }

        try
        {
            IsTransferring = true;
            CurrentTransferGameName = source.Name;
            StatusMessage = $"Copying {source.Name} locally...";
            AddLog($"Starting local copy: {source.Name} → {destPath}", LogMessageType.Transfer);

            var engine = new IncrementalSyncEngine();
            engine.LogMessageRaised += (_, msg) => AddLog(msg, LogMessageType.Info);
            engine.ProgressChanged += (_, e) =>
            {
                var now = DateTime.Now;
                if ((now - _lastProgressUpdate).TotalMilliseconds >= 100)
                {
                    _lastProgressUpdate = now;
                    Dispatcher.UIThread.Post(() =>
                    {
                        CurrentTransferProgress = e.Progress;
                        CurrentTransferFile = e.CurrentFile;
                        CurrentTransferTotalBytes = e.TotalBytes;
                        CurrentTransferDownloadedBytes = e.TransferredBytes;
                        CurrentTransferSpeed = FormatSpeed(e.SpeedBytesPerSecond, ShowSpeedInMbps);
                        CurrentTransferTimeRemaining = FormatTimeRemaining(e.EstimatedTimeRemaining);
                    });
                }
            };

            var transport = new LocalFileTransport();
            var cts = new CancellationTokenSource();
            var resumeState = TransferState.Load(destPath);
            if (resumeState != null)
                AddLog($"Resuming previous copy of {source.Name} ({resumeState.CompletedFiles.Count} files already done)", LogMessageType.Info);
            var success = await engine.CopyGameAsync(source.InstallPath, destPath, source, transport, resumeState, cts.Token);

            if (success)
            {
                StatusMessage = $"Local copy complete: {source.Name}";
                AddLog($"Local copy finished: {source.Name}", LogMessageType.Success);
            }
            else
            {
                StatusMessage = $"Local copy had errors: {source.Name}";
                AddLog($"Local copy completed with errors: {source.Name}", LogMessageType.Warning);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Local copy error: {ex.Message}";
            AddLog($"Local copy error: {ex.Message}", LogMessageType.Error);
        }
        finally
        {
            IsTransferring = false;
            CurrentTransferGameName = string.Empty;
            CurrentTransferProgress = 0;
            CurrentTransferFile = string.Empty;
        }
    }


    // ===== Xbox Transfer Wizard =====


    // ===== Xbox Sender Commands =====

    [RelayCommand]
    private async Task StartXboxStageAsync(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            AddLog("Xbox staging: no source path provided", LogMessageType.Error);
            return;
        }

        if (!ElevationHelper.IsElevated())
        {
            const string msg = "Xbox staging requires the app to run as Administrator. " +
                "Use 'Restart as Administrator' first.";
            AddLog(msg, LogMessageType.Error);
            LastError = msg;
            return;
        }

        _xboxSenderService.Reset();
        var error = _xboxSenderService.ValidateSource(sourcePath);
        if (error != null)
        {
            AddLog($"Xbox staging validation failed: {error}", LogMessageType.Error);
            LastError = error;
            return;
        }

        IsXboxTransferActive = true;
        XboxTransfer = _xboxSenderService.State;
        _xboxSenderService.StateChanged += OnXboxTransferStateChanged;
        _xboxSenderService.LogMessage += (_, msg) => AddLog(msg, LogMessageType.Info);

        AddLog($"Xbox staging: {_xboxSenderService.State.GameName} ({_xboxSenderService.State.SourceBytes / 1024 / 1024} MB, {_xboxSenderService.State.SourceFileCount} files)", LogMessageType.Info);

        // Prompt user for destination
        StatusMessage = "Xbox staging: Select destination folder (USB/shared drive)...";
    }

    [RelayCommand]
    private async Task CompleteXboxStageAsync(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            AddLog("Xbox staging: no destination provided", LogMessageType.Error);
            return;
        }

        try
        {
            await _xboxSenderService.StageToFolderAsync(destinationPath);
            if (_xboxSenderService.State.CurrentStep == XboxTransferStep.Complete)
            {
                AddLog($"Xbox staging complete: {_xboxSenderService.State.DestinationPath}", LogMessageType.Info);
                StatusMessage = $"Xbox staging complete: {_xboxSenderService.State.GameName}";
                await ScanLocalGamesAsync();
            }
            else
            {
                LastError = _xboxSenderService.State.ErrorMessage ?? "Staging failed";
            }
        }
        catch (Exception ex)
        {
            AddLog($"Xbox staging error: {ex.Message}", LogMessageType.Error);
            LastError = ex.Message;
        }
        finally
        {
            IsXboxTransferActive = false;
            _xboxSenderService.StateChanged -= OnXboxTransferStateChanged;
        }
    }

    [RelayCommand]
    private async Task PrepareXboxNetworkAsync(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            AddLog("Xbox network prep: no source path provided", LogMessageType.Error);
            return;
        }

        if (!ElevationHelper.IsElevated())
        {
            const string msg = "Xbox network streaming requires the app to run as Administrator. " +
                "Use 'Restart as Administrator' first.";
            AddLog(msg, LogMessageType.Error);
            LastError = msg;
            return;
        }

        _xboxSenderService.Reset();
        var error = _xboxSenderService.ValidateSource(sourcePath);
        if (error != null)
        {
            AddLog($"Xbox network prep validation failed: {error}", LogMessageType.Error);
            LastError = error;
            return;
        }

        IsXboxTransferActive = true;
        XboxTransfer = _xboxSenderService.State;
        _xboxSenderService.StateChanged += OnXboxTransferStateChanged;
        _xboxSenderService.LogMessage += (_, msg) => AddLog(msg, LogMessageType.Info);

        try
        {
            // Scan install folder directly, rescue protected exe/dll if possible,
            // and build a manifest — no full staging step needed.
            AddLog($"Xbox network: preparing {_xboxSenderService.State.GameName} for direct streaming...", LogMessageType.Info);
            var (manifest, pathOverrides) = await _xboxSenderService.PrepareForDirectNetworkAsync();

            if (_xboxSenderService.State.CurrentStep != XboxTransferStep.Complete)
            {
                LastError = _xboxSenderService.State.ErrorMessage ?? "Preparation failed";
                return;
            }

            _xboxNetworkSender.SetManifest(manifest);
            if (pathOverrides.Count > 0)
                _xboxNetworkSender.SetPathOverrides(pathOverrides);
            _xboxNetworkSender.Start();

            _xboxSenderService.State.CurrentStep = XboxTransferStep.WaitingForReceiver;
            _xboxSenderService.State.StatusMessage =
                $"Ready! Waiting for receiver on port {_xboxNetworkSender.Port}...";
            OnXboxTransferStateChanged(this, _xboxSenderService.State);

            AddLog($"Xbox network sender ready on port {_xboxNetworkSender.Port}: " +
                   $"{manifest.TotalFiles} files, {manifest.TotalBytes / 1024 / 1024} MB", LogMessageType.Info);
        }
        catch (Exception ex)
        {
            AddLog($"Xbox network prep error: {ex.Message}", LogMessageType.Error);
            LastError = ex.Message;
            IsXboxTransferActive = false;
            _xboxSenderService.StateChanged -= OnXboxTransferStateChanged;
        }
    }

    [RelayCommand]
    private void StopXboxNetwork()
    {
        _xboxNetworkSender.Stop();
        IsXboxTransferActive = false;
        _xboxSenderService.StateChanged -= OnXboxTransferStateChanged;
        StatusMessage = "Xbox network sender stopped";
        AddLog("Xbox network sender stopped", LogMessageType.Info);
    }

    [RelayCommand]
    private void CancelXboxStage()
    {
        _xboxSenderService.Cancel();
        IsXboxTransferActive = false;
        StatusMessage = "Xbox staging cancelled";
        AddLog("Xbox staging cancelled by user", LogMessageType.Warning);
    }

    [RelayCommand]
    private async Task StartXboxTransferAsync((string sourcePath, string? xboxRoot, bool force) args)
    {
        var (sourcePath, xboxRoot, force) = args;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            AddLog("Xbox transfer: no source path provided", LogMessageType.Error);
            return;
        }

        if (!ElevationHelper.IsElevated())
        {
            const string msg = "Xbox transfer requires the app to run as Administrator. " +
                "Use 'Restart as Administrator' first.";
            AddLog(msg, LogMessageType.Error);
            LastError = msg;
            return;
        }

        _xboxTransferService.Reset();
        var error = _xboxTransferService.ValidateSource(sourcePath);
        if (error != null)
        {
            AddLog($"Xbox transfer validation failed: {error}", LogMessageType.Error);
            LastError = error;
            return;
        }

        IsXboxTransferActive = true;
        XboxTransfer = _xboxTransferService.State;
        _xboxTransferService.StateChanged += OnXboxTransferStateChanged;
        _xboxTransferService.LogMessage += (_, msg) => AddLog(msg, LogMessageType.Info);

        AddLog($"Xbox transfer started: {_xboxTransferService.State.GameName} ({_xboxTransferService.State.SourceBytes / 1024 / 1024} MB)", LogMessageType.Info);
        StatusMessage = $"Xbox transfer: {_xboxTransferService.State.GameName}";

        try
        {
            if (!string.IsNullOrWhiteSpace(xboxRoot))
                AddLog($"Xbox transfer: using install root {xboxRoot}", LogMessageType.Info);
            if (force)
                AddLog("Xbox transfer: -Force enabled (safety checks bypassed)", LogMessageType.Warning);

            var verdict = await _xboxTransferService.RunOverlayAsync(xboxRoot, force);
            switch (verdict)
            {
                case XboxTransferVerdict.FullSkip:
                    AddLog($"Xbox transfer SUCCESS: {_xboxTransferService.State.GameName} installed with only {_xboxTransferService.State.NetworkReceivedMB:N1} MB downloaded!", LogMessageType.Info);
                    StatusMessage = $"Xbox transfer complete - {_xboxTransferService.State.GameName} installed!";
                    break;
                case XboxTransferVerdict.DeltaOnly:
                    AddLog($"Xbox transfer partial success: {_xboxTransferService.State.NetworkReceivedMB:N1} MB downloaded (delta only)", LogMessageType.Warning);
                    StatusMessage = $"Xbox transfer done - some data re-downloaded ({_xboxTransferService.State.NetworkReceivedMB:N1} MB)";
                    break;
                default:
                    AddLog($"Xbox transfer result: {verdict} (rx={_xboxTransferService.State.NetworkReceivedMB:N1} MB)", LogMessageType.Warning);
                    StatusMessage = $"Xbox transfer: {verdict}";
                    break;
            }
        }
        catch (Exception ex)
        {
            AddLog($"Xbox transfer error: {ex.Message}", LogMessageType.Error);
            LastError = ex.Message;
        }
        finally
        {
            IsXboxTransferActive = false;
            _xboxTransferService.StateChanged -= OnXboxTransferStateChanged;
            await ScanLocalGamesAsync();
        }
    }

    [RelayCommand]
    private async Task StartXboxNetworkTransferAsync((string peerHost, int peerPort, string gameAppId, string? xboxRoot, bool force) args)
    {
        var (peerHost, peerPort, gameAppId, xboxRoot, force) = args;
        if (string.IsNullOrWhiteSpace(peerHost))
        {
            AddLog("Xbox network transfer: no peer host provided", LogMessageType.Error);
            return;
        }

        if (!ElevationHelper.IsElevated())
        {
            const string msg = "Xbox network transfer requires the app to run as Administrator. " +
                "Use 'Restart as Administrator' first.";
            AddLog(msg, LogMessageType.Error);
            LastError = msg;
            return;
        }

        _xboxTransferService.Reset();
        IsXboxTransferActive = true;
        XboxTransfer = _xboxTransferService.State;
        _xboxTransferService.StateChanged += OnXboxTransferStateChanged;
        _xboxTransferService.LogMessage += (_, msg) => AddLog(msg, LogMessageType.Info);

        // Update UI state early so the bar shows "Requesting..."
        _xboxTransferService.State.StatusMessage = $"Requesting sender to prepare game...";
        _xboxTransferService.State.CurrentStep = XboxTransferStep.DownloadingFromPeer;
        OnXboxTransferStateChanged(this, _xboxTransferService.State);

        AddLog($"Xbox network transfer: requesting {peerHost} to prepare game {gameAppId}", LogMessageType.Info);
        StatusMessage = $"Xbox: requesting sender to prepare...";

        try
        {
            // Ask the sender to prepare the Xbox game for streaming
            var senderPeer = NetworkPeers.FirstOrDefault(p =>
                p.IpAddress == peerHost);
            if (senderPeer == null)
            {
                _xboxTransferService.State.StatusMessage = "Sender peer not found";
                _xboxTransferService.State.CurrentStep = XboxTransferStep.Failed;
                _xboxTransferService.State.ErrorMessage = $"Peer {peerHost} not found in network peers";
                OnXboxTransferStateChanged(this, _xboxTransferService.State);
                AddLog($"Xbox network transfer: peer {peerHost} not found", LogMessageType.Error);
                return;
            }

            var (streamOk, streamPort, streamErr) =
                await _networkService.RequestXboxStreamingAsync(senderPeer, gameAppId);
            if (!streamOk)
            {
                _xboxTransferService.State.StatusMessage = "Sender could not prepare game";
                _xboxTransferService.State.CurrentStep = XboxTransferStep.Failed;
                _xboxTransferService.State.ErrorMessage = $"Sender refused: {streamErr}";
                OnXboxTransferStateChanged(this, _xboxTransferService.State);
                AddLog($"Xbox network transfer: sender refused — {streamErr}", LogMessageType.Error);
                return;
            }

            // Use the port the sender confirmed
            if (streamPort > 0) peerPort = streamPort;
            AddLog($"Xbox network transfer: sender ready on port {peerPort}, starting download...", LogMessageType.Info);

            var verdict = await _xboxTransferService.RunNetworkOverlayAsync(
                peerHost, peerPort, xboxRoot, force, gameAppId: gameAppId);
            switch (verdict)
            {
                case XboxTransferVerdict.FullSkip:
                    AddLog($"Xbox network transfer SUCCESS! Installed with minimal extra download.", LogMessageType.Info);
                    StatusMessage = "Xbox network transfer complete!";
                    break;
                case XboxTransferVerdict.DeltaOnly:
                    AddLog($"Xbox network transfer partial success: some data re-downloaded", LogMessageType.Warning);
                    StatusMessage = "Xbox network transfer done with some re-download";
                    break;
                default:
                    AddLog($"Xbox network transfer result: {verdict}", LogMessageType.Warning);
                    StatusMessage = $"Xbox network transfer: {verdict}";
                    break;
            }
        }
        catch (Exception ex)
        {
            AddLog($"Xbox network transfer error: {ex.Message}", LogMessageType.Error);
            LastError = ex.Message;
        }
        finally
        {
            _xboxTransferService.StateChanged -= OnXboxTransferStateChanged;
            // Keep bar visible so user sees final state — DismissXboxTransfer hides it
            OnXboxTransferStateChanged(this, _xboxTransferService.State);
            await ScanLocalGamesAsync();
        }
    }

    [RelayCommand]
    private void CancelXboxTransfer()
    {
        _xboxTransferService.Cancel();
        // If a streaming peer install is showing in the bar, tear it down too.
        EndXboxPeerInstall();
        _peerInstallState = null;
        IsXboxTransferActive = false;
        StatusMessage = "Xbox transfer cancelled";
        AddLog("Xbox transfer cancelled by user", LogMessageType.Warning);
    }

    [RelayCommand]
    private void PauseXboxTransfer()
    {
        _xboxTransferService.PauseDownload();
        AddLog("Xbox transfer paused by user", LogMessageType.Warning);
    }

    [RelayCommand]
    private void ResumeXboxTransfer()
    {
        _xboxTransferService.ResumeDownload();
        AddLog("Xbox transfer resumed", LogMessageType.Info);
    }

    [RelayCommand]
    private void DismissXboxTransfer()
    {
        EndXboxPeerInstall();
        _peerInstallState = null;
        IsXboxTransferActive = false;
    }

    // ---- Skeleton capture watcher ----------------------------------------

    [RelayCommand]
    private void ToggleSkeletonWatcher()
    {
        if (_skeletonWatcher == null)
        {
            StatusMessage = "Skeleton capture is only available on Windows";
            return;
        }

        if (_skeletonWatcher.IsRunning)
        {
            _skeletonWatcher.Stop();
            IsSkeletonWatching = false;
        }
        else
        {
            var roots = OperatingSystem.IsWindows()
                ? new XboxLibraryScanner().GetLibraryFolders()
                : new List<string>();
            _skeletonWatcher.Start(roots);
            IsSkeletonWatching = _skeletonWatcher.IsRunning;
        }
    }

    [RelayCommand]
    private void OpenSkeletonDropFolder()
    {
        if (_skeletonWatcher == null) return;
        try
        {
            Directory.CreateDirectory(_skeletonWatcher.DropFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _skeletonWatcher.DropFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open drop folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RestoreSkeleton(string? name)
    {
        if (_skeletonWatcher == null)
        {
            StatusMessage = "Skeleton restore is only available on Windows";
            return;
        }
        if (string.IsNullOrWhiteSpace(name)) return;
        await _skeletonWatcher.RestoreAsync(name);
    }

    /// <summary>Reconstructs a title's genuine package to a chosen destination root (e.g. an external drive),
    /// preserving the nested CDN path so another PC's proxy serves it as a HIT. Invoked by the bridge after a
    /// folder picker.</summary>
    public async Task RestoreSkeletonToFolderAsync(string name, string destinationRoot)
    {
        if (_skeletonWatcher == null)
        {
            StatusMessage = "Skeleton reconstruct is only available on Windows";
            return;
        }
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(destinationRoot)) return;
        await _skeletonWatcher.RestoreAsync(name, destinationRoot);
    }

    /// <summary>
    /// Drives "Copy to Drive" for an Xbox game using the Smart (updatable) method: reconstruct the genuine
    /// package onto the chosen external library (drive) under its CDN path, so another PC installs it (updatable)
    /// via the proxy. Only valid when the game has a capture; the UI shows this action only when smart-ready.
    /// </summary>
    [RelayCommand]
    private async Task CopyXboxGameToDrive((string appId, string libraryId) args)
    {
        if (_skeletonWatcher == null) { StatusMessage = "Only available on Windows"; return; }
        var (appId, libraryId) = args;
        var game = LocalGames.FirstOrDefault(g => g.AppId == appId && g.Platform == GamePlatform.Xbox);
        if (game == null || string.IsNullOrEmpty(game.InstallPath))
        { AddLog($"Copy to drive: Xbox game {appId} not found", LogMessageType.Error); return; }
        var lib = _settings.ExternalLibraries.FirstOrDefault(l => l.Id.ToString() == libraryId);
        if (lib == null) { AddLog($"Copy to drive: library not found", LogMessageType.Error); return; }
        var name = Path.GetFileName(game.InstallPath.TrimEnd('\\', '/'));
        if (!_skeletonWatcher.CanRestore(name))
        {
            AddLog($"Copy to drive: \"{game.Name}\" isn't ready for an updatable copy yet — reinstall it with " +
                   $"the app open, or use Basic (Stage to Drive).", LogMessageType.Warning);
            return;
        }
        AddLog($"Copy to drive (updatable): rebuilding \"{game.Name}\" → {lib.RootPath} …", LogMessageType.Info);
        await RestoreSkeletonToFolderAsync(name, lib.RootPath);
    }

    private void OnSkeletonRestoreCompleted(string name)
    {
        AddLog($"[Skeleton] restored package for {name} (Verify/update will HIT the cache)", LogMessageType.Success);
    }

    [RelayCommand]
    private async Task ToggleCacheProxy()
    {
        if (_cacheProxy == null)
        {
            StatusMessage = "The cache proxy is only available on Windows";
            return;
        }
        if (_cacheProxy.IsRunning)
        {
            await _cacheProxy.StopAsync();
        }
        else
        {
            var cacheDir = string.IsNullOrWhiteSpace(CacheProxyDir) ? @"F:\xbox-cache" : CacheProxyDir;
            await _cacheProxy.StartAsync(cacheDir, new[] { "assets1.xboxlive.com" });
        }
        IsCacheProxyRunning = _cacheProxy.IsRunning;
    }

    private void OnCacheProxyStatsChanged()
    {
        if (_cacheProxy == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            IsCacheProxyRunning = _cacheProxy.IsRunning;
            CacheProxyStats = $"hits {_cacheProxy.Hits} · misses {_cacheProxy.Misses} · " +
                              $"filling {_cacheProxy.Filling} · cached {_cacheProxy.Cached} · " +
                              $"{_cacheProxy.Bytes / 1048576.0:F1} MB served";

            // Drive the transfer bar from peer-served bytes while a streaming peer install is active.
            if (_peerInstallState != null && !string.IsNullOrEmpty(_activePeerMatchKey))
            {
                long got = _cacheProxy.PeerBytes, total = _cacheProxy.PeerTotal;
                var now = DateTime.UtcNow;
                double secs = (now - _peerLastTick).TotalSeconds;
                if (secs >= 0.5)
                {
                    _peerInstallState.NetworkSpeedMBps = Math.Max(0, (got - _peerLastBytes) / 1048576.0 / secs);
                    _peerLastBytes = got; _peerLastTick = now;
                }
                _peerInstallState.NetworkReceivedMB = got / 1048576.0;
                if (total > 0)
                {
                    _peerInstallState.OverlayProgress = Math.Min(100.0, got * 100.0 / total);
                    double remMB = Math.Max(0, (total - got) / 1048576.0);
                    _peerInstallState.NetworkEta = _peerInstallState.NetworkSpeedMBps > 0.01
                        ? TimeSpan.FromSeconds(remMB / _peerInstallState.NetworkSpeedMBps).ToString(@"mm\:ss")
                        : "";
                    if (got >= total && _peerInstallState.CurrentStep == XboxTransferStep.DownloadingFromPeer)
                    {
                        // All bytes streamed. Show "finishing" but KEEP the peer origin active until the install
                        // is actually detected complete (the Store may re-request tail ranges) — see
                        // OnReceiverInstallDetected, which finalizes + clears the origin.
                        _peerInstallState.StatusMessage =
                            $"Streamed {total / 1048576.0:F0} MB from the peer — Xbox app is finishing the install…";
                        // Safety-net: if the watcher never reports the install (e.g. installed to an unwatched
                        // root), finalize the bar anyway after a grace period so it can't get stuck.
                        if (!_peerFinishScheduled)
                        {
                            _peerFinishScheduled = true;
                            long totalSnap = total;
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(TimeSpan.FromSeconds(60));
                                if (_peerInstallState != null &&
                                    _peerInstallState.CurrentStep == XboxTransferStep.DownloadingFromPeer)
                                    FinalizePeerInstallBar(
                                        $"Streamed {totalSnap / 1048576.0:F0} MB from the peer — install finishing in the Xbox app");
                            });
                        }
                    }
                }
                XboxTransfer = _peerInstallState;
                OnPropertyChanged(nameof(XboxTransfer));
            }
        });
    }

    private void OnSkeletonStatus(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SkeletonLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
            while (SkeletonLog.Count > MaxLogMessages)
                SkeletonLog.RemoveAt(SkeletonLog.Count - 1);
        });
        AddLog($"[Skeleton] {line}", LogMessageType.Info);
    }

    private void OnSkeletonCaptureCompleted(SkeletonCaptureResult res)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var name = Path.GetFileNameWithoutExtension(res.SkelPath);
            SkeletonCaptures.Insert(0, new SkeletonCaptureEntry
            {
                Name = string.IsNullOrEmpty(name) ? "(unknown)" : name,
                SkeletonPath = res.SkelPath,
                SkeletonBytes = res.SkelFileSize,
                PackageBytes = res.USize,
                SavedBytes = Math.Max(0, res.USize - res.SkelFileSize),
                CapturedAt = DateTime.Now.ToString("o"),
            });
        });
    }

    private void OnSkeletonCaptureFailed(string message)
    {
        AddLog($"[Skeleton] {message}", LogMessageType.Error);
    }

    private void OnXboxTransferStateChanged(object? sender, XboxTransferState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            XboxTransfer = state;
            // XboxTransferState is mutated in place, so the generated setter sees
            // no reference change and skips its notification. Force it so the
            // bridge re-pushes the updated state (step, progress) to the WebUI.
            OnPropertyChanged(nameof(XboxTransfer));
            StatusMessage = $"Xbox: {state.StatusMessage}";
        });
    }

    /// <summary>
    /// Called when a remote peer asks us to prepare an Xbox game for streaming.
    /// Finds the game by appId, prepares it, and starts the XboxNetworkSender.
    /// </summary>
    // ---- Streaming single-copy peer transfer (sender + receiver) -----------------------------------

    private System.Diagnostics.Process? _xboxServeProc;   // sender: the running `xvdtool --serve` for a title
    private string? _xboxServeTitle;
    private string? _activePeerMatchKey;                  // receiver: the proxy peer-origin key in effect
    private XboxTransferState? _peerInstallState;         // receiver: drives the transfer bar during a peer install
    private long _peerLastBytes;
    private DateTime _peerLastTick;
    private bool _peerFinishScheduled;                    // one-shot guard for the finalize safety-net

    private static string XvdToolPath =>
        Path.Combine(AppContext.BaseDirectory, "tools", "xvdtool", "XVDTool.exe");

    private static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    // Distinctive substring of the package's CDN URL path (the .msixvc filename carries "<Name>...<pubhash>").
    // Used as the proxy peer-origin match key on the receiver. Falls back to the appId.
    private static string DeriveMatchKey(GameInfo game)
    {
        var pfn = game.PackageFamilyName;
        if (!string.IsNullOrEmpty(pfn))
        {
            int us = pfn.IndexOf('_');
            var name = us > 0 ? pfn.Substring(0, us) : pfn; // "<Publisher>.<Title>"
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return game.AppId;
    }

    /// <summary>
    /// SENDER side. A peer asked to stream an Xbox title we own. Launch <c>xvdtool --serve</c> backed by this
    /// title's skeleton + installed files (genuine bytes produced on the fly - no package materialized) and
    /// return the port. The receiver's proxy forwards the title's requests here instead of the CDN.
    /// </summary>
    private Task<(bool Success, int Port, string? Error)> OnXboxStreamingRequested(string gameAppId)
    {
        try
        {
            var game = LocalGames.FirstOrDefault(g =>
                g.AppId.Equals(gameAppId, StringComparison.OrdinalIgnoreCase) &&
                g.Platform == GamePlatform.Xbox);
            if (game == null)
                return Task.FromResult((false, 0, (string?)$"Game {gameAppId} not found in local Xbox library"));
            if (string.IsNullOrEmpty(game.InstallPath) || !Directory.Exists(game.InstallPath))
                return Task.FromResult((false, 0, (string?)$"Install path not found for {game.Name}"));
            if (_skeletonWatcher == null)
                return Task.FromResult((false, 0, (string?)"Skeleton engine unavailable"));

            var name = Path.GetFileName(game.InstallPath.TrimEnd('\\', '/'));
            var skel = Path.Combine(_skeletonWatcher.SkeletonStore, name + ".skl");
            if (!File.Exists(skel))
                return Task.FromResult((false, 0, (string?)$"No skeleton captured for \"{name}\" yet — install it here so it gets captured, then retry"));

            // Stop any previous serve, start a fresh one for this title.
            StopXboxServe();
            int port = FreeTcpPort();
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = XvdToolPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--cikfolder"); psi.ArgumentList.Add(_skeletonWatcher.CikStore);
            psi.ArgumentList.Add("--skel"); psi.ArgumentList.Add(skel);
            psi.ArgumentList.Add("--install"); psi.ArgumentList.Add(game.InstallPath);
            psi.ArgumentList.Add("--serve");
            psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(port.ToString());
            psi.ArgumentList.Add("--bind"); psi.ArgumentList.Add("0.0.0.0");

            var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                return Task.FromResult((false, 0, (string?)"Failed to launch xvdtool --serve"));
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) AddLog($"[serve] {e.Data}", LogMessageType.Info); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) AddLog($"[serve] {e.Data}", LogMessageType.Warning); };
            proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
            _xboxServeProc = proc; _xboxServeTitle = name;

            AddLog($"Xbox streaming: serving \"{name}\" on port {port} (on-the-fly reconstruct, no package copy)", LogMessageType.Success);
            return Task.FromResult((true, port, (string?)null));
        }
        catch (Exception ex)
        {
            AddLog($"Xbox streaming preparation failed: {ex.Message}", LogMessageType.Error);
            return Task.FromResult((false, 0, (string?)ex.Message));
        }
    }

    private void StopXboxServe()
    {
        try { if (_xboxServeProc is { HasExited: false }) _xboxServeProc.Kill(true); } catch { }
        _xboxServeProc = null; _xboxServeTitle = null;
    }

    /// <summary>
    /// RECEIVER side. One-click streaming install of an Xbox peer game: ask the peer to start serving, point our
    /// LAN-cache proxy's miss-origin at the peer (transient), fetch the tiny skeleton so we end single-copy, then
    /// prompt the user to click Install in the Xbox app. The install pulls genuine bytes from the peer on the fly
    /// (~0 CDN download); neither PC ever stores the full package.
    /// </summary>
    public async Task StartXboxPeerInstallAsync(string peerHost, string gameAppId)
    {
        if (_cacheProxy == null || _skeletonWatcher == null)
        {
            AddLog("Xbox peer install is only available on Windows", LogMessageType.Error);
            return;
        }
        var peer = NetworkPeers.FirstOrDefault(p => p.IpAddress == peerHost);
        if (peer == null) { AddLog($"Peer {peerHost} not found", LogMessageType.Error); return; }
        var game = peer.Games?.FirstOrDefault(g => g.AppId.Equals(gameAppId, StringComparison.OrdinalIgnoreCase));
        if (game == null) { AddLog($"Game {gameAppId} not offered by {peer.DisplayName}", LogMessageType.Error); return; }

        AddLog($"Xbox peer install: requesting {peer.DisplayName} to stream {game.Name}…", LogMessageType.Info);
        var (ok, port, err) = await _networkService.RequestXboxStreamingAsync(peer, gameAppId);
        if (!ok || port <= 0)
        {
            AddLog($"Xbox peer install: sender refused — {err}", LogMessageType.Error);
            return;
        }
        AddLog($"Xbox peer install: sender streaming on {peerHost}:{port}", LogMessageType.Info);

        // Ensure the proxy is up (it intercepts the Store's CDN requests).
        if (!_cacheProxy.IsRunning)
        {
            var cacheDir = string.IsNullOrWhiteSpace(CacheProxyDir) ? @"F:\xbox-cache" : CacheProxyDir;
            await _cacheProxy.StartAsync(cacheDir, new[] { "assets1.xboxlive.com" });
            IsCacheProxyRunning = _cacheProxy.IsRunning;
        }

        // Point the proxy at the peer for this title's package, transient (no disk fill).
        var matchKey = DeriveMatchKey(game);
        _cacheProxy.SetPeerOrigin(matchKey, peerHost, port);
        _activePeerMatchKey = matchKey;

        // Show the standard Xbox transfer bar, driven live by the proxy's peer-served bytes.
        _peerInstallState = new XboxTransferState
        {
            GameName = game.Name,
            IsNetwork = true,
            CurrentStep = XboxTransferStep.DownloadingFromPeer,
            StatusMessage = $"Click Install on \"{game.Name}\" in the Xbox app — streaming from {peer.DisplayName}",
            PeerId = peer.PeerId,
            AppId = gameAppId,
        };
        _peerLastBytes = 0; _peerLastTick = DateTime.UtcNow; _peerFinishScheduled = false;
        Dispatcher.UIThread.Post(() =>
        {
            XboxTransfer = _peerInstallState;
            IsXboxTransferActive = true;
            OnPropertyChanged(nameof(XboxTransfer));
        });

        // Fetch the tiny skeleton so this PC ends single-copy (install files + skeleton, no re-capture).
        await TryFetchPeerSkeletonAsync(peerHost, port, game);

        StatusMessage = $"Ready — click Install on \"{game.Name}\" in the Xbox app (it streams from {peer.DisplayName})";
        AddLog($"Xbox peer install: READY. In the Xbox app, install \"{game.Name}\" now — bytes stream from {peer.DisplayName} (~0 download). " +
               $"Progress shows in the transfer bar.", LogMessageType.Success);
    }

    private async Task TryFetchPeerSkeletonAsync(string peerHost, int port, GameInfo game)
    {
        try
        {
            var name = !string.IsNullOrEmpty(game.PackageFamilyName)
                ? game.Name : game.Name; // skeleton named after the install folder; best-effort = game name
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var bytes = await http.GetByteArrayAsync($"http://{peerHost}:{port}/__skeleton__");
            // Save into the drop folder; the watcher matches it to the install once the folder appears.
            var outPath = Path.Combine(_skeletonWatcher!.SkeletonStore, name + ".skl");
            await File.WriteAllBytesAsync(outPath, bytes);
            AddLog($"Xbox peer install: received skeleton ({bytes.Length / 1048576.0:F1} MB) → {Path.GetFileName(outPath)}", LogMessageType.Info);
        }
        catch (Exception ex)
        {
            AddLog($"Xbox peer install: could not fetch skeleton ({ex.Message}) — install will still work; re-capture later for single-copy", LogMessageType.Warning);
        }
    }

    /// <summary>Receiver: the watcher saw a new install complete while a streaming peer install is active —
    /// finalize the transfer bar and stop forwarding to the peer.</summary>
    private void OnReceiverInstallDetected(string installDir)
    {
        if (_peerInstallState == null || string.IsNullOrEmpty(_activePeerMatchKey)) return;
        FinalizePeerInstallBar($"Installed \"{_peerInstallState.GameName}\" — streamed from the peer (no internet download)");
        AddLog("Xbox peer install: install detected complete — streaming origin cleared", LogMessageType.Success);
    }

    /// <summary>Flip the peer-install transfer bar to Complete and stop forwarding. Safe to call repeatedly.</summary>
    private void FinalizePeerInstallBar(string statusMessage)
    {
        if (_peerInstallState == null) return;
        EndXboxPeerInstall();
        Dispatcher.UIThread.Post(() =>
        {
            if (_peerInstallState == null) return;
            _peerInstallState.CurrentStep = XboxTransferStep.Complete;
            _peerInstallState.Verdict = XboxTransferVerdict.FullSkip;
            _peerInstallState.OverlayProgress = 100;
            _peerInstallState.NetworkSpeedMBps = 0;
            _peerInstallState.StatusMessage = statusMessage;
            XboxTransfer = _peerInstallState;
            OnPropertyChanged(nameof(XboxTransfer));
        });
    }

    /// <summary>Receiver: clear the active peer streaming origin (call when the install is done or cancelled).</summary>
    public void EndXboxPeerInstall()
    {
        if (_cacheProxy != null && !string.IsNullOrEmpty(_activePeerMatchKey))
        {
            _cacheProxy.ClearPeerOrigin(_activePeerMatchKey!);
            _activePeerMatchKey = null;
            AddLog("Xbox peer install: streaming origin cleared", LogMessageType.Info);
        }
    }

    [RelayCommand]
    private async Task StartXboxPeerInstall(string? payload)
    {
        // payload = "peerHost|gameAppId"
        if (string.IsNullOrWhiteSpace(payload)) return;
        var parts = payload.Split('|', 2);
        if (parts.Length != 2) return;
        await StartXboxPeerInstallAsync(parts[0], parts[1]);
    }

    public void Dispose()
    {
        _autoUpdateTimer?.Stop();
        _autoUpdateTimer?.Dispose();

        _driveDetectionService.DrivesChanged -= OnDrivesChanged;
        _driveDetectionService.Dispose();

        _networkService.PeerDiscovered -= OnPeerDiscovered;
        _networkService.PeerLost -= OnPeerLost;
        _networkService.PeerGamesUpdated -= OnPeerGamesUpdated;
        _networkService.ScanProgress -= OnScanProgress;
        _networkService.ConnectionError -= OnConnectionError;
        _networkService.GamesRequestedButEmpty -= OnGamesRequestedButEmpty;
        _fileTransferService.ProgressChanged -= OnTransferProgress;
        _fileTransferService.TransferCompleted -= OnTransferCompleted;
        _fileTransferService.TransferStopped -= OnTransferStopped;

        if (_skeletonWatcher != null)
        {
            _skeletonWatcher.Status -= OnSkeletonStatus;
            _skeletonWatcher.CaptureCompleted -= OnSkeletonCaptureCompleted;
            _skeletonWatcher.CaptureFailed -= OnSkeletonCaptureFailed;
            _skeletonWatcher.RestoreCompleted -= OnSkeletonRestoreCompleted;
            _skeletonWatcher.RestoreFailed -= OnSkeletonCaptureFailed;
            _skeletonWatcher.Dispose();
        }

        if (_cacheProxy != null)
        {
            _cacheProxy.Log -= OnSkeletonStatus;
            _cacheProxy.StatsChanged -= OnCacheProxyStatsChanged;
            _cacheProxy.Dispose();
        }

        _networkService.Dispose();
        _fileTransferService.Dispose();
        _xboxNetworkSender.Dispose();
    }
}
