namespace GamesLocalShare.Tests.Services;

public class StartupHelperTests
{
    [Fact]
    public void IsStartupEnabled_ReturnsBool_NeverThrows()
    {
        // Read-only check; should be safe regardless of OS or registry state.
        var act = () => StartupHelper.IsStartupEnabled();
        act.Should().NotThrow();
    }

    [Fact]
    public void IsStartupEnabled_OnNonWindows_AlwaysFalse()
    {
        if (OperatingSystem.IsWindows()) return; // skip on Windows
        StartupHelper.IsStartupEnabled().Should().BeFalse();
    }

    [Fact]
    public void SetStartupEnabled_OnNonWindows_ReturnsFalse_AndDoesNotThrow()
    {
        if (OperatingSystem.IsWindows()) return; // skip on Windows
        StartupHelper.SetStartupEnabled(true).Should().BeFalse();
        StartupHelper.SetStartupEnabled(false).Should().BeFalse();
    }
}
