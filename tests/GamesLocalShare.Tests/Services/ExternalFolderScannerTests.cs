using System.IO;
using GamesLocalShare.Tests.Helpers;

namespace GamesLocalShare.Tests.Services;

public class ExternalFolderScannerTests
{
    [Fact]
    public void Platform_IsExternal()
    {
        new ExternalFolderScanner(new AppSettings()).Platform.Should().Be(GamePlatform.External);
    }

    [Fact]
    public async Task ScanGamesAsync_NoLibraries_ReturnsEmpty()
    {
        var scanner = new ExternalFolderScanner(new AppSettings());
        var games = await scanner.ScanGamesAsync();
        games.Should().BeEmpty();
        scanner.ScanErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanGamesAsync_MissingPath_RecordsError()
    {
        var settings = new AppSettings
        {
            ExternalLibraries = new List<ExternalLibrary>
            {
                new() { DisplayName = "Missing", RootPath = @"Z:\does\not\exist" },
            },
        };
        var scanner = new ExternalFolderScanner(settings);

        var games = await scanner.ScanGamesAsync();

        games.Should().BeEmpty();
        scanner.ScanErrors.Should().Contain(e => e.Contains("path not found"));
    }

    [Fact]
    public async Task ScanGamesAsync_GenericFolder_ProducesGameWithExtPrefix()
    {
        using var temp = new TempDirectory("ext-scan");
        var gamesRoot = temp.CreateSubdir("Games");
        var halo = Path.Combine(gamesRoot, "Halo");
        Directory.CreateDirectory(halo);
        File.WriteAllBytes(Path.Combine(halo, "data.bin"), new byte[1234]);

        var settings = new AppSettings
        {
            ExternalLibraries = new List<ExternalLibrary>
            {
                new() { DisplayName = "Drive", RootPath = gamesRoot },
            },
        };

        var scanner = new ExternalFolderScanner(settings);
        var games = await scanner.ScanGamesAsync();

        games.Should().ContainSingle();
        var g = games[0];
        g.Name.Should().Be("Halo");
        g.InstallPath.Should().Be(halo);
        g.AppId.Should().StartWith("ext:");
        g.Platform.Should().Be(GamePlatform.External);
        g.IsExternal.Should().BeTrue();
        g.SizeOnDisk.Should().Be(1234);
    }

    [Fact]
    public async Task ScanGamesAsync_RespectsGamesyncMetaSidecar()
    {
        using var temp = new TempDirectory("ext-meta");
        var gamesRoot = temp.CreateSubdir("Games");
        var dir = Path.Combine(gamesRoot, "Stardew");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "save.bin"), "data");
        File.WriteAllText(Path.Combine(dir, ".gamesync_meta"),
            """
            {
              "appId": "413150",
              "buildId": "build-99",
              "lastUpdated": "2024-01-15T10:00:00.0000000Z",
              "platform": "Steam"
            }
            """);

        var settings = new AppSettings
        {
            ExternalLibraries = new List<ExternalLibrary> { new() { RootPath = gamesRoot } },
        };

        var games = await new ExternalFolderScanner(settings).ScanGamesAsync();
        games.Should().ContainSingle();
        var g = games[0];
        g.AppId.Should().Be("413150");
        g.BuildId.Should().Be("build-99");
        g.Platform.Should().Be(GamePlatform.Steam);
        g.IsExternal.Should().BeTrue();
    }

    [Fact]
    public async Task ScanGamesAsync_NestedSteamLibrary_IsRecognized()
    {
        using var temp = new TempDirectory("ext-nested-steam");
        var rootDrive = temp.CreateSubdir("D");

        // rootDrive\SteamLibrary\steamapps\appmanifest_570.acf + common\Game\
        var nested = Path.Combine(rootDrive, "SteamLibrary");
        var steamapps = Path.Combine(nested, "steamapps");
        var common = Path.Combine(steamapps, "common");
        Directory.CreateDirectory(common);

        var installDir = Path.Combine(common, "Halo");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "x"), "x");

        File.WriteAllText(Path.Combine(steamapps, "appmanifest_42.acf"),
            "\"AppState\" {\n" +
            "\"appid\" \"42\"\n\"name\" \"Halo\"\n\"installdir\" \"Halo\"\n\"StateFlags\" \"4\"\n" +
            "}\n");

        var settings = new AppSettings
        {
            ExternalLibraries = new List<ExternalLibrary> { new() { RootPath = rootDrive } },
        };

        var games = await new ExternalFolderScanner(settings).ScanGamesAsync();

        games.Should().Contain(g => g.AppId == "42" && g.IsExternal);
    }

    [Fact]
    public async Task ScanGamesAsync_DirectSteamLibrary_AtRoot_IsScanned()
    {
        using var temp = new TempDirectory("ext-direct-steam");
        var library = temp.CreateSubdir("Library");
        var steamapps = Path.Combine(library, "steamapps");
        var common = Path.Combine(steamapps, "common");
        Directory.CreateDirectory(common);

        var installDir = Path.Combine(common, "TF2");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "x"), "x");

        File.WriteAllText(Path.Combine(steamapps, "appmanifest_440.acf"),
            "\"AppState\" {\n" +
            "\"appid\" \"440\"\n\"name\" \"TF2\"\n\"installdir\" \"TF2\"\n\"StateFlags\" \"4\"\n" +
            "}\n");

        var settings = new AppSettings
        {
            ExternalLibraries = new List<ExternalLibrary> { new() { RootPath = library } },
        };

        var games = await new ExternalFolderScanner(settings).ScanGamesAsync();
        games.Should().Contain(g => g.AppId == "440");
    }

    [Fact]
    public void GetLibraryFolders_FiltersToExistingPaths()
    {
        using var temp = new TempDirectory();
        var existing = temp.CreateSubdir("Real");

        var settings = new AppSettings
        {
            ExternalLibraries = new List<ExternalLibrary>
            {
                new() { RootPath = existing },
                new() { RootPath = @"Z:\does\not\exist" },
            },
        };

        var folders = new ExternalFolderScanner(settings).GetLibraryFolders();

        folders.Should().ContainSingle(f => f == existing);
    }

    [Fact]
    public async Task LoadCoverImageAsync_SkipsNetwork_WhenCoverAlreadyPresent()
    {
        var scanner = new ExternalFolderScanner(new AppSettings());
        // External games now resolve covers online (delegating to TitleCoverArtService),
        // but the loader must return immediately without touching the network when a
        // cover URL is already set — so this stays fast/offline and preserves the value.
        var game = new GameInfo { Name = "x", AppId = "ext:test", CoverUrl = "http://example/cover.jpg" };
        await scanner.LoadCoverImageAsync(game);
        game.CoverUrl.Should().Be("http://example/cover.jpg");
    }
}
