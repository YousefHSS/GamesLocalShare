using System.Windows;
using GamesLocalShare.Services;

namespace GamesLocalShare;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Check for command line arguments - if we were restarted to configure firewall
        if (e.Args.Contains("--configure-firewall"))
        {
            ConfigureFirewallAndContinue();
            return;
        }

        // Check if firewall rules exist, prompt user if not
        if (!FirewallHelper.CheckFirewallRulesExist())
        {
            PromptForFirewallConfiguration();
        }
    }

    private void PromptForFirewallConfiguration()
    {
        var result = MessageBox.Show(
            "?? Firewall Configuration Required\n\n" +
            "GamesLocalShare needs to open firewall ports to:\n" +
            "• Discover other computers on your network\n" +
            "• Share your game list with peers\n" +
            "• Transfer game files\n\n" +
            "Without this, other computers won't be able to connect to you.\n\n" +
            "Configure firewall now? (Recommended)\n" +
            "(Requires Administrator privileges - you'll be prompted)",
            "GamesLocalShare - First Time Setup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        if (result == MessageBoxResult.Yes)
        {
            if (FirewallHelper.IsRunningAsAdmin())
            {
                // Already admin, configure directly
                ConfigureFirewallAndContinue();
            }
            else
            {
                // Need to restart as admin
                var adminResult = MessageBox.Show(
                    "The application needs to restart with Administrator privileges to configure the firewall.\n\n" +
                    "Click OK to restart as Administrator.",
                    "Administrator Required",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Information);

                if (adminResult == MessageBoxResult.OK)
                {
                    FirewallHelper.RestartAsAdmin();
                    // App will exit and restart
                }
            }
        }
        else
        {
            MessageBox.Show(
                "Firewall was not configured.\n\n" +
                "You can configure it later by clicking the '? Configure Firewall' button.\n\n" +
                "Note: Without firewall configuration, other computers cannot download games from you.",
                "Firewall Not Configured",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void ConfigureFirewallAndContinue()
    {
        var (success, message) = FirewallHelper.AddFirewallRules();
        
        if (success)
        {
            MessageBox.Show(
                "? Firewall configured successfully!\n\n" +
                "Ports opened:\n" +
                "• UDP 45677 - Peer discovery\n" +
                "• TCP 45678 - Game list sharing\n" +
                "• TCP 45679 - File transfers\n\n" +
                "You can now share games with other computers on your network.",
                "Firewall Configured",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                $"? Failed to configure firewall:\n\n{message}\n\n" +
                "You may need to manually add firewall rules.\n\n" +
                "Run this in Administrator PowerShell:\n" +
                "netsh advfirewall firewall add rule name=\"GamesLocalShare\" dir=in action=allow protocol=TCP localport=45678,45679\n" +
                "netsh advfirewall firewall add rule name=\"GamesLocalShare UDP\" dir=in action=allow protocol=UDP localport=45677",
                "Firewall Configuration Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
