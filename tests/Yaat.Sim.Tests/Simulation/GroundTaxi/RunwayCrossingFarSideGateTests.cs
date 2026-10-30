using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The near-line gate (<see cref="RunwayCrossingFarSideGate"/>) on SFO's 01L/19R crossing on F, judged for an E75L short of the
/// near bar against E75Ls placed on the path across and beyond the far bar, each in a given state of motion. With no taxi route
/// the path runs straight on along the way across past the far bar.
/// </summary>
public class RunwayCrossingFarSideGateTests(ITestOutputHelper output)
{
    private const string AircraftType = "E75L";

    /// <summary>A taxi to 28L on F, crossing 01L/19R and then 01R/19L.</summary>
    private const string NextTaxiCommand = "TAXI F 28L";

    /// <summary>How far (ft) short of the next runway's near holding-position marking the rolling leader's nose is.</summary>
    private const double LeaderNoseShortFt = 30.0;

    /// <summary>The leader's steady taxi speed (kt).</summary>
    private const double LeaderKts = 10.0;

    /// <summary>How far (ft) past the next runway's far holding-position marking the traffic at rest has its tail.</summary>
    private const double TrafficTailPastNextFarFt = 20.0;

    /// <summary>
    /// A leader just past the far bar slowing to a fillet's corner speed is not stopping: it leaves the room the follower
    /// needs, so the gate stays open.
    /// </summary>
    [Fact]
    public void LeaderJustPastTheFarBar_SlowingToACornerSpeed_LeavesRoom()
    {
        if (Gate() is not { } gate)
        {
            return;
        }

        AircraftState leader = gate.Place("LED", tailPastFarFt: 20.0, awayFromTheCrossing: true, speedKts: 15.0);
        leader.Targets.TargetSpeed = 8.0;

        Assert.False(gate.IsBlocked(resuming: false), "a leader slowing to 8 kt for a corner was taken for a stop");
    }

    /// <summary>
    /// A leader just past the far bar held by its ground-conflict speed limit at zero, its own speed target still at taxi
    /// speed, is braking to a stop there: the gate closes.
    /// </summary>
    [Fact]
    public void LeaderJustPastTheFarBar_HeldByItsGroundConflictLimit_Blocks()
    {
        if (Gate() is not { } gate)
        {
            return;
        }

        AircraftState leader = gate.Place("LED", tailPastFarFt: 20.0, awayFromTheCrossing: true, speedKts: 15.0);
        leader.Targets.TargetSpeed = 15.0;
        leader.Ground.SpeedLimit = 0.0;

        Assert.True(gate.IsBlocked(resuming: false), "a leader braked to a stop by its ground-conflict limit was not seen stopping");
    }

    /// <summary>
    /// An aircraft beyond the far bar taxiing at a steady speed toward the crossing comes into the room the follower needs:
    /// steady taxi leaves room only for traffic moving away along the path.
    /// </summary>
    [Fact]
    public void OncomingAircraftBeyondTheFarBar_Blocks()
    {
        if (Gate() is not { } gate)
        {
            return;
        }

        AircraftState oncoming = gate.Place("ONC", tailPastFarFt: 250.0, awayFromTheCrossing: false, speedKts: 15.0);
        oncoming.Targets.TargetSpeed = 15.0;

        Assert.True(gate.IsBlocked(resuming: false), "an aircraft taxiing toward the crossing beyond the far bar left the gate open");
    }

    /// <summary>
    /// A queue beyond the far bar: an aircraft at rest far enough out to leave the follower its room on its own, and a second
    /// one taxiing steadily away just past the far bar. Alone the taxiing one leaves room; behind the one at rest it will stop
    /// at the stop gap behind it, too close to the far bar, so the gate closes.
    /// </summary>
    [Fact]
    public void QueueBeyondTheFarBar_SteadyTaxiBehindAnAircraftAtRest_Blocks()
    {
        if (Gate() is not { } gate)
        {
            return;
        }

        AircraftState steady = gate.Place("STD", tailPastFarFt: 20.0, awayFromTheCrossing: true, speedKts: 10.0);
        steady.Targets.TargetSpeed = 10.0;
        Assert.False(gate.IsBlocked(resuming: false), "a lone aircraft taxiing steadily away beyond the far bar closed the gate");

        gate.Place("RST", tailPastFarFt: 200.0, awayFromTheCrossing: true, speedKts: 0.0);
        Assert.False(gate.WithOnly("RST").IsBlocked(resuming: false), "a lone aircraft at rest 200 ft past the far bar closed the gate");
        Assert.True(gate.IsBlocked(resuming: false), "the aircraft taxiing into the queue behind the one at rest left the gate open");
    }

    /// <summary>
    /// Traffic at rest whose tail is a little more than the room the follower needs past the far bar opens the gate for an
    /// aircraft still rolling toward the bar, but not for one at rest at the near line: that one resumes only with
    /// <see cref="RunwayCrossingFarSideGate.ResumeMarginFt"/> more.
    /// </summary>
    [Fact]
    public void TrafficAtRestJustBeyondTheNeededRoom_OpensForARollingAircraftButNotForAResume()
    {
        if (Gate() is not { } gate)
        {
            return;
        }

        double lengthFt = AircraftLength.ResolveFt(AircraftType);
        double neededFt = lengthFt + GroundConflictDetector.StopGapFt(lengthFt, lengthFt) + RunwayTailClearance.MarginFt;
        gate.Place("RST", tailPastFarFt: neededFt + (RunwayCrossingFarSideGate.ResumeMarginFt / 2.0), awayFromTheCrossing: true, speedKts: 0.0);

        Assert.False(gate.IsBlocked(resuming: false), "the room plus half the resume margin held a rolling aircraft");
        Assert.True(gate.IsBlocked(resuming: true), "the room plus half the resume margin let an aircraft at rest resume");
    }

    /// <summary>
    /// An aircraft at rest with its nose short of the near holding-position marking has not committed to the crossing even
    /// when its painted stop reads a fraction of a foot behind it; one rolling at taxi speed a few feet short of the stop has.
    /// </summary>
    [Fact]
    public void IsCommitted_AtRestShortOfTheMarkingThoughPastItsStop_IsNot_RollingIntoIt_Is()
    {
        if (Gate() is not { } gate)
        {
            return;
        }

        gate.PlaceSelf(noseShortFt: 1.0, speedKts: 0.0);
        Assert.False(gate.IsCommitted(toStopFt: -0.3), "an aircraft at rest with its nose short of the marking was taken as committed");

        gate.PlaceSelf(noseShortFt: 5.0, speedKts: 15.0);
        Assert.True(gate.IsCommitted(toStopFt: 5.0), "an aircraft rolling at 15 kt 5 ft short of its stop was not taken as committed");
    }

    /// <summary>
    /// A leader beyond the 01L/19R far bar, rolling steadily at taxi speed toward the 01R/19L near line with that crossing
    /// cleared, is held at that line by its own near-line gate (traffic at rest just past the 01R/19L far bar): it will stop
    /// there, its tail too close to the 01L/19R far bar, so the follower's gate at 01L/19R closes and the follower holds at the
    /// 01L/19R near line. Without the traffic the leader's gate is open, it rolls on, and the follower's gate stays open.
    /// </summary>
    [Fact]
    public void LeaderHeldByItsOwnGateAtTheNextRunwaysNearLine_Blocks()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        var approach = new TrueHeading(GeoMath.BearingTo(crossing.ApproachNode.Position, crossing.ApproachBar.Position));
        AircraftState self = SfoGroundHarness.SpawnAt(
            ground,
            "SKW402",
            AircraftType,
            (crossing.ApproachNode, approach),
            new HoldingInPositionPhase()
        );
        Assert.True(ground.Engine.SendCommand(self.Callsign, NextTaxiCommand).Success, $"'{NextTaxiCommand}' was refused for {self.Callsign}");
        var across = new TrueHeading(GeoMath.BearingTo(crossing.ApproachBar.Position, crossing.FarBar.Position));
        AircraftState leader = SfoGroundHarness.SpawnAt(ground, "LED", AircraftType, (crossing.FarBar, across), new HoldingInPositionPhase());
        Assert.True(ground.Engine.SendCommand(leader.Callsign, NextTaxiCommand).Success, $"'{NextTaxiCommand}' was refused for {leader.Callsign}");

        TaxiRoute leaderRoute = Assert.IsType<TaxiRoute>(leader.Ground.AssignedTaxiRoute);
        (int barIndex, HoldShortPoint nextBar, GroundNode nextFar) = NextCrossingOnRoute(ground.Layout, leaderRoute);
        nextBar.IsCleared = true;
        GroundNode nextNear = ground.Layout.Nodes[nextBar.NodeId];
        double halfLengthFt = AircraftLength.ResolveFt(AircraftType) / 2.0;
        PlaceBackAlongRoute(leader, leaderRoute, barIndex, halfLengthFt + LeaderNoseShortFt);
        leader.IndicatedAirspeed = LeaderKts;
        leader.Targets.TargetSpeed = LeaderKts;

        var nextAcross = new TrueHeading(GeoMath.BearingTo(nextNear.Position, nextFar.Position));
        AircraftState traffic = SfoGroundHarness.SpawnAt(ground, "RST", AircraftType, (nextFar, nextAcross), new HoldingInPositionPhase());
        traffic.Position = GeoMath.ProjectPoint(nextFar.Position, nextAcross, (TrafficTailPastNextFarFt + halfLengthFt) / GeoMath.FeetPerNm);

        TaxiRoute selfRoute = Assert.IsType<TaxiRoute>(self.Ground.AssignedTaxiRoute);
        int beyondStart = selfRoute.Segments.FindIndex(s => s.ToNodeId == crossing.FarBar.Id) + 1;
        Assert.True(beyondStart > 0, $"{self.Callsign}'s route does not reach the {SfoGroundHarness.CrossingRunway} far bar");
        bool Blocked(List<AircraftState> all) =>
            RunwayCrossingFarSideGate.IsBlocked(
                GateContext(ground.Layout, self, all),
                (crossing.ApproachBar.Id, crossing.FarBar.Id),
                (selfRoute, beyondStart),
                resuming: false,
                judgeLeadersGates: true
            );

        output.WriteLine($"{leader.Callsign} {LeaderNoseShortFt:F0} ft short of the next near marking at {LeaderKts:F0} kt");
        Assert.True(Blocked([self, leader, traffic]), "a leader held at the next runway's near line by its own gate was projected to roll on");
        Assert.False(Blocked([self, leader]), "a leader whose own gate is open, rolling on at taxi speed, closed the gate");
    }

    /// <summary>
    /// A leader beyond the 01L/19R far bar rolling toward the 01R/19L near line, that crossing not cleared, will stop at that
    /// bar, too close to the 01L/19R far bar: the follower's gate closes. A GIVEWAY the leader holds to traffic whose route
    /// crosses its own about 3,000 ft on sets a give-way stop out there, but the bar is nearer and still stops it first.
    /// </summary>
    [Fact]
    public void LeaderGivingWayFarBeyondAnUnclearedBar_IsProjectedToStopAtTheBar()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        var approach = new TrueHeading(GeoMath.BearingTo(crossing.ApproachNode.Position, crossing.ApproachBar.Position));
        AircraftState self = SfoGroundHarness.SpawnAt(
            ground,
            "SKW402",
            AircraftType,
            (crossing.ApproachNode, approach),
            new HoldingInPositionPhase()
        );
        Assert.True(ground.Engine.SendCommand(self.Callsign, NextTaxiCommand).Success, $"'{NextTaxiCommand}' was refused for {self.Callsign}");
        var across = new TrueHeading(GeoMath.BearingTo(crossing.ApproachBar.Position, crossing.FarBar.Position));
        AircraftState leader = SfoGroundHarness.SpawnAt(ground, "LED", AircraftType, (crossing.FarBar, across), new HoldingInPositionPhase());
        Assert.True(ground.Engine.SendCommand(leader.Callsign, NextTaxiCommand).Success, $"'{NextTaxiCommand}' was refused for {leader.Callsign}");

        TaxiRoute leaderRoute = Assert.IsType<TaxiRoute>(leader.Ground.AssignedTaxiRoute);
        (int barIndex, HoldShortPoint nextBar, _) = NextCrossingOnRoute(ground.Layout, leaderRoute);
        nextBar.IsCleared = false;
        double halfLengthFt = AircraftLength.ResolveFt(AircraftType) / 2.0;
        PlaceBackAlongRoute(leader, leaderRoute, barIndex, halfLengthFt + LeaderNoseShortFt);
        leader.IndicatedAirspeed = LeaderKts;
        leader.Targets.TargetSpeed = LeaderKts;

        TaxiRoute selfRoute = Assert.IsType<TaxiRoute>(self.Ground.AssignedTaxiRoute);
        int beyondStart = selfRoute.Segments.FindIndex(s => s.ToNodeId == crossing.FarBar.Id) + 1;
        Assert.True(beyondStart > 0, $"{self.Callsign}'s route does not reach the {SfoGroundHarness.CrossingRunway} far bar");
        bool Blocked(List<AircraftState> all) =>
            RunwayCrossingFarSideGate.IsBlocked(
                GateContext(ground.Layout, self, all),
                (crossing.ApproachBar.Id, crossing.FarBar.Id),
                (selfRoute, beyondStart),
                resuming: false,
                judgeLeadersGates: true
            );
        Assert.True(Blocked([self, leader]), "test setup: a leader rolling to an uncleared bar it stops at should close the gate");

        AircraftState traffic = CrossingTrafficAbout(leader, leaderRoute, GiveWayJunctionAlongFt);
        leader.Ground.Hold = HoldDirective.GiveWay(traffic.Callsign);
        (int NodeId, double ToStopFt)? giveWay = GroundConflictDetector.GiveWayStop(leader, traffic, out string? noStopReason);
        Assert.True(
            giveWay is { ToStopFt: > 2500.0 },
            $"test setup: the leader's give-way stop should be far beyond the bar: {giveWay} {noStopReason}"
        );

        Assert.True(Blocked([self, leader, traffic]), "a leader giving way far beyond an uncleared bar was projected to roll past the bar");
    }

    /// <summary>How far (ft) along the leader's route its give-way traffic's route crosses it.</summary>
    private const double GiveWayJunctionAlongFt = 3000.0;

    /// <summary>
    /// An E75L whose route crosses <paramref name="leader"/>'s at the first node of <paramref name="route"/> at least
    /// <paramref name="alongFt"/> along it, entering that node from a point 500 ft square off the route's leg into it.
    /// </summary>
    private static AircraftState CrossingTrafficAbout(AircraftState leader, TaxiRoute route, double alongFt)
    {
        double walkedFt = GeoMath.DistanceNm(leader.Position, route.Segments[route.CurrentSegmentIndex].Edge.ToNode.Position) * GeoMath.FeetPerNm;
        int index = route.CurrentSegmentIndex;
        while ((walkedFt < alongFt) && (index + 1 < route.Segments.Count))
        {
            index++;
            walkedFt += route.Segments[index].Edge.DistanceNm * GeoMath.FeetPerNm;
        }

        Assert.True(walkedFt >= alongFt, $"the leader's route ends {walkedFt:F0} ft along, short of {alongFt:F0} ft");
        DirectionalEdge leg = route.Segments[index].Edge;
        double legBearing = GeoMath.BearingTo(leg.FromNode.Position, leg.ToNode.Position);
        var side = new GroundNode
        {
            Id = -1,
            Position = GeoMath.ProjectPoint(leg.ToNode.Position, new TrueHeading(legBearing + 90.0), 500.0 / GeoMath.FeetPerNm),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var edge = new GroundEdge
        {
            Nodes = [side, leg.ToNode],
            TaxiwayName = "Z",
            DistanceNm = GeoMath.DistanceNm(side.Position, leg.ToNode.Position),
        };
        var traffic = new AircraftState
        {
            Callsign = "GWT",
            AircraftType = AircraftType,
            Position = side.Position,
            TrueHeading = new TrueHeading(legBearing + 270.0),
            IsOnGround = true,
        };
        traffic.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = "Z", Edge = edge.Directed(side, leg.ToNode) }],
            HoldShortPoints = [],
        };
        return traffic;
    }

    /// <summary>
    /// The first runway crossing on <paramref name="route"/> from its current segment: the index of the segment that ends at its
    /// near bar, that bar, and the far bar node, the next runway holding-position node after it.
    /// </summary>
    private static (int BarIndex, HoldShortPoint Bar, GroundNode Far) NextCrossingOnRoute(AirportGroundLayout layout, TaxiRoute route)
    {
        for (int i = Math.Max(0, route.CurrentSegmentIndex); i < route.Segments.Count; i++)
        {
            if (route.GetHoldShortAt(route.Segments[i].ToNodeId) is not { Reason: HoldShortReason.RunwayCrossing } bar)
            {
                continue;
            }

            for (int j = i + 1; j < route.Segments.Count; j++)
            {
                if (layout.Nodes[route.Segments[j].ToNodeId] is { Type: GroundNodeType.RunwayHoldShort } far)
                {
                    return (i, bar, far);
                }
            }
        }

        Assert.Fail("the route crosses no runway");
        return default;
    }

    /// <summary>
    /// Places <paramref name="aircraft"/> on <paramref name="route"/> <paramref name="backFt"/> along it short of the end node of
    /// segment <paramref name="toIndex"/>, facing along the segment it lands on, with the route's cursor on that segment.
    /// </summary>
    private static void PlaceBackAlongRoute(AircraftState aircraft, TaxiRoute route, int toIndex, double backFt)
    {
        double leftFt = backFt;
        for (int i = toIndex; i >= Math.Max(0, route.CurrentSegmentIndex); i--)
        {
            DirectionalEdge edge = route.Segments[i].Edge;
            double segmentFt = GeoMath.DistanceNm(edge.FromNode.Position, edge.ToNode.Position) * GeoMath.FeetPerNm;
            if (segmentFt >= leftFt)
            {
                route.CurrentSegmentIndex = i;
                var back = new TrueHeading(GeoMath.BearingTo(edge.ToNode.Position, edge.FromNode.Position));
                aircraft.Position = GeoMath.ProjectPoint(edge.ToNode.Position, back, leftFt / GeoMath.FeetPerNm);
                aircraft.TrueHeading = new TrueHeading(GeoMath.BearingTo(edge.FromNode.Position, edge.ToNode.Position));
                return;
            }

            leftFt -= segmentFt;
        }

        Assert.Fail($"the route is shorter than the {backFt:F0} ft {aircraft.Callsign} is placed back along it");
    }

    /// <summary>A phase context for <paramref name="self"/> over <paramref name="layout"/> seeing the aircraft in <paramref name="all"/>.</summary>
    private static PhaseContext GateContext(AirportGroundLayout layout, AircraftState self, List<AircraftState> all) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = self,
            Targets = self.Targets,
            Category = AircraftCategorization.Categorize(self.AircraftType),
            DeltaSeconds = 1.0 / SimulationEngine.PhysicsSubTickRate,
            Logger = NullLogger.Instance,
            GroundLayout = layout,
            ListAircraft = () => all,
            AircraftLookup = callsign => all.Find(a => a.Callsign == callsign),
        };

    private GateFixture? Gate()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return null;
        }

        return new GateFixture(ground.Layout, SfoGroundHarness.ResolveSfoCrossing(ground.Layout));
    }

    /// <summary>The follower short of the near bar, the aircraft placed around it, and the gate's two questions about them.</summary>
    private sealed class GateFixture(AirportGroundLayout layout, SfoCrossing crossing)
    {
        private readonly TrueHeading _across = new(GeoMath.BearingTo(crossing.ApproachBar.Position, crossing.FarBar.Position));
        private readonly AircraftState _self = MakeAircraft("SKW402", layout, crossing);
        private readonly List<AircraftState> _others = [];

        /// <summary>
        /// Places another E75L on the way across beyond the far bar, its near end <paramref name="tailPastFarFt"/> past the far
        /// bar, facing away from the crossing or back toward it, at <paramref name="speedKts"/>.
        /// </summary>
        public AircraftState Place(string callsign, double tailPastFarFt, bool awayFromTheCrossing, double speedKts)
        {
            AircraftState other = MakeAircraft(callsign, layout, crossing);
            double centreFt = tailPastFarFt + (AircraftLength.ResolveFt(AircraftType) / 2.0);
            other.Position = GeoMath.ProjectPoint(crossing.FarBar.Position, _across, centreFt / GeoMath.FeetPerNm);
            other.TrueHeading = awayFromTheCrossing ? _across : new TrueHeading(_across.Degrees + 180.0);
            other.IndicatedAirspeed = speedKts;
            _others.Add(other);
            return other;
        }

        /// <summary>
        /// Places the follower facing across, its nose <paramref name="noseShortFt"/> short of the near bar, at
        /// <paramref name="speedKts"/>.
        /// </summary>
        public void PlaceSelf(double noseShortFt, double speedKts)
        {
            double centreShortFt = noseShortFt + (AircraftLength.ResolveFt(AircraftType) / 2.0);
            _self.Position = GeoMath.ProjectPoint(
                crossing.ApproachBar.Position,
                new TrueHeading(_across.Degrees + 180.0),
                centreShortFt / GeoMath.FeetPerNm
            );
            _self.TrueHeading = _across;
            _self.IndicatedAirspeed = speedKts;
        }

        /// <summary>A fixture over the same crossing with only the placed aircraft named <paramref name="callsign"/>.</summary>
        public GateFixture WithOnly(string callsign)
        {
            var only = new GateFixture(layout, crossing);
            only._others.AddRange(_others.Where(o => o.Callsign == callsign));
            return only;
        }

        public bool IsBlocked(bool resuming) =>
            RunwayCrossingFarSideGate.IsBlocked(
                Context(),
                (crossing.ApproachBar.Id, crossing.FarBar.Id),
                (null, 0),
                resuming,
                judgeLeadersGates: true
            );

        public bool IsCommitted(double toStopFt) =>
            RunwayCrossingFarSideGate.IsCommitted(Context(), toStopFt, crossing.ApproachBar.Position, _across.Degrees);

        private PhaseContext Context()
        {
            List<AircraftState> all = [_self, .. _others];
            return new PhaseContext
            {
                ActiveRunways = ActiveRunways.Empty,
                Aircraft = _self,
                Targets = _self.Targets,
                Category = AircraftCategorization.Categorize(AircraftType),
                DeltaSeconds = 1.0,
                Logger = NullLogger.Instance,
                GroundLayout = layout,
                ListAircraft = () => all,
                AircraftLookup = callsign => all.Find(a => a.Callsign == callsign),
            };
        }

        private static AircraftState MakeAircraft(string callsign, AirportGroundLayout layout, SfoCrossing crossing)
        {
            var aircraft = new AircraftState
            {
                Callsign = callsign,
                AircraftType = AircraftType,
                Position = crossing.ApproachNode.Position,
                TrueHeading = new TrueHeading(GeoMath.BearingTo(crossing.ApproachBar.Position, crossing.FarBar.Position)),
                IsOnGround = true,
            };
            aircraft.Ground.Layout = layout;
            return aircraft;
        }
    }
}
