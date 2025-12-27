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

        // Check for command line arguments
        if (e.Args.Contains("--configure-firewall"))
        {
            ConfigureFirewall();
        }

        // Check if firewall rules exist, prompt user if not
        if (!FirewallHelper.CheckFirewallRulesExist())
        {
            var result = MessageBox.Show(
                "GamesLocalShare needs firewall rules to communicate with other computers.\n\n" +
                "Would you like to configure the firewall now?\n" +
                "(Requires Administrator privileges)\n\n" +
                "You can also manually allow ports:\n" +
                "• UDP 45677 (Discovery)\n" +
                "• TCP 45678 (Game lists)\n" +
                "• TCP 45679 (File transfer)",
                "Firewall Configuration Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                if (!FirewallHelper.IsRunningAsAdmin())
                {
                    // Restart as admin to configure firewall
                    FirewallHelper.RestartAsAdmin();
                    return;
                }
                else
                {
                    ConfigureFirewall();
                }
            }
        }
    }

    private void ConfigureFirewall()
    {
        var (success, message) = FirewallHelper.AddFirewallRules();
        
        if (success)
        {
            MessageBox.Show(
                "Firewall rules have been configured successfully!\n\n" +
                "The application will now allow network communication.",
                "Firewall Configured",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                $"Failed to configure firewall:\n\n{message}\n\n" +
                "You may need to manually configure your firewall.",
                "Firewall Configuration Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
