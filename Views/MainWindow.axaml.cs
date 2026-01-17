using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GamesLocalShare.Models;
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
    
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new ViewModels.MainViewModel();
        
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

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        
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
        // Close log panel if clicking outside of it
        if (DataContext is MainViewModel vm && vm.IsLogVisible)
        {
            var logBorder = this.FindControl<Border>("LogBorder");
            if (logBorder != null)
            {
                var position = e.GetPosition(logBorder);
                var bounds = logBorder.Bounds;
                
                // Check if click is outside the log border
                if (position.X < 0 || position.Y < 0 || 
                    position.X > bounds.Width || position.Y > bounds.Height)
                {
                    vm.IsLogVisible = false;
                }
            }
        }
    }
}
