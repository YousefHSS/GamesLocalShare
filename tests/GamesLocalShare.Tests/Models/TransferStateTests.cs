using System.IO;
using GamesLocalShare.Tests.Helpers;

namespace GamesLocalShare.Tests.Models;

public class TransferStateTests
{
    [Fact]
    public void ProgressPercent_ZeroWhenTotalIsZero()
    {
        var s = new TransferState { TotalBytes = 0, TransferredBytes = 0 };
        s.ProgressPercent.Should().Be(0);
    }

    [Fact]
    public void ProgressPercent_ScalesByTransferred()
    {
        var s = new TransferState { TotalBytes = 1000, TransferredBytes = 250 };
        s.ProgressPercent.Should().Be(25);
    }

    [Fact]
    public void FormattedProgress_RendersPercentAndSizes()
    {
        var s = new TransferState { TotalBytes = 1024 * 1024, TransferredBytes = 512 * 1024 };
        s.FormattedProgress.Should().Be("50.0% (512 KB / 1 MB)");
    }

    [Fact]
    public void GetStateFilePath_PlacesSidecarInsideTargetDir()
    {
        var path = TransferState.GetStateFilePath("C:/games/Halo");
        Path.GetFileName(path).Should().Be(".gamesync_transfer");
    }

    [Fact]
    public void Save_Then_Load_RoundtripsAllFields()
    {
        using var dir = new TempDirectory("xfer-roundtrip");
        var sub = dir.CreateSubdir("Halo");
        var s = new TransferState
        {
            GameAppId = "1234",
            GameName = "Halo",
            TargetPath = sub,
            SourcePeerIp = "192.168.0.10",
            SourcePeerName = "DESKTOP-PEER",
            BuildId = "build-42",
            TotalBytes = 5000,
            TransferredBytes = 1000,
            CompletedFiles = new() { "a.bin" },
            PendingFiles = new() { new TransferFileState { RelativePath = "b.bin", Size = 4000 } },
            IsNewDownload = true,
        };

        s.Save();

        var loaded = TransferState.Load(sub);

        loaded.Should().NotBeNull();
        loaded!.GameAppId.Should().Be("1234");
        loaded.GameName.Should().Be("Halo");
        loaded.TargetPath.Should().Be(sub);
        loaded.SourcePeerName.Should().Be("DESKTOP-PEER");
        loaded.BuildId.Should().Be("build-42");
        loaded.TotalBytes.Should().Be(5000);
        loaded.TransferredBytes.Should().Be(1000);
        loaded.CompletedFiles.Should().ContainSingle().Which.Should().Be("a.bin");
        loaded.PendingFiles.Should().ContainSingle();
        loaded.PendingFiles[0].RelativePath.Should().Be("b.bin");
        loaded.PendingFiles[0].Size.Should().Be(4000);
        loaded.IsNewDownload.Should().BeTrue();
    }

    [Fact]
    public void Load_ReturnsNull_WhenSidecarMissing()
    {
        using var dir = new TempDirectory();
        TransferState.Load(dir.Path).Should().BeNull();
    }

    [Fact]
    public void Delete_RemovesSidecar()
    {
        using var dir = new TempDirectory();
        var s = new TransferState { TargetPath = dir.Path };
        s.Save();
        File.Exists(TransferState.GetStateFilePath(dir.Path)).Should().BeTrue();

        s.Delete();

        File.Exists(TransferState.GetStateFilePath(dir.Path)).Should().BeFalse();
    }

    [Fact]
    public void TransferFileState_IsComplete_TracksByteThreshold()
    {
        var f = new TransferFileState { Size = 1000, BytesTransferred = 999 };
        f.IsComplete.Should().BeFalse();

        f.BytesTransferred = 1000;
        f.IsComplete.Should().BeTrue();

        f.BytesTransferred = 1001;
        f.IsComplete.Should().BeTrue();
    }
}
