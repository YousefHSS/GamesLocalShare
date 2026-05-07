namespace GamesLocalShare.Tests.Models;

public class LogMessageTests
{
    [Fact]
    public void Constructor_AssignsMessageAndType_AndStampsNow()
    {
        var before = DateTime.Now.AddSeconds(-1);
        var m = new LogMessage("hello", LogMessageType.Warning);
        var after = DateTime.Now.AddSeconds(1);

        m.Message.Should().Be("hello");
        m.Type.Should().Be(LogMessageType.Warning);
        m.Timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Theory]
    [InlineData(LogMessageType.Success, "#22C55E")]
    [InlineData(LogMessageType.Error, "#EF4444")]
    [InlineData(LogMessageType.Warning, "#F59E0B")]
    [InlineData(LogMessageType.Transfer, "#8B5CF6")]
    [InlineData(LogMessageType.Network, "#3B82F6")]
    [InlineData(LogMessageType.Info, "#9CA3AF")]
    public void TypeColor_MapsToType(LogMessageType type, string expected)
    {
        new LogMessage("x", type).TypeColor.Should().Be(expected);
    }

    [Fact]
    public void FormattedTime_UsesHmsFormat()
    {
        var m = new LogMessage("x") { Timestamp = new DateTime(2025, 1, 1, 14, 5, 9) };
        m.FormattedTime.Should().Be("14:05:09");
    }

    [Fact]
    public void DefaultConstructor_SetsInfoType()
    {
        var m = new LogMessage();
        m.Type.Should().Be(LogMessageType.Info);
        m.Message.Should().BeEmpty();
    }
}
