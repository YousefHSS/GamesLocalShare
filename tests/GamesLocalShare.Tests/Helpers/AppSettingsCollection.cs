namespace GamesLocalShare.Tests.Helpers;

/// <summary>
/// Tests that exercise the AppSettings singleton mutate a shared file
/// (%APPDATA%\GamesLocalShare\settings.json) and the static `_instance` cache,
/// so they cannot run in parallel with each other. Apply
/// `[Collection(AppSettingsCollection.Name)]` to opt in.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class AppSettingsCollection
{
    public const string Name = "AppSettings (singleton)";
}
