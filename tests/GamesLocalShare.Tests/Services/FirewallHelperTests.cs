namespace GamesLocalShare.Tests.Services;

public class FirewallHelperTests
{
    [Fact]
    public void IsRunningAsAdmin_ReturnsBool_NeverThrows()
    {
        var act = () => FirewallHelper.IsRunningAsAdmin();
        act.Should().NotThrow();
    }

    [Fact]
    public void IsRunningAsAdmin_OnNonWindows_AlwaysFalse()
    {
        if (OperatingSystem.IsWindows()) return;
        FirewallHelper.IsRunningAsAdmin().Should().BeFalse();
    }

    [Fact]
    public void CheckFirewallRulesExist_OnNonWindows_ReturnsTrue()
    {
        if (OperatingSystem.IsWindows()) return;
        FirewallHelper.CheckFirewallRulesExist().Should().BeTrue();
    }

    [Fact]
    public void AddFirewallRules_OnNonWindows_ReportsUnsupported()
    {
        if (OperatingSystem.IsWindows()) return;
        var (ok, msg) = FirewallHelper.AddFirewallRules();
        ok.Should().BeFalse();
        msg.Should().Contain("Windows");
    }

    [Fact]
    public void AddFirewallRules_WhenNotAdmin_ReturnsFalseWithReason()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (FirewallHelper.IsRunningAsAdmin()) return; // can't test the not-admin branch when running as admin

        var (ok, msg) = FirewallHelper.AddFirewallRules();
        ok.Should().BeFalse();
        msg.Should().Contain("Administrator");
    }

    [Fact]
    public void GetNetworkDiagnostics_RendersReportWithSections()
    {
        var report = FirewallHelper.GetNetworkDiagnostics();
        report.Should().Contain("Network Diagnostics");
        report.Should().Contain("Required Ports");
        report.Should().Contain("UDP 45677");
        report.Should().Contain("TCP 45678");
        report.Should().Contain("TCP 45679");
    }
}
