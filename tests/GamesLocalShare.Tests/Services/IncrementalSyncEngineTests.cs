using System.IO;
using GamesLocalShare.Tests.Helpers;

namespace GamesLocalShare.Tests.Services;

public class IncrementalSyncEngineTests
{
    [Fact]
    public async Task BuildFileManifestAsync_HashesEveryFile_AndReportsProgress()
    {
        using var temp = new TempDirectory();
        var src = temp.CreateSubdir("src");
        File.WriteAllBytes(Path.Combine(src, "a.bin"), new byte[1024]);
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllBytes(Path.Combine(src, "sub", "b.bin"), new byte[2048]);

        // sentinel files that should be excluded:
        File.WriteAllText(Path.Combine(src, ".gamesync_meta"), "{}");
        File.WriteAllText(Path.Combine(src, ".gamesync_transfer"), "{}");

        var engine = new IncrementalSyncEngine();
        var progressCalls = 0;
        var manifest = await engine.BuildFileManifestAsync(src, (_, _, _) => progressCalls++);

        manifest.GamePath.Should().Be(src);
        manifest.Files.Should().HaveCount(2);
        manifest.Files.Should().AllSatisfy(f =>
        {
            f.Hash.Should().NotBeEmpty();
            f.Size.Should().BeGreaterThan(0);
        });
        progressCalls.Should().BeGreaterThanOrEqualTo(2);
        manifest.Files.Should().NotContain(f => f.RelativePath.Contains(".gamesync_meta"));
        manifest.Files.Should().NotContain(f => f.RelativePath.Contains(".gamesync_transfer"));
    }

    [Fact]
    public async Task GetFilesToSyncAsync_NewDestination_NeedsAllFiles()
    {
        using var temp = new TempDirectory();
        var src = temp.CreateSubdir("src");
        var dst = temp.CreateSubdir("dst");
        File.WriteAllBytes(Path.Combine(src, "a.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(src, "b.bin"), new byte[200]);

        var engine = new IncrementalSyncEngine();
        var manifest = await engine.BuildFileManifestAsync(src);

        var toSync = await engine.GetFilesToSyncAsync(dst, manifest.Files);
        toSync.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetFilesToSyncAsync_MatchingTimestamp_SkipsCopy()
    {
        using var temp = new TempDirectory();
        var src = temp.CreateSubdir("src");
        var dst = temp.CreateSubdir("dst");
        File.WriteAllBytes(Path.Combine(src, "a.bin"), new byte[500]);
        File.WriteAllBytes(Path.Combine(dst, "a.bin"), new byte[500]);

        // Match timestamps within tolerance
        var t = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(Path.Combine(src, "a.bin"), t);
        File.SetLastWriteTimeUtc(Path.Combine(dst, "a.bin"), t);

        var engine = new IncrementalSyncEngine();
        var manifest = await engine.BuildFileManifestAsync(src);

        var toSync = await engine.GetFilesToSyncAsync(dst, manifest.Files);
        toSync.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFilesToSyncAsync_DifferentSize_RequiresCopy()
    {
        using var temp = new TempDirectory();
        var src = temp.CreateSubdir("src");
        var dst = temp.CreateSubdir("dst");
        File.WriteAllBytes(Path.Combine(src, "a.bin"), new byte[500]);
        File.WriteAllBytes(Path.Combine(dst, "a.bin"), new byte[400]);

        var engine = new IncrementalSyncEngine();
        var manifest = await engine.BuildFileManifestAsync(src);

        var toSync = await engine.GetFilesToSyncAsync(dst, manifest.Files);
        toSync.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetFilesToSyncAsync_DifferentHash_RequiresCopy()
    {
        using var temp = new TempDirectory();
        var src = temp.CreateSubdir("src");
        var dst = temp.CreateSubdir("dst");
        File.WriteAllBytes(Path.Combine(src, "a.bin"), Enumerable.Repeat<byte>(0xAA, 500).ToArray());
        File.WriteAllBytes(Path.Combine(dst, "a.bin"), Enumerable.Repeat<byte>(0xBB, 500).ToArray());

        // Make timestamps differ to force hash check
        File.SetLastWriteTimeUtc(Path.Combine(src, "a.bin"), DateTime.UtcNow);
        File.SetLastWriteTimeUtc(Path.Combine(dst, "a.bin"), DateTime.UtcNow.AddDays(-30));

        var engine = new IncrementalSyncEngine();
        var manifest = await engine.BuildFileManifestAsync(src);

        var toSync = await engine.GetFilesToSyncAsync(dst, manifest.Files);
        toSync.Should().HaveCount(1, "hashes differ so the file must be copied");
    }

    [Fact]
    public async Task CopyGameAsync_PerformsFullCopy_AndWritesGamesyncMeta()
    {
        using var temp = new TempDirectory();
        var src = temp.CreateSubdir("src");
        var dst = temp.CreateSubdir("dst");
        File.WriteAllBytes(Path.Combine(src, "a.bin"), new byte[256]);
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllBytes(Path.Combine(src, "sub", "b.bin"), new byte[1024]);

        var engine = new IncrementalSyncEngine();
        var info = new GameInfo
        {
            AppId = "test", Name = "TestGame",
            BuildId = "v1", Platform = GamePlatform.Steam,
            LastUpdated = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var ok = await engine.CopyGameAsync(src, dst, info, new LocalFileTransport(), null, CancellationToken.None);

        ok.Should().BeTrue();
        File.Exists(Path.Combine(dst, "a.bin")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "sub", "b.bin")).Should().BeTrue();
        File.Exists(Path.Combine(dst, ".gamesync_meta")).Should().BeTrue();
        // No leftover transfer state on success
        File.Exists(Path.Combine(dst, ".gamesync_transfer")).Should().BeFalse();

        var meta = File.ReadAllText(Path.Combine(dst, ".gamesync_meta"));
        meta.Should().Contain("\"appId\":\"test\"");
        meta.Should().Contain("\"buildId\":\"v1\"");
    }

    [Fact]
    public async Task CopyGameAsync_RaisesProgressEvents_DuringTransfer()
    {
        using var temp = new TempDirectory();
        var src = temp.CreateSubdir("src");
        var dst = temp.CreateSubdir("dst");
        // Use 2 MB so progress sampling has time to fire
        File.WriteAllBytes(Path.Combine(src, "big.bin"), new byte[2 * 1024 * 1024]);

        var engine = new IncrementalSyncEngine();
        var progressEvents = new List<TransferProgressEventArgs>();
        engine.ProgressChanged += (_, e) => progressEvents.Add(e);

        await engine.CopyGameAsync(src, dst,
            new GameInfo { AppId = "1", Name = "Big", BuildId = "1" },
            new LocalFileTransport(), null, CancellationToken.None);

        progressEvents.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CopyGameAsync_Cancellation_ThrowsOperationCanceled()
    {
        using var temp = new TempDirectory();
        var src = temp.CreateSubdir("src");
        var dst = temp.CreateSubdir("dst");
        File.WriteAllBytes(Path.Combine(src, "big.bin"), new byte[8 * 1024 * 1024]);

        var engine = new IncrementalSyncEngine();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await engine.CopyGameAsync(src, dst,
            new GameInfo { AppId = "1", Name = "X", BuildId = "1" },
            new LocalFileTransport(), null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void ComputeQuickHash_StableAcrossCalls_ForSameContent()
    {
        using var temp = new TempDirectory();
        var p1 = Path.Combine(temp.Path, "a.bin");
        var p2 = Path.Combine(temp.Path, "b.bin");
        var bytes = new byte[1000];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
        File.WriteAllBytes(p1, bytes);
        File.WriteAllBytes(p2, bytes);

        var h1 = IncrementalSyncEngine.ComputeQuickHash(p1, bytes.Length);
        var h2 = IncrementalSyncEngine.ComputeQuickHash(p2, bytes.Length);

        h1.Should().NotBeEmpty();
        h1.Should().Be(h2);
    }

    [Fact]
    public void ComputeQuickHash_DiffersForDifferentContent()
    {
        using var temp = new TempDirectory();
        var p1 = Path.Combine(temp.Path, "a.bin");
        var p2 = Path.Combine(temp.Path, "b.bin");
        var bytesA = new byte[1000];
        var bytesB = new byte[1000];
        for (int i = 0; i < bytesA.Length; i++) bytesA[i] = (byte)(i & 0xFF);
        for (int i = 0; i < bytesB.Length; i++) bytesB[i] = (byte)((i + 1) & 0xFF);
        File.WriteAllBytes(p1, bytesA);
        File.WriteAllBytes(p2, bytesB);

        var hA = IncrementalSyncEngine.ComputeQuickHash(p1, bytesA.Length);
        var hB = IncrementalSyncEngine.ComputeQuickHash(p2, bytesB.Length);

        hA.Should().NotBe(hB);
    }

    [Fact]
    public void ComputeQuickHash_LargeFile_HashesByEdgesAndMiddle()
    {
        using var temp = new TempDirectory();
        var p = Path.Combine(temp.Path, "large.bin");
        // 5 MB file (>2 MB threshold)
        var bytes = new byte[5 * 1024 * 1024];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
        File.WriteAllBytes(p, bytes);

        var hash = IncrementalSyncEngine.ComputeQuickHash(p, bytes.Length);
        hash.Should().NotBeEmpty();
    }

    [Fact]
    public async Task VerifyCompletedFilesAsync_FlagsMissingFiles()
    {
        using var temp = new TempDirectory();
        var src = temp.CreateSubdir("src");
        var dst = temp.CreateSubdir("dst");
        File.WriteAllBytes(Path.Combine(src, "a.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(src, "b.bin"), new byte[200]);

        var engine = new IncrementalSyncEngine();
        var manifest = await engine.BuildFileManifestAsync(src);
        var state = new TransferState { TargetPath = dst, CompletedFiles = new() { "a.bin", "b.bin" } };

        // Only a.bin is actually present in dst
        File.WriteAllBytes(Path.Combine(dst, "a.bin"), new byte[100]);

        var corrupted = await engine.VerifyCompletedFilesAsync(dst, manifest.Files, state);
        corrupted.Should().Contain("b.bin");
    }
}
