namespace GamesLocalShare.Tests.Features;

/// <summary>
/// Cross-location game comparisons drive the Drives panel: given a game's
/// presence on the device and on an external library, the UI shows what
/// direction (if any) a copy should go.
/// </summary>
public class CrossLocationGameFeatureTests
{
    private static GameInfo Make(string id, string buildId, DateTime updated)
        => new() { AppId = id, Name = "Game " + id, BuildId = buildId, LastUpdated = updated };

    private static CopyDirection Compare(GameInfo? device, GameInfo? external)
    {
        // Reproduce the conceptual rule (the actual implementation lives in MainViewModel).
        if (device == null && external == null) return CopyDirection.None;
        if (device == null) return CopyDirection.OnlyOnDrive;
        if (external == null) return CopyDirection.OnlyOnDevice;
        if (device.BuildId == external.BuildId && device.LastUpdated == external.LastUpdated)
            return CopyDirection.InSync;
        return device.LastUpdated > external.LastUpdated
            ? CopyDirection.DeviceToDrive
            : CopyDirection.DriveToDevice;
    }

    [Fact]
    public void OnlyOnDevice_WhenExternalMissing()
    {
        Compare(Make("1", "v1", DateTime.UtcNow), null).Should().Be(CopyDirection.OnlyOnDevice);
    }

    [Fact]
    public void OnlyOnDrive_WhenDeviceMissing()
    {
        Compare(null, Make("1", "v1", DateTime.UtcNow)).Should().Be(CopyDirection.OnlyOnDrive);
    }

    [Fact]
    public void InSync_WhenIdentical()
    {
        var t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Compare(Make("1", "v1", t), Make("1", "v1", t)).Should().Be(CopyDirection.InSync);
    }

    [Fact]
    public void DeviceToDrive_WhenDeviceNewer()
    {
        var older = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        Compare(Make("1", "v2", newer), Make("1", "v1", older)).Should().Be(CopyDirection.DeviceToDrive);
    }

    [Fact]
    public void DriveToDevice_WhenDriveNewer()
    {
        var older = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        Compare(Make("1", "v1", older), Make("1", "v2", newer)).Should().Be(CopyDirection.DriveToDevice);
    }

    [Theory]
    [InlineData(CopyDirection.DeviceToDrive)]
    [InlineData(CopyDirection.DriveToDevice)]
    [InlineData(CopyDirection.OnlyOnDevice)]
    [InlineData(CopyDirection.OnlyOnDrive)]
    public void DirectionsThatRequireAction_HaveMeaningfulStatusText(CopyDirection direction)
    {
        var g = new CrossLocationGame { Direction = direction, Library = new ExternalLibrary() };
        g.StatusText.Should().NotBe("Unknown").And.NotBeEmpty();
    }
}
