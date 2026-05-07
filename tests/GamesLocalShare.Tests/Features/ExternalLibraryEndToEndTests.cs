using System.IO;
using GamesLocalShare.Tests.Helpers;

namespace GamesLocalShare.Tests.Features;

/// <summary>
/// Feature tests for the "External Library" pipeline. These wire AppSettings,
/// ExternalFolderScanner, and IncrementalSyncEngine together against a real
/// temp filesystem to exercise the full add-library → scan → copy round-trip
/// without mocks.
/// </summary>
public class ExternalLibraryEndToEndTests
{
    [Fact]
    public async Task AddLibrary_ScanGames_CopyToExternalDrive_RoundTrip()
    {
        using var temp = new TempDirectory("feature-external");

        // 1. Source: a "Steam"-style library with one game on the device
        var deviceSteamapps = temp.CreateSubdir("device/steamapps");
        Directory.CreateDirectory(Path.Combine(deviceSteamapps, "common"));
        var gameSrc = Path.Combine(deviceSteamapps, "common", "Halo");
        Directory.CreateDirectory(gameSrc);
        File.WriteAllBytes(Path.Combine(gameSrc, "halo.exe"), new byte[5_000]);
        Directory.CreateDirectory(Path.Combine(gameSrc, "data"));
        File.WriteAllBytes(Path.Combine(gameSrc, "data", "level1.bin"), new byte[10_000]);
        File.WriteAllText(Path.Combine(deviceSteamapps, "appmanifest_42.acf"),
            "\"AppState\" {\n" +
            "\"appid\" \"42\"\n\"name\" \"Halo\"\n\"installdir\" \"Halo\"\n\"buildid\" \"v1\"\n\"StateFlags\" \"4\"\n" +
            "}\n");

        // 2. Destination: an external "drive" folder configured as an external library.
        //    Point the library directly at the games root so an empty initial scan finds nothing.
        var external = temp.CreateSubdir("drive_Games");

        var settings = new AppSettings
        {
            ExternalLibraries = new List<ExternalLibrary>
            {
                new() { DisplayName = "Backup Drive", RootPath = external },
            },
        };

        // 3. Scan the external library — should be empty initially
        var scanner = new ExternalFolderScanner(settings);
        (await scanner.ScanGamesAsync()).Should().BeEmpty();

        // 4. Copy the device game to the external drive via IncrementalSyncEngine
        var engine = new IncrementalSyncEngine();
        var info = new GameInfo
        {
            AppId = "42", Name = "Halo", BuildId = "v1",
            Platform = GamePlatform.Steam, LastUpdated = DateTime.UtcNow,
        };
        var dest = Path.Combine(external, "Halo");
        var ok = await engine.CopyGameAsync(gameSrc, dest, info, new LocalFileTransport(),
            null, CancellationToken.None);
        ok.Should().BeTrue();

        // 5. Re-scan the external library — should now find the copied game with the metadata sidecar
        var rescan = await new ExternalFolderScanner(settings).ScanGamesAsync();
        rescan.Should().ContainSingle();
        rescan[0].AppId.Should().Be("42");
        rescan[0].BuildId.Should().Be("v1");
        rescan[0].Platform.Should().Be(GamePlatform.Steam);
        rescan[0].IsExternal.Should().BeTrue();

        // 6. A second copy is a no-op (incremental sync skips identical files)
        var engine2 = new IncrementalSyncEngine();
        var changedFiles = new List<string>();
        engine2.LogMessageRaised += (_, msg) => changedFiles.Add(msg);
        await engine2.CopyGameAsync(gameSrc, dest, info, new LocalFileTransport(),
            null, CancellationToken.None);

        // The "Local copy complete" message is logged, but no files should need transferring
        changedFiles.Should().NotBeEmpty();
        changedFiles.Should().Contain(m => m.Contains("Incremental sync analysis"));
        var analysisLine = changedFiles.First(m => m.Contains("Incremental sync analysis"));
        analysisLine.Should().Contain("total to transfer=0");
    }

    [Fact]
    public async Task ResumeAfterPartialCopy_CompletesRemainingFiles()
    {
        using var temp = new TempDirectory("feature-resume");
        var src = temp.CreateSubdir("src");
        var dst = temp.CreateSubdir("dst");

        for (int i = 0; i < 5; i++)
        {
            File.WriteAllBytes(Path.Combine(src, $"f{i}.bin"), new byte[1024 * (i + 1)]);
        }

        var info = new GameInfo
        {
            AppId = "1", Name = "ResumableGame", BuildId = "v1",
            Platform = GamePlatform.Steam, LastUpdated = DateTime.UtcNow,
        };

        // First, partial: cancel after a moment
        var engine = new IncrementalSyncEngine();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));
        try
        {
            await engine.CopyGameAsync(src, dst, info, new LocalFileTransport(), null, cts.Token);
        }
        catch (OperationCanceledException) { /* expected */ }

        // Resume — pass the persisted state file
        var state = TransferState.Load(dst);
        var resumeEngine = new IncrementalSyncEngine();
        var ok = await resumeEngine.CopyGameAsync(src, dst, info, new LocalFileTransport(),
            state, CancellationToken.None);

        ok.Should().BeTrue();
        // All 5 source files must be at the destination after resume
        for (int i = 0; i < 5; i++)
        {
            var fp = Path.Combine(dst, $"f{i}.bin");
            File.Exists(fp).Should().BeTrue();
            new FileInfo(fp).Length.Should().Be(1024 * (i + 1));
        }
        // Transfer state cleared on success
        File.Exists(Path.Combine(dst, ".gamesync_transfer")).Should().BeFalse();
    }
}
