using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GamesLocalShare.Models;
using GamesLocalShare.Views;
using GamesLocalShare.Services;
using System.Linq;
using System;
using Avalonia.Platform;

namespace GamesLocalShare;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Remove Avalonia's data validation to avoid conflicts with CommunityToolkit.Mvvm
        // which uses INotifyDataErrorInfo
        var dataValidationPlugins = BindingPlugins.DataValidators
            .OfType<DataAnnotationsValidationPlugin>()
            .ToArray();
        foreach (var plugin in dataValidationPlugins)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Check for command line arguments - if we were restarted to configure firewall
            var args = desktop.Args ?? Array.Empty<string>();
            
            if (args.Contains("--configure-firewall"))
            {
                // We're running as admin to configure firewall (Windows only)
                if (OperatingSystem.IsWindows())
                {
                    ConfigureFirewallAndContinue();
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                // Check if firewall rules exist, prompt user if not
                if (!FirewallHelper.CheckFirewallRulesExist())
                {
                    PromptForFirewallConfiguration();
                }
            }

            _mainWindow = new MainWindow();
            desktop.MainWindow = _mainWindow;
            
            // Setup tray icon
            SetupTrayIcon(desktop);
            
            // Handle shutdown to cleanup tray icon
            desktop.ShutdownRequested += (s, e) =>
            {
                _trayIcon?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var settings = AppSettings.Load();
        
        // Create tray icon with context menu
        var trayMenu = new NativeMenu();
        
        var showItem = new NativeMenuItem("Show Window");
        showItem.Click += (s, e) => ShowMainWindow();
        trayMenu.Add(showItem);
        
        trayMenu.Add(new NativeMenuItemSeparator());
        
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (s, e) =>
        {
            _mainWindow?.AllowClose();
            _trayIcon?.Dispose();
            desktop.Shutdown();
        };
        trayMenu.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            ToolTipText = "Games Local Share",
            Menu = trayMenu,
            IsVisible = true
        };

        // Load icon from Icons/app.ico
        try
        {
            // Try to load from avares:// first (embedded resource)
            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://GamesLocalShare/Icons/app.ico"));
                _trayIcon.Icon = new WindowIcon(stream);
                System.Diagnostics.Debug.WriteLine("Tray icon loaded from avares://");
            }
            catch
            {
                // Fallback to file path if avares:// doesn't work
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Icons", "app.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    using var stream = System.IO.File.OpenRead(iconPath);
                    _trayIcon.Icon = new WindowIcon(stream);
                    System.Diagnostics.Debug.WriteLine($"Tray icon loaded from file: {iconPath}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Icon file not found at: {iconPath}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load tray icon: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
        }
        
        // Add to TrayIcons collection
        if (TrayIcon.GetIcons(this) is { } icons)
        {
            icons.Add(_trayIcon);
        }
        
        // Double-click on tray icon shows window
        _trayIcon.Clicked += (s, e) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_mainWindow != null)
            {
                _mainWindow.RestoreFromTray();
            }
        });
    }

    private void PromptForFirewallConfiguration()
    {
        // On Windows, we can prompt for firewall configuration
        // For now, we'll just log it - the UI will show the firewall button
        System.Diagnostics.Debug.WriteLine("Firewall not configured - user will need to configure via UI");
    }

    private void ConfigureFirewallAndContinue()
    {
        if (!FirewallHelper.IsRunningAsAdmin())
        {
            System.Diagnostics.Debug.WriteLine("Not running as Administrator!");
            return;
        }

        var (success, message) = FirewallHelper.AddFirewallRules();
        
        if (success)
        {
            System.Diagnostics.Debug.WriteLine("Firewall configured successfully!");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"Firewall configuration failed: {message}");
        }
    }
}
