namespace GamesLocalShare.Tests.Services;

public class EpicGamesLibraryScannerTests
{
    [Fact]
    public void Platform_IsEpicGames()
    {
        new EpicGamesLibraryScanner().Platform.Should().Be(GamePlatform.EpicGames);
    }

    [Fact]
    public void GetManifestsDirectory_ReturnsNullOnNonWindows_OrPath_OnWindows()
    {
        var dir = new EpicGamesLibraryScanner().GetManifestsDirectory();
        if (!OperatingSystem.IsWindows())
        {
            dir.Should().BeNull();
        }
        else
        {
            // Either null (Epic not installed) or an existing path with the expected segments
            if (dir != null)
            {
                dir.Should().Contain("Epic");
                dir.Should().Contain("Manifests");
                System.IO.Directory.Exists(dir).Should().BeTrue();
            }
        }
    }

    [Fact]
    public async Task ScanGamesAsync_ReturnsList_NeverNull()
    {
        var games = await new EpicGamesLibraryScanner().ScanGamesAsync();
        games.Should().NotBeNull();
        games.Should().BeAssignableTo<IList<GameInfo>>();
    }

    [Fact]
    public void GetLibraryFolders_ReturnsList()
    {
        new EpicGamesLibraryScanner().GetLibraryFolders().Should().NotBeNull();
    }
}
