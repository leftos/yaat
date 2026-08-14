using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests;

// Tests share the per-process preferences.json (temp dir set by the test fixture's
// ModuleInitializer). Each test restores the loaded-set list and removes anything it wrote,
// and FavoriteStore instances use throwaway roots.
public class UserPreferencesFavoriteSetTests : IDisposable
{
    private readonly string _storeRoot = Path.Combine(Path.GetTempPath(), "yaat-favmigration-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storeRoot))
        {
            Directory.Delete(_storeRoot, recursive: true);
        }
    }

    [Fact]
    public void SetFavoriteSetLoaded_AppendsInLoadOrder_DedupesAndRemoves()
    {
        var prefs = new UserPreferences();
        try
        {
            prefs.SetFavoriteSetLoaded("aaaa1111", true);
            prefs.SetFavoriteSetLoaded("bbbb2222", true);
            prefs.SetFavoriteSetLoaded("AAAA1111", true);
            Assert.Equal(["aaaa1111", "bbbb2222"], prefs.LoadedFavoriteSetIds);

            prefs.SetFavoriteSetLoaded("aaaa1111", false);
            Assert.Equal(["bbbb2222"], prefs.LoadedFavoriteSetIds);

            var reloaded = new UserPreferences();
            Assert.Equal(["bbbb2222"], reloaded.LoadedFavoriteSetIds);
        }
        finally
        {
            prefs.SetLoadedFavoriteSets([]);
        }
    }

    [Fact]
    public void LegacyMigration_PartitionsBasePoolByScope_AndConvertsNamedSets()
    {
        InjectLegacyFavorites(
            """
            {
              "favoriteCommands": [
                { "label": "GlobalFav", "commandText": "FH 270" },
                { "label": "OakFav", "commandText": "T A B", "airportId": "oak" },
                { "label": "ScnFav", "commandText": "CM 014", "scenarioId": "SCN-1" },
                { "label": "GlobalFav2", "commandText": "DM 010" }
              ],
              "favoriteCommandSets": [
                { "name": "Mig Set", "favorites": [ { "label": "SetFav", "commandText": "PD OAK", "airportId": "SFO" } ] }
              ],
              "loadedFavoriteSetNames": [ "Mig Set", "Ghost" ]
            }
            """
        );

        var prefs = new UserPreferences();
        var store = new FavoriteStore(_storeRoot);
        try
        {
            FavoriteLegacyMigration.Run(prefs, store);

            Assert.Equal(["GlobalFav", "GlobalFav2"], store.GetSetFavorites(store.GlobalSet.Id).Select(f => f.Label));
            Assert.Equal(["OakFav"], store.GetSetFavorites(store.FindAirportSet("OAK")!.Id).Select(f => f.Label));
            Assert.Equal(["ScnFav"], store.GetSetFavorites(store.FindScenarioSet("SCN-1")!.Id).Select(f => f.Label));

            var named = store.FindNamedSet("Mig Set");
            Assert.NotNull(named);
            // Set membership replaces the old scope fields entirely; the SFO airport scope on the
            // set favorite does not fabricate an SFO container.
            Assert.Equal(["SetFav"], store.GetSetFavorites(named.Id).Select(f => f.Label));
            Assert.Null(store.FindAirportSet("SFO"));

            Assert.Equal([named.Id], prefs.LoadedFavoriteSetIds);

            // The legacy fields are gone from disk, so a second migration pass has nothing to do.
            var reloadedPrefs = new UserPreferences();
            Assert.Null(reloadedPrefs.PeekLegacyFavorites());
        }
        finally
        {
            prefs.SetLoadedFavoriteSets([]);
        }
    }

    [Fact]
    public void LegacyMigration_MapsWindowProfileLoadedSetNamesToIds()
    {
        InjectLegacyFavorites(
            """
            {
              "favoriteCommands": [],
              "favoriteCommandSets": [ { "name": "Profile Set", "favorites": [] } ],
              "loadedFavoriteSetNames": []
            }
            """
        );

        var prefs = new UserPreferences();
        prefs.SaveWindowProfile(new SavedWindowProfile { Name = "FST-Profile", LoadedFavoriteSetNames = ["Profile Set", "Ghost"] });
        var store = new FavoriteStore(_storeRoot);
        try
        {
            FavoriteLegacyMigration.Run(prefs, store);

            var migrated = new UserPreferences().GetWindowProfile("FST-Profile");
            Assert.NotNull(migrated);
            Assert.Null(migrated.LoadedFavoriteSetNames);
            Assert.Equal([store.FindNamedSet("Profile Set")!.Id], migrated.LoadedFavoriteSetIds);
        }
        finally
        {
            prefs.DeleteWindowProfile("FST-Profile");
            prefs.SetLoadedFavoriteSets([]);
        }
    }

    [Fact]
    public void LegacyMigration_SkipsWhenStoreAlreadyExists()
    {
        var seeded = new FavoriteStore(_storeRoot);
        Assert.True(seeded.LoadedFromEmpty);

        InjectLegacyFavorites("""{ "favoriteCommands": [ { "label": "TooLate", "commandText": "X" } ] }""");

        var prefs = new UserPreferences();
        var store = new FavoriteStore(_storeRoot);
        try
        {
            Assert.False(store.LoadedFromEmpty);
            FavoriteLegacyMigration.Run(prefs, store);

            Assert.Empty(store.GetSetFavorites(store.GlobalSet.Id));
        }
        finally
        {
            ClearLegacyFavoritesFromDisk(prefs);
        }
    }

    /// <summary>Splices legacy favorites fields into the shared preferences.json on disk (merging with whatever is there).</summary>
    private static void InjectLegacyFavorites(string legacyJson)
    {
        var path = YaatPaths.Combine("preferences.json");
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        foreach (var (key, value) in JsonNode.Parse(legacyJson)!.AsObject().ToList())
        {
            root[key] = value?.DeepClone();
        }
        Directory.CreateDirectory(YaatPaths.AppDataRoot);
        File.WriteAllText(path, root.ToJsonString());
    }

    private static void ClearLegacyFavoritesFromDisk(UserPreferences prefs)
    {
        prefs.ClearLegacyFavorites();
    }
}
