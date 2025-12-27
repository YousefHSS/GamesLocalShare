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
    private const string RuleNameUdp = "GamesLocalShare UDP Discovery";
    private const string RuleNameTcp1 = "GamesLocalShare TCP GameList";
    private const string RuleNameTcp2 = "GamesLocalShare TCP FileTransfer";
    private const string RuleNameProgram = "GamesLocalShare Program";
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
            // Check for any of our rules - program rule OR all port rules
            var programResult = RunNetshCommand($"advfirewall firewall show rule name=\"{RuleNameProgram}\"");
            if (programResult.Contains(RuleNameProgram))
                return true;

            var udpResult = RunNetshCommand($"advfirewall firewall show rule name=\"{RuleNameUdp}\"");
            var tcp1Result = RunNetshCommand($"advfirewall firewall show rule name=\"{RuleNameTcp1}\"");
            var tcp2Result = RunNetshCommand($"advfirewall firewall show rule name=\"{RuleNameTcp2}\"");

            return udpResult.Contains(RuleNameUdp) && 
                   tcp1Result.Contains(RuleNameTcp1) && 
                   tcp2Result.Contains(RuleNameTcp2);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Adds firewall rules for the application (requires admin)
    /// Creates BOTH program-based AND port-based rules for maximum compatibility
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
            
            // First, delete any existing rules
            TryDeleteRule(RuleNameUdp);
            TryDeleteRule(RuleNameTcp1);
            TryDeleteRule(RuleNameTcp2);
            TryDeleteRule(RuleNameProgram);
            TryDeleteRule("GamesLocalShare TCP Communication"); // Old rule name

            // Get the path to the current executable
            var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (exePath.EndsWith(".dll"))
            {
                exePath = exePath.Replace(".dll", ".exe");
            }

            // METHOD 1: Add program-based rule (allows ALL traffic from this exe)
            var programResult = RunNetshCommand(
                $"advfirewall firewall add rule name=\"{RuleNameProgram}\" dir=in action=allow program=\"{exePath}\" enable=yes profile=any");
            results.Add($"Program rule: {(programResult.Contains("Ok") ? "OK" : "FAILED")}");

            // METHOD 2: Add port-based rules (as backup)
            var udpResult = RunNetshCommand(
                $"advfirewall firewall add rule name=\"{RuleNameUdp}\" dir=in action=allow protocol=UDP localport={UdpPort} profile=any");
            results.Add($"UDP {UdpPort}: {(udpResult.Contains("Ok") ? "OK" : "FAILED")}");

            var tcp1Result = RunNetshCommand(
                $"advfirewall firewall add rule name=\"{RuleNameTcp1}\" dir=in action=allow protocol=TCP localport={TcpPort1} profile=any");
            results.Add($"TCP {TcpPort1}: {(tcp1Result.Contains("Ok") ? "OK" : "FAILED")}");

            var tcp2Result = RunNetshCommand(
                $"advfirewall firewall add rule name=\"{RuleNameTcp2}\" dir=in action=allow protocol=TCP localport={TcpPort2} profile=any");
            results.Add($"TCP {TcpPort2}: {(tcp2Result.Contains("Ok") ? "OK" : "FAILED")}");

            // Verify rules were added
            var allSuccess = programResult.Contains("Ok") || 
                            (udpResult.Contains("Ok") && tcp1Result.Contains("Ok") && tcp2Result.Contains("Ok"));
            
            if (allSuccess)
            {
                return (true, "Firewall rules added:\n" + string.Join("\n", results));
            }
            else
            {
                return (false, "Some rules failed:\n" + string.Join("\n", results));
            }
        }
        catch (Exception ex)
        {
            return (false, $"Error adding firewall rules: {ex.Message}");
        }
    }

    /// <summary>
    /// Completely disables Windows Firewall (for testing - USE WITH CAUTION)
    /// </summary>
    public static (bool Success, string Message) DisableWindowsFirewall()
    {
        if (!IsRunningAsAdmin())
        {
            return (false, "Administrator privileges required");
        }

        try
        {
            // Disable firewall for all profiles
            RunNetshCommand("advfirewall set allprofiles state off");
            return (true, "Windows Firewall disabled for all profiles");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to disable firewall: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-enables Windows Firewall
    /// </summary>
    public static (bool Success, string Message) EnableWindowsFirewall()
    {
        if (!IsRunningAsAdmin())
        {
            return (false, "Administrator privileges required");
        }

        try
        {
            RunNetshCommand("advfirewall set allprofiles state on");
            return (true, "Windows Firewall enabled for all profiles");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to enable firewall: {ex.Message}");
        }
    }

    private static void TryDeleteRule(string ruleName)
    {
        try
        {
            RunNetshCommand($"advfirewall firewall delete rule name=\"{ruleName}\"");
        }
        catch { /* Ignore if rule doesn't exist */ }
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
            TryDeleteRule(RuleNameUdp);
            TryDeleteRule(RuleNameTcp1);
            TryDeleteRule(RuleNameTcp2);
            TryDeleteRule(RuleNameProgram);
            TryDeleteRule("GamesLocalShare TCP Communication");
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
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (exePath.EndsWith(".dll"))
                {
                    exePath = exePath.Replace(".dll", ".exe");
                }
            }

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
        report.AppendLine();
        
        // Check if Windows Firewall is enabled
        report.AppendLine("Windows Firewall Status:");
        try
        {
            var fwStatus = RunNetshCommand("advfirewall show allprofiles state");
            if (fwStatus.Contains("ON"))
            {
                report.AppendLine("  Firewall is ON");
            }
            else if (fwStatus.Contains("OFF"))
            {
                report.AppendLine("  ?? Firewall is OFF");
            }
            // Extract profile states
            foreach (var line in fwStatus.Split('\n').Where(l => l.Contains("State")))
            {
                report.AppendLine($"    {line.Trim()}");
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
            CheckAndReportRule(report, RuleNameProgram);
            CheckAndReportRule(report, RuleNameUdp);
            CheckAndReportRule(report, RuleNameTcp1);
            CheckAndReportRule(report, RuleNameTcp2);
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
                report.AppendLine("  Current: Public (?? may be more restrictive)");
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
                report.AppendLine("  No ports currently listening (start the network first)");
            }
        }
        catch
        {
            report.AppendLine("  Could not check port status");
        }

        // Check for third-party security software
        report.AppendLine();
        report.AppendLine("Third-Party Security Software:");
        try
        {
            // Check for common antivirus processes
            var processes = Process.GetProcesses();
            var securityProcesses = new[] { "mcshield", "avp", "avgnt", "norton", "nortonsecurity", "bdagent", "mbam", "kavfs", "avguard", "msmpeng" };
            var found = processes.Where(p => securityProcesses.Any(s => p.ProcessName.ToLower().Contains(s))).ToList();
            
            if (found.Any())
            {
                report.AppendLine("  ?? Detected security software:");
                foreach (var p in found.Take(5))
                {
                    report.AppendLine($"    - {p.ProcessName}");
                }
                report.AppendLine("  These may be blocking connections even if Windows Firewall allows them!");
            }
            else
            {
                report.AppendLine("  No common third-party security software detected");
            }
        }
        catch
        {
            report.AppendLine("  Could not check for security software");
        }

        return report.ToString();
    }

    private static void CheckAndReportRule(System.Text.StringBuilder report, string ruleName)
    {
        var result = RunNetshCommand($"advfirewall firewall show rule name=\"{ruleName}\"");
        if (result.Contains(ruleName))
        {
            report.AppendLine($"  ? {ruleName} - EXISTS");
            if (result.Contains("Enabled:") && result.Contains("Yes"))
            {
                report.AppendLine($"      Enabled: Yes");
            }
            if (result.Contains("Profiles:"))
            {
                var profileLine = result.Split('\n').FirstOrDefault(l => l.Contains("Profiles:"));
                if (profileLine != null)
                {
                    report.AppendLine($"      {profileLine.Trim()}");
                }
            }
        }
        else
        {
            report.AppendLine($"  ? {ruleName} - MISSING");
        }
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
