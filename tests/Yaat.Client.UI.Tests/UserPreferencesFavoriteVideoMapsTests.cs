using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, redirected by ModuleInit to a per-process
// temp directory. A fresh UserPreferences instance reads preferences.json from disk and proves
// the round-trip. Unique scope keys keep tests independent of ordering.
public class UserPreferencesFavoriteVideoMapsTests
{
    [Fact]
    public void GetFavoriteVideoMaps_NoSavedValue_ReturnsEmpty()
    {
        var prefs = new UserPreferences();

        Assert.Empty(prefs.GetFavoriteVideoMaps(FavoriteMapScope.Artcc, "TEST-fav-unknown-ZZZ"));
        Assert.False(prefs.IsFavoriteVideoMap(FavoriteMapScope.Artcc, "TEST-fav-unknown-ZZZ", "map-1"));
    }

    [Fact]
    public void SetFavoriteVideoMap_PersistsAcrossInstances()
    {
        const string artcc = "TEST-fav-roundtrip-ZOA";
        var prefs = new UserPreferences();
        prefs.SetFavoriteVideoMap(FavoriteMapScope.Artcc, artcc, "map-alpha", true);

        Assert.True(prefs.IsFavoriteVideoMap(FavoriteMapScope.Artcc, artcc, "map-alpha"));

        var reader = new UserPreferences();
        Assert.True(reader.IsFavoriteVideoMap(FavoriteMapScope.Artcc, artcc, "map-alpha"));
        Assert.Equal(["map-alpha"], reader.GetFavoriteVideoMaps(FavoriteMapScope.Artcc, artcc));
    }

    [Fact]
    public void SetFavoriteVideoMap_ScopesAreIndependent()
    {
        const string key = "TEST-fav-scope-key-KEY";
        var prefs = new UserPreferences();
        prefs.SetFavoriteVideoMap(FavoriteMapScope.Artcc, key, "map-shared", true);

        // Same key string under different scopes must not bleed into one another.
        Assert.True(prefs.IsFavoriteVideoMap(FavoriteMapScope.Artcc, key, "map-shared"));
        Assert.False(prefs.IsFavoriteVideoMap(FavoriteMapScope.Airport, key, "map-shared"));
        Assert.False(prefs.IsFavoriteVideoMap(FavoriteMapScope.Scenario, key, "map-shared"));
    }

    [Fact]
    public void SetFavoriteVideoMap_RemovingLastFavorite_DropsEntry()
    {
        const string airport = "TEST-fav-cleanup-KSFO";
        var prefs = new UserPreferences();
        prefs.SetFavoriteVideoMap(FavoriteMapScope.Airport, airport, "map-only", true);
        prefs.SetFavoriteVideoMap(FavoriteMapScope.Airport, airport, "map-only", false);

        Assert.Empty(prefs.GetFavoriteVideoMaps(FavoriteMapScope.Airport, airport));

        var reader = new UserPreferences();
        Assert.Empty(reader.GetFavoriteVideoMaps(FavoriteMapScope.Airport, airport));
    }

    [Fact]
    public void SetFavoriteVideoMap_MultipleMapsPerKey_AreTracked()
    {
        const string scenario = "TEST-fav-multi-scenario-XYZ";
        var prefs = new UserPreferences();
        prefs.SetFavoriteVideoMap(FavoriteMapScope.Scenario, scenario, "map-1", true);
        prefs.SetFavoriteVideoMap(FavoriteMapScope.Scenario, scenario, "map-2", true);

        Assert.True(prefs.IsFavoriteVideoMap(FavoriteMapScope.Scenario, scenario, "map-1"));
        Assert.True(prefs.IsFavoriteVideoMap(FavoriteMapScope.Scenario, scenario, "map-2"));

        // Removing one leaves the other and keeps the entry alive.
        prefs.SetFavoriteVideoMap(FavoriteMapScope.Scenario, scenario, "map-1", false);
        Assert.Equal(["map-2"], prefs.GetFavoriteVideoMaps(FavoriteMapScope.Scenario, scenario));
    }

    [Fact]
    public void SetFavoriteVideoMap_FavoritingTwice_DoesNotDuplicate()
    {
        const string artcc = "TEST-fav-dupe-ZLA";
        var prefs = new UserPreferences();
        prefs.SetFavoriteVideoMap(FavoriteMapScope.Artcc, artcc, "map-dup", true);
        prefs.SetFavoriteVideoMap(FavoriteMapScope.Artcc, artcc, "map-dup", true);

        Assert.Equal(["map-dup"], prefs.GetFavoriteVideoMaps(FavoriteMapScope.Artcc, artcc));
    }
}
