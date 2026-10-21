using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// Covers the scroll/zoom sensitivity preference (1.0 = current speed, lower slows wheel/trackpad
// scroll). UserPreferences writes to this test's own preferences file; a fresh instance proves the disk round-trip.
public class UserPreferencesScrollSensitivityTests
{
    [Fact]
    public void Default_IsOne() => Assert.Equal(1.0, new UserPreferences().ScrollSensitivity);

    [Fact]
    public void SetScrollSensitivity_PersistsAcrossInstances()
    {
        var prefs = new UserPreferences();

        prefs.SetScrollSensitivity(0.5);
        Assert.Equal(0.5, prefs.ScrollSensitivity);
        Assert.Equal(0.5, new UserPreferences().ScrollSensitivity);
    }

    [Fact]
    public void SetScrollSensitivity_ClampsBelowMinimum()
    {
        var prefs = new UserPreferences();

        prefs.SetScrollSensitivity(0.0);
        Assert.Equal(UserPreferences.ScrollSensitivityMin, prefs.ScrollSensitivity);

        prefs.SetScrollSensitivity(1.0);
    }

    [Fact]
    public void SetScrollSensitivity_ClampsAboveMaximum()
    {
        var prefs = new UserPreferences();

        prefs.SetScrollSensitivity(0.5);
        prefs.SetScrollSensitivity(5.0);
        Assert.Equal(UserPreferences.ScrollSensitivityMax, prefs.ScrollSensitivity);

        prefs.SetScrollSensitivity(1.0);
    }
}
