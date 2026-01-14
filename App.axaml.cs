using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using GamesLocalShare.Views;
using GamesLocalShare.Services;
using System.Linq;
using System;

namespace GamesLocalShare;

public partial class App : Application
{
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

            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
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
