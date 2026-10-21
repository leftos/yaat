using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// The open extra Radar/Ground view windows persist as (ordinal, airport) records — the ordinal is ≥ 2
// (the docked view is #1) and keys the geometry under "RadarView#n", the airport is the base the window
// was opened on. UserPreferences writes to this test's own preferences file; a fresh instance proves the disk round-trip.
public class UserPreferencesExtraViewsTests
{
    [Fact]
    public void ExtraViews_DefaultEmpty()
    {
        var prefs = new UserPreferences();

        Assert.Empty(prefs.ExtraRadarViews);
        Assert.Empty(prefs.ExtraGroundViews);
    }

    [Fact]
    public void SetExtraRadarViews_PersistsToDisk()
    {
        var prefs = new UserPreferences();
        prefs.SetExtraRadarViews([new SavedExtraView(2, "KOAK"), new SavedExtraView(4, "KSFO")]);

        Assert.Equal([new SavedExtraView(2, "KOAK"), new SavedExtraView(4, "KSFO")], prefs.ExtraRadarViews);
        // The airport rides along with the ordinal: a restored window reopens on the airport it had.
        Assert.Equal([new SavedExtraView(2, "KOAK"), new SavedExtraView(4, "KSFO")], new UserPreferences().ExtraRadarViews);
    }

    [Fact]
    public void SetExtraGroundViews_PersistsToDisk()
    {
        var prefs = new UserPreferences();
        prefs.SetExtraGroundViews([new SavedExtraView(3, "KSJC")]);

        Assert.Equal([new SavedExtraView(3, "KSJC")], prefs.ExtraGroundViews);
        Assert.Equal([new SavedExtraView(3, "KSJC")], new UserPreferences().ExtraGroundViews);
    }

    [Fact]
    public void SetExtraRadarViews_StoresACopy_NotTheCallersList()
    {
        var prefs = new UserPreferences();
        var views = new List<SavedExtraView> { new(2, "KOAK") };
        prefs.SetExtraRadarViews(views);
        views.Add(new SavedExtraView(3, "KSFO"));

        // A later mutation of the caller's list must not sneak an unsaved window into preferences.
        Assert.Equal([new SavedExtraView(2, "KOAK")], prefs.ExtraRadarViews);
    }
}
