using System.Diagnostics;
using System.Security.Principal;

namespace GamesLocalShare.Services;

/// <summary>
/// Helper class to check and configure Windows Firewall rules
/// </summary>
public static class FirewallHelper
{
    private const string RuleNameUdp = "GamesLocalShare UDP Discovery";
    private const string RuleNameTcp = "GamesLocalShare TCP Communication";
    private const int UdpPort = 45677;
    private const int TcpPort1 = 45678;
    private const int TcpPort2 = 45679;

    /// <summary>
    /// Checks if the application is running with administrator privileges
    /// </summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if firewall rules exist for the application
    /// </summary>
    public static bool CheckFirewallRulesExist()
    {
        try
        {
            var result = RunNetshCommand($"advfirewall firewall show rule name=\"{RuleNameUdp}\"");
            return result.Contains(RuleNameUdp);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Adds firewall rules for the application (requires admin)
    /// </summary>
    public static (bool Success, string Message) AddFirewallRules()
    {
        if (!IsRunningAsAdmin())
        {
            return (false, "Administrator privileges required to add firewall rules");
        }

        try
        {
            var errors = new List<string>();
            
            // Add UDP rule for discovery
            var udpResult = RunNetshCommand(
                $"advfirewall firewall add rule name=\"{RuleNameUdp}\" dir=in action=allow protocol=UDP localport={UdpPort}");
            if (!udpResult.Contains("Ok"))
            {
                errors.Add($"UDP rule: {udpResult}");
            }

            // Add TCP rule for communication
            var tcpResult = RunNetshCommand(
                $"advfirewall firewall add rule name=\"{RuleNameTcp}\" dir=in action=allow protocol=TCP localport={TcpPort1},{TcpPort2}");
            if (!tcpResult.Contains("Ok"))
            {
                errors.Add($"TCP rule: {tcpResult}");
            }

            if (errors.Count == 0)
            {
                return (true, "Firewall rules added successfully");
            }
            else
            {
                return (false, string.Join("\n", errors));
            }
        }
        catch (Exception ex)
        {
            return (false, $"Error adding firewall rules: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes firewall rules for the application (requires admin)
    /// </summary>
    public static (bool Success, string Message) RemoveFirewallRules()
    {
        if (!IsRunningAsAdmin())
        {
            return (false, "Administrator privileges required to remove firewall rules");
        }

        try
        {
            RunNetshCommand($"advfirewall firewall delete rule name=\"{RuleNameUdp}\"");
            RunNetshCommand($"advfirewall firewall delete rule name=\"{RuleNameTcp}\"");
            return (true, "Firewall rules removed");
        }
        catch (Exception ex)
        {
            return (false, $"Error removing firewall rules: {ex.Message}");
        }
    }

    /// <summary>
    /// Restarts the application with administrator privileges
    /// </summary>
    public static void RestartAsAdmin()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return;

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--configure-firewall"
            };

            Process.Start(startInfo);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to restart as admin: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets a diagnostic report about network configuration
    /// </summary>
    public static string GetNetworkDiagnostics()
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine("=== Network Diagnostics ===");
        report.AppendLine();
        
        report.AppendLine($"Running as Admin: {IsRunningAsAdmin()}");
        report.AppendLine($"Firewall Rules Exist: {CheckFirewallRulesExist()}");
        report.AppendLine();
        
        report.AppendLine("Required Ports:");
        report.AppendLine($"  UDP {UdpPort} - Discovery broadcasts");
        report.AppendLine($"  TCP {TcpPort1} - Game list exchange");
        report.AppendLine($"  TCP {TcpPort2} - File transfers");
        report.AppendLine();

        // Check if ports are in use
        report.AppendLine("Port Status:");
        try
        {
            var netstatResult = RunCommand("netstat", $"-an | findstr \":{UdpPort} :{TcpPort1} :{TcpPort2}\"");
            if (!string.IsNullOrEmpty(netstatResult))
            {
                report.AppendLine(netstatResult);
            }
            else
            {
                report.AppendLine("  No active connections on required ports");
            }
        }
        catch
        {
            report.AppendLine("  Could not check port status");
        }

        return report.ToString();
    }

    private static string RunNetshCommand(string arguments)
    {
        return RunCommand("netsh", arguments);
    }

    private static string RunCommand(string fileName, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return string.Empty;

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            return string.IsNullOrEmpty(error) ? output : $"{output}\nError: {error}";
        }
        catch (Exception ex)
        {
            return $"Command failed: {ex.Message}";
        }
    }
}
