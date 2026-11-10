using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The closing direction a crossing pair is resolved on is the path the aircraft will drive, not its nose
/// (<see cref="GroundConflictDetector.ClosingDirectionDeg"/>): the bearing to a point about half its length (floored at 50 ft)
/// ahead along the route it drives, a push's reverse heading, and nothing at all for a parked or held aircraft.
/// <see cref="GroundConflictDetector.ChooseMutualStopHolder"/> then breaks the remaining tie by ICAO Annex 2
/// 3.2.2.7.1 — the aircraft with the other on its right gives way — falling back to the callsign order only for a near head-on
/// pair, which ICAO leaves as "each shall stop".
///
/// Aircraft with real routes are built on the committed KOAK layout through <see cref="FollowCornerGeometry"/> (the detector
/// tests' OAK harness), and each row sets the pose and the route's current segment on the aircraft directly.
/// </summary>
[Collection("GroundConflictDebugSink")]
public class GroundConflictClosingDirectionTests
{
    private const double FtPerNm = 6076.12;

    /// <summary>The look-ahead floor every light aircraft's half-length falls under.</summary>
    private const double LookAheadFloorFt = 50.0;

    private readonly ITestOutputHelper _output;

    public GroundConflictClosingDirectionTests(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        _output = output;
    }

    /// <summary>Row 1: a taxiing aircraft whose nose is turned 90° off its route reads the route's direction, not the nose.</summary>
    [Fact]
    public void TaxiingNoseOffTheRoute_ReadsTheRouteDirection()
    {
        if (TaxiingOnB("N1DIR") is not { } taxi)
        {
            return;
        }

        TaxiRoute route = taxi.Route;
        int index = FirstLongLeg(route, minFt: 200.0);
        route.CurrentSegmentIndex = index;
        TaxiRouteSegment first = route.Segments[index];
        taxi.Aircraft.Position = first.Edge.FromNode.Position;

        double routeDeg = GeoMath.BearingTo(taxi.Aircraft.Position, first.Edge.ToNode.Position);
        taxi.Aircraft.TrueHeading = new TrueHeading((routeDeg + 90.0) % 360.0);

        double? closing = GroundConflictDetector.ClosingDirectionDeg(taxi.Aircraft);
        _output.WriteLine($"route {routeDeg:F1}°, nose {taxi.Aircraft.TrueHeading.Degrees:F1}°, closing {closing}");

        Assert.NotNull(closing);
        Assert.True(Diff(closing.Value, routeDeg) <= 5.0, $"closing {closing.Value:F1}° is not the route's {routeDeg:F1}°: the nose decided it");
        Assert.True(
            Diff(closing.Value, taxi.Aircraft.TrueHeading.Degrees) > 60.0,
            $"closing {closing.Value:F1}° is within 60° of the nose {taxi.Aircraft.TrueHeading.Degrees:F1}°, so the row does not discriminate"
        );
    }

    /// <summary>Row 2: a 10 ft stub onto a turn reads the point L ahead, past the stub, not the stub's own chord.</summary>
    [Fact]
    public void ShortFirstLeg_ReadsPastTheStub()
    {
        if (TaxiingOnB("N2DIR") is not { } taxi)
        {
            return;
        }

        TaxiRoute route = taxi.Route;
        int index = CornerIndex(route, minTurnDeg: 45.0, minOutFt: 60.0);
        TaxiRouteSegment stub = route.Segments[index];
        TaxiRouteSegment turned = route.Segments[index + 1];
        double stubFt = stub.Edge.DistanceNm * FtPerNm;
        route.CurrentSegmentIndex = index;
        LatLon start = FollowCornerGeometry.Between(stub.Edge.ToNode.Position, stub.Edge.FromNode.Position, 10.0 / stubFt);
        taxi.Aircraft.Position = start;
        taxi.Aircraft.TrueHeading = FollowCornerGeometry.Facing(stub.Edge.FromNode, stub.Edge.ToNode);
        taxi.Aircraft.Phases = null;
        taxi.Aircraft.IndicatedAirspeed = 5.0;

        double stubDeg = GeoMath.BearingTo(start, stub.Edge.ToNode.Position);
        LatLon expected = GeoMath.ProjectPoint(
            stub.Edge.ToNode.Position,
            FollowCornerGeometry.Facing(turned.Edge.FromNode, turned.Edge.ToNode),
            (LookAheadFloorFt - 10.0) / FtPerNm
        );
        double expectedDeg = GeoMath.BearingTo(start, expected);

        double? closing = GroundConflictDetector.ClosingDirectionDeg(taxi.Aircraft);
        _output.WriteLine($"stub chord {stubDeg:F1}°, look-ahead {expectedDeg:F1}°, closing {closing}");

        Assert.True(Diff(expectedDeg, stubDeg) > 20.0, "the look-ahead bearing is too close to the stub's chord to discriminate");
        Assert.NotNull(closing);
        Assert.True(
            Diff(closing.Value, expectedDeg) <= 5.0,
            $"closing {closing.Value:F1}° is not the look-ahead {expectedDeg:F1}° — the stub's chord was read instead"
        );
    }

    /// <summary>Row 3: a route shorter than the look-ahead reads its last node.</summary>
    [Fact]
    public void RouteShorterThanTheLookAhead_ReadsTheLastNode()
    {
        if (TaxiingOnB("N3DIR") is not { } taxi)
        {
            return;
        }

        TaxiRoute route = taxi.Route;
        int index = route.Segments.Count - 1;
        TaxiRouteSegment last = route.Segments[index];
        double lastFt = last.Edge.DistanceNm * FtPerNm;
        Assert.True(lastFt > 30.0, $"the last leg is only {lastFt:F0} ft, so the 30 ft pose does not fit");
        route.CurrentSegmentIndex = index;
        LatLon start = FollowCornerGeometry.Between(last.Edge.ToNode.Position, last.Edge.FromNode.Position, 30.0 / lastFt);
        taxi.Aircraft.Position = start;
        taxi.Aircraft.Phases = null;
        taxi.Aircraft.IndicatedAirspeed = 5.0;

        double lastNodeDeg = GeoMath.BearingTo(start, last.Edge.ToNode.Position);
        taxi.Aircraft.TrueHeading = new TrueHeading((lastNodeDeg + 90.0) % 360.0);

        double? closing = GroundConflictDetector.ClosingDirectionDeg(taxi.Aircraft);
        _output.WriteLine($"last node {lastNodeDeg:F1}°, nose {taxi.Aircraft.TrueHeading.Degrees:F1}°, closing {closing}");

        Assert.NotNull(closing);
        Assert.True(Diff(closing.Value, lastNodeDeg) <= 5.0, $"closing {closing.Value:F1}° is not the last node's {lastNodeDeg:F1}°");
    }

    /// <summary>Row 4: a push leg closes on the reverse of the nose.</summary>
    [Fact]
    public void PushLeg_ReadsTheReverseOfTheNose()
    {
        if (FollowCornerGeometry.NewEngine(_output, AutoCrossRunwayMode.All) is not { } setup)
        {
            return;
        }

        double noseDeg = 90.0;
        var pose = new LatLon(37.72, -122.22);
        LatLon legEnd = GeoMath.ProjectPoint(pose, new TrueHeading(noseDeg), 200.0 / FtPerNm);
        var aircraft = new AircraftState
        {
            Callsign = "N4DIR",
            AircraftType = "C172",
            Position = pose,
            TrueHeading = new TrueHeading(noseDeg),
            IsOnGround = true,
            IndicatedAirspeed = 5.0,
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(
            new PushbackPhase
            {
                Move = TugMove.ToPoint(PushbackLegKind.Push, legEnd),
                PlannedEnd = legEnd,
                StartsAtStand = true,
                ContinuesIntoNextMove = false,
                ContinuesStandPushOff = true,
            }
        );
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, setup.Layout));
        aircraft.Ground.Layout = setup.Layout;

        double? closing = GroundConflictDetector.ClosingDirectionDeg(aircraft);
        _output.WriteLine($"nose {noseDeg:F1}°, closing {closing}");

        Assert.NotNull(closing);
        Assert.True(Diff(closing.Value, (noseDeg + 180.0) % 360.0) <= 1.0, $"closing {closing.Value:F1}° is not the push track's 270°");
    }

    /// <summary>Row 5: a parked or held aircraft is an obstacle and has no closing direction.</summary>
    [Fact]
    public void ParkedOrHeld_HasNoDirection()
    {
        if (FollowCornerGeometry.NewEngine(_output, AutoCrossRunwayMode.All) is not { } setup)
        {
            return;
        }

        AircraftState parked = FollowCornerGeometry.SpawnAtStand(setup.Layout, "N5DIR");
        Assert.True(GroundConflictDetector.IsParkedOrHeld(parked), "the aircraft is not parked-or-held, so this row proves nothing");

        Assert.Null(GroundConflictDetector.ClosingDirectionDeg(parked));
    }

    /// <summary>Row 6: no route and at rest but not parked-or-held keeps its heading — a yielder braked for the conflict stays pinned.</summary>
    [Fact]
    public void NoRouteAtRest_ReadsTheNose()
    {
        AircraftState aircraft = FollowCornerGeometry.Spawn("OAK", "N6DIR", "C172", new LatLon(37.72, -122.22), new TrueHeading(45));
        aircraft.Phases = null;
        aircraft.IndicatedAirspeed = 0.0;

        double? closing = GroundConflictDetector.ClosingDirectionDeg(aircraft);

        Assert.NotNull(closing);
        Assert.True(Diff(closing.Value, 45.0) <= 1.0, $"closing {closing.Value:F1}° is not the nose's 45°");
    }

    /// <summary>Row 7: a mover with no route keeps its heading.</summary>
    [Fact]
    public void MovingWithNoRoute_ReadsTheNose()
    {
        AircraftState aircraft = FollowCornerGeometry.Spawn("OAK", "N7DIR", "C172", new LatLon(37.72, -122.22), new TrueHeading(45));
        aircraft.Phases = null;
        aircraft.IndicatedAirspeed = 12.0;

        double? closing = GroundConflictDetector.ClosingDirectionDeg(aircraft);

        Assert.NotNull(closing);
        Assert.True(Diff(closing.Value, 45.0) <= 1.0, $"closing {closing.Value:F1}° is not the nose's 45°");
    }

    /// <summary>Row 8: a FOLLOWG follower reads its follow route, not the assigned route it left stale.</summary>
    [Fact]
    public void Follower_ReadsItsFollowRoute_NotAStaleAssignedRoute()
    {
        if (FollowCornerGeometry.StartTaxiingLead(_output) is not { } run)
        {
            return;
        }

        AircraftState follower = FollowCornerGeometry.AddTaxiing(
            (run.Engine, run.Layout),
            "N8DIR",
            "C172",
            (run.Chain[5], run.Chain[4]),
            "TAXI B W 30"
        );
        CommandResult result = run.Engine.SendCommand("N8DIR", $"FOLLOWG {run.Lead.Callsign}");
        Assert.True(result.Success, result.Message);
        for (
            int second = 0;
            (second < 60) && !((follower.Phases?.CurrentPhase is FollowingPhase { FollowRoute.IsComplete: false }) && (follower.GroundSpeed >= 5.0));
            second++
        )
        {
            run.Engine.TickOneSecond();
        }

        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        TaxiRoute followRoute = Assert.IsType<TaxiRoute>(follow.FollowRoute);
        TaxiRouteSegment driving = Assert.IsType<TaxiRouteSegment>(followRoute.CurrentSegment);
        double drivingFt = driving.Edge.DistanceNm * FtPerNm;
        Assert.True(drivingFt >= (LookAheadFloorFt + 5.0), $"the follow route's current leg is only {drivingFt:F0} ft, inside the look-ahead");
        double followDeg = GeoMath.BearingTo(follower.Position, driving.Edge.ToNode.Position);

        TaxiRoute stale = Route(Segment(driving.Edge.ToNode, driving.Edge.FromNode, driving.Edge.Edge));
        double staleDeg = GeoMath.BearingTo(follower.Position, driving.Edge.FromNode.Position);
        Assert.True(Diff(followDeg, staleDeg) > 30.0, "the stale route leads nearly the same way as the follow route, so this row proves nothing");
        follower.Ground.AssignedTaxiRoute = stale;

        double? closing = GroundConflictDetector.ClosingDirectionDeg(follower);
        _output.WriteLine($"follow route {followDeg:F1}°, stale route {staleDeg:F1}°, closing {closing}");

        Assert.NotNull(closing);
        Assert.True(Diff(closing.Value, followDeg) <= 5.0, $"closing {closing.Value:F1}° is not the follow route's {followDeg:F1}°");
    }

    /// <summary>Row 9: two aircraft converging at 90° — the one with the other on its right holds.</summary>
    [Fact]
    public void CrossingNinetyDegrees_TheRightHandAircraftHolds()
    {
        (AircraftState a, AircraftState b) = Converging90("AAA1", "ZZZ1");
        Assert.True(string.CompareOrdinal(a.Callsign, b.Callsign) < 0, "the callsign order must pick B here, or the row does not discriminate");

        AircraftState holder = GroundConflictDetector.ChooseMutualStopHolder(a, 0.0, b, 270.0);
        _output.WriteLine($"holder {holder.Callsign}");
        Assert.Same(a, holder);
    }

    /// <summary>
    /// Row 10: the same geometry with the callsigns swapped — the right-hand rule still picks the aircraft with the other on its
    /// right.
    /// </summary>
    [Fact]
    public void CallsignSwapped_TheRightHandRuleStillPicksTheHolder()
    {
        (AircraftState a, AircraftState b) = Converging90("ZZZ1", "AAA1");

        AircraftState holder = GroundConflictDetector.ChooseMutualStopHolder(a, 0.0, b, 270.0);
        _output.WriteLine($"holder {holder.Callsign}");
        Assert.Same(a, holder);
    }

    /// <summary>Row 11: near head-on keeps the callsign order, since ICAO's head-on rule names no single holder.</summary>
    [Fact]
    public void NearHeadOn_CallsignOrderDecides()
    {
        var basePos = new LatLon(37.72, -122.22);
        LatLon north = GeoMath.ProjectPoint(basePos, new TrueHeading(0), 400.0 / FtPerNm);
        AircraftState a = Plain("AAA1", basePos, headingDeg: 0);
        AircraftState b = Plain("ZZZ1", north, headingDeg: 175);

        double offNoseA = Diff(0.0, GeoMath.BearingTo(a.Position, b.Position));
        double offNoseB = Diff(175.0, GeoMath.BearingTo(b.Position, a.Position));
        Assert.True(Math.Abs(offNoseA - offNoseB) < 30.0, "the off-nose rule decides this pair, so the head-on row proves nothing");

        AircraftState holder = GroundConflictDetector.ChooseMutualStopHolder(a, 0.0, b, 175.0);
        _output.WriteLine($"holder {holder.Callsign}");
        Assert.Same(b, holder);
    }

    /// <summary>
    /// Row 12: the issue's shape — one aircraft's nose points at the other while its route leads away. The holder is decided
    /// by the routes, not by the callsign order a nose-based reading falls through to.
    /// </summary>
    [Fact]
    public void NoseAtTheOtherButRouteAway_ReadsTheRoutes()
    {
        if (TaxiingOnB("N9DIR") is not { } taxi)
        {
            return;
        }

        TaxiRoute route = taxi.Route;
        int index = FirstLongLeg(route, minFt: 250.0);
        TaxiRouteSegment leg = route.Segments[index];
        double legFt = leg.Edge.DistanceNm * FtPerNm;
        LatLon from = leg.Edge.FromNode.Position;
        LatLon aPos = FollowCornerGeometry.Between(from, leg.Edge.ToNode.Position, 150.0 / legFt);
        LatLon bPos = FollowCornerGeometry.Between(from, leg.Edge.ToNode.Position, 50.0 / legFt);
        TaxiRoute path = Route([.. route.Segments.Skip(index)]);

        AircraftState a = Plain("ZZZ9", aPos, headingDeg: GeoMath.BearingTo(aPos, bPos));
        a.Ground.AssignedTaxiRoute = path;
        AircraftState b = Plain("AAA1", bPos, headingDeg: GeoMath.BearingTo(bPos, leg.Edge.ToNode.Position));
        b.Ground.AssignedTaxiRoute = path;

        double? dirA = GroundConflictDetector.ClosingDirectionDeg(a);
        double? dirB = GroundConflictDetector.ClosingDirectionDeg(b);
        Assert.NotNull(dirA);
        Assert.NotNull(dirB);
        double aToB = GeoMath.BearingTo(a.Position, b.Position);
        double bToA = GeoMath.BearingTo(b.Position, a.Position);
        _output.WriteLine(
            $"A nose {a.TrueHeading.Degrees:F1}° closes {dirA.Value:F1}° toward B at {aToB:F1}°; B closes {dirB.Value:F1}° toward A at {bToA:F1}°"
        );

        Assert.True(Diff(dirA.Value, aToB) > 90.0, "A's route does not lead away from B, so this row does not reproduce the issue");
        Assert.True(
            Math.Abs(Diff(dirA.Value, aToB) - Diff(dirB.Value, bToA)) >= 30.0,
            "the off-nose rule cannot decide this pair, so the row proves nothing about the route directions"
        );

        AircraftState holder = GroundConflictDetector.ChooseMutualStopHolder(a, dirA.Value, b, dirB.Value);
        _output.WriteLine($"holder {holder.Callsign}");
        Assert.Same(b, holder);
        Assert.True(
            string.CompareOrdinal(a.Callsign, b.Callsign) > 0,
            "the callsign order would have held A, so the row does not show the routes deciding"
        );
    }

    /// <summary>
    /// Row 13: a route whose current segment has its far node behind the aircraft does not describe the pose — the segment index
    /// has not caught up — so the nose is read.
    /// </summary>
    [Fact]
    public void RouteSegmentBehindTheAircraft_ReadsTheNose()
    {
        if (TaxiingOnB("N13DIR") is not { } taxi)
        {
            return;
        }

        TaxiRoute route = taxi.Route;
        int index = FirstLongLeg(route, minFt: 200.0);
        route.CurrentSegmentIndex = index;
        TaxiRouteSegment leg = route.Segments[index];
        double legDeg = GeoMath.BearingTo(leg.Edge.FromNode.Position, leg.Edge.ToNode.Position);
        taxi.Aircraft.Position = GeoMath.ProjectPoint(leg.Edge.ToNode.Position, new TrueHeading(legDeg), 100.0 / FtPerNm);
        taxi.Aircraft.TrueHeading = new TrueHeading((legDeg + 30.0) % 360.0);
        taxi.Aircraft.Phases = null;
        taxi.Aircraft.IndicatedAirspeed = 5.0;

        double? closing = GroundConflictDetector.ClosingDirectionDeg(taxi.Aircraft);
        _output.WriteLine($"leg {legDeg:F1}°, nose {taxi.Aircraft.TrueHeading.Degrees:F1}°, closing {closing}");

        Assert.NotNull(closing);
        Assert.True(
            Diff(closing.Value, taxi.Aircraft.TrueHeading.Degrees) <= 1.0,
            $"closing {closing.Value:F1}° is not the nose's {taxi.Aircraft.TrueHeading.Degrees:F1}°"
        );
        Assert.True(Diff(closing.Value, (legDeg + 180.0) % 360.0) > 90.0, "the backed-off leg's direction was read instead of the nose");
    }

    /// <summary>Row 14: a degenerate current segment (both nodes in one place) names no direction, so the nose is read.</summary>
    [Fact]
    public void DegenerateCurrentSegment_ReadsTheNose()
    {
        if (TaxiingOnB("N14DIR") is not { } taxi)
        {
            return;
        }

        TaxiRouteSegment leg = taxi.Route.Segments[0];
        GroundNode node = leg.Edge.FromNode;
        AircraftState aircraft = Plain("N14DIR", node.Position, headingDeg: 120);
        aircraft.Ground.AssignedTaxiRoute = Route(Segment(node, node, leg.Edge.Edge));

        double? closing = GroundConflictDetector.ClosingDirectionDeg(aircraft);

        Assert.NotNull(closing);
        Assert.True(Diff(closing.Value, 120.0) <= 1.0, $"closing {closing.Value:F1}° is not the nose's 120°");
    }

    /// <summary>A C172 taxiing on KOAK's B under <c>TAXI B W 30</c>, with its real route, or null when the layout is unavailable.</summary>
    private (TaxiRoute Route, AircraftState Aircraft)? TaxiingOnB(string callsign)
    {
        if (FollowCornerGeometry.NewEngine(_output, AutoCrossRunwayMode.All) is not { } setup)
        {
            return null;
        }

        List<GroundNode> chain = FollowCornerGeometry.BChain(setup.Layout);
        AircraftState aircraft = FollowCornerGeometry.AddTaxiing((setup.Engine, setup.Layout), callsign, "C172", (chain[2], chain[1]), "TAXI B W 30");
        return (Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute), aircraft);
    }

    /// <summary>
    /// The first index of <paramref name="route"/> whose next segment turns by more than <paramref name="minTurnDeg"/> onto a
    /// leg at least <paramref name="minOutFt"/> long.
    /// </summary>
    private static int CornerIndex(TaxiRoute route, double minTurnDeg, double minOutFt)
    {
        for (int i = route.CurrentSegmentIndex; i + 1 < route.Segments.Count; i++)
        {
            TaxiRouteSegment inSegment = route.Segments[i];
            TaxiRouteSegment outSegment = route.Segments[i + 1];
            double turnDeg = Diff(
                GeoMath.BearingTo(inSegment.Edge.FromNode.Position, inSegment.Edge.ToNode.Position),
                GeoMath.BearingTo(outSegment.Edge.FromNode.Position, outSegment.Edge.ToNode.Position)
            );
            if ((turnDeg > minTurnDeg) && (outSegment.Edge.DistanceNm * FtPerNm >= minOutFt))
            {
                return i;
            }
        }

        throw new Xunit.Sdk.XunitException($"route {route.ToSummary()} has no turn over {minTurnDeg:F0}° onto a {minOutFt:F0} ft leg");
    }

    /// <summary>The first index of <paramref name="route"/> whose segment is at least <paramref name="minFt"/> long.</summary>
    private static int FirstLongLeg(TaxiRoute route, double minFt)
    {
        for (int i = route.CurrentSegmentIndex; i < route.Segments.Count; i++)
        {
            if (route.Segments[i].Edge.DistanceNm * FtPerNm >= minFt)
            {
                return i;
            }
        }

        throw new Xunit.Sdk.XunitException($"route {route.ToSummary()} has no leg at least {minFt:F0} ft long");
    }

    private static TaxiRoute Route(params TaxiRouteSegment[] segments) => new() { Segments = [.. segments], HoldShortPoints = [] };

    private static TaxiRouteSegment Segment(GroundNode from, GroundNode to, IGroundEdge edge) =>
        new()
        {
            Edge = new DirectionalEdge
            {
                Edge = edge,
                FromNode = from,
                ToNode = to,
            },
            TaxiwayName = edge.TaxiwayName,
        };

    private static AircraftState Plain(string callsign, LatLon position, double headingDeg) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            TrueHeading = new TrueHeading(headingDeg),
            IsOnGround = true,
            IndicatedAirspeed = 5.0,
        };

    /// <summary>
    /// A 90° crossing where each aircraft has the other 45° off its nose: A northbound, B northeast of it heading west, so A has B
    /// on its right and B has A on its left, and neither the off-nose rule nor the follower rule decides the pair.
    /// </summary>
    private static (AircraftState A, AircraftState B) Converging90(string callsignA, string callsignB)
    {
        var basePos = new LatLon(37.72, -122.22);
        LatLon northeast = GeoMath.ProjectPoint(basePos, new TrueHeading(45), 500.0 / FtPerNm);
        AircraftState a = Plain(callsignA, basePos, headingDeg: 0);
        AircraftState b = Plain(callsignB, northeast, headingDeg: 270);
        Assert.True(
            Math.Abs(Diff(0.0, GeoMath.BearingTo(a.Position, b.Position)) - Diff(270.0, GeoMath.BearingTo(b.Position, a.Position))) < 30.0,
            "the off-nose rule decides the pair, so the right-hand rows prove nothing"
        );
        return (a, b);
    }

    private static double Diff(double a, double b) => Math.Abs(GeoMath.SignedBearingDifference(a, b));
}
