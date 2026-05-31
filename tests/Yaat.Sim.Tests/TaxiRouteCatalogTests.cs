using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for TaxiRouteCatalog — airport-keyed lookup of TaxiRouteDefinition objects.
/// The catalog itself is layout-agnostic (it stores raw definitions); applicability
/// against a specific aircraft node is the menu-builder's responsibility, validated
/// here against a real OAK ground graph from TestData.
/// </summary>
public class TaxiRouteCatalogTests
{
    public TaxiRouteCatalogTests()
    {
        // TaxiPathfinder's variant resolver (auto-extending W → W1/W2 to reach a runway
        // hold-short) consults NavigationDatabase.Instance. Tests that exercise the
        // full preset-resolution path therefore need the real NavData/CIFP loaded.
        TestVnasData.EnsureInitialized();
    }

    private static List<TaxiRouteDefinition> SampleRoutes() =>
        [
            new()
            {
                AirportId = "KOAK",
                Name = "DEP 30 via W",
                Path = "W",
                DestinationRunway = "30",
            },
            new()
            {
                AirportId = "KOAK",
                Name = "DEP 28L via K-W",
                Path = "K W",
                DestinationRunway = "28L",
            },
            new()
            {
                AirportId = "KSFO",
                Name = "DEP 1L via B-M1",
                Path = "B M1",
                DestinationRunway = "1L",
            },
        ];

    [Fact]
    public void GetRoutesForAirport_ReturnsRoutesForGivenIcao()
    {
        var catalog = new TaxiRouteCatalog(SampleRoutes());

        var oakRoutes = catalog.GetRoutesForAirport("KOAK");

        Assert.Equal(2, oakRoutes.Count);
        Assert.Contains(oakRoutes, r => r.Name == "DEP 30 via W");
        Assert.Contains(oakRoutes, r => r.Name == "DEP 28L via K-W");
    }

    [Fact]
    public void GetRoutesForAirport_IsCaseInsensitive()
    {
        var catalog = new TaxiRouteCatalog(SampleRoutes());

        Assert.Equal(2, catalog.GetRoutesForAirport("koak").Count);
        Assert.Equal(2, catalog.GetRoutesForAirport("KOAK").Count);
        Assert.Equal(2, catalog.GetRoutesForAirport("KoAk").Count);
    }

    [Fact]
    public void GetRoutesForAirport_AcceptsBothIcaoAndFaa()
    {
        // Routes are stored with ICAO airportIds in the JSON ("KOAK"), but the airport
        // ground layout exposes the FAA short form ("OAK"). The catalog must match either.
        var catalog = new TaxiRouteCatalog(SampleRoutes());

        Assert.Equal(2, catalog.GetRoutesForAirport("KOAK").Count);
        Assert.Equal(2, catalog.GetRoutesForAirport("OAK").Count);
        Assert.Single(catalog.GetRoutesForAirport("KSFO"));
        Assert.Single(catalog.GetRoutesForAirport("SFO"));
    }

    [Fact]
    public void GetRoutesForAirport_UnknownAirport_ReturnsEmpty()
    {
        var catalog = new TaxiRouteCatalog(SampleRoutes());

        Assert.Empty(catalog.GetRoutesForAirport("KXXX"));
        Assert.Empty(catalog.GetRoutesForAirport(""));
    }

    [Fact]
    public void Empty_ReturnsEmptyForAnyAirport()
    {
        var catalog = TaxiRouteCatalog.Empty;

        Assert.Empty(catalog.GetRoutesForAirport("KOAK"));
        Assert.Empty(catalog.GetRoutesForAirport("KSFO"));
    }

    [Fact]
    public void OakDepartureRoute_ResolvesAgainstRealLayout()
    {
        // Validate the catalog's KOAK W→30 route resolves through TaxiPathfinder against the
        // real OAK ground layout. This is the same call the UI menu builder will make to
        // filter applicable routes per aircraft position.
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return; // TestData GeoJSON not present
        }

        // Pick any node sitting on taxiway W as the start — simulating an aircraft ready
        // to taxi from a W-adjacent point. (Real menu code uses the aircraft's actual
        // current node; for the test, any W node validates the schema → pathfinder path.)
        var startNode = layout.Nodes.Values.FirstOrDefault(n => n.Edges.Any(e => e.MatchesTaxiway("W")));
        Assert.NotNull(startNode);

        var catalog = new TaxiRouteCatalog(SampleRoutes());
        var route = catalog.GetRoutesForAirport("KOAK").First(r => r.Name == "DEP 30 via W");

        var resolved = TaxiPathfinderV2.ResolveExplicitPath(
            layout,
            startNode!.Id,
            route.GetPathTokens(),
            out string? failReason,
            new ExplicitPathOptions { DestinationRunway = route.DestinationRunway, AirportId = "OAK" },
            AircraftCategory.Jet
        );

        Assert.NotNull(resolved);
        Assert.Null(failReason);
        Assert.NotEmpty(resolved!.Segments);
    }

    [Fact]
    public void BogusRoute_FailsToResolveAgainstRealLayout()
    {
        // Routes referencing non-existent taxiways (path: "ZZZ") must not resolve. The
        // menu builder relies on this to drop bogus catalog entries silently rather than
        // surfacing them as broken menu items.
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var startNode = layout.Nodes.Values.FirstOrDefault(n => n.Edges.Any(e => e.MatchesTaxiway("W")));
        Assert.NotNull(startNode);

        var bogus = new TaxiRouteDefinition
        {
            AirportId = "KOAK",
            Name = "Nonsense",
            Path = "ZZZ",
        };

        var resolved = TaxiPathfinderV2.ResolveExplicitPath(
            layout,
            startNode!.Id,
            bogus.GetPathTokens(),
            out string? failReason,
            new ExplicitPathOptions { AirportId = "OAK" },
            AircraftCategory.Jet
        );

        Assert.True(resolved is null || failReason is not null, "Bogus taxiway must not resolve to a valid route");
    }
}
