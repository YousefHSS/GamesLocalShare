using System.ComponentModel;

namespace GamesLocalShare.Tests.Models;

public class GameInfoTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(1073741824L, "1 GB")]
    [InlineData(1099511627776L, "1 TB")]
    [InlineData(1610612736L, "1.5 GB")]
    public void FormattedSize_RendersHumanReadableBytes(long bytes, string expected)
    {
        var game = new GameInfo { SizeOnDisk = bytes };
        game.FormattedSize.Should().Be(expected);
    }

    [Fact]
    public void Defaults_AreSensible()
    {
        var game = new GameInfo();

        game.AppId.Should().BeEmpty();
        game.Name.Should().BeEmpty();
        game.InstallPath.Should().BeEmpty();
        game.SizeOnDisk.Should().Be(0);
        game.BuildId.Should().BeEmpty();
        game.Platform.Should().Be(GamePlatform.Steam);
        game.IsInstalled.Should().BeTrue();
        game.IsAvailableFromPeer.Should().BeFalse();
        game.IsExternal.Should().BeFalse();
        game.IsHidden.Should().BeFalse();
        game.CoverImage.Should().BeNull();
        game.CoverUrl.Should().BeNull();
    }

    [Fact]
    public void IsHidden_RaisesPropertyChanged_OnlyWhenValueChanges()
    {
        var game = new GameInfo();
        var changes = new List<string?>();
        ((INotifyPropertyChanged)game).PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        game.IsHidden = true;
        game.IsHidden = true; // no-op
        game.IsHidden = false;

        changes.Should().Equal(new[] { nameof(GameInfo.IsHidden), nameof(GameInfo.IsHidden) });
    }

    [Fact]
    public void CoverUrl_RaisesPropertyChanged_OnChange()
    {
        var game = new GameInfo();
        var changes = new List<string?>();
        ((INotifyPropertyChanged)game).PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        game.CoverUrl = "https://example.com/a.jpg";
        game.CoverUrl = "https://example.com/a.jpg"; // same
        game.CoverUrl = null;

        changes.Should().Equal(new[] { nameof(GameInfo.CoverUrl), nameof(GameInfo.CoverUrl) });
    }

    [Fact]
    public void GamePlatform_HasExpectedValues()
    {
        // Guards against accidental enum reordering — values are persisted to settings.
        Enum.GetValues<GamePlatform>().Should().BeEquivalentTo(new[]
        {
            GamePlatform.Steam,
            GamePlatform.EpicGames,
            GamePlatform.Xbox,
            GamePlatform.External,
        });
    }
}
