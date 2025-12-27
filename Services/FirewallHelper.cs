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
            var result = RunNetshCommand($"advfirewall firewall show rule name=all | findstr /i \"{AppName}\"");
            return result.Contains(AppName);
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
            results.Add($"Executable: {exePath}");

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

            // UDP Discovery - Outbound
            if (AddRule($"{AppName} UDP {UdpPort} Out", $"dir=out action=allow protocol=UDP localport={UdpPort} profile=any"))
            {
                results.Add($"? UDP {UdpPort} (outbound): OK");
                successCount++;
            }
            else
            {
                results.Add($"? UDP {UdpPort} (outbound): FAILED");
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

            // ============================================
            // METHOD 3: Allow all local network traffic (FALLBACK)
            // ============================================
            
            // Allow all traffic on local subnet (192.168.x.x, 10.x.x.x, 172.16-31.x.x)
            AddRule($"{AppName} LocalNet In", $"dir=in action=allow remoteip=localsubnet profile=any program=\"{exePath}\"");
            
            // Verify rules were added
            var verified = CheckFirewallRulesExist();
            
            if (successCount >= 4 && verified)
            {
                return (true, $"Firewall configured successfully!\n\n{string.Join("\n", results)}\n\nRules verified: YES");
            }
            else if (successCount > 0)
            {
                return (true, $"Partial success ({successCount} rules added):\n\n{string.Join("\n", results)}\n\nRules verified: {(verified ? "YES" : "NO")}");
            }
            else
            {
                return (false, $"Failed to add firewall rules:\n\n{string.Join("\n", results)}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Error adding firewall rules: {ex.Message}");
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
            return result.Contains("Ok") || result.Contains("successfully");
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteAllExistingRules()
    {
        try
        {
            // Delete all rules containing "GamesLocalShare"
            var existingRules = RunNetshCommand($"advfirewall firewall show rule name=all | findstr /i \"{AppName}\"");
            
            // Try to delete by various names we've used
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
        catch { }
    }

    /// <summary>
    /// Removes all firewall rules for the application (requires admin)
    /// </summary>
    public static (bool Success, string Message) RemoveFirewallRules()
    {
        if (!IsRunningAsAdmin())
        {
            return (false, "Administrator privileges required to remove firewall rules");
        }

        try
        {
            DeleteAllExistingRules();
            return (true, "Firewall rules removed");
        }
        catch (Exception ex)
        {
            return (false, $"Error removing firewall rules: {ex.Message}");
        }
    }

    /// <summary>
    /// Tests if a specific port can accept connections
    /// </summary>
    public static bool TestPortListening(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
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
            System.Diagnostics.Debug.WriteLine($"Restarting as admin: {exePath}");

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
        report.AppendLine($"Executable: {GetExecutablePath()}");
        report.AppendLine();
        
        // Check if Windows Firewall is enabled
        report.AppendLine("Windows Firewall Status:");
        try
        {
            var fwStatus = RunNetshCommand("advfirewall show allprofiles state");
            foreach (var line in fwStatus.Split('\n').Where(l => l.Contains("State") || l.Contains("Profile")))
            {
                report.AppendLine($"  {line.Trim()}");
            }
        }
        catch
        {
            report.AppendLine("  Could not check firewall status");
        }
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
            var rules = RunNetshCommand($"advfirewall firewall show rule name=all | findstr /i \"{AppName}\"");
            if (!string.IsNullOrWhiteSpace(rules))
            {
                foreach (var line in rules.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(15))
                {
                    report.AppendLine($"  {line.Trim()}");
                }
            }
            else
            {
                report.AppendLine("  No GamesLocalShare rules found!");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"  Error checking rules: {ex.Message}");
        }
        report.AppendLine();

        // Check current network profile
        report.AppendLine("Network Profile:");
        try
        {
            var profileResult = RunNetshCommand("advfirewall show currentprofile");
            if (profileResult.Contains("Domain"))
                report.AppendLine("  Current: Domain");
            else if (profileResult.Contains("Private"))
                report.AppendLine("  Current: Private");
            else if (profileResult.Contains("Public"))
                report.AppendLine("  Current: Public (may need extra rules)");
            else
                report.AppendLine("  Current: Unknown");
        }
        catch
        {
            report.AppendLine("  Could not determine network profile");
        }
        report.AppendLine();

        // Check if ports are listening
        report.AppendLine("Listening Ports:");
        try
        {
            var netstatResult = RunCommand("cmd", $"/c netstat -an | findstr \"LISTENING\" | findstr \":{UdpPort} :{TcpPort1} :{TcpPort2}\"");
            if (!string.IsNullOrWhiteSpace(netstatResult))
            {
                foreach (var line in netstatResult.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
                {
                    report.AppendLine($"  {line.Trim()}");
                }
            }
            else
            {
                report.AppendLine("  No ports listening (start network first)");
            }
        }
        catch
        {
            report.AppendLine("  Could not check port status");
        }

        // Check for third-party security software
        report.AppendLine();
        report.AppendLine("Security Software Check:");
        try
        {
            var processes = Process.GetProcesses();
            var securityKeywords = new[] { "mcshield", "avp", "avgnt", "norton", "bdagent", "mbam", "kavfs", "avguard", "avast", "avg", "eset", "sophos", "trend" };
            var found = processes.Where(p => securityKeywords.Any(s => p.ProcessName.ToLower().Contains(s))).Select(p => p.ProcessName).Distinct().ToList();
            
            if (found.Any())
            {
                report.AppendLine("  ?? Third-party security detected:");
                foreach (var name in found)
                {
                    report.AppendLine($"    - {name}");
                }
                report.AppendLine("  May need to allow app in this software too!");
            }
            else
            {
                report.AppendLine("  No third-party security software detected");
            }
        }
        catch
        {
            report.AppendLine("  Could not check security software");
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
