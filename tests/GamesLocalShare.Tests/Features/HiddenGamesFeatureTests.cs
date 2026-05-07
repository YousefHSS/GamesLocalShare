using GamesLocalShare.Tests.Helpers;
using GamesLocalShare.Tests.Models;

namespace GamesLocalShare.Tests.Features;

/// <summary>
/// Hidden games are persisted in AppSettings and consulted by the network
/// announcement code to decide whether to advertise a game to peers.
/// </summary>
[Collection(AppSettingsCollection.Name)]
public class HiddenGamesFeatureTests
{
    [Fact]
    public void Hide_Then_Reload_PersistsAcrossSingleton()
    {
        using var iso = new AppSettingsIsolation();

        const string id = "__hide_persist_test";
        var s = AppSettings.Load();
        s.HideGame(id);
        s.IsGameHidden(id).Should().BeTrue();

        AppSettings.Reload();
        AppSettings.Load().IsGameHidden(id).Should().BeTrue();

        AppSettings.Load().UnhideGame(id);
        AppSettings.Reload();
        AppSettings.Load().IsGameHidden(id).Should().BeFalse();
    }

    [Fact]
    public void HiddenGameIds_AreCaseSensitive()
    {
        // Hidden game IDs are typically Steam AppIds (numeric) so case shouldn't matter,
        // but Epic Games AppIds are mixed-case GUIDs. The current contract uses string equality.
        var s = new AppSettings();
        s.HiddenGameIds.Add("AbC123");

        s.IsGameHidden("AbC123").Should().BeTrue();
        s.IsGameHidden("abc123").Should().BeFalse(); // case-sensitive
    }
}
