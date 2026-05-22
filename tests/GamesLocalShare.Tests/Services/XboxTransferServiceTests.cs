using GamesLocalShare.Tests.Helpers;

namespace GamesLocalShare.Tests.Services;

public class XboxTransferServiceTests
{
    private const string ValidSummary =
        "{\"GameName\":\"Test Game\",\"PackageFamilyName\":\"Pub.Test_abc123\"," +
        "\"SourceBytes\":123456,\"SourceFileCount\":42,\"IntegrityOk\":true}";

    [Fact]
    public void ValidateSource_WithMissingDirectory_ReturnsError()
    {
        var service = new XboxTransferService();
        var error = service.ValidateSource(@"X:\does\not\exist");

        error.Should().NotBeNull();
        error.Should().Contain("not found");
    }

    [Fact]
    public void ValidateSource_WithoutSummary_ReturnsError()
    {
        using var temp = new TempDirectory("xbox-recv");

        var error = new XboxTransferService().ValidateSource(temp.Path);

        error.Should().NotBeNull();
        error.Should().Contain("transfer-summary.json");
    }

    [Fact]
    public void ValidateSource_WithSummaryButNoXvi_ReturnsError()
    {
        using var temp = new TempDirectory("xbox-recv");
        temp.WriteFile("transfer-summary.json", ValidSummary);

        var error = new XboxTransferService().ValidateSource(temp.Path);

        error.Should().NotBeNull();
        error.Should().Contain(".xvi");
    }

    [Fact]
    public void ValidateSource_WithValidStage_ReturnsNullAndPopulatesState()
    {
        using var temp = new TempDirectory("xbox-recv");
        var guid = Guid.NewGuid().ToString();
        temp.WriteFile("transfer-summary.json", ValidSummary);
        temp.WriteFile($"{guid}.xvi", "");

        var service = new XboxTransferService();
        var error = service.ValidateSource(temp.Path);

        error.Should().BeNull();
        service.State.GameName.Should().Be("Test Game");
        service.State.PackageFamilyName.Should().Be("Pub.Test_abc123");
        service.State.SourceBytes.Should().Be(123456);
        service.State.SourceFileCount.Should().Be(42);
        service.State.ContentGuid.Should().Be(guid);
    }

    [Theory]
    [InlineData("H1_FULL_SKIP", XboxTransferVerdict.FullSkip)]
    [InlineData("H2_DELTA", XboxTransferVerdict.DeltaOnly)]
    [InlineData("H3_FULL_REDOWNLOAD", XboxTransferVerdict.FullRedownload)]
    [InlineData("STILL_PAUSED_OR_FAILED", XboxTransferVerdict.StillPaused)]
    [InlineData("PARTIAL_PROGRESS", XboxTransferVerdict.Pending)]
    [InlineData("INCONCLUSIVE", XboxTransferVerdict.Error)]
    [InlineData("", XboxTransferVerdict.Error)]
    public void MapHypothesis_MapsScriptVerdictsCorrectly(string hypothesis, XboxTransferVerdict expected)
    {
        XboxTransferService.MapHypothesis(hypothesis).Should().Be(expected);
    }
}
