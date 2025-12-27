using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;

namespace GamesLocalShare.Services;

/// <summary>
/// Helper class to check and configure Windows Firewall rules
/// </summary>
public static class FirewallHelper
{
    private const string AppName = "GamesLocalShare";
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
            // Check for one of our specific rules (don't use pipe/findstr which can hang)
            var result = RunNetshCommand($"advfirewall firewall show rule name=\"{AppName} Program In\"");
            return result.Contains(AppName) && !result.Contains("No rules match");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Adds comprehensive firewall rules for the application (requires admin)
    /// Creates multiple types of rules for maximum compatibility
    /// </summary>
    public static (bool Success, string Message) AddFirewallRules()
    {
        if (!IsRunningAsAdmin())
        {
            return (false, "Administrator privileges required to add firewall rules");
        }

        try
        {
            var results = new List<string>();
            var successCount = 0;
            
            // First, delete ALL existing GamesLocalShare rules
            DeleteAllExistingRules();

            // Get the path to the current executable
            var exePath = GetExecutablePath();

            // ============================================
            // METHOD 1: Program-based rules (MOST RELIABLE)
            // ============================================
            
            // Inbound program rule
            if (AddRule($"{AppName} Program In", $"dir=in action=allow program=\"{exePath}\" enable=yes profile=any"))
            {
                results.Add("? Program rule (inbound): OK");
                successCount++;
            }
            else
            {
                results.Add("? Program rule (inbound): FAILED");
            }

            // Outbound program rule
            if (AddRule($"{AppName} Program Out", $"dir=out action=allow program=\"{exePath}\" enable=yes profile=any"))
            {
                results.Add("? Program rule (outbound): OK");
                successCount++;
            }
            else
            {
                results.Add("? Program rule (outbound): FAILED");
            }

            // ============================================
            // METHOD 2: Port-based rules (BACKUP)
            // ============================================
            
            // UDP Discovery - Inbound
            if (AddRule($"{AppName} UDP {UdpPort} In", $"dir=in action=allow protocol=UDP localport={UdpPort} profile=any"))
            {
                results.Add($"? UDP {UdpPort} (inbound): OK");
                successCount++;
            }
            else
            {
                results.Add($"? UDP {UdpPort} (inbound): FAILED");
            }

            // TCP Game List - Inbound
            if (AddRule($"{AppName} TCP {TcpPort1} In", $"dir=in action=allow protocol=TCP localport={TcpPort1} profile=any"))
            {
                results.Add($"? TCP {TcpPort1} (inbound): OK");
                successCount++;
            }
            else
            {
                results.Add($"? TCP {TcpPort1} (inbound): FAILED");
            }

            // TCP File Transfer - Inbound
            if (AddRule($"{AppName} TCP {TcpPort2} In", $"dir=in action=allow protocol=TCP localport={TcpPort2} profile=any"))
            {
                results.Add($"? TCP {TcpPort2} (inbound): OK");
                successCount++;
            }
            else
            {
                results.Add($"? TCP {TcpPort2} (inbound): FAILED");
            }
            
            if (successCount >= 3)
            {
                return (true, string.Join("\n", results));
            }
            else if (successCount > 0)
            {
                return (true, $"Partial success ({successCount} rules):\n{string.Join("\n", results)}");
            }
            else
            {
                return (false, $"Failed to add rules:\n{string.Join("\n", results)}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Error: {ex.Message}");
        }
    }

    private static string GetExecutablePath()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        }
        if (exePath.EndsWith(".dll"))
        {
            exePath = exePath.Replace(".dll", ".exe");
        }
        return exePath;
    }

    private static bool AddRule(string ruleName, string ruleParams)
    {
        try
        {
            var result = RunNetshCommand($"advfirewall firewall add rule name=\"{ruleName}\" {ruleParams}");
            return result.Contains("Ok") || result.Contains("successfully") || result.Contains("Ok.");
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteAllExistingRules()
    {
        var ruleNames = new[]
        {
            $"{AppName} Program In",
            $"{AppName} Program Out",
            $"{AppName} UDP {UdpPort} In",
            $"{AppName} UDP {UdpPort} Out",
            $"{AppName} TCP {TcpPort1} In",
            $"{AppName} TCP {TcpPort2} In",
            $"{AppName} LocalNet In",
            "GamesLocalShare UDP Discovery",
            "GamesLocalShare TCP GameList",
            "GamesLocalShare TCP FileTransfer",
            "GamesLocalShare Program",
            "GamesLocalShare TCP Communication"
        };

        foreach (var name in ruleNames)
        {
            try
            {
                RunNetshCommand($"advfirewall firewall delete rule name=\"{name}\"");
            }
            catch { }
        }
    }

    /// <summary>
    /// Removes all firewall rules for the application (requires admin)
    /// </summary>
    public static (bool Success, string Message) RemoveFirewallRules()
    {
        if (!IsRunningAsAdmin())
        {
            return (false, "Administrator privileges required");
        }

        try
        {
            DeleteAllExistingRules();
            return (true, "Firewall rules removed");
        }
        catch (Exception ex)
        {
            return (false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Restarts the application with administrator privileges
    /// </summary>
    public static void RestartAsAdmin()
    {
        try
        {
            var exePath = GetExecutablePath();

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
            throw;
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
        report.AppendLine($"  UDP {UdpPort} - Discovery");
        report.AppendLine($"  TCP {TcpPort1} - Game list");
        report.AppendLine($"  TCP {TcpPort2} - File transfer");
        report.AppendLine();

        // Check specific rules
        report.AppendLine("Checking firewall rules:");
        try
        {
            var programRule = RunNetshCommand($"advfirewall firewall show rule name=\"{AppName} Program In\"");
            report.AppendLine(programRule.Contains(AppName) && !programRule.Contains("No rules") 
                ? "  ? Program rule exists" 
                : "  ? Program rule missing");
                
            var tcpRule = RunNetshCommand($"advfirewall firewall show rule name=\"{AppName} TCP {TcpPort2} In\"");
            report.AppendLine(tcpRule.Contains(AppName) && !tcpRule.Contains("No rules") 
                ? $"  ? TCP {TcpPort2} rule exists" 
                : $"  ? TCP {TcpPort2} rule missing");
        }
        catch (Exception ex)
        {
            report.AppendLine($"  Error: {ex.Message}");
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

            // Use timeout to avoid hanging
            var output = "";
            var error = "";
            
            var outputTask = Task.Run(() => output = process.StandardOutput.ReadToEnd());
            var errorTask = Task.Run(() => error = process.StandardError.ReadToEnd());
            
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { }
                return "Command timed out";
            }
            
            Task.WaitAll(new[] { outputTask, errorTask }, 1000);

            return string.IsNullOrEmpty(error) ? output : $"{output}\nError: {error}";
        }
        catch (Exception ex)
        {
            return $"Command failed: {ex.Message}";
        }
    }
}
