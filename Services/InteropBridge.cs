using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaWebView;
using WebViewCore.Events;
using GamesLocalShare.Models;
using GamesLocalShare.ViewModels;

namespace GamesLocalShare.Services;

public class InteropBridge : IDisposable
{
    private readonly WebView? _webView;
    private readonly MainViewModel _viewModel;
    private bool _isInitialized = false;
    private CancellationTokenSource? _debounceCts;
    private const int DebounceMs = 80;

    // Serialization options for JSON
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public InteropBridge(WebView? webView, MainViewModel viewModel)
    {
        _webView = webView;
        _viewModel = viewModel;

        if (_webView == null) return;

        _webView.WebMessageReceived += OnWebMessageReceived;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized || _webView == null) return;

        _isInitialized = true;

        // Subscribe to ViewModel property changes (debounced so rapid-fire
        // updates like folder-picker closure don't serialize the full state
        // on every single property change).
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null)
            {
                SchedulePushStateChange();
            }
        };

        // Forward ViewModel-raised toasts/alerts to the WebUI (window.__notify).
        _viewModel.NotificationRaised += notification =>
        {
            Dispatcher.UIThread.Post(() => _ = PushNotificationAsync(notification));
        };

        // Subscribe to collection changes
        SubscribeToCollection(_viewModel.LocalGames, "localGames");
        SubscribeToCollection(_viewModel.NetworkPeers, "networkPeers");
        SubscribeToCollection(_viewModel.AvailableSyncs, "availableSyncs");
        SubscribeToCollection(_viewModel.AvailableFromPeers, "availableFromPeers");
        SubscribeToCollection(_viewModel.IncompleteTransfers, "incompleteTransfers");
        SubscribeToCollection(_viewModel.DownloadQueue, "downloadQueue");
        SubscribeToCollection(_viewModel.LogMessages, "logMessages");
        SubscribeToCollection(_viewModel.Drives, "drives");
        SubscribeToCollection(_viewModel.CrossLocationGames, "crossLocationGames");
        SubscribeToCollection(_viewModel.SkeletonCaptures, "skeletonCaptures");
        SubscribeToCollection(_viewModel.SkeletonLog, "skeletonLog");

        // Subscribe to individual game property changes (for cover images)
        SubscribeToGamePropertyChanges();

        // Send initial state
        await PushInitialStateAsync();
    }

    private void SubscribeToCollection<T>(ObservableCollection<T> collection, string collectionName)
    {
        collection.CollectionChanged += (s, e) =>
        {
            // Ensure collection change notifications happen on UI thread
            Dispatcher.UIThread.Post(() =>
            {
                _ = PushCollectionChangeAsync(collectionName, collection);
            });
        };
    }

    private void SubscribeToGamePropertyChanges()
    {
        foreach (var game in _viewModel.LocalGames)
        {
            if (game is INotifyPropertyChanged notifyGame)
            {
                notifyGame.PropertyChanged += OnGamePropertyChanged;
            }
        }

        // Also subscribe to new games when they're added
        _viewModel.LocalGames.CollectionChanged += (s, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is INotifyPropertyChanged notifyGame)
                    {
                        notifyGame.PropertyChanged += OnGamePropertyChanged;
                    }
                }
            }
        };
    }

    private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // When a game property changes (like CoverImagePath), push the updated games list
        if (e.PropertyName == nameof(GameInfo.CoverImagePath) || e.PropertyName == nameof(GameInfo.CoverImage) || e.PropertyName == nameof(GameInfo.CoverUrl) || e.PropertyName == nameof(GameInfo.Name))
        {
            System.Diagnostics.Debug.WriteLine($"[DEBUG] Game property changed: {e.PropertyName}, pushing updated games list");
            // Ensure we push the update on the UI thread
            Dispatcher.UIThread.Post(() =>
            {
                _ = PushCollectionChangeAsync("localGames", _viewModel.LocalGames);
            });
        }
    }

    private async Task PushInitialStateAsync()
    {
        if (_webView == null) return;

        var state = GetFullState();
        var json = JsonSerializer.Serialize(state, JsonOptions);

        System.Diagnostics.Debug.WriteLine($"[DEBUG] Pushing initial state to WebView, localGames count: {((dynamic)state).localGames.Count}");
        foreach (var game in ((dynamic)state).localGames)
        {
            System.Diagnostics.Debug.WriteLine($"[DEBUG] Game: {game.name} (AppId: {game.appId}), CoverImagePath: {game.coverImagePath}");
        }

        await ExecuteJavaScriptAsync($"window.__initState({json});");
    }

    private void SchedulePushStateChange()
    {
        if (_webView == null) return;

        // Cancel any pending push — only the last one in a burst runs.
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        var cts = _debounceCts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMs, cts.Token);
                await Dispatcher.UIThread.InvokeAsync(() => PushStateNow());
            }
            catch (OperationCanceledException) { }
        });
    }

    private async void PushStateNow()
    {
        if (_webView == null) return;

        // Push only scalar / small properties — collections are already
        // pushed independently by SubscribeToCollection, so re-serializing
        // them here was the source of multi-second freezes.
        var patch = new Dictionary<string, object?>
        {
            ["statusMessage"]       = _viewModel.StatusMessage,
            ["isScanning"]          = _viewModel.IsScanning,
            ["isNetworkActive"]     = _viewModel.IsNetworkActive,
            ["isScanningPeers"]     = _viewModel.IsScanningPeers,
            ["localIpAddress"]      = _viewModel.LocalIpAddress,
            ["manualPeerIp"]        = _viewModel.ManualPeerIp,
            ["isTransferring"]      = _viewModel.IsTransferring,
            ["firewallConfigured"]  = _viewModel.FirewallConfigured,
            ["isAdmin"]             = _viewModel.IsAdmin,
            ["isWindows"]           = _viewModel.IsWindows,
            ["highSpeedMode"]       = _viewModel.HighSpeedMode,
            ["isLogVisible"]        = _viewModel.IsLogVisible,
            ["isQueueProcessing"]   = _viewModel.IsQueueProcessing,
            ["showSpeedInMbps"]     = _viewModel.ShowSpeedInMbps,
            ["lastError"]           = _viewModel.LastError,

            // Transfer progress
            ["currentTransferGameName"]          = _viewModel.CurrentTransferGameName,
            ["currentTransferProgress"]          = _viewModel.CurrentTransferProgress,
            ["currentTransferFile"]              = _viewModel.CurrentTransferFile,
            ["currentTransferSpeed"]             = _viewModel.CurrentTransferSpeed,
            ["currentTransferTimeRemaining"]     = _viewModel.CurrentTransferTimeRemaining,
            ["currentTransferTotalBytes"]        = _viewModel.CurrentTransferTotalBytes,
            ["currentTransferDownloadedBytes"]   = _viewModel.CurrentTransferDownloadedBytes,
            ["currentTransferFormattedProgress"] = _viewModel.CurrentTransferFormattedProgress,

            // Selections are NOT pushed here — they are managed via explicit
            // commands (SelectLocalGame, SelectPeer, etc.) and only included in
            // the initial full-state push.  Re-pushing them on every scalar
            // PropertyChanged would overwrite UI-local selections.

            // Xbox transfer
            ["xboxTransfer"]         = _viewModel.XboxTransfer,
            ["isXboxTransferActive"] = _viewModel.IsXboxTransferActive,
            ["xboxSourcePath"]       = _viewModel.XboxSourcePath,
            ["xboxDestinationPath"]  = _viewModel.XboxDestinationPath,
            ["xboxRootPath"]         = _viewModel.XboxRootPath,
            ["isElevated"]           = ElevationHelper.IsElevated(),

            // Skeleton capture
            ["isSkeletonWatching"]   = _viewModel.IsSkeletonWatching,
            ["skeletonCapturing"]    = _viewModel.SkeletonCapturing,
            ["skeletonDropFolder"]   = _viewModel.SkeletonDropFolder,

            // LAN cache proxy
            ["isCacheProxyRunning"]  = _viewModel.IsCacheProxyRunning,
            ["cacheProxyDir"]        = _viewModel.CacheProxyDir,
            ["cacheProxyStats"]      = _viewModel.CacheProxyStats,
        };

        var json = JsonSerializer.Serialize(patch, JsonOptions);
        await ExecuteJavaScriptAsync($"window.__updateState({json});");
    }

    private async Task PushCollectionChangeAsync<T>(string collectionName, ObservableCollection<T> collection)
    {
        if (_webView == null) return;

        var items = collection.ToList();
        var patch = new Dictionary<string, object> { [collectionName] = items };
        var json = JsonSerializer.Serialize(patch, JsonOptions);

        await ExecuteJavaScriptAsync($"window.__updateState({json});");
    }

    private object GetFullState()
    {
        return new
        {
            statusMessage = _viewModel.StatusMessage,
            isScanning = _viewModel.IsScanning,
            isNetworkActive = _viewModel.IsNetworkActive,
            isScanningPeers = _viewModel.IsScanningPeers,
            localIpAddress = _viewModel.LocalIpAddress,
            manualPeerIp = _viewModel.ManualPeerIp,
            isTransferring = _viewModel.IsTransferring,
            firewallConfigured = _viewModel.FirewallConfigured,
            isAdmin = _viewModel.IsAdmin,
            isWindows = _viewModel.IsWindows,
            highSpeedMode = _viewModel.HighSpeedMode,
            isLogVisible = _viewModel.IsLogVisible,
            isQueueProcessing = _viewModel.IsQueueProcessing,
            showSpeedInMbps = _viewModel.ShowSpeedInMbps,
            lastError = _viewModel.LastError,

            // Transfer progress
            currentTransferGameName = _viewModel.CurrentTransferGameName,
            currentTransferProgress = _viewModel.CurrentTransferProgress,
            currentTransferFile = _viewModel.CurrentTransferFile,
            currentTransferSpeed = _viewModel.CurrentTransferSpeed,
            currentTransferTimeRemaining = _viewModel.CurrentTransferTimeRemaining,
            currentTransferTotalBytes = _viewModel.CurrentTransferTotalBytes,
            currentTransferDownloadedBytes = _viewModel.CurrentTransferDownloadedBytes,
            currentTransferFormattedProgress = _viewModel.CurrentTransferFormattedProgress,

            // Collections
            localGames = _viewModel.LocalGames.ToList(),
            networkPeers = _viewModel.NetworkPeers.ToList(),
            availableSyncs = _viewModel.AvailableSyncs.ToList(),
            availableFromPeers = _viewModel.AvailableFromPeers.ToList(),
            incompleteTransfers = _viewModel.IncompleteTransfers.ToList(),
            downloadQueue = _viewModel.DownloadQueue.ToList(),
            logMessages = _viewModel.LogMessages.ToList(),

            // Selections
            selectedLocalGame = _viewModel.SelectedLocalGame,
            selectedPeer = _viewModel.SelectedPeer,
            selectedSyncItem = _viewModel.SelectedSyncItem,
            selectedPeerGame = _viewModel.SelectedPeerGame,
            selectedIncompleteTransfer = _viewModel.SelectedIncompleteTransfer,
            currentQueueItem = _viewModel.CurrentQueueItem,

            // External drives
            drives = _viewModel.Drives.ToList(),
            crossLocationGames = _viewModel.CrossLocationGames.ToList(),

            // Xbox transfer
            xboxTransfer = _viewModel.XboxTransfer,
            isXboxTransferActive = _viewModel.IsXboxTransferActive,
            xboxOverlayGames = _viewModel.XboxOverlayGames.ToList(),
            xboxSourcePath = _viewModel.XboxSourcePath,
            xboxDestinationPath = _viewModel.XboxDestinationPath,
            xboxRootPath = _viewModel.XboxRootPath,
            isElevated = ElevationHelper.IsElevated(),

            // Skeleton capture
            isSkeletonWatching = _viewModel.IsSkeletonWatching,
            skeletonCapturing = _viewModel.SkeletonCapturing,
            skeletonDropFolder = _viewModel.SkeletonDropFolder,
            skeletonCaptures = _viewModel.SkeletonCaptures.ToList(),
            skeletonLog = _viewModel.SkeletonLog.ToList(),

            // LAN cache proxy
            isCacheProxyRunning = _viewModel.IsCacheProxyRunning,
            cacheProxyDir = _viewModel.CacheProxyDir,
            cacheProxyStats = _viewModel.CacheProxyStats,

            externalLibraries = _viewModel.Settings.ExternalLibraries.Select(lib => new
            {
                id = lib.Id.ToString(),
                displayName = lib.DisplayName,
                rootPath = lib.RootPath,
                driveSerial = lib.DriveSerial,
                isRemovable = lib.IsRemovable,
                scanSubfolders = lib.ScanSubfolders,
            }).ToList(),
        };
    }

    private void OnWebMessageReceived(object? sender, WebViewMessageReceivedEventArgs e)
    {
        try
        {
            var message = e.Message ?? string.Empty;
            var jsonMessage = JsonSerializer.Deserialize<JsonElement>(message);
            if (jsonMessage.TryGetProperty("cmd", out var cmdElement))
            {
                var cmd = cmdElement.GetString();
                var payload = jsonMessage.TryGetProperty("payload", out var p) ? p : (JsonElement?)null;

                _ = HandleCommandAsync(cmd, payload);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"InteropBridge error: {ex}");
        }
    }

    private async Task HandleCommandAsync(string? cmd, JsonElement? payload)
    {
        if (cmd == null) return;

        try
        {
            switch (cmd)
            {
                // WebUI readiness handshake — push the full initial state
                // as soon as the React app signals it has mounted and can
                // receive data.  This replaces the fragile 500 ms delay.
                case "WebUIReady":
                    await PushInitialStateAsync();
                    break;

                // Toolbar commands
                case "ScanLocalGames":
                    if (_viewModel.ScanLocalGamesCommand.CanExecute(null))
                        _viewModel.ScanLocalGamesCommand.Execute(null);
                    break;

                case "StartNetwork":
                    if (_viewModel.StartNetworkCommand.CanExecute(null))
                        _viewModel.StartNetworkCommand.Execute(null);
                    break;

                case "StopNetwork":
                    if (_viewModel.StopNetworkCommand.CanExecute(null))
                        _viewModel.StopNetworkCommand.Execute(null);
                    break;

                case "ScanForPeers":
                    if (_viewModel.ScanForPeersCommand.CanExecute(null))
                        _viewModel.ScanForPeersCommand.Execute(null);
                    break;

                case "ConfigureFirewall":
                    if (_viewModel.ConfigureFirewallCommand.CanExecute(null))
                        _viewModel.ConfigureFirewallCommand.Execute(null);
                    break;

                case "CopyLocalIp":
                    if (_viewModel.CopyLocalIpToClipboardCommand.CanExecute(null))
                        _viewModel.CopyLocalIpToClipboardCommand.Execute(null);
                    break;

                // Peer commands
                case "ConnectManualIp":
                    if (payload?.TryGetProperty("ip", out var ipElement) == true)
                    {
                        _viewModel.ManualPeerIp = ipElement.GetString() ?? "";
                        if (_viewModel.ConnectToManualIpCommand.CanExecute(null))
                            _viewModel.ConnectToManualIpCommand.Execute(null);
                    }
                    break;

                case "TestConnection":
                    if (_viewModel.TestConnectionToPeerCommand.CanExecute(null))
                        _viewModel.TestConnectionToPeerCommand.Execute(null);
                    break;

                case "RefreshPeers":
                    if (_viewModel.RefreshAllPeersCommand.CanExecute(null))
                        _viewModel.RefreshAllPeersCommand.Execute(null);
                    break;

                case "CopyPeerIp":
                    if (_viewModel.CopyPeerIpCommand.CanExecute(null))
                        _viewModel.CopyPeerIpCommand.Execute(null);
                    break;

                // Transfer commands
                case "StartSync":
                    if (_viewModel.StartSyncCommand.CanExecute(null))
                        _viewModel.StartSyncCommand.Execute(null);
                    break;

                case "DownloadNewGame":
                    if (_viewModel.DownloadNewGameCommand.CanExecute(null))
                        _viewModel.DownloadNewGameCommand.Execute(null);
                    break;

                case "PauseTransfer":
                    if (_viewModel.PauseTransferCommand.CanExecute(null))
                        _viewModel.PauseTransferCommand.Execute(null);
                    break;

                case "StopTransfer":
                    if (_viewModel.StopTransferCommand.CanExecute(null))
                        _viewModel.StopTransferCommand.Execute(null);
                    break;

                case "ToggleSpeedUnit":
                    if (_viewModel.ToggleSpeedUnitCommand.CanExecute(null))
                        _viewModel.ToggleSpeedUnitCommand.Execute(null);
                    break;

                case "AddAllUpdatesToQueue":
                    if (_viewModel.AddAllUpdatesToQueueCommand.CanExecute(null))
                        _viewModel.AddAllUpdatesToQueueCommand.Execute(null);
                    break;

                // Incomplete transfer commands
                case "ResumeTransfer":
                    if (_viewModel.ResumeTransferCommand.CanExecute(null))
                        _viewModel.ResumeTransferCommand.Execute(null);
                    break;

                case "AddAllIncompleteToQueue":
                    if (_viewModel.AddAllIncompleteToQueueCommand.CanExecute(null))
                        _viewModel.AddAllIncompleteToQueueCommand.Execute(null);
                    break;

                case "DeleteIncompleteTransfer":
                    if (_viewModel.DeleteIncompleteTransferCommand.CanExecute(null))
                        _viewModel.DeleteIncompleteTransferCommand.Execute(null);
                    break;

                // Queue commands
                case "StartQueue":
                    if (_viewModel.StartQueueCommand.CanExecute(null))
                        _viewModel.StartQueueCommand.Execute(null);
                    break;

                case "PauseQueue":
                    if (_viewModel.PauseQueueCommand.CanExecute(null))
                        _viewModel.PauseQueueCommand.Execute(null);
                    break;

                case "ClearQueue":
                    if (_viewModel.ClearQueueCommand.CanExecute(null))
                        _viewModel.ClearQueueCommand.Execute(null);
                    break;

                case "RetryFailedAndPaused":
                    if (_viewModel.RetryFailedAndPausedCommand.CanExecute(null))
                        _viewModel.RetryFailedAndPausedCommand.Execute(null);
                    break;

                case "MoveQueueItemUp":
                    if (payload?.TryGetProperty("appId", out var appIdElem) == true)
                    {
                        var appId = appIdElem.GetString();
                        var item = _viewModel.DownloadQueue.FirstOrDefault(q => q.GameAppId == appId);
                        if (item != null && _viewModel.MoveQueueItemUpCommand.CanExecute(item))
                            _viewModel.MoveQueueItemUpCommand.Execute(item);
                    }
                    break;

                case "MoveQueueItemDown":
                    if (payload?.TryGetProperty("appId", out var appIdElem2) == true)
                    {
                        var appId = appIdElem2.GetString();
                        var item = _viewModel.DownloadQueue.FirstOrDefault(q => q.GameAppId == appId);
                        if (item != null && _viewModel.MoveQueueItemDownCommand.CanExecute(item))
                            _viewModel.MoveQueueItemDownCommand.Execute(item);
                    }
                    break;

                case "RetryQueueItem":
                    if (payload?.TryGetProperty("appId", out var retryAppId) == true)
                    {
                        var appId = retryAppId.GetString();
                        if (!string.IsNullOrEmpty(appId))
                            _viewModel.RetryQueueItem(appId);
                    }
                    break;

                case "RemoveFromQueue":
                    if (payload?.TryGetProperty("appId", out var appIdElem3) == true)
                    {
                        var appId = appIdElem3.GetString();
                        var item = _viewModel.DownloadQueue.FirstOrDefault(q => q.GameAppId == appId);
                        if (item != null && _viewModel.RemoveFromQueueCommand.CanExecute(item))
                            _viewModel.RemoveFromQueueCommand.Execute(item);
                    }
                    break;

                // Selection commands
                case "SelectLocalGame":
                    if (payload?.TryGetProperty("appId", out var gameAppId) == true)
                    {
                        var game = _viewModel.LocalGames.FirstOrDefault(g => g.AppId == gameAppId.GetString());
                        _viewModel.SelectedLocalGame = game;
                    }
                    break;

                case "SelectPeer":
                    if (payload?.TryGetProperty("peerId", out var peerIdElem) == true)
                    {
                        var peer = _viewModel.NetworkPeers.FirstOrDefault(p => p.PeerId == peerIdElem.GetString());
                        _viewModel.SelectedPeer = peer;
                    }
                    break;

                case "SelectSyncItem":
                    if (payload?.TryGetProperty("appId", out var syncAppId) == true)
                    {
                        var item = _viewModel.AvailableSyncs.FirstOrDefault(s => s.RemoteGame?.AppId == syncAppId.GetString());
                        _viewModel.SelectedSyncItem = item;
                    }
                    break;

                case "SelectPeerGame":
                    if (payload?.TryGetProperty("appId", out var peerGameAppId) == true)
                    {
                        var game = _viewModel.AvailableFromPeers.FirstOrDefault(g => g.AppId == peerGameAppId.GetString());
                        _viewModel.SelectedPeerGame = game;
                        // Selections are excluded from the debounced push, so push explicitly.
                        var selJson = JsonSerializer.Serialize(new { selectedPeerGame = game }, JsonOptions);
                        await ExecuteJavaScriptAsync($"window.__updateState({selJson});");
                    }
                    break;

                case "SelectIncompleteTransfer":
                    if (payload?.TryGetProperty("appId", out var incompAppId) == true)
                    {
                        var item = _viewModel.IncompleteTransfers.FirstOrDefault(t => t.GameAppId == incompAppId.GetString());
                        _viewModel.SelectedIncompleteTransfer = item;
                    }
                    break;

                // Game context menu commands
                case "OpenGameFolder":
                    {
                        // Prefer the unique install path: the same game can exist on two drives
                        // with the same AppId (e.g. a Steam title installed once and copied to an
                        // external drive), so matching by AppId alone always opened the first one.
                        GameInfo? game = null;
                        if (payload?.TryGetProperty("installPath", out var folderPath) == true)
                        {
                            var path = folderPath.GetString();
                            if (!string.IsNullOrEmpty(path))
                                game = _viewModel.LocalGames.FirstOrDefault(g => g.InstallPath == path);
                        }
                        if (game == null && payload?.TryGetProperty("appId", out var folderGameAppId) == true)
                            game = _viewModel.LocalGames.FirstOrDefault(g => g.AppId == folderGameAppId.GetString());
                        if (game != null && _viewModel.OpenGameFolderCommand.CanExecute(game))
                            _viewModel.OpenGameFolderCommand.Execute(game);
                    }
                    break;

                case "ToggleGameVisibility":
                    if (payload?.TryGetProperty("appId", out var visGameAppId) == true)
                    {
                        var game = _viewModel.LocalGames.FirstOrDefault(g => g.AppId == visGameAppId.GetString());
                        if (game != null && _viewModel.ToggleGameVisibilityCommand.CanExecute(game))
                            _viewModel.ToggleGameVisibilityCommand.Execute(game);
                    }
                    break;

                // Settings commands
                case "OpenSettings":
                    await PushSettingsAsync();
                    break;

                case "SaveSettings":
                    if (payload.HasValue)
                        await HandleSaveSettingsAsync(payload.Value);
                    break;

                case "BrowseEpicFolder":
                    await HandleBrowseEpicFolderAsync();
                    break;

                case "UnhideAllGames":
                    _viewModel.Settings.HiddenGameIds.Clear();
                    _viewModel.Settings.Save();
                    _viewModel.ApplySettingsChanges();
                    await PushSettingsAsync();
                    break;

                case "ToggleHighSpeedMode":
                    if (_viewModel.ToggleHighSpeedModeCommand.CanExecute(null))
                        _viewModel.ToggleHighSpeedModeCommand.Execute(null);
                    break;

                case "ToggleLog":
                    if (_viewModel.ToggleLogCommand.CanExecute(null))
                        _viewModel.ToggleLogCommand.Execute(null);
                    break;

                case "ClearLog":
                    if (_viewModel.ClearLogCommand.CanExecute(null))
                        _viewModel.ClearLogCommand.Execute(null);
                    break;

                case "ShowTroubleshoot":
                    if (_viewModel.ShowTroubleshootInfoCommand.CanExecute(null))
                        _viewModel.ShowTroubleshootInfoCommand.Execute(null);
                    break;

                // External drive / multi-drive commands
                case "ListDrives":
                    await HandleListDrivesAsync();
                    break;

                case "BrowseDriveFolder":
                    await HandleBrowseDriveFolderAsync();
                    break;

                case "AddExternalLibrary":
                    if (payload.HasValue &&
                        payload.Value.TryGetProperty("rootPath", out var rootPathEl) &&
                        payload.Value.TryGetProperty("displayName", out var displayNameEl))
                    {
                        var rootPath = rootPathEl.GetString() ?? string.Empty;
                        var displayName = displayNameEl.GetString() ?? string.Empty;
                        await _viewModel.AddExternalLibraryAsync(rootPath, displayName);
                        await PushExternalLibrariesAsync();
                        await HandleCompareGameLocationsAsync();
                    }
                    break;

                case "RemoveExternalLibrary":
                    if (payload.HasValue &&
                        payload.Value.TryGetProperty("id", out var libIdEl) &&
                        Guid.TryParse(libIdEl.GetString(), out var libGuid))
                    {
                        await _viewModel.RemoveExternalLibraryAsync(libGuid);
                        await PushExternalLibrariesAsync();
                    }
                    break;

                case "ScanExternalLibraries":
                    if (_viewModel.ScanExternalLibrariesCommand.CanExecute(null))
                        _viewModel.ScanExternalLibrariesCommand.Execute(null);
                    break;

                case "CompareGameLocations":
                    await HandleCompareGameLocationsAsync();
                    break;

                case "StartLocalCopy":
                    if (!payload.HasValue)
                    {
                        _viewModel.AddLogPublic("StartLocalCopy: missing payload", LogMessageType.Error);
                    }
                    else if (!payload.Value.TryGetProperty("appId", out var copyAppIdEl))
                    {
                        _viewModel.AddLogPublic("StartLocalCopy: missing appId", LogMessageType.Error);
                    }
                    else if (!payload.Value.TryGetProperty("libraryId", out var copyLibIdEl))
                    {
                        _viewModel.AddLogPublic("StartLocalCopy: missing libraryId", LogMessageType.Error);
                    }
                    else if (!Guid.TryParse(copyLibIdEl.GetString(), out var copyLibGuid))
                    {
                        _viewModel.AddLogPublic($"StartLocalCopy: libraryId is not a GUID ({copyLibIdEl.GetString()})", LogMessageType.Error);
                    }
                    else
                    {
                        var copyAppId = copyAppIdEl.GetString() ?? string.Empty;
                        CopyDirection? overrideDir = null;
                        if (payload.Value.TryGetProperty("direction", out var dirEl)
                            && dirEl.ValueKind == JsonValueKind.String
                            && Enum.TryParse<CopyDirection>(dirEl.GetString(), out var parsedDir))
                        {
                            overrideDir = parsedDir;
                        }
                        // Optional explicit destination (from the "choose destination" chooser button).
                        string? copyTargetRoot = payload.Value.TryGetProperty("targetRoot", out var trEl)
                            && trEl.ValueKind == JsonValueKind.String
                            ? trEl.GetString() : null;
                        _viewModel.AddLogPublic($"StartLocalCopy received: appId={copyAppId}, libraryId={copyLibGuid}, direction={overrideDir?.ToString() ?? "auto"}, target={copyTargetRoot ?? "auto"}", LogMessageType.Info);
                        await _viewModel.StartLocalCopyAsync(copyAppId, copyLibGuid, overrideDir, copyTargetRoot);
                    }
                    break;

                case "BrowseCopyDestination":
                    await HandleBrowseCopyDestinationAsync(payload);
                    break;

                // Xbox transfer commands (receiver + sender)
                case "StartXboxTransfer":
                    if (payload?.TryGetProperty("sourcePath", out var xboxSourceEl) == true)
                    {
                        var sourcePath = xboxSourceEl.GetString() ?? "";
                        string? xboxRoot = payload?.TryGetProperty("xboxRoot", out var xboxRootEl) == true
                            ? xboxRootEl.GetString()
                            : null;
                        bool force = payload?.TryGetProperty("force", out var xboxForceEl) == true
                            && xboxForceEl.ValueKind == JsonValueKind.True;
                        var xboxTransferArgs = (sourcePath, xboxRoot, force);
                        if (_viewModel.StartXboxTransferCommand.CanExecute(xboxTransferArgs))
                            await _viewModel.StartXboxTransferCommand.ExecuteAsync(xboxTransferArgs);
                    }
                    break;

                case "CancelXboxTransfer":
                    if (_viewModel.CancelXboxTransferCommand.CanExecute(null))
                        _viewModel.CancelXboxTransferCommand.Execute(null);
                    break;

                case "PauseXboxTransfer":
                    if (_viewModel.PauseXboxTransferCommand.CanExecute(null))
                        _viewModel.PauseXboxTransferCommand.Execute(null);
                    break;

                case "ResumeXboxTransfer":
                    if (_viewModel.ResumeXboxTransferCommand.CanExecute(null))
                        _viewModel.ResumeXboxTransferCommand.Execute(null);
                    break;

                case "DismissXboxTransfer":
                    if (_viewModel.DismissXboxTransferCommand.CanExecute(null))
                        _viewModel.DismissXboxTransferCommand.Execute(null);
                    break;

                case "ToggleSkeletonWatcher":
                    if (_viewModel.ToggleSkeletonWatcherCommand.CanExecute(null))
                        _viewModel.ToggleSkeletonWatcherCommand.Execute(null);
                    break;

                case "OpenSkeletonDropFolder":
                    if (_viewModel.OpenSkeletonDropFolderCommand.CanExecute(null))
                        _viewModel.OpenSkeletonDropFolderCommand.Execute(null);
                    break;

                case "RefreshSkeletons":
                    if (_viewModel.RefreshSkeletonsCommand.CanExecute(null))
                        _viewModel.RefreshSkeletonsCommand.Execute(null);
                    break;

                case "CaptureMissingSkeletons":
                    if (_viewModel.CaptureMissingSkeletonsCommand.CanExecute(null))
                        _viewModel.CaptureMissingSkeletonsCommand.Execute(null);
                    break;

                case "RestoreSkeleton":
                    if (payload?.TryGetProperty("name", out var skelRestoreEl) == true)
                    {
                        var skelName = skelRestoreEl.GetString() ?? "";
                        if (_viewModel.RestoreSkeletonCommand.CanExecute(skelName))
                            await _viewModel.RestoreSkeletonCommand.ExecuteAsync(skelName);
                    }
                    break;

                case "ReconstructSkeleton":
                    if (payload?.TryGetProperty("name", out var skelReconEl) == true)
                        await HandleReconstructSkeletonAsync(skelReconEl.GetString() ?? "");
                    break;

                case "ToggleCacheProxy":
                    if (_viewModel.ToggleCacheProxyCommand.CanExecute(null))
                        await _viewModel.ToggleCacheProxyCommand.ExecuteAsync(null);
                    break;

                case "ReceiveXboxFromDrive":
                    await HandleReceiveXboxFromDriveAsync();
                    break;

                case "BrowseXboxSource":
                    await HandleBrowseXboxSourceAsync();
                    break;

                case "StartXboxStage":
                    if (payload?.TryGetProperty("sourcePath", out var stageSourceEl) == true)
                    {
                        var sourcePath = stageSourceEl.GetString() ?? "";
                        if (_viewModel.StartXboxStageCommand.CanExecute(sourcePath))
                            await _viewModel.StartXboxStageCommand.ExecuteAsync(sourcePath);
                    }
                    break;

                case "CompleteXboxStage":
                    if (payload?.TryGetProperty("destinationPath", out var stageDestEl) == true)
                    {
                        var destPath = stageDestEl.GetString() ?? "";
                        if (_viewModel.CompleteXboxStageCommand.CanExecute(destPath))
                            await _viewModel.CompleteXboxStageCommand.ExecuteAsync(destPath);
                    }
                    break;

                case "CancelXboxStage":
                    if (_viewModel.CancelXboxStageCommand.CanExecute(null))
                        _viewModel.CancelXboxStageCommand.Execute(null);
                    break;

                case "BrowseXboxDestination":
                    await HandleBrowseXboxDestinationAsync();
                    break;

                case "BrowseXboxRoot":
                    await HandleBrowseXboxRootAsync();
                    break;

                case "BrowseXboxCacheRoot":
                    {
                        string? retry = payload?.TryGetProperty("retry", out var retryEl) == true
                            ? retryEl.GetString() : null;
                        await HandleBrowseXboxCacheRootAsync(retry);
                    }
                    break;

                case "SetXboxPath":
                    if (payload.HasValue)
                    {
                        if (payload.Value.TryGetProperty("xboxSourcePath", out var xsp))
                            _viewModel.XboxSourcePath = xsp.GetString() ?? "";
                        if (payload.Value.TryGetProperty("xboxDestinationPath", out var xdp))
                            _viewModel.XboxDestinationPath = xdp.GetString() ?? "";
                        if (payload.Value.TryGetProperty("xboxRootPath", out var xrp))
                        {
                            var root = xrp.GetString() ?? "";
                            _viewModel.XboxRootPath = root;
                            _viewModel.Settings.XboxRootPath = string.IsNullOrWhiteSpace(root) ? null : root;
                            _viewModel.Settings.Save();
                        }
                    }
                    break;

                case "PrepareXboxNetwork":
                    if (payload?.TryGetProperty("sourcePath", out var netSourceEl) == true)
                    {
                        var netSourcePath = netSourceEl.GetString() ?? "";
                        if (_viewModel.PrepareXboxNetworkCommand.CanExecute(netSourcePath))
                            await _viewModel.PrepareXboxNetworkCommand.ExecuteAsync(netSourcePath);
                    }
                    break;

                case "StopXboxNetwork":
                    if (_viewModel.StopXboxNetworkCommand.CanExecute(null))
                        _viewModel.StopXboxNetworkCommand.Execute(null);
                    break;

                case "StartXboxNetworkTransfer":
                    if (payload?.TryGetProperty("peerHost", out var peerHostEl) == true &&
                        payload?.TryGetProperty("peerPort", out var peerPortEl) == true &&
                        payload?.TryGetProperty("gameAppId", out var gameAppIdEl) == true)
                    {
                        string? netXboxRoot = payload?.TryGetProperty("xboxRoot", out var nxrEl) == true
                            ? nxrEl.GetString() : null;
                        bool netForce = payload?.TryGetProperty("force", out var nfEl) == true &&
                                        nfEl.ValueKind == JsonValueKind.True;
                        var netArgs = (peerHostEl.GetString() ?? "", peerPortEl.GetInt32(),
                                       gameAppIdEl.GetString() ?? "", netXboxRoot, netForce);
                        if (_viewModel.StartXboxNetworkTransferCommand.CanExecute(netArgs))
                            await _viewModel.StartXboxNetworkTransferCommand.ExecuteAsync(netArgs);
                    }
                    break;

                case "StartXboxPeerInstall":
                    if (payload?.TryGetProperty("peerHost", out var ppHost) == true &&
                        payload?.TryGetProperty("gameAppId", out var ppGame) == true)
                    {
                        var ppPayload = $"{ppHost.GetString() ?? ""}|{ppGame.GetString() ?? ""}";
                        if (_viewModel.StartXboxPeerInstallCommand.CanExecute(ppPayload))
                            await _viewModel.StartXboxPeerInstallCommand.ExecuteAsync(ppPayload);
                    }
                    break;

                case "CopyXboxGameToDrive":
                    if (payload?.TryGetProperty("appId", out var cxAppId) == true &&
                        payload?.TryGetProperty("libraryId", out var cxLib) == true)
                    {
                        var cxArgs = (cxAppId.GetString() ?? "", cxLib.GetString() ?? "");
                        if (_viewModel.CopyXboxGameToDriveCommand.CanExecute(cxArgs))
                            await _viewModel.CopyXboxGameToDriveCommand.ExecuteAsync(cxArgs);
                    }
                    break;

                case "RequestElevation":
                    var forwardArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
                    if (ElevationHelper.RelaunchAsAdmin(forwardArgs))
                    {
                        // Signal UI that relaunch is happening, then current process should exit
                        var json = JsonSerializer.Serialize(new { relaunching = true }, JsonOptions);
                        await ExecuteJavaScriptAsync($"window.__updateState({json});");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Command execution error: {ex}");
            try { _viewModel.AddLogPublic($"Command '{cmd}' failed: {ex.Message}", LogMessageType.Error); }
            catch { }
            try { _viewModel.Notify("error", "Something went wrong", ex.Message); }
            catch { }
        }
    }

    private async Task HandleReconstructSkeletonAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var topLevel = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Choose where to reconstruct \"{name}\" (e.g. an external drive cache root)",
            AllowMultiple = false
        });

        if (result.Count > 0)
            await _viewModel.RestoreSkeletonToFolderAsync(name, result[0].Path.LocalPath);
    }

    /// <summary>Browse for the drive/folder a game was copied to, then serve a Smart (updatable) copy via the
    /// proxy. If it isn't a Smart copy, fall back to the Basic/overlay receive modal.</summary>
    private async Task HandleReceiveXboxFromDriveAsync()
    {
        var topLevel = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the drive or folder you copied the Xbox game to",
            AllowMultiple = false
        });
        if (result.Count == 0) return;

        bool handled = await _viewModel.ReceiveXboxFromDriveAsync(result[0].Path.LocalPath);
        if (!handled)
            await ExecuteJavaScriptAsync("window.__openXboxReceiveModal && window.__openXboxReceiveModal();");
    }

    private async Task HandleBrowseXboxSourceAsync()
    {
        var topLevel = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Xbox staged game folder (must contain transfer-summary.json)",
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            _viewModel.XboxSourcePath = result[0].Path.LocalPath;
        }
    }

    private async Task HandleBrowseXboxDestinationAsync()
    {
        var topLevel = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select destination folder for Xbox staged game (USB/shared drive)",
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            _viewModel.XboxDestinationPath = result[0].Path.LocalPath;
        }
    }

    private async Task HandleBrowseXboxRootAsync()
    {
        var topLevel = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the Xbox install root (e.g. the XboxGames folder on the install drive)",
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            _viewModel.XboxRootPath = result[0].Path.LocalPath;
        }
    }

    /// <summary>Pick + persist the Xbox LAN cache folder, then (optionally) re-run the command that needed it
    /// (auto-retry). Raised from a "no cache folder set" / "proxy didn't start" toast action.</summary>
    private async Task HandleBrowseXboxCacheRootAsync(string? retryCommand)
    {
        var topLevel = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder for the Xbox LAN cache (stores cached game packages)",
            AllowMultiple = false
        });
        if (result.Count == 0) return;

        var path = result[0].Path.LocalPath;

        // Called from the Settings modal (no retry): just fill the form field; the modal's Save persists it,
        // so Cancel still discards. Called from a toast action (retry set): persist immediately + auto-retry.
        if (string.IsNullOrEmpty(retryCommand))
        {
            var json = JsonSerializer.Serialize(path, JsonOptions);
            await ExecuteJavaScriptAsync($"window.__xboxCacheBrowseResult && window.__xboxCacheBrowseResult({json});");
            return;
        }

        _viewModel.Settings.XboxPackageCacheRoot = string.IsNullOrWhiteSpace(path) ? null : path;
        _viewModel.Settings.Save();
        _viewModel.CacheProxyDir = path;
        _viewModel.AddLogPublic($"Xbox cache folder set to {path}", LogMessageType.Info);
        _viewModel.Notify("success", "Cache folder set", path);

        // Auto-retry the original action (e.g. start the proxy) now that the path is set.
        await HandleCommandAsync(retryCommand, null);
    }

    /// <summary>Opens a folder picker for the "Choose another folder…" option in the copy-destination chooser,
    /// then runs the drive→PC copy into the chosen folder.</summary>
    private async Task HandleBrowseCopyDestinationAsync(JsonElement? payload)
    {
        if (payload == null) return;
        if (!payload.Value.TryGetProperty("appId", out var aEl)) return;
        if (!payload.Value.TryGetProperty("libraryId", out var lEl) || !Guid.TryParse(lEl.GetString(), out var libGuid)) return;

        CopyDirection? dir = null;
        if (payload.Value.TryGetProperty("direction", out var dEl) && dEl.ValueKind == JsonValueKind.String
            && Enum.TryParse<CopyDirection>(dEl.GetString(), out var parsed))
            dir = parsed;

        var topLevel = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to install the game",
            AllowMultiple = false
        });
        if (result.Count == 0) return;

        await _viewModel.StartLocalCopyAsync(aEl.GetString() ?? string.Empty, libGuid, dir, result[0].Path.LocalPath);
    }

    private async Task PushSettingsAsync()
    {
        var s = _viewModel.Settings;
        var hiddenGames = _viewModel.LocalGames
            .Where(g => s.HiddenGameIds.Contains(g.AppId))
            .Select(g => new { appId = g.AppId, name = g.Name })
            .OrderBy(x => x.name)
            .ToList();

        var actualStartupState = OperatingSystem.IsWindows() && StartupHelper.IsStartupEnabled();

        var externalLibraries = s.ExternalLibraries.Select(lib => new
        {
            id = lib.Id.ToString(),
            displayName = lib.DisplayName,
            rootPath = lib.RootPath,
            driveSerial = lib.DriveSerial,
            isRemovable = lib.IsRemovable,
            scanSubfolders = lib.ScanSubfolders,
        }).ToList();

        var payload = new
        {
            settings = new
            {
                autoStartNetwork = s.AutoStartNetwork,
                autoUpdateGames = s.AutoUpdateGames,
                autoResumeDownloads = s.AutoResumeDownloads,
                autoUpdateCheckInterval = s.AutoUpdateCheckInterval,
                startWithWindows = OperatingSystem.IsWindows() ? actualStartupState : s.StartWithWindows,
                minimizeToTray = s.MinimizeToTray,
                epicInstallRoot = s.EpicInstallRoot ?? string.Empty,
                xboxRootPath = s.XboxRootPath ?? string.Empty,
                xboxPackageCacheRoot = s.XboxPackageCacheRoot ?? string.Empty,
                xboxSingleCopyAutoStart = s.XboxSingleCopyAutoStart,
                xboxTransferMethod = s.XboxTransferMethod.ToString(),
                captureCpuLimit = s.CaptureCpuLimit.ToString(),
                captureFromCache = s.CaptureFromCache,
                steamGridDbApiKey = s.SteamGridDbApiKey ?? string.Empty,
            },
            hiddenGames,
            externalLibraries,
            isWindows = OperatingSystem.IsWindows(),
            settingsPath = AppSettings.GetSettingsFilePath(),
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await ExecuteJavaScriptAsync($"window.__openSettings && window.__openSettings({json});");
    }

    /// <summary>Forwards a ViewModel-raised notification to the WebUI's global toast/alert system.</summary>
    private async Task PushNotificationAsync(object notification)
    {
        if (_webView == null) return;
        var json = JsonSerializer.Serialize(notification, JsonOptions);
        await ExecuteJavaScriptAsync($"window.__notify && window.__notify({json});");
    }

    private async Task HandleListDrivesAsync()
    {
        var drives = _viewModel.Drives.Select(d => new
        {
            driveLetter = d.DriveLetter,
            volumeLabel = d.VolumeLabel,
            serial = d.Serial,
            isRemovable = d.IsRemovable,
            isAvailable = d.IsAvailable,
        }).ToList();
        var json = JsonSerializer.Serialize(drives, JsonOptions);
        await ExecuteJavaScriptAsync($"window.__driveListResult && window.__driveListResult({json});");
    }

    private async Task HandleBrowseDriveFolderAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow == null)
            return;

        var sp = desktop.MainWindow.StorageProvider;
        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select external drive folder",
            AllowMultiple = false,
        });

        if (folders.Count > 0)
        {
            var path = folders[0].Path.LocalPath;
            var json = JsonSerializer.Serialize(path, JsonOptions);
            await ExecuteJavaScriptAsync($"window.__driveBrowseResult && window.__driveBrowseResult({json});");
        }
    }

    private async Task PushExternalLibrariesAsync()
    {
        var libs = _viewModel.Settings.ExternalLibraries.Select(lib => new
        {
            id = lib.Id.ToString(),
            displayName = lib.DisplayName,
            rootPath = lib.RootPath,
            driveSerial = lib.DriveSerial,
            isRemovable = lib.IsRemovable,
            scanSubfolders = lib.ScanSubfolders,
        }).ToList();
        var json = JsonSerializer.Serialize(new { externalLibraries = libs }, JsonOptions);
        await ExecuteJavaScriptAsync($"window.__updateState && window.__updateState({json});");
    }

    private async Task HandleCompareGameLocationsAsync()
    {
        // Refresh the device's game list first so Compare matches against current
        // names/build IDs rather than whatever was scanned at startup.
        if (_viewModel.ScanLocalGamesCommand is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncScan
            && asyncScan.CanExecute(null))
        {
            await asyncScan.ExecuteAsync(null);
        }

        var crossLocationGames = await _viewModel.CompareGameLocationsAsync();
        var result = crossLocationGames.Select(g => new
        {
            deviceCopy = g.DeviceCopy,
            externalCopy = g.ExternalCopy,
            library = g.Library == null ? null : new
            {
                id = g.Library.Id.ToString(),
                displayName = g.Library.DisplayName,
                rootPath = g.Library.RootPath,
                driveSerial = g.Library.DriveSerial,
                isRemovable = g.Library.IsRemovable,
                scanSubfolders = g.Library.ScanSubfolders,
            },
            direction = g.Direction.ToString(),
            displayName = g.DisplayName,
            appId = g.AppId,
            statusText = g.StatusText,
            statusColor = g.StatusColor,
        }).ToList();
        var json = JsonSerializer.Serialize(result, JsonOptions);
        await ExecuteJavaScriptAsync($"window.__crossLocationGamesResult && window.__crossLocationGamesResult({json});");
    }

    private Task HandleSaveSettingsAsync(JsonElement payload)
    {
        var s = _viewModel.Settings;

        if (payload.TryGetProperty("autoStartNetwork", out var v1)) s.AutoStartNetwork = v1.GetBoolean();
        if (payload.TryGetProperty("autoUpdateGames", out var v2)) s.AutoUpdateGames = v2.GetBoolean();
        if (payload.TryGetProperty("autoResumeDownloads", out var v3)) s.AutoResumeDownloads = v3.GetBoolean();
        if (payload.TryGetProperty("autoUpdateCheckInterval", out var v4) && v4.TryGetInt32(out var interval))
            s.AutoUpdateCheckInterval = Math.Clamp(interval, 5, 1440);
        if (payload.TryGetProperty("minimizeToTray", out var v5)) s.MinimizeToTray = v5.GetBoolean();

        if (payload.TryGetProperty("epicInstallRoot", out var v6))
        {
            var root = v6.GetString()?.Trim();
            s.EpicInstallRoot = string.IsNullOrEmpty(root) ? null : root;
        }

        if (payload.TryGetProperty("xboxRootPath", out var vXbox))
        {
            var xroot = vXbox.GetString()?.Trim();
            s.XboxRootPath = string.IsNullOrEmpty(xroot) ? null : xroot;
            _viewModel.XboxRootPath = xroot ?? string.Empty;
        }

        if (payload.TryGetProperty("xboxPackageCacheRoot", out var vCache))
        {
            var cache = vCache.GetString()?.Trim();
            s.XboxPackageCacheRoot = string.IsNullOrEmpty(cache) ? null : cache;
        }

        if (payload.TryGetProperty("xboxSingleCopyAutoStart", out var vAuto))
            s.XboxSingleCopyAutoStart = vAuto.GetBoolean();

        if (payload.TryGetProperty("captureFromCache", out var vCfc))
            s.CaptureFromCache = vCfc.GetBoolean();

        if (payload.TryGetProperty("steamGridDbApiKey", out var vSgdb))
        {
            var key = vSgdb.GetString()?.Trim();
            s.SteamGridDbApiKey = string.IsNullOrEmpty(key) ? null : key;
        }

        if (payload.TryGetProperty("xboxTransferMethod", out var vMethod))
        {
            var m = vMethod.GetString();
            if (Enum.TryParse<XboxTransferMethod>(m, out var parsed))
                s.XboxTransferMethod = parsed;
        }

        if (payload.TryGetProperty("captureCpuLimit", out var vCpu))
        {
            if (Enum.TryParse<CaptureCpuLimit>(vCpu.GetString(), out var parsedCpu))
                s.CaptureCpuLimit = parsedCpu;
        }

        if (payload.TryGetProperty("startWithWindows", out var v7))
        {
            var enable = v7.GetBoolean();
            s.StartWithWindows = enable;
            if (OperatingSystem.IsWindows())
            {
                StartupHelper.SetStartupEnabled(enable);
            }
        }

        s.Save();
        _viewModel.ApplySettingsChanges();
        // Do NOT push settings back here — that invokes window.__openSettings(...)
        // which reopens the modal in the WebUI. The modal already closes client-side
        // after Save; OpenSettings will re-fetch fresh state on next open.
        return Task.CompletedTask;
    }

    private async Task HandleBrowseEpicFolderAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow == null)
            return;

        var sp = desktop.MainWindow.StorageProvider;
        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Epic Games install folder",
            AllowMultiple = false,
        });

        if (folders.Count > 0)
        {
            var path = folders[0].Path.LocalPath;
            var json = JsonSerializer.Serialize(path, JsonOptions);
            await ExecuteJavaScriptAsync($"window.__epicBrowseResult && window.__epicBrowseResult({json});");
        }
    }

    private async Task ExecuteJavaScriptAsync(string script)
    {
        if (_webView == null) return;

        try
        {
            _ = await _webView.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"JavaScript execution error: {ex}");
        }
    }

    public void Dispose()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        if (_webView != null)
        {
            _webView.WebMessageReceived -= OnWebMessageReceived;
        }
    }
}
