using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Integration test for TaxiRouteCatalog wiring on NavigationDatabase. Confirms the
/// constructor's artccsBaseDir parameter loads routes from the supplied path and
/// exposes them via the TaxiRoutes property.
/// </summary>
public class NavigationDatabaseTaxiRoutesTests
{
    [Fact]
    public void NavigationDatabase_ForTesting_ExposesEmptyTaxiRoutes()
    {
        // The lightweight ForTesting() factory does not load taxi routes from disk;
        // it should still expose a non-null empty catalog so callers don't NRE.
        var db = NavigationDatabase.ForTesting();

        Assert.NotNull(db.TaxiRoutes);
        Assert.Empty(db.TaxiRoutes.GetRoutesForAirport("KOAK"));
    }

    [Fact]
    public void NavigationDatabase_FullConstructor_LoadsTaxiRoutesFromBaseDir()
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

        // Point at the test fixture directory so we don't depend on the production
        // bundled JSONs (which may not exist at the time this test is written).
        string artccsDir = Path.Combine(AppContext.BaseDirectory, "TestData", "ARTCCs");

        var db = new NavigationDatabase(navData, cifpPath, artccsBaseDir: artccsDir);

        var koakRoutes = db.TaxiRoutes.GetRoutesForAirport("KOAK");
        Assert.Equal(3, koakRoutes.Count);
        Assert.Contains(koakRoutes, r => r.Name == "DEP 30 via W");
    }
}
