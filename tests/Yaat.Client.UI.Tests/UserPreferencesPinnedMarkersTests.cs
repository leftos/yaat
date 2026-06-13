using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// Scope-marker pins persist per radar view (keyed by scenario id) in preferences.json. A fresh
// UserPreferences instance reads from disk (redirected to a per-process temp dir by ModuleInit),
// proving the round-trip.
public class UserPreferencesPinnedMarkersTests
{
    [Fact]
    public void RadarSettings_NoSavedValue_PinnedMarkersDefaultEmpty()
    {
        var saved = new SavedRadarSettings();
        Assert.Empty(saved.PinnedMarkers);
    }

    [Fact]
    public void RadarSettings_PinnedMarkers_PersistAcrossInstances()
    {
        const string scenario = "TEST-markers-roundtrip-ZOA";
        var prefs = new UserPreferences();
        prefs.SetRadarSettings(scenario, new SavedRadarSettings { PinnedMarkers = ["SFO", "OAK270010"] });

        var reader = new UserPreferences();
        var saved = reader.GetRadarSettings(scenario);

        Assert.NotNull(saved);
        Assert.Equal(["SFO", "OAK270010"], saved!.PinnedMarkers);
    }
}
