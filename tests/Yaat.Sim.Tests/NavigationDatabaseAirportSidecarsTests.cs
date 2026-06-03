using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Integration test for <see cref="AirportSidecarCatalog"/> wiring on <see cref="NavigationDatabase"/>.
/// Confirms the constructor's artccsBaseDir parameter loads the unified per-airport sidecars from the
/// supplied path and exposes them via the <c>AirportSidecars</c> property.
/// </summary>
public class NavigationDatabaseAirportSidecarsTests
{
    [Fact]
    public void NavigationDatabase_ForTesting_ExposesEmptySidecars()
    {
        // The lightweight ForTesting() factory does not load sidecars from disk; it should still expose
        // a non-null empty catalog so callers don't NRE.
        var db = NavigationDatabase.ForTesting();

        Assert.NotNull(db.AirportSidecars);
        Assert.Empty(db.AirportSidecars.GetTaxiRoutes("KOAK"));
        Assert.Empty(db.AirportSidecars.GetAvoidedTaxiways("KOAK"));
    }

    [Fact]
    public void NavigationDatabase_FullConstructor_LoadsSidecarsFromBaseDir()
    {
        var navDbPath = Path.Combine("TestData", "NavData.dat");
        if (!File.Exists(navDbPath))
        {
            return;
        }

        var cifpPath = TestVnasData.GetCifpPath();
        if (cifpPath is null)
        {
            return;
        }

        var bytes = File.ReadAllBytes(navDbPath);
        var navData = Yaat.Sim.Proto.NavDataSet.Parser.ParseFrom(bytes);

        // Point at the test fixture directory so we don't depend on the production bundled JSONs.
        string artccsDir = Path.Combine(AppContext.BaseDirectory, "TestData", "ARTCCs");

        var db = new NavigationDatabase(navData, cifpPath, artccsBaseDir: artccsDir);

        var koakRoutes = db.AirportSidecars.GetTaxiRoutes("KOAK");
        Assert.Equal(3, koakRoutes.Count);
        Assert.Contains(koakRoutes, r => r.Name == "DEP 30 via W");
    }
}
