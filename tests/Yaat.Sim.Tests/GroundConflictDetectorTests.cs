using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

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
        double lat,
        double lon,
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
            Latitude = lat,
            Longitude = lon,
            Heading = heading,
            IsOnGround = true,
            IndicatedAirspeed = gs,
            PushbackHeading = pushbackHeading,
            AssignedTaxiRoute = taxiRoute,
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
            Latitude = BaseLat,
            Longitude = BaseLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n1 = new GroundNode
        {
            Id = 1,
            Latitude = BaseLat + 2 * OffsetLatPer100Ft,
            Longitude = BaseLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n2 = new GroundNode
        {
            Id = 2,
            Latitude = BaseLat + 4 * OffsetLatPer100Ft,
            Longitude = BaseLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };

        var edge01 = new GroundEdge
        {
            FromNodeId = 0,
            ToNodeId = 1,
            TaxiwayName = "A",
            DistanceNm = 200.0 / FtPerNm,
        };
        var edge12 = new GroundEdge
        {
            FromNodeId = 1,
            ToNodeId = 2,
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
            Latitude = BaseLat,
            Longitude = BaseLon - 2 * OffsetLatPer100Ft,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n1 = new GroundNode
        {
            Id = 1,
            Latitude = BaseLat,
            Longitude = BaseLon + 2 * OffsetLatPer100Ft,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n2 = new GroundNode
        {
            Id = 2,
            Latitude = BaseLat + 2 * OffsetLatPer100Ft,
            Longitude = BaseLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };

        var edge02 = new GroundEdge
        {
            FromNodeId = 0,
            ToNodeId = 2,
            TaxiwayName = "A",
            DistanceNm = 200.0 / FtPerNm,
        };
        var edge12 = new GroundEdge
        {
            FromNodeId = 1,
            ToNodeId = 2,
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
        return new TaxiRouteSegment
        {
            FromNodeId = from,
            ToNodeId = to,
            TaxiwayName = taxiway,
            Edge = edge,
        };
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
        var a = MakeAircraft("A", BaseLat, BaseLon, heading: 0, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        var b = MakeAircraft("B", BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon, heading: 0, gs: 10, taxiRoute: routeB, phase: new TaxiingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout);

        // One of them should have a speed limit (the trailer)
        bool anyLimited = a.GroundSpeedLimit is not null || b.GroundSpeedLimit is not null;
        Assert.True(anyLimited, "Expected at least one aircraft to have a speed limit on same edge");
    }

    [Fact]
    public void TwoTaxiing_ConvergingOnSameNode_FartherOneSlows()
    {
        var (layout, n0, n1, _) = BuildConvergenceLayout();

        var routeA = MakeRoute(MakeSeg(0, 2, "A", layout.Edges[0]));
        var routeB = MakeRoute(MakeSeg(1, 2, "B", layout.Edges[1]));

        // A is further from N2 than B
        var a = MakeAircraft("A", n0.Latitude, n0.Longitude, heading: 45, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        var b = MakeAircraft("B", n1.Latitude, n1.Longitude, heading: 315, gs: 15, taxiRoute: routeB, phase: new TaxiingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout);

        // At least one should be limited (the one further from node 2)
        bool anyLimited = a.GroundSpeedLimit is not null || b.GroundSpeedLimit is not null;
        Assert.True(anyLimited, "Expected convergence detection to limit at least one aircraft");
    }

    [Fact]
    public void MovingAircraft_ClosingOnStationary_MovingOneStops()
    {
        // B is stationary at parking, A is taxiing toward B at 150ft
        var a = MakeAircraft("A", BaseLat, BaseLon, heading: 0, gs: 15);
        var b = MakeAircraft("B", BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon, heading: 90, gs: 0, phase: new AtParkingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // A should be limited (closing on stationary B)
        Assert.NotNull(a.GroundSpeedLimit);
        // B is stationary — no limit needed
        Assert.Null(b.GroundSpeedLimit);
    }

    [Fact]
    public void StationaryNearMoving_NotInPath_NoLimit()
    {
        // A is moving east, B is stationary to the north (not in A's path)
        var a = MakeAircraft("A", BaseLat, BaseLon, heading: 90, gs: 15);
        var b = MakeAircraft("B", BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon, heading: 0, gs: 0, phase: new AtParkingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // A is heading east but B is north — bearing to B (~0°) vs heading (90°) = 90° diff
        // Not closing (diff >= 90), so no limit
        Assert.Null(a.GroundSpeedLimit);
    }

    [Fact]
    public void PushingTowardOther_PushbackYields()
    {
        // A is pushing back south (pushbackHeading=180), B is south of A (in pushback path, 150ft)
        var a = MakeAircraft("A", BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon, heading: 0, gs: 3, pushbackHeading: 180);
        a.Phases = new PhaseList();
        a.Phases.Add(new PushbackPhase());
        a.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        var b = MakeAircraft("B", BaseLat, BaseLon, heading: 0, gs: 15);

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // Pushing aircraft closing on B should yield (stop)
        Assert.NotNull(a.GroundSpeedLimit);
        Assert.Equal(0.0, a.GroundSpeedLimit.Value);
    }

    [Fact]
    public void PushingAwayFromOther_NoYield()
    {
        // A is pushing back south (pushbackHeading=180), B is north of A (not in pushback path)
        var a = MakeAircraft("A", BaseLat, BaseLon, heading: 0, gs: 3, pushbackHeading: 180);
        a.Phases = new PhaseList();
        a.Phases.Add(new PushbackPhase());
        a.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        var b = MakeAircraft("B", BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon, heading: 0, gs: 0, phase: new AtParkingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // A is pushing away from B, no yield needed
        Assert.Null(a.GroundSpeedLimit);
    }

    [Fact]
    public void TwoMoving_HeadOn_NoLayout_BothStop()
    {
        // A heading north, B heading south, 250ft apart, both moving
        var a = MakeAircraft("A", BaseLat, BaseLon, heading: 0, gs: 15);
        var b = MakeAircraft("B", BaseLat + 2.5 * OffsetLatPer100Ft, BaseLon, heading: 180, gs: 15);

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // Head-on within 300ft: both should stop
        Assert.NotNull(a.GroundSpeedLimit);
        Assert.Equal(0.0, a.GroundSpeedLimit.Value);
        Assert.NotNull(b.GroundSpeedLimit);
        Assert.Equal(0.0, b.GroundSpeedLimit.Value);
    }

    [Fact]
    public void FollowingAircraft_ExemptFromConflictLimits()
    {
        // A is following B, both close together
        var followPhase = new FollowingPhase("B");
        var a = MakeAircraft("A", BaseLat, BaseLon, heading: 0, gs: 15, phase: followPhase);
        var b = MakeAircraft("B", BaseLat + 0.5 * OffsetLatPer100Ft, BaseLon, heading: 0, gs: 10);

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        // A is following — should NOT get a speed limit from the detector
        Assert.Null(a.GroundSpeedLimit);
    }

    [Fact]
    public void AircraftFarApart_NoInteraction()
    {
        // A and B are 1nm apart (well beyond SearchRangeNm=0.1)
        var a = MakeAircraft("A", BaseLat, BaseLon, heading: 0, gs: 15);
        var b = MakeAircraft("B", BaseLat + 1.0 / 60.0, BaseLon, heading: 180, gs: 15);

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Null(a.GroundSpeedLimit);
        Assert.Null(b.GroundSpeedLimit);
    }

    [Fact]
    public void BothStationary_NoLimitsSet()
    {
        var a = MakeAircraft("A", BaseLat, BaseLon, heading: 0, gs: 0, phase: new AtParkingPhase());
        var b = MakeAircraft("B", BaseLat + 0.5 * OffsetLatPer100Ft, BaseLon, heading: 180, gs: 0, phase: new AtParkingPhase());

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Null(a.GroundSpeedLimit);
        Assert.Null(b.GroundSpeedLimit);
    }

    [Fact]
    public void SingleAircraftOnGround_NoLimitsSet()
    {
        var a = MakeAircraft("A", BaseLat, BaseLon, heading: 0, gs: 15);

        var aircraft = new List<AircraftState> { a };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null);

        Assert.Null(a.GroundSpeedLimit);
    }

    [Fact]
    public void IsClearOf_PushingReference_OutsideBuffer_ReturnsTrue()
    {
        var a = MakeAircraft("A", BaseLat, BaseLon, heading: 0, gs: 15);
        var b = MakeAircraft("B", BaseLat + 5 * OffsetLatPer100Ft, BaseLon, heading: 0, gs: 3, pushbackHeading: 180);

        Assert.True(GroundConflictDetector.IsClearOf(a, b, null));
    }

    [Fact]
    public void IsClearOf_PushingReference_InsideBuffer_ReturnsFalse()
    {
        var a = MakeAircraft("A", BaseLat, BaseLon, heading: 0, gs: 15);
        var b = MakeAircraft("B", BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon, heading: 0, gs: 3, pushbackHeading: 180);

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

        var a = MakeAircraft("A", BaseLat, BaseLon, heading: 0, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        var b = MakeAircraft("B", BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon, heading: 0, gs: 10, taxiRoute: routeB, phase: new TaxiingPhase());

        // Give A an active BREAK timer
        a.ConflictBreakRemainingSeconds = 15.0;

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout, deltaSeconds: 0);

        // A has BREAK — must not receive a speed limit
        Assert.Null(a.GroundSpeedLimit);
    }

    [Fact]
    public void Break_TimerDecrements_EachTick()
    {
        var a = MakeAircraft("A", BaseLat, BaseLon, heading: 0, gs: 15);
        a.ConflictBreakRemainingSeconds = 15.0;

        var aircraft = new List<AircraftState> { a };
        GroundConflictDetector.ApplySpeedLimits(aircraft, null, deltaSeconds: 1.0);

        Assert.Equal(14.0, a.ConflictBreakRemainingSeconds, precision: 9);
    }

    [Fact]
    public void Break_TimerExpired_ConflictsResume()
    {
        // Same setup as Break_AircraftExempt test, but timer is at zero.
        var (layout, _, _, _) = BuildSimpleLayout();
        var edge01 = layout.Edges[0];

        var routeA = MakeRoute(MakeSeg(0, 1, "A", edge01));
        var routeB = MakeRoute(MakeSeg(0, 1, "A", edge01));

        var a = MakeAircraft("A", BaseLat, BaseLon, heading: 0, gs: 15, taxiRoute: routeA, phase: new TaxiingPhase());
        var b = MakeAircraft("B", BaseLat + 1.5 * OffsetLatPer100Ft, BaseLon, heading: 0, gs: 10, taxiRoute: routeB, phase: new TaxiingPhase());

        // BREAK has expired
        a.ConflictBreakRemainingSeconds = 0;

        var aircraft = new List<AircraftState> { a, b };
        GroundConflictDetector.ApplySpeedLimits(aircraft, layout, deltaSeconds: 0);

        // Timer is zero — conflict detection re-engages; trailer should be limited
        bool anyLimited = a.GroundSpeedLimit is not null || b.GroundSpeedLimit is not null;
        Assert.True(anyLimited, "Expected conflict detection to resume after BREAK expires");
    }
}
