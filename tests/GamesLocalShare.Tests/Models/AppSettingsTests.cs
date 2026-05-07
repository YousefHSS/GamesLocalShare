using System.IO;
using System.Reflection;
using System.Text.Json;
using GamesLocalShare.Tests.Helpers;

namespace GamesLocalShare.Tests.Models;

/// <summary>
/// Tests for AppSettings.
///
/// AppSettings.Save/Load use a fixed path under %APPDATA%\GamesLocalShare\ via static readonly fields.
/// Because these tests run on a developer machine that may also have real settings,
/// the singleton-touching tests use the IsolatedSingleton fixture to back up and
/// restore the user's actual settings file. Pure JSON shape and in-memory tests
/// don't need that.
/// </summary>
[Collection(AppSettingsCollection.Name)]
public class AppSettingsTests
{
    [Fact]
    public void Defaults_AreSensible()
    {
        var s = new AppSettings();
        s.StartWithWindows.Should().BeFalse();
        s.AutoUpdateGames.Should().BeFalse();
        s.MinimizeToTray.Should().BeFalse();
        s.AutoStartNetwork.Should().BeFalse();
        s.AutoResumeDownloads.Should().BeFalse();
        s.AutoUpdateCheckInterval.Should().Be(30);
        s.HiddenGameIds.Should().BeEmpty();
        s.EpicInstallRoot.Should().BeNull();
        s.ExternalLibraries.Should().BeEmpty();
    }

    [Fact]
    public void IsGameHidden_TracksHiddenGameIds()
    {
        var s = new AppSettings();
        s.HiddenGameIds.Add("570");
        s.IsGameHidden("570").Should().BeTrue();
        s.IsGameHidden("nope").Should().BeFalse();
    }

    [Fact]
    public void Json_Roundtrip_PreservesAllFields()
    {
        var s = new AppSettings
        {
            StartWithWindows = true,
            AutoUpdateGames = true,
            MinimizeToTray = true,
            AutoStartNetwork = true,
            AutoResumeDownloads = true,
            AutoUpdateCheckInterval = 60,
            EpicInstallRoot = @"D:\Epic Games",
            HiddenGameIds = new HashSet<string> { "570", "440" },
            ExternalLibraries = new List<ExternalLibrary>
            {
                new() { DisplayName = "External", RootPath = @"D:\Games", DriveSerial = "D:", IsRemovable = true, ScanSubfolders = false },
            },
        };

        var json = JsonSerializer.Serialize(s);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);

        loaded.Should().NotBeNull();
        loaded!.StartWithWindows.Should().BeTrue();
        loaded.AutoUpdateGames.Should().BeTrue();
        loaded.MinimizeToTray.Should().BeTrue();
        loaded.AutoStartNetwork.Should().BeTrue();
        loaded.AutoResumeDownloads.Should().BeTrue();
        loaded.AutoUpdateCheckInterval.Should().Be(60);
        loaded.EpicInstallRoot.Should().Be(@"D:\Epic Games");
        loaded.HiddenGameIds.Should().BeEquivalentTo(new[] { "570", "440" });
        loaded.ExternalLibraries.Should().HaveCount(1);
        loaded.ExternalLibraries[0].DisplayName.Should().Be("External");
        loaded.ExternalLibraries[0].RootPath.Should().Be(@"D:\Games");
        loaded.ExternalLibraries[0].ScanSubfolders.Should().BeFalse();
    }

    [Fact]
    public void GetSettingsFilePath_PointsAtAppDataLocation()
    {
        var path = AppSettings.GetSettingsFilePath();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        path.Should().StartWith(appData);
        path.Should().EndWith("settings.json");
        Path.GetFileName(path).Should().Be("settings.json");
    }

    [Fact]
    public void HideGame_And_UnhideGame_MutateHiddenSet()
    {
        // This test mutates the user's settings file; isolate by snapshotting and restoring.
        using var iso = new AppSettingsIsolation();

        var s = AppSettings.Load();
        var originalCount = s.HiddenGameIds.Count;
        const string testId = "__test_app_id_xyz";

        try
        {
            s.HideGame(testId);
            AppSettings.Reload();
            AppSettings.Load().IsGameHidden(testId).Should().BeTrue();

            AppSettings.Load().UnhideGame(testId);
            AppSettings.Reload();
            AppSettings.Load().IsGameHidden(testId).Should().BeFalse();

            AppSettings.Load().HiddenGameIds.Count.Should().Be(originalCount);
        }
        finally
        {
            // Defensive: ensure our test id is gone even if assertions fail
            var current = AppSettings.Load();
            if (current.HiddenGameIds.Contains(testId))
            {
                current.HiddenGameIds.Remove(testId);
                current.Save();
            }
        }
    }
}

/// <summary>
/// Backs up the user's AppSettings file at construction and restores it on dispose.
/// Used to keep developer machine state stable when a test exercises AppSettings.Save/Load.
/// </summary>
internal sealed class AppSettingsIsolation : IDisposable
{
    private readonly string _settingsPath;
    private readonly string? _backupPath;
    private readonly bool _existed;

    public AppSettingsIsolation()
    {
        _settingsPath = AppSettings.GetSettingsFilePath();
        _existed = File.Exists(_settingsPath);
        if (_existed)
        {
            _backupPath = _settingsPath + ".testbak-" + Guid.NewGuid().ToString("N")[..8];
            File.Copy(_settingsPath, _backupPath, overwrite: true);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_existed && _backupPath != null && File.Exists(_backupPath))
            {
                File.Copy(_backupPath, _settingsPath, overwrite: true);
                File.Delete(_backupPath);
            }
            else if (!_existed && File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
            }
            // Reset cached singleton so the next test sees the restored file
            AppSettings.Reload();
        }
        catch
        {
            // best effort
        }
    }
}
