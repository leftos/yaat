using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation.GroundTaxi;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// A corner is turned over the fillet arc the generator painted there, never square through the junction
/// centre node. S2-OAK-2 bundle, SWA2600 <c>TAXI TE U W W1 30</c>: the U→W turn resolved through the U/W junction
/// centre, so the navigator rounded it at the main-gear turn radius at 3 kt and re-acquired W with a visible swing
/// instead of flying the 75 ft <c>W - U</c> fillet at its arc speed. The reverse-arc cost was the cause: half of all
/// corner traversals run against an arc's stored node order, and that penalty made the square pivot cheaper.
/// </summary>
public class FilletCornerRoutingTests(ITestOutputHelper output)
{
    /// <summary>Where SWA2600's pushback on TE ended in the S2-OAK-2 bundle, on the map-built OAK graph.</summary>
    private static readonly LatLon OakTePushbackEnd = new(37.711033464124476, -122.21370091642606);

    /// <summary>
    /// SWA2600's taxi start: the node nearest <see cref="OakTePushbackEnd"/>, a ramp node with only RAMP edges, which no
    /// <see cref="GroundNodeLookup"/> query names.
    /// </summary>
    private static GroundNode OakTeStart(AirportGroundLayout layout) =>
        layout.Nodes.Values.MinBy(node => GeoMath.DistanceNm(node.Position, OakTePushbackEnd))
        ?? throw new InvalidOperationException("OAK layout has no nodes");

    /// <summary>The U end of the <c>W - U</c> fillet arc (the only node on both U and that arc).</summary>
    private static GroundNode OakUArcEntry(AirportGroundLayout layout) => GroundNodeLookup.Junction(layout, "U", "W - U");

    /// <summary>The W end of the <c>W - U</c> fillet arc on the W1 side of the U/W junction, toward runway 30.</summary>
    private static GroundNode OakWArcExit(AirportGroundLayout layout) =>
        GroundNodeLookup.Junction(layout, "W", "W - U", GroundNodeLookup.Junction(layout, "W", "W1"));

    /// <summary>UAL2164's stop on T after crossing 28L in the S1-SFO-2 bundle (#459), nosed south-west.</summary>
    private static GroundNode SfoTAfter28LCrossing(AirportGroundLayout layout) =>
        GroundNodeLookup.HoldShortBar(layout, "28L", "T", GroundNodeLookup.Junction(layout, "B", "T"));

    private const double SfoTSouthboundHeading = 209.0;

    /// <summary>The tangent cut on B where the right-hand T/B fillet ends (spot "32" shares the node).</summary>
    private const int SfoTbRightArcExit = 30;

    /// <summary>
    /// The tangent cut on T where the right-hand T/B fillet begins: the far end, nearer <see cref="SfoTAfter28LCrossing"/>, of a
    /// T/B fillet arc ending at <see cref="SfoTbRightArcExit"/>. Found by what it is, since the fillet pass numbers it after
    /// every node it mints earlier.
    /// </summary>
    private static int SfoTbRightArcEntry(AirportGroundLayout layout)
    {
        GroundNode exit = layout.Nodes[SfoTbRightArcExit];
        GroundNode start = SfoTAfter28LCrossing(layout);
        GroundNode entry =
            layout
                .Arcs.Where(a => a.MatchesTaxiway("T") && a.MatchesTaxiway("B") && ((a.Nodes[0] == exit) || (a.Nodes[1] == exit)))
                .Select(a => a.OtherNode(exit))
                .MinBy(n => GeoMath.DistanceNm(n.Position, start.Position))
            ?? throw new InvalidOperationException($"SFO layout has no T/B fillet arc ending at node {SfoTbRightArcExit}");
        return entry.Id;
    }

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

        GroundNode teStart = OakTeStart(layout);
        Assert.True(
            teStart.Edges.All(edge => edge.TaxiwayName == "RAMP"),
            $"the node nearest SWA2600's pushback end, #{teStart.Id}, is not RAMP-only: "
                + string.Join(", ", teStart.Edges.Select(edge => edge.TaxiwayName))
        );
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            teStart.Id,
            ["TE", "U", "W", "W1"],
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("B738"),
                DestinationRunway = "30",
            },
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );

        Assert.Null(failReason);
        Assert.NotNull(route);
        Dump(route);

        int arcEntry = OakUArcEntry(layout).Id;
        int arcExit = OakWArcExit(layout).Id;
        output.WriteLine($"W - U arc {arcEntry} -> {arcExit}");
        TaxiRouteSegment corner = Assert.Single(route.Segments, s => s.FromNodeId == arcEntry && s.ToNodeId == arcExit);
        Assert.IsType<GroundArc>(corner.Edge.Edge);
        int uwJunctionCentre = GroundNodeLookup.Junction(layout, "U", "W").Id;
        Assert.DoesNotContain(route.Segments, s => s.FromNodeId == uwJunctionCentre || s.ToNodeId == uwJunctionCentre);
        RouteGeometryAsserts.AssertNoSquarePivotWhereFilletExists(route, $"OAK {teStart.Id} TE U W W1 -> 30");
    }

    [Fact]
    public void ExplicitTaxi_SfoTRightOntoB_TurnsOntoBOverTheFilletArc()
    {
        AirportGroundLayout? layout = LoadLayout("SFO");
        if (layout is null)
        {
            return;
        }

        // UAL2164 after crossing 28L on T (holding at T's 28L bar on the B side), heading 209°, cleared "TAXI >B".
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            SfoTAfter28LCrossing(layout).Id,
            ["T", "B"],
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("B738"),
                PathTurnHints = [null, TurnDirection.Right],
                StartHeadingTrue = SfoTSouthboundHeading,
            },
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );

        Assert.Null(failReason);
        Assert.NotNull(route);
        Dump(route);

        RouteGeometryAsserts.AssertNoSquarePivotWhereFilletExists(route, "SFO 855 T >B");
        int sfoTbArcEntry = SfoTbRightArcEntry(layout);
        output.WriteLine($"T/B right arc entry: node {sfoTbArcEntry}");
        Assert.Contains(route.Segments, s => (s.Edge.Edge is GroundArc) && (s.FromNodeId == sfoTbArcEntry) && (s.ToNodeId == SfoTbRightArcExit));
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
            SfoTAfter28LCrossing(layout).Id,
            ["T", "B"],
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("B738"),
                PathTurnHints = [null, TurnDirection.Left],
                StartHeadingTrue = SfoTSouthboundHeading,
            },
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
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
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
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
            pair.Category,
            WakeTurbulenceData.WakeClass.Large
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
