namespace GamesLocalShare.Tests.Models;

public class CrossLocationGameTests
{
    [Theory]
    [InlineData(CopyDirection.InSync, "In sync", "green")]
    [InlineData(CopyDirection.DeviceToDrive, "Newer on device → copy to drive", "yellow")]
    [InlineData(CopyDirection.DriveToDevice, "Newer on drive → copy to device", "yellow")]
    [InlineData(CopyDirection.OnlyOnDevice, "Only on device", "slate")]
    [InlineData(CopyDirection.OnlyOnDrive, "Only on drive", "slate")]
    [InlineData(CopyDirection.None, "Unknown", "slate")]
    public void StatusText_And_StatusColor_MapToDirection(
        CopyDirection direction, string expectedText, string expectedColor)
    {
        var g = new CrossLocationGame { Direction = direction, Library = new ExternalLibrary() };
        g.StatusText.Should().Be(expectedText);
        g.StatusColor.Should().Be(expectedColor);
    }

    [Fact]
    public void DisplayName_PrefersDeviceCopy_FallsBackToExternal()
    {
        var withDevice = new CrossLocationGame
        {
            DeviceCopy = new GameInfo { Name = "Halo (device)" },
            ExternalCopy = new GameInfo { Name = "Halo (drive)" },
            Library = new ExternalLibrary(),
        };
        withDevice.DisplayName.Should().Be("Halo (device)");

        var driveOnly = new CrossLocationGame
        {
            ExternalCopy = new GameInfo { Name = "Halo (drive)" },
            Library = new ExternalLibrary(),
        };
        driveOnly.DisplayName.Should().Be("Halo (drive)");

        var empty = new CrossLocationGame { Library = new ExternalLibrary() };
        empty.DisplayName.Should().Be("Unknown");
    }

    [Fact]
    public void AppId_PrefersDeviceCopy_FallsBackToExternal_ThenEmpty()
    {
        new CrossLocationGame
        {
            DeviceCopy = new GameInfo { AppId = "device" },
            ExternalCopy = new GameInfo { AppId = "drive" },
            Library = new ExternalLibrary(),
        }.AppId.Should().Be("device");

        new CrossLocationGame
        {
            ExternalCopy = new GameInfo { AppId = "drive" },
            Library = new ExternalLibrary(),
        }.AppId.Should().Be("drive");

        new CrossLocationGame { Library = new ExternalLibrary() }.AppId.Should().BeEmpty();
    }
}
