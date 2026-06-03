using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for <see cref="AirportSidecarCatalog"/> — airport-keyed lookup over the unified per-airport
/// sidecars, accepting both ICAO ("KOAK") and FAA ("OAK") forms. Replaces the former separate
/// AvoidTaxiwayCatalog and TaxiRouteCatalog tests.
/// </summary>
public class AirportSidecarCatalogTests
{
    public AirportSidecarCatalogTests()
    {
        // The real-layout route-resolution tests below consult NavigationDatabase.Instance via
        // TaxiPathfinder's variant resolver, so the real NavData/CIFP must be loaded.
        TestVnasData.EnsureInitialized();
    }

    private static AirportSidecarCatalog SampleAvoid() =>
        new([
            new AirportSidecar("KOAK") { AvoidTaxiways = [new AvoidTaxiwayEntry { Name = "S" }] },
            new AirportSidecar("OAK") { AvoidTaxiways = [new AvoidTaxiwayEntry { Name = "Q3" }] },
        ]);

    private static AirportSidecarCatalog SampleRoutes() =>
        new([
            new AirportSidecar("KOAK")
            {
                TaxiRoutes =
                [
                    new TaxiRouteDefinition
                    {
                        AirportId = "KOAK",
                        Name = "DEP 30 via W",
                        Path = "W",
                        DestinationRunway = "30",
                    },
                    new TaxiRouteDefinition
                    {
                        AirportId = "KOAK",
                        Name = "DEP 28L via K-W",
                        Path = "K W",
                        DestinationRunway = "28L",
                    },
                ],
            },
            new AirportSidecar("KSFO")
            {
                TaxiRoutes =
                [
                    new TaxiRouteDefinition
                    {
                        AirportId = "KSFO",
                        Name = "DEP 1L via B-M1",
                        Path = "B M1",
                        DestinationRunway = "1L",
                    },
                ],
            },
        ]);

    [Fact]
    public void GetAvoidedTaxiways_AcceptsBothIcaoAndFaa()
    {
        var catalog = new AirportSidecarCatalog([new AirportSidecar("KOAK") { AvoidTaxiways = [new AvoidTaxiwayEntry { Name = "S" }] }]);

        Assert.Contains("S", catalog.GetAvoidedTaxiways("KOAK"));
        Assert.Contains("S", catalog.GetAvoidedTaxiways("OAK"));
        Assert.Contains("s", catalog.GetAvoidedTaxiways("oak")); // case-insensitive membership
    }

    [Fact]
    public void GetAvoidedTaxiways_CombinesEntriesForSameAirportAcrossFiles()
    {
        var avoided = SampleAvoid().GetAvoidedTaxiways("KOAK");

        Assert.Contains("S", avoided);
        Assert.Contains("Q3", avoided);
        Assert.Equal(2, avoided.Count);
    }

    [Fact]
    public void GetAvoidedTaxiways_UnknownAirport_ReturnsEmptyNeverNull()
    {
        var avoided = SampleAvoid().GetAvoidedTaxiways("KSFO");
        Assert.NotNull(avoided);
        Assert.Empty(avoided);
    }

    [Fact]
    public void GetTaxiRoutes_AcceptsBothIcaoAndFaa()
    {
        var catalog = SampleRoutes();

        Assert.Equal(2, catalog.GetTaxiRoutes("KOAK").Count);
        Assert.Equal(2, catalog.GetTaxiRoutes("OAK").Count);
        Assert.Equal(2, catalog.GetTaxiRoutes("koak").Count);
        Assert.Single(catalog.GetTaxiRoutes("KSFO"));
        Assert.Single(catalog.GetTaxiRoutes("SFO"));
    }

    [Fact]
    public void GetTaxiRoutes_UnknownAirport_ReturnsEmpty()
    {
        var catalog = SampleRoutes();

        Assert.Empty(catalog.GetTaxiRoutes("KXXX"));
        Assert.Empty(catalog.GetTaxiRoutes(""));
    }

    [Fact]
    public void Empty_ReturnsEmptyForAnyAirport()
    {
        Assert.Empty(AirportSidecarCatalog.Empty.GetAvoidedTaxiways("KOAK"));
        Assert.Empty(AirportSidecarCatalog.Empty.GetAvoidedTaxiways(""));
        Assert.Empty(AirportSidecarCatalog.Empty.GetTaxiRoutes("KOAK"));
        Assert.Empty(AirportSidecarCatalog.Empty.GetOneWayConstraints("KOAK"));
    }

    [Fact]
    public void GetOneWayConstraints_ByIcaoAndFaa()
    {
        var constraint = new OneWayConstraint(
            [new OneWayPoint(37.61, -122.39, "A"), new OneWayPoint(37.62, -122.38, "A")],
            BlockBoth: false,
            Notes: null
        );
        var catalog = new AirportSidecarCatalog([new AirportSidecar("KSFO") { OneWayEdges = [constraint] }]);

        Assert.Single(catalog.GetOneWayConstraints("KSFO"));
        Assert.Single(catalog.GetOneWayConstraints("SFO"));
        Assert.Empty(catalog.GetOneWayConstraints("KOAK"));
    }

    [Fact]
    public void OakDepartureRoute_ResolvesAgainstRealLayout()
    {
        // Validate the catalog's KOAK W→30 route resolves through TaxiPathfinder against the real OAK
        // ground layout — the same call the UI menu builder makes to filter applicable routes.
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return; // TestData GeoJSON not present
        }

        var startNode = layout.Nodes.Values.FirstOrDefault(n => n.Edges.Any(e => e.MatchesTaxiway("W")));
        Assert.NotNull(startNode);

        var route = SampleRoutes().GetTaxiRoutes("KOAK").First(r => r.Name == "DEP 30 via W");

        var resolved = TaxiPathfinder.ResolveExplicitPath(
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

        var resolved = TaxiPathfinder.ResolveExplicitPath(
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
