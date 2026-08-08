using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, redirected by ModuleInit to a per-process
// temp directory. A fresh UserPreferences instance reads preferences.json from disk and proves
// the round-trip. Unique scenario keys keep tests independent of ordering.
public class UserPreferencesFavoriteMetarStationsTests
{
    [Fact]
    public void IsFavoriteMetarStation_NoSavedValue_ReturnsFalse()
    {
        var prefs = new UserPreferences();

        Assert.False(prefs.IsFavoriteMetarStation("TEST-metar-unknown-ZZZ", "OAK"));
    }

    [Fact]
    public void SetFavoriteMetarStation_PersistsAcrossInstances()
    {
        const string scenario = "TEST-metar-roundtrip-scenario";
        var prefs = new UserPreferences();
        prefs.SetFavoriteMetarStation(scenario, "SFO", true);

        Assert.True(prefs.IsFavoriteMetarStation(scenario, "SFO"));

        var reader = new UserPreferences();
        Assert.True(reader.IsFavoriteMetarStation(scenario, "SFO"));
        Assert.False(reader.IsFavoriteMetarStation(scenario, "OAK"));
    }

    [Fact]
    public void SetFavoriteMetarStation_ScenariosAreIndependent()
    {
        const string scenarioA = "TEST-metar-indep-A";
        const string scenarioB = "TEST-metar-indep-B";
        var prefs = new UserPreferences();
        prefs.SetFavoriteMetarStation(scenarioA, "OAK", true);

        Assert.True(prefs.IsFavoriteMetarStation(scenarioA, "OAK"));
        Assert.False(prefs.IsFavoriteMetarStation(scenarioB, "OAK"));
    }

    [Fact]
    public void SetFavoriteMetarStation_RemovingLastFavorite_DropsEntry()
    {
        const string scenario = "TEST-metar-cleanup-scenario";
        var prefs = new UserPreferences();
        prefs.SetFavoriteMetarStation(scenario, "HAF", true);
        prefs.SetFavoriteMetarStation(scenario, "HAF", false);

        Assert.False(prefs.IsFavoriteMetarStation(scenario, "HAF"));

        var reader = new UserPreferences();
        Assert.False(reader.IsFavoriteMetarStation(scenario, "HAF"));
    }

    [Fact]
    public void SetFavoriteMetarStation_FavoritingTwice_ThenRemovingOnce_ClearsIt()
    {
        const string scenario = "TEST-metar-dupe-scenario";
        var prefs = new UserPreferences();
        prefs.SetFavoriteMetarStation(scenario, "NUQ", true);
        prefs.SetFavoriteMetarStation(scenario, "NUQ", true);
        prefs.SetFavoriteMetarStation(scenario, "NUQ", false);

        Assert.False(prefs.IsFavoriteMetarStation(scenario, "NUQ"));
    }
}
