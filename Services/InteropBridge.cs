using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        // Subscribe to ViewModel property changes
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null)
            {
                _ = PushStateChangeAsync();
            }
        };

        // Subscribe to collection changes
        SubscribeToCollection(_viewModel.LocalGames, "localGames");
        SubscribeToCollection(_viewModel.NetworkPeers, "networkPeers");
        SubscribeToCollection(_viewModel.AvailableSyncs, "availableSyncs");
        SubscribeToCollection(_viewModel.AvailableFromPeers, "availableFromPeers");
        SubscribeToCollection(_viewModel.IncompleteTransfers, "incompleteTransfers");
        SubscribeToCollection(_viewModel.DownloadQueue, "downloadQueue");
        SubscribeToCollection(_viewModel.LogMessages, "logMessages");

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
        if (e.PropertyName == nameof(GameInfo.CoverImagePath) || e.PropertyName == nameof(GameInfo.CoverImage) || e.PropertyName == nameof(GameInfo.CoverUrl))
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

    private async Task PushStateChangeAsync()
    {
        if (_webView == null) return;

        var state = GetFullState();
        var json = JsonSerializer.Serialize(state, JsonOptions);

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
                    if (payload?.TryGetProperty("appId", out var folderGameAppId) == true)
                    {
                        var game = _viewModel.LocalGames.FirstOrDefault(g => g.AppId == folderGameAppId.GetString());
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
                    if (_viewModel.OpenSettingsCommand.CanExecute(null))
                        _viewModel.OpenSettingsCommand.Execute(null);
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
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Command execution error: {ex}");
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
        if (_webView != null)
        {
            _webView.WebMessageReceived -= OnWebMessageReceived;
        }
    }
}
