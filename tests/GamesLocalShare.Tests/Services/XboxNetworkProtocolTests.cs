using System.IO;
using GamesLocalShare.Tests.Helpers;

namespace GamesLocalShare.Tests.Services;

public class XboxNetworkProtocolTests : IDisposable
{
    private readonly XboxNetworkSender _sender;
    private readonly XboxNetworkReceiver _receiver;

    public XboxNetworkProtocolTests()
    {
        _sender = new XboxNetworkSender { Port = 0 }; // 0 = auto-assign
        _receiver = new XboxNetworkReceiver();
    }

    public void Dispose()
    {
        _sender.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RoundTrip_SendsManifestAndFiles()
    {
        using var src = new TempDirectory("xbox-net-src");
        src.WriteFile("data.bin", "Hello, World!");
        src.WriteFile("sub\\nested.txt", "Nested content");

        var manifest = new XboxOverlayManifest
        {
            ContentGuid = Guid.NewGuid().ToString(),
            PackageFamilyName = "TestPkg_12345",
            SourcePath = src.Path,
            TotalFiles = 2,
            TotalBytes = 13 + 14,
            Entries = new List<XboxOverlayManifestEntry>
            {
                new() { RelativePath = "data.bin", Size = 13, LastModifiedUtc = DateTime.UtcNow },
                new() { RelativePath = Path.Combine("sub", "nested.txt"), Size = 14, LastModifiedUtc = DateTime.UtcNow }
            }
        };

        _sender.SetManifest(manifest);
        _sender.Start();

        // Get the actual port assigned
        var actualPort = _sender.Port;
        actualPort.Should().BeGreaterThan(0);

        using var dst = new TempDirectory("xbox-net-dst");
        await _receiver.ReceiveAsync("127.0.0.1", actualPort, dst.Path);

        // Verify files received
        var receivedData = File.ReadAllText(Path.Combine(dst.Path, "data.bin"));
        receivedData.Should().Be("Hello, World!");

        var receivedNested = File.ReadAllText(Path.Combine(dst.Path, "sub", "nested.txt"));
        receivedNested.Should().Be("Nested content");

        _receiver.BytesReceived.Should().Be(13 + 14);
    }

    [Fact]
    public void Start_WithPortZero_AssignsEphemeralPort()
    {
        using var sender = new XboxNetworkSender { Port = 0 };
        sender.Start();
        sender.Port.Should().BeGreaterThan(0);
        sender.Stop();
    }

    [Fact]
    public void BytesStreamed_TracksTotalSent()
    {
        _sender.BytesStreamed.Should().Be(0);
    }
}
