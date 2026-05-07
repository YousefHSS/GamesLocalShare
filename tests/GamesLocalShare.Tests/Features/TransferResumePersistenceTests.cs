using System.IO;
using GamesLocalShare.Tests.Helpers;

namespace GamesLocalShare.Tests.Features;

/// <summary>
/// The transfer state sidecar is the single source of truth for resuming after
/// the app is killed mid-transfer. These tests exercise the save → kill → reload
/// scenario that the auto-resume feature depends on.
/// </summary>
public class TransferResumePersistenceTests
{
    [Fact]
    public void State_Survives_AppRestart_SimulatedByReload()
    {
        using var temp = new TempDirectory("resume-state");
        var target = temp.CreateSubdir("Halo");

        var state = new TransferState
        {
            GameAppId = "42",
            GameName = "Halo",
            TargetPath = target,
            SourcePeerIp = "192.168.0.1",
            SourcePeerName = "PEER",
            BuildId = "build-1",
            TotalBytes = 10_000,
            TransferredBytes = 4_000,
            CompletedFiles = new() { "intro.bin", "menu.bin" },
            PendingFiles = new()
            {
                new TransferFileState { RelativePath = "level1.bin", Size = 6_000, BytesTransferred = 0 },
            },
            IsNewDownload = true,
            StartedAt = new DateTime(2025, 5, 1, 12, 0, 0, DateTimeKind.Utc),
        };

        state.Save();

        // Simulate app restart
        var loaded = TransferState.Load(target);

        loaded.Should().NotBeNull();
        loaded!.GameAppId.Should().Be("42");
        loaded.TransferredBytes.Should().Be(4_000);
        loaded.ProgressPercent.Should().Be(40);
        loaded.CompletedFiles.Should().BeEquivalentTo(new[] { "intro.bin", "menu.bin" });
        loaded.PendingFiles.Should().HaveCount(1);
        loaded.PendingFiles[0].RelativePath.Should().Be("level1.bin");
    }

    [Fact]
    public void DiscoverIncompleteTransfers_FindsAllSidecarFiles()
    {
        using var temp = new TempDirectory("resume-discover");

        // Set up multiple in-progress transfers, each with a sidecar
        var halo = temp.CreateSubdir("Halo");
        new TransferState { TargetPath = halo, GameAppId = "42", GameName = "Halo" }.Save();

        var hl2 = temp.CreateSubdir("HL2");
        new TransferState { TargetPath = hl2, GameAppId = "220", GameName = "Half-Life 2" }.Save();

        // A folder without a sidecar should not be reported
        temp.CreateSubdir("Other");

        // Simulate the discovery the app does at startup
        var discovered = Directory.GetDirectories(temp.Path)
            .Select(TransferState.Load)
            .Where(s => s != null)
            .Cast<TransferState>()
            .ToList();

        discovered.Should().HaveCount(2);
        discovered.Select(s => s.GameAppId).Should().BeEquivalentTo(new[] { "42", "220" });
    }
}
