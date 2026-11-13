using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Phases.Ground;

/// <summary>
/// A tug plan is one continuous tow, not a queue of standing starts. <c>PUSH $6B</c> from SFO gate E6 plans
/// three moves — a straight push-off, a push turn onto the approach line, then a pull onto the spot behind a
/// reversal — and the tug carries its speed across the boundary between the two pushes. Only a genuine stop
/// slows it: the reversal, which dwells (AC 00-65A §11.17), and the end of the plan.
///
/// <para>The defect these pin: every move clamped its last 10 ft to 1 kt and zeroed the speed when it
/// completed, so the aircraft crawled and restarted at each boundary and took most of a minute to cover its
/// first 90 ft.</para>
/// </summary>
public class PushbackMoveBoundaryTests
{
    private const string Pusher = "PSH1";
    private const string PusherType = "B738";

    /// <summary>The E-pier gate the push starts from: its plan has three moves, the first only 65 ft long.</summary>
    private const string PusherGate = "E6";

    /// <summary>The spot the push ends on, the other side of the alley from the gate.</summary>
    private const string AlleySpot = "6B";

    /// <summary>The budget the six-alley choreography tests give a full E6 push to 6B.</summary>
    private const int BudgetSeconds = 400;

    /// <summary>
    /// The floor the tow holds between boundaries, knots. The tug runs at 5 kt through straights and turns alike, so a
    /// sample below this is the aircraft braking for a move boundary rather than steering through one. Well clear of
    /// the 1 kt final-approach crawl and of the standing restart that follows it.
    /// </summary>
    private const double ContinuousSpeedFloorKts = 2.0;

    /// <summary>
    /// The speed a push is up to once it is clear of the stand, knots, and the second it has by: a tug picks speed
    /// up at <see cref="CategoryPerformance.TugAccelRate"/>, so 4.5 kt from rest is 15 s of pulling, and the budget
    /// carries two seconds of slack for the quarter-second sub-ticks and the second the clearance lands in.
    /// </summary>
    private const double PromptSpeedKts = 4.5;
    private const int PromptSpeedBySecond = 17;

    /// <summary>Ground speed at or below which the aircraft counts as stopped for the dwell, knots.</summary>
    private const double AtRestKts = 0.05;

    /// <summary>Slack over the half-fuselage setback a completed spot push rests with, feet.</summary>
    private const double SpotToleranceMarginFt = 25.0;

    /// <summary>How far off the spot's outbound heading a completed spot push may rest, degrees.</summary>
    private const double SpotHeadingToleranceDeg = 15.0;

    /// <summary>
    /// The clearance that takes the tug off mid-move: a taxi to the spot, which routes from the ramp the push is
    /// still on (a movement-area taxiway does not, mid-push-off — the route leaves the movement area). It is a TAXIAUTO: a
    /// plain <c>TAXI $6B</c> from mid-push is refused ("Unable, need a route to spot 6B. To auto-route it: TAXIAUTO $6B"),
    /// since no route inside the ramp fits from there and a bare TAXI from the ramp stays inside it.
    /// </summary>
    private const string TaxiClearance = $"TAXIAUTO ${AlleySpot}";

    /// <summary>
    /// The fastest the aircraft may be going one second after a clearance clears the push, knots: the tug let go
    /// at a standstill, so all it can have is the one second of taxi acceleration from rest (1 kt/s) — the taxi
    /// rate, because the towbar rates went with the tug.
    /// </summary>
    private const double ClearedSpeedCeilingKts = 1.0;

    /// <summary>How far a second's speed change may overshoot the towbar rate it is integrated at, knots.</summary>
    private const double RateToleranceKts = 0.05;

    /// <summary>A planned step whose nose-gear steer angle reaches this is a turning step, degrees.</summary>
    private const double TurnNoticedDeg = 1.0;

    /// <summary>
    /// How near the planned turn entry the steering tow must be for its steer to be that turn's, feet: a few sub-tick
    /// steps, so a small steer the move takes earlier on its run in (a pull settling onto its line) is not the entry; a
    /// judgement figure.
    /// </summary>
    private const double EntryReachedFt = 10.0;

    /// <summary>How far under the push speed or the turn brake's curve a sub-tick's tug speed may read, knots; float noise.</summary>
    private const double BrakeFloorToleranceKts = 0.01;

    /// <summary>
    /// The fastest the tug may still be going the second before it comes to a stand, knots: the last stretch is
    /// held at <see cref="PushbackPhase.FinalApproachKts"/>, and a second sampled just before the crawl window
    /// opens sits a sliver above it (1.15 kt measured on this run).
    /// </summary>
    private const double CrawlBeforeStopKts = 1.5;

    private readonly ITestOutputHelper _output;

    public PushbackMoveBoundaryTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// The push-off and the turn behind it are both pushes, so the tug never stops between them: from the
    /// first second the aircraft is properly rolling until it starts slowing for the reversal, no second is
    /// slower than <see cref="ContinuousSpeedFloorKts"/>. The straight → turn boundary falls inside that
    /// window, and a tow that stops and restarts there shows up as a 1 kt second.
    ///
    /// <para>The window ends where the run of below-floor seconds that leads into the dwell begins, not at
    /// the first still second: the move before a reversal <em>is</em> a genuine stop, so it still crawls its
    /// last 10 ft at 1 kt before standing still.</para>
    /// </summary>
    [Fact]
    public void SpeedContinuesAcrossSameDirectionMoves()
    {
        PushRun? run = RunPush();
        if (run is null)
        {
            return;
        }

        List<SpeedSample> samples = run.Value.Samples;
        int rolling = samples.FindIndex(s => s.GroundSpeedKts > ContinuousSpeedFloorKts);
        Assert.True(rolling >= 0, $"the push never exceeded {ContinuousSpeedFloorKts:F1} kt: {Trace(samples)}");
        int atRest = samples.FindIndex(rolling, s => s.GroundSpeedKts <= AtRestKts);
        Assert.True(atRest > rolling, $"the push never came to rest for the reversal dwell: {Trace(samples)}");

        int slowingForReversal = atRest;
        while ((slowingForReversal > rolling) && (samples[slowingForReversal - 1].GroundSpeedKts < ContinuousSpeedFloorKts))
        {
            slowingForReversal--;
        }

        for (int i = rolling; i < slowingForReversal; i++)
        {
            SpeedSample sample = samples[i];
            Assert.True(
                sample.GroundSpeedKts >= ContinuousSpeedFloorKts,
                $"the tow dropped to {sample.GroundSpeedKts:F1} kt at t={sample.Second}s, between rolling at t={samples[rolling].Second}s "
                    + $"and slowing for the reversal at t={samples[slowingForReversal].Second}s — it stopped at a move boundary: {Trace(samples)}"
            );
        }
    }

    /// <summary>The tow is up to push speed within seconds of the clearance, not a minute later.</summary>
    [Fact]
    public void ReachesPushSpeedPromptly()
    {
        PushRun? run = RunPush();
        if (run is null)
        {
            return;
        }

        List<SpeedSample> samples = run.Value.Samples;
        double bestKts = samples.Where(s => s.Second <= PromptSpeedBySecond).Max(s => s.GroundSpeedKts);
        Assert.True(
            bestKts >= PromptSpeedKts,
            $"the push reached only {bestKts:F1} kt in its first {PromptSpeedBySecond}s, short of {PromptSpeedKts:F1} kt: {Trace(samples)}"
        );
    }

    /// <summary>
    /// The reversal before the pull still stops the aircraft for its full dwell: a run of at least
    /// <see cref="PushbackPhase.DwellSeconds"/> consecutive still seconds before the tug pulls it forward.
    /// </summary>
    [Fact]
    public void ReversalStillDwells()
    {
        PushRun? run = RunPush();
        if (run is null)
        {
            return;
        }

        List<SpeedSample> samples = run.Value.Samples;
        int pulling = samples.FindIndex(s => !s.Pushing && (s.GroundSpeedKts > AtRestKts));
        Assert.True(pulling > 0, $"the tug never pulled the aircraft forward: {Trace(samples)}");

        int stillSeconds = 0;
        for (int i = pulling - 1; (i >= 0) && (samples[i].GroundSpeedKts <= AtRestKts); i--)
        {
            stillSeconds++;
        }

        Assert.True(
            stillSeconds >= PushbackPhase.DwellSeconds,
            $"the reversal stood still for only {stillSeconds}s before the pull at t={samples[pulling].Second}s, "
                + $"against a {PushbackPhase.DwellSeconds:F0}s dwell: {Trace(samples)}"
        );
    }

    /// <summary>The plan still finishes where it was sent: nosewheel on 6B, nose out along the lane.</summary>
    [Fact]
    public void CompletesOnTheSpot()
    {
        PushRun? run = RunPush();
        if (run is null)
        {
            return;
        }

        PushRun push = run.Value;
        Assert.True(
            push.DoneSecond > 0,
            $"the push never completed within {BudgetSeconds}s (phase={push.Aircraft.Phases?.CurrentPhase?.Name ?? "null"}): {Trace(push.Samples)}"
        );
        AssertRestingOnSpot(push.Aircraft, push.Spot, push.Ground.Layout);
    }

    /// <summary>
    /// A clearance that takes the tug off mid-move stops the aircraft dead. Only a move that runs to completion
    /// hands its speed to the move behind it; a <c>TAXI</c> that clears the phase ends the push with the aircraft
    /// still tail-first, and 5 kt carried into the next phase would run it on along a direction of travel that is
    /// about to flip.
    /// </summary>
    [Fact]
    public void ClearedMidMoveStopsDead()
    {
        SfoGround? built = SfoGroundHarness.Build(_output, autoCrossMode: AutoCrossRunwayMode.Off);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, Pusher, PusherType, PusherGate);
        string command = $"PUSH ${AlleySpot}";
        CommandResult issued = ground.Engine.SendCommand(Pusher, command);
        Assert.True(issued.Success, $"'{command}' from {PusherGate} failed: {issued.Message}");

        int rolling = SfoGroundHarness.TickUntil(ground.Engine, () => aircraft.GroundSpeed >= PromptSpeedKts, PromptSpeedBySecond, null);
        Assert.True(rolling > 0, $"the push never reached {PromptSpeedKts:F1} kt within {PromptSpeedBySecond}s to be cleared off");
        PushbackPhase running = Assert.IsType<PushbackPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(running.ContinuesIntoNextMove, "the push-off should be a move that continues into the next one");
        Assert.NotNull(aircraft.Ground.PushbackTrueHeading);

        CommandResult taxi = ground.Engine.SendCommand(Pusher, TaxiClearance);
        Assert.True(taxi.Success, $"'{TaxiClearance}' mid-push failed: {taxi.Message}");
        ground.Engine.TickOneSecond();

        _output.WriteLine(
            $"{Pusher}: cleared at t={rolling}s, gs={aircraft.GroundSpeed:F1}kt one second later, phase={aircraft.Phases?.CurrentPhase?.Name}"
        );
        Assert.True(
            aircraft.GroundSpeed <= ClearedSpeedCeilingKts,
            $"{Pusher} was still doing {aircraft.GroundSpeed:F1} kt a second after '{TaxiClearance}' cleared the push at "
                + $"{PromptSpeedKts:F1}+ kt — the cleared move handed its speed on instead of stopping dead"
        );
        Assert.Null(aircraft.Ground.PushbackTrueHeading);
        Assert.Null(aircraft.Targets.DesiredAccelRate);
        Assert.Null(aircraft.Targets.DesiredDecelRate);
    }

    /// <summary>
    /// Nothing on a towbar starts or stops at the aircraft's own taxi rates: every second of the run changes the
    /// speed by no more than <see cref="CategoryPerformance.TugAccelRate"/> going up or
    /// <see cref="CategoryPerformance.TugDecelRate"/> coming down. The one fall that is allowed to be abrupt is
    /// the standstill at the end of a move, which the tug reaches already down at the
    /// <see cref="PushbackPhase.FinalApproachKts"/> crawl — <see cref="CrawlBeforeStopKts"/>, the same allowance
    /// <see cref="ReachesTheCrawlBeforeTheStop"/> holds every stop to.
    /// </summary>
    [Fact]
    public void SpeedChangesAtTowbarRates()
    {
        PushRun? run = RunPush();
        if (run is null)
        {
            return;
        }

        List<SpeedSample> samples = run.Value.Samples;
        double accelRate = CategoryPerformance.TugAccelRate(AircraftCategory.Jet);
        double decelRate = CategoryPerformance.TugDecelRate(AircraftCategory.Jet);
        for (int i = 1; i < samples.Count; i++)
        {
            double from = samples[i - 1].GroundSpeedKts;
            double change = samples[i].GroundSpeedKts - from;
            if (change >= 0.0)
            {
                Assert.True(
                    change <= accelRate + RateToleranceKts,
                    $"the tow gained {change:F2} kt over the second to t={samples[i].Second}s, past the {accelRate:F1} kt/s towbar "
                        + $"accel rate: {Trace(samples)}"
                );
                continue;
            }

            if (from <= CrawlBeforeStopKts)
            {
                continue;
            }

            Assert.True(
                -change <= decelRate + RateToleranceKts,
                $"the tow shed {-change:F2} kt over the second to t={samples[i].Second}s from {from:F2} kt, past the {decelRate:F1} kt/s "
                    + $"towbar brake rate: {Trace(samples)}"
            );
        }
    }

    /// <summary>
    /// The tug does not ease off for a turn earlier than its brake needs, with no wingtip-pace slowdown. The turn judged
    /// is the first the tug meets inside a move: the first planned step whose nose-gear steer angle reaches
    /// <see cref="TurnNoticedDeg"/> after a straight step of the same move, at <c>δ_entry</c>. On that move's straight
    /// run into it the tug — the main gear's speed over the cosine of the steer angle —
    /// first reaches push speed while the brake does not yet bind, and from then on to the entry, sampled every physics
    /// sub-tick, it is never under the lower of push speed and the braking curve onto <c>5·cos δ_entry</c> at the towbar
    /// brake rate from where it stands (one sub-tick of travel taken off the distance as lead), less
    /// <see cref="BrakeFloorToleranceKts"/>. The settled seconds of a turn are held to push speed by
    /// <c>PushTowSpeedTests.TurningTow_TugHoldsPushbackSpeed</c>.
    ///
    /// <para>The distance to the entry is the straight-line distance from the aircraft to the planned entry point: the
    /// run into it is straight, and a distance summed along the flown path would carry the flown path's own drift.</para>
    /// </summary>
    [Fact]
    public void HoldsPushSpeedUntilTheTurnBrake()
    {
        if (SfoGroundHarness.Build(_output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, Pusher, PusherType, PusherGate);
        var start = new PushbackPose(aircraft.Position, aircraft.TrueHeading.Degrees);
        string command = $"PUSH ${AlleySpot}";
        CommandResult issued = ground.Engine.SendCommand(Pusher, command);
        Assert.True(issued.Success, $"'{command}' from {PusherGate} failed: {issued.Message}");

        List<PushbackPhase> phases = [.. aircraft.Phases!.Phases.OfType<PushbackPhase>()];
        (int move, LatLon entryPoint, double entrySteerDeg) = PlannedTurnEntry(
            start,
            [.. phases.Select(p => p.Move)],
            AircraftFootprint.FromType(PusherType)
        );
        PushbackPhase turningPhase = phases[move];
        bool turned = false;
        List<SubTickSample> run = [];
        SubTickDriver.TickSubTicksUntil(
            ground.Engine,
            Pusher,
            _ => turned,
            BudgetSeconds,
            a =>
            {
                if (!ReferenceEquals(a.Phases?.CurrentPhase, turningPhase))
                {
                    return;
                }

                double steerDeg = a.Ground.TowbarTrueHeading is { } towbar ? towbar.AbsAngleTo(a.TrueHeading) : 0.0;
                double distanceFt = GeoMath.DistanceNm(a.Position, entryPoint) * GeoMath.FeetPerNm;
                turned = (steerDeg >= TurnNoticedDeg) && (distanceFt <= EntryReachedFt);
                if (!turned)
                {
                    run.Add(new SubTickSample(a.IndicatedAirspeed / Math.Cos(steerDeg * Math.PI / 180.0), distanceFt));
                }
            }
        );

        double pushKts = CategoryPerformance.PushbackSpeed(AircraftCategory.Jet);
        double decelKtPerSec = CategoryPerformance.TugDecelRate(AircraftCategory.Jet);
        double turnKts = pushKts * Math.Cos(entrySteerDeg * Math.PI / 180.0);
        Assert.True(turned, $"the tug never entered the planned turn in move {move + 1} ({entrySteerDeg:F1}° of steer)");
        double FloorKts(SubTickSample s) => Math.Min(pushKts, BrakeFloorKts(s, turnKts, decelKtPerSec));
        int atPace = run.FindIndex(s => (Math.Abs(s.TugKts - pushKts) <= BrakeFloorToleranceKts) && (FloorKts(s) >= pushKts));
        _output.WriteLine(
            $"turn entry at {entrySteerDeg:F1}° (turn pace {turnKts:F2} kt); last of the run in, tug/floor@ft: "
                + string.Join(" ", run.TakeLast(40).Select(s => $"{s.TugKts:F2}/{FloorKts(s):F2}@{s.DistanceFt:F0}"))
        );

        Assert.True(atPace >= 0, $"the tug never reached the {pushKts:F1} kt push speed before the turn brake bound it");
        foreach (SubTickSample s in run.Skip(atPace))
        {
            Assert.True(
                s.TugKts >= FloorKts(s) - BrakeFloorToleranceKts,
                $"the tug was doing {s.TugKts:F3} kt {s.DistanceFt:F1} ft before the turn, under the {FloorKts(s):F3} kt its brake needs"
            );
        }
    }

    /// <summary>
    /// The braking curve onto <paramref name="turnKts"/> at the turn entry, from <paramref name="sample"/>'s distance
    /// less one sub-tick of travel at its speed: <c>sqrt(v_turn² + 2·a·d)</c>, the distance in knot-seconds.
    /// </summary>
    private static double BrakeFloorKts(SubTickSample sample, double turnKts, double decelKtPerSec)
    {
        const double ftPerSecPerKt = GeoMath.FeetPerNm / 3600.0;
        double leadFt = sample.TugKts * ftPerSecPerKt / SimulationEngine.PhysicsSubTickRate;
        double distanceFt = Math.Max(0.0, sample.DistanceFt - leadFt);
        return Math.Sqrt((turnKts * turnKts) + (2.0 * decelKtPerSec * distanceFt / ftPerSecPerKt));
    }

    /// <summary>
    /// Where the plan first turns inside a move: the start of the first planned step whose nose-gear steer angle,
    /// <c>atan(L·κ)</c> on the type's wheelbase, reaches <see cref="TurnNoticedDeg"/> after a straight step of the
    /// same move, with that move's index and that angle. A move that turns from its first step is not entered from a
    /// straight run inside it.
    /// </summary>
    private static (int Move, LatLon Point, double SteerDeg) PlannedTurnEntry(PushbackPose start, List<TugMove> moves, AircraftFootprint footprint)
    {
        double wheelbaseFt =
            ((footprint.WheelbaseFt is { } recorded) && (recorded > 0.0)) ? recorded : TugKinematics.TurnRadiusFt(footprint, tight: false);
        IReadOnlyList<TugMoveTrace> traces = TugKinematics.Simulate(start, moves, footprint, TugMovePlanner.StepFt).Moves;
        for (int m = 0; m < traces.Count; m++)
        {
            IReadOnlyList<PushbackPose> trace = traces[m].Samples;
            for (int i = 2; i < trace.Count; i++)
            {
                double steerDeg = PlannedSteerDeg(trace[i - 1], trace[i], wheelbaseFt);
                if ((steerDeg >= TurnNoticedDeg) && (PlannedSteerDeg(trace[i - 2], trace[i - 1], wheelbaseFt) < TurnNoticedDeg))
                {
                    return (m, trace[i - 1].Position, steerDeg);
                }
            }
        }

        Assert.Fail("the plan never turns inside a move");
        return default;
    }

    /// <summary>The nose-gear steer angle a planned step implies, degrees: <c>atan(L·κ)</c>, <c>κ</c> the step's turn per foot.</summary>
    private static double PlannedSteerDeg(PushbackPose from, PushbackPose to, double wheelbaseFt)
    {
        double stepFt = GeoMath.DistanceNm(from.Position, to.Position) * GeoMath.FeetPerNm;
        double turnRad = new TrueHeading(from.NoseTrueDeg).AbsAngleTo(new TrueHeading(to.NoseTrueDeg)) * Math.PI / 180.0;
        return stepFt > 0.0 ? Math.Atan(wheelbaseFt * turnRad / stepFt) * 180.0 / Math.PI : 0.0;
    }

    /// <summary>
    /// One physics sub-tick of the tow's run in to the turn: the tug's speed (the gear's over the cosine of the towbar
    /// steer) and the distance to the planned turn entry.
    /// </summary>
    private readonly record struct SubTickSample(double TugKts, double DistanceFt);

    /// <summary>
    /// Every stand the tug comes to is reached at the crawl, not dropped onto from walking pace: the last rolling
    /// second before each standstill is at most <see cref="CrawlBeforeStopKts"/>.
    /// </summary>
    [Fact]
    public void ReachesTheCrawlBeforeTheStop()
    {
        PushRun? run = RunPush();
        if (run is null)
        {
            return;
        }

        List<SpeedSample> samples = run.Value.Samples;
        int stops = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            if ((samples[i].GroundSpeedKts > AtRestKts) || (samples[i - 1].GroundSpeedKts <= AtRestKts))
            {
                continue;
            }

            stops++;
            Assert.True(
                samples[i - 1].GroundSpeedKts <= CrawlBeforeStopKts,
                $"the tug stood still at t={samples[i].Second}s straight off {samples[i - 1].GroundSpeedKts:F2} kt, without reaching the "
                    + $"{CrawlBeforeStopKts:F1} kt crawl first: {Trace(samples)}"
            );
        }

        Assert.True(stops > 0, $"the run never came to a standstill: {Trace(samples)}");
    }

    /// <summary>One second of the run: the ground speed, the nose, the move running it and the push/pull of it.</summary>
    /// <param name="Second">Seconds since the clearance.</param>
    /// <param name="GroundSpeedKts">Ground speed at the end of that second.</param>
    /// <param name="NoseTrueDeg">True heading of the nose at the end of that second.</param>
    /// <param name="Phase">The phase driving the aircraft at the end of that second — one instance per planned move.</param>
    /// <param name="Pushing">The aircraft was being pushed (tail-first) rather than pulled.</param>
    /// <param name="SteerDeg">The nose-gear steer angle at the end of that second — the towbar's angle off the nose; zero with no towbar set.</param>
    private readonly record struct SpeedSample(int Second, double GroundSpeedKts, double NoseTrueDeg, Phase? Phase, bool Pushing, double SteerDeg);

    /// <summary>A finished E6 push to 6B: the world it ran in, the per-second samples and when it came to rest.</summary>
    private readonly record struct PushRun(SfoGround Ground, AircraftState Aircraft, GroundNode Spot, List<SpeedSample> Samples, int DoneSecond);

    /// <summary>
    /// Spawns a B738 parked at E6, pushes it to 6B and samples the ground speed every second until it comes
    /// to rest on the spot. Null when the layout or navdata is unavailable (the harness's silent-skip
    /// convention).
    /// </summary>
    /// <returns>The run, or null to skip.</returns>
    private PushRun? RunPush()
    {
        SfoGround? built = SfoGroundHarness.Build(_output, autoCrossMode: AutoCrossRunwayMode.Off);
        if (built is null)
        {
            return null;
        }

        SfoGround ground = built.Value;
        GroundNode? spot = ground.Layout.FindSpotNodeByName(AlleySpot);
        Assert.True(spot is not null, $"the SFO layout has no spot named '{AlleySpot}'");

        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, Pusher, PusherType, PusherGate);
        string command = $"PUSH ${AlleySpot}";
        CommandResult issued = ground.Engine.SendCommand(Pusher, command);
        Assert.True(issued.Success, $"'{command}' from {PusherGate} failed: {issued.Message}");

        var samples = new List<SpeedSample>(BudgetSeconds);
        bool everPushed = false;
        int done = -1;
        SfoGroundHarness.TickUntil(
            ground.Engine,
            () => done > 0,
            BudgetSeconds,
            second =>
            {
                samples.Add(
                    new SpeedSample(
                        second,
                        aircraft.GroundSpeed,
                        aircraft.TrueHeading.Degrees,
                        aircraft.Phases?.CurrentPhase,
                        aircraft.Ground.PushbackTrueHeading is not null,
                        aircraft.Ground.TowbarTrueHeading is { } towbar ? towbar.AbsAngleTo(aircraft.TrueHeading) : 0.0
                    )
                );
                everPushed |= aircraft.Phases?.CurrentPhase is PushbackPhase;
                if ((done < 0) && everPushed && (aircraft.Phases?.CurrentPhase is HoldingAfterPushbackPhase))
                {
                    done = second;
                }
            }
        );

        _output.WriteLine($"{Pusher}: {command} from {PusherGate} finished t={done}s");
        _output.WriteLine(Trace(samples));
        return new PushRun(ground, aircraft, spot!, samples, done);
    }

    /// <summary>
    /// A completed <c>PUSH $spot</c> rests with the nosewheel on the marking — centroid a half fuselage
    /// back — and the nose on the spot's outbound heading, ready to taxi out.
    /// </summary>
    private static void AssertRestingOnSpot(AircraftState ac, GroundNode spot, AirportGroundLayout layout)
    {
        double halfLengthFt = AircraftLength.ResolveFt(ac.AircraftType) / 2.0;
        double distFt = GeoMath.DistanceNm(ac.Position, spot.Position) * GeoMath.FeetPerNm;
        Assert.True(
            distFt <= halfLengthFt + SpotToleranceMarginFt,
            $"{ac.Callsign} rested {distFt:F0}ft from the spot — a nose-at-spot setback is ~{halfLengthFt:F0}ft "
                + $"(tolerance +{SpotToleranceMarginFt:F0}ft)"
        );

        Assert.True(
            layout.TryGetSpotOutboundHeading(spot, out double outBearing),
            $"the layout carries no outbound heading for the spot {ac.Callsign} pushed to"
        );
        double headingErrDeg = new TrueHeading(outBearing).AbsAngleTo(ac.TrueHeading);
        Assert.True(
            headingErrDeg <= SpotHeadingToleranceDeg,
            $"{ac.Callsign} rested on heading {ac.TrueHeading.Degrees:F0}° — expected ~{outBearing:F0}° (nose out along the lane), off by {headingErrDeg:F0}°"
        );
    }

    /// <summary>The per-second speeds and noses as one line, each second tagged push or pull.</summary>
    private static string Trace(IEnumerable<SpeedSample> samples) =>
        string.Join(" ", samples.Select(s => $"{s.Second}:{s.GroundSpeedKts:F1}@{s.NoseTrueDeg:F0}{(s.Pushing ? "P" : "-")}"));
}
