using Xunit;
using Yaat.Sim.Data.Airport;

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
        GroundNode Node(int id, double lat, double lon) =>
            new()
            {
                Id = id,
                Position = new LatLon(lat, lon),
                Type = GroundNodeType.TaxiwayIntersection,
            };
        var n0 = Node(0, BaseLat, BaseLon - 0.001);
        var n1 = Node(1, BaseLat, BaseLon + 0.001);
        var n2 = Node(2, BaseLat + 0.001, BaseLon);
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
        var held = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        held.Ground.HoldElapsedSeconds = GiveWayConstants.SafetyTimeoutSeconds - 1;
        var target = MakeGround("UAL2", new LatLon(BaseLat + 0.01, BaseLon), heading: 180);

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, target), deltaSeconds: 2);

        Assert.Null(held.Ground.Hold);
        Assert.Equal(0, held.Ground.HoldElapsedSeconds);
    }

    [Fact]
    public void SafetyTimeout_DoesNotReleaseIntoCloseDeadAheadTarget()
    {
        // Target is close (~365 ft) and directly ahead/head-on — no lateral room. The timeout
        // must NOT drive the aircraft into it; the hold stays (conflict detector manages proximity).
        var held = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        held.Ground.HoldElapsedSeconds = GiveWayConstants.SafetyTimeoutSeconds + 10;
        var target = MakeGround("UAL2", new LatLon(BaseLat + 0.001, BaseLon), heading: 180);

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, target), deltaSeconds: 1);

        Assert.NotNull(held.Ground.Hold);
    }

    [Fact]
    public void BeforeTimeout_HeadOnTargetKeepsHoldActive()
    {
        var held = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        var target = MakeGround("UAL2", new LatLon(BaseLat + 0.01, BaseLon), heading: 180);

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, target), deltaSeconds: 1);

        Assert.NotNull(held.Ground.Hold);
        Assert.Equal(1, held.Ground.HoldElapsedSeconds);
    }

    [Fact]
    public void GeometryMet_ButSharedUpcomingNode_KeepsHoldActive()
    {
        // Target ahead and same-direction → IsGiveWayMet returns true (geometry says "passed"),
        // but both routes converge on node 2 ahead — the route-intersection check keeps the hold.
        var (routeA, routeB) = MakeConvergingRoutes();
        var held = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        held.Ground.AssignedTaxiRoute = routeA;
        var target = MakeGround("UAL2", new LatLon(BaseLat + 0.01, BaseLon), heading: 0);
        target.Ground.AssignedTaxiRoute = routeB;

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, target), deltaSeconds: 1);

        Assert.NotNull(held.Ground.Hold);
    }

    [Fact]
    public void GeometryMet_NoSharedNode_ReleasesHold()
    {
        var held = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        // Same direction, target ahead (north) → IsGiveWayMet true; no routes → no shared node.
        var target = MakeGround("UAL2", new LatLon(BaseLat + 0.01, BaseLon), heading: 0);

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, target), deltaSeconds: 1);

        Assert.Null(held.Ground.Hold);
    }

    [Fact]
    public void TargetStationary_WithLateralClearance_ReleasesHold()
    {
        // Held heading north; target abeam to the east, head-on heading → geometry not met,
        // but the target is abeam (≥90° off our heading) so we have room to pass.
        var held = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        var target = MakeGround("UAL2", new LatLon(BaseLat, BaseLon + 0.01), heading: 180);
        target.Ground.StationarySeconds = GiveWayConstants.TargetStationaryThresholdSeconds + 1;

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, target), deltaSeconds: 1);

        Assert.Null(held.Ground.Hold);
    }

    [Fact]
    public void TargetStationary_HeadOnNoClearance_KeepsHoldActive()
    {
        // Target directly ahead and head-on — no lateral room — stays held despite stationary.
        var held = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0);
        held.Ground.Hold = HoldDirective.GiveWay("UAL2");
        var target = MakeGround("UAL2", new LatLon(BaseLat + 0.001, BaseLon), heading: 180);
        target.Ground.StationarySeconds = GiveWayConstants.TargetStationaryThresholdSeconds + 1;

        FlightPhysics.UpdateGiveWayResume(held, Lookup(held, target), deltaSeconds: 1);

        Assert.NotNull(held.Ground.Hold);
    }

    [Fact]
    public void MutualYield_HigherCallsign_StaysHeld()
    {
        // Both abeam each other and mutually yielding; the higher-callsign aircraft must wait
        // so both never proceed on the same tick.
        var higher = MakeGround("ZZZ9", new LatLon(BaseLat, BaseLon), heading: 0);
        higher.Ground.Hold = HoldDirective.GiveWay("AAA1");
        var lower = MakeGround("AAA1", new LatLon(BaseLat, BaseLon + 0.01), heading: 180);
        lower.Ground.Hold = HoldDirective.GiveWay("ZZZ9");
        lower.Ground.StationarySeconds = GiveWayConstants.TargetStationaryThresholdSeconds + 1;

        FlightPhysics.UpdateGiveWayResume(higher, Lookup(higher, lower), deltaSeconds: 1);

        Assert.NotNull(higher.Ground.Hold);
    }

    [Fact]
    public void MutualYield_LowerCallsign_Releases()
    {
        var lower = MakeGround("AAA1", new LatLon(BaseLat, BaseLon), heading: 0);
        lower.Ground.Hold = HoldDirective.GiveWay("ZZZ9");
        var higher = MakeGround("ZZZ9", new LatLon(BaseLat, BaseLon + 0.01), heading: 180);
        higher.Ground.Hold = HoldDirective.GiveWay("AAA1");
        higher.Ground.StationarySeconds = GiveWayConstants.TargetStationaryThresholdSeconds + 1;

        FlightPhysics.UpdateGiveWayResume(lower, Lookup(lower, higher), deltaSeconds: 1);

        Assert.Null(lower.Ground.Hold);
    }

    [Fact]
    public void StationaryTimer_AccumulatesWhenStopped_ResetsWhenMoving()
    {
        var ac = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0);

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
        var ac = MakeGround("AAL1", new LatLon(BaseLat, BaseLon), heading: 0, gs: 0);
        ac.Ground.StationarySeconds = 42;
        ac.IsOnGround = false;

        FlightPhysics.UpdateGroundStationaryTimer(ac, deltaSeconds: 5);

        Assert.Equal(0, ac.Ground.StationarySeconds);
    }
}
