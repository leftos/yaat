using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
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
        IReadOnlySet<string> avoided = SampleAvoid().GetAvoidedTaxiways("KOAK");

        Assert.Contains("S", avoided);
        Assert.Contains("Q3", avoided);
        Assert.Equal(2, avoided.Count);
    }

    [Fact]
    public void GetAvoidedTaxiways_UnknownAirport_ReturnsEmptyNeverNull()
    {
        IReadOnlySet<string> avoided = SampleAvoid().GetAvoidedTaxiways("KSFO");
        Assert.NotNull(avoided);
        Assert.Empty(avoided);
    }

    [Fact]
    public void GetTaxiRoutes_AcceptsBothIcaoAndFaa()
    {
        AirportSidecarCatalog catalog = SampleRoutes();

        Assert.Equal(2, catalog.GetTaxiRoutes("KOAK").Count);
        Assert.Equal(2, catalog.GetTaxiRoutes("OAK").Count);
        Assert.Equal(2, catalog.GetTaxiRoutes("koak").Count);
        Assert.Single(catalog.GetTaxiRoutes("KSFO"));
        Assert.Single(catalog.GetTaxiRoutes("SFO"));
    }

    [Fact]
    public void GetTaxiRoutes_UnknownAirport_ReturnsEmpty()
    {
        AirportSidecarCatalog catalog = SampleRoutes();

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

    /// <summary>
    /// The ASDE-X landing-taxiway list: null when no sidecar declares it (any taxiway alerts), an empty set when one declares
    /// none, and case-insensitive membership either way, by ICAO and FAA alike.
    /// </summary>
    [Fact]
    public void GetAsdexLandingTaxiways_NullWhenAbsent_EmptyStaysEmpty_CaseInsensitiveMembership()
    {
        var catalog = new AirportSidecarCatalog([
            new AirportSidecar("KOAK") { AsdexLandingTaxiways = new HashSet<string>(["A"], StringComparer.OrdinalIgnoreCase) },
            new AirportSidecar("KSFO") { AsdexLandingTaxiways = new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
        ]);

        IReadOnlySet<string>? oak = catalog.GetAsdexLandingTaxiways("OAK");
        Assert.NotNull(oak);
        Assert.Contains("a", oak);

        IReadOnlySet<string>? sfo = catalog.GetAsdexLandingTaxiways("SFO");
        Assert.NotNull(sfo);
        Assert.Empty(sfo);

        Assert.Null(catalog.GetAsdexLandingTaxiways("KLAX"));
        Assert.Null(catalog.GetAsdexLandingTaxiways(""));
        Assert.Null(AirportSidecarCatalog.Empty.GetAsdexLandingTaxiways("KOAK"));
    }

    /// <summary>Two sidecars for one airport union their ASDE-X landing taxiways, the rule every other name set here follows.</summary>
    [Fact]
    public void GetAsdexLandingTaxiways_TwoFilesForOneAirport_UnionThem()
    {
        var catalog = new AirportSidecarCatalog([
            new AirportSidecar("KOAK") { AsdexLandingTaxiways = new HashSet<string>(["A"], StringComparer.OrdinalIgnoreCase) },
            new AirportSidecar("OAK") { AsdexLandingTaxiways = new HashSet<string>(["B"], StringComparer.OrdinalIgnoreCase) },
        ]);

        IReadOnlySet<string>? taxiways = catalog.GetAsdexLandingTaxiways("KOAK");
        Assert.NotNull(taxiways);
        Assert.Equal(2, taxiways.Count);
        Assert.Contains("A", taxiways);
        Assert.Contains("B", taxiways);
    }

    /// <summary>
    /// The catalog keeps its own case-insensitive set, so a file's list matches the layout's spelling whatever case it was authored
    /// in, even when it is handed a case-sensitive set.
    /// </summary>
    [Fact]
    public void GetAsdexLandingTaxiways_KeepsACaseInsensitiveSet()
    {
        var catalog = new AirportSidecarCatalog([new AirportSidecar("KOAK") { AsdexLandingTaxiways = new HashSet<string>(["A"]) }]);

        IReadOnlySet<string>? taxiways = catalog.GetAsdexLandingTaxiways("OAK");
        Assert.NotNull(taxiways);
        Assert.Contains("a", taxiways);
    }

    /// <summary>Stand-departure area rules from several files for one airport concatenate in load order, by ICAO and FAA alike.</summary>
    [Fact]
    public void GetStandDepartureAreas_ConcatenatesFilesInLoadOrder()
    {
        var first = new StandDepartureArea("28R", ExitSide.Right, StandDeparture.TaxiOut, "North Field");
        var second = new StandDepartureArea("10R", ExitSide.Right, StandDeparture.Either, null);
        var catalog = new AirportSidecarCatalog([
            new AirportSidecar("KOAK") { StandDepartureAreas = [first] },
            new AirportSidecar("OAK") { StandDepartureAreas = [second] },
        ]);

        Assert.Equal([first, second], catalog.GetStandDepartureAreas("KOAK"));
        Assert.Equal([first, second], catalog.GetStandDepartureAreas("oak"));
        Assert.Empty(catalog.GetStandDepartureAreas("KSFO"));
        Assert.Empty(catalog.GetStandDepartureAreas(""));
    }

    [Fact]
    public void GetOneWayConstraints_ByIcaoAndFaa()
    {
        var constraint = new OneWayConstraint(
            [new OneWayPoint(37.61, -122.39, "A"), new OneWayPoint(37.62, -122.38, "A")],
            BlockBoth: false,
            Notes: null,
            ExemptWakeClasses: new HashSet<WakeTurbulenceData.WakeClass>()
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
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return; // TestData GeoJSON not present
        }

        GroundNode? startNode = layout.Nodes.Values.FirstOrDefault(n => n.Edges.Any(e => e.MatchesTaxiway("W")));
        Assert.NotNull(startNode);

        TaxiRouteDefinition route = SampleRoutes().GetTaxiRoutes("KOAK").First(r => r.Name == "DEP 30 via W");

        TaxiRoute? resolved = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startNode!.Id,
            route.GetPathTokens(),
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("B738"),
                DestinationRunway = route.DestinationRunway,
            },
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );

        Assert.NotNull(resolved);
        Assert.Null(failReason);
        Assert.NotEmpty(resolved!.Segments);
    }

    [Fact]
    public void BogusRoute_FailsToResolveAgainstRealLayout()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        GroundNode? startNode = layout.Nodes.Values.FirstOrDefault(n => n.Edges.Any(e => e.MatchesTaxiway("W")));
        Assert.NotNull(startNode);

        var bogus = new TaxiRouteDefinition
        {
            AirportId = "KOAK",
            Name = "Nonsense",
            Path = "ZZZ",
        };

        TaxiRoute? resolved = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startNode!.Id,
            bogus.GetPathTokens(),
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("B738"),
            },
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );

        Assert.True(resolved is null || failReason is not null, "Bogus taxiway must not resolve to a valid route");
    }
}
