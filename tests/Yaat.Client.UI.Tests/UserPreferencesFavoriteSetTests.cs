using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// Tests share preferences.json (per-process temp dir set by the test fixture's
// ModuleInitializer). Each test uses unique set names so concurrent runs do not
// collide, and deletes its own sets at the end to limit accumulation. Disk
// round-trip assertions read from a fresh UserPreferences; other assertions use
// the same instance to avoid the inter-instance Save() race.
public class UserPreferencesFavoriteSetTests
{
    [Fact]
    public void CreateFavoriteSet_WithFavorites_RoundTripsThroughDisk()
    {
        var writer = new UserPreferences();
        Assert.True(writer.CreateFavoriteSet("FST-Roundtrip"));
        writer.SetFavoriteSetFavorites(
            "FST-Roundtrip",
            [
                new FavoriteCommand
                {
                    Label = "RT",
                    CommandText = "FH 270",
                    AirportId = "OAK",
                    Category = FavoriteCommandCategory.Ground,
                },
            ]
        );
        writer.SetFavoriteSetLoaded("FST-Roundtrip", true);

        var reader = new UserPreferences();
        var reloaded = reader.FavoriteCommandSets.FirstOrDefault(s => s.Name == "FST-Roundtrip");

        Assert.NotNull(reloaded);
        var favorite = Assert.Single(reloaded.Favorites);
        Assert.Equal("RT", favorite.Label);
        Assert.Equal("FH 270", favorite.CommandText);
        Assert.Equal("OAK", favorite.AirportId);
        Assert.Equal(FavoriteCommandCategory.Ground, favorite.Category);
        Assert.Contains("FST-Roundtrip", reader.LoadedFavoriteSetNames);

        new UserPreferences().DeleteFavoriteSet("FST-Roundtrip");
    }

    [Fact]
    public void CreateFavoriteSet_Collision_ReturnsFalse()
    {
        var prefs = new UserPreferences();
        Assert.True(prefs.CreateFavoriteSet("FST-Collide"));

        Assert.False(prefs.CreateFavoriteSet("fst-collide"));
        Assert.False(prefs.CreateFavoriteSet("  "));

        prefs.DeleteFavoriteSet("FST-Collide");
    }

    [Fact]
    public void RenameFavoriteSet_UpdatesLoadedList()
    {
        var prefs = new UserPreferences();
        prefs.CreateFavoriteSet("FST-OldName");
        prefs.SetFavoriteSetLoaded("FST-OldName", true);

        Assert.True(prefs.RenameFavoriteSet("FST-OldName", "FST-NewName"));

        Assert.DoesNotContain("FST-OldName", prefs.LoadedFavoriteSetNames);
        Assert.Contains("FST-NewName", prefs.LoadedFavoriteSetNames);
        Assert.DoesNotContain(prefs.FavoriteCommandSets, s => s.Name == "FST-OldName");

        prefs.DeleteFavoriteSet("FST-NewName");
    }

    [Fact]
    public void RenameFavoriteSet_Collision_ReturnsFalse()
    {
        var prefs = new UserPreferences();
        prefs.CreateFavoriteSet("FST-RenameA");
        prefs.CreateFavoriteSet("FST-RenameB");

        Assert.False(prefs.RenameFavoriteSet("FST-RenameA", "fst-renameb"));
        Assert.Contains(prefs.FavoriteCommandSets, s => s.Name == "FST-RenameA");

        prefs.DeleteFavoriteSet("FST-RenameA");
        prefs.DeleteFavoriteSet("FST-RenameB");
    }

    [Fact]
    public void DeleteFavoriteSet_AlsoUnloadsIt()
    {
        var prefs = new UserPreferences();
        prefs.CreateFavoriteSet("FST-DeleteLoaded");
        prefs.SetFavoriteSetLoaded("FST-DeleteLoaded", true);

        Assert.True(prefs.DeleteFavoriteSet("FST-DeleteLoaded"));

        Assert.DoesNotContain("FST-DeleteLoaded", prefs.LoadedFavoriteSetNames);
        Assert.DoesNotContain(prefs.FavoriteCommandSets, s => s.Name == "FST-DeleteLoaded");
    }

    [Fact]
    public void SetFavoriteSetLoaded_TogglesAndPreservesLoadOrder()
    {
        var prefs = new UserPreferences();
        prefs.CreateFavoriteSet("FST-OrderB");
        prefs.CreateFavoriteSet("FST-OrderA");

        prefs.SetFavoriteSetLoaded("FST-OrderB", true);
        prefs.SetFavoriteSetLoaded("FST-OrderA", true);
        // Loading an already-loaded set must not duplicate or reorder it.
        prefs.SetFavoriteSetLoaded("FST-OrderB", true);

        var loaded = prefs.LoadedFavoriteSetNames.Where(n => n.StartsWith("FST-Order", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["FST-OrderB", "FST-OrderA"], loaded);

        prefs.SetFavoriteSetLoaded("FST-OrderB", false);
        Assert.DoesNotContain("FST-OrderB", prefs.LoadedFavoriteSetNames);

        prefs.DeleteFavoriteSet("FST-OrderA");
        prefs.DeleteFavoriteSet("FST-OrderB");
    }

    [Fact]
    public void FavoriteSetsChanged_RaisedOnEveryFavoritesMutator()
    {
        var prefs = new UserPreferences();
        var raised = 0;
        prefs.FavoriteSetsChanged += () => raised++;

        prefs.CreateFavoriteSet("FST-Events");
        prefs.SetFavoriteSetFavorites("FST-Events", [new FavoriteCommand { Label = "E", CommandText = "E" }]);
        prefs.SetFavoriteSetLoaded("FST-Events", true);
        prefs.SetFavoriteCommands(prefs.FavoriteCommands.ToList());
        prefs.DeleteFavoriteSet("FST-Events");

        Assert.Equal(5, raised);
    }
}
