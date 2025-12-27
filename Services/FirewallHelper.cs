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
    /// Checks if ALL firewall rules exist for the application
    /// </summary>
    public static bool CheckFirewallRulesExist()
    {
        try
        {
            // Check for UDP rule
            var udpResult = RunNetshCommand($"advfirewall firewall show rule name=\"{RuleNameUdp}\"");
            var hasUdp = udpResult.Contains(RuleNameUdp);

            // Check for TCP rule
            var tcpResult = RunNetshCommand($"advfirewall firewall show rule name=\"{RuleNameTcp}\"");
            var hasTcp = tcpResult.Contains(RuleNameTcp);

            // Both rules must exist
            return hasUdp && hasTcp;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if specific ports are allowed through the firewall
    /// </summary>
    public static (bool udpAllowed, bool tcp1Allowed, bool tcp2Allowed) CheckPortsAllowed()
    {
        try
        {
            var allRules = RunNetshCommand("advfirewall firewall show rule name=all dir=in");
            
            var udpAllowed = allRules.Contains($"LocalPort:.*{UdpPort}") || 
                             allRules.Contains(RuleNameUdp);
            var tcp1Allowed = allRules.Contains($"{TcpPort1}") && allRules.Contains("TCP");
            var tcp2Allowed = allRules.Contains($"{TcpPort2}") && allRules.Contains("TCP");

            return (udpAllowed, tcp1Allowed, tcp2Allowed);
        }
        catch
        {
            return (false, false, false);
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
            
            // First, try to delete existing rules (in case they're corrupted)
            try
            {
                RunNetshCommand($"advfirewall firewall delete rule name=\"{RuleNameUdp}\"");
                RunNetshCommand($"advfirewall firewall delete rule name=\"{RuleNameTcp}\"");
            }
            catch { /* Ignore if rules don't exist */ }

            // Add UDP rule for discovery
            var udpResult = RunNetshCommand(
                $"advfirewall firewall add rule name=\"{RuleNameUdp}\" dir=in action=allow protocol=UDP localport={UdpPort}");
            if (!udpResult.Contains("Ok"))
            {
                errors.Add($"UDP rule failed: {udpResult}");
            }

            // Add TCP rule for game list (port 45678)
            var tcp1Result = RunNetshCommand(
                $"advfirewall firewall add rule name=\"{RuleNameTcp}\" dir=in action=allow protocol=TCP localport={TcpPort1},{TcpPort2}");
            if (!tcp1Result.Contains("Ok"))
            {
                errors.Add($"TCP rule failed: {tcp1Result}");
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
            {
                // Fallback to executable name
                exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (exePath.EndsWith(".dll"))
                {
                    exePath = exePath.Replace(".dll", ".exe");
                }
            }

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
        report.AppendLine($"Firewall Rules Configured: {CheckFirewallRulesExist()}");
        report.AppendLine();
        
        report.AppendLine("Required Ports:");
        report.AppendLine($"  UDP {UdpPort} - Discovery broadcasts");
        report.AppendLine($"  TCP {TcpPort1} - Game list exchange");
        report.AppendLine($"  TCP {TcpPort2} - File transfers");
        report.AppendLine();

        // Check our specific rules
        report.AppendLine("GamesLocalShare Firewall Rules:");
        try
        {
            var udpRule = RunNetshCommand($"advfirewall firewall show rule name=\"{RuleNameUdp}\"");
            report.AppendLine(udpRule.Contains(RuleNameUdp) ? $"  ? {RuleNameUdp} - EXISTS" : $"  ? {RuleNameUdp} - MISSING");
            
            var tcpRule = RunNetshCommand($"advfirewall firewall show rule name=\"{RuleNameTcp}\"");
            report.AppendLine(tcpRule.Contains(RuleNameTcp) ? $"  ? {RuleNameTcp} - EXISTS" : $"  ? {RuleNameTcp} - MISSING");
        }
        catch (Exception ex)
        {
            report.AppendLine($"  Error checking rules: {ex.Message}");
        }
        report.AppendLine();

        // Check if ports are listening
        report.AppendLine("Listening Ports (this app):");
        try
        {
            var netstatResult = RunCommand("cmd", $"/c netstat -an | findstr \":{UdpPort} :{TcpPort1} :{TcpPort2}\"");
            if (!string.IsNullOrWhiteSpace(netstatResult))
            {
                foreach (var line in netstatResult.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
                {
                    report.AppendLine($"  {line.Trim()}");
                }
            }
            else
            {
                report.AppendLine("  No ports currently listening (start the network first)");
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
