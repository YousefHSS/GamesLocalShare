using System.IO;
using GamesLocalShare.Tests.Helpers;

namespace GamesLocalShare.Tests.Services;

public class SteamManifestParserTests
{
    private static string Manifest(string body) =>
        "\"AppState\"\n" +
        "{\n" + body + "\n" +
        "}\n";

    [Fact]
    public void Parses_ValidManifest_ProducesGameInfo()
    {
        using var temp = new TempDirectory("steam-parse");
        var steamapps = temp.CreateSubdir("steamapps");
        var common = Path.Combine(steamapps, "common");
        Directory.CreateDirectory(common);

        var installDir = Path.Combine(common, "TestGame");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "game.exe"), "x"); // give the dir a file

        var manifestPath = Path.Combine(steamapps, "appmanifest_570.acf");
        File.WriteAllText(manifestPath, Manifest(
            "\"appid\" \"570\"\n" +
            "\"name\" \"Test Game\"\n" +
            "\"installdir\" \"TestGame\"\n" +
            "\"buildid\" \"100\"\n" +
            "\"SizeOnDisk\" \"123456\"\n" +
            "\"LastUpdated\" \"1700000000\"\n" +
            "\"StateFlags\" \"4\"\n"));

        var errors = new List<string>();
        var game = SteamManifestParser.ParseAppManifest(manifestPath, steamapps, errors);

        game.Should().NotBeNull();
        game!.AppId.Should().Be("570");
        game.Name.Should().Be("Test Game");
        game.InstallPath.Should().Be(installDir);
        game.BuildId.Should().Be("100");
        game.SizeOnDisk.Should().Be(123456);
        game.LastUpdated.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000).LocalDateTime);
        game.Platform.Should().Be(GamePlatform.Steam);
        game.IsInstalled.Should().BeTrue();
        // CDN URL is deterministic for numeric AppIds
        game.CoverUrl.Should().Be("https://cdn.cloudflare.steamstatic.com/steam/apps/570/header.jpg");
        errors.Should().BeEmpty();
    }

    [Fact]
    public void ReturnsNull_WhenInstallDirIsMissing()
    {
        using var temp = new TempDirectory();
        var steamapps = temp.CreateSubdir("steamapps");
        // Note: don't create the install dir
        var manifestPath = Path.Combine(steamapps, "appmanifest_570.acf");
        File.WriteAllText(manifestPath, Manifest(
            "\"appid\" \"570\"\n\"name\" \"Test\"\n\"installdir\" \"Missing\"\n\"StateFlags\" \"4\"\n"));

        var errors = new List<string>();
        var game = SteamManifestParser.ParseAppManifest(manifestPath, steamapps, errors);

        game.Should().BeNull();
        errors.Should().NotBeEmpty();
        errors[0].Should().Contain("install dir not found");
    }

    [Fact]
    public void ReturnsNull_WhenInstallDirIsEmpty()
    {
        using var temp = new TempDirectory();
        var steamapps = temp.CreateSubdir("steamapps");
        var common = Path.Combine(steamapps, "common");
        Directory.CreateDirectory(Path.Combine(common, "EmptyGame"));

        var manifestPath = Path.Combine(steamapps, "appmanifest_570.acf");
        File.WriteAllText(manifestPath, Manifest(
            "\"appid\" \"570\"\n\"name\" \"Empty\"\n\"installdir\" \"EmptyGame\"\n\"StateFlags\" \"4\"\n"));

        var errors = new List<string>();
        SteamManifestParser.ParseAppManifest(manifestPath, steamapps, errors).Should().BeNull();
        errors.Should().Contain(e => e.Contains("install dir is empty"));
    }

    [Fact]
    public void ReturnsNull_WhenStateFlagsIsZero()
    {
        using var temp = new TempDirectory();
        var steamapps = temp.CreateSubdir("steamapps");
        var common = Path.Combine(steamapps, "common");
        var dir = Path.Combine(common, "Game");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "x"), "x");

        var manifestPath = Path.Combine(steamapps, "appmanifest_570.acf");
        File.WriteAllText(manifestPath, Manifest(
            "\"appid\" \"570\"\n\"name\" \"X\"\n\"installdir\" \"Game\"\n\"StateFlags\" \"0\"\n"));

        var errors = new List<string>();
        SteamManifestParser.ParseAppManifest(manifestPath, steamapps, errors).Should().BeNull();
        errors.Should().Contain(e => e.Contains("StateFlags=0"));
    }

    [Fact]
    public void ReturnsNull_WhenAppIdMissing()
    {
        using var temp = new TempDirectory();
        var steamapps = temp.CreateSubdir("steamapps");

        var manifestPath = Path.Combine(steamapps, "appmanifest_570.acf");
        File.WriteAllText(manifestPath, Manifest("\"name\" \"NoAppId\"\n"));

        SteamManifestParser.ParseAppManifest(manifestPath, steamapps).Should().BeNull();
    }

    [Fact]
    public void NonNumericAppId_ProducesNullCoverUrl()
    {
        using var temp = new TempDirectory();
        var steamapps = temp.CreateSubdir("steamapps");
        var common = Path.Combine(steamapps, "common");
        var dir = Path.Combine(common, "Mod");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "x"), "x");

        var manifestPath = Path.Combine(steamapps, "appmanifest_abc.acf");
        File.WriteAllText(manifestPath, Manifest(
            "\"appid\" \"abc\"\n\"name\" \"Mod\"\n\"installdir\" \"Mod\"\n\"StateFlags\" \"4\"\n"));

        var game = SteamManifestParser.ParseAppManifest(manifestPath, steamapps);
        game.Should().NotBeNull();
        game!.CoverUrl.Should().BeNull();
    }

    [Fact]
    public void ZeroSizeOnDisk_FallsBackToDirectorySize()
    {
        using var temp = new TempDirectory();
        var steamapps = temp.CreateSubdir("steamapps");
        var common = Path.Combine(steamapps, "common");
        var dir = Path.Combine(common, "MyGame");
        Directory.CreateDirectory(dir);
        var bytes = new byte[5000];
        File.WriteAllBytes(Path.Combine(dir, "data.bin"), bytes);

        var manifestPath = Path.Combine(steamapps, "appmanifest_42.acf");
        File.WriteAllText(manifestPath, Manifest(
            "\"appid\" \"42\"\n\"name\" \"MyGame\"\n\"installdir\" \"MyGame\"\n\"SizeOnDisk\" \"0\"\n\"StateFlags\" \"4\"\n"));

        var game = SteamManifestParser.ParseAppManifest(manifestPath, steamapps);
        game!.SizeOnDisk.Should().Be(5000);
    }

    [Fact]
    public void MalformedManifest_RecordsError()
    {
        using var temp = new TempDirectory();
        var steamapps = temp.CreateSubdir("steamapps");
        var manifestPath = Path.Combine(steamapps, "appmanifest_bad.acf");
        File.WriteAllText(manifestPath, "this is not vdf {");

        var errors = new List<string>();
        SteamManifestParser.ParseAppManifest(manifestPath, steamapps, errors).Should().BeNull();
        errors.Should().NotBeEmpty();
    }
}
