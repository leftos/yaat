using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

/// <summary>
/// The Ground View ADW toggle persists alongside the other label/filter toggles. Defaults on: airports
/// without an authored <c>adw</c> sidecar section ship no marks, so it draws nothing there anyway.
/// UserPreferences writes to YaatPaths.AppDataRoot, redirected by ModuleInit to a per-process temp
/// directory; a fresh instance reads preferences.json back and proves the round-trip.
/// </summary>
public class UserPreferencesAdwMarkingsTests
{
    private static void SetFilters(UserPreferences prefs, bool adwMarkings) =>
        prefs.SetGroundLabelFilters(
            runways: true,
            taxiways: true,
            holdShort: GroundFilterMode.LabelsAndIcons,
            parking: GroundFilterMode.LabelsAndIcons,
            spot: GroundFilterMode.LabelsAndIcons,
            adwMarkings: adwMarkings
        );

    [Fact]
    public void GroundShowAdwMarkings_DefaultsOn()
    {
        Assert.True(new UserPreferences().GroundShowAdwMarkings);
    }

    [Fact]
    public void SetGroundLabelFilters_PersistsAdwMarkings()
    {
        var prefs = new UserPreferences();
        try
        {
            SetFilters(prefs, adwMarkings: false);

            Assert.False(prefs.GroundShowAdwMarkings);
            Assert.False(new UserPreferences().GroundShowAdwMarkings);
        }
        finally
        {
            // Restore the default so the Defaults test stays order-tolerant.
            SetFilters(prefs, adwMarkings: true);
        }
    }

    [Fact]
    public void SavedGroundSettings_CloneCarriesAdwMarkings()
    {
        var original = new SavedGroundSettings { ShowAdwMarkings = false };

        var clone = original.Clone();
        clone.ShowAdwMarkings = true;

        Assert.False(original.ShowAdwMarkings);
    }
}
