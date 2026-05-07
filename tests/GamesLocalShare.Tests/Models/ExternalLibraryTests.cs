namespace GamesLocalShare.Tests.Models;

public class ExternalLibraryTests
{
    [Fact]
    public void Defaults_AreSensible()
    {
        var lib = new ExternalLibrary();
        lib.Id.Should().NotBe(Guid.Empty);
        lib.DisplayName.Should().BeEmpty();
        lib.RootPath.Should().BeEmpty();
        lib.DriveSerial.Should().BeEmpty();
        lib.IsRemovable.Should().BeFalse();
        lib.ScanSubfolders.Should().BeTrue();
    }

    [Fact]
    public void Id_IsUniquePerInstance()
    {
        new ExternalLibrary().Id.Should().NotBe(new ExternalLibrary().Id);
    }
}
