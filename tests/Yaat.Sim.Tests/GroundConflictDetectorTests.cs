using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

[Collection("GroundConflictDebugSink")]
public class GroundConflictDetectorTests
{
    /// <summary>
    /// Pins the shared aircraft-performance singletons before any test body runs: the detector reads
    /// wingspans and lengths out of them, and a class racing another one's initialization reads the
    /// default-fallback dimensions for one call and the loaded ones for the next.
    /// </summary>
    public GroundConflictDetectorTests(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        _output = output;
    }

    private readonly ITestOutputHelper _output;

    /// <summary>
    /// A <c>FOLLOWG</c> follower on a crossing taxiway, short of a KOAK junction on B its lead has still to reach, stops giving
    /// way where GIVEWAY stops it for the same pose: the follow overload, given the follower's route to the merge and the
    /// lead's path edges into and out of it, returns the GIVEWAY overload's distance to the stop (both aircraft on their
    /// assigned routes for that one).
    /// </summary>
    [Fact]
    public void GiveWayStop_FollowerAtTheMerge_MatchesGiveWay()
    {
        if (KoakFollowGeometry.StartTaxiingLead(_output) is not { } run)
        {
            return;
        }

        TaxiRoute route = Assert.IsType<TaxiRoute>(run.Lead.Ground.AssignedTaxiRoute);
        HashSet<int> routeNodes = [.. route.Segments.SelectMany(s => new[] { s.FromNodeId, s.ToNodeId })];
        (int into, GroundNode junction, GroundNode far) = Enumerable
            .Range(route.CurrentSegmentIndex + 1, route.Segments.Count - route.CurrentSegmentIndex - 2)
            .Where(i => route.PrefixDistanceFt(i + 1) - route.PrefixDistanceFt(route.CurrentSegmentIndex) <= 1400.0)
            .SelectMany(i =>
                route
                    .Segments[i]
                    .Edge.ToNode.Edges.OfType<GroundEdge>()
                    .Select(edge => (Into: i, Junction: route.Segments[i].Edge.ToNode, Edge: edge))
            )
            .Where(c =>
                !c.Edge.IsRunwayCenterline
                && !c.Edge.IsRamp
                && !routeNodes.Contains(c.Edge.OtherNode(c.Junction).Id)
                && (c.Edge.DistanceNm * FtPerNm >= 50.0)
            )
            .OrderByDescending(c => c.Edge.DistanceNm)
            .Select(c => (c.Into, c.Junction, c.Edge.OtherNode(c.Junction)))
            .First();
        AircraftState follower = KoakFollowGeometry.Spawn("N2FOL", "C172", far.Position, KoakFollowGeometry.Facing(far, junction));
        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(run.Layout, follower, run.Lead));
        Assert.Equal(junction.Id, plan.MergeNode);
        Assert.True(plan.MergeAheadOfLead);
        Assert.Equal(route.Segments[into + 1].ToNodeId, plan.LeadPathFromMerge[0].ToNodeId);
        DirectionalEdge intoMerge = Assert.IsType<DirectionalEdge>(plan.LeadEdgeIntoMerge);
        Assert.Equal((route.Segments[into].FromNodeId, junction.Id), (intoMerge.FromNodeId, intoMerge.ToNodeId));
        follower.Ground.AssignedTaxiRoute = plan.PathToMerge;

        (int NodeId, double ToStopFt) giveWay = Assert.IsType<(int, double)>(GroundConflictDetector.GiveWayStop(follower, run.Lead, out string? why));
        (int NodeId, double ToStopFt) follow = Assert.IsType<(int, double)>(
            GroundConflictDetector.GiveWayStop(follower, plan.PathToMerge, [intoMerge, plan.LeadPathFromMerge[0]], run.Lead, out string? whyNot)
        );
        _output.WriteLine(
            $"junction #{junction.Id} from #{far.Id}: GIVEWAY {giveWay.ToStopFt:F1} ft ({why}), follow {follow.ToStopFt:F1} ft ({whyNot})"
        );

        Assert.True(giveWay.ToStopFt > 0.0, "the follower is inside the lead's track already, so the comparison proves nothing");
        Assert.Equal(junction.Id, follow.NodeId);
        Assert.Equal(giveWay.ToStopFt, follow.ToStopFt, 0.5);
    }

    /// <summary>A follower with no segment left on its route to the merge has no give-way stop from the follow overload, and says why.</summary>
    [Fact]
    public void GiveWayStop_FollowerWithNoRouteLeft_NoStop()
    {
        if (KoakFollowGeometry.StartTaxiingLead(_output) is not { } run)
        {
            return;
        }

        TaxiRoute leadRoute = Assert.IsType<TaxiRoute>(run.Lead.Ground.AssignedTaxiRoute);
        AircraftState follower = KoakFollowGeometry.Spawn(
            "N2FOL",
            "C172",
            run.Chain[6].Position,
            KoakFollowGeometry.Facing(run.Chain[6], run.Chain[5])
        );
        var empty = new TaxiRoute { Segments = [], HoldShortPoints = [] };

        Assert.Null(GroundConflictDetector.GiveWayStop(follower, empty, [leadRoute.Segments[^1].Edge], run.Lead, out string? why));
        Assert.Equal("no route left to the merge", why);
    }

    /// <summary>
    /// A follower whose route reaches the merge only past the 1,500 ft look-ahead, the lead's track at the route's far end, has
    /// no give-way stop from the follow overload, and says why.
    /// </summary>
    [Fact]
    public void GiveWayStop_MergeBeyondTheLookAhead_NoStop()
    {
        if (KoakFollowGeometry.StartTaxiingLead(_output) is not { } run)
        {
            return;
        }

        TaxiRoute route = Assert.IsType<TaxiRoute>(run.Lead.Ground.AssignedTaxiRoute);
        DirectionalEdge last = route.Segments[^1].Edge;
        double straightFt = GeoMath.DistanceNm(run.Lead.Position, last.FromNode.Position) * FtPerNm;
        _output.WriteLine($"route {route.ToSummary()}: {straightFt:F0} ft straight from the lead to its last segment");
        Assert.True(straightFt >= 2500.0, $"the route's last segment is only {straightFt:F0} ft from the lead");
        AircraftState follower = KoakFollowGeometry.Spawn("N2FOL", "C172", run.Lead.Position, run.Lead.TrueHeading);

        Assert.Null(GroundConflictDetector.GiveWayStop(follower, route, [last], run.Lead, out string? why));
        Assert.Equal("the merge is beyond the 1,500 ft look-ahead along its route", why);
    }

    private const double FtPerNm = 6076.12;

    // Two points ~150 ft apart along a north-south line at KSFO
    private const double BaseLat = 37.620;
    private const double BaseLon = -122.380;
    private const double OffsetLatPer100Ft = 100.0 / FtPerNm / 60.0; // ~100ft in lat degrees

    /// <summary>~100 ft in longitude degrees at <see cref="BaseLat"/> — the latitude offset stretched by 1/cos(lat).</summary>
    private static readonly double OffsetLonPer100Ft = OffsetLatPer100Ft / Math.Cos(BaseLat * Math.PI / 180.0);

    private static AircraftState MakeAircraft(
        string callsign,
        LatLon position,
        double heading = 0,
        double gs = 0,
        double? pushbackHeading = null,
        TaxiRoute? taxiRoute = null,
        Phase? phase = null
    )
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(heading),
            IsOnGround = true,
            IndicatedAirspeed = gs,
            Ground = new AircraftGroundOps
            {
                PushbackTrueHeading = pushbackHeading.HasValue ? new TrueHeading(pushbackHeading.Value) : null,
                AssignedTaxiRoute = taxiRoute,
            },
        };

        if (phase is not null)
        {
            ac.Phases = new PhaseList();
            ac.Phases.Add(phase);
            // Start the phase so CurrentPhase is set
            ac.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        }

        return ac;
    }

    /// <summary>
    /// Builds a simple 3-node layout: Node0 --[A]--> Node1 --[A]--> Node2
    /// Nodes spaced 200ft apart along latitude.
    /// </summary>
    private static (AirportGroundLayout Layout, GroundNode N0, GroundNode N1, GroundNode N2) BuildSimpleLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        var n0 = new GroundNode
        {
            Id = 0,
            Position = new LatLon(BaseLat, BaseLon),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(BaseLat + 2 * OffsetLatPer100Ft, BaseLon),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(BaseLat + 4 * OffsetLatPer100Ft, BaseLon),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        var edge01 = new GroundEdge
        {
            Nodes = [n0, n1],
            TaxiwayName = "A",
            DistanceNm = 200.0 / FtPerNm,
        };
        var edge12 = new GroundEdge
        {
            Nodes = [n1, n2],
            TaxiwayName = "A",
            DistanceNm = 200.0 / FtPerNm,
        };

        n0.Edges.Add(edge01);
        n1.Edges.AddRange([edge01, edge12]);
        n2.Edges.Add(edge12);

        layout.Nodes[0] = n0;
        layout.Nodes[1] = n1;
        layout.Nodes[2] = n2;
        layout.Edges.AddRange([edge01, edge12]);

        layout.RebuildAdjacencyLists();
        return (layout, n0, n1, n2);
    }

    /// <summary>
    /// Builds a Y-junction: Node0 --[A]--> Node2, Node1 --[B]--> Node2
    /// Both converge on Node2.
    /// </summary>
    private static (AirportGroundLayout Layout, GroundNode N0, GroundNode N1, GroundNode N2) BuildConvergenceLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        // N0 and N1 are 300ft apart, both 200ft from N2
        var n0 = new GroundNode
        {
            Id = 0,
            Position = new LatLon(BaseLat, BaseLon - 2 * OffsetLatPer100Ft),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(BaseLat, BaseLon + 2 * OffsetLatPer100Ft),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(BaseLat + 2 * OffsetLatPer100Ft, BaseLon),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        var edge02 = new GroundEdge
        {
            Nodes = [n0, n2],
            TaxiwayName = "A",
            DistanceNm = 200.0 / FtPerNm,
        };
        var edge12 = new GroundEdge
        {
            Nodes = [n1, n2],
            TaxiwayName = "B",
            DistanceNm = 200.0 / FtPerNm,
        };

        n0.Edges.Add(edge02);
        n1.Edges.Add(edge12);
        n2.Edges.AddRange([edge02, edge12]);

        layout.Nodes[0] = n0;
        layout.Nodes[1] = n1;
        layout.Nodes[2] = n2;
        layout.Edges.AddRange([edge02, edge12]);

        layout.RebuildAdjacencyLists();
        return (layout, n0, n1, n2);
    }

    /// <summary>The mover of the parked-neighbour pair; the parked aircraft is the B738 <see cref="MakeAircraft"/> builds.</summary>
    private const string ParkedNeighborMoverType = "E75L";

    /// <summary>Bearing in degrees from the parked aircraft to the mover stopped beside it.</summary>
    private const double ParkedNeighborBearingDeg = 60.0;

    /// <summary>Nose heading of that mover: 40° off the 240° bearing back to the parked aircraft, as if it braked mid-turn.</summary>
    private const double ParkedNeighborMoverHeadingDeg = 200.0;

    /// <summary>How far inside the pair's stop ring the mover is placed, so the detector must actively release it.</summary>
    private const double InsideStopRingMarginFt = 5.0;

    /// <summary>
    /// The separation at which the detector pins a pair to a stop: the two half-lengths plus
    /// <see cref="GroundConflictDetector.StopBufferFt"/>, floored at
    /// <see cref="GroundConflictDetector.DefaultStopDistanceFt"/> — the same arithmetic the detector's
    /// <c>GetSeparation</c> does, over the same FAA dimensions, rather than a copied number that drifts. Shared with
    /// the crossing tests, which hold their pair to it too.
    /// </summary>
    internal static double StopRingFt(string leaderType, string followerType) =>
        Math.Max(
            GroundConflictDetector.DefaultStopDistanceFt,
            ((LengthFt(leaderType) + LengthFt(followerType)) / 2) + GroundConflictDetector.StopBufferFt
        );

    /// <summary>The stop ring of this class's parked-neighbour pair.</summary>
    private static double ParkedNeighborStopRingFt => StopRingFt("B738", ParkedNeighborMoverType);

    /// <summary>The FAA fuselage length of an aircraft type, in feet.</summary>
    /// <param name="type">ICAO type designator.</param>
    /// <returns>Length in feet.</returns>
    /// <exception cref="InvalidOperationException">The FAA database carries no dimensions for that type.</exception>
    private static double LengthFt(string type) =>
        FaaAircraftDatabase.Get(type)?.LengthFt ?? throw new InvalidOperationException($"the FAA database has no dimensions for '{type}'");

    /// <summary>
    /// A parked B738 at the base point with an E75L stopped <see cref="InsideStopRingMarginFt"/> inside the
    /// pair's stop ring (<see cref="ParkedNeighborStopRingFt"/>, 142.75 ft for this pair) on a bearing of
    /// 060° from it, nose 40° off the bearing back to it — the geometry an arrival that braked mid-turn in a
    /// ramp alley ends up in. When <paramref name="routeLateralFt"/> is given the E75L carries a straight
    /// taxi route passing the parked aircraft at that lateral distance; otherwise it has no route at all and
    /// must be rolling to count as a mover.
    /// </summary>
    private static (AircraftState Parked, AircraftState Mover) BuildParkedNeighborPair(double? routeLateralFt, double moverGs)
    {
        AircraftState parked = MakeAircraft("PRK", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, phase: new AtParkingPhase());

        double separationFt = ParkedNeighborStopRingFt - InsideStopRingMarginFt;
        double bearingRad = ParkedNeighborBearingDeg * Math.PI / 180.0;
        double northFt = separationFt * Math.Cos(bearingRad);
        double eastFt = separationFt * Math.Sin(bearingRad);
        var mover = new AircraftState
        {
            Callsign = "MOV",
            AircraftType = ParkedNeighborMoverType,
            Position = new LatLon(BaseLat + ((northFt / 100.0) * OffsetLatPer100Ft), BaseLon + ((eastFt / 100.0) * OffsetLonPer100Ft)),
            TrueHeading = new TrueHeading(ParkedNeighborMoverHeadingDeg),
            IsOnGround = true,
            IndicatedAirspeed = moverGs,
            Ground = new AircraftGroundOps { AssignedTaxiRoute = routeLateralFt is { } lateralFt ? MakeStraightRoute(lateralFt) : null },
        };

        return (parked, mover);
    }

    /// <summary>
    /// One 400 ft taxi segment running north past the base point at <paramref name="lateralFt"/> to its east,
    /// closest approach abeam it. Carries real node positions, unlike <see cref="MakeSeg"/>.
    /// </summary>
    private static TaxiRoute MakeStraightRoute(double lateralFt)
    {
        double lonOffset = (lateralFt / 100.0) * OffsetLonPer100Ft;
        var from = new GroundNode
        {
            Id = 10,
            Position = new LatLon(BaseLat - (2 * OffsetLatPer100Ft), BaseLon + lonOffset),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var to = new GroundNode
        {
            Id = 11,
            Position = new LatLon(BaseLat + (2 * OffsetLatPer100Ft), BaseLon + lonOffset),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        return MakeRoute(MakeGeoSeg(from, to));
    }

    /// <summary>A straight route segment between two real nodes, its length taken from their positions.</summary>
    private static TaxiRouteSegment MakeGeoSeg(GroundNode from, GroundNode to)
    {
        var edge = new GroundEdge
        {
            Nodes = [from, to],
            TaxiwayName = "T6A",
            DistanceNm = GeoMath.DistanceNm(from.Position, to.Position),
        };
        return new TaxiRouteSegment { TaxiwayName = "T6A", Edge = edge.Directed(from, to) };
    }

    [Fact]
    public void ParkedNeighbor_RouteClearsIt_MoverNotStopped()
    {
        // The lane the mover will actually drive passes the parked B738 at 140 ft — more than the
        // 50.85 + 58.7 + 25 = 134.55 ft the wingspan bypass needs — so nothing may cap it, even though its
        // nose (40° off the bearing) points between the lanes and reads as a closing conflict.
        (AircraftState? parked, AircraftState? mover) = BuildParkedNeighborPair(routeLateralFt: 140.0, moverGs: 0);

        var aircraft = new List<AircraftState> { parked, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.True(
            mover.Ground.SpeedLimit is null,
            $"the route clears the parked aircraft by 140ft, but the mover was capped at {mover.Ground.SpeedLimit}"
        );
    }

    [Fact]
    public void ParkedNeighbor_RoutePassesTooClose_MoverStops()
    {
        // Same geometry, but the route runs 60 ft from the parked aircraft — inside the wingspan
        // clearance — so the mover still stops.
        (AircraftState? parked, AircraftState? mover) = BuildParkedNeighborPair(routeLateralFt: 60.0, moverGs: 0);

        var aircraft = new List<AircraftState> { parked, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.NotNull(mover.Ground.SpeedLimit);
        Assert.Equal(0.0, mover.Ground.SpeedLimit!.Value);
    }

    [Fact]
    public void ParkedNeighbor_NextSegmentPassesClose_StillStops()
    {
        // The route budget is what is left to drive, not the whole current segment: the mover is 20 ft from
        // the end of a segment that clears the parked aircraft, and the next one passes 40 ft from it.
        // Charging the full current segment spent the whole budget on the clearing leg and let the mover go.
        (AircraftState? parked, AircraftState? mover) = BuildParkedNeighborPair(routeLateralFt: 140.0, moverGs: 0);
        double closeLonOffset = (40.0 / 100.0) * OffsetLonPer100Ft;
        var elbow = new GroundNode
        {
            Id = 12,
            Position = new LatLon(BaseLat + (0.9 * OffsetLatPer100Ft), BaseLon + ((140.0 / 100.0) * OffsetLonPer100Ft)),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var closePass = new GroundNode
        {
            Id = 13,
            Position = new LatLon(BaseLat + (0.9 * OffsetLatPer100Ft), BaseLon + closeLonOffset),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        // Segment 0 ends 20 ft ahead of the mover at the elbow; segment 1 turns in to within 40 ft.
        TaxiRouteSegment first = mover.Ground.AssignedTaxiRoute!.Segments[0];
        mover.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [MakeGeoSeg(first.Edge.FromNode, elbow), MakeGeoSeg(elbow, closePass)],
            HoldShortPoints = [],
            CurrentSegmentIndex = 0,
        };
        mover.Position = new LatLon(BaseLat + (0.7 * OffsetLatPer100Ft), BaseLon + ((140.0 / 100.0) * OffsetLonPer100Ft));

        var aircraft = new List<AircraftState> { parked, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // Capped down to a crawl, not waved through: charging the whole current segment spent the budget on
        // the clearing leg, never saw the 40 ft turn-in, and left the limit null.
        Assert.NotNull(mover.Ground.SpeedLimit);
        Assert.True(
            mover.Ground.SpeedLimit <= 5.0,
            $"the next segment passes 40ft from the parked aircraft, but the mover kept {mover.Ground.SpeedLimit}kt"
        );
    }

    [Fact]
    public void ParkedShadow_RouteClearsIt_MoverNotStopped()
    {
        // A live-traffic shadow standing still is as fixed as a parked aircraft — external, so nothing we do
        // moves it — and the route-aware bypass has to treat it the same way or a stopped shadow gates every
        // aircraft whose lane merely points at it.
        (AircraftState? parked, AircraftState? mover) = BuildParkedNeighborPair(routeLateralFt: 140.0, moverGs: 0);
        parked.Phases = null;
        parked.LiveTraffic = new AircraftLiveTraffic();

        var aircraft = new List<AircraftState> { parked, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.True(
            mover.Ground.SpeedLimit is null,
            $"the route clears the stopped shadow by 140ft, but the mover was capped at {mover.Ground.SpeedLimit}"
        );
    }

    [Fact]
    public void ParkedNeighbor_MoverWithoutRoute_KeepsHeadingOnlyStop()
    {
        // With no route there is nothing but the nose to go on, so the heading-based test still rules: the
        // mover sits inside the stop ring and the lateral room along its heading is only ~89 ft.
        (AircraftState? parked, AircraftState? mover) = BuildParkedNeighborPair(routeLateralFt: null, moverGs: 8);

        var aircraft = new List<AircraftState> { parked, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.NotNull(mover.Ground.SpeedLimit);
        Assert.Equal(0.0, mover.Ground.SpeedLimit!.Value);
    }

    private static TaxiRoute MakeRoute(params TaxiRouteSegment[] segments)
    {
        return new TaxiRoute
        {
            Segments = [.. segments],
            HoldShortPoints = [],
            CurrentSegmentIndex = 0,
        };
    }

    /// <summary>
    /// A route segment traversing <paramref name="edge"/> from one of its nodes to the other. An id that
    /// names a node of <see cref="GroundEdge.Nodes"/> resolves to that node, so the segment carries the
    /// layout's real geometry.
    ///
    /// <para>An id that names neither — the synthetic ids a few pairs use — gets a placeholder node at
    /// (0, 0) instead, which is all a test reading only the segment's taxiway name and node ids needs. Any
    /// test whose assertion depends on where the route runs (a parked obstacle measured against it, a
    /// lateral clearance, a distance) must therefore pass ids of the edge, or build its nodes outright the
    /// way <see cref="MakeGeoSeg"/> does: a placeholder at the origin puts the route in the Gulf of Guinea,
    /// thousands of miles from the obstacle, and every clearance test against it passes vacuously.</para>
    /// </summary>
    private static TaxiRouteSegment MakeSeg(int from, int to, string taxiway, GroundEdge edge)
    {
        GroundNode fromNode = edge.Nodes.FirstOrDefault(n => n.Id == from) ?? PlaceholderNode(from);
        GroundNode toNode = edge.Nodes.FirstOrDefault(n => n.Id == to) ?? PlaceholderNode(to);
        return new TaxiRouteSegment { TaxiwayName = taxiway, Edge = edge.Directed(fromNode, toNode) };
    }

    private static GroundNode PlaceholderNode(int id) =>
        new()
        {
            Id = id,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };

    [Fact]
    public void TwoTaxiing_SameEdgeSameDirection_TrailerGetsSpeedLimit()
    {
        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        GroundEdge edge01 = layout.Edges[0];

        // Both on edge 0→1, heading north, A is behind B (further from node 1)
        TaxiRoute routeA = MakeRoute(MakeSeg(0, 1, "A", edge01));
        TaxiRoute routeB = MakeRoute(MakeSeg(0, 1, "A", edge01));

        // A at node 0, B 150ft ahead
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        AircraftState b = MakeAircraft(
            "B",
            new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon),
            heading: 0,
            gs: 10,
            taxiRoute: routeB,
            phase: new TaxiingPhase()
        );

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout);

        // One of them should have a speed limit (the trailer)
        bool anyLimited = a.Ground.SpeedLimit is not null || b.Ground.SpeedLimit is not null;
        Assert.True(anyLimited, "Expected at least one aircraft to have a speed limit on same edge");
    }

    /// <summary>
    /// A leader whose type the FAA database does not carry is sized by its wake category: the A225 is CWT A (super),
    /// so the CWT resolver gives it 250 ft and the stop ring against a B738 trailer is (250 + 129.5) / 2 + 25 ft. A
    /// trailer 20 ft inside that ring must stop, not merely match the leader's speed as it would behind a 60 ft guess.
    /// </summary>
    [Fact]
    public void SameEdgeTrailing_UnknownHeavyLeader_StopRingUsesCwtLength()
    {
        Assert.Null(FaaAircraftDatabase.Get("A225"));
        Assert.Equal("A", WakeTurbulenceData.GetCwt("A225"));

        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        GroundEdge edge01 = layout.Edges[0];
        double stopRingFt = ((AircraftLength.CwtFallbackLengthFt("A225") + LengthFt("B738")) / 2) + GroundConflictDetector.StopBufferFt;
        double gapFt = stopRingFt - 20.0;

        AircraftState trailer = MakeAircraft(
            "TRAIL",
            new LatLon(BaseLat, BaseLon),
            heading: 0,
            gs: 15,
            taxiRoute: MakeRoute(MakeSeg(0, 1, "A", edge01)),
            phase: new TaxiingPhase()
        );
        AircraftState leader = MakeAircraft(
            "LEAD",
            new LatLon(BaseLat + (gapFt / 100.0 * OffsetLatPer100Ft), BaseLon),
            heading: 0,
            gs: 10,
            taxiRoute: MakeRoute(MakeSeg(0, 1, "A", edge01)),
            phase: new TaxiingPhase()
        );
        leader.AircraftType = "A225";

        GroundConflictDetector.ApplySpeedLimits([trailer, leader], layout);

        Assert.Null(leader.Ground.SpeedLimit);
        Assert.NotNull(trailer.Ground.SpeedLimit);
        Assert.Equal(0.0, trailer.Ground.SpeedLimit!.Value);
    }

    [Fact]
    public void TwoTaxiing_ConvergingOnSameNode_FartherOneSlows()
    {
        (AirportGroundLayout? layout, GroundNode? n0, GroundNode? n1, GroundNode _) = BuildConvergenceLayout();

        TaxiRoute routeA = MakeRoute(MakeSeg(0, 2, "A", layout.Edges[0]));
        TaxiRoute routeB = MakeRoute(MakeSeg(1, 2, "B", layout.Edges[1]));

        // A is further from N2 than B
        AircraftState a = MakeAircraft("A", n0.Position, heading: 45, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        AircraftState b = MakeAircraft("B", n1.Position, heading: 315, gs: 15, taxiRoute: routeB, phase: new TaxiingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout);

        // At least one should be limited (the one further from node 2)
        bool anyLimited = a.Ground.SpeedLimit is not null || b.Ground.SpeedLimit is not null;
        Assert.True(anyLimited, "Expected convergence detection to limit at least one aircraft");
    }

    /// <summary>
    /// Convergence layout with the two start nodes at very different distances from the shared node:
    /// N0 (yielder) is ~1000 ft from N2, N1 (winner) is ~150 ft from N2. Used to exercise the ETA
    /// gate that skips the slowdown when the nearer aircraft clears the shared node first.
    /// </summary>
    private static (AirportGroundLayout Layout, GroundNode N0, GroundNode N1, GroundNode N2) BuildAsymmetricConvergenceLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var n2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(BaseLat, BaseLon),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n0 = new GroundNode
        {
            Id = 0,
            Position = new LatLon(BaseLat - 10 * OffsetLatPer100Ft, BaseLon),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        var edge02 = new GroundEdge
        {
            Nodes = [n0, n2],
            TaxiwayName = "A",
            DistanceNm = 1000.0 / FtPerNm,
        };
        var edge12 = new GroundEdge
        {
            Nodes = [n1, n2],
            TaxiwayName = "B",
            DistanceNm = 150.0 / FtPerNm,
        };

        n0.Edges.Add(edge02);
        n1.Edges.Add(edge12);
        n2.Edges.AddRange([edge02, edge12]);
        layout.Nodes[0] = n0;
        layout.Nodes[1] = n1;
        layout.Nodes[2] = n2;
        layout.Edges.AddRange([edge02, edge12]);
        layout.RebuildAdjacencyLists();
        return (layout, n0, n1, n2);
    }

    [Fact]
    public void Convergence_NearerAircraftClearsFirst_FartherIsNotSlowed()
    {
        (AirportGroundLayout? layout, GroundNode? n0, GroundNode? n1, GroundNode _) = BuildAsymmetricConvergenceLayout();

        TaxiRoute routeA = MakeRoute(MakeSeg(0, 2, "A", layout.Edges[0]));
        TaxiRoute routeB = MakeRoute(MakeSeg(1, 2, "B", layout.Edges[1]));

        // A (yielder) is ~1000 ft from the shared node; B (winner) is ~150 ft and moving fast, so it
        // clears the node well before A arrives. A must not be slowed.
        AircraftState a = MakeAircraft("A", n0.Position, heading: 0, gs: 8, taxiRoute: routeA, phase: new TaxiingPhase());
        AircraftState b = MakeAircraft("B", n1.Position, heading: 180, gs: 25, taxiRoute: routeB, phase: new TaxiingPhase());

        GroundConflictDetector.ApplySpeedLimits([a, b], layout);

        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(a.Ground.AutoYieldTarget);
    }

    [Fact]
    public void Convergence_NearStoppedWinner_FartherStillSlows()
    {
        (AirportGroundLayout? layout, GroundNode? n0, GroundNode? n1, GroundNode _) = BuildAsymmetricConvergenceLayout();

        TaxiRoute routeA = MakeRoute(MakeSeg(0, 2, "A", layout.Edges[0]));
        TaxiRoute routeB = MakeRoute(MakeSeg(1, 2, "B", layout.Edges[1]));

        // The nearer aircraft B is essentially stopped (<= 3 kt), so it cannot be trusted to clear
        // the node first — the gate keeps the slowdown and the farther aircraft A yields.
        AircraftState a = MakeAircraft("A", n0.Position, heading: 0, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        AircraftState b = MakeAircraft("B", n1.Position, heading: 180, gs: 2, taxiRoute: routeB, phase: new TaxiingPhase());

        GroundConflictDetector.ApplySpeedLimits([a, b], layout);

        Assert.NotNull(a.Ground.SpeedLimit);
    }

    [Fact]
    public void Convergence_AnnotatesYielderWithAutoYieldTarget()
    {
        (AirportGroundLayout? layout, GroundNode? n0, GroundNode? n1, GroundNode _) = BuildConvergenceLayout();
        TaxiRoute routeA = MakeRoute(MakeSeg(0, 2, "A", layout.Edges[0]));
        TaxiRoute routeB = MakeRoute(MakeSeg(1, 2, "B", layout.Edges[1]));
        AircraftState a = MakeAircraft("A", n0.Position, heading: 45, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        AircraftState b = MakeAircraft("B", n1.Position, heading: 315, gs: 15, taxiRoute: routeB, phase: new TaxiingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout);

        // The yielder is the speed-limited one; it carries the auto-yield annotation
        // pointing at the winner. The winner carries none.
        AircraftState yielder = a.Ground.SpeedLimit is not null ? a : b;
        AircraftState winner = ReferenceEquals(yielder, a) ? b : a;
        Assert.Equal(winner.Callsign, yielder.Ground.AutoYieldTarget);
        Assert.False(yielder.Ground.AutoYieldIsFollowing); // converging give-way, not in-trail follow
        Assert.Null(winner.Ground.AutoYieldTarget);
    }

    [Fact]
    public void SameEdgeTrailing_AnnotatesTrailerWithAutoYieldTarget()
    {
        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        GroundEdge edge01 = layout.Edges[0];
        TaxiRoute routeA = MakeRoute(MakeSeg(0, 1, "A", edge01));
        TaxiRoute routeB = MakeRoute(MakeSeg(0, 1, "A", edge01));
        // A behind, B ahead — A trails B on the shared edge.
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        AircraftState b = MakeAircraft(
            "B",
            new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon),
            heading: 0,
            gs: 10,
            taxiRoute: routeB,
            phase: new TaxiingPhase()
        );

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout);

        AircraftState trailer = a.Ground.SpeedLimit is not null ? a : b;
        AircraftState leader = ReferenceEquals(trailer, a) ? b : a;
        Assert.Equal(leader.Callsign, trailer.Ground.AutoYieldTarget);
        Assert.True(trailer.Ground.AutoYieldIsFollowing); // in-trail follow, not converging give-way
        Assert.Null(leader.Ground.AutoYieldTarget);
    }

    /// <summary>The aircraft type the crossing pair is built from.</summary>
    private const string CrossingAircraftType = "E75L";

    /// <summary>How far past the crossing's entry segment end the crossing leader sits.</summary>
    private const double CrossingLeaderPastEntryEndFt = 120.0;

    /// <summary>How far short of the entry segment's end node the crossing follower sits, on that segment's own line.</summary>
    private const double FollowerShortOfEntryEndFt = 30.0;

    /// <summary>
    /// Two <see cref="CrossingAircraftType"/>s crossing SFO's <see cref="SfoGroundHarness.CrossingRunway"/> in trail
    /// on <see cref="SfoGroundHarness.CrossingTaxiway"/>, both with their route position held on the crossing's entry
    /// segment (the crossing phase's contract): the follower <see cref="FollowerShortOfEntryEndFt"/> ft short of that
    /// segment's end node, the leader <see cref="CrossingLeaderPastEntryEndFt"/> ft past it, both on the segment's own
    /// line. The follower must trail and the leader must go uncapped: ordering the pair by straight-line distance to the
    /// segment's end node — which grows again once an aircraft is past it — makes the leader read as the trailing
    /// aircraft, and the detector then caps the leader for the aircraft running up behind it.
    /// </summary>
    [Fact]
    public void CrossingInTrail_LeaderPastTheEntryEnd_FollowerTrailsAndLeaderGoesFree()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(layout);
        const string LeaderCallsign = "LEAD";
        const string FollowerCallsign = "TRAIL";

        AircraftState leader = MakeCrossingInTrailAircraft(LeaderCallsign, crossing, CrossingLeaderPastEntryEndFt, groundSpeedKts: 20.0);
        AircraftState follower = MakeCrossingInTrailAircraft(FollowerCallsign, crossing, -FollowerShortOfEntryEndFt, groundSpeedKts: 10.0);

        GroundConflictDetector.ApplySpeedLimits([leader, follower], layout);

        Assert.True(
            leader.Ground.SpeedLimit is null,
            $"the leader {CrossingLeaderPastEntryEndFt:F0} ft past the crossing's entry segment end was capped at {leader.Ground.SpeedLimit} kt"
        );
        Assert.NotNull(follower.Ground.SpeedLimit);
        Assert.Equal(LeaderCallsign, follower.Ground.AutoYieldTarget);
        Assert.True(follower.Ground.AutoYieldIsFollowing);
        Assert.Null(leader.Ground.AutoYieldTarget);
    }

    /// <summary>
    /// A <see cref="CrossingAircraftType"/> on the crossing's entry segment line — the taxiway run from
    /// <paramref name="crossing"/>'s approach bar into the crossing — placed <paramref name="alongEntryEndFt"/> ft
    /// along that line from the entry segment's end node (negative = short of it), rolling at
    /// <paramref name="groundSpeedKts"/>, in a <see cref="SfoGroundHarness.CrossingRunway"/> crossing whose route
    /// position is held on the entry segment.
    /// </summary>
    private static AircraftState MakeCrossingInTrailAircraft(string callsign, SfoCrossing crossing, double alongEntryEndFt, double groundSpeedKts)
    {
        var heading = new TrueHeading(GeoMath.BearingTo(crossing.ApproachBar.Position, crossing.EnteredNode.Position));
        TrueHeading project = alongEntryEndFt >= 0 ? heading : new TrueHeading(heading.Degrees + 180.0);
        LatLon position = GeoMath.ProjectPoint(crossing.EnteredNode.Position, project, Math.Abs(alongEntryEndFt) / FtPerNm);
        AircraftState ac = MakeAircraft(
            callsign,
            position,
            heading: heading.Degrees,
            gs: groundSpeedKts,
            taxiRoute: MakeRoute(MakeGeoSeg(crossing.ApproachBar, crossing.EnteredNode), MakeGeoSeg(crossing.EnteredNode, crossing.BeyondNode)),
            phase: new CrossingRunwayPhase(crossing.ApproachBar.Id, crossing.FarBar.Id, SfoGroundHarness.CrossingRunway)
        );
        ac.AircraftType = CrossingAircraftType;
        return ac;
    }

    /// <summary>
    /// A fillet the in-trail ordering is exercised on must turn more than this, so its leader can sit past the 90°
    /// point where a tangent projection stops growing.
    /// </summary>
    private const double MinArcSweepDeg = 100.0;

    /// <summary>How far round the fillet the leader is placed: past 90°, where projecting onto the start tangent is already shrinking.</summary>
    private const double LeaderTurnDeg = 110.0;

    /// <summary>How far round the fillet the follower is placed: short of 90°, where that projection is still growing.</summary>
    private const double FollowerTurnDeg = 80.0;

    /// <summary>Sweep left out past the leader's turn, so it is placed on the arc and not past its far node.</summary>
    private const double ArcEndMarginDeg = 4.0;

    /// <summary>Bisection steps placing an aircraft by the arc's turn; 60 halves the curve parameter past double precision.</summary>
    private const int ArcSearchIterations = 60;

    /// <summary>
    /// <see cref="GroundConflictDetector"/>'s pair search range, in feet: two aircraft farther apart than this are
    /// never paired, so a fillet longer than it could host a pair the detector does not see and leave this test
    /// asserting nothing.
    /// </summary>
    private const double DetectorInteractionRangeFt = GroundConflictDetector.SearchRangeNm * FtPerNm;

    /// <summary>
    /// Two B738s in trail on one fillet arc that turns more than 90°, the leader past the 90° point and the follower
    /// short of it: the leader must go uncapped and the follower must yield to it. The trailer is whoever has less
    /// progress along the shared edge, so that progress has to keep growing all the way round an arc, and this pins
    /// the line it is measured along: measured along the edge's departure tangent — the tangent at the from-node only
    /// — it stops growing at 90° of sweep and falls away after, so on a 118° fillet the leader 110° round reads 0.94 R
    /// against the follower 80° round's 0.985 R and the leader is capped for the aircraft running up behind it.
    ///
    /// <para>The distance-to-the-end-node metric the crossing case above replaced happened to order a fillet correctly;
    /// the departure tangent is what this case pins.</para>
    /// </summary>
    [Fact]
    public void ArcInTrail_LeaderPastNinetyDegreesOfTurn_FollowerTrailsAndLeaderGoesFree()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        (GroundArc arc, CubicBezier curve, double sweepDeg) = FindLongestTurn(layout);
        double leaderDeg = Math.Min(LeaderTurnDeg, sweepDeg - ArcEndMarginDeg);
        double followerDeg = Math.Min(FollowerTurnDeg, leaderDeg - 1.0);
        Assert.True(
            (followerDeg < 90.0) && (leaderDeg > 90.0),
            $"the {sweepDeg:F0}° fillet cannot host a follower short of 90° and a leader past it ({followerDeg:F0}°/{leaderDeg:F0}°)"
        );

        DirectionalEdge directed = arc.Directed(arc.Nodes[0], arc.Nodes[1]);
        (LatLon leaderPosition, TrueHeading leaderHeading) = PointAfterTurn(curve, leaderDeg);
        (LatLon followerPosition, TrueHeading followerHeading) = PointAfterTurn(curve, followerDeg);
        const string LeaderCallsign = "ARCL";
        const string FollowerCallsign = "ARCT";

        AircraftState leader = MakeAircraft(
            LeaderCallsign,
            leaderPosition,
            heading: leaderHeading.Degrees,
            gs: 20.0,
            taxiRoute: ArcRoute(arc, directed),
            phase: new TaxiingPhase()
        );
        AircraftState follower = MakeAircraft(
            FollowerCallsign,
            followerPosition,
            heading: followerHeading.Degrees,
            gs: 10.0,
            taxiRoute: ArcRoute(arc, directed),
            phase: new TaxiingPhase()
        );

        GroundConflictDetector.ApplySpeedLimits([leader, follower], layout);

        Assert.True(
            leader.Ground.SpeedLimit is null,
            $"the leader {leaderDeg:F0}° round a {sweepDeg:F0}° fillet was capped at {leader.Ground.SpeedLimit} kt"
        );
        Assert.NotNull(follower.Ground.SpeedLimit);
        Assert.Equal(LeaderCallsign, follower.Ground.AutoYieldTarget);
        Assert.True(follower.Ground.AutoYieldIsFollowing);
        Assert.Null(leader.Ground.AutoYieldTarget);
    }

    /// <summary>
    /// The fillet arc with the widest turn on <paramref name="layout"/> — a non-ramp taxiway arc, short enough that
    /// two aircraft on it fall inside <see cref="DetectorInteractionRangeFt"/> — with the Bézier it is played as and
    /// that turn in degrees. Fails when the layout carries nothing turning more than <see cref="MinArcSweepDeg"/>.
    /// </summary>
    private static (GroundArc Arc, CubicBezier Curve, double SweepDeg) FindLongestTurn(AirportGroundLayout layout)
    {
        var turns = new List<(GroundArc Arc, CubicBezier Curve, double SweepDeg)>();
        foreach (GroundArc arc in layout.Arcs)
        {
            if (arc.IsRamp || arc.IsRunwayCenterline || (arc.DistanceNm * FtPerNm >= DetectorInteractionRangeFt))
            {
                continue;
            }

            CubicBezier curve = arc.ToBezier();
            double sweepDeg = Math.Abs(GeoMath.SignedBearingDifference(curve.TangentBearing(0.0), curve.TangentBearing(1.0)));
            turns.Add((arc, curve, sweepDeg));
        }

        if (turns.Count == 0)
        {
            Assert.Fail($"the SFO layout carries no taxiway fillet arc within the detector's {DetectorInteractionRangeFt:F0} ft interaction range");
        }

        (GroundArc Arc, CubicBezier Curve, double SweepDeg) best = turns.MaxBy(t => t.SweepDeg);
        Assert.True(
            best.SweepDeg > MinArcSweepDeg,
            $"the widest turn available is {best.SweepDeg:F1}°, under the {MinArcSweepDeg:F0}° this test needs"
        );
        return best;
    }

    /// <summary>
    /// The point on <paramref name="curve"/> where its tangent has turned <paramref name="turnDeg"/> degrees from the
    /// curve's start, with the tangent bearing there.
    /// </summary>
    private static (LatLon Position, TrueHeading Heading) PointAfterTurn(CubicBezier curve, double turnDeg)
    {
        double startBearingDeg = curve.TangentBearing(0.0);
        double lo = 0.0;
        double hi = 1.0;
        for (int i = 0; i < ArcSearchIterations; i++)
        {
            double mid = (lo + hi) / 2.0;
            if (Math.Abs(GeoMath.SignedBearingDifference(startBearingDeg, curve.TangentBearing(mid))) < turnDeg)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        double t = (lo + hi) / 2.0;
        (double lat, double lon) = curve.Evaluate(t);
        return (new LatLon(lat, lon), new TrueHeading(curve.TangentBearing(t)));
    }

    /// <summary>A fresh one-segment route over <paramref name="directed"/>, so the two aircraft do not share one route object.</summary>
    private static TaxiRoute ArcRoute(GroundArc arc, DirectionalEdge directed) =>
        MakeRoute(new TaxiRouteSegment { TaxiwayName = arc.TaxiwayName, Edge = directed });

    [Fact]
    public void NoConflict_ClearsStaleAutoYieldTarget()
    {
        // Two aircraft well outside SearchRangeNm — no pair is formed, and the per-tick
        // reset must clear any stale annotation.
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);
        AircraftState b = MakeAircraft("B", new LatLon(BaseLat + 1.0, BaseLon), heading: 0, gs: 15);
        a.Ground.AutoYieldTarget = "STALE";

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Null(a.Ground.AutoYieldTarget);
        Assert.Null(b.Ground.AutoYieldTarget);
    }

    [Fact]
    public void MovingAircraft_ClosingOnStationary_MovingOneStops()
    {
        // B is stationary at parking, A is taxiing toward B at 150ft
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);
        AircraftState b = MakeAircraft("B", new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon), heading: 90, gs: 0, phase: new AtParkingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // A should be limited (closing on stationary B)
        Assert.NotNull(a.Ground.SpeedLimit);
        // B is stationary — no limit needed
        Assert.Null(b.Ground.SpeedLimit);
    }

    [Fact]
    public void LiningUpToward_LinedUpAndWaiting_MovingOneStops()
    {
        // Issue #409: B is lined up and waiting on the runway; A was cleared for takeoff
        // from the hold-short at the same entry and is actively lining up toward B.
        // A must be treated as a mover (not a parked obstacle) so it stops behind B
        // instead of driving through it.
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 10, phase: new LineUpPhase());
        AircraftState b = MakeAircraft(
            "B",
            new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon),
            heading: 0,
            gs: 0,
            phase: new LinedUpAndWaitingPhase()
        );

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.NotNull(a.Ground.SpeedLimit);
        Assert.Equal(0.0, a.Ground.SpeedLimit!.Value);
        Assert.Null(b.Ground.SpeedLimit);
    }

    [Fact]
    public void LiningUpAircraft_BelowStationaryThreshold_IsStillStoppedBehindLuawOccupant()
    {
        // Issue #409 survivor: LineUpPhase drives at a 2 kt lineup speed, below the 3 kt held-stationary
        // threshold, so a lining-up aircraft creeping toward a LUAW occupant classified as a parked
        // obstacle on every sub-tick its speed had just been pinned to zero. No limit was written on
        // those sub-ticks, and it crept forward at ~3 ft/s. The commanded speed is the intent signal:
        // an aircraft asking for forward speed is a mover however slowly it happens to be rolling.
        AircraftState luaw = MakeAircraft(
            "LUAW1",
            new LatLon(BaseLat + (0.8 * OffsetLatPer100Ft), BaseLon),
            heading: 0,
            gs: 0,
            phase: new LinedUpAndWaitingPhase()
        );
        luaw.Targets.TargetSpeed = 0;

        AircraftState liningUp = MakeAircraft("LINE1", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, phase: new LineUpPhase());

        LatLon startPosition = liningUp.Position;
        var aircraft = new List<AircraftState> { luaw, liningUp };

        for (int k = 1; k <= 4; k++)
        {
            // LineUpPhase re-publishes its lineup speed every tick; mirror that before each detector pass.
            liningUp.Targets.TargetSpeed = 2;

            GroundConflictDetector.ApplySpeedLimits(aircraft, null, 0.25);

            Assert.True(
                (liningUp.Ground.SpeedLimit is { } limit) && (limit == 0),
                $"sub-tick {k}: the lining-up aircraft must stay pinned behind the LUAW occupant 80 ft ahead, "
                    + $"got limit={liningUp.Ground.SpeedLimit?.ToString("F1") ?? "none"} gs={liningUp.GroundSpeed:F2}"
            );

            FlightPhysics.Update(liningUp, 0.25, null, null, simTimeSeconds: k * 0.25);
        }

        Assert.Equal(startPosition.Lat, liningUp.Position.Lat, 12);
        Assert.Equal(startPosition.Lon, liningUp.Position.Lon, 12);
    }

    [Fact]
    public void LiningUp_WhileStopped_RemainsPassableObstacle()
    {
        // A stationary aircraft in LineUpPhase (e.g. braked at the hold-short bar waiting
        // for its lineup path) is still a passable obstacle: a mover with adequate lateral
        // clearance behind it must not be hard-stopped by mere proximity.
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 90, gs: 15);
        AircraftState b = MakeAircraft("B", new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 0, phase: new LineUpPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // B is north of east-moving A (not in path, diff >= 90): no limits either way.
        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(b.Ground.SpeedLimit);
    }

    [Fact]
    public void StationaryNearMoving_NotInPath_NoLimit()
    {
        // A is moving east, B is stationary to the north (not in A's path)
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 90, gs: 15);
        AircraftState b = MakeAircraft("B", new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 0, phase: new AtParkingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // A is heading east but B is north — bearing to B (~0°) vs heading (90°) = 90° diff
        // Not closing (diff >= 90), so no limit
        Assert.Null(a.Ground.SpeedLimit);
    }

    [Fact]
    public void PushingTowardOther_PushbackYields()
    {
        // A is pushing back south (pushbackHeading=180), B is south of A (in pushback path, 150ft)
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);
        a.Phases = new PhaseList();
        a.Phases.Add(StraightPushFrom(a));
        a.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        AircraftState b = MakeAircraft("B", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // Pushing aircraft closing on B should yield (stop)
        Assert.NotNull(a.Ground.SpeedLimit);
        Assert.Equal(0.0, a.Ground.SpeedLimit.Value);
    }

    [Fact]
    public void ForcedTowTowardMovingTraffic_StillYields()
    {
        // A forced tow (PUSHF) ignores parked aircraft only: a taxiing aircraft in its path still stops it.
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);
        a.Phases = new PhaseList();
        a.Phases.Add(StraightPushFrom(a));
        a.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        a.Ground.ForcedTowIgnoresParked = true;

        AircraftState b = MakeAircraft("B", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);

        GroundConflictDetector.ApplySpeedLimits([a, b], null);

        Assert.Equal(0.0, a.Ground.SpeedLimit);
    }

    [Fact]
    public void ForcedTowAgainstRunwayCrosser_StillYieldsAndShowsWhy()
    {
        // The give-way arbitration is unchanged for a forced tow: a runway crosser is never held for it.
        AircraftState pusher = MakePusherFromStand(StandNorthOf(190), new LatLon(BaseLat + (1.9 * OffsetLatPer100Ft), BaseLon));
        pusher.Ground.ForcedTowIgnoresParked = true;
        AircraftState mover = MakeTaxiingE75L(new LatLon(BaseLat, BaseLon), heading: 0);
        mover.Phases = new PhaseList();
        mover.Phases.Add(new CrossingRunwayPhase(approachNodeId: 0, targetNodeId: 1, runwayId: "28L"));
        mover.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        GroundConflictDetector.ApplySpeedLimits([pusher, mover], null);

        Assert.Null(mover.Ground.AutoYieldTarget);
        Assert.Equal(0.0, pusher.Ground.SpeedLimit);
        Assert.Equal("ARR", pusher.Ground.AutoYieldTarget);
    }

    [Fact]
    public void ForcedTowTowardParkedNeighbor_TooClose_IsNotStopped()
    {
        // The same geometry PushingTowardParkedNeighbor_TooClose_StillStops pins at zero: a forced tow is not braked for
        // a parked aircraft.
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat + 1.2 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);
        a.Phases = new PhaseList();
        a.Phases.Add(StraightPushFrom(a));
        a.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        a.Ground.ForcedTowIgnoresParked = true;

        AircraftState b = MakeAircraft("B", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, phase: new AtParkingPhase());

        GroundConflictDetector.ApplySpeedLimits([a, b], null);

        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(a.Ground.AutoYieldTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForcedTowTowardHeldAircraft_TooClose_StillStops(bool holdingInPosition)
    {
        // A forced tow does not stop for an aircraft at a stand or resting after a push, but one stopped on the pavement
        // under a controller HOLD, or holding in position, is not parked: the geometry PushingTowardParkedNeighbor_TooClose_StillStops
        // pins at zero still stops it.
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat + 1.2 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);
        a.Phases = new PhaseList();
        a.Phases.Add(StraightPushFrom(a));
        a.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        a.Ground.ForcedTowIgnoresParked = true;

        AircraftState b = holdingInPosition
            ? MakeAircraft("B", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, phase: new HoldingInPositionPhase())
            : MakeAircraft("B", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0);
        if (!holdingInPosition)
        {
            b.Ground.Hold = HoldDirective.HoldPosition;
        }

        GroundConflictDetector.ApplySpeedLimits([a, b], null);

        Assert.Equal(0.0, a.Ground.SpeedLimit);
    }

    [Fact]
    public void ForcedTowTowardAircraftAtAStandUnderHold_TooClose_IsNotStopped()
    {
        // An aircraft at a stand is parked whether or not a HOLD is on it: a forced tow is not braked for it.
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat + 1.2 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);
        a.Phases = new PhaseList();
        a.Phases.Add(StraightPushFrom(a));
        a.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        a.Ground.ForcedTowIgnoresParked = true;

        AircraftState b = MakeAircraft("B", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, phase: new AtParkingPhase());
        b.Ground.Hold = HoldDirective.HoldPosition;

        GroundConflictDetector.ApplySpeedLimits([a, b], null);

        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(a.Ground.AutoYieldTarget);
    }

    [Fact]
    public void PushingAwayFromOther_NoYield()
    {
        // A is pushing back south (pushbackHeading=180), B is north of A (not in pushback path)
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);
        a.Phases = new PhaseList();
        a.Phases.Add(StraightPushFrom(a));
        a.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        AircraftState b = MakeAircraft("B", new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 0, phase: new AtParkingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // A is pushing away from B, no yield needed
        Assert.Null(a.Ground.SpeedLimit);
    }

    [Fact]
    public void PushingTowardParkedNeighbor_DoesNotHardStop()
    {
        // Issue #222: A pushes back south toward B, which is PARKED at an adjacent
        // gate ~180 ft ahead — beyond collision distance (stopDist ~154 ft for the
        // B738 pair) but inside the 200 ft pushback buffer. A pushback must not be
        // pinned to 0 by a parked neighbor; it may creep at its pushback speed.
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat + 1.8 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);
        a.Phases = new PhaseList();
        a.Phases.Add(StraightPushFrom(a));
        a.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        AircraftState b = MakeAircraft("B", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, phase: new AtParkingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // Not pinned to 0 — the pushback can still creep (>= slow-taxi speed) past
        // the parked aircraft rather than deadlocking until the controller issues BREAK.
        Assert.True(
            a.Ground.SpeedLimit is null || a.Ground.SpeedLimit > 0,
            $"Pushback should not be hard-stopped by a parked neighbor, but SpeedLimit={a.Ground.SpeedLimit}"
        );
    }

    [Fact]
    public void PushingTowardParkedNeighbor_TooClose_StillStops()
    {
        // Safety floor: when the parked neighbor is within actual collision distance
        // (~120 ft < stopDist ~154 ft for the B738 pair, dead ahead), the pushback
        // still hard-stops so it does not back into the parked aircraft.
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat + 1.2 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);
        a.Phases = new PhaseList();
        a.Phases.Add(StraightPushFrom(a));
        a.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        AircraftState b = MakeAircraft("B", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, phase: new AtParkingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.NotNull(a.Ground.SpeedLimit);
        Assert.Equal(0.0, a.Ground.SpeedLimit!.Value);
    }

    /// <summary>
    /// A B738 pushed at the full tow speed straight back toward a B738 parked dead astern, from every distance between
    /// 140 and 220 ft in 1 ft steps: wherever the outline stop limits the push, the limit asks the tug to shed no more
    /// than the towbar rate (<see cref="CategoryPerformance.TugDecelRate"/>) over one detector interval — physics
    /// clamps the speed to the limit at once, so a lower limit would brake the tow harder than a towbar can. At least
    /// one distance must put the stop inside the tug's braking distance, so the check cannot pass on no limit at all.
    /// </summary>
    [Fact]
    public void TugMoveTowardParkedNeighbour_NeverAskedToBrakeHarderThanTheTowbarRate()
    {
        double speedKts = CategoryPerformance.PushbackSpeed(AircraftCategory.Jet);
        double floorKts = speedKts - (CategoryPerformance.TugDecelRate(AircraftCategory.Jet) / SimulationEngine.PhysicsSubTickRate);
        int braked = 0;
        for (int distanceFt = 140; distanceFt <= 220; distanceFt++)
        {
            LatLon pusherPosition = new(BaseLat + ((distanceFt / 100.0) * OffsetLatPer100Ft), BaseLon);
            AircraftState a = MakeAircraft("A", pusherPosition, heading: 0, gs: speedKts, pushbackHeading: 180);
            a.Phases = new PhaseList();
            a.Phases.Add(StraightPushFrom(a));
            a.Phases.CurrentPhase!.Status = PhaseStatus.Active;
            AircraftState b = MakeAircraft("B", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, phase: new AtParkingPhase());
            var diagnostics = new List<string>();

            GroundConflictDetector.ApplySpeedLimits([a, b], null, 1.0 / SimulationEngine.PhysicsSubTickRate, diagnostics.Add);

            if (a.Ground.SpeedLimit is not { } limitKts)
            {
                continue;
            }

            braked += limitKts < speedKts ? 1 : 0;
            Assert.True(
                limitKts >= floorKts - 1e-9,
                $"{distanceFt} ft astern: a tow at {speedKts:F2} kt was limited to {limitKts:F2} kt, under the {floorKts:F2} kt the towbar "
                    + $"can brake to in one detector interval:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}"
            );
        }

        Assert.True(braked > 0, "no distance put the parked aircraft inside the tug's braking distance, so nothing was checked");
    }

    /// <summary>
    /// A B738 mid-push off a stand, tail-first to the south. The phase is started while the aircraft is at
    /// <paramref name="standPosition"/> — what <see cref="PushbackPhase.HasRampPriority"/> measures from — and
    /// the aircraft is then placed where the push has got to, so passing the same point for both leaves it
    /// still on its stand.
    /// </summary>
    private static AircraftState MakePusherFromStand(LatLon standPosition, LatLon position) =>
        MakePusher(standPosition, position, startsAtStand: true, continuesStandPushOff: false);

    /// <summary>
    /// The same straight push with the two priority flags set explicitly: the stand push-off
    /// (<paramref name="startsAtStand"/>), a move flown through from it (<paramref name="continuesStandPushOff"/>),
    /// or — with both false — a move the plan reversed into, which is a repositioning tow.
    /// </summary>
    private static AircraftState MakePusher(LatLon standPosition, LatLon position, bool startsAtStand, bool continuesStandPushOff)
    {
        AircraftState pusher = MakeAircraft("PSH", standPosition, heading: 0, gs: 3, pushbackHeading: 180);
        pusher.Phases = new PhaseList();
        pusher.Phases.Add(
            new PushbackPhase
            {
                Move = TugMove.Straight(PushbackLegKind.Push, TugMovePlanner.SimplePushbackFt(pusher.AircraftType)),
                PlannedEnd = GeoMath.ProjectPoint(
                    standPosition,
                    new TrueHeading(180),
                    CategoryPerformance.SimplePushbackDistanceNm(pusher.AircraftType)
                ),
                StartsAtStand = startsAtStand,
                ContinuesIntoNextMove = false,
                ContinuesStandPushOff = continuesStandPushOff,
            }
        );
        pusher.Phases.Start(CommandDispatcher.BuildMinimalContext(pusher));
        pusher.Position = position;
        return pusher;
    }

    /// <summary>
    /// A B738 mid-push whose remaining leg ends at <paramref name="target"/>, placed at
    /// <paramref name="position"/> with the tail currently tracking <paramref name="pushHeading"/> — the two
    /// differ while the tug is steering the pursuit arc. The phase is started at
    /// <paramref name="standPosition"/> (what <see cref="PushbackPhase.HasRampPriority"/> measures from) with
    /// the nose pointed away from the target, so it is aligned and reversing from the first tick.
    /// </summary>
    private static AircraftState MakePusherToTarget(LatLon standPosition, LatLon position, LatLon target, double pushHeading)
    {
        double noseAtStand = new TrueHeading(GeoMath.BearingTo(standPosition, target)).ToReciprocal().Degrees;
        AircraftState pusher = MakeAircraft("PSH", standPosition, heading: noseAtStand, gs: 3, pushbackHeading: pushHeading);
        pusher.Phases = new PhaseList();
        pusher.Phases.Add(
            new PushbackPhase
            {
                Move = TugMove.ToPoint(PushbackLegKind.Push, target),
                PlannedEnd = target,
                StartsAtStand = true,
                ContinuesIntoNextMove = false,
                ContinuesStandPushOff = false,
            }
        );
        pusher.Phases.Start(CommandDispatcher.BuildMinimalContext(pusher));
        pusher.Position = position;
        pusher.Ground.PushbackTrueHeading = new TrueHeading(pushHeading);
        return pusher;
    }

    /// <summary>
    /// A not-yet-started straight push of the simple pushback distance, planned to end that far along the aircraft's
    /// push heading (the nose's reciprocal when none is set).
    /// </summary>
    private static PushbackPhase StraightPushFrom(AircraftState aircraft)
    {
        TrueHeading pushHeading = aircraft.Ground.PushbackTrueHeading ?? aircraft.TrueHeading.ToReciprocal();
        return new PushbackPhase
        {
            Move = TugMove.Straight(PushbackLegKind.Push, TugMovePlanner.SimplePushbackFt(aircraft.AircraftType)),
            PlannedEnd = GeoMath.ProjectPoint(aircraft.Position, pushHeading, CategoryPerformance.SimplePushbackDistanceNm(aircraft.AircraftType)),
            StartsAtStand = true,
            ContinuesIntoNextMove = false,
            ContinuesStandPushOff = false,
        };
    }

    /// <summary>An E75L taxiing at 8 kt on <paramref name="heading"/>, with no route and no phase.</summary>
    private static AircraftState MakeTaxiingE75L(LatLon position, double heading) =>
        new()
        {
            Callsign = "ARR",
            AircraftType = "E75L",
            Position = position,
            TrueHeading = new TrueHeading(heading),
            IsOnGround = true,
            IndicatedAirspeed = 8,
        };

    /// <summary>The stand 200 ft north of the alley position a pusher is measured at — well past half a B738.</summary>
    private static LatLon StandNorthOf(double alleyNorthFt) => new(BaseLat + ((alleyNorthFt + 200.0) / 100.0 * OffsetLatPer100Ft), BaseLon);

    [Fact]
    public void MoverVsPusher_MutualStop_MoverGivesWay_PusherContinues()
    {
        // A pushback whose tail is already out in the alley owns it: the taxiing aircraft gives way and the
        // pusher completes into its spot. Resolving the two sides independently stopped both forever — the
        // pusher pinned for the mover ahead of its push direction, and the mover trailed the stopped pusher.
        AircraftState pusher = MakePusherFromStand(StandNorthOf(190), new LatLon(BaseLat + (1.9 * OffsetLatPer100Ft), BaseLon));

        // Head-on along the push axis ~190 ft out: no lateral room for either to pass.
        AircraftState mover = MakeTaxiingE75L(new LatLon(BaseLat, BaseLon), heading: 0);

        var aircraft = new List<AircraftState> { pusher, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.NotNull(mover.Ground.SpeedLimit);
        Assert.Equal(0.0, mover.Ground.SpeedLimit!.Value);

        // The operator sees who it is waiting for, and DeadlockGuard sees an intentional yield.
        Assert.Equal("PSH", mover.Ground.AutoYieldTarget);
        Assert.False(mover.Ground.AutoYieldIsFollowing);

        // The pusher keeps its priority: it may carry a graduated closing limit but is never
        // pinned to zero at this range, so it clears the alley instead of deadlocking.
        Assert.True(
            (pusher.Ground.SpeedLimit is null) || (pusher.Ground.SpeedLimit > 0),
            $"Pushback in progress must keep going, but SpeedLimit={pusher.Ground.SpeedLimit}"
        );
        Assert.Null(pusher.Ground.AutoYieldTarget);
    }

    [Fact]
    public void MoverVsPusher_PushAfterAReversal_MoverKeepsGoing_PusherYields()
    {
        // Same geometry as the mutual stop, but the push is a leg the plan reversed into — the second leg of a
        // PUSHM, or the push half of a three-point turn. That tow is repositioning, not committing an alley off a
        // stand, so it is ordinary ramp traffic: the mover keeps going and the pusher takes the hard stop.
        AircraftState pusher = MakePusher(
            StandNorthOf(190),
            new LatLon(BaseLat + (1.9 * OffsetLatPer100Ft), BaseLon),
            startsAtStand: false,
            continuesStandPushOff: false
        );
        AircraftState mover = MakeTaxiingE75L(new LatLon(BaseLat, BaseLon), heading: 0);

        var aircraft = new List<AircraftState> { pusher, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.True(
            (mover.Ground.SpeedLimit is null) || (mover.Ground.SpeedLimit > 0),
            $"the mover was held for a repositioning tow, SpeedLimit={mover.Ground.SpeedLimit}"
        );
        Assert.NotEqual("PSH", mover.Ground.AutoYieldTarget);
        Assert.NotNull(pusher.Ground.SpeedLimit);
        Assert.Equal(0.0, pusher.Ground.SpeedLimit!.Value);
    }

    [Fact]
    public void MoverVsPusher_ContinuationPush_KeepsPriority()
    {
        // The move after the push-off, flown through from it with no reversal between, is still leg 1 of the push
        // off the stand: the tail is out in the alley and the tug crew cannot see behind it, so the taxiing
        // aircraft gives way exactly as it does for the push-off itself.
        AircraftState pusher = MakePusher(
            StandNorthOf(190),
            new LatLon(BaseLat + (1.9 * OffsetLatPer100Ft), BaseLon),
            startsAtStand: false,
            continuesStandPushOff: true
        );
        AircraftState mover = MakeTaxiingE75L(new LatLon(BaseLat, BaseLon), heading: 0);

        var aircraft = new List<AircraftState> { pusher, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.NotNull(mover.Ground.SpeedLimit);
        Assert.Equal(0.0, mover.Ground.SpeedLimit!.Value);
        Assert.Equal("PSH", mover.Ground.AutoYieldTarget);
        Assert.False(mover.Ground.AutoYieldIsFollowing);
        Assert.True(
            (pusher.Ground.SpeedLimit is null) || (pusher.Ground.SpeedLimit > 0),
            $"a continuation of the push-off must keep going, but SpeedLimit={pusher.Ground.SpeedLimit}"
        );
        Assert.Null(pusher.Ground.AutoYieldTarget);
    }

    [Fact]
    public void MoverVsPusher_MoverNotInPushPath_PusherUnaffected_MoverTrails()
    {
        // The mover sits behind the push direction (the tail is swinging away from it), so the
        // pusher owes it nothing and the give-way rule does not engage — the mover just trails.
        AircraftState pusher = MakePusherFromStand(StandNorthOf(0), new LatLon(BaseLat, BaseLon));
        AircraftState mover = MakeTaxiingE75L(new LatLon(BaseLat + (1.9 * OffsetLatPer100Ft), BaseLon), heading: 180);

        var aircraft = new List<AircraftState> { pusher, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Null(pusher.Ground.SpeedLimit);
        Assert.NotNull(mover.Ground.SpeedLimit);
        Assert.Equal(pusher.GroundSpeed, mover.Ground.SpeedLimit!.Value);
        Assert.Null(mover.Ground.AutoYieldTarget);
    }

    [Fact]
    public void MoverCrossingRunway_NotHeldForPushback_PusherYieldsAndShowsWhy()
    {
        // An aircraft crossing a runway is never held for a ramp push — it has to get the whole aircraft
        // past the hold line first (AIM 4-3-21.b) — so the pusher takes the stop, annotated with who it is
        // waiting for so a stalled PUSH is readable.
        AircraftState pusher = MakePusherFromStand(StandNorthOf(190), new LatLon(BaseLat + (1.9 * OffsetLatPer100Ft), BaseLon));
        AircraftState mover = MakeTaxiingE75L(new LatLon(BaseLat, BaseLon), heading: 0);
        mover.Phases = new PhaseList();
        mover.Phases.Add(new CrossingRunwayPhase(approachNodeId: 0, targetNodeId: 1, runwayId: "28L"));
        mover.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        var aircraft = new List<AircraftState> { pusher, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Null(mover.Ground.AutoYieldTarget);
        Assert.NotNull(pusher.Ground.SpeedLimit);
        Assert.Equal(0.0, pusher.Ground.SpeedLimit!.Value);
        Assert.Equal("ARR", pusher.Ground.AutoYieldTarget);
    }

    [Fact]
    public void ShadowMover_NotHeldForPushback_PusherKeepsHardStop()
    {
        // A live-traffic shadow is not ours to hold: nothing we write to it moves it, so the give-way rule
        // must not pick it as the holder. The pusher keeps its hard stop and the shadow is left untouched.
        AircraftState pusher = MakePusherFromStand(StandNorthOf(190), new LatLon(BaseLat + (1.9 * OffsetLatPer100Ft), BaseLon));
        AircraftState shadow = MakeTaxiingE75L(new LatLon(BaseLat, BaseLon), heading: 0);
        shadow.LiveTraffic = new AircraftLiveTraffic();

        var aircraft = new List<AircraftState> { pusher, shadow };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Null(shadow.Ground.SpeedLimit);
        Assert.Null(shadow.Ground.AutoYieldTarget);
        Assert.NotNull(pusher.Ground.SpeedLimit);
        Assert.Equal(0.0, pusher.Ground.SpeedLimit!.Value);
    }

    [Fact]
    public void PusherStillOnStand_NoPriority_PusherYieldsAndMoverTrails()
    {
        // Priority belongs to a push whose tail is already in the lane. One that has not moved off its stand
        // can wait for the traffic to go by — "hold your push, traffic in the alley" — so today's yield
        // applies unchanged.
        var stand = new LatLon(BaseLat + (1.9 * OffsetLatPer100Ft), BaseLon);
        AircraftState pusher = MakePusherFromStand(stand, stand);
        AircraftState mover = MakeTaxiingE75L(new LatLon(BaseLat, BaseLon), heading: 0);

        var aircraft = new List<AircraftState> { pusher, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.NotNull(pusher.Ground.SpeedLimit);
        Assert.Equal(0.0, pusher.Ground.SpeedLimit!.Value);
        Assert.Null(mover.Ground.AutoYieldTarget);
        Assert.NotEqual(0.0, mover.Ground.SpeedLimit!.Value);
    }

    [Fact]
    public void GiveWayHolder_HeadOnInsideStopDistance_BothStop_TheDocumentedWedge()
    {
        // The residual the graduated closing logic leaves: 120 ft apart, dead ahead of the push with no
        // lateral room, the holder is pinned by the give-way and the pusher by its own proximity stop. Both
        // sit at zero until the controller BREAKs one of them — documented on PushbackYieldForTraffic rather
        // than papered over, because no automatic resolution is safe this close.
        AircraftState pusher = MakePusherFromStand(StandNorthOf(120), new LatLon(BaseLat + (1.2 * OffsetLatPer100Ft), BaseLon));
        AircraftState mover = MakeTaxiingE75L(new LatLon(BaseLat, BaseLon), heading: 0);

        var aircraft = new List<AircraftState> { pusher, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Equal(0.0, mover.Ground.SpeedLimit!.Value);
        Assert.Equal("PSH", mover.Ground.AutoYieldTarget);
        Assert.Equal(0.0, pusher.Ground.SpeedLimit!.Value);
    }

    [Fact]
    public void GiveWayHolder_PushPathDiverges_PusherContinues()
    {
        // The holder sits 142 ft dead ahead of the tail's current track — inside the pusher's ~143 ft stop
        // ring — so the give-way pins it. The rest of the push runs east across the alley, though, and never
        // comes closer to the holder than it already is. Stopping the pusher there would wedge it against the
        // very aircraft holding for it, with neither moving again until the controller BREAKs one.
        AircraftState pusher = MakePusherToTarget(
            StandNorthOf(142),
            new LatLon(BaseLat + (1.42 * OffsetLatPer100Ft), BaseLon),
            new LatLon(BaseLat + (1.42 * OffsetLatPer100Ft), BaseLon + (2.0 * OffsetLonPer100Ft)),
            pushHeading: 180
        );
        AircraftState mover = MakeTaxiingE75L(new LatLon(BaseLat, BaseLon), heading: 0);

        var aircraft = new List<AircraftState> { pusher, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Equal(0.0, mover.Ground.SpeedLimit!.Value);
        Assert.Equal("PSH", mover.Ground.AutoYieldTarget);
        Assert.False(mover.Ground.AutoYieldIsFollowing);
        Assert.True(
            (pusher.Ground.SpeedLimit is null) || (pusher.Ground.SpeedLimit > 0),
            $"the remaining push leg clears the holder by more than two half-spans, but SpeedLimit={pusher.Ground.SpeedLimit}"
        );
    }

    [Fact]
    public void GiveWayHolder_PushPathTowardMover_PusherStops()
    {
        // The same pair, with the push leg ending on the holder instead of alongside it: the remaining track
        // drives into the aircraft that is waiting for it, so the pusher keeps its proximity stop. Both sit at
        // zero until the controller BREAKs one of them — the wedge documented on PushbackYieldForTraffic,
        // because no automatic resolution is safe when the push is aimed at the other aircraft.
        AircraftState pusher = MakePusherToTarget(
            StandNorthOf(142),
            new LatLon(BaseLat + (1.42 * OffsetLatPer100Ft), BaseLon),
            new LatLon(BaseLat, BaseLon),
            pushHeading: 180
        );
        AircraftState mover = MakeTaxiingE75L(new LatLon(BaseLat, BaseLon), heading: 0);

        var aircraft = new List<AircraftState> { pusher, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Equal(0.0, mover.Ground.SpeedLimit!.Value);
        Assert.Equal("PSH", mover.Ground.AutoYieldTarget);
        Assert.Equal(0.0, pusher.Ground.SpeedLimit!.Value);
    }

    /// <summary>
    /// A B738 on the last stretch of a creep move — the pull onto a spot <paramref name="remainingFt"/> ahead, inside
    /// the pull-forward distance where the tug is commanded down to the alignment creep.
    /// </summary>
    private static AircraftState MakeCreepingPuller(LatLon position, double remainingFt)
    {
        TugMove move = TugMove.Straight(PushbackLegKind.Pull, remainingFt) with { Creep = true };
        var phase = new PushbackPhase
        {
            Move = move,
            PlannedEnd = GeoMath.ProjectPoint(position, new TrueHeading(0), remainingFt / FtPerNm),
            ContinuesIntoNextMove = false,
            ContinuesStandPushOff = false,
        };
        return MakeAircraft("TUG", position, heading: 0, gs: 2, phase: phase);
    }

    [Fact]
    public void CreepingTugMove_LimitAboveTheCommandedCreep_ShowsNoYieldTarget()
    {
        AircraftState parked = MakeAircraft("PRK", new LatLon(BaseLat, BaseLon + OffsetLonPer100Ft), heading: 0, gs: 0, phase: new AtParkingPhase());
        AircraftState mover = MakeCreepingPuller(new LatLon(BaseLat, BaseLon), remainingFt: 20);
        var tugMove = (PushbackPhase)mover.Phases!.CurrentPhase!;
        Assert.Equal(CategoryPerformance.PushbackAlignSpeed(AircraftCategory.Jet), tugMove.CommandedSpeedKts(mover));

        // 4 kt is above the 3 kt this stretch is commanded at, so the limit takes nothing off the tow: naming a yield
        // target there points the operator at a neighbour the move is not slowing for.
        GroundConflictDetector.ShowTugMoveYield(mover, parked, limitKts: 4);

        Assert.Null(mover.Ground.AutoYieldTarget);
    }

    [Fact]
    public void CreepingTugMove_LimitBelowTheCommandedCreep_ShowsTheNeighbour()
    {
        AircraftState parked = MakeAircraft("PRK", new LatLon(BaseLat, BaseLon + OffsetLonPer100Ft), heading: 0, gs: 0, phase: new AtParkingPhase());
        AircraftState mover = MakeCreepingPuller(new LatLon(BaseLat, BaseLon), remainingFt: 20);

        // 2 kt is under the commanded creep: the tow is being braked for the neighbour, and the operator sees who for.
        GroundConflictDetector.ShowTugMoveYield(mover, parked, limitKts: 2);

        Assert.Equal("PRK", mover.Ground.AutoYieldTarget);
        Assert.False(mover.Ground.AutoYieldIsFollowing);
    }

    /// <summary>
    /// A B738 towed at the 5 kt push pace, <paramref name="remainingFt"/> short of the move's end — outside a creep, so
    /// the tug is commanded the full pace.
    /// </summary>
    private static AircraftState MakeTowedPuller(LatLon position, double remainingFt)
    {
        var phase = new PushbackPhase
        {
            Move = TugMove.Straight(PushbackLegKind.Pull, remainingFt),
            PlannedEnd = GeoMath.ProjectPoint(position, new TrueHeading(0), remainingFt / FtPerNm),
            ContinuesIntoNextMove = false,
            ContinuesStandPushOff = false,
        };
        return MakeAircraft("TUG", position, heading: 0, gs: 2, phase: phase);
    }

    [Fact]
    public void TugMoveMidTurn_LimitAboveTheGearSpeed_ShowsNoYieldTarget()
    {
        AircraftState parked = MakeAircraft("PRK", new LatLon(BaseLat, BaseLon + OffsetLonPer100Ft), heading: 0, gs: 0, phase: new AtParkingPhase());
        AircraftState mover = MakeTowedPuller(new LatLon(BaseLat, BaseLon), remainingFt: 200);
        mover.Ground.TowbarTrueHeading = new TrueHeading(mover.TrueHeading.Degrees + 60.0);
        var tugMove = (PushbackPhase)mover.Phases!.CurrentPhase!;
        Assert.Equal(CategoryPerformance.PushbackSpeed(AircraftCategory.Jet), tugMove.CommandedSpeedKts(mover));
        Assert.Equal(2.5, tugMove.CommandedGearSpeedKts(mover), 6);

        // The towbar 60° off the nose puts the main gear at 5 kt × cos 60° = 2.5 kt: a 3 kt limit is above it and takes
        // nothing off the tow, though it is under the tug's own pace.
        GroundConflictDetector.ShowTugMoveYield(mover, parked, limitKts: 3);

        Assert.Null(mover.Ground.AutoYieldTarget);
    }

    [Fact]
    public void TugMoveStraightAhead_LimitBelowThePace_ShowsTheNeighbour()
    {
        AircraftState parked = MakeAircraft("PRK", new LatLon(BaseLat, BaseLon + OffsetLonPer100Ft), heading: 0, gs: 0, phase: new AtParkingPhase());
        AircraftState mover = MakeTowedPuller(new LatLon(BaseLat, BaseLon), remainingFt: 200);
        Assert.Null(mover.Ground.TowbarTrueHeading);
        var tugMove = (PushbackPhase)mover.Phases!.CurrentPhase!;
        Assert.Equal(CategoryPerformance.PushbackSpeed(AircraftCategory.Jet), tugMove.CommandedGearSpeedKts(mover));

        // No towbar direction yet: the gear runs at the tug's 5 kt, so the same 3 kt limit is braking the tow.
        GroundConflictDetector.ShowTugMoveYield(mover, parked, limitKts: 3);

        Assert.Equal("PRK", mover.Ground.AutoYieldTarget);
        Assert.False(mover.Ground.AutoYieldIsFollowing);
    }

    [Fact]
    public void TwoMoving_HeadOn_NoLayout_BothStop()
    {
        // A heading north, B heading south, 250ft apart, both moving
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);
        AircraftState b = MakeAircraft("B", new LatLon(BaseLat + 2.5 * OffsetLatPer100Ft, BaseLon), heading: 180, gs: 15);

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // Head-on within 300ft: both should stop
        Assert.NotNull(a.Ground.SpeedLimit);
        Assert.Equal(0.0, a.Ground.SpeedLimit.Value);
        Assert.NotNull(b.Ground.SpeedLimit);
        Assert.Equal(0.0, b.Ground.SpeedLimit.Value);
    }

    [Fact]
    public void FollowingAircraft_ExemptFromConflictLimits()
    {
        // A is following B, both close together
        var followPhase = new FollowingPhase("B");
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15, phase: followPhase);
        AircraftState b = MakeAircraft("B", new LatLon(BaseLat + 0.5 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 10);

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // A is following — should NOT get a speed limit from the detector
        Assert.Null(a.Ground.SpeedLimit);
    }

    private const string LeadCallsign = "N1LED";
    private const string FollowerCallsign = "N2FOL";
    private const string SiblingCallsign = "N3FOL";
    private const string ThirdCallsign = "N4TRD";

    /// <summary>
    /// A <c>FOLLOWG</c> follower rolling on its follow route converges with an unrelated aircraft taxiing in from a side edge onto
    /// a junction ahead of it: the detector resolves the pair exactly as it does when the follower is a plain taxiing aircraft on
    /// the same route, so the follower, farther from the shared node, yields to the third aircraft.
    /// </summary>
    [Fact]
    public void Follower_WithAThirdAircraftConverging_YieldsLikeATaxiingOne()
    {
        if (StartFollowAtJunction() is not { } at)
        {
            return;
        }

        AircraftState third = ThirdAtTheSide(at, speedKts: 2.0);
        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([at.Follower, third], at.Run.Layout, 0, log.Add);
        log.ForEach(_output.WriteLine);
        (double?, string?, double?, string?) asFollower = YieldOutcome(at.Follower, third);

        at.Follower.Phases = null;
        at.Follower.Ground.AssignedTaxiRoute = at.Follow.FollowRoute;
        GroundConflictDetector.ApplySpeedLimits([at.Follower, third], at.Run.Layout);
        (double? FollowerLimit, string? FollowerYieldsTo, double?, string?) asTaxiing = YieldOutcome(at.Follower, third);
        _output.WriteLine($"as a follower {asFollower}, as a taxiing aircraft {asTaxiing}");

        Assert.NotNull(asTaxiing.FollowerLimit);
        Assert.Equal(ThirdCallsign, asTaxiing.FollowerYieldsTo);
        Assert.Equal(asTaxiing, asFollower);
    }

    /// <summary>
    /// A follower rolling behind its own lead on KOAK's B, within the detector's search range of it, is never limited against it,
    /// nor the lead against the follower: the pair is not resolved at all, since the follow keeps its own gap to its lead.
    /// </summary>
    [Fact]
    public void Follower_VsItsOwnLead_GetsNoLimit()
    {
        if (StartFollower(string.Empty) is not { } started)
        {
            return;
        }

        (KoakFollowGeometry.LeadRun run, AircraftState follower, FollowingPhase _) = started;
        for (int second = 1; second <= 8; second++)
        {
            run.Engine.TickOneSecond();
            _output.WriteLine(
                $"t={second} follower gs={follower.GroundSpeed:F1} limit={follower.Ground.SpeedLimit} lead gs={run.Lead.GroundSpeed:F1} "
                    + $"limit={run.Lead.Ground.SpeedLimit}"
            );
            Assert.Null(follower.Ground.SpeedLimit);
            Assert.Null(follower.Ground.AutoYieldTarget);
            Assert.Null(run.Lead.Ground.SpeedLimit);
        }

        Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.True(
            GeoMath.DistanceNm(follower.Position, run.Lead.Position) <= GroundConflictDetector.SearchRangeNm,
            "the follower is out of the detector's search range of its lead, so the pair proves nothing"
        );
        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([run.Lead, follower], run.Layout, 0, log.Add);
        log.ForEach(_output.WriteLine);
        Assert.DoesNotContain(log, line => line.StartsWith("[Pair]", StringComparison.Ordinal));
    }

    /// <summary>
    /// A follower of a follower rolling toward the first follower's own lead on KOAK's B, within the detector's search range of it:
    /// that lead is in its lead chain, so the detector resolves neither it nor its direct lead against the second follower.
    /// </summary>
    [Fact]
    public void Follower_VsItsLeadsLead_GetsNoLimit()
    {
        if (StartFollower(string.Empty) is not { } started)
        {
            return;
        }

        (KoakFollowGeometry.LeadRun run, AircraftState follower, FollowingPhase _) = started;
        AircraftState last = KoakFollowGeometry.Spawn(
            SiblingCallsign,
            "C172",
            run.Chain[6].Position,
            KoakFollowGeometry.Facing(run.Chain[6], run.Chain[5])
        );
        last.Ground.Layout = run.Layout;
        last.Phases = new PhaseList();
        last.Phases.Add(new FollowingPhase(FollowerCallsign));
        last.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        last.IndicatedAirspeed = 10.0;
        Assert.True(
            GeoMath.DistanceNm(last.Position, run.Lead.Position) <= GroundConflictDetector.SearchRangeNm,
            "the last follower is out of the detector's search range of its lead's lead, so the pair proves nothing"
        );

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([run.Lead, follower, last], run.Layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        Assert.DoesNotContain(log, line => line.StartsWith("[Pair]", StringComparison.Ordinal));
        Assert.Null(last.Ground.SpeedLimit);
        Assert.Null(run.Lead.Ground.SpeedLimit);
    }

    /// <summary>
    /// Two followers of one lead, the second joining from a side edge at a junction ahead of the first: the two followers are an
    /// ordinary pair the detector resolves against each other, while neither is resolved against the lead.
    /// </summary>
    [Fact]
    public void Follower_VsItsLeadsOtherFollower_IsResolved()
    {
        if (StartFollowAtJunction() is not { } at)
        {
            return;
        }

        AircraftState sibling = KoakFollowGeometry.Spawn(SiblingCallsign, "C172", at.SidePoint, KoakFollowGeometry.Facing(at.SideFrom, at.Junction));
        sibling.Ground.Layout = at.Run.Layout;
        at.Run.Engine.World.AddAircraft(sibling);
        CommandResult result = at.Run.Engine.SendCommand(SiblingCallsign, $"FOLLOWG {at.Run.Lead.Callsign}");
        Assert.True(result.Success, result.Message);
        at.Run.Engine.TickOneSecond();
        FollowingPhase siblingFollow = Assert.IsType<FollowingPhase>(sibling.Phases?.CurrentPhase);
        Assert.True(siblingFollow.FollowRoute is not null, "the second follower planned no follow route, so it is no mover to resolve");

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([at.Run.Lead, at.Follower, sibling], at.Run.Layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        Assert.Contains(log, line => line.StartsWith($"[Pair] {FollowerCallsign}(Taxiing)+{SiblingCallsign}(Taxiing)", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.StartsWith($"[Pair] {at.Run.Lead.Callsign}(", StringComparison.Ordinal));
    }

    /// <summary>
    /// A follower with no route to drive (waiting for its lead) classifies as any aircraft with no route does: stationary while at
    /// rest, a passable obstacle, and an untracked mover on its heading once rolling.
    /// </summary>
    [Fact]
    public void Follower_WithNoDrivenRoute_IsAnObstacleAtRestAndAnUntrackedMoverRolling()
    {
        if (KoakFollowGeometry.LoadLayout(_output) is not { } layout)
        {
            return;
        }

        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        AircraftState waiting = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", chain[4].Position, KoakFollowGeometry.Facing(chain[4], chain[3]));
        waiting.Phases = new PhaseList();
        waiting.Phases.Add(new FollowingPhase("N1LED"));
        waiting.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        AircraftState mover = KoakFollowGeometry.Spawn(ThirdCallsign, "C172", chain[2].Position, KoakFollowGeometry.Facing(chain[2], chain[3]));
        mover.Phases = null;
        mover.IndicatedAirspeed = 10.0;

        var atRest = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([waiting, mover], layout, 0, atRest.Add);
        atRest.ForEach(_output.WriteLine);
        Assert.Contains(atRest, line => line.StartsWith($"[Classify] {FollowerCallsign}: Stationary", StringComparison.Ordinal));

        waiting.IndicatedAirspeed = 10.0;
        var rolling = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([waiting, mover], layout, 0, rolling.Add);
        rolling.ForEach(_output.WriteLine);
        Assert.Contains(
            rolling,
            line => line.StartsWith($"[Classify] {FollowerCallsign}: Untracked, dir={waiting.TrueHeading.Degrees:F0}", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// A lead pushed tail-first on KOAK's B toward its own follower, which holds at rest astern of it: the lead is closing on the
    /// follower, so the detector limits the push as it would against any aircraft astern, while the follower stays unlimited.
    /// </summary>
    [Fact]
    public void Lead_PushingTowardItsHoldingFollower_IsLimited()
    {
        if (KoakFollowGeometry.LoadLayout(_output) is not { } layout)
        {
            return;
        }

        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        TrueHeading nose = KoakFollowGeometry.Facing(chain[3], chain[2]);
        TrueHeading tailward = nose.ToReciprocal();
        double pushKts = CategoryPerformance.PushbackSpeed(AircraftCategory.Jet);
        AircraftState lead = MakeAircraft(LeadCallsign, chain[3].Position, heading: nose.Degrees, gs: pushKts, pushbackHeading: tailward.Degrees);
        lead.Phases = new PhaseList();
        lead.Phases.Add(StraightPushFrom(lead));
        lead.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        lead.Ground.Layout = layout;
        LatLon astern = GeoMath.ProjectPoint(chain[3].Position, tailward, 150.0 / FtPerNm);
        AircraftState follower = MakeAircraft(FollowerCallsign, astern, heading: nose.Degrees, phase: new FollowingPhase(LeadCallsign));
        follower.Ground.Layout = layout;

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([lead, follower], layout, 1.0 / SimulationEngine.PhysicsSubTickRate, log.Add);
        log.ForEach(_output.WriteLine);

        Assert.True(
            (lead.Ground.SpeedLimit is { } limitKts) && (limitKts < pushKts),
            $"the push toward its follower was not limited: {lead.Ground.SpeedLimit}"
        );
        Assert.Null(follower.Ground.SpeedLimit);
        Assert.Null(follower.Ground.AutoYieldTarget);
    }

    /// <summary>
    /// A lead re-routed back up KOAK's B toward its own follower, which holds at rest on B ahead of it: the lead's track points at
    /// the follower, and the follower sits on the lead's own trail (the B edges it came down), which is what makes the lead
    /// closing on it — a follower off that trail would leave the lead exempt. The detector limits the lead, which yields to its
    /// follower, while the follower stays unlimited.
    /// </summary>
    [Fact]
    public void Lead_ReroutedBackTowardItsFollower_IsLimited()
    {
        if (KoakFollowGeometry.LoadLayout(_output) is not { } layout)
        {
            return;
        }

        // The lead came south down B from chain[5] to chain[3] (its trail), the follower behind it, and is now re-routed back north.
        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        const double TaxiKts = 10.0;
        AircraftState lead = KoakFollowGeometry.Spawn(LeadCallsign, "C172", chain[3].Position, KoakFollowGeometry.Facing(chain[3], chain[4]));
        lead.Phases = null;
        lead.IndicatedAirspeed = TaxiKts;
        lead.Ground.Layout = layout;
        lead.Ground.AssignedTaxiRoute = RouteAlongB(chain, 3, 5);
        for (int i = 5; i > 3; i--)
        {
            lead.Ground.TaxiEdgeTrail.Record(KoakFollowGeometry.EdgeBetween(chain[i], chain[i - 1]), chain[i]);
        }

        LatLon onTrail = KoakFollowGeometry.Between(chain[4].Position, chain[5].Position, 0.5);
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", onTrail, KoakFollowGeometry.Facing(chain[5], chain[4]));
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase(LeadCallsign));
        follower.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        follower.Ground.Layout = layout;

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([lead, follower], layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        Assert.True(
            (lead.Ground.SpeedLimit is { } limitKts) && (limitKts < TaxiKts),
            $"the lead taxiing at its follower was not limited: {lead.Ground.SpeedLimit}"
        );
        Assert.Null(follower.Ground.SpeedLimit);
        Assert.Null(follower.Ground.AutoYieldTarget);
    }

    /// <summary>
    /// A lead re-routed back down its own trail on KOAK toward its follower, the two head-on on one edge of the follower's follow
    /// route, while a third aircraft taxiing just ahead of the follower on that edge has already stopped it earlier in the pass.
    /// The head-on pair holds the follower, which cannot yield to its lead, so the lead yields to its follower instead, although
    /// the follower's limit was already zero; the follower keeps the third aircraft's stop.
    /// </summary>
    [Fact]
    public void Lead_ReroutedBackTowardItsFollower_FollowerAlreadyPinnedByThirdAircraft_IsLimited()
    {
        if (StartFollower(string.Empty) is not { } started)
        {
            return;
        }

        (KoakFollowGeometry.LeadRun run, AircraftState follower, FollowingPhase follow) = started;
        TaxiRoute route = Assert.IsType<TaxiRoute>(follow.FollowRoute);
        int index = StraightPairOn(route, firstMinFt: 50.0, secondMinFt: 240.0);
        TaxiRouteSegment came = route.Segments[index];
        TaxiRouteSegment shared = route.Segments[index + 1];
        route.CurrentSegmentIndex = index + 1;
        double sharedFt = shared.Edge.DistanceNm * FtPerNm;
        TrueHeading along = KoakFollowGeometry.Facing(shared.Edge.FromNode, shared.Edge.ToNode);
        follower.Position = KoakFollowGeometry.Between(shared.Edge.FromNode.Position, shared.Edge.ToNode.Position, 30.0 / sharedFt);
        follower.TrueHeading = along;
        follower.Ground.TaxiEdgeTrail.Clear();
        follower.Ground.TaxiEdgeTrail.Record(Assert.IsType<GroundEdge>(shared.Edge.Edge), shared.Edge.FromNode);

        LatLon aheadOfFollower = KoakFollowGeometry.Between(shared.Edge.FromNode.Position, shared.Edge.ToNode.Position, 110.0 / sharedFt);
        AircraftState third = KoakFollowGeometry.Spawn(ThirdCallsign, "C172", aheadOfFollower, along);
        third.Phases = null;
        third.IndicatedAirspeed = 5.0;
        third.Ground.Layout = run.Layout;
        third.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { Edge = shared.Edge, TaxiwayName = shared.TaxiwayName }],
            HoldShortPoints = [],
        };
        SetComingBack(run.Lead, came, shared, pastFt: 230.0, speedKts: 10.0);

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([follower, third, run.Lead], run.Layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        Assert.Contains(log, line => line.StartsWith($"[Pair] {FollowerCallsign}(Taxiing)+{run.Lead.Callsign}(Taxiing)", StringComparison.Ordinal));
        Assert.Equal(0.0, follower.Ground.SpeedLimit);
        Assert.Equal(ThirdCallsign, follower.Ground.AutoYieldTarget);
        Assert.Equal(0.0, run.Lead.Ground.SpeedLimit);
        Assert.Equal(FollowerCallsign, run.Lead.Ground.AutoYieldTarget);
        Assert.False(run.Lead.Ground.AutoYieldIsFollowing);
    }

    /// <summary>
    /// A lead that has just turned more than 90° at a KOAK junction onto an edge it had not driven, its follower behind it on the
    /// edge it came along: the lead's heading still points back past the follower, but it is driving onto new ground, not coming
    /// back — whether its trail has recorded the edge it turned onto (the node it entered that edge from, the junction, is behind
    /// it) or not yet (its newest trail edge is still the edge it came along, whose far end its heading points back toward, but
    /// it is no longer on that edge). Over several detector passes neither aircraft is limited by the pair, and both keep rolling.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Lead_TurnedSharplyAwayOntoNewGround_IsNotClosing(bool outEdgeRecorded)
    {
        if (KoakFollowGeometry.LoadLayout(_output) is not { } layout)
        {
            return;
        }

        (GroundNode farIn, GroundNode junction, GroundNode farOut, GroundEdge inEdge, GroundEdge outEdge, double turnDeg) =
            KoakFollowGeometry.SharpTurn(layout);
        TrueHeading inHeading = KoakFollowGeometry.Facing(farIn, junction);
        TrueHeading outHeading = KoakFollowGeometry.Facing(junction, farOut);
        double inFt = inEdge.DistanceNm * FtPerNm;
        double outFt = outEdge.DistanceNm * FtPerNm;

        // The lead stops just short of where its heading would stop pointing back past the follower, so the follower is the
        // aircraft the lead's track points at: only the node it entered its newest edge from, behind it, says it is not coming back.
        double behindFt = Math.Min(150.0, inFt - 5.0);
        double pastFt = Math.Clamp(-0.5 * behindFt * Math.Cos(turnDeg * Math.PI / 180.0), 2.0, outFt - 25.0);
        _output.WriteLine(
            $"junction #{junction.Id}: in from #{farIn.Id} on {inHeading.Degrees:F0}° ({inFt:F0} ft), out to #{farOut.Id} on "
                + $"{outHeading.Degrees:F0}° ({outFt:F0} ft), turn {turnDeg:F0}°; follower {behindFt:F0} ft short, lead {pastFt:F1} ft past"
        );

        const double TaxiKts = 10.0;
        LatLon pastTheTurn = KoakFollowGeometry.Between(junction.Position, farOut.Position, pastFt / outFt);
        AircraftState lead = KoakFollowGeometry.Spawn(LeadCallsign, "C172", pastTheTurn, outHeading);
        lead.Phases = null;
        lead.IndicatedAirspeed = TaxiKts;
        lead.Ground.Layout = layout;
        var outward = new DirectionalEdge
        {
            Edge = outEdge,
            FromNode = junction,
            ToNode = farOut,
        };
        lead.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { Edge = outward, TaxiwayName = outEdge.TaxiwayName }],
            HoldShortPoints = [],
        };
        lead.Ground.TaxiEdgeTrail.Record(inEdge, farIn);
        if (outEdgeRecorded)
        {
            lead.Ground.TaxiEdgeTrail.Record(outEdge, junction);
        }
        else
        {
            double backTowardEntryDeg = GeoMath.AbsBearingDifference(outHeading.Degrees, GeoMath.BearingTo(pastTheTurn, farIn.Position));
            Assert.True(
                backTowardEntryDeg < 90.0,
                $"the lead's heading is {backTowardEntryDeg:F0}° off the way back to #{farIn.Id}, "
                    + "the node its newest trail edge was entered from, so the test proves nothing"
            );
        }

        LatLon behind = KoakFollowGeometry.Between(junction.Position, farIn.Position, behindFt / inFt);
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", behind, inHeading);
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase(LeadCallsign));
        follower.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        follower.IndicatedAirspeed = TaxiKts;
        follower.Ground.Layout = layout;
        follower.Ground.TaxiEdgeTrail.Record(inEdge, farIn);

        const double PassSeconds = 1.0 / SimulationEngine.PhysicsSubTickRate;
        double stepNm = TaxiKts * PassSeconds / 3600.0;
        for (int pass = 1; pass <= 4; pass++)
        {
            var log = new List<string>();
            GroundConflictDetector.ApplySpeedLimits([lead, follower], layout, PassSeconds, log.Add);
            log.ForEach(_output.WriteLine);

            AssertPairNotResolved(log);
            Assert.Null(lead.Ground.SpeedLimit);
            Assert.Null(lead.Ground.AutoYieldTarget);
            Assert.Null(follower.Ground.SpeedLimit);
            Assert.Null(follower.Ground.AutoYieldTarget);
            lead.Position = GeoMath.ProjectPoint(lead.Position, outHeading, stepNm);
            follower.Position = GeoMath.ProjectPoint(follower.Position, inHeading, stepNm);
        }
    }

    /// <summary>
    /// A C172 lead starting a push on KOAK's B, its follower rolling up ahead of its nose inside the trail distance: the push runs
    /// away from the follower and opens the gap, so the lead is not closing on it and neither aircraft is limited.
    /// </summary>
    [Fact]
    public void Lead_PushingAwayFromFollowerInFrontOfItsNose_IsNotLimited()
    {
        if (KoakFollowGeometry.LoadLayout(_output) is not { } layout)
        {
            return;
        }

        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        TrueHeading nose = KoakFollowGeometry.Facing(chain[3], chain[2]);
        TrueHeading tailward = nose.ToReciprocal();
        double pushKts = CategoryPerformance.PushbackSpeed(AircraftCategory.Piston);
        AircraftState lead = MakeAircraft(LeadCallsign, chain[3].Position, heading: nose.Degrees, gs: pushKts, pushbackHeading: tailward.Degrees);
        lead.AircraftType = "C172";
        lead.Phases = new PhaseList();
        lead.Phases.Add(StraightPushFrom(lead));
        lead.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        lead.Ground.Layout = layout;
        LatLon aheadOfNose = GeoMath.ProjectPoint(chain[3].Position, nose, 150.0 / FtPerNm);
        AircraftState follower = MakeAircraft(
            FollowerCallsign,
            aheadOfNose,
            heading: tailward.Degrees,
            gs: 2.0,
            phase: new FollowingPhase(LeadCallsign)
        );
        follower.AircraftType = "C172";
        follower.Ground.Layout = layout;

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([lead, follower], layout, 1.0 / SimulationEngine.PhysicsSubTickRate, log.Add);
        log.ForEach(_output.WriteLine);

        AssertPairNotResolved(log);
        Assert.Null(lead.Ground.SpeedLimit);
        Assert.Null(lead.Ground.AutoYieldTarget);
        Assert.Null(follower.Ground.SpeedLimit);
    }

    /// <summary>
    /// The lead and its follower were never resolved as a pair: the detector's diagnostic log carries no <c>[Pair]</c> line and
    /// no <c>[LeadChain]</c> line, so the lead read as not closing on its follower.
    /// </summary>
    private static void AssertPairNotResolved(List<string> log)
    {
        Assert.DoesNotContain(log, line => line.StartsWith("[Pair]", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("[LeadChain]", StringComparison.Ordinal));
    }

    /// <summary>
    /// The lead of the re-route above has driven on back past <c>chain[4]</c>, the node it turned at, onto the B edge it first
    /// came down, its follower still holding further up that edge: its trail is that edge, the edge on to <c>chain[3]</c>, and
    /// that first edge again. The node the two newest trail edges share is now behind the lead, but the edge it is on appears
    /// earlier in its trail, so it is retracing its own trail toward its follower and is limited, the follower unlimited.
    /// </summary>
    [Fact]
    public void Lead_ReroutedBackPastTheNodeItTurnedAt_IsLimited()
    {
        if (KoakFollowGeometry.LoadLayout(_output) is not { } layout)
        {
            return;
        }

        // The lead came south down B from chain[5] to chain[3], turned back north, and has passed chain[4] again.
        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        const double TaxiKts = 10.0;
        LatLon pastTurnNode = KoakFollowGeometry.Between(chain[4].Position, chain[5].Position, 0.2);
        AircraftState lead = KoakFollowGeometry.Spawn(LeadCallsign, "C172", pastTurnNode, KoakFollowGeometry.Facing(chain[4], chain[5]));
        lead.Phases = null;
        lead.IndicatedAirspeed = TaxiKts;
        lead.Ground.Layout = layout;
        lead.Ground.AssignedTaxiRoute = RouteAlongB(chain, 4, 5);
        for (int i = 5; i > 3; i--)
        {
            lead.Ground.TaxiEdgeTrail.Record(KoakFollowGeometry.EdgeBetween(chain[i], chain[i - 1]), chain[i]);
        }

        GroundEdge drivenAgain = KoakFollowGeometry.EdgeBetween(chain[4], chain[5]);
        lead.Ground.TaxiEdgeTrail.Record(drivenAgain, TaxiEdgeTrail.EntryNodeOf(drivenAgain, lead));

        LatLon onTrail = KoakFollowGeometry.Between(chain[4].Position, chain[5].Position, 0.5);
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", onTrail, KoakFollowGeometry.Facing(chain[5], chain[4]));
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase(LeadCallsign));
        follower.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        follower.Ground.Layout = layout;

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([lead, follower], layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        string closingLine = $"[LeadChain] {LeadCallsign} closing on its follower {FollowerCallsign} (retracing its trail)";
        Assert.Contains(log, line => line.Contains(closingLine, StringComparison.Ordinal));
        Assert.Equal(3, lead.Ground.TaxiEdgeTrail.Edges.Count);
        Assert.True(
            (lead.Ground.SpeedLimit is { } limitKts) && (limitKts < TaxiKts),
            $"the lead retracing its trail toward its follower was not limited: {lead.Ground.SpeedLimit}"
        );
        Assert.Null(follower.Ground.SpeedLimit);
        Assert.Null(follower.Ground.AutoYieldTarget);
    }

    /// <summary>
    /// A lead that turned through a filleted KOAK corner (the fillet arc replaced the junction node, so the edge before the arc and
    /// the edge after it share no node) and then turned about on the edge after it, heading back toward the arc, its follower
    /// holding on that edge just past the arc: the lead's track points at the node it entered its newest edge from, so it is
    /// closing on its follower and is limited, the follower unlimited.
    /// </summary>
    [Fact]
    public void Lead_TurnedAboutAfterAFilletedCorner_IsLimited()
    {
        if (KoakFollowGeometry.LoadLayout(_output) is not { } layout)
        {
            return;
        }

        FilletedCorner corner = FindFilletedCorner(layout, minSweepDeg: 45.0);
        double outFt = corner.OutEdge.DistanceNm * FtPerNm;
        const double TaxiKts = 10.0;
        LatLon turnedAboutAt = KoakFollowGeometry.Between(corner.T2.Position, corner.Q.Position, 150.0 / outFt);
        AircraftState lead = KoakFollowGeometry.Spawn(LeadCallsign, "C172", turnedAboutAt, KoakFollowGeometry.Facing(corner.Q, corner.T2));
        lead.Phases = null;
        lead.IndicatedAirspeed = TaxiKts;
        lead.Ground.Layout = layout;
        lead.Ground.AssignedTaxiRoute = MakeRoute(
            new TaxiRouteSegment { TaxiwayName = corner.OutEdge.TaxiwayName, Edge = corner.OutEdge.Directed(corner.Q, corner.T2) }
        );
        lead.Ground.TaxiEdgeTrail.Record(corner.BeforeEdge, corner.O);
        lead.Ground.TaxiEdgeTrail.Record(corner.InEdge, corner.P);
        lead.Ground.TaxiEdgeTrail.Record(corner.OutEdge, corner.T2);

        LatLon pastTheArc = KoakFollowGeometry.Between(corner.T2.Position, corner.Q.Position, 25.0 / outFt);
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", pastTheArc, KoakFollowGeometry.Facing(corner.T2, corner.Q));
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase(LeadCallsign));
        follower.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        follower.Ground.Layout = layout;

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([lead, follower], layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        string closingLine = $"[LeadChain] {LeadCallsign} closing on its follower {FollowerCallsign} (turned about";
        Assert.Contains(log, line => line.Contains(closingLine, StringComparison.Ordinal));
        Assert.True(
            (lead.Ground.SpeedLimit is { } limitKts) && (limitKts < TaxiKts),
            $"the lead turned about toward its follower was not limited: {lead.Ground.SpeedLimit}"
        );
        Assert.Null(follower.Ground.SpeedLimit);
        Assert.Null(follower.Ground.AutoYieldTarget);
    }

    /// <summary>
    /// A C172 lead that turned off H onto KOAK's C and stands mid-way along its newest trail edge, the long C edge west of H
    /// (<see cref="KoakTaxiwayC.LongEdgeWestOfH"/>), its follower behind it on that edge at the follow gap, is cleared by a
    /// controller <c>TAXI C B</c>, back the way it came: the route is planned from the edge's end behind it and turns it about
    /// toward that end (<see cref="TaxiTurnAboutShape.FromFarEnd"/>), so segment 0 is the drive back to that end, not the edge it
    /// stands on. Turned about and driving back along that edge toward its follower, it is closing on it and is limited, the
    /// follower unlimited.
    /// </summary>
    [Fact]
    public void Lead_TurnedAboutMidEdgeFromFarEnd_IsLimited()
    {
        if (KoakFollowGeometry.NewEngine(_output, autoCross: true) is not { } setup)
        {
            return;
        }

        (SimulationEngine engine, AirportGroundLayout layout) = setup;
        (GroundNode entry, GroundNode ahead) = KoakTaxiwayC.LongEdgeWestOfH(layout);
        GroundEdge newestEdge = KoakFollowGeometry.EdgeBetween(entry, ahead);
        double edgeFt = newestEdge.DistanceNm * FtPerNm;
        double centresFt = FollowGap.StopGapFt("C172", AircraftCategory.Piston, "C172", AircraftCategory.Piston) + LengthFt("C172");
        const double FollowerFt = 40.0;
        double leadFt = FollowerFt + centresFt;
        _output.WriteLine($"C #{entry.Id}-#{ahead.Id} {edgeFt:F0} ft: lead {leadFt:F0} ft along, follower {FollowerFt:F0} ft along");

        const double TaxiKts = 10.0;
        LatLon midEdge = KoakFollowGeometry.Between(entry.Position, ahead.Position, leadFt / edgeFt);
        AircraftState lead = KoakFollowGeometry.Spawn(LeadCallsign, "C172", midEdge, KoakFollowGeometry.Facing(entry, ahead));
        lead.Ground.Layout = layout;
        engine.World.AddAircraft(lead);
        CommandResult taxi = engine.SendCommand(LeadCallsign, "TAXI C B");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(lead.Ground.AssignedTaxiRoute);
        TaxiRouteSegment first = route.Segments[0];
        _output.WriteLine(
            $"lead route {route.ToSummary()}, segment 0 #{first.FromNodeId}->#{first.ToNodeId}, "
                + $"turn about {route.PendingTurnAboutShape} toward #{route.TurnAboutTargetNodeId}"
        );
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, route.PendingTurnAboutShape);
        Assert.Equal(entry.Id, route.TurnAboutTargetNodeId);

        lead.Ground.TaxiEdgeTrail.Record(newestEdge, entry);
        TaxiTrailEdge recorded = Assert.NotNull(lead.Ground.TaxiEdgeTrail.Newest);
        GroundEdge driving = Assert.IsType<GroundEdge>(first.Edge.Edge);
        Assert.False(recorded.Is(driving), "segment 0 is the lead's newest trail edge, so the test proves nothing");
        TrueHeading turnedAbout = KoakFollowGeometry.Facing(ahead, entry);
        lead.TrueHeading = turnedAbout;
        lead.TrueTrack = turnedAbout;
        lead.IndicatedAirspeed = TaxiKts;

        LatLon behind = KoakFollowGeometry.Between(entry.Position, ahead.Position, FollowerFt / edgeFt);
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", behind, KoakFollowGeometry.Facing(entry, ahead));
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase(LeadCallsign));
        follower.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        follower.Ground.Layout = layout;
        follower.Ground.TaxiEdgeTrail.Record(newestEdge, entry);

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([lead, follower], layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        string closingLine = $"[LeadChain] {LeadCallsign} closing on its follower {FollowerCallsign} (turned about";
        Assert.Contains(log, line => line.Contains(closingLine, StringComparison.Ordinal));
        Assert.True(
            (lead.Ground.SpeedLimit is { } limitKts) && (limitKts < TaxiKts),
            $"the lead turned about toward its follower was not limited: {lead.Ground.SpeedLimit}"
        );
        Assert.Null(follower.Ground.SpeedLimit);
        Assert.Null(follower.Ground.AutoYieldTarget);
    }

    /// <summary>
    /// A lead part-way round a KOAK fillet arc turning more than 90°, already past 90° of it, its follower holding on the edge
    /// the lead came along: the lead's heading points back toward the ground it came over, but it is rounding the arc onto new
    /// ground, not coming back, so it is not closing on its follower and the pair is not resolved.
    /// </summary>
    [Fact]
    public void Lead_MidWayRoundAFilletedTurnOfMoreThan90Degrees_IsNotClosing()
    {
        if (KoakFollowGeometry.LoadLayout(_output) is not { } layout)
        {
            return;
        }

        FilletedCorner corner = FindFilletedCorner(layout, minSweepDeg: 100.0);
        double turnDeg = corner.SweepDeg - 5.0;
        (LatLon onArc, TrueHeading arcHeading) = PointAfterTurn(corner.Curve, turnDeg);
        const double TaxiKts = 10.0;
        AircraftState lead = KoakFollowGeometry.Spawn(LeadCallsign, "C172", onArc, arcHeading);
        lead.Phases = null;
        lead.IndicatedAirspeed = TaxiKts;
        lead.Ground.Layout = layout;
        lead.Ground.AssignedTaxiRoute = MakeRoute(
            new TaxiRouteSegment { TaxiwayName = corner.Arc.TaxiwayName, Edge = corner.Arc.Directed(corner.T1, corner.T2) },
            new TaxiRouteSegment { TaxiwayName = corner.OutEdge.TaxiwayName, Edge = corner.OutEdge.Directed(corner.T2, corner.Q) }
        );
        lead.Ground.TaxiEdgeTrail.Record(corner.BeforeEdge, corner.O);
        lead.Ground.TaxiEdgeTrail.Record(corner.InEdge, corner.P);

        double inFt = corner.InEdge.DistanceNm * FtPerNm;
        LatLon behind = KoakFollowGeometry.Between(corner.T1.Position, corner.P.Position, Math.Min(60.0, inFt / 2.0) / inFt);
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", behind, KoakFollowGeometry.Facing(corner.P, corner.T1));
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase(LeadCallsign));
        follower.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        follower.Ground.Layout = layout;
        double towardEntryOffDeg = Math.Abs(GeoMath.SignedBearingDifference(arcHeading.Degrees, GeoMath.BearingTo(onArc, corner.P.Position)));
        _output.WriteLine(
            $"lead {turnDeg:F0}° round a {corner.SweepDeg:F0}° arc; its heading is {towardEntryOffDeg:F0}° off the way back to #{corner.P.Id}"
        );
        Assert.True(towardEntryOffDeg < 90.0, "the lead's heading does not point back toward the ground it came over, so the test proves nothing");

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([lead, follower], layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        AssertPairNotResolved(log);
        Assert.Null(lead.Ground.SpeedLimit);
        Assert.Null(follower.Ground.SpeedLimit);
    }

    /// <summary>
    /// A lead that came round a filleted KOAK corner and turned about on the edge after the arc, its trail the edge before the
    /// corner's edge in, the edge in and the edge after the arc, now comes back over the arc and on along the edge in toward its
    /// follower, which holds on the edge in or part-way round the arc. On every detector pass, round the whole arc and along the
    /// edge in before the next trail record adds it again, the lead is closing on its follower — coming back over the arc on the
    /// arc, retracing its trail on the edge in: the pair is resolved and the follower is never limited. The lead is limited on the
    /// arc short of its follower, and wherever the follower stands dead ahead of it inside the stop distance.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lead_ComingBackOverTheFilletArcItCameRound_IsClosingOnEveryPass(bool followerOnArc)
    {
        if (KoakFollowGeometry.LoadLayout(_output) is not { } layout)
        {
            return;
        }

        FilletedCorner corner = FindFilletedCorner(layout, minSweepDeg: 45.0);
        double inFt = corner.InEdge.DistanceNm * FtPerNm;
        const double TaxiKts = 10.0;
        TaxiRoute route = MakeRoute(
            new TaxiRouteSegment { TaxiwayName = corner.Arc.TaxiwayName, Edge = corner.Arc.Directed(corner.T2, corner.T1) },
            new TaxiRouteSegment { TaxiwayName = corner.InEdge.TaxiwayName, Edge = corner.InEdge.Directed(corner.T1, corner.P) }
        );
        AircraftState lead = KoakFollowGeometry.Spawn(LeadCallsign, "C172", corner.T2.Position, KoakFollowGeometry.Facing(corner.Q, corner.T2));
        lead.Phases = null;
        lead.IndicatedAirspeed = TaxiKts;
        lead.Ground.Layout = layout;
        lead.Ground.AssignedTaxiRoute = route;
        lead.Ground.TaxiEdgeTrail.Record(corner.BeforeEdge, corner.O);
        lead.Ground.TaxiEdgeTrail.Record(corner.InEdge, corner.P);
        lead.Ground.TaxiEdgeTrail.Record(corner.OutEdge, corner.T2);

        (LatLon followerAt, TrueHeading followerHeading) = followerOnArc
            ? PointAfterTurn(corner.Curve, 0.25 * corner.SweepDeg)
            : (
                KoakFollowGeometry.Between(corner.T1.Position, corner.P.Position, Math.Min(90.0, inFt - 10.0) / inFt),
                KoakFollowGeometry.Facing(corner.P, corner.T1)
            );
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", followerAt, followerHeading);
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase(LeadCallsign));
        follower.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        follower.Ground.Layout = layout;
        follower.Ground.TaxiEdgeTrail.Record(corner.BeforeEdge, corner.O);
        follower.Ground.TaxiEdgeTrail.Record(corner.InEdge, corner.P);

        List<(LatLon Position, TrueHeading Heading, int Segment, string Where)> passes = [];
        for (double turnDeg = corner.SweepDeg - 2.0; turnDeg > 2.0; turnDeg -= 6.0)
        {
            (LatLon onArc, TrueHeading outbound) = PointAfterTurn(corner.Curve, turnDeg);
            passes.Add((onArc, outbound.ToReciprocal(), 0, $"arc {turnDeg:F0}° from #{corner.T1.Id}"));
        }

        TrueHeading towardP = KoakFollowGeometry.Facing(corner.T1, corner.P);
        for (double pastFt = 5.0; pastFt < inFt; pastFt += 10.0)
        {
            passes.Add(
                (KoakFollowGeometry.Between(corner.T1.Position, corner.P.Position, pastFt / inFt), towardP, 1, $"{pastFt:F0} ft past #{corner.T1.Id}")
            );
        }

        string closingLine = $"[LeadChain] {LeadCallsign} closing on its follower {FollowerCallsign}";
        int onArcPasses = 0;
        int limitedOnArc = 0;
        foreach ((LatLon position, TrueHeading heading, int segment, string where) in passes)
        {
            double gapFt = GeoMath.DistanceNm(position, followerAt) * FtPerNm;
            if (gapFt <= 60.0)
            {
                break;
            }

            lead.Position = position;
            lead.TrueHeading = heading;
            route.CurrentSegmentIndex = segment;
            var log = new List<string>();
            GroundConflictDetector.ApplySpeedLimits([lead, follower], layout, 0, log.Add);
            double offNoseDeg = GeoMath.AbsBearingDifference(heading.Degrees, GeoMath.BearingTo(position, followerAt));
            _output.WriteLine($"lead {where}: follower {gapFt:F0} ft, {offNoseDeg:F0}° off its nose; lead limit {lead.Ground.SpeedLimit}");

            string reason = (segment == 0) ? "coming back over the fillet arc it came round" : "retracing its trail";
            Assert.True(
                log.Exists(line => line.Contains($"{closingLine} ({reason})", StringComparison.Ordinal)),
                $"lead {where} not closing ({reason}):\n{string.Join("\n", log)}"
            );
            Assert.Null(follower.Ground.SpeedLimit);
            Assert.Null(follower.Ground.AutoYieldTarget);
            bool limited = (lead.Ground.SpeedLimit is { } limitKts) && (limitKts < TaxiKts);
            if ((gapFt <= GroundConflictDetector.DefaultStopDistanceFt) && (offNoseDeg <= 20.0))
            {
                Assert.True(limited, $"lead {where} closing on its follower dead ahead inside the stop distance, not limited");
            }

            onArcPasses += (segment == 0) ? 1 : 0;
            limitedOnArc += ((segment == 0) && limited) ? 1 : 0;
        }

        Assert.True(onArcPasses >= 3, $"only {onArcPasses} passes on the arc, so the test proves little about it");
        Assert.True(limitedOnArc > 0, "the lead was never limited coming back over the arc toward its follower");
    }

    /// <summary>
    /// The lead of <see cref="Lead_ComingBackOverTheFilletArcItCameRound_IsClosingOnEveryPass"/> a few feet to either side of the
    /// arc's curve close to either of its ends, where a straight edge at that end can be nearer than the curve: its driven route
    /// is on the arc, so at every such point it is closing on its follower on the edge in.
    /// </summary>
    [Fact]
    public void Lead_ComingBackOffTheCurveNearTheArcsEnds_IsClosing()
    {
        if (KoakFollowGeometry.LoadLayout(_output) is not { } layout)
        {
            return;
        }

        FilletedCorner corner = FindFilletedCorner(layout, minSweepDeg: 45.0);
        double inFt = corner.InEdge.DistanceNm * FtPerNm;
        AircraftState lead = KoakFollowGeometry.Spawn(LeadCallsign, "C172", corner.T2.Position, KoakFollowGeometry.Facing(corner.Q, corner.T2));
        lead.Phases = null;
        lead.IndicatedAirspeed = 10.0;
        lead.Ground.Layout = layout;
        lead.Ground.AssignedTaxiRoute = MakeRoute(
            new TaxiRouteSegment { TaxiwayName = corner.Arc.TaxiwayName, Edge = corner.Arc.Directed(corner.T2, corner.T1) },
            new TaxiRouteSegment { TaxiwayName = corner.InEdge.TaxiwayName, Edge = corner.InEdge.Directed(corner.T1, corner.P) }
        );
        lead.Ground.TaxiEdgeTrail.Record(corner.BeforeEdge, corner.O);
        lead.Ground.TaxiEdgeTrail.Record(corner.InEdge, corner.P);
        lead.Ground.TaxiEdgeTrail.Record(corner.OutEdge, corner.T2);

        LatLon followerAt = KoakFollowGeometry.Between(corner.T1.Position, corner.P.Position, Math.Min(90.0, inFt - 10.0) / inFt);
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", followerAt, KoakFollowGeometry.Facing(corner.P, corner.T1));
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase(LeadCallsign));
        follower.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        follower.Ground.Layout = layout;
        follower.Ground.TaxiEdgeTrail.Record(corner.BeforeEdge, corner.O);
        follower.Ground.TaxiEdgeTrail.Record(corner.InEdge, corner.P);

        string closingLine = $"[LeadChain] {LeadCallsign} closing on its follower {FollowerCallsign}";
        double[] nearEndsDeg = [1.0, 2.0, 4.0, 6.0, 8.0, 10.0, corner.SweepDeg - 10.0, corner.SweepDeg - 6.0, corner.SweepDeg - 2.0];
        List<string> notClosing = [];
        foreach (double turnDeg in nearEndsDeg)
        {
            (LatLon onArc, TrueHeading outbound) = PointAfterTurn(corner.Curve, turnDeg);
            TrueHeading back = outbound.ToReciprocal();
            foreach ((double sideDeg, double offFt) in new[] { (90.0, 3.0), (90.0, 6.0), (270.0, 3.0), (270.0, 6.0) })
            {
                lead.Position = GeoMath.ProjectPoint(onArc, new TrueHeading(back.Degrees + sideDeg), offFt / FtPerNm);
                lead.TrueHeading = back;
                var log = new List<string>();
                GroundConflictDetector.ApplySpeedLimits([lead, follower], layout, 0, log.Add);
                bool closing = log.Exists(line => line.Contains(closingLine, StringComparison.Ordinal));
                string where = $"{turnDeg:F0}° round from #{corner.T1.Id}, {offFt:F0} ft {(sideDeg < 180.0 ? "right" : "left")} of the curve";
                _output.WriteLine($"lead {where}: {(closing ? "closing" : "NOT closing")}");
                if (!closing)
                {
                    notClosing.Add(where);
                }
            }
        }

        Assert.True(notClosing.Count == 0, $"the lead coming back over the arc was not closing at: {string.Join("; ", notClosing)}");
    }

    /// <summary>
    /// A lead that has just come off a KOAK fillet arc turning more than 90° onto the edge after it, which its trail has not
    /// recorded yet (its newest trail edge is still the edge before the arc), its follower behind on that edge: the lead's heading
    /// points back within 90° of the node it entered its newest trail edge from, but it is on an edge its trail does not hold yet,
    /// new ground, so it is not closing and the pair is not resolved over several detector passes.
    /// </summary>
    [Fact]
    public void Lead_JustPastAFilletedTurnOfMoreThan90Degrees_IsNotClosing()
    {
        if (KoakFollowGeometry.LoadLayout(_output) is not { } layout)
        {
            return;
        }

        FilletedCorner corner = FindFilletedCorner(layout, minSweepDeg: 100.0);
        double outFt = corner.OutEdge.DistanceNm * FtPerNm;
        double inFt = corner.InEdge.DistanceNm * FtPerNm;
        TrueHeading outHeading = KoakFollowGeometry.Facing(corner.T2, corner.Q);
        LatLon justPast = KoakFollowGeometry.Between(corner.T2.Position, corner.Q.Position, 10.0 / outFt);
        const double TaxiKts = 10.0;
        AircraftState lead = KoakFollowGeometry.Spawn(LeadCallsign, "C172", justPast, outHeading);
        lead.Phases = null;
        lead.IndicatedAirspeed = TaxiKts;
        lead.Ground.Layout = layout;
        lead.Ground.AssignedTaxiRoute = MakeRoute(
            new TaxiRouteSegment { TaxiwayName = corner.OutEdge.TaxiwayName, Edge = corner.OutEdge.Directed(corner.T2, corner.Q) }
        );
        lead.Ground.TaxiEdgeTrail.Record(corner.BeforeEdge, corner.O);
        lead.Ground.TaxiEdgeTrail.Record(corner.InEdge, corner.P);

        TrueHeading inHeading = KoakFollowGeometry.Facing(corner.P, corner.T1);
        LatLon behind = KoakFollowGeometry.Between(corner.T1.Position, corner.P.Position, Math.Min(60.0, inFt / 2.0) / inFt);
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", behind, inHeading);
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase(LeadCallsign));
        follower.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        follower.IndicatedAirspeed = TaxiKts;
        follower.Ground.Layout = layout;
        follower.Ground.TaxiEdgeTrail.Record(corner.InEdge, corner.P);

        double backTowardEntryDeg = GeoMath.AbsBearingDifference(outHeading.Degrees, GeoMath.BearingTo(justPast, corner.P.Position));
        double chordFt = GeoMath.DistanceNm(corner.T1.Position, corner.T2.Position) * FtPerNm;
        _output.WriteLine(
            $"{corner.SweepDeg:F0}° arc, chord {chordFt:F0} ft; lead heading {backTowardEntryDeg:F0}° off the way back to #{corner.P.Id}"
        );
        Assert.True(
            backTowardEntryDeg < 90.0,
            "the lead's heading does not point back toward the node it entered its newest edge from, so the test proves nothing"
        );

        const double PassSeconds = 1.0 / SimulationEngine.PhysicsSubTickRate;
        double stepNm = TaxiKts * PassSeconds / 3600.0;
        for (int pass = 1; pass <= 4; pass++)
        {
            var log = new List<string>();
            GroundConflictDetector.ApplySpeedLimits([lead, follower], layout, PassSeconds, log.Add);
            log.ForEach(_output.WriteLine);

            AssertPairNotResolved(log);
            Assert.Null(lead.Ground.SpeedLimit);
            Assert.Null(follower.Ground.SpeedLimit);
            lead.Position = GeoMath.ProjectPoint(lead.Position, outHeading, stepNm);
            follower.Position = GeoMath.ProjectPoint(follower.Position, inHeading, stepNm);
        }
    }

    /// <summary>
    /// A lead just off a KATL fillet arc turning more than 90° whose radius the fillet generator shrank, so its chord is short
    /// (KOAK has no such corner), onto the edge after it, which its trail has not recorded yet, its follower behind on the edge
    /// in. Standing that close to the edge in, the lead reads as on it (<see cref="TaxiEdgeLocator.DrivenEdgeUnder"/> looks only
    /// around the edge last found), and its heading points back within 90° of the node it entered that edge from; but its route
    /// is on the edge out, new ground, so it is not closing and the pair is not resolved.
    /// </summary>
    [Fact]
    public void Lead_JustPastAShortChordFilletedTurnOfMoreThan90Degrees_IsNotClosing()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("ATL") is not { } layout)
        {
            _output.WriteLine("SKIP: KATL layout unavailable");
            return;
        }

        ShortChordCorner corner = FindShortChordCorner(layout);
        TrueHeading outHeading = KoakFollowGeometry.Facing(corner.T2, corner.Q);
        const double TaxiKts = 10.0;
        AircraftState lead = KoakFollowGeometry.Spawn(LeadCallsign, "C172", corner.LeadAt, outHeading);
        lead.Phases = null;
        lead.IndicatedAirspeed = TaxiKts;
        lead.Ground.Layout = layout;
        lead.Ground.AssignedTaxiRoute = MakeRoute(
            new TaxiRouteSegment { TaxiwayName = corner.OutEdge.TaxiwayName, Edge = corner.OutEdge.Directed(corner.T2, corner.Q) }
        );
        lead.Ground.TaxiEdgeTrail.Record(corner.InEdge, corner.P);

        double inFt = corner.InEdge.DistanceNm * FtPerNm;
        TrueHeading inHeading = KoakFollowGeometry.Facing(corner.P, corner.T1);
        LatLon behind = KoakFollowGeometry.Between(corner.T1.Position, corner.P.Position, Math.Min(60.0, inFt / 2.0) / inFt);
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", behind, inHeading);
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase(LeadCallsign));
        follower.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        follower.IndicatedAirspeed = TaxiKts;
        follower.Ground.Layout = layout;
        follower.Ground.TaxiEdgeTrail.Record(corner.InEdge, corner.P);

        double backTowardEntryDeg = GeoMath.AbsBearingDifference(outHeading.Degrees, GeoMath.BearingTo(corner.LeadAt, corner.P.Position));
        _output.WriteLine($"lead heading {backTowardEntryDeg:F0}° off the way back to #{corner.P.Id}");
        Assert.True(
            backTowardEntryDeg < 90.0,
            "the lead's heading does not point back toward the node it entered its newest edge from, so the test proves nothing"
        );

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([lead, follower], layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        AssertPairNotResolved(log);
        Assert.Null(lead.Ground.SpeedLimit);
        Assert.Null(follower.Ground.SpeedLimit);
    }

    /// <summary>
    /// A filleted corner turning more than 90° with a short chord: the straight <see cref="InEdge"/> from <see cref="P"/> to
    /// <see cref="T1"/>, the fillet arc from <see cref="T1"/> to <see cref="T2"/> (<see cref="ChordFt"/> apart, turning
    /// <see cref="SweepDeg"/>), and <see cref="OutEdge"/> from <see cref="T2"/> to <see cref="Q"/>; <see cref="LeadAt"/> is a point
    /// on the edge out just past the arc that still reads as on the edge in (<see cref="ShortChordCornerOn"/>).
    /// </summary>
    private sealed record ShortChordCorner(
        GroundNode P,
        GroundNode T1,
        GroundNode T2,
        GroundNode Q,
        GroundEdge InEdge,
        GroundEdge OutEdge,
        double SweepDeg,
        double ChordFt,
        LatLon LeadAt
    );

    /// <summary>
    /// The filleted corner of <paramref name="layout"/> turning more than 90° with the shortest chord
    /// (<see cref="ShortChordCornerOn"/>), driven either way round its arc. Fails when the layout has none.
    /// </summary>
    private ShortChordCorner FindShortChordCorner(AirportGroundLayout layout)
    {
        ShortChordCorner? best = null;
        foreach (GroundArc arc in layout.Arcs.Where(arc => !arc.IsRamp && !arc.IsRunwayCenterline))
        {
            foreach (GroundNode t1 in arc.Nodes)
            {
                if ((ShortChordCornerOn(layout, arc, t1) is { } corner) && (corner.ChordFt < (best?.ChordFt ?? double.PositiveInfinity)))
                {
                    best = corner;
                }
            }
        }

        Assert.True(best is not null, "the layout has no filleted corner turning more than 90° whose edge in is still found just past the arc");
        _output.WriteLine(
            $"corner #{best.P.Id}>#{best.T1.Id} arc {best.SweepDeg:F0}° chord {best.ChordFt:F0} ft #{best.T2.Id}>#{best.Q.Id} "
                + $"({best.InEdge.TaxiwayName} to {best.OutEdge.TaxiwayName})"
        );
        return best;
    }

    /// <summary>
    /// The corner <paramref name="arc"/> makes driven from <paramref name="t1"/>: a turn of more than 90° (under 175°) from a
    /// straight taxiway edge of at least 20 ft running into <paramref name="t1"/> along the arc's tangent there onto one of at
    /// least 20 ft running out of the arc's other end along its tangent there, the two sharing no node, with a point 5 ft (a
    /// third of the edge out at most) past the arc that reads as on the edge in: the edge
    /// <see cref="TaxiEdgeLocator.DrivenEdgeUnder"/> finds there, looking around the edge in, is the edge in or no nearer than it.
    /// Null when the arc makes no such corner that way round.
    /// </summary>
    private static ShortChordCorner? ShortChordCornerOn(AirportGroundLayout layout, GroundArc arc, GroundNode t1)
    {
        GroundNode t2 = arc.OtherNode(t1);
        (double startDeg, double endDeg) = TangentsFrom(arc, t1);
        double sweepDeg = GeoMath.AbsBearingDifference(startDeg, endDeg);
        GroundEdge? inEdge = AlignedTaxiEdge(t1, minFt: 20.0, alongDeg: startDeg, into: true, toleranceDeg: 20.0);
        GroundEdge? outEdge = AlignedTaxiEdge(t2, minFt: 20.0, alongDeg: endDeg, into: false, toleranceDeg: 20.0);
        if ((sweepDeg <= 90.0) || (sweepDeg >= 175.0) || (inEdge is null) || (outEdge is null))
        {
            return null;
        }

        GroundNode q = outEdge.OtherNode(t2);
        double outFt = outEdge.DistanceNm * FtPerNm;
        LatLon leadAt = KoakFollowGeometry.Between(t2.Position, q.Position, Math.Min(5.0, outFt / 3.0) / outFt);
        bool sharesANode = inEdge.Nodes.Any(node => outEdge.Nodes.Any(other => other.Id == node.Id));
        GroundEdge? found = TaxiEdgeLocator.DrivenEdgeUnder(layout, leadAt, (inEdge.Nodes[0].Id, inEdge.Nodes[1].Id));
        double chordFt = GeoMath.DistanceNm(t1.Position, t2.Position) * FtPerNm;
        return (!sharesANode && (found is not null) && (DistanceToFt(leadAt, inEdge) <= DistanceToFt(leadAt, found)))
            ? new ShortChordCorner(inEdge.OtherNode(t1), t1, t2, q, inEdge, outEdge, sweepDeg, chordFt, leadAt)
            : null;
    }

    private static double DistanceToFt(LatLon position, GroundEdge edge) =>
        GeoMath.DistanceToSegmentFt(position, edge.Nodes[0].Position, edge.Nodes[1].Position);

    /// <summary>
    /// The bearings (degrees) of <paramref name="arc"/>'s curve, driven from <paramref name="t1"/>, where it leaves
    /// <paramref name="t1"/> and where it reaches its other end.
    /// </summary>
    private static (double StartDeg, double EndDeg) TangentsFrom(GroundArc arc, GroundNode t1)
    {
        CubicBezier curve = arc.ToBezier();
        return (arc.Nodes[0].Id == t1.Id)
            ? (curve.TangentBearing(0.0), curve.TangentBearing(1.0))
            : ((curve.TangentBearing(1.0) + 180.0) % 360.0, (curve.TangentBearing(0.0) + 180.0) % 360.0);
    }

    /// <summary>
    /// A <c>FOLLOWG</c> pair the engine drives on KOAK: the lead taxis a route through a sharp turn — the sharpest unfilleted
    /// junction turning more than 90°, or the filleted corner turning most past 90° — and on beyond it, its follower behind it on
    /// its trail. The trail is the real one, recorded once a second as the lead drives, while the detector runs on every physics
    /// sub-tick. What it guards: in no second does the detector log a <c>[LeadChain]</c> closing line while the lead drives these
    /// two real KOAK corners through the real once-a-second recording path. The lead starts <paramref name="startFt"/> along its
    /// first edge, which shifts where it is at each trail record. It does not reach the state a closing test can misread after a
    /// turn of more than 90° (off its newest trail edge, its heading back toward that edge's entry node): the trail records the
    /// edge out before the heading swings, and at the fillet the tangent stubs; the unit tests above prove that state.
    /// </summary>
    [Theory]
    [InlineData(false, 0.0)]
    [InlineData(false, 4.0)]
    [InlineData(false, 8.0)]
    [InlineData(false, 12.0)]
    [InlineData(true, 0.0)]
    [InlineData(true, 4.0)]
    [InlineData(true, 8.0)]
    [InlineData(true, 12.0)]
    public void Engine_LeadThroughASharpTurn_NeverClosesOnItsFollowerBehind(bool filleted, double startFt)
    {
        if (KoakFollowGeometry.NewEngine(_output, autoCross: true) is not { } setup)
        {
            return;
        }

        (SimulationEngine engine, AirportGroundLayout layout) = setup;
        engine.World.GroundLayout = layout;
        (List<DirectionalEdge> path, GroundNode turnNode) = filleted ? FilletedTurnPath(layout) : SharpTurnPath(layout);
        _output.WriteLine(
            $"lead path (turn at #{turnNode.Id}): "
                + string.Join(", ", path.Select(e => $"#{e.FromNode.Id}>#{e.ToNode.Id} {e.Edge.GetType().Name} {e.DistanceNm * FtPerNm:F0}ft"))
        );

        AircraftState lead = StartTaxiingOn(engine, layout, LeadCallsign, path, startFt);
        LatLon followerStart = KoakFollowGeometry.Between(path[0].FromNode.Position, path[0].ToNode.Position, 0.3);
        for (int second = 0; (second < 90) && ((GeoMath.DistanceNm(lead.Position, followerStart) * FtPerNm) < 200.0); second++)
        {
            engine.TickOneSecond();
        }

        AircraftState follower = KoakFollowGeometry.Spawn(
            FollowerCallsign,
            "C172",
            followerStart,
            KoakFollowGeometry.Facing(path[0].FromNode, path[0].ToNode)
        );
        follower.Ground.Layout = layout;
        engine.World.AddAircraft(follower);
        CommandResult result = engine.SendCommand(FollowerCallsign, $"FOLLOWG {LeadCallsign}");
        Assert.True(result.Success, result.Message);

        var lines = new List<string>();
        int watchedSeconds = 0;
        try
        {
            GroundConflictDetector.DebugSink = lines.Add;
            for (int second = 1; (second <= 240) && !HasStopped(lead); second++)
            {
                lines.Clear();
                engine.TickOneSecond();
                double turnFt = GeoMath.DistanceNm(lead.Position, turnNode.Position) * FtPerNm;
                bool watched = FollowsOnTrail(follower, lead, layout) && (turnFt <= 200.0);
                watchedSeconds += watched ? 1 : 0;
                _output.WriteLine(
                    $"t={second} lead {turnFt:F0} ft from the turn hdg={lead.TrueHeading.Degrees:F0} gs={lead.GroundSpeed:F1} "
                        + $"trail={TrailSummary(lead)} "
                        + $"follower {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1}{(watched ? " watched" : string.Empty)}"
                );
                List<string> leadChain = [.. lines.Where(line => line.Contains("[LeadChain]", StringComparison.Ordinal))];
                Assert.True(
                    leadChain.Count == 0,
                    $"t={second}: the detector read the lead as closing on its follower:\n{string.Join("\n", leadChain)}"
                );
            }
        }
        finally
        {
            GroundConflictDetector.DebugSink = null;
        }

        Assert.True(
            watchedSeconds >= 3,
            $"the follower was on the lead's trail behind it near the turn for only {watchedSeconds} s, so the run proves little"
        );
        GroundEdge outOfTurn = Assert.IsType<GroundEdge>(path.First(e => e.FromNode.Id == turnNode.Id).Edge);
        Assert.Contains(lead.Ground.TaxiEdgeTrail.Edges, edge => edge.Is(outOfTurn));
    }

    private static bool HasStopped(AircraftState aircraft) =>
        (aircraft.Ground.AssignedTaxiRoute is not { IsComplete: false }) && (aircraft.GroundSpeed <= 0.0);

    /// <summary>
    /// Whether <paramref name="follower"/> is following <paramref name="lead"/> within the detector's search range of it, standing
    /// on an edge of the lead's trail.
    /// </summary>
    private static bool FollowsOnTrail(AircraftState follower, AircraftState lead, AirportGroundLayout layout)
    {
        if (
            (follower.Phases?.CurrentPhase is not FollowingPhase follow)
            || !string.Equals(follow.TargetCallsign, lead.Callsign, StringComparison.OrdinalIgnoreCase)
            || (GeoMath.DistanceNm(follower.Position, lead.Position) > GroundConflictDetector.SearchRangeNm)
        )
        {
            return false;
        }

        (int NodeA, int NodeB)? last = follower.Ground.TaxiEdgeTrail.Newest is { } newest ? (newest.NodeA, newest.NodeB) : null;
        return (TaxiEdgeLocator.DrivenEdgeUnder(layout, follower.Position, last) is { } under)
            && lead.Ground.TaxiEdgeTrail.Edges.Any(edge => edge.Is(under));
    }

    private static string TrailSummary(AircraftState aircraft) =>
        string.Join(" ", aircraft.Ground.TaxiEdgeTrail.Edges.TakeLast(3).Select(edge => $"#{edge.NodeA}-#{edge.NodeB}<#{edge.EntryNodeId}"));

    /// <summary>
    /// <paramref name="callsign"/>, a C172 added to <paramref name="engine"/> <paramref name="startFt"/> along the first edge of
    /// <paramref name="path"/>, facing along it, taxiing it as its route.
    /// </summary>
    private static AircraftState StartTaxiingOn(
        SimulationEngine engine,
        AirportGroundLayout layout,
        string callsign,
        List<DirectionalEdge> path,
        double startFt
    )
    {
        double firstFt = path[0].DistanceNm * FtPerNm;
        AircraftState aircraft = KoakFollowGeometry.Spawn(
            callsign,
            "C172",
            KoakFollowGeometry.Between(path[0].FromNode.Position, path[0].ToNode.Position, startFt / firstFt),
            KoakFollowGeometry.Facing(path[0].FromNode, path[0].ToNode)
        );
        aircraft.Ground.Layout = layout;
        aircraft.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [.. path.Select(edge => new TaxiRouteSegment { Edge = edge, TaxiwayName = edge.Edge.TaxiwayName })],
            HoldShortPoints = [],
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new TaxiingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    /// <summary>
    /// A route through <see cref="KoakFollowGeometry.SharpTurn"/>: about 300 ft of edges running straight on into the edge in, the edge in, the edge
    /// out, and about 200 ft of edges running straight on beyond it; with the junction the turn is made at.
    /// </summary>
    private static (List<DirectionalEdge> Path, GroundNode TurnNode) SharpTurnPath(AirportGroundLayout layout)
    {
        (GroundNode farIn, GroundNode junction, GroundNode farOut, GroundEdge inEdge, GroundEdge outEdge, double _) = KoakFollowGeometry.SharpTurn(
            layout
        );
        List<DirectionalEdge> path =
        [
            .. LeadIn(farIn, junction, 300.0),
            Along(inEdge, farIn, junction),
            Along(outEdge, junction, farOut),
            .. StraightOn(farOut, junction, 200.0),
        ];
        return (path, junction);
    }

    /// <summary>
    /// A route round the KOAK filleted corner turning most past 90° (<see cref="FindFilletedCorner"/>): about 200 ft of edges
    /// running straight on into the edge before it, that edge, the edge in, the arc, the edge out, and about 200 ft of edges
    /// running straight on beyond it; with the node where the arc ends, which the turn comes off at.
    /// </summary>
    private (List<DirectionalEdge> Path, GroundNode TurnNode) FilletedTurnPath(AirportGroundLayout layout)
    {
        FilletedCorner corner = FindFilletedCorner(layout, minSweepDeg: 90.0);
        List<DirectionalEdge> path =
        [
            .. LeadIn(corner.O, corner.P, 200.0),
            Along(corner.BeforeEdge, corner.O, corner.P),
            Along(corner.InEdge, corner.P, corner.T1),
            Along(corner.Arc, corner.T1, corner.T2),
            Along(corner.OutEdge, corner.T2, corner.Q),
            .. StraightOn(corner.Q, corner.T2, 200.0),
        ];
        return (path, corner.T2);
    }

    private static DirectionalEdge Along(IGroundEdge edge, GroundNode from, GroundNode to) =>
        new()
        {
            Edge = edge,
            FromNode = from,
            ToNode = to,
        };

    /// <summary>
    /// The edges driven up to <paramref name="node"/> over about <paramref name="minFt"/>, coming straight on from the side away
    /// from <paramref name="ahead"/>.
    /// </summary>
    private static List<DirectionalEdge> LeadIn(GroundNode node, GroundNode ahead, double minFt)
    {
        List<DirectionalEdge> away = StraightOn(node, ahead, minFt);
        away.Reverse();
        return [.. away.Select(edge => Along(edge.Edge, edge.ToNode, edge.FromNode))];
    }

    /// <summary>
    /// The edges from <paramref name="node"/> on, away from <paramref name="cameFrom"/>, each the taxiway edge (no runway centreline,
    /// no ramp connector) turning least from the way the one before ran, until about <paramref name="minFt"/> are covered or the
    /// least turn passes 45°.
    /// </summary>
    private static List<DirectionalEdge> StraightOn(GroundNode node, GroundNode cameFrom, double minFt)
    {
        List<DirectionalEdge> walk = [];
        GroundNode here = node;
        GroundNode previous = cameFrom;
        double coveredFt = 0.0;
        while (coveredFt < minFt)
        {
            GroundNode from = here;
            GroundNode back = previous;
            double inDeg = GeoMath.BearingTo(back.Position, from.Position);
            double TurnDeg(IGroundEdge edge) => GeoMath.AbsBearingDifference(inDeg, GeoMath.BearingTo(from.Position, edge.OtherNode(from).Position));
            IGroundEdge? next = from
                .Edges.Where(edge => !edge.IsRunwayCenterline && !edge.IsRamp && (edge.OtherNode(from).Id != back.Id))
                .MinBy(TurnDeg);
            if ((next is null) || (TurnDeg(next) > 45.0))
            {
                break;
            }

            walk.Add(Along(next, from, next.OtherNode(from)));
            coveredFt += next.DistanceNm * FtPerNm;
            previous = from;
            here = next.OtherNode(from);
        }

        return walk;
    }

    /// <summary>
    /// A filleted corner: the straight edge <see cref="BeforeEdge"/> from <see cref="O"/> to <see cref="P"/>, then
    /// <see cref="InEdge"/> from <see cref="P"/> to <see cref="T1"/>, the fillet <see cref="Arc"/> (played as
    /// <see cref="Curve"/>, turning <see cref="SweepDeg"/>) from <see cref="T1"/> to <see cref="T2"/>, and
    /// <see cref="OutEdge"/> from <see cref="T2"/> to <see cref="Q"/>.
    /// </summary>
    private sealed record FilletedCorner(
        GroundNode O,
        GroundNode P,
        GroundNode T1,
        GroundNode T2,
        GroundNode Q,
        GroundEdge BeforeEdge,
        GroundEdge InEdge,
        GroundArc Arc,
        GroundEdge OutEdge,
        CubicBezier Curve,
        double SweepDeg
    );

    /// <summary>
    /// The KOAK fillet arc turning the most, past <paramref name="minSweepDeg"/>, that a straight taxiway edge of at least 120 ft
    /// runs into along the arc's start tangent and another of at least 200 ft runs out of along its end tangent, the edge in
    /// having a straight edge before it within 45° of its line; the two edges share no node, the fillet having replaced the
    /// junction. Fails when the layout has none.
    /// </summary>
    private FilletedCorner FindFilletedCorner(AirportGroundLayout layout, double minSweepDeg)
    {
        FilletedCorner? best = null;
        foreach (GroundArc arc in layout.Arcs.Where(arc => !arc.IsRamp && !arc.IsRunwayCenterline))
        {
            CubicBezier curve = arc.ToBezier();
            double sweepDeg = Math.Abs(GeoMath.SignedBearingDifference(curve.TangentBearing(0.0), curve.TangentBearing(1.0)));
            if ((sweepDeg <= minSweepDeg) || (sweepDeg <= (best?.SweepDeg ?? 0.0)))
            {
                continue;
            }

            GroundNode t1 = arc.Nodes[0];
            GroundNode t2 = arc.Nodes[1];
            GroundEdge? inEdge = AlignedTaxiEdge(t1, minFt: 120.0, alongDeg: curve.TangentBearing(0.0), into: true, toleranceDeg: 20.0);
            GroundEdge? outEdge = AlignedTaxiEdge(t2, minFt: 200.0, alongDeg: curve.TangentBearing(1.0), into: false, toleranceDeg: 20.0);
            if ((inEdge is null) || (outEdge is null) || inEdge.Nodes.Any(node => outEdge.Nodes.Any(other => other.Id == node.Id)))
            {
                continue;
            }

            GroundNode p = inEdge.OtherNode(t1);
            if (AlignedTaxiEdge(p, minFt: 0.0, alongDeg: GeoMath.BearingTo(p.Position, t1.Position), into: true, toleranceDeg: 45.0) is { } before)
            {
                best = new FilletedCorner(before.OtherNode(p), p, t1, t2, outEdge.OtherNode(t2), before, inEdge, arc, outEdge, curve, sweepDeg);
            }
        }

        Assert.True(best is not null, $"KOAK has no filleted corner turning more than {minSweepDeg:F0}° between long enough straight edges");
        _output.WriteLine(
            $"corner #{best.O.Id}>#{best.P.Id}>#{best.T1.Id} arc {best.SweepDeg:F0}° #{best.T2.Id}>#{best.Q.Id} "
                + $"({best.InEdge.TaxiwayName} to {best.OutEdge.TaxiwayName})"
        );
        return best;
    }

    /// <summary>
    /// The straight taxiway edge at <paramref name="node"/>, at least <paramref name="minFt"/> long, whose line runs within
    /// <paramref name="toleranceDeg"/> of <paramref name="alongDeg"/>: driven into the node when <paramref name="into"/>, else out
    /// of it. Null when there is none.
    /// </summary>
    private static GroundEdge? AlignedTaxiEdge(GroundNode node, double minFt, double alongDeg, bool into, double toleranceDeg) =>
        node
            .Edges.OfType<GroundEdge>()
            .Where(edge => !edge.IsRunwayCenterline && !edge.IsRamp && (edge.DistanceNm * FtPerNm >= minFt))
            .Where(edge =>
            {
                GroundNode other = edge.OtherNode(node);
                double lineDeg = into ? GeoMath.BearingTo(other.Position, node.Position) : GeoMath.BearingTo(node.Position, other.Position);
                return Math.Abs(GeoMath.SignedBearingDifference(alongDeg, lineDeg)) <= toleranceDeg;
            })
            .MinBy(edge => edge.OtherNode(node).Id);

    /// <summary>
    /// A C560 lead coming back at 20 kt down its own trail on KOAK toward the node it came from, its C172 follower rolling toward
    /// the same node on the edge before it: the two converge there, and the follower, farther out, yields with a slow-taxi limit it
    /// is exempt from. The limit that moves onto the lead is raised to the speed the lead's own brakes shed in one detector pass,
    /// so the lead brakes as its brakes allow rather than dropping to the follower's limit at once.
    /// </summary>
    [Fact]
    public void TransferredLimit_FasterLead_GetsItsOwnBrakingFloor()
    {
        if (StartFollower(string.Empty) is not { } started)
        {
            return;
        }

        (KoakFollowGeometry.LeadRun run, AircraftState follower, FollowingPhase follow) = started;
        TaxiRoute route = Assert.IsType<TaxiRoute>(follow.FollowRoute);
        int index = StraightPairOn(route, firstMinFt: 140.0, secondMinFt: 110.0);
        TaxiRouteSegment towardNode = route.Segments[index];
        TaxiRouteSegment beyond = route.Segments[index + 1];
        route.CurrentSegmentIndex = index;
        double towardFt = towardNode.Edge.DistanceNm * FtPerNm;
        double shortOfNodeFt = Math.Min(200.0, towardFt - 10.0);
        follower.Position = KoakFollowGeometry.Between(towardNode.Edge.ToNode.Position, towardNode.Edge.FromNode.Position, shortOfNodeFt / towardFt);
        follower.TrueHeading = KoakFollowGeometry.Facing(towardNode.Edge.FromNode, towardNode.Edge.ToNode);
        follower.IndicatedAirspeed = 8.0;
        follower.Ground.TaxiEdgeTrail.Clear();
        follower.Ground.TaxiEdgeTrail.Record(Assert.IsType<GroundEdge>(towardNode.Edge.Edge), towardNode.Edge.FromNode);
        SetComingBack(run.Lead, towardNode, beyond, pastFt: 100.0, speedKts: 20.0);
        run.Lead.Targets.DesiredDecelRate = null;

        double pairFt = GeoMath.DistanceNm(follower.Position, run.Lead.Position) * FtPerNm;
        Assert.True(pairFt > 200.0, $"the pair is {pairFt:F0} ft apart, inside the closing trail distance, so the lead would be limited directly");
        AircraftCategory leadCategory = AircraftCategorization.Categorize(run.Lead.AircraftType);
        double floorKts = run.Lead.GroundSpeed - (CategoryPerformance.TaxiDecelRate(leadCategory) / SimulationEngine.PhysicsSubTickRate);
        Assert.True(floorKts > 15.0, $"the lead's braking floor {floorKts:F2} kt is not above every convergence limit, so the test proves nothing");

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([follower, run.Lead], run.Layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        Assert.Contains(
            log,
            line => line.Contains("[Convergence] shared node", StringComparison.Ordinal) && line.Contains($"{FollowerCallsign} yields")
        );
        Assert.Null(follower.Ground.SpeedLimit);
        Assert.True(run.Lead.Ground.SpeedLimit is not null, "the lead closing on its yielding follower was not limited");
        Assert.Equal(floorKts, run.Lead.Ground.SpeedLimit.Value, 6);
    }

    /// <summary>
    /// A push off a stand that has priority over its own follower taxiing up behind its tail: the follower gives way to the push,
    /// a hold it is exempt from toward its lead, but the rest of the push leg clears it, so the detector lets the push continue and
    /// moves no stop onto it.
    /// </summary>
    [Fact]
    public void GiveWayToPushback_PushClearsFollower_PushIsNotStopped()
    {
        AircraftState pusher = MakePusherToTarget(
            StandNorthOf(142),
            new LatLon(BaseLat + (1.42 * OffsetLatPer100Ft), BaseLon),
            new LatLon(BaseLat + (1.42 * OffsetLatPer100Ft), BaseLon + (2.0 * OffsetLonPer100Ft)),
            pushHeading: 180
        );
        AircraftState follower = MakeTaxiingE75L(new LatLon(BaseLat, BaseLon), heading: 0);
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase(pusher.Callsign));
        follower.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([pusher, follower], null, 0, log.Add);
        log.ForEach(_output.WriteLine);

        Assert.Contains(log, line => line.Contains($"[Pushback] {pusher.Callsign} push path clears {follower.Callsign}", StringComparison.Ordinal));
        Assert.True(
            (pusher.Ground.SpeedLimit is null) || (pusher.Ground.SpeedLimit > 0),
            $"the push leg clears its follower, but the push was stopped: SpeedLimit={pusher.Ground.SpeedLimit}"
        );
        Assert.Null(follower.Ground.SpeedLimit);
    }

    /// <summary>
    /// Puts <paramref name="lead"/> <paramref name="pastFt"/> along <paramref name="onto"/> from its from-node, having driven
    /// <paramref name="came"/> and then <paramref name="onto"/> (its trail), turned back toward that node at
    /// <paramref name="speedKts"/> on a one-segment route to it: a lead re-routed back the way it came.
    /// </summary>
    private static void SetComingBack(AircraftState lead, TaxiRouteSegment came, TaxiRouteSegment onto, double pastFt, double speedKts)
    {
        GroundNode back = onto.Edge.FromNode;
        GroundNode ahead = onto.Edge.ToNode;
        lead.Phases = null;
        lead.IndicatedAirspeed = speedKts;
        lead.Position = KoakFollowGeometry.Between(back.Position, ahead.Position, pastFt / (onto.Edge.DistanceNm * FtPerNm));
        lead.TrueHeading = KoakFollowGeometry.Facing(ahead, back);
        var backward = new DirectionalEdge
        {
            Edge = onto.Edge.Edge,
            FromNode = ahead,
            ToNode = back,
        };
        lead.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { Edge = backward, TaxiwayName = onto.TaxiwayName }],
            HoldShortPoints = [],
        };
        lead.Ground.TaxiEdgeTrail.Clear();
        lead.Ground.TaxiEdgeTrail.Record(Assert.IsType<GroundEdge>(came.Edge.Edge), came.Edge.FromNode);
        lead.Ground.TaxiEdgeTrail.Record(Assert.IsType<GroundEdge>(onto.Edge.Edge), onto.Edge.FromNode);
    }

    /// <summary>
    /// The index of the first segment of <paramref name="route"/>, from its current one on, that is a straight taxiway edge at
    /// least <paramref name="firstMinFt"/> long followed by one at least <paramref name="secondMinFt"/> long.
    /// </summary>
    private int StraightPairOn(TaxiRoute route, double firstMinFt, double secondMinFt)
    {
        _output.WriteLine(
            "follow route: "
                + string.Join(
                    ", ",
                    route.Segments.Select(s => $"{s.FromNodeId}->{s.ToNodeId} {s.Edge.Edge.GetType().Name} {s.Edge.DistanceNm * FtPerNm:F0}ft")
                )
        );
        int? found = Enumerable
            .Range(route.CurrentSegmentIndex, Math.Max(0, route.Segments.Count - route.CurrentSegmentIndex - 1))
            .Where(i => IsStraightAtLeast(route.Segments[i], firstMinFt) && IsStraightAtLeast(route.Segments[i + 1], secondMinFt))
            .Select(i => (int?)i)
            .FirstOrDefault();
        Assert.True(found is not null, $"the follow route has no straight edge of {firstMinFt:F0} ft followed by one of {secondMinFt:F0} ft");
        return found.Value;
    }

    private static bool IsStraightAtLeast(TaxiRouteSegment segment, double minFt) =>
        (segment.Edge.Edge is GroundEdge { IsRunwayCenterline: false }) && (segment.Edge.DistanceNm * FtPerNm >= minFt);

    /// <summary>
    /// Two aircraft told to follow each other on KOAK's B and a third taxiing between them: the lead-chain walk stops where the
    /// chain comes back on itself, so the detector returns, leaves the mutual pair unresolved, and resolves the third against each.
    /// </summary>
    [Fact]
    public void MutualFollowers_WithAThirdAircraft_ResolveTheThirdAgainstEach()
    {
        if (KoakFollowGeometry.LoadLayout(_output) is not { } layout)
        {
            return;
        }

        const string MutualA = "N5MUT";
        const string MutualB = "N6MUT";
        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        AircraftState a = MakeAircraft(
            MutualA,
            chain[2].Position,
            heading: KoakFollowGeometry.Facing(chain[2], chain[3]).Degrees,
            phase: new FollowingPhase(MutualB)
        );
        AircraftState b = MakeAircraft(
            MutualB,
            chain[5].Position,
            heading: KoakFollowGeometry.Facing(chain[5], chain[4]).Degrees,
            phase: new FollowingPhase(MutualA)
        );
        AircraftState third = MakeAircraft(
            ThirdCallsign,
            chain[3].Position,
            heading: KoakFollowGeometry.Facing(chain[3], chain[4]).Degrees,
            gs: 10.0,
            taxiRoute: RouteAlongB(chain, 3, 5)
        );
        Assert.True(
            GeoMath.DistanceNm(a.Position, b.Position) <= GroundConflictDetector.SearchRangeNm,
            "the mutual followers are out of the detector's search range of each other, so the pair proves nothing"
        );

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([a, b, third], layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        Assert.Contains(log, line => line.StartsWith($"[Pair] {MutualA}(", StringComparison.Ordinal) && line.Contains($"+{ThirdCallsign}("));
        Assert.Contains(log, line => line.StartsWith($"[Pair] {MutualB}(", StringComparison.Ordinal) && line.Contains($"+{ThirdCallsign}("));
        Assert.DoesNotContain(log, line => line.StartsWith($"[Pair] {MutualA}(", StringComparison.Ordinal) && line.Contains($"+{MutualB}("));
    }

    /// <summary>A taxi route along KOAK's B from <c>chain[from]</c> to <c>chain[to]</c>, edge by edge.</summary>
    private static TaxiRoute RouteAlongB(List<GroundNode> chain, int from, int to) =>
        new()
        {
            Segments =
            [
                .. Enumerable
                    .Range(from, to - from)
                    .Select(i => new TaxiRouteSegment
                    {
                        Edge = new DirectionalEdge
                        {
                            Edge = KoakFollowGeometry.EdgeBetween(chain[i], chain[i + 1]),
                            FromNode = chain[i],
                            ToNode = chain[i + 1],
                        },
                        TaxiwayName = "B",
                    }),
            ],
            HoldShortPoints = [],
        };

    /// <summary>
    /// A follower converging with a third aircraft standing on a side edge short of the junction is limited by the detector, and
    /// the speed the follow publishes never exceeds that limit: the follow's own caps only ever lower it.
    /// </summary>
    [Fact]
    public void DetectorLimit_NeverRaisedByFollowCaps()
    {
        if (StartFollowAtJunction() is not { } at)
        {
            return;
        }

        AircraftState third = ThirdAtTheSide(at, speedKts: 0.0);
        at.Run.Engine.World.AddAircraft(third);
        int limitedSeconds = 0;
        for (int second = 1; second <= 40; second++)
        {
            at.Run.Engine.TickOneSecond();
            double? limit = at.Follower.Ground.SpeedLimit;
            double? published = at.Follower.Targets.TargetSpeed;
            _output.WriteLine($"t={second} gs={at.Follower.GroundSpeed:F2} limit={limit:F2} published={published:F2}");
            if (limit is { } kts)
            {
                limitedSeconds++;
                Assert.True((published is null) || (published <= kts + 1e-9), $"t={second}: published {published:F3} kt over the limit {kts:F3} kt");
            }
        }

        Assert.True(limitedSeconds > 0, "the detector never limited the follower converging with the third aircraft");
    }

    /// <summary>
    /// A KOAK lead taxiing on B, a C172 taxiing <c>TAXI B W 30</c> behind it, then sent <c>FOLLOWG</c> the lead with
    /// <paramref name="clearance"/> appended, rolling on its follow route.
    /// </summary>
    private (KoakFollowGeometry.LeadRun Run, AircraftState Follower, FollowingPhase Follow)? StartFollower(string clearance)
    {
        if (KoakFollowGeometry.StartTaxiingLead(_output) is not { } run)
        {
            return null;
        }

        AircraftState follower = KoakFollowGeometry.AddTaxiing(
            (run.Engine, run.Layout),
            FollowerCallsign,
            "C172",
            (run.Chain[6], run.Chain[5]),
            "TAXI B W 30"
        );
        CommandResult result = run.Engine.SendCommand(FollowerCallsign, $"FOLLOWG {run.Lead.Callsign}{clearance}");
        Assert.True(result.Success, result.Message);
        for (int second = 0; (second < 60) && !IsRollingOnFollowRoute(follower); second++)
        {
            run.Engine.TickOneSecond();
        }

        Assert.True(IsRollingOnFollowRoute(follower), $"the follower never rolled on a follow route: gs={follower.GroundSpeed:F1}");
        return (run, follower, Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase));
    }

    private static bool IsRollingOnFollowRoute(AircraftState follower) =>
        (follower.Phases?.CurrentPhase is FollowingPhase { FollowRoute.IsComplete: false }) && (follower.GroundSpeed >= 5.0);

    /// <summary>
    /// While <paramref name="follower"/> rolls on its follow route, the first junction on that route 250 ft or more ahead of it in a
    /// straight line, within 1,200 ft along the route and more than <see cref="KoakFollowGeometry.ClearOfRunwayFt"/> from every
    /// runway centreline, with a straight taxiway side edge off the route 75-1,200 ft long; else null.
    /// </summary>
    private static (GroundNode Junction, GroundEdge Edge)? SideJunctionAhead(AircraftState follower)
    {
        if (!IsRollingOnFollowRoute(follower) || (follower.Phases?.CurrentPhase is not FollowingPhase { FollowRoute: { } route }))
        {
            return null;
        }

        List<RunwayInfo> runways = [.. RunwayOccupancy.AirportRunways(KoakFollowGeometry.AirportId)];
        HashSet<int> routeNodes = [.. route.Segments.SelectMany(s => new[] { s.FromNodeId, s.ToNodeId })];
        double startFt = route.PrefixDistanceFt(route.CurrentSegmentIndex);
        return Enumerable
            .Range(route.CurrentSegmentIndex, route.Segments.Count - route.CurrentSegmentIndex)
            .Where(i => route.PrefixDistanceFt(i + 1) - startFt <= 1200.0)
            .Select(i => route.Segments[i].Edge.ToNode)
            .Where(node =>
                (GeoMath.DistanceNm(follower.Position, node.Position) * FtPerNm >= 250.0)
                && runways.All(r =>
                    GeoMath.DistanceToSegmentFt(node.Position, new LatLon(r.Lat1, r.Lon1), new LatLon(r.Lat2, r.Lon2))
                    > KoakFollowGeometry.ClearOfRunwayFt
                )
            )
            .SelectMany(node => node.Edges.OfType<GroundEdge>().Select(edge => (Junction: node, Edge: edge)))
            .Where(c =>
                !c.Edge.IsRunwayCenterline
                && !c.Edge.IsRamp
                && !routeNodes.Contains(c.Edge.OtherNode(c.Junction).Id)
                && (c.Edge.DistanceNm * FtPerNm >= 75.0)
                && (c.Edge.DistanceNm * FtPerNm <= 1200.0)
            )
            .Select(c => ((GroundNode Junction, GroundEdge Edge)?)c)
            .FirstOrDefault();
    }

    /// <summary>A follower on its follow route, a junction ahead on that route, and a point on a side edge off it into the junction.</summary>
    private sealed record FollowAtJunction(
        KoakFollowGeometry.LeadRun Run,
        AircraftState Follower,
        FollowingPhase Follow,
        GroundNode Junction,
        GroundNode SideFrom,
        GroundEdge Side,
        LatLon SidePoint
    );

    /// <summary>How long (s) <see cref="StartFollowAtJunction"/> ticks the follower across both runways to a side taxiway.</summary>
    private const int JunctionBudgetSeconds = 240;

    /// <summary>
    /// <see cref="StartFollower"/> cleared across 28R and 28L, ticked until <see cref="SideJunctionAhead"/> finds a junction ahead,
    /// and a point on that junction's side edge nearer the junction than the follower is.
    /// </summary>
    private FollowAtJunction? StartFollowAtJunction()
    {
        if (StartFollower("; CROSS 28R 28L") is not { } started)
        {
            return null;
        }

        (KoakFollowGeometry.LeadRun run, AircraftState follower, FollowingPhase _) = started;
        (GroundNode Junction, GroundEdge Edge)? found = SideJunctionAhead(follower);
        for (int second = 0; (second < JunctionBudgetSeconds) && (found is null); second++)
        {
            run.Engine.TickOneSecond();
            found = SideJunctionAhead(follower);
            if (second % 10 == 0)
            {
                _output.WriteLine(
                    $"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} "
                        + $"route={(follower.Phases?.CurrentPhase as FollowingPhase)?.FollowRoute?.ToSummary()}"
                );
            }
        }

        Assert.True(found is not null, $"the follower rolled on no follow route with a side taxiway ahead within {JunctionBudgetSeconds}s");
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        _output.WriteLine($"follow route {follow.FollowRoute?.ToSummary()}, segment {follow.FollowRoute?.CurrentSegmentIndex}");
        (GroundNode junction, GroundEdge side) = found.Value;
        GroundNode from = side.OtherNode(junction);
        double followerToJunctionFt = GeoMath.DistanceNm(follower.Position, junction.Position) * FtPerNm;
        double sideFt = side.DistanceNm * FtPerNm;
        double fromJunctionFt = Math.Min(sideFt, followerToJunctionFt) / 2.0;
        LatLon point = KoakFollowGeometry.Between(junction.Position, from.Position, fromJunctionFt / sideFt);
        _output.WriteLine(
            $"junction #{junction.Id} {followerToJunctionFt:F0} ft from the follower; side {side.TaxiwayName} from #{from.Id}, point "
                + $"{fromJunctionFt:F0} ft short of the junction"
        );
        return new FollowAtJunction(run, follower, follow, junction, from, side, point);
    }

    /// <summary>
    /// A C172 at <paramref name="at"/>'s side point facing the junction, with no phase and a one-segment route into the junction,
    /// at <paramref name="speedKts"/>: a plain taxiing aircraft to the detector.
    /// </summary>
    private static AircraftState ThirdAtTheSide(FollowAtJunction at, double speedKts)
    {
        AircraftState third = KoakFollowGeometry.Spawn(ThirdCallsign, "C172", at.SidePoint, KoakFollowGeometry.Facing(at.SideFrom, at.Junction));
        third.Phases = null;
        third.Ground.Layout = at.Run.Layout;
        var intoJunction = new DirectionalEdge
        {
            Edge = at.Side,
            FromNode = at.SideFrom,
            ToNode = at.Junction,
        };
        third.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { Edge = intoJunction, TaxiwayName = at.Side.TaxiwayName }],
            HoldShortPoints = [],
        };
        third.IndicatedAirspeed = speedKts;
        return third;
    }

    /// <summary>The limit and yield target the detector left on the follower and on the third aircraft.</summary>
    private static (double? FollowerLimit, string? FollowerYieldsTo, double? ThirdLimit, string? ThirdYieldsTo) YieldOutcome(
        AircraftState follower,
        AircraftState third
    ) => (follower.Ground.SpeedLimit, follower.Ground.AutoYieldTarget, third.Ground.SpeedLimit, third.Ground.AutoYieldTarget);

    [Fact]
    public void AircraftFarApart_NoInteraction()
    {
        // A and B are 1nm apart (well beyond SearchRangeNm=0.1)
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);
        AircraftState b = MakeAircraft("B", new LatLon(BaseLat + 1.0 / 60.0, BaseLon), heading: 180, gs: 15);

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(b.Ground.SpeedLimit);
    }

    [Fact]
    public void BothStationary_NoLimitsSet()
    {
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, phase: new AtParkingPhase());
        AircraftState b = MakeAircraft("B", new LatLon(BaseLat + 0.5 * OffsetLatPer100Ft, BaseLon), heading: 180, gs: 0, phase: new AtParkingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(b.Ground.SpeedLimit);
    }

    [Fact]
    public void SingleAircraftOnGround_NoLimitsSet()
    {
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);

        var aircraft = new List<AircraftState> { a };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Null(a.Ground.SpeedLimit);
    }

    [Fact]
    public void IsClearOf_PushingReference_OutsideBuffer_ReturnsTrue()
    {
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);
        AircraftState b = MakeAircraft("B", new LatLon(BaseLat + 5 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);

        Assert.True(GroundConflictDetector.IsClearOf(a, b, null));
    }

    [Fact]
    public void IsClearOf_PushingReference_InsideBuffer_ReturnsFalse()
    {
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);
        AircraftState b = MakeAircraft("B", new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);

        Assert.False(GroundConflictDetector.IsClearOf(a, b, null));
    }

    // -------------------------------------------------------------------------
    // BREAK command integration
    // -------------------------------------------------------------------------

    [Fact]
    public void Break_AircraftExempt_NotSpeedLimited_WhenConflictWouldApply()
    {
        // A heading north at 15kts, B is 150ft ahead also heading north at 10kts.
        // Without BREAK, A would get a trailing speed limit. With BREAK, A is exempt.
        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        GroundEdge edge01 = layout.Edges[0];

        TaxiRoute routeA = MakeRoute(MakeSeg(0, 1, "A", edge01));
        TaxiRoute routeB = MakeRoute(MakeSeg(0, 1, "A", edge01));

        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        AircraftState b = MakeAircraft(
            "B",
            new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon),
            heading: 0,
            gs: 10,
            taxiRoute: routeB,
            phase: new TaxiingPhase()
        );

        // Give A an active BREAK timer
        a.Ground.ConflictBreakRemainingSeconds = 15.0;

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout, deltaSeconds: 0);

        // A has BREAK — must not receive a speed limit
        Assert.Null(a.Ground.SpeedLimit);
    }

    [Fact]
    public void Break_TimerDecrements_EachTick()
    {
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);
        a.Ground.ConflictBreakRemainingSeconds = 15.0;

        var aircraft = new List<AircraftState> { a };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null, deltaSeconds: 1.0);

        Assert.Equal(14.0, a.Ground.ConflictBreakRemainingSeconds, precision: 9);
    }

    [Fact]
    public void Break_TimerExpired_ConflictsResume()
    {
        // Same setup as Break_AircraftExempt test, but timer is at zero.
        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        GroundEdge edge01 = layout.Edges[0];

        TaxiRoute routeA = MakeRoute(MakeSeg(0, 1, "A", edge01));
        TaxiRoute routeB = MakeRoute(MakeSeg(0, 1, "A", edge01));

        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        AircraftState b = MakeAircraft(
            "B",
            new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon),
            heading: 0,
            gs: 10,
            taxiRoute: routeB,
            phase: new TaxiingPhase()
        );

        // BREAK has expired
        a.Ground.ConflictBreakRemainingSeconds = 0;

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout, deltaSeconds: 0);

        // Timer is zero — conflict detection re-engages; trailer should be limited
        bool anyLimited = a.Ground.SpeedLimit is not null || b.Ground.SpeedLimit is not null;
        Assert.True(anyLimited, "Expected conflict detection to resume after BREAK expires");
    }

    /// <summary>
    /// Closing-proximity stop must account for the trailer's length, not just
    /// the leader's. AircraftState.Position is the centroid, so to keep the
    /// trailer's nose from passing through the leader's tail the stop distance
    /// (between centroids) must be at least
    /// <c>(leaderLengthFt + trailerLengthFt) / 2 + buffer</c>.
    ///
    /// Pre-fix bug: <c>GetSeparation</c> used <c>leaderLength + buffer</c>,
    /// which underestimates whenever the trailer is longer than the leader.
    /// In <c>sfo-s1-ground-control-28-01</c> at t=917 a stationary E175 (length
    /// 106ft) was hit in the tail by an A350 (length 218.5ft) closing on it —
    /// pair gap was ~145ft, current code's stop threshold was 131ft, so the
    /// A350 kept going until its nose ran into the E175.
    /// </summary>
    [Fact]
    public void LongTrailerBehindShortStationaryLeader_StopsBeforeNoseTouchesTail()
    {
        TestVnasData.EnsureInitialized();
        if (!Yaat.Sim.Data.Faa.FaaAircraftDatabase.IsInitialized)
        {
            return;
        }

        // E175: ~106 ft, A359: ~218.5 ft (FAA ACD).
        // Required nose-to-tail separation = (106 + 218.5) / 2 + 25 = 187.25 ft.
        // Place trailer 145 ft *south* of leader (center-to-center) — clearly
        // inside the dimension-aware threshold but well past the
        // leader-only-length threshold (106 + 25 = 131 ft).
        const double pairCenterToCenterFt = 145.0;
        double offsetLat = pairCenterToCenterFt / FtPerNm / 60.0;

        var leader = new AircraftState
        {
            Callsign = "LEAD",
            AircraftType = "E75L/L",
            Position = new LatLon(BaseLat + offsetLat, BaseLon),
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
            IndicatedAirspeed = 0,
            Phases = new PhaseList(),
        };
        leader.Phases.Add(new AtParkingPhase());
        leader.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        var trailer = new AircraftState
        {
            Callsign = "TRAIL",
            AircraftType = "A359/L",
            Position = new LatLon(BaseLat, BaseLon),
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
            IndicatedAirspeed = 15,
        };

        var aircraft = new List<AircraftState> { leader, trailer };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.NotNull(trailer.Ground.SpeedLimit);
        Assert.Equal(0.0, trailer.Ground.SpeedLimit!.Value);
    }

    // -------------------------------------------------------------------------
    // IsHeld classification (GIVEWAY / HOLDPOSITION / BEHIND)
    // -------------------------------------------------------------------------

    /// <summary>
    /// A controller-held aircraft (GIVEWAY/HOLDPOSITION) must classify as Stationary
    /// so the wingspan-lateral-clearance bypass opens for passing traffic.
    /// Geometry: held aircraft at origin facing north; mover 100ft south and
    /// 200ft east, heading north so the held aircraft sits inside the mover's
    /// forward cone but well beyond combined half-wingspans + buffer.
    /// Without the IsHeld → Stationary classification, the mover would stop
    /// because a held-but-routed aircraft was still classified as Taxiing.
    /// </summary>
    [Fact]
    public void HeldAircraft_PassableLaterally()
    {
        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        GroundEdge edge01 = layout.Edges[0];

        TaxiRoute routeHeld = MakeRoute(MakeSeg(0, 1, "A", edge01));
        TaxiRoute routeMover = MakeRoute(MakeSeg(0, 1, "A", edge01));

        // Held aircraft sits at origin, heading north, with a route. Hold=GiveWay
        // simulates GIVEWAY — the route is assigned but the aircraft is parked
        // until the resume geometry fires.
        AircraftState held = MakeAircraft("HELD", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, taxiRoute: routeHeld, phase: new TaxiingPhase());
        held.Ground.Hold = HoldDirective.GiveWay("MOVER");

        // Mover is 100ft south and 200ft east, heading north. B738 wingspan ~117 ft
        // → required lateral = 117/2 + 117/2 + 25 = 142 ft. The 200ft offset
        // clears this, so the bypass should let the mover pass at speed.
        const double OffsetLonPer100Ft = 100.0 / FtPerNm / 60.0; // approx; longitude scales with cos(lat) but at 37° the error is small
        AircraftState mover = MakeAircraft(
            "MOVER",
            new LatLon(BaseLat - OffsetLatPer100Ft, BaseLon + 2.5 * OffsetLonPer100Ft),
            heading: 0,
            gs: 15,
            taxiRoute: routeMover,
            phase: new TaxiingPhase()
        );

        var aircraft = new List<AircraftState> { held, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout);

        // Held aircraft is the obstacle — it stays at gs=0 by virtue of IsHeld;
        // the detector should not need to set its limit either way, but the key
        // assertion is on the mover: lateral clearance bypass must open.
        Assert.True(
            mover.Ground.SpeedLimit is null || mover.Ground.SpeedLimit > 0,
            $"Mover got SpeedLimit={mover.Ground.SpeedLimit?.ToString("F1") ?? "null"}; expected null or >0 because "
                + "the held aircraft is laterally offset by ~200ft (> combined half-wingspans + buffer)."
        );
    }

    /// <summary>
    /// Same geometry but with the held aircraft directly in the mover's path —
    /// lateral offset is zero. The mover MUST stop. Verifies the Hold-based
    /// Stationary classification doesn't accidentally disable in-path collision
    /// avoidance.
    /// </summary>
    [Fact]
    public void HeldAircraft_StopsInPathMover()
    {
        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        GroundEdge edge01 = layout.Edges[0];

        TaxiRoute routeHeld = MakeRoute(MakeSeg(0, 1, "A", edge01));
        TaxiRoute routeMover = MakeRoute(MakeSeg(0, 1, "A", edge01));

        AircraftState held = MakeAircraft("HELD", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, taxiRoute: routeHeld, phase: new TaxiingPhase());
        held.Ground.Hold = HoldDirective.HoldPosition;

        // Mover 100ft south, same longitude — directly behind the held aircraft.
        AircraftState mover = MakeAircraft(
            "MOVER",
            new LatLon(BaseLat - OffsetLatPer100Ft, BaseLon),
            heading: 0,
            gs: 15,
            taxiRoute: routeMover,
            phase: new TaxiingPhase()
        );

        var aircraft = new List<AircraftState> { held, mover };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout);

        Assert.NotNull(mover.Ground.SpeedLimit);
        Assert.Equal(0.0, mover.Ground.SpeedLimit!.Value);
    }

    /// <summary>
    /// Issue #407: the Hold → Stationary classification assumed a held aircraft is
    /// actually stopped. When a bug (or the deceleration window) leaves a held
    /// aircraft still rolling, treating it as Stationary removed BOTH aircraft of a
    /// held head-on pair from conflict resolution and they drove through each other.
    /// A held aircraft that is still moving must keep participating as a mover.
    /// </summary>
    [Fact]
    public void HeldButStillMoving_HeadOnPair_StillGetsSpeedLimits()
    {
        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        GroundEdge edge01 = layout.Edges[0];

        // Nose-to-nose on the same taxiway, 250 ft apart, both "held" but both
        // still rolling at taxi speed (the OAK N262QX / N28697 geometry).
        AircraftState north = MakeAircraft(
            "NORTH",
            new LatLon(BaseLat, BaseLon),
            heading: 0,
            gs: 12,
            taxiRoute: MakeRoute(MakeSeg(0, 1, "A", edge01)),
            phase: new TaxiingPhase()
        );
        north.Ground.Hold = HoldDirective.HoldPosition;

        AircraftState south = MakeAircraft(
            "SOUTH",
            new LatLon(BaseLat + 2.5 * OffsetLatPer100Ft, BaseLon),
            heading: 180,
            gs: 12,
            taxiRoute: MakeRoute(MakeSeg(1, 0, "A", edge01)),
            phase: new TaxiingPhase()
        );
        south.Ground.Hold = HoldDirective.HoldPosition;

        var aircraft = new List<AircraftState> { north, south };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout);

        Assert.True(
            (north.Ground.SpeedLimit is not null) || (south.Ground.SpeedLimit is not null),
            "Two held-but-still-moving aircraft converging head-on got no conflict speed limits — "
                + "the Stationary classification must only apply to held aircraft that are actually near-stationary."
        );
    }

    /// <summary>
    /// Diagnostic enrichment for GIVEWAY relationships. When the controller has
    /// said "N123, give way to MOVER" and both aircraft are in the conflict
    /// detector's search range, the DebugSink should emit a
    /// "[Pair] ControllerGiveWay N123→MOVER" line so the operator sees the
    /// intent-bearing relationship instead of an anonymous "Stationary" pair.
    /// </summary>
    [Fact]
    public void DebugSink_EmitsControllerGiveWayLine_ForControllerHeldPair()
    {
        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        GroundEdge edge01 = layout.Edges[0];

        TaxiRoute routeHeld = MakeRoute(MakeSeg(0, 1, "A", edge01));
        TaxiRoute routeMover = MakeRoute(MakeSeg(0, 1, "A", edge01));

        AircraftState held = MakeAircraft("N123", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, taxiRoute: routeHeld, phase: new TaxiingPhase());
        held.Ground.Hold = HoldDirective.GiveWay("MOVER");

        AircraftState mover = MakeAircraft(
            "MOVER",
            new LatLon(BaseLat - OffsetLatPer100Ft, BaseLon),
            heading: 0,
            gs: 15,
            taxiRoute: routeMover,
            phase: new TaxiingPhase()
        );

        var captured = new System.Collections.Generic.List<string>();
        GroundConflictDetector.ApplySpeedLimits([held, mover], layout, deltaSeconds: 0, diagnosticLog: captured.Add);

        Assert.Contains(
            captured,
            line => line.StartsWith("[Pair] ControllerGiveWay ", System.StringComparison.Ordinal) && line.Contains("N123") && line.Contains("MOVER")
        );
    }

    /// <summary>
    /// Companion: HOLDPOSITION must NOT emit the ControllerGiveWay line — there is
    /// no yield relationship to surface, only an unconditional stop.
    /// </summary>
    [Fact]
    public void DebugSink_OmitsControllerGiveWayLine_ForHoldPosition()
    {
        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        GroundEdge edge01 = layout.Edges[0];

        TaxiRoute routeHeld = MakeRoute(MakeSeg(0, 1, "A", edge01));
        TaxiRoute routeMover = MakeRoute(MakeSeg(0, 1, "A", edge01));

        AircraftState held = MakeAircraft("N123", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, taxiRoute: routeHeld, phase: new TaxiingPhase());
        held.Ground.Hold = HoldDirective.HoldPosition;

        AircraftState mover = MakeAircraft(
            "MOVER",
            new LatLon(BaseLat - OffsetLatPer100Ft, BaseLon),
            heading: 0,
            gs: 15,
            taxiRoute: routeMover,
            phase: new TaxiingPhase()
        );

        var captured = new System.Collections.Generic.List<string>();
        GroundConflictDetector.ApplySpeedLimits([held, mover], layout, deltaSeconds: 0, diagnosticLog: captured.Add);

        Assert.DoesNotContain(captured, line => line.StartsWith("[Pair] ControllerGiveWay ", System.StringComparison.Ordinal));
        // The HoldPosition kind should still surface in the [Classify] line for N123.
        Assert.Contains(captured, line => line.StartsWith("[Classify] N123", System.StringComparison.Ordinal) && line.Contains("hold=HoldPosition"));
    }

    // -------------------------------------------------------------------------
    // Crossing resolution: one-holds-one-goes (no symmetric crawl / oscillation)
    // -------------------------------------------------------------------------

    /// <summary>
    /// An aircraft on the runway surface (here: a runway-exit aircraft still on the
    /// centerline) must not be made to yield to a plain taxiing crosser — it has
    /// priority to clear the runway environment without delay (AIM 4-3-21.a). The
    /// crosser yields instead.
    /// </summary>
    [Fact]
    public void Crossing_OnRunwayAircraft_HasPriority_TaxiingCrosserYields()
    {
        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        GroundEdge edge01 = layout.Edges[0];
        TaxiRoute routeCrosser = MakeRoute(MakeSeg(0, 1, "A", edge01));

        // Runway-exit aircraft at origin heading north, closing on a crosser ~90ft ahead.
        AircraftState exiting = MakeAircraft("EXIT", new LatLon(BaseLat, BaseLon), heading: 0, gs: 12, phase: new RunwayExitPhase());
        AircraftState crosser = MakeAircraft(
            "CROSS",
            new LatLon(BaseLat + 0.9 * OffsetLatPer100Ft, BaseLon),
            heading: 90,
            gs: 8,
            taxiRoute: routeCrosser,
            phase: new TaxiingPhase()
        );

        var aircraft = new List<AircraftState> { exiting, crosser };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout);

        Assert.True(
            exiting.Ground.SpeedLimit is null || exiting.Ground.SpeedLimit > 0,
            $"Runway-exit aircraft should proceed, got limit={exiting.Ground.SpeedLimit?.ToString("F1") ?? "null"}"
        );
        Assert.Equal(0.0, crosser.Ground.SpeedLimit);
    }

    /// <summary>
    /// A departure rolling on the runway has the same priority as an exiting one: the takeoff
    /// roll is never speed-limited for a taxiing crosser; the crosser holds.
    /// </summary>
    [Fact]
    public void Crossing_TakeoffRollAircraft_HasPriority_TaxiingCrosserYields()
    {
        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        GroundEdge edge01 = layout.Edges[0];
        TaxiRoute routeCrosser = MakeRoute(MakeSeg(0, 1, "A", edge01));

        AircraftState departing = MakeAircraft("DEP", new LatLon(BaseLat, BaseLon), heading: 0, gs: 40, phase: new TakeoffPhase());
        AircraftState crosser = MakeAircraft(
            "CROSS",
            new LatLon(BaseLat + 0.9 * OffsetLatPer100Ft, BaseLon),
            heading: 90,
            gs: 8,
            taxiRoute: routeCrosser,
            phase: new TaxiingPhase()
        );

        var aircraft = new List<AircraftState> { departing, crosser };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout);

        Assert.True(departing.Ground.SpeedLimit is null || departing.Ground.SpeedLimit > 0);
        Assert.Equal(0.0, crosser.Ground.SpeedLimit);
    }

    /// <summary>
    /// The self-pin oscillation fix: a route-less ground mover momentarily stopped
    /// at gs=0 but still pointed at a crosser ahead must KEEP its hold (stay pinned),
    /// not lose its limit (which would let it lurch forward and re-pin every other
    /// tick — the slow-motion crawl). A stopped aircraft pointed into a conflict is
    /// yielding, not free to accelerate.
    /// </summary>
    [Fact]
    public void Crossing_RoutelessStoppedMover_PointedAtCrosser_StaysPinned()
    {
        // "STOP" has no route and gs=0 but heading north, with a crosser ~70ft due
        // north heading east. STOP is closing (by heading) on the crosser → must hold.
        AircraftState stopped = MakeAircraft("STOP", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0);
        AircraftState crosser = MakeAircraft(
            "CROSS",
            new LatLon(BaseLat + 0.7 * OffsetLatPer100Ft, BaseLon),
            heading: 90,
            gs: 8,
            phase: new TaxiingPhase()
        );

        var aircraft = new List<AircraftState> { stopped, crosser };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Equal(0.0, stopped.Ground.SpeedLimit);
    }

    /// <summary>
    /// Two same-priority aircraft on a crossing collision course (both would have to
    /// stop for each other) must resolve to exactly one holder and one mover, not a
    /// symmetric double-stop. Holder is deterministic by callsign.
    /// </summary>
    [Fact]
    public void Crossing_MutualCollisionCourse_OneHoldsOneProceeds()
    {
        // AAA heading 030, ZZZ 50ft due north heading 150 — both closing, heading
        // difference 120° (not head-on), within stop distance.
        AircraftState aaa = MakeAircraft("AAA", new LatLon(BaseLat, BaseLon), heading: 30, gs: 10);
        AircraftState zzz = MakeAircraft("ZZZ", new LatLon(BaseLat + 0.5 * OffsetLatPer100Ft, BaseLon), heading: 150, gs: 10);

        var aircraft = new List<AircraftState> { aaa, zzz };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // Exactly one holds (deterministic: ZZZ sorts after AAA → ZZZ holds), the other proceeds.
        Assert.Equal(0.0, zzz.Ground.SpeedLimit);
        Assert.True(
            aaa.Ground.SpeedLimit is null || aaa.Ground.SpeedLimit > 0,
            $"AAA should proceed, got limit={aaa.Ground.SpeedLimit?.ToString("F1") ?? "null"}"
        );
    }

    /// <summary>
    /// Two aircraft passing on oblique crossing paths (headings ~130° apart, e.g. one
    /// exiting a runway toward its hold-short while another taxis to the apron) are NOT
    /// a head-on and must not both be stopped at range. A head-on requires near-anti-
    /// parallel headings; an oblique crossing resolves via the closing/arbitration rules
    /// (or, at this separation, no limit at all). Regression for the N569SX/N342T
    /// false-head-on gridlock.
    /// </summary>
    [Fact]
    public void Crossing_ObliqueOpposingHeadings_NotTreatedAsHeadOn()
    {
        // A heading 030, B 280ft due north heading 160 — 130° apart, both approaching,
        // inside the 300ft head-on range but beyond trail distance.
        AircraftState a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 30, gs: 10);
        AircraftState b = MakeAircraft("B", new LatLon(BaseLat + 2.8 * OffsetLatPer100Ft, BaseLon), heading: 160, gs: 10);

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // Not a head-on: neither is stopped at this separation.
        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(b.Ground.SpeedLimit);
    }

    // -------------------------------------------------------------------------
    // Converging merge: one-holds-one-goes (no mutual-stop deadlock at a merge)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Two departures converging on a shared node that begins a single shared lane (a taxiway
    /// merge), within stop distance of each other, must be sequenced one-at-a-time: the
    /// merge-order leader (nearer the shared node) proceeds while the other holds. Regression
    /// for the OAK U/W (node 17) JSX177-vs-SWA897 deadlock, where the convergence safety-net
    /// pinned BOTH aircraft to zero. Uses the real bundle geometry (node 17 plus both aircraft
    /// positions, ~110 ft apart, headings ~92° apart so both close on each other).
    /// </summary>
    [Fact]
    public void ConvergingMerge_WithinStopDistance_WinnerProceeds_YielderHolds()
    {
        TestVnasData.EnsureInitialized();
        if (!Yaat.Sim.Data.Faa.FaaAircraftDatabase.IsInitialized)
        {
            return;
        }

        // JSX177 (twy W, nearer node 17) and SWA897 (twy U, farther) both converge on node 17,
        // then share the lane onward (17 -> 676). Real lengths make the ~110 ft gap fall inside
        // the combined stop distance, so the current safety net pins both to zero.
        var node17 = new LatLon(37.706607311591235, -122.21819280404034);
        var layout = new AirportGroundLayout { AirportId = "OAK" };
        layout.Nodes[17] = new GroundNode
        {
            Id = 17,
            Position = node17,
            Type = GroundNodeType.TaxiwayIntersection,
        };

        var edge = new GroundEdge
        {
            Nodes = [layout.Nodes[17], layout.Nodes[17]],
            TaxiwayName = "W",
            DistanceNm = 110.0 / FtPerNm,
        };

        TaxiRoute winnerRoute = MakeRoute(MakeSeg(677, 17, "W", edge), MakeSeg(17, 676, "W", edge));
        TaxiRoute yielderRoute = MakeRoute(MakeSeg(679, 17, "U", edge), MakeSeg(17, 676, "W", edge));

        AircraftState winner = MakeAircraft(
            "JSX177",
            new LatLon(37.70671679458664, -122.21835798527978),
            heading: 129,
            gs: 8,
            taxiRoute: winnerRoute,
            phase: new TaxiingPhase()
        );
        AircraftState yielder = MakeAircraft(
            "SWA897",
            new LatLon(37.70679525153174, -122.21799088712922),
            heading: 221,
            gs: 8,
            taxiRoute: yielderRoute,
            phase: new TaxiingPhase()
        );

        GroundConflictDetector.ApplySpeedLimits([winner, yielder], layout);

        // The merge-order follower (farther from node 17) holds.
        Assert.Equal(0.0, yielder.Ground.SpeedLimit);
        // The merge-order leader (nearer node 17) must proceed, not deadlock at zero.
        Assert.True(
            winner.Ground.SpeedLimit is null || winner.Ground.SpeedLimit > 0,
            $"Merge-order leader (nearer node 17) must proceed, got limit={winner.Ground.SpeedLimit?.ToString("F1") ?? "null"}"
        );
    }

    /// <summary>
    /// Issue #224: a follower taxiing up behind a stationary lead on a merging TE lane at OAK
    /// must be the one HELD — never released "through" the lead. Two B738s merge onto TE lane
    /// 949→1: SWA863 (route starts at node 949) sits stationary having just been cleared to
    /// taxi; SWA1182 taxis down TE with SWA863 dead ahead (~1° off its nose). The merge node is
    /// SWA863's route-start (a from-node, invisible to convergence detection) and the current
    /// segments differ, so the pair classifies as Crossing and both compute a mutual
    /// proximity-stop. The mutual-stop tie-break must hold the follower (SWA1182) and let the
    /// lead (SWA863) proceed — not pick by callsign ordinal, which held SWA863 and released
    /// SWA1182 straight into it (7110.65 §3-7-2.a FOLLOW/BEHIND sequencing).
    ///
    /// Captured geometry (bundle t≈585): SWA863 37.709154/-122.214694 hdg 107.5;
    /// SWA1182 hdg 216, ~146 ft NE. The real 146 ft gap sits only in the trail band; the
    /// mutual-stop branch fires inside the ~135 ft two-B738 stop distance, so the test tightens
    /// the gap to 90 ft with SWA863 placed dead ahead on SWA1182's 216° nose.
    /// </summary>
    [Fact]
    public void Crossing_FollowerBehindStationaryLead_HoldsFollower_ReleasesLead()
    {
        TestVnasData.EnsureInitialized();

        // A throwaway edge so both aircraft classify as Taxiing (routes only need a current
        // segment; layout is passed null so the pair resolves via the Crossing path — matching
        // the real bug, where FindSharedUpcomingNode misses the route-start merge node).
        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        GroundEdge edge = layout.Edges[0];

        var swa863Pos = new LatLon(37.709154, -122.214694);
        // SWA1182 sits 90 ft from SWA863 on bearing 036°, so SWA863 is dead ahead on
        // SWA1182's 216° nose (216 = 036 + 180) while SWA1182 is ~71° off SWA863's nose.
        LatLon swa1182Pos = GeoMath.ProjectPoint(swa863Pos, new TrueHeading(36.0), 90.0 / FtPerNm);

        AircraftState swa863 = MakeAircraft(
            "SWA863",
            swa863Pos,
            heading: 107.5,
            gs: 0,
            taxiRoute: MakeRoute(MakeSeg(949, 1, "TE", edge)),
            phase: new TaxiingPhase()
        );
        AircraftState swa1182 = MakeAircraft(
            "SWA1182",
            swa1182Pos,
            heading: 216.0,
            gs: 8,
            taxiRoute: MakeRoute(MakeSeg(948, 949, "TE", edge)),
            phase: new TaxiingPhase()
        );

        GroundConflictDetector.ApplySpeedLimits([swa863, swa1182], null);

        // The follower (SWA863 dead ahead of it) holds; the lead (pointed away) proceeds.
        Assert.Equal(0.0, swa1182.Ground.SpeedLimit);
        Assert.True(
            swa863.Ground.SpeedLimit is null || swa863.Ground.SpeedLimit > 0,
            $"Lead SWA863 should proceed (SWA1182 is ~71° off its nose), got limit={swa863.Ground.SpeedLimit?.ToString("F1") ?? "null"}"
        );
    }

    // -------------------------------------------------------------------------
    // Mutual stops: which aircraft holds when both movers would stop for each other. The aircraft with the other nearly dead
    // ahead holds first, so a follower with traffic crossing on its nose stops for it; otherwise a FOLLOWG follower facing an
    // aircraft that is not following keeps its place in its chain and the other holds; else the callsign tie-break decides.
    // -------------------------------------------------------------------------

    /// <summary>The unrelated, route-less mover of the mutual-stop tests below: ordinal below <see cref="FollowerCallsign"/>.</summary>
    private const string OpposingCallsign = "N1OPP";

    /// <summary>
    /// A route-less C172 <c>N1OPP</c> <paramref name="aheadFt"/> ft ahead of <paramref name="follower"/> on its heading, facing it:
    /// a head-on pair whose two closing limits both sit at zero (the two-aircraft stop distance), so the pair reaches the mutual
    /// stop. <c>N1OPP</c> sorts below the follower's callsign, so the callsign tie-break would hold the follower.
    /// </summary>
    private static AircraftState OpposingHeadOn(KoakFollowGeometry.LeadRun run, AircraftState follower, double aheadFt) =>
        RouteLessOpponent(run, follower, aheadFt, offNoseDeg: 0.0, headingOffDeg: 180.0);

    /// <summary>
    /// A route-less C172 <c>N1OPP</c> <paramref name="aheadFt"/> ft from <paramref name="follower"/>, 10° off its nose and heading
    /// 110° off the bearing to it — 70° off the nose of the follower sitting dead ahead of it — an aircraft crossing ahead of the
    /// follower rather than meeting it head-on. Both closing limits still sit at zero, so the pair reaches the mutual stop.
    /// </summary>
    private static AircraftState OpposingCrossingAhead(KoakFollowGeometry.LeadRun run, AircraftState follower, double aheadFt) =>
        RouteLessOpponent(run, follower, aheadFt, offNoseDeg: 10.0, headingOffDeg: 110.0);

    /// <summary>
    /// A route-less C172 <c>N1OPP</c> <paramref name="aheadFt"/> ft from <paramref name="follower"/> on the bearing
    /// <paramref name="offNoseDeg"/> off its nose, heading <paramref name="headingOffDeg"/> off that bearing, rolling at 8 kt with
    /// no route: an Untracked mover, so the pair resolves as a Crossing.
    /// </summary>
    private static AircraftState RouteLessOpponent(
        KoakFollowGeometry.LeadRun run,
        AircraftState follower,
        double aheadFt,
        double offNoseDeg,
        double headingOffDeg
    )
    {
        double towardMoverDeg = follower.TrueHeading.Degrees + offNoseDeg;
        AircraftState mover = KoakFollowGeometry.Spawn(
            OpposingCallsign,
            "C172",
            GeoMath.ProjectPoint(follower.Position, new TrueHeading(towardMoverDeg), aheadFt / FtPerNm),
            new TrueHeading(towardMoverDeg + headingOffDeg)
        );
        mover.Phases = null;
        mover.IndicatedAirspeed = 8.0;
        mover.Ground.Layout = run.Layout;
        return mover;
    }

    /// <summary>Asserts the pair of the two callsigns was classified a Crossing, whichever order the pass listed it in.</summary>
    private static void AssertCrossingPair(List<string> log, string firstCallsign, string secondCallsign) =>
        Assert.Contains(
            log,
            line =>
                line.StartsWith("[Pair] ", StringComparison.Ordinal)
                && line.Contains($"{firstCallsign}(", StringComparison.Ordinal)
                && line.Contains($"{secondCallsign}(", StringComparison.Ordinal)
                && line.EndsWith("Crossing", StringComparison.Ordinal)
        );

    /// <summary>
    /// A mutual stop outside every lead chain: a <c>FOLLOWG</c> follower on KOAK's B and an unrelated route-less aircraft closing
    /// head-on on it. The follower keeps its place in its chain, so the other aircraft holds, in either list order.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MutualStop_FollowerAndNonFollower_NonFollowerHolds(bool moverFirst)
    {
        if (StartFollower(string.Empty) is not { } started)
        {
            return;
        }

        (KoakFollowGeometry.LeadRun run, AircraftState follower, FollowingPhase _) = started;
        AircraftState mover = OpposingHeadOn(run, follower, aheadFt: 90.0);
        List<AircraftState> pair = moverFirst ? [mover, follower] : [follower, mover];

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits(pair, run.Layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        AssertCrossingPair(log, FollowerCallsign, OpposingCallsign);
        Assert.True(
            (follower.Ground.SpeedLimit is null) || (follower.Ground.SpeedLimit > 0),
            $"the follower keeps its place in its chain, so it must not be the holder, got limit={follower.Ground.SpeedLimit}"
        );
        Assert.Equal(0.0, mover.Ground.SpeedLimit);
    }

    /// <summary>
    /// A mutual stop with the crossing aircraft 10° off the follower's nose, so it has the follower 70° off its own: the follower
    /// rule does not reach that geometry, and the aircraft with the other nearly dead ahead holds — the follower stops for the
    /// traffic in front of it instead of holding it mid-crossing.
    /// </summary>
    [Fact]
    public void MutualStop_FollowerWithCrossingTrafficDeadAhead_FollowerHolds()
    {
        if (StartFollower(string.Empty) is not { } started)
        {
            return;
        }

        (KoakFollowGeometry.LeadRun run, AircraftState follower, FollowingPhase _) = started;
        AircraftState crossing = OpposingCrossingAhead(run, follower, aheadFt: 80.0);

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([follower, crossing], run.Layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        AssertCrossingPair(log, FollowerCallsign, OpposingCallsign);
        Assert.Contains(log, line => line.Contains("[Crossing] mutual stop", StringComparison.Ordinal));
        Assert.Equal(0.0, follower.Ground.SpeedLimit);
        Assert.True(
            (crossing.Ground.SpeedLimit is null) || (crossing.Ground.SpeedLimit > 0),
            $"the aircraft nearly dead ahead of the follower must stop for it, got limit={crossing.Ground.SpeedLimit}"
        );
    }

    /// <summary>
    /// The same head-on Crossing geometry with the follower's <c>FOLLOWG</c> phase replaced by a plain taxiing one: neither is
    /// following, so the follower rule does not apply and the callsign tie-break holds the ordinal-higher callsign alone.
    /// </summary>
    [Fact]
    public void MutualStop_TwoPlainTaxiers_KeepTheCallsignTieBreak()
    {
        if (StartFollower(string.Empty) is not { } started)
        {
            return;
        }

        (KoakFollowGeometry.LeadRun run, AircraftState taxiing, FollowingPhase follow) = started;
        taxiing.Phases = new PhaseList();
        taxiing.Phases.Add(new TaxiingPhase());
        taxiing.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        taxiing.Ground.AssignedTaxiRoute = follow.FollowRoute;
        AircraftState mover = OpposingHeadOn(run, taxiing, aheadFt: 90.0);

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits([taxiing, mover], run.Layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        AssertCrossingPair(log, FollowerCallsign, OpposingCallsign);
        Assert.Equal(0.0, taxiing.Ground.SpeedLimit);
        Assert.True(
            (mover.Ground.SpeedLimit is null) || (mover.Ground.SpeedLimit > 0),
            $"the tie-break holds the ordinal-higher callsign alone, got mover limit={mover.Ground.SpeedLimit}"
        );
    }

    /// <summary>
    /// Two followers closing head-on, neither in the other's lead chain: the follower rule needs exactly one of the pair
    /// following, so it does not apply and the callsign tie-break decides — the ordinal-higher callsign holds, in either list
    /// order, where it is the first or the second of the pair.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MutualStop_TwoFollowers_CallsignTieBreakDecides(bool siblingFirst)
    {
        if (StartFollower(string.Empty) is not { } started)
        {
            return;
        }

        (KoakFollowGeometry.LeadRun run, AircraftState follower, FollowingPhase _) = started;
        AircraftState sibling = MakeAircraft(
            SiblingCallsign,
            GeoMath.ProjectPoint(follower.Position, follower.TrueHeading, 90.0 / FtPerNm),
            heading: follower.TrueHeading.Degrees + 180.0,
            gs: 8.0,
            phase: new FollowingPhase(LeadCallsign)
        );
        List<AircraftState> pair = siblingFirst ? [sibling, follower] : [follower, sibling];

        var log = new List<string>();
        GroundConflictDetector.ApplySpeedLimits(pair, run.Layout, 0, log.Add);
        log.ForEach(_output.WriteLine);

        AssertCrossingPair(log, FollowerCallsign, SiblingCallsign);
        Assert.Equal(0.0, sibling.Ground.SpeedLimit);
        Assert.True(
            (follower.Ground.SpeedLimit is null) || (follower.Ground.SpeedLimit > 0),
            $"the callsign tie-break holds one aircraft alone, got follower limit={follower.Ground.SpeedLimit}"
        );
    }

    // -------------------------------------------------------------------------
    // Parallel-track lateral room: two aircraft on neighbouring taxiways pass
    // each other instead of trailing or stopping (SFO ground control, taxiways
    // A and B, centrelines ~160 ft apart, passes bottoming out at ~238 ft).
    // -------------------------------------------------------------------------

    /// <summary>Track of the lane the subject aircraft of the parallel-pass tests taxis on.</summary>
    private const double ParallelTrackDeg = 118.0;

    /// <summary>The reciprocal of <see cref="ParallelTrackDeg"/> — the track of the aircraft coming the other way on the neighbouring lane.</summary>
    private const double ParallelReciprocalTrackDeg = 298.0;

    /// <summary>
    /// Lateral separation of the two lanes in the measured field pass: inside the two-B738 trail ring (254 ft) and
    /// the 300 ft head-on ring.
    /// </summary>
    private const double ParallelLateralFt = 238.0;

    /// <summary>Along-track offset between the two aircraft in the field pass — they are nearly abeam.</summary>
    private const double ParallelAlongFt = 50.0;

    /// <summary>Lateral separation too tight for two B738s to pass (their half-spans plus the wingtip buffer need 142.4 ft).</summary>
    private const double TooCloseLateralFt = 120.0;

    /// <summary>Convergence of the merging pair in the look-ahead tests: inside the tolerance, so the bypass still has to decide them.</summary>
    private const double ConvergingTrackDiffDeg = 15.0;

    /// <summary>Convergence of the crossing pair: 18°, off the tolerance's 20° edge but far enough apart to cross within the look-ahead.</summary>
    private const double CrossingTrackDiffDeg = 18.0;

    /// <summary>Lateral offset of the piston twin-lane pairs in the look-ahead tests: clear of the 61.1 ft two C172s need.</summary>
    private const double PistonLateralFt = 80.0;

    /// <summary>Nose wander of the leading aircraft in the route-segment test: inside its lane, but more than the pair's margin can spare.</summary>
    private const double NoseWanderDeg = 5.0;

    /// <summary>
    /// Lateral offset of the two B738 lanes in the nose-wander test: above their 142.4 ft requirement by less than
    /// the wander drifts.
    /// </summary>
    private const double NoseWanderLateralFt = 155.0;

    /// <summary>Along-track offset of the converging pair, which puts the two 89.4 ft apart — inside the 100 ft stop distance.</summary>
    private const double ConvergingAlongFt = 40.0;

    /// <summary>The FAA wingspan of an aircraft type, in feet.</summary>
    /// <param name="type">ICAO type designator.</param>
    /// <returns>Wingspan in feet.</returns>
    /// <exception cref="InvalidOperationException">The FAA database carries no dimensions for that type.</exception>
    private static double WingspanFt(string type) =>
        FaaAircraftDatabase.Get(type)?.WingspanFt ?? throw new InvalidOperationException($"the FAA database has no dimensions for '{type}'");

    /// <summary>
    /// A point <paramref name="alongFt"/> ahead of <paramref name="origin"/> along <paramref name="trackDeg"/> and
    /// <paramref name="lateralFt"/> to the right of that track.
    /// </summary>
    private static LatLon AlongAndAbeam(LatLon origin, double trackDeg, double alongFt, double lateralFt)
    {
        LatLon ahead = GeoMath.ProjectPoint(origin, new TrueHeading(trackDeg), alongFt / FtPerNm);
        return GeoMath.ProjectPoint(ahead, new TrueHeading((trackDeg + 90.0) % 360.0), lateralFt / FtPerNm);
    }

    /// <summary>
    /// An aircraft of an explicit type — <see cref="MakeAircraft"/> is hard-wired to a B738, and the wingspan pair is
    /// exactly what the lateral tests turn on.
    /// </summary>
    private static AircraftState MakeTypedAircraft(string callsign, string type, LatLon position, double heading, double gs) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = position,
            TrueHeading = new TrueHeading(heading),
            IsOnGround = true,
            IndicatedAirspeed = gs,
            Ground = new AircraftGroundOps(),
        };

    /// <summary>
    /// A single 600 ft route segment centred on <paramref name="laneCenter"/> and running along <paramref name="trackDeg"/>,
    /// with real node positions. Distinct node ids per lane keep the pair a Crossing (no shared upcoming node).
    /// </summary>
    private static TaxiRoute ParallelLaneRoute(LatLon laneCenter, double trackDeg, int fromId, int toId)
    {
        var from = new GroundNode
        {
            Id = fromId,
            Position = GeoMath.ProjectPoint(laneCenter, new TrueHeading((trackDeg + 180.0) % 360.0), 300.0 / FtPerNm),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var to = new GroundNode
        {
            Id = toId,
            Position = GeoMath.ProjectPoint(laneCenter, new TrueHeading(trackDeg), 300.0 / FtPerNm),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        return MakeRoute(MakeGeoSeg(from, to));
    }

    /// <summary>
    /// Two B738s passing in opposite directions on neighbouring taxiways, 238 ft apart laterally and nearly abeam.
    /// They are inside the trail ring (254 ft) and inside the 300 ft head-on ring, but they have far more lateral
    /// room than the 142.4 ft their half-spans plus the wingtip buffer need, so neither may be slowed or held —
    /// on the graph (one holds) or off it (both stop). The SFO taxiway A/B field case.
    /// </summary>
    [Fact]
    public void ParallelTaxiways_OppositeDirection_238ft_NoLimit()
    {
        var basePos = new LatLon(BaseLat, BaseLon);
        LatLon otherPos = AlongAndAbeam(basePos, ParallelTrackDeg, ParallelAlongFt, ParallelLateralFt);

        AircraftState a = MakeAircraft("AAA", basePos, heading: ParallelTrackDeg, gs: 20);
        AircraftState b = MakeAircraft("BBB", otherPos, heading: ParallelReciprocalTrackDeg, gs: 20);
        GroundConflictDetector.ApplySpeedLimits([a, b], null);

        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(b.Ground.SpeedLimit);

        // Same geometry with routes on two separate lanes. The layout only has to be non-null here: it is what makes
        // the pair's routes "known" to the crossing resolution, which then arbitrates the head-on instead of stopping both.
        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        AircraftState routedA = MakeAircraft(
            "AAA",
            basePos,
            heading: ParallelTrackDeg,
            gs: 20,
            taxiRoute: ParallelLaneRoute(basePos, ParallelTrackDeg, 10, 11),
            phase: new TaxiingPhase()
        );
        AircraftState routedB = MakeAircraft(
            "BBB",
            otherPos,
            heading: ParallelReciprocalTrackDeg,
            gs: 20,
            taxiRoute: ParallelLaneRoute(otherPos, ParallelReciprocalTrackDeg, 12, 13),
            phase: new TaxiingPhase()
        );
        GroundConflictDetector.ApplySpeedLimits([routedA, routedB], layout);

        Assert.Null(routedA.Ground.SpeedLimit);
        Assert.Null(routedB.Ground.SpeedLimit);
    }

    /// <summary>
    /// The same pass with the widest pair of the field case: an A359 against a B738 needs 189.9 ft and the lanes give
    /// 238 ft, so the wider wingspan still passes.
    /// </summary>
    [Fact]
    public void ParallelTaxiways_OppositeDirection_A359vsB738_238ft_NoLimit()
    {
        var basePos = new LatLon(BaseLat, BaseLon);
        LatLon otherPos = AlongAndAbeam(basePos, ParallelTrackDeg, ParallelAlongFt, ParallelLateralFt);

        double requiredFt = (WingspanFt("A359") / 2) + (WingspanFt("B738") / 2) + GroundOutlineSweep.WingtipBufferFt;
        Assert.True(
            requiredFt < ParallelLateralFt,
            $"test geometry: the pair needs {requiredFt:F1} ft and the lanes are {ParallelLateralFt:F0} ft apart"
        );

        AircraftState heavy = MakeTypedAircraft("THY9WC", "A359", basePos, ParallelTrackDeg, 20);
        AircraftState narrow = MakeTypedAircraft("WJA1508", "B738", otherPos, ParallelReciprocalTrackDeg, 20);
        GroundConflictDetector.ApplySpeedLimits([heavy, narrow], null);

        Assert.Null(heavy.Ground.SpeedLimit);
        Assert.Null(narrow.Ground.SpeedLimit);
    }

    /// <summary>Two B738s on neighbouring lanes going the same way, 238 ft apart laterally: a pass, not a trail.</summary>
    [Fact]
    public void ParallelTaxiways_SameDirection_NoLimit()
    {
        var basePos = new LatLon(BaseLat, BaseLon);
        LatLon otherPos = AlongAndAbeam(basePos, ParallelTrackDeg, ParallelAlongFt, ParallelLateralFt);

        AircraftState a = MakeAircraft("AAA", basePos, heading: ParallelTrackDeg, gs: 20);
        AircraftState b = MakeAircraft("BBB", otherPos, heading: ParallelTrackDeg, gs: 20);
        GroundConflictDetector.ApplySpeedLimits([a, b], null);

        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(b.Ground.SpeedLimit);
    }

    /// <summary>
    /// Parallel tracks with only 120 ft between them — less than the 142.4 ft two B738s need — is not a pass: the
    /// head-on rule still stops them (off the graph, both).
    /// </summary>
    [Fact]
    public void ParallelTaxiways_TooClose_StillLimited()
    {
        var basePos = new LatLon(BaseLat, BaseLon);
        LatLon otherPos = AlongAndAbeam(basePos, ParallelTrackDeg, ParallelAlongFt, TooCloseLateralFt);

        AircraftState a = MakeAircraft("AAA", basePos, heading: ParallelTrackDeg, gs: 20);
        AircraftState b = MakeAircraft("BBB", otherPos, heading: ParallelReciprocalTrackDeg, gs: 20);
        GroundConflictDetector.ApplySpeedLimits([a, b], null);

        Assert.Equal(0.0, a.Ground.SpeedLimit);
        Assert.Equal(0.0, b.Ground.SpeedLimit);
    }

    /// <summary>
    /// Two C172s merging into one lane — twin lanes converging <see cref="ConvergingTrackDiffDeg"/>° inside the
    /// tolerance, so the bypass has to decide them, but their offsets from each other's track decay as the lanes close.
    /// Those offsets are 80.0 ft and 87.6 ft, both clear of the 61.1 ft two pistons need, and the ~49 ft the lanes close
    /// in the 7.5 s either takes to brake to a stop from 15 kt (2 kt/s taxi decel) is the stopping margin already spent
    /// by the time the merge is close. The look-ahead reads the projected offset as inside the requirement and does not
    /// grant the bypass, so the distance rule governs: the two sit 89 ft apart, inside the 100 ft stop distance, and the
    /// converging aircraft stops rather than carrying on into the neighbouring lane.
    /// </summary>
    [Fact]
    public void ParallelTaxiways_ConvergingPistonsMergingLanes_LoseTheBypassWhileStoppingRoomRemains()
    {
        var basePos = new LatLon(BaseLat, BaseLon);
        LatLon otherPos = AlongAndAbeam(basePos, ParallelTrackDeg, ConvergingAlongFt, PistonLateralFt);

        double requiredFt = WingspanFt("C172") + GroundOutlineSweep.WingtipBufferFt;
        Assert.True(
            requiredFt < PistonLateralFt,
            $"test geometry: the pair needs {requiredFt:F1} ft and the twin lanes are {PistonLateralFt:F0} ft apart"
        );

        AircraftState a = MakeTypedAircraft("N123AA", "C172", basePos, ParallelTrackDeg, 15);
        AircraftState b = MakeTypedAircraft("N456BB", "C172", otherPos, ParallelTrackDeg - ConvergingTrackDiffDeg, 15);
        GroundConflictDetector.ApplySpeedLimits([a, b], null);

        Assert.Equal(0.0, a.Ground.SpeedLimit);
        Assert.Null(b.Ground.SpeedLimit);
    }

    /// <summary>
    /// The same two C172s at the same 80 ft offset on exactly parallel tracks, the follower closing along-track: an
    /// offset that does not decay is clear however far ahead it is projected, so the bypass stands and neither is
    /// limited. This is the pass the bypass exists for.
    /// </summary>
    [Fact]
    public void ParallelTaxiways_ExactlyParallelPistons_KeepTheBypass()
    {
        var basePos = new LatLon(BaseLat, BaseLon);
        LatLon otherPos = AlongAndAbeam(basePos, ParallelTrackDeg, ParallelAlongFt, PistonLateralFt);

        AircraftState a = MakeTypedAircraft("N123AA", "C172", basePos, ParallelTrackDeg, 15);
        AircraftState b = MakeTypedAircraft("N456BB", "C172", otherPos, ParallelTrackDeg, 10);
        GroundConflictDetector.ApplySpeedLimits([a, b], null);

        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(b.Ground.SpeedLimit);
    }

    /// <summary>
    /// Two C172s whose lanes cross at <see cref="CrossingTrackDiffDeg"/>° at 25 kt — inside the tolerance, so the bypass
    /// has to decide them. Each offset clears the 61.1 ft two pistons need at both ends of the look-ahead: 80.0 ft /
    /// 88.4 ft now and 76.5 ft / 68.1 ft at the capped 12 s, and B runs through A's track line 80 / sin 18° = 259 ft
    /// along its own path, inside the 506 ft the horizon projects. A look-ahead that read only magnitudes would call
    /// both ends a pass and grant the bypass; the signed offsets see each cross the other's line inside the horizon and
    /// deny it, and the distance rule stops the crossing aircraft where the two are 89 ft apart.
    /// </summary>
    [Fact]
    public void ParallelTaxiways_ConvergingPistonsThatCrossInsideTheHorizon_LoseTheBypass()
    {
        const double speedKts = 25.0;
        const double stopSeconds = speedKts / 2.0; // 2 kt/s piston brake rate (CategoryPerformance.TaxiDecelRate)
        double horizonFt = Math.Min(stopSeconds, GroundConflictDetector.ParallelTrackLookAheadCapSeconds) * speedKts * FtPerNm / 3600.0;
        var basePos = new LatLon(BaseLat, BaseLon);
        LatLon otherPos = AlongAndAbeam(basePos, ParallelTrackDeg, ConvergingAlongFt, PistonLateralFt);

        double requiredFt = WingspanFt("C172") + GroundOutlineSweep.WingtipBufferFt;
        double crossingFt = PistonLateralFt / Math.Sin(CrossingTrackDiffDeg * Math.PI / 180.0);
        double farSideFt = (horizonFt * Math.Sin(CrossingTrackDiffDeg * Math.PI / 180.0)) - PistonLateralFt;
        Assert.True(crossingFt < horizonFt, $"test geometry: the pair crosses at {crossingFt:F0} ft along, inside the {horizonFt:F0} ft horizon");
        Assert.True(
            farSideFt > requiredFt,
            $"test geometry: the crossing aircraft ends up {farSideFt:F0} ft out on the far side, clear of the {requiredFt:F1} ft requirement"
        );

        AircraftState a = MakeTypedAircraft("N123AA", "C172", basePos, ParallelTrackDeg, speedKts);
        AircraftState b = MakeTypedAircraft("N456BB", "C172", otherPos, ParallelTrackDeg - CrossingTrackDiffDeg, speedKts);
        GroundConflictDetector.ApplySpeedLimits([a, b], null);

        Assert.Equal(0.0, a.Ground.SpeedLimit);
        Assert.Null(b.Ground.SpeedLimit);
    }

    /// <summary>
    /// The field-pass lanes with the second aircraft heading 15° <em>away</em> from the first: the offsets only grow, so
    /// both signs hold and the bypass stands. A pair that separates is a pass however close it starts.
    /// </summary>
    [Fact]
    public void ParallelTaxiways_DivergingPair_KeepsTheBypass()
    {
        var basePos = new LatLon(BaseLat, BaseLon);
        LatLon otherPos = AlongAndAbeam(basePos, ParallelTrackDeg, ParallelAlongFt, ParallelLateralFt);

        AircraftState a = MakeAircraft("AAA", basePos, heading: ParallelTrackDeg, gs: 20);
        AircraftState b = MakeAircraft("BBB", otherPos, heading: ParallelTrackDeg + ConvergingTrackDiffDeg, gs: 20);
        GroundConflictDetector.ApplySpeedLimits([a, b], null);

        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(b.Ground.SpeedLimit);
    }

    /// <summary>
    /// Two pistons nose to nose on neighbouring lanes 80 ft apart and inside the 300 ft head-on ring: two C172s need
    /// 61.1 ft, so the bypass has to stand the head-on rule down. Off the graph that rule stops both, so this is the
    /// guard that the anti-parallel pass still reaches it.
    /// </summary>
    [Fact]
    public void ParallelTaxiways_AntiParallelTwinLanes_KeepTheBypass()
    {
        var basePos = new LatLon(BaseLat, BaseLon);
        LatLon otherPos = AlongAndAbeam(basePos, ParallelTrackDeg, ParallelAlongFt, PistonLateralFt);

        AircraftState a = MakeTypedAircraft("N123AA", "C172", basePos, ParallelTrackDeg, 15);
        AircraftState b = MakeTypedAircraft("N456BB", "C172", otherPos, ParallelReciprocalTrackDeg, 15);
        GroundConflictDetector.ApplySpeedLimits([a, b], null);

        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(b.Ground.SpeedLimit);
    }

    /// <summary>
    /// Two B738s on parallel route segments close enough that a few degrees of nose wander spends the pair's margin
    /// over the look-ahead, with only the leading aircraft's nose wandered <see cref="NoseWanderDeg"/>° toward the
    /// other. Each projection follows its own route segment (both lanes run along <see cref="ParallelTrackDeg"/>), so
    /// the wander costs nothing and the bypass stands. Projecting along the noses instead leaves 138.3 ft at the far
    /// end against 142.4 ft needed, which denies the bypass and limits the wanderer 163 ft from its neighbour.
    /// </summary>
    [Fact]
    public void ParallelTaxiways_NoseWanderOnStraightRouteSegments_KeepsTheBypass()
    {
        const double speedKts = 20.0;
        const double jetBrakeKts = 5.0; // CategoryPerformance.TaxiDecelRate for a jet
        double horizonFt = Math.Min(speedKts / jetBrakeKts, GroundConflictDetector.ParallelTrackLookAheadCapSeconds) * speedKts * FtPerNm / 3600.0;
        var basePos = new LatLon(BaseLat, BaseLon);
        LatLon otherPos = AlongAndAbeam(basePos, ParallelTrackDeg, ParallelAlongFt, NoseWanderLateralFt);

        // The two measurements the detector makes on the current positions: the lane itself, and the one along the
        // wandered nose that ComputeClosingLimit's lateral test takes. Both have to clear the requirement, and the
        // second is the tight one the wander eats into; projecting along the nose instead is what falls inside it.
        double requiredFt = WingspanFt("B738") + GroundOutlineSweep.WingtipBufferFt;
        double wanderRad = NoseWanderDeg * Math.PI / 180.0;
        double laneDistFt = Math.Sqrt((ParallelAlongFt * ParallelAlongFt) + (NoseWanderLateralFt * NoseWanderLateralFt));
        double noseTiltedFt = laneDistFt * Math.Sin(Math.Atan2(NoseWanderLateralFt, ParallelAlongFt) - wanderRad);
        double acrossFt = NoseWanderLateralFt - (horizonFt * Math.Sin(wanderRad));
        double alongFt = ParallelAlongFt + horizonFt - (horizonFt * Math.Cos(wanderRad));
        double noseProjectedFt = Math.Sqrt((acrossFt * acrossFt) + (alongFt * alongFt)) * Math.Sin(Math.Atan2(acrossFt, alongFt) - wanderRad);
        Assert.True(
            noseTiltedFt > requiredFt,
            $"test geometry: the lanes give {noseTiltedFt:F1} ft of room against the {requiredFt:F1} ft two B738s need"
        );
        Assert.True(
            noseProjectedFt < requiredFt,
            $"test geometry: projecting along the wandered nose leaves {noseProjectedFt:F1} ft, inside the {requiredFt:F1} ft requirement"
        );

        AircraftState a = MakeAircraft(
            "AAA",
            basePos,
            heading: ParallelTrackDeg + NoseWanderDeg,
            gs: 20,
            taxiRoute: ParallelLaneRoute(basePos, ParallelTrackDeg, 10, 11),
            phase: new TaxiingPhase()
        );
        AircraftState b = MakeAircraft(
            "BBB",
            otherPos,
            heading: ParallelTrackDeg,
            gs: 20,
            taxiRoute: ParallelLaneRoute(otherPos, ParallelTrackDeg, 12, 13),
            phase: new TaxiingPhase()
        );
        GroundConflictDetector.ApplySpeedLimits([a, b], null);

        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(b.Ground.SpeedLimit);
    }

    /// <summary>
    /// An obstacle crossing from the side (90° off the mover's track) at the same 238 ft is not a parallel-track pass:
    /// the distance rule still governs it and the mover keeps its trail limit.
    /// </summary>
    [Fact]
    public void CrossingFromSide_238ft_StillLimited()
    {
        var basePos = new LatLon(BaseLat, BaseLon);
        LatLon otherPos = AlongAndAbeam(basePos, ParallelTrackDeg, ParallelAlongFt, ParallelLateralFt);

        AircraftState mover = MakeAircraft("AAA", basePos, heading: ParallelTrackDeg, gs: 20);
        AircraftState crosser = MakeAircraft("BBB", otherPos, heading: (ParallelTrackDeg + 90.0) % 360.0, gs: 20);
        GroundConflictDetector.ApplySpeedLimits([mover, crosser], null);

        Assert.NotNull(mover.Ground.SpeedLimit);
    }

    /// <summary>
    /// Two aircraft nose to nose on ONE taxiway have no lateral room at all, so the same-edge head-on rule still holds
    /// one of them: the parallel-track bypass must not reach this branch.
    /// </summary>
    [Fact]
    public void SameEdgeHeadOn_StillHolds()
    {
        (AirportGroundLayout? layout, GroundNode _, GroundNode _, GroundNode _) = BuildSimpleLayout();
        GroundEdge edge01 = layout.Edges[0];

        AircraftState north = MakeAircraft(
            "NORTH",
            new LatLon(BaseLat, BaseLon),
            heading: 0,
            gs: 12,
            taxiRoute: MakeRoute(MakeSeg(0, 1, "A", edge01)),
            phase: new TaxiingPhase()
        );
        AircraftState south = MakeAircraft(
            "SOUTH",
            new LatLon(BaseLat + 2.5 * OffsetLatPer100Ft, BaseLon),
            heading: 180,
            gs: 12,
            taxiRoute: MakeRoute(MakeSeg(1, 0, "A", edge01)),
            phase: new TaxiingPhase()
        );

        GroundConflictDetector.ApplySpeedLimits([north, south], layout);

        // Equal remaining route → callsign tie-break holds SOUTH; NORTH proceeds.
        Assert.Equal(0.0, south.Ground.SpeedLimit);
        Assert.True(
            north.Ground.SpeedLimit is null || north.Ground.SpeedLimit > 0,
            $"NORTH should proceed, got limit={north.Ground.SpeedLimit?.ToString("F1") ?? "null"}"
        );
    }
}
