using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaWebView;
using GamesLocalShare.Models;
using GamesLocalShare.Services;
using GamesLocalShare.ViewModels;
using System;

namespace GamesLocalShare.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private bool _startMinimized;
    private bool _allowClose;
    private bool _hasBeenShown;
    private bool _initialMinimizeDone;
    private InteropBridge? _bridge;

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new ViewModels.MainViewModel();
        DataContext = viewModel;

        _settings = AppSettings.Load();

        // Check if we should start minimized
        var args = Environment.GetCommandLineArgs();
        _startMinimized = Array.Exists(args, arg => arg == "--minimized");
        _hasBeenShown = !_startMinimized; // If not starting minimized, it will be shown normally

        if (_startMinimized)
        {
            WindowState = WindowState.Minimized;
        }
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Initialize WebView and InteropBridge — ONCE. Avalonia raises Opened again every time the
        // window is re-shown (restoring from the tray calls Show()), and building a second bridge here
        // used to leave two live WebMessageReceived subscriptions, so every WebUI command ran twice
        // (three times after two restores) — e.g. the "Receive Xbox Game" folder picker opening again
        // and again. Reload the page too and the in-page state would be lost on every restore.
        if (_bridge != null) return;

        var webView = this.FindControl<WebView>("MainWebView");
        if (webView != null && DataContext is MainViewModel viewModel)
        {
            _bridge = new InteropBridge(webView, viewModel);

            var webUiPath = Path.Combine(AppContext.BaseDirectory, "Assets", "webui", "index.html");
            if (File.Exists(webUiPath))
            {
                webView.Url = new Uri(webUiPath);
            }
            else
            {
                webView.HtmlContent = $"<html><body style='background:#1E1E1E;color:#fff;font-family:sans-serif;padding:24px;'><h1>Web UI not found</h1><p>Expected at:<br><code>{System.Net.WebUtility.HtmlEncode(webUiPath)}</code></p></body></html>";
            }

            // Initialize the bridge — subscribes to ViewModel changes.
            // The initial state push now happens in response to the
            // "WebUIReady" command sent by the React app once it mounts,
            // so we no longer rely on an arbitrary delay here.
            await _bridge.InitializeAsync();
        }

        // Only hide to tray once on initial startup, not when restoring
        if (_startMinimized && _settings.MinimizeToTray && !_initialMinimizeDone)
        {
            _initialMinimizeDone = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Task.Delay(100).ContinueWith(_ =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        Hide();
                        ShowInTaskbar = false;
                    });
                });
            });
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // If we're explicitly allowing close (for app shutdown), let it through.
        if (_allowClose)
        {
            _bridge?.Dispose();
            base.OnClosing(e);
            return;
        }

        // Reload settings in case they changed
        var settings = AppSettings.Load();

        if (settings.MinimizeToTray && _hasBeenShown)
        {
            // Prevent closing, hide to tray instead (only once the window has been shown).
            // Crucially, DO NOT dispose the bridge here — the window stays alive and must
            // keep its WebView interop working when restored from the tray.
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            return;
        }

        // Real close: tear down the bridge, then let the close proceed.
        _bridge?.Dispose();
        base.OnClosing(e);
    }

    /// <summary>
    /// Restores the window from minimized/tray state
    /// </summary>
    public void RestoreFromTray()
    {
        _hasBeenShown = true;
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>
    /// Allows the window to actually close (for app shutdown)
    /// </summary>
    public void AllowClose()
    {
        _allowClose = true;
    }

    private void MainGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // This event handler is no longer used since the log overlay is now in the React UI
        // Kept for potential future use
    }
}
