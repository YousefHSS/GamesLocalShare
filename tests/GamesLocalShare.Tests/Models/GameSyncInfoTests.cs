namespace GamesLocalShare.Tests.Models;

public class GameSyncInfoTests
{
    private static GameInfo MakeGame(string buildId, DateTime updated, string id = "1")
        => new() { AppId = id, Name = "Game " + id, BuildId = buildId, LastUpdated = updated };

    [Fact]
    public void IsNewDownload_TrueWhenNoLocalGame()
    {
        var sync = new GameSyncInfo { RemoteGame = MakeGame("1", DateTime.Now), RemotePeer = new NetworkPeer() };
        sync.IsNewDownload.Should().BeTrue();
    }

    [Fact]
    public void IsNewDownload_TrueWhenLocalNotInstalled()
    {
        var sync = new GameSyncInfo
        {
            LocalGame = new GameInfo { IsInstalled = false },
            RemoteGame = MakeGame("1", DateTime.Now),
            RemotePeer = new NetworkPeer(),
        };
        sync.IsNewDownload.Should().BeTrue();
    }

    [Fact]
    public void LocalIsOlder_DetectsOlderBuildId()
    {
        var older = MakeGame("100", DateTime.Now);
        var newer = MakeGame("200", DateTime.Now);

        var sync = new GameSyncInfo { LocalGame = older, RemoteGame = newer, RemotePeer = new NetworkPeer() };

        sync.LocalIsOlder.Should().BeTrue();
        sync.RemoteIsOlder.Should().BeFalse();
    }

    [Fact]
    public void RemoteIsOlder_DetectsNewerLocalBuild_WithMatchingTimestamp()
    {
        // RemoteIsOlder requires LocalIsOlder == false and BuildId mismatch.
        var ts = new DateTime(2025, 1, 1);
        var local = MakeGame("200", ts);
        var remote = MakeGame("100", ts);

        var sync = new GameSyncInfo { LocalGame = local, RemoteGame = remote, RemotePeer = new NetworkPeer() };

        sync.LocalIsOlder.Should().BeFalse();
        sync.RemoteIsOlder.Should().BeTrue();
    }

    [Fact]
    public void SyncDescription_NewDownload_RendersCleanLabel()
    {
        var sync = new GameSyncInfo
        {
            RemoteGame = MakeGame("1", DateTime.Now),
            RemotePeer = new NetworkPeer(),
        };
        sync.SyncDescription.Should().Be("New Download");
    }

    [Fact]
    public void SyncDescription_Update_ShowsBuildArrow()
    {
        var sync = new GameSyncInfo
        {
            LocalGame = MakeGame("100", DateTime.Now),
            RemoteGame = MakeGame("200", DateTime.Now),
            RemotePeer = new NetworkPeer(),
        };
        sync.SyncDescription.Should().Be("Update: 100 -> 200");
    }

    [Fact]
    public void DisplayName_FallsBackToRemoteGameName()
    {
        var sync = new GameSyncInfo
        {
            RemoteGame = new GameInfo { Name = "Halo Infinite" },
            RemotePeer = new NetworkPeer(),
        };
        sync.DisplayName.Should().Be("Halo Infinite");
    }

    [Fact]
    public void FormattedSpeed_AppendsPerSecondUnit()
    {
        var sync = new GameSyncInfo
        {
            RemoteGame = new GameInfo(),
            RemotePeer = new NetworkPeer(),
            TransferSpeed = 5L * 1024L * 1024L,
        };
        sync.FormattedSpeed.Should().Be("5 MB/s");
    }
}
