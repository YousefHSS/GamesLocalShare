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
            "GamesLocalShare needs to open firewall ports so other computers can:\n\n" +
            "• Discover this computer on the network\n" +
            "• See your game list\n" +
            "• Download games FROM you\n\n" +
            "WITHOUT THIS: You can download from others, but they CANNOT download from you.\n\n" +
            "Configure firewall now?\n" +
            "(You will be prompted for Administrator permission)",
            "GamesLocalShare - Firewall Setup Required",
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
                try
                {
                    FirewallHelper.RestartAsAdmin();
                    // App will exit and restart as admin
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Failed to restart as Administrator:\n\n{ex.Message}\n\n" +
                        "Please right-click the application and select 'Run as administrator', " +
                        "then click 'Configure Firewall' button.",
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
                "You can configure it later:\n" +
                "• Click the red '? Configure Firewall' button in the app\n" +
                "• Or run the app as Administrator",
                "Firewall Not Configured",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ConfigureFirewallAndContinue()
    {
        var isAdmin = FirewallHelper.IsRunningAsAdmin();
        
        if (!isAdmin)
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
            // Verify the rules are actually working
            var verified = FirewallHelper.CheckFirewallRulesExist();
            
            MessageBox.Show(
                "? FIREWALL CONFIGURED SUCCESSFULLY!\n\n" +
                "Ports opened for incoming connections:\n" +
                "• UDP 45677 - Peer discovery\n" +
                "• TCP 45678 - Game list sharing\n" +
                "• TCP 45679 - File transfers\n\n" +
                $"Verification: {(verified ? "PASSED ?" : "Please restart the app")}\n\n" +
                "Other computers can now download games from you!",
                "Firewall Configured",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                $"? FIREWALL CONFIGURATION FAILED\n\n" +
                $"Error: {message}\n\n" +
                "MANUAL FIX - Run this in Administrator PowerShell:\n\n" +
                "netsh advfirewall firewall add rule name=\"GamesLocalShare UDP\" dir=in action=allow protocol=UDP localport=45677 profile=any\n\n" +
                "netsh advfirewall firewall add rule name=\"GamesLocalShare TCP1\" dir=in action=allow protocol=TCP localport=45678 profile=any\n\n" +
                "netsh advfirewall firewall add rule name=\"GamesLocalShare TCP2\" dir=in action=allow protocol=TCP localport=45679 profile=any\n\n" +
                "If you have antivirus software (Norton, McAfee, Kaspersky, etc.), you may also need to allow these ports in that software.",
                "Firewall Configuration Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
