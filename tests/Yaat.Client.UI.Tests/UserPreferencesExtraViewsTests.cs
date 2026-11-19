using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// The open extra Ground view windows persist as (ordinal, airport) records — the ordinal is ≥ 2
// (the docked view is #1) and keys the geometry under "GroundView#n", the airport is the base the window
// was opened on. UserPreferences writes to this test's own preferences file; a fresh instance proves the disk round-trip.
public class UserPreferencesExtraViewsTests
{
    [Fact]
    public void ExtraViews_DefaultEmpty()
    {
        var prefs = new UserPreferences();

        Assert.Empty(prefs.ExtraGroundViews);
    }

    [Fact]
    public void SetExtraGroundViews_PersistsToDisk()
    {
        var prefs = new UserPreferences();
        prefs.SetExtraGroundViews([new SavedExtraView(3, "KSJC")]);

        Assert.Equal([new SavedExtraView(3, "KSJC")], prefs.ExtraGroundViews);
        Assert.Equal([new SavedExtraView(3, "KSJC")], new UserPreferences().ExtraGroundViews);
    }
}
