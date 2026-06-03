using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for contextual implicit connectors — a named connector taxiway (e.g. SFO's "LF") is authorized
/// only when the controller's cleared sequence places its two <c>between</c> taxiways adjacent.
/// </summary>
public class ImplicitConnectorTests
{
    public ImplicitConnectorTests() => TestVnasData.EnsureInitialized();

    private static List<ImplicitConnectorEntry> LfConnector() => [new ImplicitConnectorEntry { Connector = "LF", Between = ["L", "F"] }];

    [Fact]
    public void BuildAuthorizedTaxiwaySet_AuthorizesConnector_WhenBetweenPairAdjacent()
    {
        var set = SearchContext.BuildAuthorizedTaxiwaySet(["L", "F"], LfConnector());

        Assert.NotNull(set);
        Assert.Contains("LF", set!);
        Assert.Contains("L", set);
        Assert.Contains("F", set);
    }

    [Fact]
    public void BuildAuthorizedTaxiwaySet_Unordered_AuthorizesConnector()
    {
        var set = SearchContext.BuildAuthorizedTaxiwaySet(["F", "L"], LfConnector());

        Assert.NotNull(set);
        Assert.Contains("LF", set!);
    }

    [Fact]
    public void BuildAuthorizedTaxiwaySet_DoesNotAuthorize_WhenPairNotAdjacent()
    {
        // L and F are both present but separated by A — the connector must NOT be authorized.
        var set = SearchContext.BuildAuthorizedTaxiwaySet(["L", "A", "F"], LfConnector());

        Assert.NotNull(set);
        Assert.DoesNotContain("LF", set!);
    }

    [Fact]
    public void BuildAuthorizedTaxiwaySet_NoConnectors_OnlyLetterOnlyNames()
    {
        var set = SearchContext.BuildAuthorizedTaxiwaySet(["L", "F"], []);

        Assert.NotNull(set);
        Assert.Contains("L", set!);
        Assert.Contains("F", set);
        Assert.DoesNotContain("LF", set);
    }

    [Fact]
    public void BuildAuthorizedTaxiwaySet_EmptySequence_ReturnsNull()
    {
        Assert.Null(SearchContext.BuildAuthorizedTaxiwaySet([], LfConnector()));
    }

    [Fact]
    public void Loader_ParsesAndValidatesConnectors()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-conn-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(
                Path.Combine(categoryDir, "sfo.json"),
                """
                {
                  "airportId": "KSFO",
                  "implicitConnectors": [
                    { "connector": "lf", "between": ["l", "f"] },
                    { "connector": "BAD", "between": ["A"] },
                    { "connector": "", "between": ["A", "B"] }
                  ]
                }
                """
            );

            var result = AirportSidecarLoader.LoadAll(tempDir);

            var airport = Assert.Single(result.Airports);
            var conn = Assert.Single(airport.ImplicitConnectors);
            // Names are upper-cased at load.
            Assert.Equal("LF", conn.Connector);
            Assert.Equal(["L", "F"], conn.Between);
            // The 1-element 'between' and the blank connector are both skipped with warnings.
            Assert.Equal(2, result.Warnings.Count);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Catalog_GetImplicitConnectors_ByIcaoAndFaa()
    {
        var catalog = new AirportSidecarCatalog([new AirportSidecar("KSFO") { ImplicitConnectors = LfConnector() }]);

        Assert.Single(catalog.GetImplicitConnectors("KSFO"));
        Assert.Single(catalog.GetImplicitConnectors("SFO"));
        Assert.Empty(catalog.GetImplicitConnectors("KOAK"));
    }

    [Fact]
    public void Sfo_ProductionData_AuthorizesLfBetweenLandF()
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        if (!File.Exists(path) || TestVnasData.NavigationDb is null)
        {
            return;
        }

        // The SFO LF connector must ship in production data — fail loud if it's missing or wrong.
        var connectors = NavigationDatabase.Instance.AirportSidecars.GetImplicitConnectors("SFO");
        Assert.Contains(connectors, c => c.Connector == "LF" && c.Between.Count == 2 && c.Between.Contains("L") && c.Between.Contains("F"));

        var layout = GeoJsonParser.Parse("SFO", File.ReadAllText(path), null);
        var startNode = layout.Nodes.Values.FirstOrDefault(n => n.Edges.Any(e => e.MatchesTaxiway("L")));
        Assert.NotNull(startNode);

        var ctx = SearchContext.Compile(
            layout,
            startNode!.Id,
            ["L", "F"],
            null,
            null,
            null,
            null,
            null,
            AircraftCategory.Jet,
            null,
            null,
            null,
            null
        );

        Assert.NotNull(ctx.AuthorizedTaxiways);
        Assert.Contains("LF", ctx.AuthorizedTaxiways!);

        // A non-adjacent clearance does not authorize LF.
        var ctx2 = SearchContext.Compile(
            layout,
            startNode.Id,
            ["L", "A", "F"],
            null,
            null,
            null,
            null,
            null,
            AircraftCategory.Jet,
            null,
            null,
            null,
            null
        );
        Assert.NotNull(ctx2.AuthorizedTaxiways);
        Assert.DoesNotContain("LF", ctx2.AuthorizedTaxiways!);
    }
}
