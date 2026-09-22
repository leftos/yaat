using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

[Collection("GroundConflictDebugSink")]
public class GroundConflictDetectorTests
{
    /// <summary>
    /// Pins the shared aircraft-performance singletons before any test body runs: the detector reads
    /// wingspans and lengths out of them, and a class racing another one's initialization reads the
    /// default-fallback dimensions for one call and the loaded ones for the next.
    /// </summary>
    public GroundConflictDetectorTests()
    {
        TestVnasData.EnsureInitialized();
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
    /// The separation at which the detector pins this pair to a stop: the two half-lengths plus
    /// <see cref="GroundConflictDetector.StopBufferFt"/>, floored at
    /// <see cref="GroundConflictDetector.DefaultStopDistanceFt"/> — the same arithmetic the detector's
    /// <c>GetSeparation</c> does, over the same FAA dimensions, rather than a copied number that drifts.
    /// </summary>
    private static double ParkedNeighborStopRingFt =>
        Math.Max(
            GroundConflictDetector.DefaultStopDistanceFt,
            ((LengthFt("B738") + LengthFt(ParkedNeighborMoverType)) / 2) + GroundConflictDetector.StopBufferFt
        );

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
    // Parallel-track lateral room: two aircraft on neighbouring taxiways pass
    // each other instead of trailing or stopping (SFO ground control, taxiways
    // A and B, centrelines ~160 ft apart, passes bottoming out at ~238 ft).
    // -------------------------------------------------------------------------

    /// <summary>Track of the lane the subject aircraft of the parallel-pass tests taxis on.</summary>
    private const double ParallelTrackDeg = 118.0;

    /// <summary>The reciprocal of <see cref="ParallelTrackDeg"/> — the track of the aircraft coming the other way on the neighbouring lane.</summary>
    private const double ParallelReciprocalTrackDeg = 298.0;

    /// <summary>Lateral separation of the two lanes in the measured field pass: inside the two-B738 trail ring (254 ft) and the 300 ft head-on ring.</summary>
    private const double ParallelLateralFt = 238.0;

    /// <summary>Along-track offset between the two aircraft in the field pass — they are nearly abeam.</summary>
    private const double ParallelAlongFt = 50.0;

    /// <summary>Lateral separation too tight for two B738s to pass (their half-spans plus the wingtip buffer need 142.4 ft).</summary>
    private const double TooCloseLateralFt = 120.0;

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

        double requiredFt = (WingspanFt("A359") / 2) + (WingspanFt("B738") / 2) + GroundConflictDetector.WingtipBufferFt;
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
