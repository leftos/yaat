using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Two E75Ls cross SFO's runway 01L/19R in trail on taxiway F with the crossing pre-cleared
/// (<c>AutoCrossRunwayMode</c>). Both hold their route position on the crossing's entry segment while they cross, so
/// the ground detector reads them as a same-edge in-trail pair for the whole crossing. The follower must keep its
/// gap behind the leader and both must clear the far-side hold-short: ordering the pair by straight-line distance to
/// the entry segment's end node makes a leader already past that node read as the trailer, and the detector then
/// caps the leader for the aircraft behind it.
/// </summary>
[Collection("GroundConflictDebugSink")]
public class RunwayCrossingInTrailTests(ITestOutputHelper output)
{
    private const string LeadingCallsign = "SKW401";
    private const string FollowingCallsign = "SKW402";
    private const string AircraftType = "E75L";
    private const string TaxiCommand = "TAXI F 28L";

    /// <summary>How far ahead of the follower the leader starts, along the taxiway.</summary>
    private const double TrailFt = SfoGroundHarness.CrossingTrailFt;

    /// <summary>Margin under the detector's stop ring the pair may close to while a cap takes effect.</summary>
    private const double StopRingMarginFt = 5.0;

    private const int BudgetSeconds = 120;

    /// <summary>The aircraft standing just past the far bar in the near-line tests.</summary>
    private const string TrafficCallsign = "SKW403";

    /// <summary>How far (ft) past the far holding-position marking the standing traffic's tail is: well inside a follower's need.</summary>
    private const double TrafficTailPastFarBarFt = 20.0;

    /// <summary>How long (s) the follower must stay at rest short of the crossing to count as holding there.</summary>
    private const int NearLineRestSeconds = 5;

    private const double FeetPerSecondPerKt = 1.68781;

    /// <summary>Seconds watched after the first second at rest in the crossing for the not-clear call (made about 3 s in).</summary>
    private const int NotClearWatchSeconds = 5;

    /// <summary>The runway a taxi to 28L on F crosses next, beyond <see cref="SfoGroundHarness.CrossingRunway"/>.</summary>
    private const string NextRunway = "01R/19L";

    /// <summary>Ground speed (kt) the follower must be above to be sampled past its commit point: moving, never at rest.</summary>
    private const double CommitSampleMinKts = 3.0;

    /// <summary>How far (ft) short of the near holding-position marking the nose is in the held-on-runway readback test.</summary>
    private const double NoseShortOfTheMarkingFt = 5.0;

    /// <summary>A piston, whose taxi brake rate (2 kt/s) is under its HOLD-on-the-runway and firm rates (4.5 kt/s).</summary>
    private const string PistonType = "C172";

    /// <summary>One physics sub-tick (s), the cadence the phases and the ground-conflict detector run at.</summary>
    private const double SubTickSeconds = 1.0 / SimulationEngine.PhysicsSubTickRate;

    /// <summary>How far (kt) a sub-tick's speed loss may stray from the HOLD rate's and still count as braking at it.</summary>
    private const double HoldRateLossToleranceKts = 0.05;

    /// <summary>An aircraft behind the crossing follower, which a GIVEWAY to it never releases while it stays behind.</summary>
    private const string BehindCallsign = "SKW404";

    [Fact]
    public void TwoInTrail_CrossSfoRunway_KeepTheirGapAndBothClearIt()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        var heading = new TrueHeading(GeoMath.BearingTo(crossing.ApproachNode.Position, crossing.ApproachBar.Position));
        var crossingBearing = new TrueHeading(GeoMath.BearingTo(crossing.ApproachBar.Position, crossing.FarBar.Position));
        double approachFt = GeoMath.DistanceNm(crossing.ApproachNode.Position, crossing.ApproachBar.Position) * GeoMath.FeetPerNm;
        Assert.True(
            approachFt > TrailFt,
            $"the node the crossing is approached from is {approachFt:F0} ft short of the {SfoGroundHarness.CrossingRunway} bar, "
                + $"inside the {TrailFt:F0} ft trail: the leader would start past the bar"
        );

        AircraftState follower = SfoGroundHarness.SpawnAt(
            ground,
            FollowingCallsign,
            AircraftType,
            (crossing.ApproachNode, heading),
            new HoldingInPositionPhase()
        );
        AircraftState leader = SfoGroundHarness.SpawnAt(
            ground,
            LeadingCallsign,
            AircraftType,
            (crossing.ApproachNode, heading),
            new HoldingInPositionPhase()
        );
        leader.Position = GeoMath.ProjectPoint(crossing.ApproachNode.Position, heading, TrailFt / GeoMath.FeetPerNm);
        leader.TrueTrack = heading;
        output.WriteLine(
            $"{leader.Callsign} spawned {TrailFt:F0} ft ahead of {follower.Callsign} on {SfoGroundHarness.CrossingTaxiway}, "
                + $"{approachFt:F0} ft short of the {SfoGroundHarness.CrossingRunway} bar"
        );

        foreach (AircraftState ac in new[] { follower, leader })
        {
            CommandResult taxi = ground.Engine.SendCommand(ac.Callsign, TaxiCommand);
            output.WriteLine($"{ac.Callsign} {TaxiCommand}: {taxi.Success} — {taxi.Message}");
            Assert.True(taxi.Success, $"'{TaxiCommand}' was refused for {ac.Callsign}: {taxi.Message}");
            SfoGroundHarness.DumpRoute(output, ac.Ground.AssignedTaxiRoute!);
        }

        double separationFloorFt = GroundConflictDetectorTests.StopRingFt(AircraftType, AircraftType) - StopRingMarginFt;
        double minSeparationFt = double.MaxValue;
        int followerClearedAt = -1;
        int leaderClearedAt = -1;
        for (int second = 1; second <= BudgetSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            minSeparationFt = Math.Min(minSeparationFt, GeoMath.DistanceNm(follower.Position, leader.Position) * GeoMath.FeetPerNm);

            if ((followerClearedAt < 0) && HasClearedTheCrossing(follower, crossing, crossingBearing))
            {
                followerClearedAt = second;
            }
            if ((leaderClearedAt < 0) && HasClearedTheCrossing(leader, crossing, crossingBearing))
            {
                leaderClearedAt = second;
            }
        }

        output.WriteLine(
            $"{BudgetSeconds}s: {follower.Callsign} cleared at {followerClearedAt}s ({follower.Phases?.CurrentPhase?.Name}), "
                + $"{leader.Callsign} cleared at {leaderClearedAt}s ({leader.Phases?.CurrentPhase?.Name}), "
                + $"min separation {minSeparationFt:F0} ft"
        );

        Assert.True(
            minSeparationFt > separationFloorFt,
            $"the pair closed to {minSeparationFt:F0} ft, inside the {separationFloorFt:F0} ft stop-ring floor"
        );
        Assert.True(followerClearedAt > 0, $"{follower.Callsign} did not clear the far-side hold-short out of the crossing within {BudgetSeconds}s");
        Assert.True(leaderClearedAt > 0, $"{leader.Callsign} did not clear the far-side hold-short out of the crossing within {BudgetSeconds}s");
    }

    /// <summary>
    /// The committed crossing: the leader is held once it has cleared 01L/19R and stops just past the far bar while the
    /// follower is still crossing. Committed to the crossing, the follower is not trail-capped to the stopping leader: until the
    /// pair's stop ring is inside its own stopping distance it carries no ground-conflict speed limit at all, so it keeps the
    /// speed the crossing is flying; from there it brakes onto the ring, never losing more speed in a second than its firm rate
    /// allows, and stops at that ring, never inside it. The detector's lines for the follower are printed each second.
    /// </summary>
    [Fact]
    public void InTrail_LeaderStopsPastTheFarBar_FollowerKeepsItsCrossingSpeedUntilTheStopRing()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        var crossingBearing = new TrueHeading(GeoMath.BearingTo(crossing.ApproachBar.Position, crossing.FarBar.Position));
        (AircraftState follower, AircraftState leader) = SpawnInTrailAndTaxi(ground, crossing, AircraftType);
        double stopRingFt = GroundConflictDetectorTests.StopRingFt(AircraftType, AircraftType);
        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet);
        var detectorLines = new List<string>();
        var capped = new List<string>();
        var speeds = new List<(int Second, double SpeedKts)>();
        double minSeparationFt = double.MaxValue;
        int heldAt = -1;
        int crossingSecondsAfterHold = 0;
        try
        {
            GroundConflictDetector.DebugSink = line =>
            {
                if (
                    line.Contains($"{FollowingCallsign}→", StringComparison.Ordinal)
                    || line.Contains($"] {FollowingCallsign}:", StringComparison.Ordinal)
                )
                {
                    detectorLines.Add(line.Trim());
                }
            };
            for (int second = 1; second <= BudgetSeconds; second++)
            {
                detectorLines.Clear();
                double startKts = follower.GroundSpeed;
                ground.Engine.TickOneSecond();
                double separationFt = GeoMath.DistanceNm(follower.Position, leader.Position) * GeoMath.FeetPerNm;
                if ((heldAt < 0) && HasClearedTheCrossing(leader, crossing, crossingBearing))
                {
                    heldAt = second;
                    Assert.True(ground.Engine.SendCommand(leader.Callsign, "HOLD").Success);
                    output.WriteLine(
                        $"t={second}: {leader.Callsign} cleared the crossing, HOLD at gs={leader.GroundSpeed:F1}, {separationFt:F0} ft ahead"
                    );
                    continue;
                }

                if ((heldAt < 0) || (follower.Phases?.CurrentPhase is not CrossingRunwayPhase))
                {
                    continue;
                }

                crossingSecondsAfterHold++;
                minSeparationFt = Math.Min(minSeparationFt, separationFt);
                output.WriteLine(
                    $"t={second} sep={separationFt:F0} ft gs={follower.GroundSpeed:F1} limit={follower.Ground.SpeedLimit?.ToString("F1")} "
                        + $"leader gs={leader.GroundSpeed:F1} {leader.Phases?.CurrentPhase?.Name} | {string.Join(" | ", detectorLines)}"
                );
                speeds.Add((second, follower.GroundSpeed));
                // The room left before the ring, less a second of travel for the detector pass that set the limit, against its
                // own taxi-rate stopping distance from the speed it started the second at.
                double roomFt = separationFt - stopRingFt - (follower.GroundSpeed * FeetPerSecondPerKt);
                double ownStopFt = startKts * startKts / (2.0 * taxiRate) * FeetPerSecondPerKt;
                if ((roomFt > ownStopFt) && (follower.Ground.SpeedLimit is { } limitKts and > 0.0))
                {
                    capped.Add($"t={second}: limit {limitKts:F1} kt at {separationFt:F0} ft, own stop {ownStopFt:F0} ft");
                }
            }
        }
        finally
        {
            GroundConflictDetector.DebugSink = null;
        }

        Assert.True(heldAt > 0, $"{leader.Callsign} never cleared the crossing within {BudgetSeconds}s");
        Assert.True(crossingSecondsAfterHold > 0, $"{follower.Callsign} had left the crossing before {leader.Callsign} was held");
        Assert.True(
            capped.Count == 0,
            $"{follower.Callsign} was capped before the {stopRingFt:F0} ft stop ring was inside its stopping distance: {string.Join("; ", capped)}"
        );
        Assert.True(
            minSeparationFt > stopRingFt - StopRingMarginFt,
            $"{follower.Callsign} closed to {minSeparationFt:F0} ft while crossing, inside the {stopRingFt:F0} ft stop ring"
        );
        (double lossKts, int atSecond) = LargestLossOverConsecutiveSeconds(speeds);
        double firmLossKts = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet) * (1.0 + (1.0 / SimulationEngine.PhysicsSubTickRate));
        Assert.True(
            lossKts <= firmLossKts + 1e-6,
            $"{follower.Callsign} lost {lossKts:F2} kt in the second ending t={atSecond} while crossing, more than the firm rate's {firmLossKts:F2}"
        );
    }

    /// <summary>
    /// An ordinary closure on the stop ring: once the follower is in its crossing, traffic comes to rest just past the far bar,
    /// several of the follower's stopping distances ahead. Approaching the ring from outside its taxi-rate stopping distance, the
    /// follower brakes onto it at its taxi brake rate, never losing more speed in a second than that rate allows — for a jet, and
    /// for a piston, whose taxi rate (2 kt/s) is furthest under its firm rate (4.5 kt/s).
    /// </summary>
    [Theory]
    [InlineData(AircraftType)]
    [InlineData(PistonType)]
    public void Crossing_TrafficComesToRestPastTheFarBar_FollowerBrakesOntoTheStopRingAtItsTaxiRate(string type)
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        var heading = new TrueHeading(GeoMath.BearingTo(crossing.ApproachNode.Position, crossing.ApproachBar.Position));
        AircraftState follower = SfoGroundHarness.SpawnAt(
            ground,
            FollowingCallsign,
            type,
            (crossing.ApproachNode, heading),
            new HoldingInPositionPhase()
        );
        Assert.True(ground.Engine.SendCommand(follower.Callsign, TaxiCommand).Success, $"'{TaxiCommand}' was refused");
        int enteredAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.Phases?.CurrentPhase is CrossingRunwayPhase,
            BudgetSeconds,
            eachSecond: null
        );
        Assert.True(enteredAt > 0, $"{type} {follower.Callsign} never entered the crossing");

        var across = new TrueHeading(GeoMath.BearingTo(crossing.ApproachBar.Position, crossing.FarBar.Position));
        AircraftState traffic = SfoGroundHarness.SpawnAt(ground, TrafficCallsign, type, (crossing.FarBar, across), new HoldingInPositionPhase());
        double centreFt = TrafficTailPastFarBarFt + (AircraftLength.ResolveFt(type) / 2.0);
        traffic.Position = GeoMath.ProjectPoint(crossing.FarBar.Position, across, centreFt / GeoMath.FeetPerNm);
        traffic.TrueTrack = across;
        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategorization.Categorize(type));
        double roomFt =
            (GeoMath.DistanceNm(follower.Position, traffic.Position) * GeoMath.FeetPerNm) - GroundConflictDetectorTests.StopRingFt(type, type);
        double taxiKts = CategoryPerformance.TaxiSpeed(AircraftCategorization.Categorize(type));
        double ownStopFt = taxiKts * taxiKts / (2.0 * taxiRate) * FeetPerSecondPerKt;
        Assert.True(roomFt > ownStopFt, $"{follower.Callsign} has {roomFt:F0} ft to the ring, inside its {ownStopFt:F0} ft taxi-rate stop");

        var speeds = new List<(int Second, double SpeedKts)> { (0, follower.GroundSpeed) };
        int restedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts,
            BudgetSeconds,
            eachSecond: second => speeds.Add((second, follower.GroundSpeed))
        );
        (double lossKts, int atSecond) = LargestLossOverConsecutiveSeconds(speeds);
        double taxiLossKts = taxiRate * (1.0 + (1.0 / SimulationEngine.PhysicsSubTickRate));
        output.WriteLine(
            $"{type}: room {roomFt:F0} ft at {speeds[0].SpeedKts:F1} kt, at rest t+{restedAt}, largest loss {lossKts:F2} kt at t+{atSecond}"
        );
        Assert.True(restedAt > 0, $"{type} {follower.Callsign} never came to rest behind the traffic");
        Assert.True(
            lossKts <= taxiLossKts + 1e-6,
            $"{type} {follower.Callsign} lost {lossKts:F2} kt in the second ending t+{atSecond}, more than the taxi rate's {taxiLossKts:F2}"
        );
    }

    /// <summary>
    /// A HOLD given to a piston mid-crossing with the room listing the crossed runway's other end as active: the readback names
    /// the runway by that active end ("holding position on runway …"), not the end nearest the aircraft. Red first with the
    /// active-end branch of <see cref="RunwayCrossingEnd.Spoken"/> disabled.
    /// </summary>
    [Fact]
    public void Hold_MidCrossing_WithTheOtherEndActive_ReadsBackHoldingOnTheActiveEnd()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        var heading = new TrueHeading(GeoMath.BearingTo(crossing.ApproachNode.Position, crossing.ApproachBar.Position));
        AircraftState follower = SfoGroundHarness.SpawnAt(
            ground,
            FollowingCallsign,
            PistonType,
            (crossing.ApproachNode, heading),
            new HoldingInPositionPhase()
        );
        Assert.True(ground.Engine.SendCommand(follower.Callsign, TaxiCommand).Success, $"'{TaxiCommand}' was refused");
        int onRunway = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is CrossingRunwayPhase phase) && (phase.HeldOnRunwayEnd(follower, ActiveRunways.Empty) is not null),
            BudgetSeconds,
            null
        );
        Assert.True(onRunway > 0, $"{follower.Callsign} never reached a point mid-crossing where a HOLD stops it on the runway");

        CrossingRunwayPhase onCrossing = Assert.IsType<CrossingRunwayPhase>(follower.Phases?.CurrentPhase);
        string nearest = Assert.IsType<string>(onCrossing.HeldOnRunwayEnd(follower, ActiveRunways.Empty));
        RunwayIdentifier runway = SfoGroundHarness.Runway(nearest).Id;
        string other = string.Equals(runway.End1, RunwayIdentifier.NormalizeDesignator(nearest), StringComparison.OrdinalIgnoreCase)
            ? runway.End2
            : runway.End1;
        ActiveRunways otherEndActive = ActiveRunways.Empty.With("SFO", [new ActiveRunway(other, ActiveRunwayUse.Both)]);
        PilotSpeechText readback = Assert.IsType<PilotSpeechText>(
            PilotResponder.BuildReadback(new([new ParsedBlock(null, [new HoldPositionCommand()])]), follower, otherEndActive)
        );
        output.WriteLine($"nearest {nearest}, active {other}: readback '{readback.Terminal}'");

        string activeEnd = RunwayIdentifier.ToDisplayDesignator(other);
        Assert.Equal($"holding position on runway {PhraseologyVerbalizer.CompactRunway(activeEnd)}", readback.Terminal);
    }

    /// <summary>
    /// A HOLD given to a piston in the taxi before its crossing, while the crossing has not started yet (the taxi hands the
    /// crossing over only once the centre reaches the near bar's node), with the nose already past the near holding-position
    /// marking or still short of it by less than the HOLD's stopping distance: the HOLD stops it on the runway, so it reads
    /// back "holding position on runway …" (AIM 2-3-5.a.1, 4-3-21.a), and brakes at the HOLD-on-the-runway rate, not its
    /// 2 kt/s taxi rate.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hold_InTheTaxiBeforeTheCrossingStarts_ReadsBackHoldingOnTheRunwayAndBrakesAtTheHoldRate(bool nosePastTheMarking)
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        var heading = new TrueHeading(GeoMath.BearingTo(crossing.ApproachNode.Position, crossing.ApproachBar.Position));
        AircraftState follower = SfoGroundHarness.SpawnAt(
            ground,
            FollowingCallsign,
            PistonType,
            (crossing.ApproachNode, heading),
            new HoldingInPositionPhase()
        );
        Assert.True(ground.Engine.SendCommand(follower.Callsign, TaxiCommand).Success, $"'{TaxiCommand}' was refused");
        double holdRate = CrossingRunwayPhase.HoldBrakeRateKtsPerSec(AircraftCategory.Piston);
        bool InTheWindow()
        {
            double nosePastFt = NosePastNearMarkingFt(follower, crossing);
            double holdStopFt = follower.GroundSpeed * follower.GroundSpeed / (2.0 * holdRate) * FeetPerSecondPerKt;
            bool placed = nosePastTheMarking ? nosePastFt > 0.0 : (nosePastFt < 0.0) && (-nosePastFt < holdStopFt);
            return placed && (follower.Phases?.CurrentPhase is TaxiingPhase) && (follower.GroundSpeed > CommitSampleMinKts);
        }

        int subTick = 0;
        while ((subTick < BudgetSeconds * SimulationEngine.PhysicsSubTickRate) && !InTheWindow())
        {
            ground.Engine.TickPhysics(SubTickSeconds);
            subTick++;
        }

        Assert.True(InTheWindow(), $"{PistonType} {follower.Callsign} was never sampled in the taxi window before its crossing");
        PilotSpeechText readback = Assert.IsType<PilotSpeechText>(
            PilotResponder.BuildReadback(new([new ParsedBlock(null, [new HoldPositionCommand()])]), follower, ActiveRunways.Empty)
        );
        string runway = RunwayCrossingEnd.Spoken(follower, SfoGroundHarness.CrossingRunway, ground.Layout, ActiveRunways.Empty);
        double holdKts = follower.GroundSpeed;
        output.WriteLine(
            $"sub-tick {subTick}: HOLD at {holdKts:F2} kt, nose {NosePastNearMarkingFt(follower, crossing):F1} ft past the near marking, "
                + $"readback '{readback.Terminal}'"
        );
        Assert.True(ground.Engine.SendCommand(follower.Callsign, "HOLD").Success);
        ground.Engine.TickPhysics(SubTickSeconds);
        double lossKts = holdKts - follower.GroundSpeed;
        output.WriteLine($"one sub-tick later: {follower.GroundSpeed:F2} kt ({follower.Phases?.CurrentPhase?.Name}), lost {lossKts:F3} kt");

        Assert.Equal($"holding position on runway {PhraseologyVerbalizer.CompactRunway(runway)}", readback.Terminal);
        Assert.True(
            Math.Abs(lossKts - (holdRate * SubTickSeconds)) < HoldRateLossToleranceKts,
            $"{follower.Callsign} lost {lossKts:F3} kt in the sub-tick after the HOLD, not the {holdRate * SubTickSeconds:F3} kt of the HOLD rate"
        );
    }

    /// <summary>
    /// A HOLD given as a crossing E75L comes within its braking distance of the crossing's end, past the far marking: the
    /// crossing hands the aircraft back to its taxi there, which brakes it on at the taxi rate, rather than dropping the speed
    /// it still carries at the crossing's last node.
    /// </summary>
    [Fact]
    public void Hold_WithinBrakingDistanceOfTheCrossingsEnd_HandsBackAndBrakesOnWithoutStoppingDead()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        AircraftState follower = SpawnFollowerAndTaxi(ground, crossing);
        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet);
        var speeds = new List<(int Second, double SpeedKts)>();
        int holdAt = -1;
        for (int second = 1; (second <= BudgetSeconds) && ((holdAt < 0) || (follower.GroundSpeed >= SfoGroundHarness.StationarySpeedKts)); second++)
        {
            ground.Engine.TickOneSecond();
            if (holdAt > 0)
            {
                speeds.Add((second, follower.GroundSpeed));
                output.WriteLine($"t={second}: gs={follower.GroundSpeed:F2} {follower.Phases?.CurrentPhase?.Name}");
                continue;
            }

            if (follower.Phases?.CurrentPhase is not CrossingRunwayPhase { CrossingRoute: { } slice })
            {
                continue;
            }

            double toEndFt = slice.RemainingDistanceFt(follower.Position, slice.Segments.Count);
            double stopFt = follower.GroundSpeed * follower.GroundSpeed / (2.0 * taxiRate) * FeetPerSecondPerKt;
            // Well inside: at half its stopping distance it reaches the end still carrying most of its speed.
            if (toEndFt < stopFt / 2.0)
            {
                holdAt = second;
                speeds.Add((second, follower.GroundSpeed));
                output.WriteLine($"t={second}: HOLD {toEndFt:F1} ft from the crossing's end at gs={follower.GroundSpeed:F1} (stop {stopFt:F1} ft)");
                Assert.True(ground.Engine.SendCommand(follower.Callsign, "HOLD").Success);
            }
        }

        Assert.True(holdAt > 0, $"{follower.Callsign} was never sampled inside its braking distance of the crossing's end");
        Assert.True(follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts, $"{follower.Callsign} never came to rest after the HOLD");
        Assert.IsNotType<CrossingRunwayPhase>(follower.Phases?.CurrentPhase);
        (double lossKts, int atSecond) = LargestLossOverConsecutiveSeconds(speeds);
        double allowedKts = taxiRate * (1.0 + (1.0 / SimulationEngine.PhysicsSubTickRate));
        Assert.True(
            lossKts <= allowedKts + 1e-6,
            $"{follower.Callsign} lost {lossKts:F2} kt in the second ending t={atSecond}, more than {allowedKts:F2}"
        );
    }

    /// <summary>
    /// A HOLD taken mid-crossing with the nose still short of the near holding-position marking reads back as a hold on the
    /// runway only when the HOLD's braking cannot stop the nose at the marking: then the aircraft comes to rest on the runway.
    /// </summary>
    [Theory]
    [InlineData(15.0, true)]
    [InlineData(2.0, false)]
    public void HeldOnRunwayEnd_NoseShortOfTheNearMarking_NamesTheRunwayOnlyWhenTheHoldStopsItPastTheMarking(double speedKts, bool onRunway)
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        AircraftState follower = SpawnFollowerAndTaxi(ground, crossing);
        var back = new TrueHeading(GeoMath.BearingTo(crossing.FarBar.Position, crossing.ApproachBar.Position));
        double centreShortFt = NoseShortOfTheMarkingFt + (AircraftLength.ResolveFt(AircraftType) / 2.0);
        follower.Position = GeoMath.ProjectPoint(crossing.ApproachBar.Position, back, centreShortFt / GeoMath.FeetPerNm);
        follower.IndicatedAirspeed = speedKts;
        var phase = new CrossingRunwayPhase(crossing.ApproachBar.Id, crossing.FarBar.Id, SfoGroundHarness.CrossingRunway);

        string? end = phase.HeldOnRunwayEnd(follower, ActiveRunways.Empty);

        output.WriteLine(
            $"{speedKts:F1} kt, nose {NosePastNearMarkingFt(follower, crossing):F1} ft past the near marking: {end ?? "(not on the runway)"}"
        );
        Assert.Equal(onRunway ? RunwayCrossingEnd.Spoken(follower, SfoGroundHarness.CrossingRunway, ground.Layout, ActiveRunways.Empty) : null, end);
    }

    /// <summary>
    /// The near-line gate's holding-short call, made before a snapshot, is not made again by the phase restored from it
    /// (through the recording JSON) while the follower waits on at the near line: the taxi's when it braked there pre-cleared,
    /// the hold-short's when a CROSS reached it holding short.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NearLine_TrafficAheadCallMadeBeforeASnapshot_IsNotRepeatedAfterTheRestore(bool preCleared)
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: preCleared ? AutoCrossRunwayMode.All : AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        SpawnTrafficPastTheFarBar(ground, crossing);
        AircraftState follower = SpawnFollowerAndTaxi(ground, crossing);
        List<string> warnings = CaptureWarnings(ground.Engine, FollowingCallsign);
        bool enteredCrossing = false;
        TickUntilAtRest(ground, follower, ref enteredCrossing);
        if (!preCleared)
        {
            string end = RunwayCrossingEnd.Spoken(follower, SfoGroundHarness.CrossingRunway, ground.Layout, ActiveRunways.Empty);
            Assert.True(ground.Engine.SendCommand(follower.Callsign, $"CROSS {end}").Success, $"'CROSS {end}' was refused");
            TickUntilAtRest(ground, follower, ref enteredCrossing);
        }

        Assert.Single(TrafficAheadCalls(warnings));
        SfoGround restored = RestoreFrom(SnapshotJson(ground), preCleared ? AutoCrossRunwayMode.All : AutoCrossRunwayMode.Off);
        List<string> restoredWarnings = CaptureWarnings(restored.Engine, FollowingCallsign);
        for (int second = 0; second < 2 * NearLineRestSeconds; second++)
        {
            restored.Engine.TickOneSecond();
        }

        AircraftState restoredFollower = Assert.IsType<AircraftState>(restored.Engine.FindAircraft(FollowingCallsign));
        Assert.IsNotType<CrossingRunwayPhase>(restoredFollower.Phases?.CurrentPhase);
        Assert.Empty(TrafficAheadCalls(restoredWarnings));
    }

    /// <summary>
    /// A snapshot taken the second before the pre-cleared follower, braked to rest at the near line, makes its holding-short
    /// call is restored (through the recording JSON) into a run that still makes the call, once.
    /// </summary>
    [Fact]
    public void NearLine_SnapshotTheSecondBeforeTheTrafficAheadCall_StillMakesTheCallAfterTheRestore()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        SpawnTrafficPastTheFarBar(ground, crossing);
        AircraftState follower = SpawnFollowerAndTaxi(ground, crossing);
        List<string> warnings = CaptureWarnings(ground.Engine, FollowingCallsign);
        string json = SnapshotJson(ground);
        int calledAt = -1;
        for (int second = 1; (second <= BudgetSeconds) && (calledAt < 0); second++)
        {
            json = SnapshotJson(ground);
            ground.Engine.TickOneSecond();
            calledAt = TrafficAheadCalls(warnings).Count > 0 ? second : -1;
        }

        Assert.True(calledAt > 0, $"{follower.Callsign} never made the holding-short call");
        output.WriteLine($"{follower.Callsign} made the call at t={calledAt}; restoring the snapshot taken before that second");
        SfoGround restored = RestoreFrom(json, autoCrossMode: AutoCrossRunwayMode.All);
        List<string> restoredWarnings = CaptureWarnings(restored.Engine, FollowingCallsign);
        for (int second = 0; second < 2 * NearLineRestSeconds; second++)
        {
            restored.Engine.TickOneSecond();
        }

        Assert.Single(TrafficAheadCalls(restoredWarnings));
    }

    /// <summary>
    /// A run rewound to the follower holding short of the crossing, the traffic at rest just past the far bar, and then given
    /// its CROSS still makes the holding-short call once, though the hold-short phase was restored from the snapshot.
    /// </summary>
    [Fact]
    public void NearLine_RewoundToHoldingShortThenCrossed_StillMakesTheTrafficAheadCall()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        SpawnTrafficPastTheFarBar(ground, crossing);
        AircraftState follower = SpawnFollowerAndTaxi(ground, crossing);
        bool enteredCrossing = false;
        TickUntilAtRest(ground, follower, ref enteredCrossing);
        Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);

        SfoGround restored = RestoreFrom(SnapshotJson(ground), autoCrossMode: AutoCrossRunwayMode.Off);
        List<string> restoredWarnings = CaptureWarnings(restored.Engine, FollowingCallsign);
        AircraftState restoredFollower = Assert.IsType<AircraftState>(restored.Engine.FindAircraft(FollowingCallsign));
        string end = RunwayCrossingEnd.Spoken(restoredFollower, SfoGroundHarness.CrossingRunway, restored.Layout, ActiveRunways.Empty);
        Assert.True(restored.Engine.SendCommand(FollowingCallsign, $"CROSS {end}").Success, $"'CROSS {end}' was refused");
        for (int second = 0; second < NearLineRestSeconds; second++)
        {
            restored.Engine.TickOneSecond();
        }

        Assert.Single(TrafficAheadCalls(restoredWarnings));
    }

    /// <summary>The engine's snapshot as the recording JSON carries it.</summary>
    private static string SnapshotJson(SfoGround ground) => JsonSerializer.Serialize(ground.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);

    /// <summary>A fresh SFO engine restored from <paramref name="json"/> (<see cref="SnapshotJson"/>).</summary>
    private SfoGround RestoreFrom(string json, AutoCrossRunwayMode autoCrossMode)
    {
        SfoGround restored = Assert.IsType<SfoGround>(SfoGroundHarness.Build(output, autoCrossMode));
        restored.Engine.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
        );
        return restored;
    }

    /// <summary>The largest speed loss (kt) between two samples one second apart, and the second it ended at.</summary>
    private static (double LossKts, int AtSecond) LargestLossOverConsecutiveSeconds(List<(int Second, double SpeedKts)> speeds)
    {
        (double LossKts, int AtSecond) largest = (0.0, -1);
        for (int i = 1; i < speeds.Count; i++)
        {
            double lossKts = speeds[i - 1].SpeedKts - speeds[i].SpeedKts;
            if ((speeds[i].Second == speeds[i - 1].Second + 1) && (lossKts > largest.LossKts))
            {
                largest = (lossKts, speeds[i].Second);
            }
        }

        return largest;
    }

    /// <summary>
    /// YAAT-483, the near-line gate: traffic at rest just past 01L/19R's far bar leaves less room beyond the far holding-position
    /// marking than the follower's length, the pair's stop gap and 10 ft, so the follower cleared across stops with its nose at or
    /// short of the near marking rather than entering the crossing, says once that it is holding short of the runway with traffic
    /// ahead, and crosses by itself once the traffic taxis on.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NearLine_TrafficAtRestJustPastTheFarBar_FollowerHoldsAtTheNearMarkingOnceThenCrossesWhenItMovesOn(bool preCleared)
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: preCleared ? AutoCrossRunwayMode.All : AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        var crossingBearing = new TrueHeading(GeoMath.BearingTo(crossing.ApproachBar.Position, crossing.FarBar.Position));
        SpawnTrafficPastTheFarBar(ground, crossing);
        AircraftState follower = SpawnFollowerAndTaxi(ground, crossing);
        List<string> warnings = CaptureWarnings(ground.Engine, FollowingCallsign);

        bool enteredCrossing = false;
        int restSeconds = TickUntilAtRest(ground, follower, ref enteredCrossing);
        if (!preCleared)
        {
            // Not cleared across, the follower holds short of the bar; the CROSS given there is held for the traffic.
            Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
            string end = RunwayCrossingEnd.Spoken(follower, SfoGroundHarness.CrossingRunway, ground.Layout, ActiveRunways.Empty);
            Assert.True(ground.Engine.SendCommand(follower.Callsign, $"CROSS {end}").Success, $"'CROSS {end}' was refused");
            restSeconds = TickUntilAtRest(ground, follower, ref enteredCrossing);
        }

        double nosePastFt = NosePastNearMarkingFt(follower, crossing);
        output.WriteLine($"{follower.Callsign}: {follower.Phases?.CurrentPhase?.Name}, nose {nosePastFt:F1} ft past the near marking");
        Assert.False(
            enteredCrossing,
            $"{follower.Callsign} entered the crossing with traffic at rest {TrafficTailPastFarBarFt:F0} ft past the far bar"
        );
        Assert.True(restSeconds >= NearLineRestSeconds, $"{follower.Callsign} never came to rest short of the crossing");
        Assert.True(nosePastFt <= 0.0, $"{follower.Callsign} stopped with its nose {nosePastFt:F1} ft past the near holding-position marking");
        string runway = RunwayCrossingEnd.Spoken(follower, SfoGroundHarness.CrossingRunway, ground.Layout, ActiveRunways.Empty);
        Assert.Equal([$"Holding short of runway {runway}, traffic ahead"], TrafficAheadCalls(warnings));

        Assert.True(ground.Engine.SendCommand(TrafficCallsign, TaxiCommand).Success, $"'{TaxiCommand}' was refused for {TrafficCallsign}");
        if (!preCleared)
        {
            // Holding short of the next runway just ahead, the traffic would still leave the follower no room: clear it across too.
            AircraftState traffic = Assert.IsType<AircraftState>(ground.Engine.FindAircraft(TrafficCallsign));
            string next = RunwayCrossingEnd.Spoken(traffic, NextRunway, ground.Layout, ActiveRunways.Empty);
            Assert.True(ground.Engine.SendCommand(TrafficCallsign, $"CROSS {next}").Success, $"'CROSS {next}' was refused for {TrafficCallsign}");
        }

        int clearedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => HasClearedTheCrossing(follower, crossing, crossingBearing),
            BudgetSeconds,
            eachSecond: null
        );
        Assert.True(clearedAt > 0, $"{follower.Callsign} did not cross once the traffic taxied on");
        Assert.Single(TrafficAheadCalls(warnings));
    }

    /// <summary>
    /// YAAT-483, the commit point: the traffic comes to rest just past the far bar only once the follower can no longer stop with
    /// its nose at the near marking even at the firm rate, so the follower commits and enters the crossing still rolling.
    /// </summary>
    [Fact]
    public void NearLine_TrafficStopsOnceTheFollowerIsPastItsCommitPoint_FollowerCommitsToTheCrossing()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        (AircraftState follower, int spawnedAt) = RunUntilPastTheCommitPoint(ground, crossing);
        int enteredAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.Phases?.CurrentPhase is CrossingRunwayPhase,
            BudgetSeconds,
            eachSecond: second =>
                Assert.True(
                    follower.GroundSpeed >= SfoGroundHarness.StationarySpeedKts,
                    $"t+{second}: {follower.Callsign} stopped short of the crossing after the traffic stopped at t={spawnedAt}, "
                        + $"nose {NosePastNearMarkingFt(follower, crossing):F1} ft past the near marking"
                )
        );
        Assert.True(enteredAt > 0, $"{follower.Callsign} never entered the crossing");
    }

    /// <summary>
    /// Spawns the follower at <see cref="SfoCrossing.ApproachNode"/> and taxis it, ticking each second until its nose is short
    /// of the near marking by less than its firm-rate stopping distance (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>),
    /// then spawns the traffic at rest just past the far bar. Returns the follower and the second the traffic appeared.
    /// </summary>
    private (AircraftState Follower, int SpawnedAt) RunUntilPastTheCommitPoint(SfoGround ground, SfoCrossing crossing)
    {
        AircraftState follower = SpawnFollowerAndTaxi(ground, crossing);
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet);
        for (int second = 1; second <= BudgetSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            double shortFt = -NosePastNearMarkingFt(follower, crossing);
            double firmStopFt = follower.GroundSpeed * follower.GroundSpeed / (2.0 * firmRate) * FeetPerSecondPerKt;
            output.WriteLine($"t={second}: {follower.Callsign} {shortFt:F1} ft short at {follower.GroundSpeed:F1} kt (firm stop {firmStopFt:F1} ft)");
            // Past the commit point, nose short of the marking or already over it, and still taxiing toward the crossing: a
            // window wider than a second's travel, so the per-second sampling cannot step over it.
            if ((follower.Phases?.CurrentPhase is TaxiingPhase) && (follower.GroundSpeed > CommitSampleMinKts) && (shortFt < firmStopFt))
            {
                SpawnTrafficPastTheFarBar(ground, crossing);
                return (follower, second);
            }
        }

        Assert.Fail($"{follower.Callsign} was never sampled between its commit point and the near marking");
        return (follower, -1);
    }

    /// <summary>
    /// Committed to the crossing, the follower comes to rest behind the traffic with its whole length still inside the far
    /// holding-position marking. About 3 s after coming to rest it says once that it is holding position, not clear of the
    /// runway, with traffic ahead — and never again while it waits. A GIVEWAY given once it is at rest does not silence the
    /// call: only a HOLD, which stops it on the runway by the controller's own word, does.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Crossing_ComesToRestNotClearOfTheRunway_SaysSoOnceAboutThreeSecondsLater(bool giveWay)
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        (AircraftState follower, _) = RunUntilPastTheCommitPoint(ground, crossing);
        List<string> warnings = CaptureWarnings(ground.Engine, FollowingCallsign);
        int restedAt = TickUntilAtRestInTheCrossing(ground, follower);
        if (giveWay)
        {
            // A GIVEWAY to an aircraft at rest behind the follower on its heading: physics never releases it while that
            // aircraft stays behind (FlightPhysics.UpdateGiveWayResume), so it is in force for the whole watch.
            var heading = new TrueHeading(GeoMath.BearingTo(crossing.ApproachNode.Position, crossing.ApproachBar.Position));
            SfoGroundHarness.SpawnAt(ground, BehindCallsign, AircraftType, (crossing.ApproachNode, heading), new HoldingInPositionPhase());
            follower.Ground.Hold = HoldDirective.GiveWay(BehindCallsign);
        }

        ground.Engine.TickOneSecond();
        Assert.Empty(NotClearCalls(warnings));
        for (int second = 0; second < NotClearWatchSeconds; second++)
        {
            ground.Engine.TickOneSecond();
        }

        string runway = RunwayCrossingEnd.Spoken(follower, SfoGroundHarness.CrossingRunway, ground.Layout, ActiveRunways.Empty);
        output.WriteLine($"{follower.Callsign} at rest in the crossing from t+{restedAt}: {string.Join(" | ", warnings)}");
        Assert.Equal(giveWay ? HoldKind.GiveWay : null, follower.Ground.Hold?.Kind);
        Assert.IsType<CrossingRunwayPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal([$"Holding position, not clear of runway {runway}, traffic ahead"], NotClearCalls(warnings));
    }

    /// <summary>
    /// The not-clear call of <see cref="Crossing_ComesToRestNotClearOfTheRunway_SaysSoOnceAboutThreeSecondsLater"/>, made before a
    /// snapshot, is not made again by the crossing restored from it (through the recording JSON) while it waits on.
    /// </summary>
    [Fact]
    public void Crossing_NotClearCallMadeBeforeASnapshot_IsNotRepeatedAfterTheRestore()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        (AircraftState follower, _) = RunUntilPastTheCommitPoint(ground, crossing);
        List<string> warnings = CaptureWarnings(ground.Engine, FollowingCallsign);
        TickUntilAtRestInTheCrossing(ground, follower);
        int calledAt = SfoGroundHarness.TickUntil(ground.Engine, () => NotClearCalls(warnings).Count > 0, NotClearWatchSeconds, eachSecond: null);
        Assert.True(calledAt > 0, $"{follower.Callsign} never made the not-clear call");

        SfoGround restored = Assert.IsType<SfoGround>(SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All));
        string json = JsonSerializer.Serialize(ground.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        restored.Engine.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
        );
        List<string> restoredWarnings = CaptureWarnings(restored.Engine, FollowingCallsign);
        for (int second = 0; second < 2 * NotClearWatchSeconds; second++)
        {
            restored.Engine.TickOneSecond();
        }

        AircraftState restoredFollower = Assert.IsType<AircraftState>(restored.Engine.FindAircraft(FollowingCallsign));
        Assert.IsType<CrossingRunwayPhase>(restoredFollower.Phases?.CurrentPhase);
        Assert.Empty(NotClearCalls(restoredWarnings));
    }

    /// <summary>Ticks each second until <paramref name="follower"/> is at rest in its crossing; returns the second it was.</summary>
    private static int TickUntilAtRestInTheCrossing(SfoGround ground, AircraftState follower)
    {
        int restedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is CrossingRunwayPhase) && (follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts),
            BudgetSeconds,
            eachSecond: null
        );
        Assert.True(restedAt > 0, $"{follower.Callsign} never came to rest in the crossing behind the traffic");
        return restedAt;
    }

    /// <summary>The crossing's not-clear calls among <paramref name="warnings"/>.</summary>
    private static List<string> NotClearCalls(List<string> warnings) =>
        [.. warnings.Where(static w => w.StartsWith("Holding position, not clear of runway", StringComparison.Ordinal))];

    /// <summary>
    /// Ticks each second until <paramref name="follower"/> has been at rest for <see cref="NearLineRestSeconds"/>, noting in
    /// <paramref name="enteredCrossing"/> whether it was ever in the crossing. Returns the seconds at rest it ended on.
    /// </summary>
    private static int TickUntilAtRest(SfoGround ground, AircraftState follower, ref bool enteredCrossing)
    {
        int restSeconds = 0;
        for (int second = 1; (second <= BudgetSeconds) && (restSeconds < NearLineRestSeconds); second++)
        {
            ground.Engine.TickOneSecond();
            enteredCrossing |= follower.Phases?.CurrentPhase is CrossingRunwayPhase;
            restSeconds = follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts ? restSeconds + 1 : 0;
        }

        return restSeconds;
    }

    /// <summary>
    /// Spawns <see cref="TrafficCallsign"/> at rest on the far side of the crossing, facing on along it, its tail
    /// <see cref="TrafficTailPastFarBarFt"/> past the far holding-position marking.
    /// </summary>
    private static AircraftState SpawnTrafficPastTheFarBar(SfoGround ground, SfoCrossing crossing)
    {
        var heading = new TrueHeading(GeoMath.BearingTo(crossing.ApproachBar.Position, crossing.FarBar.Position));
        AircraftState traffic = SfoGroundHarness.SpawnAt(
            ground,
            TrafficCallsign,
            AircraftType,
            (crossing.FarBar, heading),
            new HoldingInPositionPhase()
        );
        double centreFt = TrafficTailPastFarBarFt + (AircraftLength.ResolveFt(AircraftType) / 2.0);
        traffic.Position = GeoMath.ProjectPoint(crossing.FarBar.Position, heading, centreFt / GeoMath.FeetPerNm);
        traffic.TrueTrack = heading;
        return traffic;
    }

    /// <summary>Spawns the follower at <see cref="SfoCrossing.ApproachNode"/> and sends it <see cref="TaxiCommand"/>.</summary>
    private static AircraftState SpawnFollowerAndTaxi(SfoGround ground, SfoCrossing crossing)
    {
        var heading = new TrueHeading(GeoMath.BearingTo(crossing.ApproachNode.Position, crossing.ApproachBar.Position));
        AircraftState follower = SfoGroundHarness.SpawnAt(
            ground,
            FollowingCallsign,
            AircraftType,
            (crossing.ApproachNode, heading),
            new HoldingInPositionPhase()
        );
        CommandResult taxi = ground.Engine.SendCommand(follower.Callsign, TaxiCommand);
        Assert.True(taxi.Success, $"'{TaxiCommand}' was refused for {follower.Callsign}: {taxi.Message}");
        return follower;
    }

    /// <summary>
    /// How far (ft) <paramref name="aircraft"/>'s nose is past the crossing's near holding-position marking, measured along the
    /// crossing's direction; negative while short of it.
    /// </summary>
    private static double NosePastNearMarkingFt(AircraftState aircraft, SfoCrossing crossing)
    {
        var bearing = new TrueHeading(GeoMath.BearingTo(crossing.ApproachBar.Position, crossing.FarBar.Position));
        double centreFt = GeoMath.AlongTrackDistanceNm(aircraft.Position, crossing.ApproachBar.Position, bearing) * GeoMath.FeetPerNm;
        return centreFt + (AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0);
    }

    /// <summary>Every warning <paramref name="engine"/> surfaces from here on for <paramref name="callsign"/>.</summary>
    private static List<string> CaptureWarnings(SimulationEngine engine, string callsign)
    {
        List<string> warnings = [];
        engine.WarningEmitted += (from, text) =>
        {
            if (from.Equals(callsign, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(text);
            }
        };
        return warnings;
    }

    /// <summary>The near-line gate's holding-short calls among <paramref name="warnings"/>.</summary>
    private static List<string> TrafficAheadCalls(List<string> warnings) =>
        [
            .. warnings.Where(static w =>
                w.StartsWith("Holding short of runway", StringComparison.Ordinal) && w.EndsWith(", traffic ahead", StringComparison.Ordinal)
            ),
        ];

    /// <summary>
    /// Spawns the pair on <see cref="SfoGroundHarness.CrossingTaxiway"/>, the leader <see cref="TrailFt"/> ahead of the follower,
    /// and sends both <see cref="TaxiCommand"/>.
    /// </summary>
    private (AircraftState Follower, AircraftState Leader) SpawnInTrailAndTaxi(SfoGround ground, SfoCrossing crossing, string type)
    {
        var heading = new TrueHeading(GeoMath.BearingTo(crossing.ApproachNode.Position, crossing.ApproachBar.Position));
        AircraftState follower = SfoGroundHarness.SpawnAt(
            ground,
            FollowingCallsign,
            type,
            (crossing.ApproachNode, heading),
            new HoldingInPositionPhase()
        );
        AircraftState leader = SfoGroundHarness.SpawnAt(
            ground,
            LeadingCallsign,
            type,
            (crossing.ApproachNode, heading),
            new HoldingInPositionPhase()
        );
        leader.Position = GeoMath.ProjectPoint(crossing.ApproachNode.Position, heading, TrailFt / GeoMath.FeetPerNm);
        leader.TrueTrack = heading;
        foreach (AircraftState ac in new[] { follower, leader })
        {
            CommandResult taxi = ground.Engine.SendCommand(ac.Callsign, TaxiCommand);
            Assert.True(taxi.Success, $"'{TaxiCommand}' was refused for {ac.Callsign}: {taxi.Message}");
        }

        return (follower, leader);
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/> is past the crossing's far-side hold-short, measured along the crossing's
    /// direction, and no longer in a <see cref="CrossingRunwayPhase"/>.
    /// </summary>
    private static bool HasClearedTheCrossing(AircraftState aircraft, SfoCrossing crossing, TrueHeading crossingBearing) =>
        (aircraft.Phases?.CurrentPhase is not CrossingRunwayPhase)
        && (GeoMath.AlongTrackDistanceNm(aircraft.Position, crossing.FarBar.Position, crossingBearing) * GeoMath.FeetPerNm > 0.0);
}
