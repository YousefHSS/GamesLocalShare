using System.IO;
using GamesLocalShare.Tests.Helpers;

namespace GamesLocalShare.Tests.Services;

public class XboxSenderServiceTests
{
    [Fact]
    public void ValidateSource_WithValidMsixvcLayout_ReturnsNull()
    {
        using var temp = new TempDirectory("xbox-sender");
        var guid = Guid.NewGuid().ToString();
        temp.WriteFile($"{guid}.xvi", "");
        temp.CreateSubdir("Content");
        temp.WriteFile("Content\\data.bin", "dummy");
        temp.CreateSubdir(guid);

        var service = new XboxSenderService();
        var error = service.ValidateSource(temp.Path);

        error.Should().BeNull();
        service.State.ContentGuid.Should().Be(guid);
        service.State.GameName.Should().Be(Path.GetFileName(temp.Path));
    }

    [Fact]
    public void ValidateSource_WithoutXvi_ReturnsError()
    {
        using var temp = new TempDirectory("xbox-sender");
        temp.CreateSubdir("Content");

        var service = new XboxSenderService();
        var error = service.ValidateSource(temp.Path);

        error.Should().NotBeNull();
        error.Should().Contain("No MSIXVC envelope files");
    }

    [Fact]
    public void ValidateSource_WithoutContentFolder_ReturnsError()
    {
        using var temp = new TempDirectory("xbox-sender");
        temp.WriteFile("game.xvi", "");

        var service = new XboxSenderService();
        var error = service.ValidateSource(temp.Path);

        error.Should().NotBeNull();
        error.Should().Contain("Content subfolder not found");
    }

    [Fact]
    public async Task PrepareForNetworkAsync_BuildsCorrectManifest()
    {
        using var temp = new TempDirectory("xbox-sender");
        var guid = Guid.NewGuid().ToString();
        temp.WriteFile($"{guid}.xvi", "");
        temp.CreateSubdir("Content");
        temp.WriteFile("Content\\file1.bin", "hello");
        temp.WriteFile("Content\\sub\\file2.bin", "world");

        var service = new XboxSenderService();
        service.ValidateSource(temp.Path);

        var manifest = await service.PrepareForNetworkAsync();

        manifest.Should().NotBeNull();
        manifest.ContentGuid.Should().Be(guid);
        manifest.SourcePath.Should().Be(temp.Path);
        manifest.TotalFiles.Should().Be(2);
        manifest.TotalBytes.Should().Be(5 + 5); // "hello" + "world"
        manifest.Entries.Should().HaveCount(2);
        manifest.Entries.Should().Contain(e => e.RelativePath == Path.Combine("Content", "file1.bin"));
        manifest.Entries.Should().Contain(e => e.RelativePath == Path.Combine("Content", "sub", "file2.bin"));
    }
}
