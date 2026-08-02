using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// The <c>adw</c> section of the per-airport sidecar carries facility-published Arrival/Departure
/// Windows verbatim (arrival runway, departure runway, outer/inner range in nm from the landing
/// threshold). Loader validation is warn-don't-throw, matching every other section.
/// </summary>
public class AdwSidecarTests
{
    private static AirportSidecarLoadResult LoadSidecar(string json)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "adw-sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(Path.Combine(categoryDir, "test.json"), json);
            return AirportSidecarLoader.LoadAll(tempDir);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_ReadsAdwSection()
    {
        var result = LoadSidecar(
            """
            {
              "airportId": "KMIA",
              "adw": [
                { "arrivalRunway": "26R", "departureRunway": "30", "outerNm": 2.7, "innerNm": -0.1, "notes": "SOP 3-9.F" }
              ]
            }
            """
        );

        Assert.Empty(result.Warnings);
        var airport = Assert.Single(result.Airports);
        var window = Assert.Single(airport.Adw);
        Assert.Equal("26R", window.ArrivalRunway);
        Assert.Equal("30", window.DepartureRunway);
        Assert.Equal(2.7, window.OuterNm);
        Assert.Equal(-0.1, window.InnerNm);
        Assert.Equal("SOP 3-9.F", window.Notes);
    }

    [Fact]
    public void LoadAll_NormalizesSingleDigitDesignators()
    {
        var result = LoadSidecar(
            """
            {
              "airportId": "KTST",
              "adw": [ { "arrivalRunway": "9", "departureRunway": " 4l ", "outerNm": 2.0, "innerNm": 0 } ]
            }
            """
        );

        var window = Assert.Single(Assert.Single(result.Airports).Adw);
        Assert.Equal("09", window.ArrivalRunway);
        Assert.Equal("04L", window.DepartureRunway);
    }

    [Theory]
    [InlineData("""{ "departureRunway": "30", "outerNm": 2.7, "innerNm": -0.1 }""", "requires both")]
    [InlineData("""{ "arrivalRunway": "26R", "outerNm": 2.7, "innerNm": -0.1 }""", "requires both")]
    [InlineData("""{ "arrivalRunway": "26R", "departureRunway": "30", "outerNm": -0.1, "innerNm": 2.7 }""", "must exceed")]
    [InlineData("""{ "arrivalRunway": "26R", "departureRunway": "30", "outerNm": 2.7, "innerNm": 2.7 }""", "must exceed")]
    [InlineData("""{ "arrivalRunway": "26R", "departureRunway": "30", "outerNm": 40, "innerNm": -0.1 }""", "within")]
    [InlineData("""{ "arrivalRunway": "26R", "departureRunway": "30", "outerNm": 2.7, "innerNm": -40 }""", "within")]
    [InlineData("""{ "arrivalRunway": "26R", "departureRunway": "26r", "outerNm": 2.7, "innerNm": -0.1 }""", "must differ")]
    [InlineData("""{ "arrivalRunway": "26R", "departureRunway": "30", "outerNm": -0.1, "innerNm": -0.5 }""", "must be positive")]
    public void LoadAll_InvalidEntry_WarnsAndSkips(string entry, string expectedWarningFragment)
    {
        var result = LoadSidecar(
            $$"""
            { "airportId": "KTST", "adw": [ {{entry}} ] }
            """
        );

        Assert.Empty(Assert.Single(result.Airports).Adw);
        Assert.Contains(result.Warnings, w => w.Contains(expectedWarningFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Catalog_GetAdwWindows_MergesFilesAndAcceptsEitherAirportForm()
    {
        var catalog = new AirportSidecarCatalog([
            new AirportSidecar("KMIA") { Adw = [new AdwWindow("26R", "30", 2.7, -0.1, null)] },
            new AirportSidecar("MIA") { Adw = [new AdwWindow("30", "26L", 2.7, 0.1, null)] },
        ]);

        Assert.Equal(2, catalog.GetAdwWindows("KMIA").Count);
        Assert.Equal(2, catalog.GetAdwWindows("MIA").Count);
        Assert.Empty(catalog.GetAdwWindows("KOAK"));
        Assert.Empty(catalog.GetAdwWindows(""));
    }

    [Fact]
    public void ShippedMiamiSidecar_CarriesThePublishedWindows()
    {
        string baseDir = Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs");
        var result = AirportSidecarLoader.LoadAll(baseDir);

        var mia = Assert.Single(result.Airports, a => a.AirportId == "KMIA");
        Assert.Equal(4, mia.Adw.Count);
        Assert.All(mia.Adw, w => Assert.False(string.IsNullOrWhiteSpace(w.Notes), "every ADW entry must cite its facility directive"));

        var arr30Dep26R = Assert.Single(mia.Adw, w => (w.ArrivalRunway == "30") && (w.DepartureRunway == "26R"));
        Assert.Equal(2.9, arr30Dep26R.OuterNm);
        Assert.Equal(-0.3, arr30Dep26R.InnerNm);

        // 3-9.F publishes one row for arrivals 26L/26R; it expands to one window per arrival runway.
        Assert.Equal(2, mia.Adw.Count(w => w.DepartureRunway == "30"));
    }
}
