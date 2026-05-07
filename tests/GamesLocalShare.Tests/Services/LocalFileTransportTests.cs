using System.IO;
using GamesLocalShare.Tests.Helpers;

namespace GamesLocalShare.Tests.Services;

public class LocalFileTransportTests
{
    [Fact]
    public void Name_IsLocal()
    {
        new LocalFileTransport().Name.Should().Be("Local");
    }

    [Fact]
    public async Task OpenWriteAsync_CreatesParentDirectories()
    {
        using var temp = new TempDirectory();
        var transport = new LocalFileTransport();

        await using (var s = await transport.OpenWriteAsync(temp.Path, "nested/dir/out.bin"))
        {
            s.WriteByte(0x42);
        }

        File.Exists(Path.Combine(temp.Path, "nested", "dir", "out.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task OpenReadAsync_RoundtripsBytes()
    {
        using var temp = new TempDirectory();
        var transport = new LocalFileTransport();
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(Path.Combine(temp.Path, "in.bin"), payload);

        await using var stream = await transport.OpenReadAsync(temp.Path, "in.bin");
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task SetLastWriteTimeUtcAsync_UpdatesMtime()
    {
        using var temp = new TempDirectory();
        var transport = new LocalFileTransport();
        var path = Path.Combine(temp.Path, "f.bin");
        File.WriteAllText(path, "x");

        var target = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        await transport.SetLastWriteTimeUtcAsync(temp.Path, "f.bin", target);

        File.GetLastWriteTimeUtc(path).Should().BeCloseTo(target, TimeSpan.FromSeconds(2));
    }
}
