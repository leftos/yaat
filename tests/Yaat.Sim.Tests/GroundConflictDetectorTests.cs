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
    /// false-head-on gridlock under all-V2.
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
}
