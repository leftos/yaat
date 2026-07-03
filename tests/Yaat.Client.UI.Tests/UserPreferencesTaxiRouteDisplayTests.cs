using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, which ModuleInit redirects to a per-process temp
// directory. A fresh UserPreferences instance proves the disk round-trip.
public class UserPreferencesTaxiRouteDisplayTests
{
    [Fact]
    public void Defaults_HoverOn_ShowAllOff()
    {
        var prefs = new UserPreferences();

        Assert.True(prefs.GroundShowTaxiRouteOnHover);
        Assert.False(prefs.GroundShowAllTaxiRoutes);
    }

    [Fact]
    public void SetGroundTaxiRouteDisplay_PersistsBothFlags()
    {
        var prefs = new UserPreferences();
        try
        {
            // Invert both defaults so a stale-default read would fail.
            prefs.SetGroundTaxiRouteDisplay(onHover: false, showAll: true);

            Assert.False(prefs.GroundShowTaxiRouteOnHover);
            Assert.True(prefs.GroundShowAllTaxiRoutes);

            // Fresh instance reads preferences.json from disk, proving persistence.
            var reader = new UserPreferences();
            Assert.False(reader.GroundShowTaxiRouteOnHover);
            Assert.True(reader.GroundShowAllTaxiRoutes);
        }
        finally
        {
            // Tests share one per-process preferences.json; restore factory defaults so the
            // order-independent defaults test above never reads these inverted values.
            prefs.SetGroundTaxiRouteDisplay(onHover: true, showAll: false);
        }
    }
}
