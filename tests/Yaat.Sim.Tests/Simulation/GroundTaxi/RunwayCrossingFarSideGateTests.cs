using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The near-line gate (<see cref="RunwayCrossingFarSideGate"/>) on SFO's 01L/19R crossing on F, judged for an E75L short of the
/// near bar against E75Ls placed on the path across and beyond the far bar, each in a given state of motion. With no taxi route
/// the path runs straight on along the way across past the far bar. Two cases drive a leader through the crossing itself and
/// read the stop the gate projects for it, which a crossing's stale route cursor would overstate.
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

    /// <summary>The runway a taxi to 28L on F crosses next, beyond <see cref="SfoGroundHarness.CrossingRunway"/>.</summary>
    private const string NextCrossingRunway = "01R/19L";

    /// <summary>How many seconds a taxied leader gets to hold short of the first crossing and be released across it.</summary>
    private const int HoldBudgetSeconds = 90;

    /// <summary>How many seconds a released leader gets to reach the point the crossing is sampled at.</summary>
    private const int CrossingBudgetSeconds = 30;

    /// <summary>How many seconds a sampled leader gets to come to rest at the bar it was projected to stop at.</summary>
    private const int RestBudgetSeconds = 90;

    /// <summary>The gate's projection window (s); <see cref="RunwayCrossingFarSideGate.RouteStopAheadFt"/> does not read it.</summary>
    private const double LookAheadSeconds = 12.0;

    /// <summary>The number of feet the stale route cursor must overstate a crossing leader's stop by for the red case to tell.</summary>
    private const double StaleOverstatementFt = 20.0;

    /// <summary>How far (ft) the distance a sampled leader travels to rest may stray from the stop the gate read for it.</summary>
    private const double RestToleranceFt = 10.0;

    /// <summary>The number of feet the leader may stop short of the painted stop and still count as resting at it.</summary>
    private const double RestSpeedKts = SfoGroundHarness.StationarySpeedKts;

    /// <summary>
    /// How far (ft) past the crossing's first segment the leader is sampled. The stale cursor overstates the distance left by twice
    /// the distance driven past its segment's end node, so the sample is taken far enough past the node for the two to be told
    /// apart.
    /// </summary>
    private const double MinPastEntryFt = 120.0;

    /// <summary>
    /// How far (ft) past the crossing's first segment a leader is sampled when the sample is about the bar its own gate holds it at:
    /// just past the node, while it still carries its crossing speed, which is what makes a distance measured from the onward route
    /// alone read as one it can no longer stop within.
    /// </summary>
    private const double EntryOnlyFt = 10.0;

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

    /// <summary>
    /// A leader released across 01L/19R on F, inside the crossing and past the end node of the assigned route's cursor segment
    /// (the crossing's entry segment), heading for the uncleared 01R/19L near bar. The stop the gate reads for it is the
    /// distance along the crossing's own progress — the rest of its slice, then the onward route from where the tail clearance
    /// stops — to that bar's painted stop, and not the longer distance measured from the route cursor, which stays on the entry
    /// segment until the crossing ends (<see cref="CrossingRunwayPhase.HandRouteBack"/> is its only writer).
    /// </summary>
    [Fact]
    public void LeaderMidCrossing_ItsStopIsMeasuredFromTheCrossingNotItsStaleRoute()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        var approach = new TrueHeading(GeoMath.BearingTo(crossing.ApproachNode.Position, crossing.ApproachBar.Position));
        AircraftState leader = SfoGroundHarness.SpawnAt(ground, "LED", AircraftType, (crossing.ApproachNode, approach), new HoldingInPositionPhase());
        Assert.True(ground.Engine.SendCommand(leader.Callsign, NextTaxiCommand).Success, $"'{NextTaxiCommand}' was refused for {leader.Callsign}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(leader.Ground.AssignedTaxiRoute);
        int entryIndex = route.Segments.FindIndex(s => s.FromNodeId == crossing.ApproachBar.Id);
        Assert.True(
            entryIndex >= 0,
            $"{leader.Callsign}'s route does not leave the {SfoGroundHarness.CrossingRunway} approach bar #{crossing.ApproachBar.Id}"
        );

        int heldAt = SfoGroundHarness.TickUntil(ground.Engine, () => leader.Phases?.CurrentPhase is HoldingShortPhase, HoldBudgetSeconds, null);
        Assert.True(heldAt > 0, $"{leader.Callsign} never held short of {SfoGroundHarness.CrossingRunway}: {leader.Phases?.CurrentPhase?.Name}");
        CommandResult cross = ground.Engine.SendCommand(leader.Callsign, "CROSS 1L");
        Assert.True(cross.Success, $"CROSS 1L was refused for {leader.Callsign}: {cross.Message}");

        PhaseContext context = GateContext(ground.Layout, leader, [leader]);
        var judge = new RunwayCrossingFarSideGate.Judge(context, LookAheadSeconds, JudgeLeadersGates: true);
        double? measuredFt = null;
        (double SliceFt, double CutFt, double ExpectedFt, double StaleFt, HoldShortPoint Bar)? measures = null;
        LatLon samplePosition = leader.Position;
        AircraftState? sampled = SubTickDriver.TickSubTicksUntil(
            ground.Engine,
            leader.Callsign,
            ac => PastTheEntrySegment(ac, MinPastEntryFt),
            CrossingBudgetSeconds,
            ac =>
            {
                if (
                    (measuredFt is not null)
                    || (ac.Phases?.CurrentPhase is not CrossingRunwayPhase phase)
                    || !PastTheEntrySegment(ac, MinPastEntryFt)
                )
                {
                    return;
                }

                measures = CrossingStopMeasures(ground.Layout, ac, phase, route);
                measuredFt = RunwayCrossingFarSideGate.RouteStopAheadFt(judge, ac);
                samplePosition = ac.Position;
            }
        );

        Assert.True(sampled is not null, $"{leader.Callsign} was never sampled inside the {SfoGroundHarness.CrossingRunway} crossing");
        Assert.True(measuredFt is not null, "the gate projected no stop for the leader");
        Assert.True(measures is not null, "the crossing has no onward bar to measure to");
        (double sliceFt, double cutFt, double expectedFt, double staleFt, HoldShortPoint bar) = measures.Value;
        output.WriteLine(
            $"{leader.Callsign} mid-crossing: slice left {sliceFt:F0} ft, tail cut {cutFt:F0} ft, gate reads {measuredFt.Value:F0} ft, "
                + $"crossing progress {expectedFt:F0} ft, stale cursor {staleFt:F0} ft, next bar {bar.TargetName}"
        );
        Assert.True(
            SfoGroundHarness.HoldShortMatches(bar, NextCrossingRunway),
            $"test setup: the first uncleared bar past the crossing is {bar.TargetName}, expected {NextCrossingRunway}"
        );
        Assert.Equal(entryIndex, route.CurrentSegmentIndex);
        Assert.True(
            expectedFt < staleFt - StaleOverstatementFt,
            $"the crossing's own progress reads {expectedFt:F0} ft where the stale cursor reads {staleFt:F0} ft"
        );
        Assert.True(
            Math.Abs(measuredFt.Value - expectedFt) < 1.0,
            $"the gate reads the leader's stop {measuredFt.Value:F1} ft ahead; along the crossing to the bar's stop it is {expectedFt:F1} ft"
        );

        // The oracle that does not copy the formula: the leader comes to rest at that bar, and what it travelled from the sample
        // point to get there is the stop the gate read for it.
        double travelledFt = 0.0;
        LatLon last = samplePosition;
        AircraftState? rested = SubTickDriver.TickSubTicksUntil(
            ground.Engine,
            leader.Callsign,
            ac =>
                (ac.Phases?.CurrentPhase is HoldingShortPhase { HoldShort: { } restedAt })
                && (restedAt.NodeId == bar.NodeId)
                && (ac.GroundSpeed < RestSpeedKts),
            RestBudgetSeconds,
            ac =>
            {
                travelledFt += GeoMath.DistanceNm(last, ac.Position) * GeoMath.FeetPerNm;
                last = ac.Position;
            }
        );
        Assert.True(rested is not null, $"{leader.Callsign} never came to rest at the {bar.TargetName} bar");
        output.WriteLine($"{leader.Callsign} travelled {travelledFt:F0} ft from the sample to rest, gate read {measuredFt.Value:F0} ft");
        Assert.True(
            Math.Abs(travelledFt - measuredFt.Value) <= RestToleranceFt,
            $"the leader travelled {travelledFt:F1} ft to rest where the gate read its stop {measuredFt.Value:F1} ft ahead"
        );
    }

    /// <summary>
    /// A leader cleared across both 01L/19R and 01R/19L on F, mid-way across 01L/19R and still able to stop short of the 01R/19L
    /// near marking, with another aircraft at rest just past that runway's far bar. The cleared bar beyond its own gate's reach is
    /// not yet a stop it brakes for — a crossing leader is judged exactly as a taxiing one at the same true distance — so the gate
    /// reads the first bar it does stop at, the 28L destination bar, measured from the slice's end. The bar it will be held at is
    /// 519 ft away; measured from the onward route's start segment alone it read 133 ft, inside the reach, and was counted.
    /// </summary>
    [Fact]
    public void LeaderMidCrossing_AClearedBarBeyondItsReach_IsNotYetItsStop()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        var approach = new TrueHeading(GeoMath.BearingTo(crossing.ApproachNode.Position, crossing.ApproachBar.Position));
        AircraftState leader = SfoGroundHarness.SpawnAt(ground, "LED", AircraftType, (crossing.ApproachNode, approach), new HoldingInPositionPhase());
        Assert.True(ground.Engine.SendCommand(leader.Callsign, NextTaxiCommand).Success, $"'{NextTaxiCommand}' was refused for {leader.Callsign}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(leader.Ground.AssignedTaxiRoute);
        (int nextBarIndex, HoldShortPoint nextBar, GroundNode nextFar) = NextRunwayCrossingOnRoute(ground.Layout, route, NextCrossingRunway);

        int heldAt = SfoGroundHarness.TickUntil(ground.Engine, () => leader.Phases?.CurrentPhase is HoldingShortPhase, HoldBudgetSeconds, null);
        Assert.True(heldAt > 0, $"{leader.Callsign} never held short of {SfoGroundHarness.CrossingRunway}: {leader.Phases?.CurrentPhase?.Name}");
        CommandResult cross = ground.Engine.SendCommand(leader.Callsign, "CROSS 1L 1R");
        Assert.True(cross.Success, $"CROSS 1L 1R was refused for {leader.Callsign}: {cross.Message}");
        Assert.True(nextBar.IsCleared, $"test setup: CROSS 1L 1R left the {NextCrossingRunway} crossing uncleared");

        // A blocker at rest just past the 01R/19L far bar, too close for the leader to clear that runway with it there.
        GroundNode nextNear = ground.Layout.Nodes[nextBar.NodeId];
        double halfLengthFt = AircraftLength.ResolveFt(AircraftType) / 2.0;
        var nextAcross = new TrueHeading(GeoMath.BearingTo(nextNear.Position, nextFar.Position));
        AircraftState blocker = SfoGroundHarness.SpawnAt(ground, "RST", AircraftType, (nextFar, nextAcross), new HoldingInPositionPhase());
        blocker.Position = GeoMath.ProjectPoint(nextFar.Position, nextAcross, (TrafficTailPastNextFarFt + halfLengthFt) / GeoMath.FeetPerNm);

        PhaseContext context = GateContext(ground.Layout, leader, [leader, blocker]);
        var judge = new RunwayCrossingFarSideGate.Judge(context, LookAheadSeconds, JudgeLeadersGates: true);
        double? measuredFt = null;
        double nearBarFt = 0.0;
        double beyondFt = 0.0;
        double sliceFt = 0.0;
        double cutFt = 0.0;
        AircraftState? sampled = SubTickDriver.TickSubTicksUntil(
            ground.Engine,
            leader.Callsign,
            ac => PastTheEntrySegment(ac, EntryOnlyFt),
            CrossingBudgetSeconds,
            ac =>
            {
                if (
                    (measuredFt is not null)
                    || (ac.Phases?.CurrentPhase is not CrossingRunwayPhase phase)
                    || (phase.PathAhead(ac) is not { } ahead)
                    || (ahead.Onward is not { } onward)
                    || !PastTheEntrySegment(ac, EntryOnlyFt)
                )
                {
                    return;
                }

                int onwardBarIndex = onward.Segments.FindIndex(s => s.ToNodeId == nextBar.NodeId);
                if (onwardBarIndex < 0)
                {
                    return;
                }

                int start = Math.Clamp(ahead.OnwardStart, 0, onward.Segments.Count);
                sliceFt = ahead.Slice.IsComplete ? 0.0 : ahead.Slice.RemainingDistanceFt(ac.Position, ahead.Slice.Segments.Count);
                cutFt = phase.TailCutFt;
                double setbackFt = onward.HoldShortSetbackNm(onwardBarIndex, start, nextBar) * GeoMath.FeetPerNm;
                nearBarFt = sliceFt + onward.PrefixDistanceFt(onwardBarIndex + 1) - onward.PrefixDistanceFt(start) - cutFt - setbackFt;
                beyondFt = sliceFt + FirstUnclearedOnwardFt(onward, start, cutFt);
                measuredFt = RunwayCrossingFarSideGate.RouteStopAheadFt(judge, ac);
            }
        );

        Assert.True(sampled is not null, $"{leader.Callsign} was never sampled inside the {SfoGroundHarness.CrossingRunway} crossing");
        Assert.True(measuredFt is not null, "the gate projected no stop for the leader");
        // Measured on SFO F with the leader at 17 kt: slice still to go 439 ft, tail cut 53 ft, onward from OnwardStart's start node
        // to the 01R/19L painted stop 133 ft (80 ft cut-adjusted), so that stop is 518.6 ft from the leader; its own reach is about
        // 116 ft at 17 kt, so the cleared bar is not yet a stop and the gate reads the 28L destination bar instead, 5899 ft ahead.
        output.WriteLine(
            $"{leader.Callsign} mid-crossing of {SfoGroundHarness.CrossingRunway} at {leader.GroundSpeed:F0} kt: slice left {sliceFt:F0} ft, "
                + $"tail cut {cutFt:F0} ft, {NextCrossingRunway} bar at {nearBarFt:F0} ft, beyond it {beyondFt:F0} ft, "
                + $"gate reads {measuredFt.Value:F0} ft"
        );
        Assert.False(
            RunwayCrossingFarSideGate.IsCommitted(context, nearBarFt, nextNear.Position, route.Segments[nextBarIndex + 1].Edge.DepartureBearing),
            $"test setup: the leader {nearBarFt:F0} ft from the {NextCrossingRunway} near marking is committed to it"
        );
        Assert.True(
            measuredFt.Value > nearBarFt + 1.0,
            $"the gate chose the {NextCrossingRunway} bar at {nearBarFt:F1} ft, beyond the leader's reach, as its stop"
        );
        Assert.True(
            Math.Abs(measuredFt.Value - beyondFt) < 1.0,
            $"the gate reads the leader's stop {measuredFt.Value:F1} ft ahead; to the first bar it does stop at it is {beyondFt:F1} ft"
        );
    }

    /// <summary>
    /// A leader crossing 01L/19R on a path of its own (<see cref="CrossingRunwayPhase.OverOwnPath"/>, a follow armed at the bar)
    /// has no onward route of the aircraft's, so the stop the gate reads for it is the rest of the crossing slice to its end —
    /// its tail clear of the far marking — and not null for the route it does not drive.
    /// </summary>
    [Fact]
    public void LeaderCrossingItsOwnPath_ItsStopIsTheSlicesEnd()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        var approach = new TrueHeading(GeoMath.BearingTo(crossing.ApproachNode.Position, crossing.ApproachBar.Position));
        TaxiRoute ownPath = RealRoute(ground.Layout, crossing.ApproachNode, approach);
        var across = new TrueHeading(GeoMath.BearingTo(crossing.ApproachBar.Position, crossing.FarBar.Position));
        var phase = CrossingRunwayPhase.OverOwnPath(
            crossing.ApproachBar.Id,
            crossing.FarBar.Id,
            SfoGroundHarness.CrossingRunway,
            ownPath,
            expedited: true
        );
        AircraftState leader = SfoGroundHarness.SpawnAt(ground, "OWN", AircraftType, (crossing.ApproachBar, across), phase);
        Assert.Null(leader.Ground.AssignedTaxiRoute);

        PhaseContext context = GateContext(ground.Layout, leader, [leader]);
        var judge = new RunwayCrossingFarSideGate.Judge(context, LookAheadSeconds, JudgeLeadersGates: true);
        double? measuredFt = null;
        double expectedFt = 0.0;
        AircraftState? sampled = SubTickDriver.TickSubTicksUntil(
            ground.Engine,
            leader.Callsign,
            ac => PastTheEntrySegment(ac, EntryOnlyFt),
            CrossingBudgetSeconds,
            ac =>
            {
                if (
                    (measuredFt is not null)
                    || (ac.Phases?.CurrentPhase is not CrossingRunwayPhase crossingPhase)
                    || (crossingPhase.CrossingRoute is not { } slice)
                    || !PastTheEntrySegment(ac, EntryOnlyFt)
                )
                {
                    return;
                }

                expectedFt = slice.RemainingDistanceFt(ac.Position, slice.Segments.Count);
                measuredFt = RunwayCrossingFarSideGate.RouteStopAheadFt(judge, ac);
            }
        );

        Assert.True(sampled is not null, $"{leader.Callsign} was never sampled inside its own-path crossing");
        Assert.True(measuredFt is not null, "the gate projected no stop for a crossing on its own path");
        output.WriteLine($"{leader.Callsign} on its own path mid-crossing: gate reads {measuredFt.Value:F0} ft, slice left {expectedFt:F0} ft");
        Assert.True(
            Math.Abs(measuredFt.Value - expectedFt) < 1.0,
            $"the gate reads the leader's stop {measuredFt.Value:F1} ft ahead; the crossing slice has {expectedFt:F1} ft left"
        );
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/> is inside a runway crossing, at least <paramref name="pastFt"/> past the end node of
    /// the crossing slice's first segment (the segment the assigned route's cursor stays on for the whole crossing).
    /// </summary>
    private static bool PastTheEntrySegment(AircraftState aircraft, double pastFt)
    {
        if ((aircraft.Phases?.CurrentPhase is not CrossingRunwayPhase { CrossingRoute: { } slice }) || (slice.CurrentSegmentIndex <= 0))
        {
            return false;
        }

        DirectionalEdge entry = slice.Segments[0].Edge;
        double bearing = GeoMath.BearingTo(entry.FromNode.Position, entry.ToNode.Position);
        double drivenFt = GeoMath.AlongTrackDistanceNm(aircraft.Position, entry.ToNode.Position, new TrueHeading(bearing)) * GeoMath.FeetPerNm;
        return drivenFt >= pastFt;
    }

    /// <summary>
    /// For <paramref name="leader"/> inside its crossing: how far its slice has left, the length of the tail clearance's cut into
    /// the onward route, the distance the gate should read to the painted stop of the first uncleared bar on the onward route
    /// (searched from the tail clearance's <see cref="CrossingRunwayPhase.PathAhead"/> onward start and measured from the slice's
    /// end point), the same measured from the stale assigned-route cursor, and that bar. Null when the crossing has no onward route
    /// or no bar on it.
    /// </summary>
    private static (double SliceFt, double CutFt, double ExpectedFt, double StaleFt, HoldShortPoint Bar)? CrossingStopMeasures(
        AirportGroundLayout layout,
        AircraftState leader,
        CrossingRunwayPhase phase,
        TaxiRoute route
    )
    {
        if ((phase.PathAhead(leader) is not { } ahead) || (ahead.Onward is not { } onward))
        {
            return null;
        }

        int start = Math.Clamp(ahead.OnwardStart, 0, onward.Segments.Count);
        int barIndex = -1;
        HoldShortPoint? bar = null;
        for (int i = start; i < onward.Segments.Count; i++)
        {
            if (onward.GetHoldShortAt(onward.Segments[i].ToNodeId) is { IsCleared: false } found)
            {
                barIndex = i;
                bar = found;
                break;
            }
        }

        if (bar is not { } stop)
        {
            return null;
        }

        double sliceFt = ahead.Slice.IsComplete ? 0.0 : ahead.Slice.RemainingDistanceFt(leader.Position, ahead.Slice.Segments.Count);
        double cutFt = phase.TailCutFt;
        double setbackFt = onward.HoldShortSetbackNm(barIndex, start, stop) * GeoMath.FeetPerNm;
        double onwardFt = onward.PrefixDistanceFt(barIndex + 1) - onward.PrefixDistanceFt(start) - cutFt - setbackFt;
        double staleFt = TaxiingPhase.AlongRouteDistanceToHoldShortFt(layout, route, leader.Position, stop);
        return (sliceFt, cutFt, sliceFt + onwardFt, staleFt, stop);
    }

    /// <summary>
    /// The onward stretch from the crossing slice's end (cut-adjusted) to the stop <paramref name="onward"/> holds from segment
    /// <paramref name="start"/> on: the painted stop of the first uncleared bar from that segment on, else the route's end.
    /// </summary>
    private static double FirstUnclearedOnwardFt(TaxiRoute onward, int start, double cutFt)
    {
        for (int i = start; i < onward.Segments.Count; i++)
        {
            if (onward.GetHoldShortAt(onward.Segments[i].ToNodeId) is { IsCleared: false } bar)
            {
                double setbackFt = onward.HoldShortSetbackNm(i, start, bar) * GeoMath.FeetPerNm;
                return onward.PrefixDistanceFt(i + 1) - onward.PrefixDistanceFt(start) - cutFt - setbackFt;
            }
        }

        return onward.PrefixDistanceFt(onward.Segments.Count) - onward.PrefixDistanceFt(start) - cutFt;
    }

    /// <summary>
    /// The real <c>TAXI F 28L</c> route on <paramref name="layout"/> from <paramref name="from"/>, built by the command handler on
    /// a bare aircraft outside the world so nothing taxis or conflicts, for use as a crossing's own path.
    /// </summary>
    private static TaxiRoute RealRoute(AirportGroundLayout layout, GroundNode from, TrueHeading heading)
    {
        var bare = new AircraftState
        {
            Callsign = "PATH",
            AircraftType = AircraftType,
            Position = from.Position,
            TrueHeading = heading,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "SFO", Destination = "KLAX" },
        };
        var taxi = new TaxiCommand(Path: ["F", "28L"], HoldShorts: [], Expedite: false, DestinationParking: null);
        CommandResult result = GroundCommandHandler.TryTaxi(bare, taxi, layout);
        Assert.True(result.Success, $"TAXI F 28L failed for the own-path fixture: {result.Message}");
        return Assert.IsType<TaxiRoute>(bare.Ground.AssignedTaxiRoute);
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
    /// The crossing of <paramref name="runway"/> on <paramref name="route"/>: the index of the segment that ends at its near bar,
    /// that bar, and the far bar node, the next runway holding-position node after it.
    /// </summary>
    private static (int BarIndex, HoldShortPoint Bar, GroundNode Far) NextRunwayCrossingOnRoute(
        AirportGroundLayout layout,
        TaxiRoute route,
        string runway
    )
    {
        for (int i = 0; i < route.Segments.Count; i++)
        {
            if (
                (route.GetHoldShortAt(route.Segments[i].ToNodeId) is not { Reason: HoldShortReason.RunwayCrossing } bar)
                || !SfoGroundHarness.HoldShortMatches(bar, runway)
            )
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

        Assert.Fail($"the route crosses no {runway}");
        return default;
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
