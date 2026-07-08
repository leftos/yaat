using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// Covers the scroll/zoom sensitivity preference (1.0 = current speed, lower slows wheel/trackpad
// scroll). UserPreferences writes to YaatPaths.AppDataRoot, redirected by ModuleInit to a per-process
// temp dir; a fresh instance proves the disk round-trip. The mutating test restores the default so the
// Defaults test stays order-tolerant against the shared preferences.json.
public class UserPreferencesScrollSensitivityTests
{
    [Fact]
    public void Default_IsOne()
    {
        Assert.Equal(1.0, new UserPreferences().ScrollSensitivity);
    }

    [Fact]
    public void SetScrollSensitivity_PersistsAcrossInstances()
    {
        var prefs = new UserPreferences();

        prefs.SetScrollSensitivity(0.5);
        Assert.Equal(0.5, prefs.ScrollSensitivity);
        Assert.Equal(0.5, new UserPreferences().ScrollSensitivity);

        // Restore the default so the Defaults test is order-independent.
        prefs.SetScrollSensitivity(1.0);
        Assert.Equal(1.0, new UserPreferences().ScrollSensitivity);
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
