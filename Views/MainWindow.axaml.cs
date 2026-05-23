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

        // Initialize WebView and InteropBridge
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
        // Dispose bridge
        _bridge?.Dispose();

        // If we're explicitly allowing close (for app shutdown), let it through
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        // Reload settings in case they changed
        var settings = AppSettings.Load();

        if (settings.MinimizeToTray && _hasBeenShown)
        {
            // Prevent closing, hide to tray instead
            // Only do this if the window has been shown at least once
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
        }
        else if (!settings.MinimizeToTray)
        {
            // Normal close behavior
            base.OnClosing(e);
        }
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
