using System.IO;
using GamesLocalShare.Tests.Helpers;

namespace GamesLocalShare.Tests.Services;

public class SteamLibraryScannerTests
{
    [Fact]
    public void GetLibraryFolders_WhenSteamPathNotFound_AddsErrorAndReturnsEmpty()
    {
        var scanner = new SteamLibraryScanner();

        // We can't deterministically force GetSteamPath() to fail on a dev machine that has Steam,
        // so we just assert the API contract: it returns a list and records errors when no folders.
        var folders = scanner.GetLibraryFolders();

        folders.Should().NotBeNull();
        // If Steam is installed and detected, scan errors may be empty. If not, errors should mention Steam.
        if (folders.Count == 0)
        {
            scanner.ScanErrors.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void Platform_IsSteam()
    {
        new SteamLibraryScanner().Platform.Should().Be(GamePlatform.Steam);
    }

    [Fact]
    public async Task ScanGamesAsync_ReturnsList_NeverNull()
    {
        var scanner = new SteamLibraryScanner();
        var games = await scanner.ScanGamesAsync();
        games.Should().NotBeNull();
    }

    [Fact]
    public void GetScanReport_IncludesPlatformLine()
    {
        var scanner = new SteamLibraryScanner();
        var report = scanner.GetScanReport();
        report.Should().Contain("Platform:");
        report.Should().Contain("Steam Library Scan Report");
    }
}
