using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// The <c>exitDirections</c> section of the per-airport sidecar overrides the default exit
/// (turn-off) side per landing runway end — needed where the GeoJSON <c>turnoff</c> flip for the
/// reciprocal end is wrong (issue #405, KMIA 26R). Loader validation is warn-don't-throw, matching
/// every other section.
/// </summary>
public class ExitDirectionSidecarTests
{
    private static AirportSidecarLoadResult LoadSidecar(string json)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "exitdir-sidecar-" + Guid.NewGuid());
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
    public void LoadAll_ReadsExitDirectionsSection()
    {
        var result = LoadSidecar(
            """
            {
              "airportId": "KMIA",
              "exitDirections": [
                { "runway": "26R", "side": "left", "notes": "facility request" }
              ]
            }
            """
        );

        Assert.Empty(result.Warnings);
        var airport = Assert.Single(result.Airports);
        var entry = Assert.Single(airport.ExitDirections);
        Assert.Equal("26R", entry.Runway);
        Assert.Equal(ExitSide.Left, entry.Side);
        Assert.Equal("facility request", entry.Notes);
    }

    [Fact]
    public void LoadAll_NormalizesDesignatorAndSideCase()
    {
        var result = LoadSidecar(
            """
            {
              "airportId": "KTST",
              "exitDirections": [ { "runway": " 8l ", "side": "RIGHT" } ]
            }
            """
        );

        Assert.Empty(result.Warnings);
        var entry = Assert.Single(Assert.Single(result.Airports).ExitDirections);
        Assert.Equal("08L", entry.Runway);
        Assert.Equal(ExitSide.Right, entry.Side);
    }

    [Theory]
    [InlineData("""{ "side": "left" }""", "missing runway")]
    [InlineData("""{ "runway": "26R", "side": "up" }""", "must be 'left' or 'right'")]
    [InlineData("""{ "runway": "26R" }""", "must be 'left' or 'right'")]
    public void LoadAll_InvalidEntry_WarnsAndSkips(string entry, string expectedWarningFragment)
    {
        var result = LoadSidecar(
            $$"""
            { "airportId": "KTST", "exitDirections": [ {{entry}} ] }
            """
        );

        Assert.Empty(Assert.Single(result.Airports).ExitDirections);
        Assert.Contains(result.Warnings, w => w.Contains(expectedWarningFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadAll_DuplicateRunwayInOneFile_WarnsAndLastWins()
    {
        var result = LoadSidecar(
            """
            {
              "airportId": "KTST",
              "exitDirections": [
                { "runway": "26R", "side": "left" },
                { "runway": "26r", "side": "right" }
              ]
            }
            """
        );

        Assert.Contains(result.Warnings, w => w.Contains("duplicate runway", StringComparison.OrdinalIgnoreCase));
        var entry = Assert.Single(Assert.Single(result.Airports).ExitDirections);
        Assert.Equal(ExitSide.Right, entry.Side);
    }

    [Fact]
    public void Catalog_GetExitDirection_NormalizesAirportAndDesignatorForms()
    {
        var catalog = new AirportSidecarCatalog([
            new AirportSidecar("KMIA") { ExitDirections = [new ExitDirectionOverride("26R", ExitSide.Left, null)] },
        ]);

        Assert.Equal(ExitSide.Left, catalog.GetExitDirection("KMIA", "26R"));
        Assert.Equal(ExitSide.Left, catalog.GetExitDirection("MIA", "26R"));
        Assert.Null(catalog.GetExitDirection("KMIA", "8L"));
        Assert.Null(catalog.GetExitDirection("KOAK", "26R"));
        Assert.Null(catalog.GetExitDirection("", "26R"));
        Assert.Null(catalog.GetExitDirection("KMIA", ""));
    }

    [Fact]
    public void Catalog_GetExitDirection_NormalizesSingleDigitDesignators()
    {
        var catalog = new AirportSidecarCatalog([
            new AirportSidecar("KTST") { ExitDirections = [new ExitDirectionOverride("09", ExitSide.Left, null)] },
        ]);

        Assert.Equal(ExitSide.Left, catalog.GetExitDirection("KTST", "9"));
        Assert.Equal(ExitSide.Left, catalog.GetExitDirection("KTST", "09"));
    }

    [Fact]
    public void Catalog_GetExitDirection_CrossFileMerge_LastWins()
    {
        var catalog = new AirportSidecarCatalog([
            new AirportSidecar("KMIA") { ExitDirections = [new ExitDirectionOverride("26R", ExitSide.Right, null)] },
            new AirportSidecar("MIA") { ExitDirections = [new ExitDirectionOverride("26R", ExitSide.Left, null)] },
        ]);

        Assert.Equal(ExitSide.Left, catalog.GetExitDirection("KMIA", "26R"));
    }
}
