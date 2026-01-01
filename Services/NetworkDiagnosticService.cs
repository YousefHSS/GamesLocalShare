using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace GamesLocalShare.Services;

/// <summary>
/// Service for diagnosing network connectivity issues between peers
/// </summary>
public class NetworkDiagnosticService
{
    /// <summary>
    /// Represents information about a network interface
    /// </summary>
    public class NetworkInterfaceInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string SubnetMask { get; set; } = string.Empty;
        public string Gateway { get; set; } = string.Empty;
        public string NetworkAddress { get; set; } = string.Empty;
        public bool IsUp { get; set; }
        public bool IsWireless { get; set; }
    }

    /// <summary>
    /// Result of a network diagnostic check
    /// </summary>
    public class DiagnosticResult
    {
        public bool Success { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Suggestion { get; set; }
        public DiagnosticSeverity Severity { get; set; } = DiagnosticSeverity.Info;
    }

    public enum DiagnosticSeverity
    {
        Info,
        Warning,
        Error,
        Critical
    }

    /// <summary>
    /// Gets all active network interfaces with their details
    /// </summary>
    public static List<NetworkInterfaceInfo> GetAllNetworkInterfaces()
    {
        var result = new List<NetworkInterfaceInfo>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                            ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var ni in interfaces)
            {
                var ipProps = ni.GetIPProperties();
                
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var gateway = ipProps.GatewayAddresses
                            .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "None";

                        var networkAddr = CalculateNetworkAddress(addr.Address, addr.IPv4Mask);

                        result.Add(new NetworkInterfaceInfo
                        {
                            Name = ni.Name,
                            Description = ni.Description,
                            Type = ni.NetworkInterfaceType.ToString(),
                            IpAddress = addr.Address.ToString(),
                            SubnetMask = addr.IPv4Mask.ToString(),
                            Gateway = gateway,
                            NetworkAddress = networkAddr,
                            IsUp = ni.OperationalStatus == OperationalStatus.Up,
                            IsWireless = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting network interfaces: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Calculates the network address from an IP and subnet mask
    /// </summary>
    private static string CalculateNetworkAddress(IPAddress ip, IPAddress mask)
    {
        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var networkBytes = new byte[4];

        for (int i = 0; i < 4; i++)
        {
            networkBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
        }

        return new IPAddress(networkBytes).ToString();
    }

    /// <summary>
    /// Checks if two IP addresses are on the same subnet
    /// </summary>
    public static bool AreOnSameSubnet(string ip1, string ip2, string subnetMask = "255.255.255.0")
    {
        try
        {
            var addr1 = IPAddress.Parse(ip1);
            var addr2 = IPAddress.Parse(ip2);
            var mask = IPAddress.Parse(subnetMask);

            var network1 = CalculateNetworkAddress(addr1, mask);
            var network2 = CalculateNetworkAddress(addr2, mask);

            return network1 == network2;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the subnet/network identifier from an IP (e.g., "192.168.0" from "192.168.0.133")
    /// </summary>
    public static string GetSubnetIdentifier(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length >= 3)
        {
            return $"{parts[0]}.{parts[1]}.{parts[2]}";
        }
        return ip;
    }

    /// <summary>
    /// Performs a ping test to an IP address
    /// </summary>
    public static async Task<(bool Success, long RoundtripMs, string Error)> PingAsync(string ipAddress, int timeoutMs = 3000)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ipAddress, timeoutMs);
            
            return reply.Status == IPStatus.Success 
                ? (true, reply.RoundtripTime, string.Empty)
                : (false, 0, reply.Status.ToString());
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Tests TCP connectivity to a specific port
    /// </summary>
    public static async Task<(bool Success, string Error)> TestTcpConnectionAsync(string ipAddress, int port, int timeoutMs = 5000)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            
            await client.ConnectAsync(ipAddress, port, cts.Token);
            return (true, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (false, "Connection timed out");
        }
        catch (SocketException ex)
        {
            return (false, ex.SocketErrorCode.ToString());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Runs a comprehensive network diagnostic for connecting to a peer
    /// </summary>
    public static async Task<List<DiagnosticResult>> RunFullDiagnosticAsync(string localIp, string peerIp)
    {
        var results = new List<DiagnosticResult>();

        // Check 1: Validate IP addresses
        results.Add(ValidateIpAddresses(localIp, peerIp));

        // Check 2: Subnet comparison
        results.Add(CheckSubnetCompatibility(localIp, peerIp));

        // Check 3: Network interface analysis
        results.AddRange(AnalyzeNetworkInterfaces(localIp, peerIp));

        // Check 4: Ping test
        var pingResult = await TestPingConnectivity(peerIp);
        results.Add(pingResult);

        // Check 5: Port connectivity (only if ping succeeds or we're on different subnets)
        if (pingResult.Success || !AreOnSameSubnet(localIp, peerIp))
        {
            results.AddRange(await TestPortConnectivity(peerIp));
        }

        // Check 6: Route analysis
        results.Add(await AnalyzeRouteAsync(peerIp));

        return results;
    }

    private static DiagnosticResult ValidateIpAddresses(string localIp, string peerIp)
    {
        bool localValid = IPAddress.TryParse(localIp, out var localAddr);
        bool peerValid = IPAddress.TryParse(peerIp, out var peerAddr);

        if (!localValid || !peerValid)
        {
            return new DiagnosticResult
            {
                Success = false,
                Title = "IP Address Validation",
                Message = $"Invalid IP address format. Local: {(localValid ? "OK" : "INVALID")}, Peer: {(peerValid ? "OK" : "INVALID")}",
                Severity = DiagnosticSeverity.Error
            };
        }

        // Check for private IP ranges
        bool localIsPrivate = IsPrivateIp(localAddr!);
        bool peerIsPrivate = IsPrivateIp(peerAddr!);

        if (!localIsPrivate || !peerIsPrivate)
        {
            return new DiagnosticResult
            {
                Success = false,
                Title = "IP Address Validation",
                Message = "One or both IP addresses appear to be public IPs. This app is designed for local network (LAN) use only.",
                Severity = DiagnosticSeverity.Warning,
                Suggestion = "Make sure both computers are connected to the same local network."
            };
        }

        return new DiagnosticResult
        {
            Success = true,
            Title = "IP Address Validation",
            Message = $"Both IP addresses are valid private addresses.\n  Local: {localIp}\n  Peer: {peerIp}",
            Severity = DiagnosticSeverity.Info
        };
    }

    private static bool IsPrivateIp(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        
        // 10.0.0.0 - 10.255.255.255
        if (bytes[0] == 10) return true;
        
        // 172.16.0.0 - 172.31.255.255
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        
        // 192.168.0.0 - 192.168.255.255
        if (bytes[0] == 192 && bytes[1] == 168) return true;

        // 169.254.0.0 - 169.254.255.255 (link-local)
        if (bytes[0] == 169 && bytes[1] == 254) return true;

        return false;
    }

    private static DiagnosticResult CheckSubnetCompatibility(string localIp, string peerIp)
    {
        var localSubnet = GetSubnetIdentifier(localIp);
        var peerSubnet = GetSubnetIdentifier(peerIp);

        if (localSubnet == peerSubnet)
        {
            return new DiagnosticResult
            {
                Success = true,
                Title = "Subnet Check",
                Message = $"? Both devices are on the same subnet: {localSubnet}.x",
                Severity = DiagnosticSeverity.Info
            };
        }

        // Different subnets - this is likely the problem!
        return new DiagnosticResult
        {
            Success = false,
            Title = "?? SUBNET MISMATCH DETECTED",
            Message = $"Your devices are on DIFFERENT subnets!\n\n" +
                     $"  Your IP: {localIp} (subnet: {localSubnet}.x)\n" +
                     $"  Peer IP: {peerIp} (subnet: {peerSubnet}.x)\n\n" +
                     $"This is why they can't communicate directly!",
            Severity = DiagnosticSeverity.Critical,
            Suggestion = GetSubnetMismatchSuggestion(localIp, peerIp)
        };
    }

    private static string GetSubnetMismatchSuggestion(string localIp, string peerIp)
    {
        var sb = new StringBuilder();
        sb.AppendLine("???????????????????????????????????????????????????????");
        sb.AppendLine("SOLUTIONS (try in order):");
        sb.AppendLine("???????????????????????????????????????????????????????");
        sb.AppendLine();
        sb.AppendLine("1. QUICKEST FIX - Set a static IP on one device:");
        sb.AppendLine($"   On the device with IP {peerIp}:");
        sb.AppendLine($"   ? Open Network Settings ¨ Change adapter options");
        sb.AppendLine($"   ? Right-click the network adapter ¨ Properties");
        sb.AppendLine($"   ? Select 'Internet Protocol Version 4' ¨ Properties");
        sb.AppendLine($"   ? Choose 'Use the following IP address':");
        
        // Suggest an IP in the same subnet as local
        var localParts = localIp.Split('.');
        if (localParts.Length == 4)
        {
            sb.AppendLine($"     IP: {localParts[0]}.{localParts[1]}.{localParts[2]}.200");
            sb.AppendLine($"     Subnet: 255.255.255.0");
            sb.AppendLine($"     Gateway: {localParts[0]}.{localParts[1]}.{localParts[2]}.1");
        }
        
        sb.AppendLine();
        sb.AppendLine("2. CHECK YOUR NETWORK EQUIPMENT:");
        sb.AppendLine("   ? You may have multiple routers creating separate networks");
        sb.AppendLine("   ? A switch with built-in DHCP can cause this");
        sb.AppendLine("   ? WiFi and Ethernet may be isolated by router settings");
        sb.AppendLine();
        sb.AppendLine("3. ROUTER CONFIGURATION:");
        sb.AppendLine("   ? Log into your router (usually 192.168.0.1 or 192.168.1.1)");
        sb.AppendLine("   ? Look for 'AP Isolation' or 'Client Isolation' ¨ Disable it");
        sb.AppendLine("   ? Check if there's a secondary router ¨ Set it to 'Bridge Mode'");
        sb.AppendLine();
        sb.AppendLine("4. CONNECT BOTH DEVICES THE SAME WAY:");
        sb.AppendLine("   ? Try connecting both via WiFi, OR");
        sb.AppendLine("   ? Try connecting both via Ethernet cable");
        
        return sb.ToString();
    }

    private static List<DiagnosticResult> AnalyzeNetworkInterfaces(string localIp, string peerIp)
    {
        var results = new List<DiagnosticResult>();
        var interfaces = GetAllNetworkInterfaces();

        // Find our active interface
        var activeInterface = interfaces.FirstOrDefault(i => i.IpAddress == localIp);
        
        if (activeInterface != null)
        {
            results.Add(new DiagnosticResult
            {
                Success = true,
                Title = "Your Network Interface",
                Message = $"  Adapter: {activeInterface.Description}\n" +
                         $"  Type: {(activeInterface.IsWireless ? "WiFi (Wireless)" : "Ethernet (Wired)")}\n" +
                         $"  IP: {activeInterface.IpAddress}\n" +
                         $"  Subnet: {activeInterface.SubnetMask}\n" +
                         $"  Gateway: {activeInterface.Gateway}\n" +
                         $"  Network: {activeInterface.NetworkAddress}",
                Severity = DiagnosticSeverity.Info
            });
        }

        // Check if we have multiple interfaces on different subnets
        var distinctSubnets = interfaces.Select(i => i.NetworkAddress).Distinct().ToList();
        if (distinctSubnets.Count > 1)
        {
            results.Add(new DiagnosticResult
            {
                Success = true,
                Title = "Multiple Network Interfaces Detected",
                Message = $"Your computer has {interfaces.Count} network interface(s) on {distinctSubnets.Count} different subnet(s):\n" +
                         string.Join("\n", interfaces.Select(i => $"  ? {i.Description}: {i.IpAddress} ({i.NetworkAddress})")),
                Severity = DiagnosticSeverity.Warning,
                Suggestion = "If you're having connection issues, try disabling one of the network adapters temporarily."
            });
        }

        return results;
    }

    private static async Task<DiagnosticResult> TestPingConnectivity(string peerIp)
    {
        var (success, roundtrip, error) = await PingAsync(peerIp);

        if (success)
        {
            return new DiagnosticResult
            {
                Success = true,
                Title = "Ping Test",
                Message = $"? Ping to {peerIp} successful! Response time: {roundtrip}ms",
                Severity = DiagnosticSeverity.Info
            };
        }

        return new DiagnosticResult
        {
            Success = false,
            Title = "Ping Test",
            Message = $"? Ping to {peerIp} FAILED: {error}",
            Severity = DiagnosticSeverity.Error,
            Suggestion = "The peer device is not reachable at the network level. This usually means:\n" +
                        "  ? Devices are on different subnets (see above)\n" +
                        "  ? Firewall is blocking ICMP/ping\n" +
                        "  ? The device is offline or IP is incorrect\n" +
                        "  ? Network equipment is blocking traffic between devices"
        };
    }

    private static async Task<List<DiagnosticResult>> TestPortConnectivity(string peerIp)
    {
        var results = new List<DiagnosticResult>();
        var ports = new[] { (45677, "UDP Discovery"), (45678, "TCP Game List"), (45679, "TCP File Transfer") };

        foreach (var (port, name) in ports)
        {
            if (port == 45677) continue; // Skip UDP for now, TCP is more reliable test

            var (success, error) = await TestTcpConnectionAsync(peerIp, port);
            
            results.Add(new DiagnosticResult
            {
                Success = success,
                Title = $"Port {port} ({name})",
                Message = success 
                    ? $"? Port {port} is reachable"
                    : $"? Port {port} connection failed: {error}",
                Severity = success ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning,
                Suggestion = success ? null : $"Make sure the peer has:\n  1. Started the network in the app\n  2. Configured their firewall\n  3. No other app using port {port}"
            });
        }

        return results;
    }

    private static async Task<DiagnosticResult> AnalyzeRouteAsync(string peerIp)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "tracert",
                Arguments = $"-d -h 5 -w 1000 {peerIp}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new DiagnosticResult
                {
                    Success = false,
                    Title = "Route Analysis",
                    Message = "Could not start traceroute",
                    Severity = DiagnosticSeverity.Warning
                };
            }

            // Only wait a short time
            var outputTask = process.StandardOutput.ReadToEndAsync();
            
            if (!process.WaitForExit(8000))
            {
                try { process.Kill(); } catch { }
            }

            var output = await outputTask;
            var lines = output.Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Take(8)
                .ToList();

            var hopCount = lines.Count(l => l.Trim().StartsWith("1") || l.Trim().StartsWith("2") || 
                                           l.Trim().StartsWith("3") || l.Trim().StartsWith("4") || 
                                           l.Trim().StartsWith("5"));

            if (hopCount <= 1)
            {
                return new DiagnosticResult
                {
                    Success = true,
                    Title = "Route Analysis",
                    Message = $"Direct route to peer (1 hop or less) - devices should be able to communicate directly.",
                    Severity = DiagnosticSeverity.Info
                };
            }
            else if (hopCount > 1)
            {
                return new DiagnosticResult
                {
                    Success = false,
                    Title = "Route Analysis",
                    Message = $"Multiple hops detected ({hopCount}). Traffic may be going through multiple routers.",
                    Severity = DiagnosticSeverity.Warning,
                    Suggestion = "Multiple network hops can indicate:\n" +
                                "  ? Multiple routers between devices\n" +
                                "  ? Complex network setup\n" +
                                "  ? Possible subnet isolation"
                };
            }

            return new DiagnosticResult
            {
                Success = true,
                Title = "Route Analysis",
                Message = "Route analysis completed",
                Severity = DiagnosticSeverity.Info
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticResult
            {
                Success = false,
                Title = "Route Analysis",
                Message = $"Route analysis failed: {ex.Message}",
                Severity = DiagnosticSeverity.Warning
            };
        }
    }

    /// <summary>
    /// Generates a complete diagnostic report as formatted text
    /// </summary>
    public static async Task<string> GenerateReportAsync(string localIp, string peerIp)
    {
        var sb = new StringBuilder();
        sb.AppendLine("??????????????????????????????????????????????????????????????????");
        sb.AppendLine("?           NETWORK DIAGNOSTIC REPORT                            ?");
        sb.AppendLine("??????????????????????????????????????????????????????????????????");
        sb.AppendLine();
        sb.AppendLine($"  Your IP:    {localIp}");
        sb.AppendLine($"  Peer IP:    {peerIp}");
        sb.AppendLine($"  Timestamp:  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        var results = await RunFullDiagnosticAsync(localIp, peerIp);

        foreach (var result in results)
        {
            var icon = result.Severity switch
            {
                DiagnosticSeverity.Info => "??",
                DiagnosticSeverity.Warning => "??",
                DiagnosticSeverity.Error => "?",
                DiagnosticSeverity.Critical => "??",
                _ => "?"
            };

            sb.AppendLine($"„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ");
            sb.AppendLine($"{icon} {result.Title}");
            sb.AppendLine($"„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ„Ÿ");
            sb.AppendLine(result.Message);
            
            if (!string.IsNullOrEmpty(result.Suggestion))
            {
                sb.AppendLine();
                sb.AppendLine("?? SUGGESTION:");
                sb.AppendLine(result.Suggestion);
            }
            sb.AppendLine();
        }

        // Summary
        var criticalCount = results.Count(r => r.Severity == DiagnosticSeverity.Critical);
        var errorCount = results.Count(r => r.Severity == DiagnosticSeverity.Error);
        
        sb.AppendLine("????????????????????????????????????????????????????????????????");
        sb.AppendLine("SUMMARY");
        sb.AppendLine("????????????????????????????????????????????????????????????????");
        
        if (criticalCount > 0)
        {
            sb.AppendLine("?? CRITICAL ISSUE DETECTED - See subnet mismatch solution above!");
        }
        else if (errorCount > 0)
        {
            sb.AppendLine($"? {errorCount} error(s) found - Review the suggestions above.");
        }
        else
        {
            sb.AppendLine("? No critical issues detected. Connection should work.");
        }

        return sb.ToString();
    }
}
