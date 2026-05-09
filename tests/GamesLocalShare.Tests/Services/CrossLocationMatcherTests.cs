namespace GamesLocalShare.Tests.Services;

public class CrossLocationMatcherTests
{
    private static GameInfo Make(string appId, string name, string installPath, string buildId, DateTime updated,
        GamePlatform platform = GamePlatform.Steam, int stateFlags = 4)
        => new()
        {
            AppId = appId,
            Name = name,
            InstallPath = installPath,
            BuildId = buildId,
            LastUpdated = updated,
            Platform = platform,
            StateFlags = stateFlags,
        };

    [Fact]
    public void FilterDeviceGames_ExcludesGamesUnderExternalRoot()
    {
        var local = Make("123", "Lords", @"C:\Steam\steamapps\common\Lords", "newbuild", DateTime.UtcNow);
        var onExternal = Make("123", "Lords", @"E:\steamapps\common\Lords", "oldbuild", DateTime.UtcNow.AddDays(-1));

        var filtered = CrossLocationMatcher.FilterDeviceGames(
            new[] { local, onExternal },
            new[] { @"E:\" });

        filtered.Should().ContainSingle().Which.InstallPath.Should().Be(@"C:\Steam\steamapps\common\Lords");
    }

    [Fact]
    public void FilterDeviceGames_RootMatchIsCaseInsensitive()
    {
        var onExternal = Make("1", "Game", @"E:\steamapps\common\Game", "v1", DateTime.UtcNow);
        var filtered = CrossLocationMatcher.FilterDeviceGames(new[] { onExternal }, new[] { @"e:\" });
        filtered.Should().BeEmpty();
    }

    [Fact]
    public void FilterDeviceGames_IgnoresEmptyRoots()
    {
        var local = Make("1", "Game", @"C:\Games\Game", "v1", DateTime.UtcNow);
        var filtered = CrossLocationMatcher.FilterDeviceGames(new[] { local }, new[] { "", "   " });
        filtered.Should().ContainSingle();
    }

    [Fact]
    public void ClassifyDirection_DifferentBuildIds_PicksDirectionByLastUpdated()
    {
        var older = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var device = Make("1", "G", @"C:\G", "newbuild", newer);
        var ext = Make("1", "G", @"E:\G", "oldbuild", older);

        CrossLocationMatcher.ClassifyDirection(device, ext).Should().Be(CopyDirection.DeviceToDrive);
    }

    [Fact]
    public void ClassifyDirection_SameBuildId_IsInSync()
    {
        var t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var device = Make("1", "G", @"C:\G", "v1", t);
        var ext = Make("1", "G", @"E:\G", "v1", t);
        CrossLocationMatcher.ClassifyDirection(device, ext).Should().Be(CopyDirection.InSync);
    }

    [Fact]
    public void ClassifyDirection_NullDevice_IsOnlyOnDrive()
    {
        var ext = Make("1", "G", @"E:\G", "v1", DateTime.UtcNow);
        CrossLocationMatcher.ClassifyDirection(null, ext).Should().Be(CopyDirection.OnlyOnDrive);
    }

    // Regression: when Steam's libraryfolders.vdf registers an external drive as a Steam
    // library, SteamLibraryScanner returns the drive's copy as a "local" game alongside the
    // real on-device install. Without the FilterDeviceGames step, FindDeviceCopy would pair
    // an external entry with itself (identical BuildId) and the comparator would report
    // InSync — hiding a pending update.
    [Fact]
    public void DriveCopyRegisteredAsSteamLibrary_DoesNotMaskPendingUpdate()
    {
        var older = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var realLocal = Make("1501750", "Lords of the Fallen",
            @"C:\Program Files (x86)\Steam\steamapps\common\Lords of the Fallen",
            "newbuild", newer);

        // Same game showing up via Steam's libraryfolders.vdf because the external drive is
        // also registered as a Steam library. Note: identical AppId, identical BuildId to
        // the external scanner's entry (both read the same .acf).
        var driveAsLocal = Make("1501750", "Lords of the Fallen",
            @"E:\SteamLibrary\steamapps\common\Lords of the Fallen",
            "oldbuild", older);

        var extScannerEntry = Make("1501750", "Lords of the Fallen",
            @"E:\SteamLibrary\steamapps\common\Lords of the Fallen",
            "oldbuild", older);

        var localGames = new[] { driveAsLocal, realLocal };
        var externalRoots = new[] { @"E:\SteamLibrary" };

        var filtered = CrossLocationMatcher.FilterDeviceGames(localGames, externalRoots);
        var deviceCopy = CrossLocationMatcher.FindDeviceCopy(filtered, extScannerEntry);
        var direction = CrossLocationMatcher.ClassifyDirection(deviceCopy, extScannerEntry);

        deviceCopy.Should().BeSameAs(realLocal);
        direction.Should().Be(CopyDirection.DeviceToDrive);
    }

    // Drive copy is a generic folder dropped in via Explorer (no .gamesync_meta sidecar, so
    // BuildId is empty). Folder mtime on the drive reflects "when the user copied it there",
    // not the game's actual version — so we cannot trust LastUpdated to pick a direction.
    // Instead: if Steam's StateFlags == 4, the device install is fully current and safe to
    // treat as the source of truth. Otherwise the situation is ambiguous and needs human
    // input.
    [Fact]
    public void DriveBuildIdEmpty_DeviceFullyInstalled_RecommendsDeviceToDrive()
    {
        var device = Make("123", "Risk of Rain 2", @"F:\SteamLibrary\steamapps\common\Risk of Rain 2",
            "21587608", new DateTime(2025, 2, 20, 0, 0, 0, DateTimeKind.Utc),
            stateFlags: 4);
        var ext = Make("ext:abc", "Risk of Rain 2", @"G:\Games\Risk of Rain 2",
            buildId: "", new DateTime(2025, 4, 15, 12, 3, 31, DateTimeKind.Utc));

        CrossLocationMatcher.ClassifyDirection(device, ext).Should().Be(CopyDirection.DeviceToDrive);
    }

    [Theory]
    [InlineData(0)]      // uninstalled (shouldn't happen here, but be safe)
    [InlineData(6)]      // installed + update required
    [InlineData(16)]     // update required
    [InlineData(1026)]   // updating + queued
    [InlineData(258)]    // update running + installed
    public void DriveBuildIdEmpty_DevicePendingUpdate_IsUnknownVersion(int stateFlags)
    {
        var device = Make("123", "Game", @"F:\SteamLibrary\steamapps\common\Game",
            "21587608", new DateTime(2025, 2, 20, 0, 0, 0, DateTimeKind.Utc),
            stateFlags: stateFlags);
        var ext = Make("ext:abc", "Game", @"G:\Games\Game",
            buildId: "", new DateTime(2025, 4, 15, 12, 3, 31, DateTimeKind.Utc));

        CrossLocationMatcher.ClassifyDirection(device, ext).Should().Be(CopyDirection.UnknownVersion);
    }

    [Fact]
    public void DriveBuildIdEmpty_NonSteamDevice_IsUnknownVersion()
    {
        // Epic / Xbox / generic device install: we have no equivalent of StateFlags, so we
        // can't claim the device is "fully current" — fall back to needing human input.
        var device = Make("epic-1", "Game", @"F:\Games\Game",
            "v1", new DateTime(2025, 2, 20, 0, 0, 0, DateTimeKind.Utc),
            platform: GamePlatform.EpicGames, stateFlags: 0);
        var ext = Make("ext:abc", "Game", @"G:\Games\Game",
            buildId: "", new DateTime(2025, 4, 15, 12, 3, 31, DateTimeKind.Utc));

        CrossLocationMatcher.ClassifyDirection(device, ext).Should().Be(CopyDirection.UnknownVersion);
    }

    [Fact]
    public void BothBuildIdsEmpty_IsUnknownVersion()
    {
        var device = Make("ext:1", "Game", @"F:\Games\Game",
            buildId: "", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var ext = Make("ext:2", "Game", @"G:\Games\Game",
            buildId: "", new DateTime(2025, 4, 15, 0, 0, 0, DateTimeKind.Utc));

        CrossLocationMatcher.ClassifyDirection(device, ext).Should().Be(CopyDirection.UnknownVersion);
    }

    [Fact]
    public void BuildIdLiteralUnknown_IsTreatedAsEmpty()
    {
        // SteamManifestParser falls back to "unknown" when the .acf has no buildid line;
        // that's not a real version we can compare.
        var device = Make("1", "G", @"F:\G", "unknown",
            new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc), stateFlags: 4);
        var ext = Make("1", "G", @"G:\G", "unknown",
            new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc));

        CrossLocationMatcher.ClassifyDirection(device, ext).Should().Be(CopyDirection.UnknownVersion);
    }
}
