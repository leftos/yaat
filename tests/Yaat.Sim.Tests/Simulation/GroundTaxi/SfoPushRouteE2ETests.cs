using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Tug moves end to end on the real SFO ramp, off gate D15 into the six alley: <c>PUSH $6A</c>, and the move a
/// ZOA ground controller makes in the ground video — <c>PUSH $6A</c> then <c>PUSH $6B</c> — issued here as one
/// <c>PUSHM $6A $6B</c>. The engine is driven rather than a phase ticked directly: <see cref="FlightPhysics"/> is
/// the only integrator of ground speed, so a bare OnTick loop would leave the aircraft parked on the stand.
///
/// <para>A whole move is sampled once per simulated second — the engine has no public way to advance one live
/// physics sub-tick (<see cref="SimulationEngine.ReplayOneSubTick"/> steps a replay, under the replay host) — and
/// checked for what a tug cannot do: turn the nose tighter than the type's tightest radius, turn it while the
/// aircraft is still, crab (a push keeps the nose on the reciprocal of its travel, a pull travels where the nose
/// points), or change between push and pull without first waiting stopped.</para>
///
/// <para>Every node is resolved by name — ids are geometry-coupled and renumber whenever the layout is
/// regenerated. A missing SFO layout silently skips, the repo's convention for absent test data.</para>
/// </summary>
public class SfoPushRouteE2ETests(ITestOutputHelper output)
{
    private const string Gate = "D15";
    private const string AlleySpot = "6A";
    private const string EndSpot = "6B";
    private const string AircraftType = "B738";
    private const string PushCommand = "PUSH $6A";
    private const string MoveCommand = "PUSHM $6A $6B";
    private const string TaxiOutTaxiway = "A";
    private const string TaxiOutCommand = $"TAXI {TaxiOutTaxiway}";

    /// <summary>The gate a stand-terminus move ends on — D15's neighbour across the six alley's near side.</summary>
    private const string EndGate = "D16";

    /// <summary>A move that ends on a stand rather than a spot: across the alley to 6A, then onto gate D16.</summary>
    private const string StandMoveCommand = $"PUSHM $6A @{EndGate}";

    /// <summary>The spot the redirect ends on — the third marking in the same alley, between 6A and 6B.</summary>
    private const string RedirectEndSpot = "6";

    /// <summary>The move a redirect issues mid-move: the same alley, stopping one marking short of the original.</summary>
    private const string RedirectCommand = "PUSHM $6A $6";

    /// <summary>
    /// Tick budget for a whole move off D15. A five-leg <c>PUSHM</c> spends a quarter-minute of towbar
    /// acceleration on every standing start and brakes onto every turn, so <c>PUSHM $6A @D16</c> runs past the
    /// 300 s that covered it when a tug started and stopped at the aircraft's own taxi rates.
    /// </summary>
    private const int MoveBudgetSeconds = 450;

    /// <summary>
    /// How far the running tug move must have moved the aircraft before a mid-move command is issued, feet: far
    /// enough that the move is under way, not just starting.
    /// </summary>
    private const double MidMoveFt = 30.0;

    /// <summary>How long a held aircraft may take to come to rest; shedding 5 kt at the towbar brake rate takes five seconds.</summary>
    private const int HoldStopBudgetSeconds = 6;

    /// <summary>The most speed a held tug may lose in the first second of the stop, knots: one second of the towbar brake rate.</summary>
    private const double HoldFirstSecondLossKts = 1.1;

    /// <summary>How far into the dwell before the pull onto 6A the dwell redirect is issued, seconds.</summary>
    private const double DwellRedirectAfterSeconds = 1.0;

    /// <summary>How long a held aircraft is watched for movement.</summary>
    private const int HoldObservationSeconds = 15;

    /// <summary>How long the aircraft is watched for a tug leg re-arming behind a taxi clearance.</summary>
    private const int TaxiObservationSeconds = 60;

    /// <summary>How often the per-second trajectory sample is written to the test output.</summary>
    private const int TrajectoryLogInterval = 5;

    /// <summary>Ground speed (kt) below which the aircraft counts as stopped.</summary>
    private const double AtRestSpeedKts = 0.5;

    /// <summary>How close to the spot's rest point a move checked end to end must finish.</summary>
    private const double RestPositionToleranceFt = 3.0;

    /// <summary>How close to the spot's nose-out heading a move checked end to end must finish.</summary>
    private const double RestNoseToleranceDeg = 1.0;

    /// <summary>How close to a spot's rest point a held or redirected move must finish.</summary>
    private const double OnSpotToleranceFt = 20.0;

    /// <summary>How far a held aircraft may drift before it counts as still moving.</summary>
    private const double FrozenToleranceFt = 2.0;

    /// <summary>A second in which the aircraft moved less than this counts as still.</summary>
    private const double StillFt = 0.05;

    /// <summary>The most the nose may turn in a still second.</summary>
    private const double StillNoseToleranceDeg = 0.01;

    /// <summary>The curvature bound's margin: a second's chord is a little shorter than the arc it spans.</summary>
    private const double CurvatureMargin = 1.05;

    /// <summary>The curvature bound's slack for rounding, radians.</summary>
    private const double CurvatureSlackRad = 0.002;

    /// <summary>How far a push's nose may sit off the reciprocal of its push heading.</summary>
    private const double PushNoseToleranceDeg = 1.0;

    /// <summary>How far a pull's displacement may point off the nose it had midway through the second.</summary>
    private const double PullTravelToleranceDeg = 2.0;

    /// <summary>A pull second must move at least this far before its displacement bearing is judged.</summary>
    private const double PullBearingMinMoveFt = 0.5;

    /// <summary>
    /// Whole still seconds that must precede the first movement after a change between push and pull: the 5 s
    /// dwell counts from a standstill in quarter-second steps, so at least four whole seconds are still.
    /// </summary>
    private const int MinStillSecondsBeforeReversal = 4;

    /// <summary>Fuselage length assumed for a type the FAA database does not carry, matching the handler's.</summary>
    private const double DefaultFuselageLengthFt = 110.0;

    /// <summary>
    /// <c>PUSH $6A</c> off D15: the straight push-off, a second straight push, a push onto the T6A lane's line, then a
    /// reversal and a creep pull up the lane onto the mark — flown without a pivot or a crab, ending nose-out on the rest
    /// point.
    /// </summary>
    [Fact]
    public void PushFromD15ToSixA_PushesStraightThenOntoTheLineThenPulls_WithoutPivotOrCrab()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "PSH9", AircraftType, Gate);
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, PushCommand);
        output.WriteLine($"{Gate} stand heading {ac.TrueHeading.Degrees:F1}° — '{PushCommand}' → success={result.Success} \"{result.Message}\"");
        Assert.True(result.Success, $"'{PushCommand}' off {Gate} was refused: {result.Message}");

        MoveRun run = TickMove(ground, ac, MoveBudgetSeconds);

        Assert.True(run.CompletedSecond > 0, $"the move never finished within {MoveBudgetSeconds}s (phase={PhaseName(ac)})");
        AssertMoves(
            run,
            (PushbackLegKind.Push, TugMoveShape.Straight, false),
            (PushbackLegKind.Push, TugMoveShape.Straight, false),
            (PushbackLegKind.Push, TugMoveShape.ViaLine, false),
            (PushbackLegKind.Pull, TugMoveShape.ViaLine, true)
        );
        AssertTugMotion(run);
        AssertRestsOnSpot(ground.Layout, ac, AlleySpot);
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary>
    /// The documented case. <c>PUSHM $6A $6B</c> off gate D15 is accepted, flown without a pivot or a crab, and
    /// leaves the aircraft stopped on spot 6B nose-out — the nosewheel on the marking, which puts the centroid a
    /// half-fuselage behind it, the same geometry <c>PUSH $spot</c> ends in. A spot is not a stand, so the
    /// aircraft holds after the pushback with no parking spot, as <c>PUSH $spot</c> leaves it.
    /// </summary>
    [Fact]
    public void PushmFromD15_ComesToRestNoseOutOnTheSecondSpot()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "PSM1", AircraftType, Gate);
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, MoveCommand);
        output.WriteLine($"{Gate} stand heading {ac.TrueHeading.Degrees:F1}° — '{MoveCommand}' → success={result.Success} \"{result.Message}\"");
        Assert.True(result.Success, $"'{MoveCommand}' off {Gate} was refused: {result.Message}");

        MoveRun run = TickMove(ground, ac, MoveBudgetSeconds);

        Assert.True(run.CompletedSecond > 0, $"the move never finished within {MoveBudgetSeconds}s (phase={PhaseName(ac)})");
        AssertTugMotion(run);
        AssertRestsOnSpot(ground.Layout, ac, EndSpot);
        Assert.True(ac.GroundSpeed <= AtRestSpeedKts, $"the aircraft was still moving at {ac.GroundSpeed:F2} kt when the move ended");
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.Null(ac.Ground.ParkingSpot);
    }

    /// <summary>
    /// A move whose last target is a stand parks the aircraft there, the same as <c>PUSH @gate</c>:
    /// <c>PUSHM $6A @D16</c> off D15 pushes across the alley and tows the aircraft onto the neighbouring gate,
    /// where it ends at parking with that gate as its parking spot.
    /// </summary>
    [Fact]
    public void PushmFromD15_EndingOnAGate_ParksTheAircraftThere()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "PSM8", AircraftType, Gate);
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, StandMoveCommand);
        output.WriteLine($"'{StandMoveCommand}' off {Gate} → success={result.Success} \"{result.Message}\"");
        Assert.True(result.Success, $"'{StandMoveCommand}' off {Gate} was refused: {result.Message}");

        MoveRun run = TickMove(ground, ac, MoveBudgetSeconds);
        GroundNode endGate =
            ground.Layout.FindParkingByName(EndGate) ?? throw new InvalidOperationException($"the SFO layout has no gate '{EndGate}'");
        output.WriteLine(
            $"at rest {DistanceFt(ac.Position, endGate.Position):F2} ft off {EndGate}, nose {ac.TrueHeading.Degrees:F2}° vs the stand's "
                + $"{DescribeHeading(endGate.TrueHeading?.Degrees)}"
        );

        Assert.True(run.CompletedSecond > 0, $"the move never finished within {MoveBudgetSeconds}s (phase={PhaseName(ac)})");
        AssertTugMotion(run);
        Assert.IsType<AtParkingPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(EndGate, ac.Ground.ParkingSpot);
    }

    /// <summary>
    /// A move whose last target is a stand parks on the stand's own heading, so a final facing with it is refused
    /// and the aircraft stays parked.
    /// </summary>
    [Fact]
    public void PushmEndingOnAGate_WithAFacing_Refused()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "PSM9", AircraftType, Gate);
        string command = $"{StandMoveCommand} FACE E";
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, command);
        output.WriteLine($"'{command}' off {Gate} → success={result.Success} \"{result.Message}\"");

        Assert.False(result.Success, $"'{command}' was accepted");
        Assert.Equal($"PUSHM to {EndGate} does not take a facing — the aircraft parks on the stand's own heading", result.Message);
        Assert.IsType<AtParkingPhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary>
    /// The moves the planner chose are the moves that run: two straight pushes off the stand, a push onto the T6A
    /// lane's line, a creep pull up onto 6A, a push onto the T6B lane's line, a creep pull up onto 6B. A push sets
    /// <c>Ground.PushbackTrueHeading</c> — the heading <see cref="FlightPhysics"/> displaces along, tail-first — for
    /// its whole run; a pull leaves it null, so the aircraft moves nose-first.
    /// </summary>
    [Fact]
    public void PushmFromD15_PushesStraightThenOntoEachLineThenCreepsOntoEachSpot()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "PSM2", AircraftType, Gate);
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, MoveCommand);
        Assert.True(result.Success, $"'{MoveCommand}' off {Gate} was refused: {result.Message}");

        AssertStandPushOffLeg(ac);

        MoveRun run = TickMove(ground, ac, MoveBudgetSeconds);

        AssertMoves(
            run,
            (PushbackLegKind.Push, TugMoveShape.Straight, false),
            (PushbackLegKind.Push, TugMoveShape.Straight, false),
            (PushbackLegKind.Push, TugMoveShape.ViaLine, false),
            (PushbackLegKind.Pull, TugMoveShape.ViaLine, true),
            (PushbackLegKind.Push, TugMoveShape.ViaLine, false),
            (PushbackLegKind.Pull, TugMoveShape.ViaLine, true)
        );
        foreach (Sample? sample in run.Samples.Where(s => s.Phase is not null))
        {
            bool push = sample.Phase!.Kind == PushbackLegKind.Push;
            Assert.True(
                push == sample.PushHeadingDeg.HasValue,
                $"t={sample.Second}s: a {sample.Phase.Kind} move had Ground.PushbackTrueHeading {DescribeHeading(sample.PushHeadingDeg)}"
            );
        }
    }

    /// <summary>
    /// Pins how the installed moves carry the push's ramp priority off a stand: move 0 is the push-off, every move
    /// flown through from it before the plan's first reversal is leg 1 of the same push, and the reversal and
    /// everything behind it — the second leg's push in particular — is a repositioning tow with neither flag.
    /// </summary>
    /// <param name="aircraft">The aircraft the tug move was installed on, before it is ticked.</param>
    private static void AssertStandPushOffLeg(AircraftState aircraft)
    {
        var moves = aircraft.Phases!.Phases.OfType<PushbackPhase>().ToList();
        Assert.True(moves[0].StartsAtStand, "the first move off the gate is not the stand push-off");
        Assert.False(moves[0].ContinuesStandPushOff, "the push-off itself carries the continuation flag");

        int firstDwell = moves.FindIndex(move => move.Move.DwellBefore);
        Assert.True(firstDwell > 0, $"the plan has no reversal to measure leg 1 against ({moves.Count} moves)");
        for (int i = 1; i < firstDwell; i++)
        {
            Assert.True(moves[i].ContinuesStandPushOff, $"move {i}, flown through from the push-off, lost the push's ramp priority");
        }

        for (int i = firstDwell; i < moves.Count; i++)
        {
            Assert.False(moves[i].ContinuesStandPushOff, $"move {i}, after the plan's first reversal, claimed the push's ramp priority");
            Assert.False(moves[i].StartsAtStand, $"move {i} claimed to be the stand push-off");
        }
    }

    /// <summary>
    /// The bug report's case: an aircraft already off its gate, holding in the alley, is not on a stand, so a
    /// first leg ahead of the nose is a tow rather than a refusal. It runs as a pull.
    /// </summary>
    [Fact]
    public void PushmFromTheAlley_FirstLegAheadOfTheNose_RunsAsAPull()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode from = Spot(ground.Layout, EndSpot);
        GroundNode toward = Spot(ground.Layout, AlleySpot);
        var facing = new TrueHeading(GeoMath.BearingTo(from.Position, toward.Position));
        AircraftState ac = SfoGroundHarness.SpawnAt(ground, "PSM3", AircraftType, (from, facing), new HoldingAfterPushbackPhase());

        CommandResult result = ground.Engine.SendCommand(ac.Callsign, MoveCommand);
        output.WriteLine($"holding in the alley facing {facing.Degrees:F0}° — '{MoveCommand}' → success={result.Success} \"{result.Message}\"");
        Assert.True(result.Success, $"'{MoveCommand}' from the alley was refused: {result.Message}");

        MoveRun run = TickMove(ground, ac, MoveBudgetSeconds);

        Assert.NotEmpty(run.Moves);
        Assert.Equal(PushbackLegKind.Pull, run.Moves[0].Kind);
        Assert.False(
            run.Samples.Any(s => ReferenceEquals(s.Phase, run.Moves[0].Phase) && s.PushHeadingDeg.HasValue),
            "the first leg set Ground.PushbackTrueHeading — the aircraft was reversed toward a target that was ahead of its nose"
        );
    }

    /// <summary>
    /// <c>HOLD</c>, issued while a tug move is rolling mid-move with more moves queued behind it, stops the tug and
    /// keeps it where it came to rest without dropping the move, and <c>RES</c> puts it back under way on the very
    /// leg it was running — not on the next one, and not on a fresh plan.
    /// </summary>
    [Fact]
    public void PushmHeldMidMove_ResumesOnTheLegItWasRunning()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "PSM4", AircraftType, Gate);
        Assert.True(ground.Engine.SendCommand(ac.Callsign, MoveCommand) is { Success: true });
        int cueSecond = TickUntilCue(ground, ac, "a move rolling mid-move with another queued", () => (MidMove(ac) is not null) && IsRolling(ac));

        PushbackPhase running = Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        double speedAtHold = ac.GroundSpeed;
        CommandResult hold = ground.Engine.SendCommand(ac.Callsign, "HOLD");
        Assert.True(hold.Success, $"'HOLD' during the tug move was refused: {hold.Message}");

        // The hold brakes the tow at the towbar rate rather than freezing it where it stands: a second later the
        // aircraft is still rolling, and has lost no more than that second of braking.
        ground.Engine.TickOneSecond();
        output.WriteLine($"held at {speedAtHold:F2} kt, {ac.GroundSpeed:F2} kt one second later");
        Assert.True(
            ac.GroundSpeed >= speedAtHold - HoldFirstSecondLossKts,
            $"the held tug went from {speedAtHold:F2} kt to {ac.GroundSpeed:F2} kt in one second, harder than the towbar brake rate"
        );
        Assert.True(ac.GroundSpeed > 0.0, $"the held tug stopped dead from {speedAtHold:F2} kt instead of braking to rest");

        int stoppedSecond = SfoGroundHarness.TickUntil(ground.Engine, () => !IsRolling(ac), HoldStopBudgetSeconds, null);
        Assert.True(stoppedSecond > 0, $"the held aircraft was still rolling at {ac.GroundSpeed:F2} kt {HoldStopBudgetSeconds}s after HOLD");

        LatLon heldAt = ac.Position;
        SfoGroundHarness.TickUntil(ground.Engine, () => false, HoldObservationSeconds, null);
        double driftFt = DistanceFt(ac.Position, heldAt);
        output.WriteLine(
            $"held on a {running.Kind} move at t={cueSecond}s, at rest {stoppedSecond}s later: drifted {driftFt:F1} ft over "
                + $"{HoldObservationSeconds}s, gs={ac.GroundSpeed:F2}kt"
        );

        Assert.True(driftFt <= FrozenToleranceFt, $"the held aircraft drifted {driftFt:F1} ft — HOLD stops the tug");
        Assert.Same(running, ac.Phases?.CurrentPhase);

        CommandResult resume = ground.Engine.SendCommand(ac.Callsign, "RES");
        Assert.True(resume.Success, $"'RES' after the hold was refused: {resume.Message}");
        Assert.Same(running, ac.Phases?.CurrentPhase);

        MoveRun run = TickMove(ground, ac, MoveBudgetSeconds);
        (LatLon Position, double OutHeadingDeg) rest = SpotRest(ground.Layout, EndSpot);
        double offRestFt = DistanceFt(ac.Position, rest.Position);
        output.WriteLine($"resumed: finished t={run.CompletedSecond}s, {offRestFt:F1} ft off the {EndSpot} rest point");

        Assert.Same(running, run.Moves[0].Phase);
        Assert.True(run.CompletedSecond > 0, $"the resumed move never finished within {MoveBudgetSeconds}s (phase={PhaseName(ac)})");
        Assert.True(offRestFt <= OnSpotToleranceFt, $"the resumed move ended {offRestFt:F1} ft from spot {EndSpot}'s rest point");
    }

    /// <summary>
    /// A taxi clearance mid-move drops the whole move, not just the leg under way: with the remaining legs
    /// still queued behind the running one, clearing the phase has to take them with it or a leg re-arms behind
    /// the taxi and reverses the aircraft mid-clearance. The clearance is issued at the first second, mid-move with a
    /// leg queued, from which <c>TAXI A</c> routes; from much of the ramp it is refused as unreachable.
    /// </summary>
    [Fact]
    public void TaxiMidMove_DropsEveryQueuedLeg()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "PSM5", AircraftType, Gate);
        Assert.True(ground.Engine.SendCommand(ac.Callsign, MoveCommand) is { Success: true });
        TickUntilCue(
            ground,
            ac,
            $"mid-move with another move queued, and '{TaxiOutCommand}' routing from here",
            () => (MidMove(ac) is not null) && TaxiOutRoutes(ground.Layout, ac)
        );

        int liveLegs = LiveTugLegs(ac).Count;
        Assert.True(liveLegs >= 2, $"the move only has {liveLegs} live tug leg(s) — this case needs a leg still pending behind the running one");

        CommandResult taxi = ground.Engine.SendCommand(ac.Callsign, TaxiOutCommand);
        output.WriteLine($"'{TaxiOutCommand}' mid-move → success={taxi.Success} \"{taxi.Message}\"");
        Assert.True(taxi.Success, $"'{TaxiOutCommand}' during the tug move was refused: {taxi.Message}");

        List<PushbackPhase> stillLive = LiveTugLegs(ac);
        Assert.True(stillLive.Count == 0, $"{stillLive.Count} tug leg(s) survived the taxi clearance — the remaining legs must go with the move");

        int reArmedSecond = SfoGroundHarness.TickUntil(ground.Engine, () => ac.Phases?.CurrentPhase is PushbackPhase, TaxiObservationSeconds, null);
        output.WriteLine($"after the taxi clearance: phase={PhaseName(ac)}, gs={ac.GroundSpeed:F2}kt");
        Assert.True(reArmedSecond < 0, $"a queued tug leg re-armed behind the taxi clearance at t={reArmedSecond}s");
    }

    /// <summary>
    /// A move already under way may be redirected: a second <c>PUSHM</c> replaces it whole, so the aircraft
    /// finishes on the new terminus and no leg of the original — the one running or the ones still queued behind
    /// it — survives the swap. The ground-view menu offers the command while a move is running, and an RPO
    /// redirecting an attached tug is ordinary.
    /// </summary>
    [Fact]
    public void PushmMidMove_ReplacesTheRunningMoveAndItsQueuedLegs()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "PSM7", AircraftType, Gate);
        Assert.True(ground.Engine.SendCommand(ac.Callsign, MoveCommand) is { Success: true });
        int cueSecond = TickUntilCue(ground, ac, "mid-move with another move queued", () => MidMove(ac) is not null);

        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        var originalLegs = ac.Phases!.Phases.OfType<PushbackPhase>().ToList();
        Assert.True(
            originalLegs.Count >= 2,
            $"the move only queued {originalLegs.Count} tug leg(s) — this case needs a leg still pending behind the running one"
        );

        CommandResult redirect = ground.Engine.SendCommand(ac.Callsign, RedirectCommand);
        output.WriteLine($"'{RedirectCommand}' at t={cueSecond}s into '{MoveCommand}' → success={redirect.Success} \"{redirect.Message}\"");
        Assert.True(redirect.Success, $"'{RedirectCommand}' during the tug move was refused: {redirect.Message}");

        var survivors = ac.Phases!.Phases.Where(p => originalLegs.Any(leg => ReferenceEquals(leg, p))).ToList();
        Assert.True(
            survivors.Count == 0,
            $"{survivors.Count} leg(s) of the original move survived the redirect — a fresh move replaces the one running and everything behind it"
        );

        MoveRun run = TickMove(ground, ac, MoveBudgetSeconds);
        (LatLon Position, double OutHeadingDeg) rest = SpotRest(ground.Layout, RedirectEndSpot);
        double offRestFt = DistanceFt(ac.Position, rest.Position);
        double offOldEndFt = DistanceFt(ac.Position, SpotRest(ground.Layout, EndSpot).Position);
        output.WriteLine(
            $"redirected: finished t={run.CompletedSecond}s — {offRestFt:F1} ft off spot {RedirectEndSpot}'s rest point, "
                + $"{offOldEndFt:F1} ft off spot {EndSpot}'s"
        );

        Assert.True(run.CompletedSecond > 0, $"the redirected move never finished within {MoveBudgetSeconds}s (phase={PhaseName(ac)})");
        Assert.True(
            offRestFt <= OnSpotToleranceFt,
            $"the redirected move ended {offRestFt:F1} ft from spot {RedirectEndSpot}'s rest point, past the {OnSpotToleranceFt:F0} ft tolerance"
        );
        Assert.True(
            offOldEndFt > OnSpotToleranceFt,
            $"the aircraft came to rest {offOldEndFt:F1} ft from spot {EndSpot}, the terminus of the move the redirect was supposed to replace"
        );
    }

    /// <summary>
    /// A redirect that reverses the move under way waits like any other reversal: <c>PUSHM $6A $6B</c> issued again
    /// while the aircraft is still being pushed, mid-move, on the push the plan follows with a pull, plans a pull onto
    /// 6A first, and the tug stops and waits the full dwell, stopped, before it pulls.
    /// </summary>
    [Fact]
    public void PushmMidPush_FirstNewMoveIsAPull_StopsAndDwellsBeforePulling()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "PSM9", AircraftType, Gate);
        Assert.True(ground.Engine.SendCommand(ac.Callsign, MoveCommand) is { Success: true });
        TickUntilCue(
            ground,
            ac,
            "a push rolling mid-move with a pull queued next, from where the re-plan starts with a pull",
            () =>
                (MidMove(ac) is { Kind: PushbackLegKind.Push })
                && IsRolling(ac)
                && (NextTugMove(ac) is { Kind: PushbackLegKind.Pull })
                && (FirstMoveOfMoveCommandFromHere(ground.Layout, ac) == PushbackLegKind.Pull)
        );
        PushbackPhase pushing = Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(PushbackLegKind.Push, pushing.Kind);
        Assert.True(ac.GroundSpeed > AtRestSpeedKts, $"test setup: the push is not rolling ({ac.GroundSpeed:F2} kt)");

        CommandResult redirect = ground.Engine.SendCommand(ac.Callsign, MoveCommand);
        Assert.True(redirect.Success, $"'{MoveCommand}' mid-push was refused: {redirect.Message}");
        PushbackPhase first = Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        output.WriteLine($"redirect → first move {first.Kind} {first.Move.Shape}, dwell before={first.Move.DwellBefore}");
        Assert.Equal(PushbackLegKind.Pull, first.Kind);
        Assert.True(first.Move.DwellBefore, "a pull replacing a running push is a reversal, so it dwells first");

        MoveRun run = TickMove(ground, ac, MoveBudgetSeconds);

        int firstPull = FirstForwardMotionIndex(run.Samples, first);
        Assert.True(firstPull > 0, "the redirected pull never moved the aircraft nose-first");
        double dwellSeconds = run.Samples[firstPull].DwellSeconds;
        int stillSeconds = StillSecondsBefore(run.Samples, firstPull);
        output.WriteLine(
            $"first nose-first second t={run.Samples[firstPull].Second}s: dwell {dwellSeconds:F2} s, {stillSeconds} still seconds before it"
        );
        Assert.True(dwellSeconds >= PushbackPhase.DwellSeconds, $"the pull moved after only {dwellSeconds:F2} s of dwell at rest");
        Assert.True(stillSeconds >= MinStillSecondsBeforeReversal, $"only {stillSeconds} still second(s) came before the pull moved");
    }

    /// <summary>
    /// A redirect issued while a pull is still waiting out its dwell reverses the push before that pull: the aircraft
    /// has not pulled yet. <c>PUSHM $6A $6B</c> issued again a second into the dwell before the creep onto 6A plans
    /// that same pull first, as a reversal, so the aircraft stays stopped at least
    /// <see cref="PushbackPhase.DwellSeconds"/> whole seconds in all, counted across the redirect, before it pulls.
    /// </summary>
    [Fact]
    public void PushmWhileAPullDwells_FirstNewMoveIsAPull_StillWaitsTheFullDwellBeforePulling()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "PSM10", AircraftType, Gate);
        Assert.True(ground.Engine.SendCommand(ac.Callsign, MoveCommand) is { Success: true });
        var before = new List<Sample> { SampleOf(0, ac) };
        int redirectSecond = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => before[^1] is { Phase: { Kind: PushbackLegKind.Pull, Move.DwellBefore: true }, DwellSeconds: >= DwellRedirectAfterSeconds },
            MoveBudgetSeconds,
            second => before.Add(SampleOf(second, ac))
        );
        Assert.True(redirectSecond > 0, "test setup: the move never reached a pull waiting out its dwell");
        PushbackPhase dwelling = Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        output.WriteLine(
            $"at t={redirectSecond}s: {dwelling.Kind} {dwelling.Move.Shape} dwelling {before[^1].DwellSeconds:F2} s, moved={dwelling.HasMoved}"
        );
        Assert.False(dwelling.HasMoved, "test setup: the dwelling pull has already moved the aircraft");

        CommandResult redirect = ground.Engine.SendCommand(ac.Callsign, MoveCommand);
        Assert.True(redirect.Success, $"'{MoveCommand}' during the dwell was refused: {redirect.Message}");
        PushbackPhase first = Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        output.WriteLine($"redirect → first move {first.Kind} {first.Move.Shape}, dwell before={first.Move.DwellBefore}");
        Assert.Equal(PushbackLegKind.Pull, first.Kind);
        Assert.True(first.Move.DwellBefore, "the aircraft last moved in a push, so the redirected pull is a reversal and dwells first");

        MoveRun run = TickMove(ground, ac, MoveBudgetSeconds);

        // The run's first sample is the redirect second again, now with the redirected move running.
        var samples = before.Take(before.Count - 1).Concat(run.Samples.Select(s => s with { Second = s.Second + redirectSecond })).ToList();
        int firstPull = FirstForwardMotionIndex(samples, first);
        Assert.True(firstPull > 0, "the redirected pull never moved the aircraft nose-first");
        int stillSeconds = StillSecondsBefore(samples, firstPull);
        output.WriteLine($"first nose-first second t={samples[firstPull].Second}s: {stillSeconds} still seconds before it, across the redirect");
        Assert.True(
            stillSeconds >= PushbackPhase.DwellSeconds,
            $"only {stillSeconds} still second(s), across the redirect, came before the pull moved"
        );
    }

    /// <summary>
    /// A move the planner refuses changes nothing: the aircraft keeps the phase it was in, still active, so a
    /// mis-clicked second point cannot leave it stranded off its stand.
    /// </summary>
    [Fact]
    public void RefusedPushm_LeavesTheAircraftsPhaseUntouched()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "PSM6", AircraftType, Gate);
        AtParkingPhase before = Assert.IsType<AtParkingPhase>(ac.Phases?.CurrentPhase);
        GroundNode bar = NearestRunwayHoldShort(ground.Layout, ac.Position);

        string command = $"PUSHM ${AlleySpot} #{bar.Id}";
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, command);
        output.WriteLine(
            $"'{command}' (second point is the {bar.Name ?? "unnamed"} holding position) → success={result.Success} \"{result.Message}\""
        );

        Assert.False(result.Success, $"a leg to a runway holding position was accepted: {result.Message}");
        Assert.Contains("Unable", result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Same(before, ac.Phases?.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, before.Status);
        Assert.Null(ac.Ground.PushbackTrueHeading);
    }

    /// <summary>The aircraft at the end of one simulated second.</summary>
    /// <param name="Second">The second, counted from the start of the sampled run (0 = before the first tick).</param>
    /// <param name="Position">The reference point.</param>
    /// <param name="NoseDeg">The nose heading, degrees true.</param>
    /// <param name="GroundSpeedKts">Ground speed, knots.</param>
    /// <param name="Phase">The tug move running, or null once none is.</param>
    /// <param name="PushHeadingDeg"><c>Ground.PushbackTrueHeading</c>, degrees true, or null.</param>
    /// <param name="DwellSeconds">The running move's dwell so far, seconds; zero when none is running.</param>
    private sealed record Sample(
        int Second,
        LatLon Position,
        double NoseDeg,
        double GroundSpeedKts,
        PushbackPhase? Phase,
        double? PushHeadingDeg,
        double DwellSeconds
    );

    /// <summary>What one tug move did while it ran.</summary>
    private sealed class MoveTrace
    {
        internal required PushbackPhase Phase { get; init; }

        internal PushbackLegKind Kind => Phase.Kind;

        internal required int FirstSecond { get; init; }

        internal int LastSecond { get; set; }

        /// <summary>The distance covered in the seconds that began with this move running, feet.</summary>
        internal double PathFt { get; set; }

        internal double PeakKts { get; set; }

        internal double DwellSeconds { get; set; }

        internal string Describe()
        {
            TugMove move = Phase.Move;
            string flags = $"{(move.DwellBefore ? " dwell" : "")}{(move.Tight ? " tight" : "")}{(move.Creep ? " creep" : "")}";
            return $"{move.Kind} {move.Shape}{flags}: t={FirstSecond}-{LastSecond}s ({LastSecond - FirstSecond + 1} s), path {PathFt:F1} ft, "
                + $"dwell {DwellSeconds:F2} s, peak {PeakKts:F2} kt";
        }
    }

    /// <summary>What a sampled run did: a sample per second, the moves in the order they ran, and when it ended.</summary>
    /// <param name="Samples">One sample per second, starting before the first tick.</param>
    /// <param name="Moves">The moves observed, in order.</param>
    /// <param name="CompletedSecond">The second no move was running any more, or -1 when the budget ran out.</param>
    private sealed record MoveRun(IReadOnlyList<Sample> Samples, IReadOnlyList<MoveTrace> Moves, int CompletedSecond);

    /// <summary>
    /// Ticks the engine until no tug move is running, sampling the aircraft every second, then writes one line per
    /// move (kind, shape, flags, duration, path length, dwell, peak speed) and the peak pull speed.
    /// </summary>
    private MoveRun TickMove(SfoGround ground, AircraftState ac, int budgetSeconds)
    {
        var samples = new List<Sample> { SampleOf(0, ac) };
        var moves = new List<MoveTrace>();
        Observe(moves, samples[0]);
        int completed = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => ac.Phases?.CurrentPhase is not PushbackPhase,
            budgetSeconds,
            second =>
            {
                Sample previous = samples[^1];
                Sample sample = SampleOf(second, ac);
                samples.Add(sample);
                if (moves.FirstOrDefault(m => ReferenceEquals(m.Phase, previous.Phase)) is { } startedIn)
                {
                    startedIn.PathFt += DistanceFt(previous.Position, sample.Position);
                }

                Observe(moves, sample);
                if ((second % TrajectoryLogInterval) == 0)
                {
                    output.WriteLine(
                        $"  t={second, 3}s move{moves.Count} {sample.Phase?.Kind.ToString() ?? "-", -4} gs={sample.GroundSpeedKts, 5:F2}kt "
                            + $"nose={sample.NoseDeg, 6:F1}° push={DescribeHeading(sample.PushHeadingDeg)} "
                            + $"pos=({sample.Position.Lat:F6},{sample.Position.Lon:F6})"
                    );
                }
            }
        );

        for (int i = 0; i < moves.Count; i++)
        {
            output.WriteLine($"move {i + 1}: {moves[i].Describe()}");
        }

        double peakPullKts = moves.Where(m => m.Kind == PushbackLegKind.Pull).Select(m => m.PeakKts).DefaultIfEmpty(0.0).Max();
        output.WriteLine($"finished t={completed}s, phase={PhaseName(ac)}, peak pull speed {peakPullKts:F2} kt");
        return new MoveRun(samples, moves, completed);
    }

    private static Sample SampleOf(int second, AircraftState ac)
    {
        var phase = ac.Phases?.CurrentPhase as PushbackPhase;
        double dwell = phase?.ToSnapshot() is PushbackPhaseDto dto ? dto.DwellElapsedSeconds : 0.0;
        return new Sample(second, ac.Position, ac.TrueHeading.Degrees, ac.GroundSpeed, phase, ac.Ground.PushbackTrueHeading?.Degrees, dwell);
    }

    private static void Observe(List<MoveTrace> moves, Sample sample)
    {
        if (sample.Phase is not { } phase)
        {
            return;
        }

        if ((moves.Count == 0) || !ReferenceEquals(moves[^1].Phase, phase))
        {
            moves.Add(new MoveTrace { Phase = phase, FirstSecond = sample.Second });
        }

        MoveTrace move = moves[^1];
        move.LastSecond = sample.Second;
        move.PeakKts = Math.Max(move.PeakKts, sample.GroundSpeedKts);
        move.DwellSeconds = sample.DwellSeconds;
    }

    /// <summary>
    /// The checks a tug move passes second by second: (a) the nose turns no more than the distance moved over the
    /// type's tightest radius; (b) a still second does not turn it; (c) a push keeps the nose on the reciprocal of
    /// its push heading, and a pull leaves the push heading null and moves where the nose pointed midway through
    /// the second; (d) every change between push and pull has at least four still seconds before the new move
    /// moves.
    /// </summary>
    private void AssertTugMotion(MoveRun run)
    {
        double tightRadiusFt = TugKinematics.TurnRadiusFt(AircraftType, tight: true);
        IReadOnlyList<Sample> samples = run.Samples;
        for (int i = 1; i < samples.Count; i++)
        {
            Sample from = samples[i - 1];
            Sample to = samples[i];
            double movedFt = DistanceFt(from.Position, to.Position);
            double turnedDeg = new TrueHeading(from.NoseDeg).AbsAngleTo(new TrueHeading(to.NoseDeg));
            double boundRad = ((movedFt / tightRadiusFt) * CurvatureMargin) + CurvatureSlackRad;
            Assert.True(
                (turnedDeg * Math.PI / 180.0) <= boundRad,
                $"t={to.Second}s: the nose turned {turnedDeg:F3}° over {movedFt:F2} ft, tighter than R={tightRadiusFt:F1} ft allows"
            );
            if (movedFt < StillFt)
            {
                Assert.True(turnedDeg < StillNoseToleranceDeg, $"t={to.Second}s: the nose turned {turnedDeg:F4}° while the aircraft was still");
            }

            AssertNoCrab(from, to, movedFt);
        }

        for (int k = 1; k < run.Moves.Count; k++)
        {
            MoveTrace previous = run.Moves[k - 1];
            MoveTrace next = run.Moves[k];
            if (previous.Kind == next.Kind)
            {
                continue;
            }

            int firstMotion = FirstMotionIndex(samples, next.Phase);
            Assert.True(firstMotion > 0, $"move {k + 1} ({next.Describe()}) never moved the aircraft");
            int still = StillSecondsBefore(samples, firstMotion);
            Assert.True(
                still >= MinStillSecondsBeforeReversal,
                $"move {k + 1} ({next.Describe()}) reversed move {k}'s {previous.Kind} after only {still} still second(s)"
            );
        }
    }

    private static void AssertNoCrab(Sample from, Sample to, double movedFt)
    {
        if (to.Phase is not { } phase)
        {
            return;
        }

        if (phase.Kind == PushbackLegKind.Push)
        {
            double pushDeg = Assert.NotNull(to.PushHeadingDeg);
            double offDeg = new TrueHeading(to.NoseDeg).AbsAngleTo(new TrueHeading(pushDeg).ToReciprocal());
            Assert.True(offDeg <= PushNoseToleranceDeg, $"t={to.Second}s: a push had the nose {offDeg:F2}° off the reciprocal of its travel");
            return;
        }

        Assert.True(to.PushHeadingDeg is null, $"t={to.Second}s: a pull had Ground.PushbackTrueHeading {DescribeHeading(to.PushHeadingDeg)}");
        if (!ReferenceEquals(from.Phase, phase) || (movedFt <= PullBearingMinMoveFt))
        {
            return;
        }

        double offDegPull = new TrueHeading(GeoMath.BearingTo(from.Position, to.Position)).AbsAngleTo(new TrueHeading(MidNoseDeg(from, to)));
        Assert.True(offDegPull <= PullTravelToleranceDeg, $"t={to.Second}s: a pull moved {offDegPull:F2}° off its nose over {movedFt:F2} ft");
    }

    /// <summary>
    /// Where the aircraft comes to rest: within <see cref="RestPositionToleranceFt"/> of the spot's rest point and
    /// <see cref="RestNoseToleranceDeg"/> of its nose-out heading.
    /// </summary>
    private void AssertRestsOnSpot(AirportGroundLayout layout, AircraftState ac, string spotName)
    {
        (LatLon Position, double OutHeadingDeg) rest = SpotRest(layout, spotName);
        double offRestFt = DistanceFt(ac.Position, rest.Position);
        double offNoseOutDeg = new TrueHeading(rest.OutHeadingDeg).AbsAngleTo(ac.TrueHeading);
        output.WriteLine(
            $"at rest {offRestFt:F2} ft off the {spotName} rest point, nose {ac.TrueHeading.Degrees:F2}° vs nose-out "
                + $"{rest.OutHeadingDeg:F2}° ({offNoseOutDeg:F2}° off), gs={ac.GroundSpeed:F2}kt"
        );
        Assert.True(
            offRestFt <= RestPositionToleranceFt,
            $"the move ended {offRestFt:F2} ft from spot {spotName}'s rest point, past the {RestPositionToleranceFt:F0} ft tolerance"
        );
        Assert.True(
            offNoseOutDeg <= RestNoseToleranceDeg,
            $"the nose finished {offNoseOutDeg:F2}° off spot {spotName}'s {rest.OutHeadingDeg:F1}° nose-out heading — a spot move leaves the "
                + "aircraft pointed out to taxi"
        );
    }

    /// <summary>The moves that ran, in order, are <paramref name="expected"/>: each one's kind, shape and whether it creeps onto the mark.</summary>
    private static void AssertMoves(MoveRun run, params (PushbackLegKind Kind, TugMoveShape Shape, bool Creep)[] expected)
    {
        (PushbackLegKind Kind, TugMoveShape Shape, bool Creep)[] ran = [.. run.Moves.Select(m => (m.Kind, m.Phase.Move.Shape, m.Phase.Move.Creep))];
        Assert.True(
            expected.SequenceEqual(ran),
            $"expected moves {string.Join(", ", expected)} but ran {string.Join(", ", run.Moves.Select(m => m.Describe()))}"
        );
    }

    /// <summary>The first sample index whose second began and ended in <paramref name="phase"/> and moved the aircraft.</summary>
    private static int FirstMotionIndex(IReadOnlyList<Sample> samples, PushbackPhase phase)
    {
        for (int i = 1; i < samples.Count; i++)
        {
            if (ReferenceEquals(samples[i - 1].Phase, phase) && ReferenceEquals(samples[i].Phase, phase) && (StepFt(samples, i) >= StillFt))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The first sample index whose second began and ended in <paramref name="phase"/> and moved the aircraft
    /// nose-first.
    /// </summary>
    private static int FirstForwardMotionIndex(IReadOnlyList<Sample> samples, PushbackPhase phase)
    {
        for (int i = 1; i < samples.Count; i++)
        {
            Sample from = samples[i - 1];
            Sample to = samples[i];
            bool inPhase = ReferenceEquals(from.Phase, phase) && ReferenceEquals(to.Phase, phase);
            if (!inPhase || (StepFt(samples, i) < StillFt))
            {
                continue;
            }

            double offNoseDeg = new TrueHeading(GeoMath.BearingTo(from.Position, to.Position)).AbsAngleTo(new TrueHeading(MidNoseDeg(from, to)));
            if (offNoseDeg < 90.0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>How many still seconds come straight before sample <paramref name="index"/>.</summary>
    private static int StillSecondsBefore(IReadOnlyList<Sample> samples, int index)
    {
        int still = 0;
        for (int i = index - 1; (i >= 1) && (StepFt(samples, i) < StillFt); i--)
        {
            still++;
        }

        return still;
    }

    private static double StepFt(IReadOnlyList<Sample> samples, int index) => DistanceFt(samples[index - 1].Position, samples[index].Position);

    /// <summary>The nose midway through a second: the start nose turned half the way to the end nose.</summary>
    private static double MidNoseDeg(Sample from, Sample to) =>
        new TrueHeading(from.NoseDeg + (new TrueHeading(from.NoseDeg).SignedAngleTo(new TrueHeading(to.NoseDeg)) / 2.0)).Degrees;

    private static List<PushbackPhase> LiveTugLegs(AircraftState ac) =>
        [.. ac.Phases!.Phases.OfType<PushbackPhase>().Where(p => p.Status is PhaseStatus.Active or PhaseStatus.Pending)];

    /// <summary>The tug move queued straight behind the running one, or null.</summary>
    private static PushbackPhase? NextTugMove(AircraftState ac) => LiveTugLegs(ac).Skip(1).FirstOrDefault();

    /// <summary>
    /// The running tug move, when it has moved the aircraft at least <see cref="MidMoveFt"/> and another move is
    /// queued behind it; null otherwise.
    /// </summary>
    private static PushbackPhase? MidMove(AircraftState ac) =>
        (ac.Phases?.CurrentPhase is PushbackPhase running) && (MovedFt(running) >= MidMoveFt) && (NextTugMove(ac) is not null) ? running : null;

    /// <summary>How far a tug move has moved the aircraft so far, feet.</summary>
    private static double MovedFt(PushbackPhase phase) => phase.ToSnapshot() is PushbackPhaseDto dto ? dto.ProgressDistanceFt : 0.0;

    private static bool IsRolling(AircraftState ac) => ac.GroundSpeed > AtRestSpeedKts;

    /// <summary>
    /// The kind of the first move <see cref="MoveCommand"/> would plan if issued now, planned the way the handler plans a
    /// redirect of a push that has moved: from the live pose, off the stand, with a push as the last motion. Null when
    /// the plan is refused. A push turn on the routine radius is long, and a re-plan early in it carries the turn on
    /// before pulling, so a test that needs a pull first has to wait for a pose that gives one.
    /// </summary>
    private static PushbackLegKind? FirstMoveOfMoveCommandFromHere(AirportGroundLayout layout, AircraftState ac)
    {
        var request = new TugRequest
        {
            Start = new TugPose(ac.Position, ac.TrueHeading.Degrees),
            StartsAtStand = false,
            AircraftType = ac.AircraftType,
            Goals = [TugGoal.Spot(Spot(layout, AlleySpot)), TugGoal.Spot(Spot(layout, EndSpot))],
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = PushbackLegKind.Push,
        };
        return TugMovePlanner.Plan(layout, request, out _)?.Moves[0].Move.Kind;
    }

    /// <summary>
    /// Ticks until <paramref name="cue"/> holds and writes what the aircraft was doing then; fails when the cue has not
    /// come within <see cref="MoveBudgetSeconds"/>.
    /// </summary>
    /// <returns>The second the cue came.</returns>
    private int TickUntilCue(SfoGround ground, AircraftState ac, string cueName, Func<bool> cue)
    {
        int second = SfoGroundHarness.TickUntil(ground.Engine, cue, MoveBudgetSeconds, null);
        Assert.True(second > 0, $"test setup: the cue '{cueName}' never came within {MoveBudgetSeconds}s (phase={PhaseName(ac)})");
        string running = ac.Phases?.CurrentPhase is PushbackPhase phase
            ? $"{phase.Kind} {phase.Move.Shape} moved {MovedFt(phase):F1} ft, next {NextTugMove(ac)?.Kind.ToString() ?? "none"}"
            : PhaseName(ac);
        output.WriteLine(
            $"cue '{cueName}' at t={second}s: {running}, {LiveTugLegs(ac).Count} tug legs live, gs={ac.GroundSpeed:F2}kt, "
                + $"pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) nose={ac.TrueHeading.Degrees:F1}°"
        );
        return second;
    }

    /// <summary>
    /// Whether <c>TAXI A</c> would route from where the aircraft is now, resolved the way the taxi handler resolves it,
    /// without issuing it: the start node nearest the aircraft along its heading (else the nearest node), re-anchored
    /// onto a node of taxiway A within 100 ft that is at least as close, then the explicit-path resolver from there
    /// with the aircraft's heading.
    /// </summary>
    private static bool TaxiOutRoutes(AirportGroundLayout layout, AircraftState ac)
    {
        GroundNode? start = layout.FindNearestNodeForTaxi(ac.Position, ac.TrueHeading) ?? layout.FindNearestNode(ac.Position);
        if (start is null)
        {
            return false;
        }

        if (
            !start.Edges.Any(e => e.MatchesTaxiway(TaxiOutTaxiway))
            && (layout.FindNearestNodeOnTaxiway(ac.Position, TaxiOutTaxiway, maxDistFt: 100.0) is { } onTaxiway)
            && (DistanceFt(ac.Position, onTaxiway.Position) <= DistanceFt(ac.Position, start.Position))
        )
        {
            start = onTaxiway;
        }

        var options = new ExplicitPathOptions { OccupiedTaxiway = null, StartHeadingTrue = ac.TrueHeading.Degrees };
        AircraftCategory category = AircraftCategorization.Categorize(ac.AircraftType);
        return TaxiPathfinder.ResolveExplicitPathDetailed(layout, start.Id, [TaxiOutTaxiway], out _, options, category) is not null;
    }

    /// <summary>
    /// Where a spot pushback leaves the aircraft: the centroid a half-fuselage behind the marking along the
    /// spot's nose-out heading, so the nosewheel sits on the mark. Recomputed here from the layout rather than
    /// read off the phase, so the test pins the geometry and not the handler's own arithmetic.
    /// </summary>
    /// <param name="layout">The SFO ground layout.</param>
    /// <param name="spotName">Name of the ramp spot.</param>
    /// <returns>The rest point and the spot's nose-out heading in degrees true.</returns>
    private static (LatLon Position, double OutHeadingDeg) SpotRest(AirportGroundLayout layout, string spotName)
    {
        GroundNode spot = Spot(layout, spotName);
        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double outBearingDeg), $"spot '{spotName}' has no outbound heading in the layout");
        double halfLengthNm = ((FaaAircraftDatabase.Get(AircraftType)?.LengthFt ?? DefaultFuselageLengthFt) / 2.0) / GeoMath.FeetPerNm;
        return (GeoMath.ProjectPoint(spot.Position, new TrueHeading(outBearingDeg).ToReciprocal(), halfLengthNm), outBearingDeg);
    }

    private static GroundNode Spot(AirportGroundLayout layout, string spotName) =>
        layout.FindSpotNodeByName(spotName) ?? throw new InvalidOperationException($"the SFO layout has no spot named '{spotName}'");

    /// <summary>The runway holding position nearest <paramref name="from"/> — a point no tug move may reach.</summary>
    private static GroundNode NearestRunwayHoldShort(AirportGroundLayout layout, LatLon from)
    {
        GroundNode? best = null;
        double bestNm = double.MaxValue;
        foreach (GroundNode node in layout.Nodes.Values)
        {
            if (node.Type != GroundNodeType.RunwayHoldShort)
            {
                continue;
            }

            double distNm = GeoMath.DistanceNm(from, node.Position);
            if (distNm < bestNm)
            {
                bestNm = distNm;
                best = node;
            }
        }

        Assert.True(best is not null, "the SFO layout carries no runway holding positions");
        return best!;
    }

    private static string DescribeHeading(double? headingDeg) => headingDeg is { } deg ? $"{deg:F1}°" : "null";

    private static double DistanceFt(LatLon from, LatLon to) => GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;

    private static string PhaseName(AircraftState ac) => ac.Phases?.CurrentPhase?.Name ?? "null";
}
