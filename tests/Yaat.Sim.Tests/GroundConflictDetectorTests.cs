using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

public class GroundConflictDetectorTests
{
    private const double FtPerNm = 6076.12;

    // Two points ~150 ft apart along a north-south line at KSFO
    private const double BaseLat = 37.620;
    private const double BaseLon = -122.380;
    private const double OffsetLatPer100Ft = 100.0 / FtPerNm / 60.0; // ~100ft in lat degrees

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

    private static TaxiRoute MakeRoute(params TaxiRouteSegment[] segments)
    {
        return new TaxiRoute
        {
            Segments = [.. segments],
            HoldShortPoints = [],
            CurrentSegmentIndex = 0,
        };
    }

    private static TaxiRouteSegment MakeSeg(int from, int to, string taxiway, GroundEdge edge)
    {
        var fromNode = new GroundNode
        {
            Id = from,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var toNode = new GroundNode
        {
            Id = to,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        return new TaxiRouteSegment { TaxiwayName = taxiway, Edge = edge.Directed(fromNode, toNode) };
    }

    [Fact]
    public void TwoTaxiing_SameEdgeSameDirection_TrailerGetsSpeedLimit()
    {
        var (layout, _, _, _) = BuildSimpleLayout();
        var edge01 = layout.Edges[0];

        // Both on edge 0→1, heading north, A is behind B (further from node 1)
        var routeA = MakeRoute(MakeSeg(0, 1, "A", edge01));
        var routeB = MakeRoute(MakeSeg(0, 1, "A", edge01));

        // A at node 0, B 150ft ahead
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        var b = MakeAircraft(
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
        var (layout, n0, n1, _) = BuildConvergenceLayout();

        var routeA = MakeRoute(MakeSeg(0, 2, "A", layout.Edges[0]));
        var routeB = MakeRoute(MakeSeg(1, 2, "B", layout.Edges[1]));

        // A is further from N2 than B
        var a = MakeAircraft("A", n0.Position, heading: 45, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        var b = MakeAircraft("B", n1.Position, heading: 315, gs: 15, taxiRoute: routeB, phase: new TaxiingPhase());

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
        var (layout, n0, n1, _) = BuildAsymmetricConvergenceLayout();

        var routeA = MakeRoute(MakeSeg(0, 2, "A", layout.Edges[0]));
        var routeB = MakeRoute(MakeSeg(1, 2, "B", layout.Edges[1]));

        // A (yielder) is ~1000 ft from the shared node; B (winner) is ~150 ft and moving fast, so it
        // clears the node well before A arrives. A must not be slowed.
        var a = MakeAircraft("A", n0.Position, heading: 0, gs: 8, taxiRoute: routeA, phase: new TaxiingPhase());
        var b = MakeAircraft("B", n1.Position, heading: 180, gs: 25, taxiRoute: routeB, phase: new TaxiingPhase());

        GroundConflictDetector.ApplySpeedLimits([a, b], layout);

        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(a.Ground.AutoYieldTarget);
    }

    [Fact]
    public void Convergence_NearStoppedWinner_FartherStillSlows()
    {
        var (layout, n0, n1, _) = BuildAsymmetricConvergenceLayout();

        var routeA = MakeRoute(MakeSeg(0, 2, "A", layout.Edges[0]));
        var routeB = MakeRoute(MakeSeg(1, 2, "B", layout.Edges[1]));

        // The nearer aircraft B is essentially stopped (<= 3 kt), so it cannot be trusted to clear
        // the node first — the gate keeps the slowdown and the farther aircraft A yields.
        var a = MakeAircraft("A", n0.Position, heading: 0, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        var b = MakeAircraft("B", n1.Position, heading: 180, gs: 2, taxiRoute: routeB, phase: new TaxiingPhase());

        GroundConflictDetector.ApplySpeedLimits([a, b], layout);

        Assert.NotNull(a.Ground.SpeedLimit);
    }

    [Fact]
    public void Convergence_AnnotatesYielderWithAutoYieldTarget()
    {
        var (layout, n0, n1, _) = BuildConvergenceLayout();
        var routeA = MakeRoute(MakeSeg(0, 2, "A", layout.Edges[0]));
        var routeB = MakeRoute(MakeSeg(1, 2, "B", layout.Edges[1]));
        var a = MakeAircraft("A", n0.Position, heading: 45, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        var b = MakeAircraft("B", n1.Position, heading: 315, gs: 15, taxiRoute: routeB, phase: new TaxiingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout);

        // The yielder is the speed-limited one; it carries the auto-yield annotation
        // pointing at the winner. The winner carries none.
        var yielder = a.Ground.SpeedLimit is not null ? a : b;
        var winner = ReferenceEquals(yielder, a) ? b : a;
        Assert.Equal(winner.Callsign, yielder.Ground.AutoYieldTarget);
        Assert.False(yielder.Ground.AutoYieldIsFollowing); // converging give-way, not in-trail follow
        Assert.Null(winner.Ground.AutoYieldTarget);
    }

    [Fact]
    public void SameEdgeTrailing_AnnotatesTrailerWithAutoYieldTarget()
    {
        var (layout, _, _, _) = BuildSimpleLayout();
        var edge01 = layout.Edges[0];
        var routeA = MakeRoute(MakeSeg(0, 1, "A", edge01));
        var routeB = MakeRoute(MakeSeg(0, 1, "A", edge01));
        // A behind, B ahead — A trails B on the shared edge.
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        var b = MakeAircraft(
            "B",
            new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon),
            heading: 0,
            gs: 10,
            taxiRoute: routeB,
            phase: new TaxiingPhase()
        );

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout);

        var trailer = a.Ground.SpeedLimit is not null ? a : b;
        var leader = ReferenceEquals(trailer, a) ? b : a;
        Assert.Equal(leader.Callsign, trailer.Ground.AutoYieldTarget);
        Assert.True(trailer.Ground.AutoYieldIsFollowing); // in-trail follow, not converging give-way
        Assert.Null(leader.Ground.AutoYieldTarget);
    }

    [Fact]
    public void NoConflict_ClearsStaleAutoYieldTarget()
    {
        // Two aircraft well outside SearchRangeNm — no pair is formed, and the per-tick
        // reset must clear any stale annotation.
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);
        var b = MakeAircraft("B", new LatLon(BaseLat + 1.0, BaseLon), heading: 0, gs: 15);
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
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);
        var b = MakeAircraft("B", new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon), heading: 90, gs: 0, phase: new AtParkingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // A should be limited (closing on stationary B)
        Assert.NotNull(a.Ground.SpeedLimit);
        // B is stationary — no limit needed
        Assert.Null(b.Ground.SpeedLimit);
    }

    [Fact]
    public void StationaryNearMoving_NotInPath_NoLimit()
    {
        // A is moving east, B is stationary to the north (not in A's path)
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 90, gs: 15);
        var b = MakeAircraft("B", new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 0, phase: new AtParkingPhase());

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
        var a = MakeAircraft("A", new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);
        a.Phases = new PhaseList();
        a.Phases.Add(new PushbackPhase());
        a.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        var b = MakeAircraft("B", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);

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
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);
        a.Phases = new PhaseList();
        a.Phases.Add(new PushbackPhase());
        a.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        var b = MakeAircraft("B", new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 0, phase: new AtParkingPhase());

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
        var a = MakeAircraft("A", new LatLon(BaseLat + 1.8 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);
        a.Phases = new PhaseList();
        a.Phases.Add(new PushbackPhase());
        a.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        var b = MakeAircraft("B", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, phase: new AtParkingPhase());

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
        var a = MakeAircraft("A", new LatLon(BaseLat + 1.2 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);
        a.Phases = new PhaseList();
        a.Phases.Add(new PushbackPhase());
        a.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        var b = MakeAircraft("B", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, phase: new AtParkingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.NotNull(a.Ground.SpeedLimit);
        Assert.Equal(0.0, a.Ground.SpeedLimit!.Value);
    }

    [Fact]
    public void TwoMoving_HeadOn_NoLayout_BothStop()
    {
        // A heading north, B heading south, 250ft apart, both moving
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);
        var b = MakeAircraft("B", new LatLon(BaseLat + 2.5 * OffsetLatPer100Ft, BaseLon), heading: 180, gs: 15);

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
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15, phase: followPhase);
        var b = MakeAircraft("B", new LatLon(BaseLat + 0.5 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 10);

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // A is following — should NOT get a speed limit from the detector
        Assert.Null(a.Ground.SpeedLimit);
    }

    [Fact]
    public void AircraftFarApart_NoInteraction()
    {
        // A and B are 1nm apart (well beyond SearchRangeNm=0.1)
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);
        var b = MakeAircraft("B", new LatLon(BaseLat + 1.0 / 60.0, BaseLon), heading: 180, gs: 15);

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(b.Ground.SpeedLimit);
    }

    [Fact]
    public void BothStationary_NoLimitsSet()
    {
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, phase: new AtParkingPhase());
        var b = MakeAircraft("B", new LatLon(BaseLat + 0.5 * OffsetLatPer100Ft, BaseLon), heading: 180, gs: 0, phase: new AtParkingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Null(a.Ground.SpeedLimit);
        Assert.Null(b.Ground.SpeedLimit);
    }

    [Fact]
    public void SingleAircraftOnGround_NoLimitsSet()
    {
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);

        var aircraft = new List<AircraftState> { a };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Null(a.Ground.SpeedLimit);
    }

    [Fact]
    public void IsClearOf_PushingReference_OutsideBuffer_ReturnsTrue()
    {
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);
        var b = MakeAircraft("B", new LatLon(BaseLat + 5 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);

        Assert.True(GroundConflictDetector.IsClearOf(a, b, null));
    }

    [Fact]
    public void IsClearOf_PushingReference_InsideBuffer_ReturnsFalse()
    {
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);
        var b = MakeAircraft("B", new LatLon(BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon), heading: 0, gs: 3, pushbackHeading: 180);

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
        var (layout, _, _, _) = BuildSimpleLayout();
        var edge01 = layout.Edges[0];

        var routeA = MakeRoute(MakeSeg(0, 1, "A", edge01));
        var routeB = MakeRoute(MakeSeg(0, 1, "A", edge01));

        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        var b = MakeAircraft(
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
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15);
        a.Ground.ConflictBreakRemainingSeconds = 15.0;

        var aircraft = new List<AircraftState> { a };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null, deltaSeconds: 1.0);

        Assert.Equal(14.0, a.Ground.ConflictBreakRemainingSeconds, precision: 9);
    }

    [Fact]
    public void Break_TimerExpired_ConflictsResume()
    {
        // Same setup as Break_AircraftExempt test, but timer is at zero.
        var (layout, _, _, _) = BuildSimpleLayout();
        var edge01 = layout.Edges[0];

        var routeA = MakeRoute(MakeSeg(0, 1, "A", edge01));
        var routeB = MakeRoute(MakeSeg(0, 1, "A", edge01));

        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 0, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        var b = MakeAircraft(
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
        };
        leader.Phases = new PhaseList();
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
        var (layout, _, _, _) = BuildSimpleLayout();
        var edge01 = layout.Edges[0];

        var routeHeld = MakeRoute(MakeSeg(0, 1, "A", edge01));
        var routeMover = MakeRoute(MakeSeg(0, 1, "A", edge01));

        // Held aircraft sits at origin, heading north, with a route. Hold=GiveWay
        // simulates GIVEWAY — the route is assigned but the aircraft is parked
        // until the resume geometry fires.
        var held = MakeAircraft("HELD", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, taxiRoute: routeHeld, phase: new TaxiingPhase());
        held.Ground.Hold = HoldDirective.GiveWay("MOVER");

        // Mover is 100ft south and 200ft east, heading north. B738 wingspan ~117 ft
        // → required lateral = 117/2 + 117/2 + 25 = 142 ft. The 200ft offset
        // clears this, so the bypass should let the mover pass at speed.
        const double OffsetLonPer100Ft = 100.0 / FtPerNm / 60.0; // approx; longitude scales with cos(lat) but at 37° the error is small
        var mover = MakeAircraft(
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
        var (layout, _, _, _) = BuildSimpleLayout();
        var edge01 = layout.Edges[0];

        var routeHeld = MakeRoute(MakeSeg(0, 1, "A", edge01));
        var routeMover = MakeRoute(MakeSeg(0, 1, "A", edge01));

        var held = MakeAircraft("HELD", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, taxiRoute: routeHeld, phase: new TaxiingPhase());
        held.Ground.Hold = HoldDirective.HoldPosition;

        // Mover 100ft south, same longitude — directly behind the held aircraft.
        var mover = MakeAircraft(
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
    /// Diagnostic enrichment for GIVEWAY relationships. When the controller has
    /// said "N123, give way to MOVER" and both aircraft are in the conflict
    /// detector's search range, the DebugSink should emit a
    /// "[Pair] ControllerGiveWay N123→MOVER" line so the operator sees the
    /// intent-bearing relationship instead of an anonymous "Stationary" pair.
    /// </summary>
    [Fact]
    public void DebugSink_EmitsControllerGiveWayLine_ForControllerHeldPair()
    {
        var (layout, _, _, _) = BuildSimpleLayout();
        var edge01 = layout.Edges[0];

        var routeHeld = MakeRoute(MakeSeg(0, 1, "A", edge01));
        var routeMover = MakeRoute(MakeSeg(0, 1, "A", edge01));

        var held = MakeAircraft("N123", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, taxiRoute: routeHeld, phase: new TaxiingPhase());
        held.Ground.Hold = HoldDirective.GiveWay("MOVER");

        var mover = MakeAircraft(
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
        var (layout, _, _, _) = BuildSimpleLayout();
        var edge01 = layout.Edges[0];

        var routeHeld = MakeRoute(MakeSeg(0, 1, "A", edge01));
        var routeMover = MakeRoute(MakeSeg(0, 1, "A", edge01));

        var held = MakeAircraft("N123", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0, taxiRoute: routeHeld, phase: new TaxiingPhase());
        held.Ground.Hold = HoldDirective.HoldPosition;

        var mover = MakeAircraft(
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
        var (layout, _, _, _) = BuildSimpleLayout();
        var edge01 = layout.Edges[0];
        var routeCrosser = MakeRoute(MakeSeg(0, 1, "A", edge01));

        // Runway-exit aircraft at origin heading north, closing on a crosser ~90ft ahead.
        var exiting = MakeAircraft("EXIT", new LatLon(BaseLat, BaseLon), heading: 0, gs: 12, phase: new RunwayExitPhase());
        var crosser = MakeAircraft(
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
        var stopped = MakeAircraft("STOP", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0);
        var crosser = MakeAircraft("CROSS", new LatLon(BaseLat + 0.7 * OffsetLatPer100Ft, BaseLon), heading: 90, gs: 8, phase: new TaxiingPhase());

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
        var aaa = MakeAircraft("AAA", new LatLon(BaseLat, BaseLon), heading: 30, gs: 10);
        var zzz = MakeAircraft("ZZZ", new LatLon(BaseLat + 0.5 * OffsetLatPer100Ft, BaseLon), heading: 150, gs: 10);

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
        var a = MakeAircraft("A", new LatLon(BaseLat, BaseLon), heading: 30, gs: 10);
        var b = MakeAircraft("B", new LatLon(BaseLat + 2.8 * OffsetLatPer100Ft, BaseLon), heading: 160, gs: 10);

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

        var winnerRoute = MakeRoute(MakeSeg(677, 17, "W", edge), MakeSeg(17, 676, "W", edge));
        var yielderRoute = MakeRoute(MakeSeg(679, 17, "U", edge), MakeSeg(17, 676, "W", edge));

        var winner = MakeAircraft(
            "JSX177",
            new LatLon(37.70671679458664, -122.21835798527978),
            heading: 129,
            gs: 8,
            taxiRoute: winnerRoute,
            phase: new TaxiingPhase()
        );
        var yielder = MakeAircraft(
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
        var (layout, _, _, _) = BuildSimpleLayout();
        var edge = layout.Edges[0];

        var swa863Pos = new LatLon(37.709154, -122.214694);
        // SWA1182 sits 90 ft from SWA863 on bearing 036°, so SWA863 is dead ahead on
        // SWA1182's 216° nose (216 = 036 + 180) while SWA1182 is ~71° off SWA863's nose.
        var swa1182Pos = GeoMath.ProjectPoint(swa863Pos, new TrueHeading(36.0), 90.0 / FtPerNm);

        var swa863 = MakeAircraft(
            "SWA863",
            swa863Pos,
            heading: 107.5,
            gs: 0,
            taxiRoute: MakeRoute(MakeSeg(949, 1, "TE", edge)),
            phase: new TaxiingPhase()
        );
        var swa1182 = MakeAircraft(
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
}
