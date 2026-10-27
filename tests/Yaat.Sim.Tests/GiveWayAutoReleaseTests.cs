using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Tests;

/// <summary>
/// Covers the auto-release safety nets layered onto direct GIVEWAY holds in
/// <see cref="FlightPhysics.UpdateGiveWayResume"/>: the route-intersection hold, the hold-age
/// safety timeout, and the target-stationary stalemate bypass. The pure bearing/heading release
/// (<see cref="FlightPhysics.IsGiveWayMet"/>) is exercised separately by the BEHIND suites.
/// </summary>
public class GiveWayAutoReleaseTests
{
    private const double BaseLat = 37.620;
    private const double BaseLon = -122.380;

    public GiveWayAutoReleaseTests() => Yaat.Sim.Testing.TestVnasData.EnsureInitialized();

    private static AircraftState MakeGround(string callsign, LatLon pos, double heading, double gs = 0)
    {
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = pos,
            TrueHeading = new TrueHeading(heading),
            IsOnGround = true,
            IndicatedAirspeed = gs,
            Ground = new AircraftGroundOps(),
        };
    }

    private static Func<string, AircraftState?> Lookup(params AircraftState[] aircraft) => cs => aircraft.FirstOrDefault(a => a.Callsign == cs);

    // 3-node convergence layout: routeA 0→2 on taxiway A, routeB 1→2 on taxiway B share node 2.
    private static (TaxiRoute A, TaxiRoute B) MakeConvergingRoutes()
    {
        static GroundNode Node(int id, double lat, double lon) =>
            new()
            {
                Id = id,
                Position = new LatLon(lat, lon),
                Type = GroundNodeType.TaxiwayIntersection,
            };
        GroundNode n0 = Node(0, BaseLat, BaseLon - 0.001);
        GroundNode n1 = Node(1, BaseLat, BaseLon + 0.001);
        GroundNode n2 = Node(2, BaseLat + 0.001, BaseLon);
        var edge02 = new GroundEdge
        {
            Nodes = [n0, n2],
            TaxiwayName = "A",
            DistanceNm = 0.03,
        };
        var edge12 = new GroundEdge
        {
            Nodes = [n1, n2],
            TaxiwayName = "B",
            DistanceNm = 0.03,
        };
        var routeA = new TaxiRoute { Segments = [new TaxiRouteSegment { TaxiwayName = "A", Edge = edge02.Directed(n0, n2) }], HoldShortPoints = [] };
        var routeB = new TaxiRoute { Segments = [new TaxiRouteSegment { TaxiwayName = "B", Edge = edge12.Directed(n1, n2) }], HoldShortPoints = [] };
        return (routeA, routeB);
    }

    [Fact]
    public void SafetyTimeout_ReleasesHoldOnDistantTargetThatNeverPasses()
    {
        // Held aircraft heading north; target ahead heading south (head-on) — geometry never met.
        AircraftState held = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        held.Ground.HoldElapsedSeconds = GiveWayConstants.SafetyTimeoutSeconds - 1;
        AircraftState target = MakeGround("UAL2", new LatLon(BaseLat + 0.01, BaseLon), heading: 180);

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, target), deltaSeconds: 2);

        Assert.Null(held.Ground.Hold);
        Assert.Equal(0, held.Ground.HoldElapsedSeconds);
    }

    [Fact]
    public void SafetyTimeout_DoesNotReleaseIntoCloseDeadAheadTarget()
    {
        // Target is close (~365 ft) and directly ahead/head-on — no lateral room. The timeout
        // must NOT drive the aircraft into it; the hold stays (conflict detector manages proximity).
        AircraftState held = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        held.Ground.HoldElapsedSeconds = GiveWayConstants.SafetyTimeoutSeconds + 10;
        AircraftState target = MakeGround("UAL2", new LatLon(BaseLat + 0.001, BaseLon), heading: 180);

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, target), deltaSeconds: 1);

        Assert.NotNull(held.Ground.Hold);
    }

    [Fact]
    public void BeforeTimeout_HeadOnTargetKeepsHoldActive()
    {
        AircraftState held = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        AircraftState target = MakeGround("UAL2", new LatLon(BaseLat + 0.01, BaseLon), heading: 180);

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, target), deltaSeconds: 1);

        Assert.NotNull(held.Ground.Hold);
        Assert.Equal(1, held.Ground.HoldElapsedSeconds);
    }

    [Fact]
    public void GeometryMet_ButSharedUpcomingNode_KeepsHoldActive()
    {
        // Target ahead and same-direction → IsGiveWayMet returns true (geometry says "passed"),
        // but both routes converge on node 2 ahead — the route-intersection check keeps the hold.
        (TaxiRoute? routeA, TaxiRoute? routeB) = MakeConvergingRoutes();
        AircraftState held = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        held.Ground.AssignedTaxiRoute = routeA;
        AircraftState target = MakeGround("UAL2", new LatLon(BaseLat + 0.01, BaseLon), heading: 0);
        target.Ground.AssignedTaxiRoute = routeB;

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, target), deltaSeconds: 1);

        Assert.NotNull(held.Ground.Hold);
    }

    [Fact]
    public void GeometryMet_NoSharedNode_ReleasesHold()
    {
        AircraftState held = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        // Same direction, target ahead (north) → IsGiveWayMet true; no routes → no shared node.
        AircraftState target = MakeGround("UAL2", new LatLon(BaseLat + 0.01, BaseLon), heading: 0);

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, target), deltaSeconds: 1);

        Assert.Null(held.Ground.Hold);
    }

    [Fact]
    public void TargetStationary_WithLateralClearance_ReleasesHold()
    {
        // Held heading north; target abeam to the east, head-on heading → geometry not met,
        // but the target is abeam (≥90° off our heading) so we have room to pass.
        AircraftState held = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        AircraftState target = MakeGround("UAL2", new LatLon(BaseLat, BaseLon + 0.01), heading: 180);
        target.Ground.StationarySeconds = GiveWayConstants.TargetStationaryThresholdSeconds + 1;

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, target), deltaSeconds: 1);

        Assert.Null(held.Ground.Hold);
    }

    [Fact]
    public void TargetStationary_HeadOnNoClearance_KeepsHoldActive()
    {
        // Target directly ahead and head-on — no lateral room — stays held despite stationary.
        AircraftState held = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        AircraftState target = MakeGround("UAL2", new LatLon(BaseLat + 0.001, BaseLon), heading: 180);
        target.Ground.StationarySeconds = GiveWayConstants.TargetStationaryThresholdSeconds + 1;

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, target), deltaSeconds: 1);

        Assert.NotNull(held.Ground.Hold);
    }

    [Fact]
    public void MutualYield_HigherCallsign_StaysHeld()
    {
        // Both abeam each other and mutually yielding; the higher-callsign aircraft must wait
        // so both never proceed on the same tick.
        AircraftState higher = MakeGround("ZZZ9", new LatLon(BaseLat, BaseLon), heading: 0);
        higher.Ground.Hold = HoldDirective.GiveWay("AAA1");
        AircraftState lower = MakeGround("AAA1", new LatLon(BaseLat, BaseLon + 0.01), heading: 180);
        lower.Ground.Hold = HoldDirective.GiveWay("ZZZ9");
        lower.Ground.StationarySeconds = GiveWayConstants.TargetStationaryThresholdSeconds + 1;

        FlightPhysics.UpdateGiveWayResume(higher, Lookup(higher, lower), deltaSeconds: 1);

        Assert.NotNull(higher.Ground.Hold);
    }

    [Fact]
    public void MutualYield_LowerCallsign_Releases()
    {
        AircraftState lower = MakeGround("AAA1", new LatLon(BaseLat, BaseLon), heading: 0);
        lower.Ground.Hold = HoldDirective.GiveWay("ZZZ9");
        AircraftState higher = MakeGround("ZZZ9", new LatLon(BaseLat, BaseLon + 0.01), heading: 180);
        higher.Ground.Hold = HoldDirective.GiveWay("AAA1");
        higher.Ground.StationarySeconds = GiveWayConstants.TargetStationaryThresholdSeconds + 1;

        FlightPhysics.UpdateGiveWayResume(lower, Lookup(lower, higher), deltaSeconds: 1);

        Assert.Null(lower.Ground.Hold);
    }

    [Fact]
    public void StationaryTimer_AccumulatesWhenStopped_ResetsWhenMoving()
    {
        AircraftState ac = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0);

        FlightPhysics.UpdateGroundStationaryTimer(ac, deltaSeconds: 5);
        Assert.Equal(5, ac.Ground.StationarySeconds);

        FlightPhysics.UpdateGroundStationaryTimer(ac, deltaSeconds: 5);
        Assert.Equal(10, ac.Ground.StationarySeconds);

        ac.IndicatedAirspeed = 12; // now moving
        FlightPhysics.UpdateGroundStationaryTimer(ac, deltaSeconds: 5);
        Assert.Equal(0, ac.Ground.StationarySeconds);
    }

    [Fact]
    public void StationaryTimer_ResetsWhenAirborne()
    {
        AircraftState ac = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0);
        ac.Ground.StationarySeconds = 42;
        ac.IsOnGround = false;

        FlightPhysics.UpdateGroundStationaryTimer(ac, deltaSeconds: 5);

        Assert.Equal(0, ac.Ground.StationarySeconds);
    }

    // --- Crossing traffic: the hold lasts until the traffic's tail is past the shared node (YAAT-1263) ---

    private static readonly LatLon SharedPos = new(BaseLat, BaseLon);

    private static LatLon From(LatLon origin, double bearingDeg, double ft) =>
        GeoMath.ProjectPoint(origin, new TrueHeading(bearingDeg), ft / GeoMath.FeetPerNm);

    private static GroundNode CrossNode(int id, LatLon pos) =>
        new()
        {
            Id = id,
            Position = pos,
            Type = GroundNodeType.TaxiwayIntersection,
        };

    private static TaxiRouteSegment Seg(GroundNode from, GroundNode to, string taxiway)
    {
        var edge = new GroundEdge
        {
            Nodes = [from, to],
            TaxiwayName = taxiway,
            DistanceNm = GeoMath.DistanceNm(from.Position, to.Position),
        };
        return new TaxiRouteSegment { TaxiwayName = taxiway, Edge = edge.Directed(from, to) };
    }

    private static TaxiRoute Route(int current, params TaxiRouteSegment[] segments) =>
        new()
        {
            Segments = [.. segments],
            HoldShortPoints = [],
            CurrentSegmentIndex = current,
        };

    /// <summary>
    /// Taxiway A runs north through the shared node 10, from node 11 5,000 ft south of it to node 12 1,000 ft north; taxiway F
    /// runs east through it, from node 20 5,000 ft west of it to node 21 2,000 ft east.
    /// </summary>
    private sealed record Crossing(GroundNode Shared, GroundNode ASouth, GroundNode ANorth, GroundNode FWest, GroundNode FEast);

    private static Crossing MakeCrossing() =>
        new(
            CrossNode(10, SharedPos),
            CrossNode(11, From(SharedPos, 180, 5000)),
            CrossNode(12, From(SharedPos, 0, 1000)),
            CrossNode(20, From(SharedPos, 270, 5000)),
            CrossNode(21, From(SharedPos, 90, 2000))
        );

    /// <summary>The held aircraft's route: north on A from node 11 through the shared node to node 12.</summary>
    private static TaxiRoute NorthOnA(Crossing x) => Route(0, Seg(x.ASouth, x.Shared, "A"), Seg(x.Shared, x.ANorth, "A"));

    /// <summary>The traffic's route: east on F from node 20 through the shared node to node 21.</summary>
    private static TaxiRoute EastOnF(Crossing x, int current) => Route(current, Seg(x.FWest, x.Shared, "F"), Seg(x.Shared, x.FEast, "F"));

    private static AircraftState HeldOnA(Crossing x, string callsign, string yieldTo, LatLon pos)
    {
        AircraftState held = MakeGround(callsign, pos, heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay(yieldTo);
        held.Ground.AssignedTaxiRoute = NorthOnA(x);
        return held;
    }

    private static double B738HalfSpanFt() => (FaaAircraftDatabase.Get("B738")?.WingspanFt ?? AircraftFootprint.ResolveWingspanFt("B738")) / 2;

    /// <summary>How far a B738's centre must be past the node for its tail to clear a held B738: its half-length plus the held half-span plus the buffer.</summary>
    private static double TailClearCentreFt() => (AircraftLength.ResolveFt("B738") / 2) + B738HalfSpanFt() + GroundOutlineSweep.WingtipBufferFt;

    [Fact]
    public void GiveWay_CrossingTargetBeyondLookahead_KeepsHolding()
    {
        // UAL2237/UAL926 at SFO: the routes cross 5,000 ft ahead of each, headings 90 degrees apart.
        Crossing x = MakeCrossing();
        AircraftState held = HeldOnA(x, "AAL1", "UAL2", x.ASouth.Position);
        AircraftState traffic = MakeGround("UAL2", x.FWest.Position, heading: 90, gs: 15);
        traffic.Ground.AssignedTaxiRoute = EastOnF(x, current: 0);

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, traffic), deltaSeconds: 1);

        Assert.NotNull(held.Ground.Hold);
    }

    [Fact]
    public void GiveWay_CrossingTargetTailPastSharedNode_Releases()
    {
        Crossing x = MakeCrossing();
        AircraftState held = HeldOnA(x, "AAL1", "UAL2", x.ASouth.Position);
        AircraftState traffic = MakeGround("UAL2", From(SharedPos, 90, TailClearCentreFt() + 10), heading: 90, gs: 15);
        traffic.Ground.AssignedTaxiRoute = EastOnF(x, current: 1);

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, traffic), deltaSeconds: 1);

        Assert.Null(held.Ground.Hold);
    }

    [Fact]
    public void GiveWay_CrossingTargetTailJustShortOfClearance_KeepsHolding()
    {
        Crossing x = MakeCrossing();
        AircraftState held = HeldOnA(x, "AAL1", "UAL2", x.ASouth.Position);
        AircraftState traffic = MakeGround("UAL2", From(SharedPos, 90, TailClearCentreFt() - 10), heading: 90, gs: 15);
        traffic.Ground.AssignedTaxiRoute = EastOnF(x, current: 1);

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, traffic), deltaSeconds: 1);

        Assert.NotNull(held.Ground.Hold);
    }

    [Fact]
    public void GiveWay_CrossingTargetRouteNoLongerSharesNode_Releases()
    {
        // Re-routed south onto G at node 20: its route no longer passes the shared node, and the routes share none.
        Crossing x = MakeCrossing();
        AircraftState held = HeldOnA(x, "AAL1", "UAL2", x.ASouth.Position);
        AircraftState traffic = MakeGround("UAL2", x.FWest.Position, heading: 90, gs: 15);
        GroundNode gSouth = CrossNode(30, From(x.FWest.Position, 180, 3000));
        traffic.Ground.AssignedTaxiRoute = Route(0, Seg(x.FWest, gSouth, "G"));

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, traffic), deltaSeconds: 1);

        Assert.Null(held.Ground.Hold);
    }

    [Fact]
    public void GiveWay_StationaryTrafficShortOfSharedNode_BypassDoesNotRelease()
    {
        // Stopped short of the shared node, facing south so the pass geometry is not met, with room to pass it abeam.
        Crossing x = MakeCrossing();
        AircraftState held = HeldOnA(x, "AAL1", "UAL2", x.ASouth.Position);
        AircraftState traffic = MakeGround("UAL2", x.FWest.Position, heading: 180);
        traffic.Ground.AssignedTaxiRoute = EastOnF(x, current: 0);
        traffic.Ground.StationarySeconds = GiveWayConstants.TargetStationaryThresholdSeconds + 1;
        Assert.True(GroundConflictDetector.HasWingspanLateralClearance(held, traffic), "the fixture's traffic should be laterally clear");

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, traffic), deltaSeconds: 1);

        Assert.NotNull(held.Ground.Hold);
    }

    [Fact]
    public void GiveWay_SafetyTimeoutWhileRoutesShareNode_KeepsHolding()
    {
        // 3,609 ft from the shared node each: 0.84 nm apart, laterally clear, the hold past its safety timeout.
        Crossing x = MakeCrossing();
        AircraftState held = HeldOnA(x, "AAL1", "UAL2", From(SharedPos, 180, 3609));
        held.Ground.HoldElapsedSeconds = GiveWayConstants.SafetyTimeoutSeconds + 1;
        AircraftState traffic = MakeGround("UAL2", From(SharedPos, 270, 3609), heading: 90, gs: 15);
        traffic.Ground.AssignedTaxiRoute = EastOnF(x, current: 0);

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, traffic), deltaSeconds: 1);

        Assert.NotNull(held.Ground.Hold);
    }

    [Fact]
    public void GiveWay_MutualCrossing_LowerCallsignReleases()
    {
        Crossing x = MakeCrossing();
        AircraftState lower = HeldOnA(x, "AAA1", "ZZZ9", x.ASouth.Position);
        AircraftState higher = MakeGround("ZZZ9", x.FWest.Position, heading: 90);
        higher.Ground.Hold = HoldDirective.GiveWay("AAA1");
        higher.Ground.AssignedTaxiRoute = EastOnF(x, current: 0);

        FlightPhysics.UpdateGiveWayResume(higher, Lookup(lower, higher), deltaSeconds: 1);
        FlightPhysics.UpdateGiveWayResume(lower, Lookup(lower, higher), deltaSeconds: 1);

        Assert.NotNull(higher.Ground.Hold);
        Assert.Null(lower.Ground.Hold);
    }

    [Fact]
    public void GiveWay_HeadOnSameEdgeIntoSharedNode_UsesOppositeDirectionBranch()
    {
        // The traffic comes south on A into the shared node, along the held aircraft's own route, and turns west onto F there.
        Crossing x = MakeCrossing();
        TaxiRoute SouthOnAThenWest() => Route(0, Seg(x.ANorth, x.Shared, "A"), Seg(x.Shared, x.FWest, "F"));

        // Head-on and dead ahead: the opposite-direction branch keeps the hold.
        AircraftState heldAhead = HeldOnA(x, "AAL1", "UAL2", x.ASouth.Position);
        AircraftState ahead = MakeGround("UAL2", From(SharedPos, 0, 900), heading: 180, gs: 15);
        ahead.Ground.AssignedTaxiRoute = SouthOnAThenWest();
        Assert.Null(GroundConflictDetector.GiveWayJunctionOf(heldAhead, ahead));
        FlightPhysics.UpdateGiveWayResume(heldAhead, Lookup(heldAhead, ahead), deltaSeconds: 1);
        Assert.NotNull(heldAhead.Ground.Hold);

        // Head-on and behind the held aircraft: the opposite-direction branch releases, with no shared-node hold.
        AircraftState heldPassed = HeldOnA(x, "AAL1", "UAL2", x.ASouth.Position);
        AircraftState passed = MakeGround("UAL2", From(x.ASouth.Position, 135, 300), heading: 180, gs: 15);
        passed.Ground.AssignedTaxiRoute = SouthOnAThenWest();
        Assert.Null(GroundConflictDetector.GiveWayJunctionOf(heldPassed, passed));
        FlightPhysics.UpdateGiveWayResume(heldPassed, Lookup(heldPassed, passed), deltaSeconds: 1);
        Assert.Null(heldPassed.Ground.Hold);
    }

    [Fact]
    public void GiveWayStop_SharedNodeBeyond1500Ft_ReturnsStopShortOfTheNode()
    {
        Crossing x = MakeCrossing();
        AircraftState held = HeldOnA(x, "AAL1", "UAL2", x.ASouth.Position);
        AircraftState traffic = MakeGround("UAL2", x.FWest.Position, heading: 90, gs: 15);
        traffic.Ground.AssignedTaxiRoute = EastOnF(x, current: 0);

        (int NodeId, double ToStopFt)? stop = GroundConflictDetector.GiveWayStop(held, traffic, out string? noStopReason);

        // The centre keeps both half-spans plus the buffer off F, the nose (half a length ahead) the traffic's half-span plus it.
        double halfSpanFt = B738HalfSpanFt();
        double centreOffTrackFt = Math.Max(
            (2 * halfSpanFt) + GroundOutlineSweep.WingtipBufferFt,
            halfSpanFt + GroundOutlineSweep.WingtipBufferFt + (AircraftLength.ResolveFt("B738") / 2)
        );
        Assert.True(stop is not null, $"no give-way stop: {noStopReason}");
        Assert.Equal(x.Shared.Id, stop.Value.NodeId);
        double toNodeFt = GeoMath.DistanceNm(x.ASouth.Position, SharedPos) * GeoMath.FeetPerNm;
        Assert.True(toNodeFt > 4900, $"the fixture's shared node should be about 5,000 ft ahead, is {toNodeFt:F0} ft");
        Assert.Equal(toNodeFt - centreOffTrackFt, stop.Value.ToStopFt, 1.0);
    }

    [Fact]
    public void GiveWay_TrafficTurnsOntoHeldRouteTowardHeld_KeepsHoldingAfterSideNode()
    {
        // The traffic comes east on F to the side node and turns south down A, toward the held aircraft.
        Crossing x = MakeCrossing();
        AircraftState held = HeldOnA(x, "AAL1", "UAL2", x.ASouth.Position);
        AircraftState traffic = MakeGround("UAL2", SharedPos, heading: 180, gs: 15);
        traffic.Ground.AssignedTaxiRoute = Route(1, Seg(x.FWest, x.Shared, "F"), Seg(x.Shared, x.ASouth, "A"));

        foreach (double pastFt in new[] { 10.0, 200.0, 400.0, 800.0, 1600.0 })
        {
            traffic.Position = From(SharedPos, 180, TailClearCentreFt() + pastFt);
            FlightPhysics.UpdateGiveWayResume(held, Lookup(held, traffic), deltaSeconds: 1);

            Assert.True(held.Ground.Hold is not null, $"released with the traffic's tail {pastFt:F0} ft past its clearance, heading down A at it");
        }
    }

    [Fact]
    public void GiveWay_HeadOnTrafficRerouteStartingOnHeldTaxiway_NotAJunction()
    {
        // Re-routed where it stands on A north of the shared node: its route starts on the held route and leaves down A toward it.
        Crossing x = MakeCrossing();
        AircraftState held = HeldOnA(x, "AAL1", "UAL2", x.ASouth.Position);
        AircraftState traffic = MakeGround("UAL2", x.ANorth.Position, heading: 180, gs: 15);
        traffic.Ground.AssignedTaxiRoute = Route(0, Seg(x.ANorth, x.Shared, "A"), Seg(x.Shared, x.ASouth, "A"));

        Assert.Null(GroundConflictDetector.GiveWayJunctionOf(held, traffic));
    }

    [Fact]
    public void GiveWay_SameDirectionTrafficAheadOnHeldTaxiway_NotAJunction()
    {
        // Ahead on A north of the shared node, its route starting there and running north as the held aircraft's does.
        Crossing x = MakeCrossing();
        AircraftState held = HeldOnA(x, "AAL1", "UAL2", x.ASouth.Position);
        AircraftState traffic = MakeGround("UAL2", From(SharedPos, 0, 300), heading: 0, gs: 15);
        traffic.Ground.AssignedTaxiRoute = Route(0, Seg(x.Shared, x.ANorth, "A"));

        Assert.Null(GroundConflictDetector.GiveWayJunctionOf(held, traffic));
    }

    [Fact]
    public void GiveWay_TrafficShortOfItsRouteStartCrossing_NotPast()
    {
        // The traffic's route starts on the shared node and runs east on F; the traffic is still 300 ft west of it.
        Crossing x = MakeCrossing();
        AircraftState held = HeldOnA(x, "AAL1", "UAL2", x.ASouth.Position);
        AircraftState traffic = MakeGround("UAL2", From(SharedPos, 270, 300), heading: 90, gs: 15);
        traffic.Ground.AssignedTaxiRoute = Route(0, Seg(x.Shared, x.FEast, "F"));

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, traffic), deltaSeconds: 1);

        Assert.NotNull(held.Ground.Hold);
    }

    [Fact]
    public void GiveWayStop_TwoCrossingsInOppositeOrder_StopsShortOfTheFirstMet()
    {
        // The held aircraft runs north on A through M (2,000 ft south of N) and then N. The traffic crosses A at N going east,
        // loops round to the east, and crosses A again at M going west: N first on its route, M first on the held aircraft's.
        Crossing x = MakeCrossing();
        GroundNode m = CrossNode(13, From(SharedPos, 180, 2000));
        GroundNode p = CrossNode(22, From(SharedPos, 90, 1500));
        GroundNode q = CrossNode(23, From(m.Position, 90, 1500));
        GroundNode r = CrossNode(24, From(m.Position, 270, 1500));
        AircraftState held = MakeGround("AAL1", x.ASouth.Position, heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        held.Ground.AssignedTaxiRoute = Route(0, Seg(x.ASouth, m, "A"), Seg(m, x.Shared, "A"), Seg(x.Shared, x.ANorth, "A"));
        AircraftState traffic = MakeGround("UAL2", x.FWest.Position, heading: 90, gs: 15);
        traffic.Ground.AssignedTaxiRoute = Route(
            0,
            Seg(x.FWest, x.Shared, "F"),
            Seg(x.Shared, p, "F"),
            Seg(p, q, "G"),
            Seg(q, m, "H"),
            Seg(m, r, "H")
        );

        (int NodeId, double ToStopFt)? stop = GroundConflictDetector.GiveWayStop(held, traffic, out string? noStopReason);

        Assert.True(stop is not null, $"no give-way stop: {noStopReason}");
        Assert.Equal(m.Id, stop.Value.NodeId);
        double toMFt = GeoMath.DistanceNm(x.ASouth.Position, m.Position) * GeoMath.FeetPerNm;
        Assert.Equal(toMFt - CentreOffTrackFt(), stop.Value.ToStopFt, 1.0);
    }

    [Fact]
    public void GiveWay_MutualCrossing_HigherAlreadyInJunction_LowerKeepsHolding()
    {
        // The higher callsign has already entered the junction: its centre is 20 ft past the shared node, its tail short of it.
        Crossing x = MakeCrossing();
        AircraftState lower = HeldOnA(x, "AAA1", "ZZZ9", x.ASouth.Position);
        AircraftState higher = MakeGround("ZZZ9", From(SharedPos, 90, 20), heading: 90);
        higher.Ground.Hold = HoldDirective.GiveWay("AAA1");
        higher.Ground.AssignedTaxiRoute = EastOnF(x, current: 1);

        FlightPhysics.UpdateGiveWayResume(lower, Lookup(lower, higher), deltaSeconds: 1);

        Assert.NotNull(lower.Ground.Hold);
    }

    [Fact]
    public void GiveWay_MergeAtSharedNode_HoldsUntilTailClears_ThenReleases()
    {
        // The traffic, taxiing east on F, turns north onto A at the shared node, the way the held aircraft 2,000 ft south of it
        // on A leaves it too: a merge. Nearer the node, it still holds the held aircraft until its tail has cleared the node.
        Crossing x = MakeCrossing();
        AircraftState held = HeldOnA(x, "AAL1", "UAL2", From(SharedPos, 180, 2000));
        AircraftState traffic = MakeGround("UAL2", From(SharedPos, 270, 1000), heading: 90, gs: 15);
        TaxiRoute merging = Route(0, Seg(x.FWest, x.Shared, "F"), Seg(x.Shared, x.ANorth, "A"));
        traffic.Ground.AssignedTaxiRoute = merging;

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, traffic), deltaSeconds: 1);
        Assert.True(held.Ground.Hold is not null, "released with the merging traffic 1,000 ft short of the shared node");

        merging.CurrentSegmentIndex = 1;
        traffic.Position = From(SharedPos, 0, TailClearCentreFt() - 10);
        traffic.TrueHeading = new TrueHeading(0);
        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, traffic), deltaSeconds: 1);
        Assert.True(held.Ground.Hold is not null, "released with the merging traffic's tail just short of its clearance past the node");

        traffic.Position = From(SharedPos, 0, TailClearCentreFt() + 10);
        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, traffic), deltaSeconds: 1);
        Assert.Null(held.Ground.Hold);
    }

    [Fact]
    public void GiveWay_TrafficSplitsAndRejoinsHeldPathSameWay_NotAJunction()
    {
        // Ahead of the held aircraft on its route north up A, the traffic leaves A at M by a detour east through P and comes
        // back onto A at the shared node, leaving it north as the held route does: still in trail, no crossing.
        Crossing x = MakeCrossing();
        (GroundNode m, GroundNode p) = DetourNodes();
        AircraftState held = HeldOnAThroughM(x, m);
        AircraftState traffic = MakeGround("UAL2", m.Position, heading: 45, gs: 15);
        traffic.Ground.AssignedTaxiRoute = Route(1, Seg(x.ASouth, m, "A"), Seg(m, p, "K"), Seg(p, x.Shared, "K"), Seg(x.Shared, x.ANorth, "A"));

        Assert.Null(GroundConflictDetector.GiveWayJunctionOf(held, traffic));
    }

    [Fact]
    public void GiveWay_TrafficLeavesHeldPathAndCrossesBackOver_StillAJunction()
    {
        // As above, but at the shared node the traffic leaves east on F, not north on A with the held route: it crosses there.
        Crossing x = MakeCrossing();
        (GroundNode m, GroundNode p) = DetourNodes();
        AircraftState held = HeldOnAThroughM(x, m);
        AircraftState traffic = MakeGround("UAL2", m.Position, heading: 45, gs: 15);
        traffic.Ground.AssignedTaxiRoute = Route(1, Seg(x.ASouth, m, "A"), Seg(m, p, "K"), Seg(p, x.Shared, "K"), Seg(x.Shared, x.FEast, "F"));

        Assert.Equal(x.Shared.Id, GroundConflictDetector.GiveWayJunctionOf(held, traffic)?.NodeId);
    }

    [Fact]
    public void GiveWay_TrafficFirstMeetsHeldRouteFromASide_StillAJunction()
    {
        // The traffic's first node on the held route is the shared node, entered east along F, and it leaves north on A as the
        // held route does: a plain merge, with no earlier node where it was in trail.
        Crossing x = MakeCrossing();
        (GroundNode m, _) = DetourNodes();
        AircraftState held = HeldOnAThroughM(x, m);
        AircraftState traffic = MakeGround("UAL2", x.FWest.Position, heading: 90, gs: 15);
        traffic.Ground.AssignedTaxiRoute = Route(0, Seg(x.FWest, x.Shared, "F"), Seg(x.Shared, x.ANorth, "A"));

        Assert.Equal(x.Shared.Id, GroundConflictDetector.GiveWayJunctionOf(held, traffic)?.NodeId);
    }

    [Fact]
    public void GiveWay_TrafficCrossedHeldRouteBeforeTheSplit_RejoinStillAJunction()
    {
        // The traffic crossed A at M from the west (its tail now clear of M), detours east through P and comes back onto A at
        // the shared node leaving north: it was never in trail before the split, so the shared node is a crossing.
        Crossing x = MakeCrossing();
        (GroundNode m, GroundNode p) = DetourNodes();
        GroundNode r = CrossNode(24, From(m.Position, 270, 1500));
        AircraftState held = HeldOnAThroughM(x, m);
        AircraftState traffic = MakeGround("UAL2", From(m.Position, MToPBearingDeg, TailClearCentreFt() + 200), heading: MToPBearingDeg, gs: 15);
        traffic.Ground.AssignedTaxiRoute = Route(2, Seg(r, m, "K"), Seg(m, p, "K"), Seg(p, x.Shared, "K"), Seg(x.Shared, x.ANorth, "A"));

        Assert.Equal(x.Shared.Id, GroundConflictDetector.GiveWayJunctionOf(held, traffic)?.NodeId);
    }

    [Fact]
    public void GiveWay_TrafficRouteStartsOnHeldRouteThenRejoins_StillAJunction()
    {
        // The traffic's route starts on M, on the held route, with no node it entered M from: it was not in trail there, so
        // where it comes back onto A at the shared node is a crossing.
        Crossing x = MakeCrossing();
        (GroundNode m, GroundNode p) = DetourNodes();
        AircraftState held = HeldOnAThroughM(x, m);
        AircraftState traffic = MakeGround("UAL2", p.Position, heading: 0, gs: 15);
        traffic.Ground.AssignedTaxiRoute = Route(1, Seg(m, p, "K"), Seg(p, x.Shared, "K"), Seg(x.Shared, x.ANorth, "A"));

        Assert.Equal(x.Shared.Id, GroundConflictDetector.GiveWayJunctionOf(held, traffic)?.NodeId);
    }

    [Fact]
    public void GiveWay_HeldReachesTheSplitAfterTheRejoin_StillAJunction()
    {
        // The held route runs north on A through M, the shared node and node 12 to Q. The traffic, ahead on it the same way,
        // follows it to Q, loops east through Z and comes back onto A at M leaving north: the held route reaches M before Q,
        // so the traffic is not in trail behind its split at Q there but crosses into the held aircraft's path at M.
        Crossing x = MakeCrossing();
        (GroundNode m, _) = DetourNodes();
        GroundNode q = CrossNode(25, From(SharedPos, 0, 2000));
        GroundNode z = CrossNode(26, From(SharedPos, 90, 3000));
        AircraftState held = MakeGround("AAL1", x.ASouth.Position, heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        held.Ground.AssignedTaxiRoute = Route(0, Seg(x.ASouth, m, "A"), Seg(m, x.Shared, "A"), Seg(x.Shared, x.ANorth, "A"), Seg(x.ANorth, q, "A"));
        AircraftState traffic = MakeGround("UAL2", SharedPos, heading: 0, gs: 15);
        traffic.Ground.AssignedTaxiRoute = Route(
            0,
            Seg(x.Shared, x.ANorth, "A"),
            Seg(x.ANorth, q, "A"),
            Seg(q, z, "K"),
            Seg(z, m, "K"),
            Seg(m, x.Shared, "A")
        );

        Assert.Equal(m.Id, GroundConflictDetector.GiveWayJunctionOf(held, traffic)?.NodeId);
    }

    /// <summary>The bearing (deg) from node M to node P of <see cref="DetourNodes"/>: 500 ft east over 1,000 ft north.</summary>
    private static readonly double MToPBearingDeg = Math.Atan2(500, 1000) * 180 / Math.PI;

    /// <summary>Node M on A 2,000 ft south of the shared node, and node P 500 ft east of A 1,000 ft south of it.</summary>
    private static (GroundNode M, GroundNode P) DetourNodes() =>
        (CrossNode(13, From(SharedPos, 180, 2000)), CrossNode(22, From(From(SharedPos, 180, 1000), 90, 500)));

    /// <summary>A held aircraft at node 11 giving way to UAL2, routed north on A through <paramref name="m"/> and the shared node.</summary>
    private static AircraftState HeldOnAThroughM(Crossing x, GroundNode m)
    {
        AircraftState held = MakeGround("AAL1", x.ASouth.Position, heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        held.Ground.AssignedTaxiRoute = Route(0, Seg(x.ASouth, m, "A"), Seg(m, x.Shared, "A"), Seg(x.Shared, x.ANorth, "A"));
        return held;
    }

    [Fact]
    public void GiveWayStop_FarJunctionSkip_SameStopAsFullWalk()
    {
        // A runs north to the shared node in 600 ft segments. From the last of them the junction is under 1,500 ft and every
        // piece of the walk is searched; from 4,800 ft back the pieces far from F are skipped. The stops must differ by exactly
        // the route between the two starts.
        Crossing x = MakeCrossing();
        List<GroundNode> south = [.. Enumerable.Range(1, 8).Select(k => CrossNode(100 + k, From(SharedPos, 180, 600 * k)))];
        List<TaxiRouteSegment> farSegments = [];
        for (int k = south.Count - 1; k > 0; k--)
        {
            farSegments.Add(Seg(south[k], south[k - 1], "A"));
        }

        farSegments.Add(Seg(south[0], x.Shared, "A"));
        farSegments.Add(Seg(x.Shared, x.ANorth, "A"));
        AircraftState traffic = MakeGround("UAL2", x.FWest.Position, heading: 90, gs: 15);
        traffic.Ground.AssignedTaxiRoute = EastOnF(x, current: 0);

        AircraftState near = MakeGround("AAL1", south[0].Position, heading: 0);
        near.Ground.AssignedTaxiRoute = Route(0, Seg(south[0], x.Shared, "A"), Seg(x.Shared, x.ANorth, "A"));
        AircraftState far = MakeGround("AAL1", south[^1].Position, heading: 0);
        far.Ground.AssignedTaxiRoute = Route(0, [.. farSegments]);

        (int NodeId, double ToStopFt)? nearStop = GroundConflictDetector.GiveWayStop(near, traffic, out string? nearReason);
        (int NodeId, double ToStopFt)? farStop = GroundConflictDetector.GiveWayStop(far, traffic, out string? farReason);

        Assert.True(nearStop is not null, $"no give-way stop from 600 ft: {nearReason}");
        Assert.True(farStop is not null, $"no give-way stop from 4,800 ft: {farReason}");
        double betweenFt = 0;
        for (int k = south.Count - 1; k > 0; k--)
        {
            betweenFt += GeoMath.DistanceNm(south[k].Position, south[k - 1].Position) * GeoMath.FeetPerNm;
        }

        Assert.True(nearStop.Value.ToStopFt < 1500, $"the reference stop should be under 1,500 ft, is {nearStop.Value.ToStopFt:F1} ft");
        Assert.Equal(nearStop.Value.NodeId, farStop.Value.NodeId);
        Assert.Equal(nearStop.Value.ToStopFt + betweenFt, farStop.Value.ToStopFt, 1e-6);
    }

    /// <summary>How far a held B738's centre stops off a crossing B738's straight track: the centre's clearance, or the nose's plus half a length.</summary>
    private static double CentreOffTrackFt()
    {
        double halfSpanFt = B738HalfSpanFt();
        return Math.Max(
            (2 * halfSpanFt) + GroundOutlineSweep.WingtipBufferFt,
            halfSpanFt + GroundOutlineSweep.WingtipBufferFt + (AircraftLength.ResolveFt("B738") / 2)
        );
    }
}
