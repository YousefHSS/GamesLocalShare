namespace GamesLocalShare.Tests.Models;

public class NetworkPeerTests
{
    [Fact]
    public void DefaultPorts_MatchProtocolConvention()
    {
        var peer = new NetworkPeer();
        peer.Port.Should().Be(45678);
        peer.FileTransferPort.Should().Be(45679);
    }

    [Fact]
    public void DefaultPeerId_IsUniqueGuid()
    {
        var a = new NetworkPeer();
        var b = new NetworkPeer();
        a.PeerId.Should().NotBe(b.PeerId);
        Guid.TryParse(a.PeerId, out _).Should().BeTrue();
    }

    [Fact]
    public void IsOnline_TrueWhenLastSeenRecent()
    {
        var peer = new NetworkPeer { LastSeen = DateTime.Now };
        peer.IsOnline.Should().BeTrue();
    }

    [Fact]
    public void IsOnline_FalseWhenLastSeenStale()
    {
        var peer = new NetworkPeer { LastSeen = DateTime.Now.AddMinutes(-3) };
        peer.IsOnline.Should().BeFalse();
    }

    [Fact]
    public void MarkAsSeen_UpdatesTimestampToNow()
    {
        var peer = new NetworkPeer { LastSeen = DateTime.Now.AddMinutes(-10) };
        peer.MarkAsSeen();
        (DateTime.Now - peer.LastSeen).TotalSeconds.Should().BeLessThan(2);
        peer.IsOnline.Should().BeTrue();
    }
}
