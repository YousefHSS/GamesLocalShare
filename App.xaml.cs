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
            // We're running as admin to configure firewall
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
            "?? FIREWALL CONFIGURATION REQUIRED\n\n" +
            "GamesLocalShare needs to configure Windows Firewall so other computers can:\n\n" +
            "• Discover this computer on the network\n" +
            "• See your game list\n" +
            "• Download games FROM you\n\n" +
            "WITHOUT THIS: You can see others, but they CANNOT connect to you!\n\n" +
            "Configure firewall now? (Recommended)\n" +
            "(You will be prompted for Administrator permission)",
            "GamesLocalShare - Firewall Setup Required",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        if (result == MessageBoxResult.Yes)
        {
            if (FirewallHelper.IsRunningAsAdmin())
            {
                ConfigureFirewallAndContinue();
            }
            else
            {
                try
                {
                    FirewallHelper.RestartAsAdmin();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Failed to restart as Administrator:\n\n{ex.Message}\n\n" +
                        "Please right-click the application and select 'Run as administrator'.",
                        "Administrator Access Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }
        else
        {
            MessageBox.Show(
                "Firewall was NOT configured.\n\n" +
                "?? Other computers will NOT be able to download games from you!\n\n" +
                "You can configure it later by clicking the red 'Configure Firewall' button.",
                "Firewall Not Configured",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ConfigureFirewallAndContinue()
    {
        if (!FirewallHelper.IsRunningAsAdmin())
        {
            MessageBox.Show(
                "Not running as Administrator!\n\n" +
                "Please right-click the application and select 'Run as administrator'.",
                "Administrator Required",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var (success, message) = FirewallHelper.AddFirewallRules();
        
        if (success)
        {
            MessageBox.Show(
                "? FIREWALL CONFIGURED SUCCESSFULLY!\n\n" +
                $"{message}\n\n" +
                "Other computers should now be able to connect to you.\n\n" +
                "If connections still fail:\n" +
                "1. Make sure both computers are on the same network\n" +
                "2. Check for third-party antivirus/firewall software\n" +
                "3. Try the 'Test Connection' button to diagnose",
                "Firewall Configured",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                $"? FIREWALL CONFIGURATION FAILED\n\n" +
                $"{message}\n\n" +
                "MANUAL FIX - Run these commands in Administrator PowerShell:\n\n" +
                "netsh advfirewall firewall add rule name=\"GamesLocalShare\" dir=in action=allow program=\"<path to exe>\" profile=any\n\n" +
                "Or temporarily disable Windows Firewall to test.",
                "Firewall Configuration Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
