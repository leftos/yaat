using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation.GroundTaxi;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// A corner is turned over the fillet arc the generator painted there, never square through the junction
/// centre node. S2-OAK-2 bundle, SWA2600 <c>TAXI TE U W W1 30</c>: the U→W turn resolved as 694→17→691, so
/// the navigator rounded it at the nose-wheel radius at 3 kt and re-acquired W with a visible swing instead
/// of flying the 75 ft fillet 694→691 at its arc speed. The reverse-arc cost was the cause: half of all
/// corner traversals run against an arc's stored node order, and that penalty made the square pivot cheaper.
/// </summary>
public class FilletCornerRoutingTests(ITestOutputHelper output)
{
    /// <summary>SWA2600's pushback end on TE in the S2-OAK-2 bundle.</summary>
    private const int OakTeStart = 904;

    private const int OakUArcEntry = 694;
    private const int OakUwJunctionCentre = 17;
    private const int OakWArcExit = 691;

    /// <summary>UAL2164's stop on T after crossing 28L in the S1-SFO-2 bundle (#459), nosed south-west.</summary>
    private const int SfoTAfter28LCrossing = 855;

    private const double SfoTSouthboundHeading = 209.0;
    private const int SfoTbArcEntry = 1394;

    /// <summary>The tangent cut on B where the right-hand T/B fillet ends (spot "32" shares the node).</summary>
    private const int SfoTbRightArcExit = 30;

    private static readonly RoutePreference[] AllPreferences = [RoutePreference.FewestTurns, RoutePreference.Shortest, RoutePreference.Fastest];

    private static AirportGroundLayout? LoadLayout(string airportId)
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb is null ? null : new TestAirportGroundData().GetLayout(airportId);
    }

    [Fact]
    public void ExplicitTaxi_OakTeUWW1ToRunway30_TurnsOntoWOverTheFilletArc()
    {
        AirportGroundLayout? layout = LoadLayout("OAK");
        if (layout is null)
        {
            return;
        }

        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            OakTeStart,
            ["TE", "U", "W", "W1"],
            out string? failReason,
            new ExplicitPathOptions { OccupiedTaxiway = null, DestinationRunway = "30" },
            AircraftCategory.Jet
        );

        Assert.Null(failReason);
        Assert.NotNull(route);
        Dump(route);

        TaxiRouteSegment corner = Assert.Single(route.Segments, s => s.FromNodeId == OakUArcEntry && s.ToNodeId == OakWArcExit);
        Assert.IsType<GroundArc>(corner.Edge.Edge);
        Assert.DoesNotContain(route.Segments, s => s.FromNodeId == OakUwJunctionCentre || s.ToNodeId == OakUwJunctionCentre);
        RouteGeometryAsserts.AssertNoSquarePivotWhereFilletExists(route, "OAK 904 TE U W W1 -> 30");
    }

    [Fact]
    public void ExplicitTaxi_SfoTRightOntoB_TurnsOntoBOverTheFilletArc()
    {
        AirportGroundLayout? layout = LoadLayout("SFO");
        if (layout is null)
        {
            return;
        }

        // UAL2164 after crossing 28L on T (node 855), heading 209°, cleared "TAXI >B".
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            SfoTAfter28LCrossing,
            ["T", "B"],
            out string? failReason,
            new ExplicitPathOptions
            {
                OccupiedTaxiway = null,
                PathTurnHints = [null, TurnDirection.Right],
                StartHeadingTrue = SfoTSouthboundHeading,
            },
            AircraftCategory.Jet
        );

        Assert.Null(failReason);
        Assert.NotNull(route);
        Dump(route);

        RouteGeometryAsserts.AssertNoSquarePivotWhereFilletExists(route, "SFO 855 T >B");
        Assert.Contains(route.Segments, s => (s.Edge.Edge is GroundArc) && (s.FromNodeId == SfoTbArcEntry) && (s.ToNodeId == SfoTbRightArcExit));
    }

    [Fact]
    public void ExplicitTaxi_SfoTLeftOntoB_TurnsOntoBOverTheFilletArc()
    {
        AirportGroundLayout? layout = LoadLayout("SFO");
        if (layout is null)
        {
            return;
        }

        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            SfoTAfter28LCrossing,
            ["T", "B"],
            out string? failReason,
            new ExplicitPathOptions
            {
                OccupiedTaxiway = null,
                PathTurnHints = [null, TurnDirection.Left],
                StartHeadingTrue = SfoTSouthboundHeading,
            },
            AircraftCategory.Jet
        );

        Assert.Null(failReason);
        Assert.NotNull(route);
        Dump(route);

        RouteGeometryAsserts.AssertNoSquarePivotWhereFilletExists(route, "SFO 855 T <B");
        TaxiRouteSegment corner = Assert.Single(route.Segments, s => s.Edge.Edge is GroundArc);
        GroundArc arc = Assert.IsType<GroundArc>(corner.Edge.Edge);
        Assert.Contains("T", arc.TaxiwayNames);
        Assert.Contains("B", arc.TaxiwayNames);
        double netTurn = GeoMath.SignedBearingDifference(corner.Edge.DepartureBearing, corner.Edge.ArrivalBearing);
        Assert.True(netTurn < -45.0, $"T->B corner arc {corner.FromNodeId}->{corner.ToNodeId} turns {netTurn:F0}°, expected a left turn");
    }

    [Theory]
    [InlineData(RoutePreference.FewestTurns)]
    [InlineData(RoutePreference.Shortest)]
    [InlineData(RoutePreference.Fastest)]
    public void AutoRoute_OakGate22ToRunway30_NeverPivotsSquareAtAFilletedJunction(RoutePreference preference)
    {
        AirportGroundLayout? layout = LoadLayout("OAK");
        if (layout is null)
        {
            return;
        }

        GroundNode? origin = layout.FindParkingByName("22");
        Assert.NotNull(origin);
        GroundNode? destination = TaxiCoverageRunner.ResolveNode(layout, "30", TaxiNodeKind.RunwayExit, "30", requireForwardLineup: true);
        Assert.NotNull(destination);

        List<TaxiRoute> routes = TaxiPathfinder.FindRoutes(
            layout,
            origin.Id,
            destination.Id,
            preference,
            maxRoutes: 1,
            authorizedTaxiways: null,
            AircraftCategory.Jet
        );
        TaxiRoute route = Assert.Single(routes);
        Dump(route);

        RouteGeometryAsserts.AssertNoSquarePivotWhereFilletExists(route, $"OAK gate 22 -> 30 ({preference})");
    }

    public static IEnumerable<object[]> SmokePairsByPreference() =>
        from pair in TaxiCoverageData.OakSmoke.Concat(TaxiCoverageData.SfoSmoke).Concat(TaxiCoverageData.FllSmoke)
        from preference in AllPreferences
        select new object[] { $"{pair.PairId}/{preference}", pair, preference };

    [Theory]
    [MemberData(nameof(SmokePairsByPreference))]
    public void SmokePairAutoRoutes_NeverPivotSquareWhereAFilletExists(string caseId, TaxiPair pair, RoutePreference preference)
    {
        AirportGroundLayout? layout = LoadLayout(pair.AirportId);
        if (layout is null)
        {
            output.WriteLine($"SKIP {caseId}: NavigationDb not initialized");
            return;
        }

        GroundNode? destination = TaxiCoverageRunner.ResolveNode(
            layout,
            pair.DestinationName,
            pair.DestinationKind,
            pair.DestinationRunway,
            requireForwardLineup: true
        );
        GroundNode? origin = destination is null
            ? null
            : TaxiCoverageRunner.ResolveNode(
                layout,
                pair.OriginName,
                pair.OriginKind,
                null,
                requireForwardLineup: false,
                tieBreakerToNode: destination
            );
        if (origin is null || destination is null)
        {
            output.WriteLine($"SKIP {caseId}: endpoint not found in the {pair.AirportId} layout");
            return;
        }

        List<TaxiRoute> routes = TaxiPathfinder.FindRoutes(
            layout,
            origin.Id,
            destination.Id,
            preference,
            maxRoutes: 1,
            authorizedTaxiways: null,
            pair.Category
        );
        if (routes.Count == 0)
        {
            output.WriteLine($"SKIP {caseId}: no route from {origin.Id} to {destination.Id}");
            return;
        }

        RouteGeometryAsserts.AssertNoSquarePivotWhereFilletExists(routes[0], caseId);
    }

    private void Dump(TaxiRoute route)
    {
        output.WriteLine(route.ToSummary());
        for (int i = 0; i < route.Segments.Count; i++)
        {
            TaxiRouteSegment seg = route.Segments[i];
            string kind = seg.Edge.Edge is GroundArc ? "arc" : "   ";
            output.WriteLine($"  seg[{i, 3}] {kind} {seg.TaxiwayName, -8} {seg.FromNodeId, 5} -> {seg.ToNodeId, -5}");
        }
    }
}
