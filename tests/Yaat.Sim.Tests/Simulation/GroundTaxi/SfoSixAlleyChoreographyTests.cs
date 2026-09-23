using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The SFO "six alley": the ramp alley between the D and E piers, carrying three lanes about 70 ft apart —
/// T6B nearest the D pier (spot 6B on it, the lead-in to the D gates), T6 down the middle, and T6A on the
/// far side (spot 6A on it, the lead-in to the E gates). 6A and 6B are the outer pair, 140.2 ft apart, and
/// the only pair of lanes that fits two B738-class aircraft abreast. Which lane an arrival uses follows from
/// its gate: a D gate is reached over T6B, an E gate over T6A.
///
/// <para>Both cases here are a D-pier push that never enters the arrival's lane: a push out of a D stand
/// backs onto T6B and either stays there (to spot 6B) or continues across to spot 6A, and neither version
/// puts the tail on the lane the arrival is cleared up. What separates them is what the arrival is allowed
/// to feel. Passing a pusher <em>parked</em> a lane away must cost it nothing at all: an E75L (101.7 ft
/// span) passing a parked B738 (117.4 ft span) needs 50.85 + 58.7 + 25 = 134.55 ft for the detector's
/// wingspan bypass, and the outer lanes give 140.2 ft — a 5.6 ft margin — so no speed cap may appear.
/// Passing one that is still <em>moving</em> costs more: the detector trails it, and on that 5.6 ft of
/// margin the trail limit caps the arrival, and may briefly reach zero while the tail settles.
/// What the arrival may not be given is a hold that outlasts the push — it is trailing a neighbour, not
/// giving way to it, so the wait is short, it is never annotated as yielding to the pusher, and it is never
/// stopped again once the push has finished.</para>
///
/// <para>The give-way case — a push whose tail does cross the arrival's lane — lives in
/// <see cref="SfoSixAlleyArrivalAheadOfPushTests"/>.</para>
/// </summary>
public class SfoSixAlleyChoreographyTests
{
    private const string Pusher = "PSH1";
    private const string Arrival = "ARR1";
    private const string PusherType = "B738";
    private const string ArrivalType = "E75L";

    /// <summary>The long push's stand: a D-pier gate, so the push starts on the near lane T6B.</summary>
    private const string PushLongPusherGate = "D15";

    /// <summary>The spot the long push ends on: 6A, on the far lane T6A, all the way across the alley.</summary>
    private const string PushLongSpot = "6A";

    /// <summary>The arrival's gate in the long-push run: a D-pier stand, so its clearance stays on T6B.</summary>
    private const string PushLongArrivalGate = "D16";

    /// <summary>
    /// The pusher's stand in the concurrent run: the same D-pier gate, so the push backs onto T6B and stops
    /// there — the tail never reaches T6A, the lane the arrival is cleared up.
    /// </summary>
    private const string TrailPusherGate = "D15";

    /// <summary>The spot the concurrent push ends on: 6B, on the near lane T6B, the other side of the alley from the arrival.</summary>
    private const string AlleySpot = "6B";

    /// <summary>
    /// The arrival's gate in the concurrent run: an E-pier stand whose lead-in hangs off T6A, a little past
    /// spot 6A, so a clearance up T6A never enters the T6B lane that spot 6B sits on. The path it flies comes
    /// no closer than about 140 ft to where the push comes to rest on 6B — against the 134.55 ft two
    /// half-spans plus the wingtip buffer ask for — where a D gate would have routed the arrival straight
    /// over the parked aircraft.
    /// </summary>
    private const string AlleyGate = "E9";
    private const double SpotToleranceMarginFt = 25.0;
    private const double SpotHeadingToleranceDeg = 15.0;

    /// <summary>
    /// How long the arrival may sit at a zero trail limit while the pusher is still reversing on the other
    /// outer lane. Trailing a mover abreast on 5.6 ft of lateral margin may cost a pause (measured with the push
    /// timing here: a trail cap down to about 5 kt and no zero-cap second), but a wait that runs past this is no
    /// longer the tail settling, it is a hold.
    /// </summary>
    private const int MaxHoldWhilePushRollsSeconds = 10;

    /// <summary>
    /// How long after the push the arrival is cleared to taxi in the concurrent run. From the 28L bar the arrival
    /// reaches the alley about 55 s after its clearance, and the D15 push onto 6B runs along the T6B lane until it
    /// creeps onto the mark at t≈114 s, so a clearance 25 s into the push brings the arrival up T6A abeam the pusher
    /// at t≈80 s while it is still rolling on the other lane (measured: trail-capped to about 5 kt, parked t=93 s).
    /// Cleared at 40 s the arrival closed only after the push had finished (t=105 s); anything from 20 to 28 s gives
    /// the trail cap.
    /// </summary>
    private const int ArrivalClearanceSeconds = 25;
    private const int PushBudgetSeconds = 200;
    private const int ArrivalBudgetSeconds = 240;
    private const int ChoreographyBudgetSeconds = 400;

    private readonly ITestOutputHelper _output;

    public SfoSixAlleyChoreographyTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// The D-pier pusher goes long — across the alley onto the far lane's spot 6A — and comes to rest nose-out
    /// on the marking. The arrival then taxis in on the near lane T6B to its D gate and is never slowed by it:
    /// no <c>SpeedLimit</c> on any tick, and never annotated as yielding to the pusher.
    /// </summary>
    [Fact]
    public void PushLong_ArrivalNeverSlowed()
    {
        Alley? setup = Setup(PushLongPusherGate, PushLongSpot, PushLongArrivalGate);
        if (setup is null)
        {
            return;
        }

        Alley alley = setup.Value;
        var guard = new DeadlockGuard(alley.Pusher, alley.Arrival);
        string pushCommand = $"PUSH ${PushLongSpot}";
        int pushDoneSec = PushToSpot(alley, guard, pushCommand, PushLongPusherGate);
        _output.WriteLine($"{pushCommand} finished at t={pushDoneSec}s");
        AssertRestingOnSpot(alley.Pusher, alley.Spot, alley.Ground.Layout);

        string clearance = $"TAXI T A T6B @{PushLongArrivalGate}";
        CommandResult taxi = alley.Ground.Engine.SendCommand(Arrival, clearance);
        Assert.True(taxi.Success, $"'{clearance}' from the 28L bar on T failed: {taxi.Message}");

        var slowdowns = new List<string>();
        int arrivedSec = SfoGroundHarness.TickUntil(
            alley.Ground.Engine,
            () => alley.Arrival.Phases?.CurrentPhase is AtParkingPhase,
            ArrivalBudgetSeconds,
            second =>
            {
                guard.Tick(second);
                RecordSlowdown(slowdowns, second, alley.Arrival);
            }
        );

        Assert.True(
            slowdowns.Count == 0,
            $"the arrival on T6B was slowed by the pusher stopped across the alley on {PushLongSpot}:{Environment.NewLine}"
                + string.Join(Environment.NewLine, slowdowns)
        );
        Assert.True(
            arrivedSec > 0,
            $"the arrival never reached {PushLongArrivalGate} within {ArrivalBudgetSeconds}s "
                + $"(phase={PhaseName(alley.Arrival)}, gs={alley.Arrival.GroundSpeed:F1}kt)"
        );

        double finalFt = DistanceFt(alley.Arrival.Position, alley.Parking.Position);
        _output.WriteLine($"arrival parked at t={arrivedSec}s, {finalFt:F0}ft from the {PushLongArrivalGate} node");
        Assert.True(
            finalFt <= TaxiCoverageRunner.ParkingArrivalToleranceFt,
            $"the arrival stopped {finalFt:F0}ft from the {PushLongArrivalGate} parking node, past the "
                + $"{TaxiCoverageRunner.ParkingArrivalToleranceFt:F0}ft arrival tolerance"
        );
    }

    /// <summary>
    /// The push goes first and the taxi clearance <see cref="ArrivalClearanceSeconds"/> later, timed so the two meet
    /// on the two outer lanes: the D-pier pusher makes its final push along T6B and creeps onto spot 6B while the
    /// arrival taxis up T6A to its gate on the E pier. Nothing crosses the
    /// arrival's lane, so what the push costs it is the trail limit — capped while the B738 is rolling
    /// abreast at ~140 ft; on that 5.6 ft of lateral margin the cap may briefly reach a stop (measured here: capped,
    /// slowed to about 5 kt, never stopped). Any such wait is bounded (<see cref="MaxHoldWhilePushRollsSeconds"/>) and it belongs to the rolling push: once
    /// the pusher has come to rest, the arrival is never capped to a stop by it again — a stopped neighbour a
    /// lane away costs nothing at all, which is <see cref="PushLong_ArrivalNeverSlowed"/>. It is never annotated
    /// as auto-yielding either: this is trailing, not giving way. Both then reach their marks, and the pair
    /// never comes inside the two half-spans plus the detector's wingtip buffer.
    /// </summary>
    [Fact]
    public void ArrivalWaitsBrieflyForPushOnOtherLane()
    {
        Alley? setup = Setup(TrailPusherGate, AlleySpot, AlleyGate);
        if (setup is null)
        {
            return;
        }

        Alley alley = setup.Value;
        SimulationEngine engine = alley.Ground.Engine;
        var guard = new DeadlockGuard(alley.Pusher, alley.Arrival);

        string pushCommand = $"PUSH ${AlleySpot}";
        CommandResult push = engine.SendCommand(Pusher, pushCommand);
        Assert.True(push.Success, $"'{pushCommand}' from {TrailPusherGate} failed: {push.Message}");
        SfoGroundHarness.TickUntil(engine, () => false, ArrivalClearanceSeconds, second => guard.Tick(second));
        Assert.True(
            alley.Pusher.Phases?.CurrentPhase is PushbackPhase,
            $"test setup: the push had stopped before the arrival's clearance at t={ArrivalClearanceSeconds}s (phase={PhaseName(alley.Pusher)})"
        );
        string clearance = $"TAXI T A T6A @{AlleyGate}";
        CommandResult taxi = engine.SendCommand(Arrival, clearance);
        Assert.True(taxi.Success, $"'{clearance}' from the 28L bar on T failed: {taxi.Message}");

        TrailRun run = RunTrail(alley, guard, ArrivalClearanceSeconds);
        double requiredFt = RequiredLateralFt(ArrivalType, PusherType);
        _output.WriteLine(
            $"push finished t={run.PusherDoneSecond}s, arrival parked t={run.ArrivalParkedSecond}s, trail-limited={run.TrailLimited}, "
                + $"longest hold while the push rolled {run.LongestHoldSeconds}s, min separation {run.MinSeparationFt:F0}ft (floor {requiredFt:F2}ft)"
        );

        Assert.True(
            run.TrailLimited,
            $"the arrival was never speed-capped while the push was running: with a B738 reversing onto {AlleySpot} one lane over, "
                + "the detector is expected to trail it, so a run with no SpeedLimit at all means this case no longer exercises the trail path"
        );
        Assert.True(
            run.LongestHoldSeconds <= MaxHoldWhilePushRollsSeconds,
            $"the arrival sat at a zero trail limit for {run.LongestHoldSeconds}s straight while the push was still rolling, past the "
                + $"{MaxHoldWhilePushRollsSeconds}s a tail settling one lane over may cost it:{Environment.NewLine}"
                + string.Join(Environment.NewLine, run.ZeroCaps)
        );
        Assert.True(
            run.ZeroCapsAfterPushDone.Count == 0,
            $"the arrival was capped to a stop after the pusher had come to rest on {AlleySpot} at t={run.PusherDoneSecond}s — a "
                + $"stopped aircraft a lane away has to cost it nothing:{Environment.NewLine}"
                + string.Join(Environment.NewLine, run.ZeroCapsAfterPushDone)
        );
        Assert.True(
            run.YieldAnnotations.Count == 0,
            $"the arrival was annotated as auto-yielding to {Pusher}, which never entered its lane:{Environment.NewLine}"
                + string.Join(Environment.NewLine, run.YieldAnnotations)
        );

        Assert.True(
            (run.PusherDoneSecond > 0) && (run.PusherDoneSecond <= PushBudgetSeconds),
            $"the pusher never completed {pushCommand} within {PushBudgetSeconds}s (finished t={run.PusherDoneSecond}s, phase={PhaseName(alley.Pusher)})"
        );
        Assert.True(
            run.ArrivalParkedSecond > 0,
            $"the arrival never reached {AlleyGate} within {ChoreographyBudgetSeconds}s (phase={PhaseName(alley.Arrival)})"
        );
        Assert.True(
            run.MinSeparationFt >= requiredFt,
            $"the two came within {run.MinSeparationFt:F0}ft of each other across the {ChoreographyBudgetSeconds}s run, inside the "
                + $"{requiredFt:F2}ft an {ArrivalType} and a {PusherType} need side by side (two half-spans plus the wingtip buffer)"
        );

        AssertRestingOnSpot(alley.Pusher, alley.Spot, alley.Ground.Layout);
        double finalFt = DistanceFt(alley.Arrival.Position, alley.Parking.Position);
        Assert.True(
            finalFt <= TaxiCoverageRunner.ParkingArrivalToleranceFt,
            $"the arrival stopped {finalFt:F0}ft from the {AlleyGate} parking node, past the {TaxiCoverageRunner.ParkingArrivalToleranceFt:F0}ft arrival tolerance"
        );
    }

    /// <summary>The two aircraft of one alley run plus the layout features they are aimed at.</summary>
    private readonly record struct Alley(SfoGround Ground, AircraftState Pusher, AircraftState Arrival, GroundNode Spot, GroundNode Parking);

    /// <summary>
    /// What the concurrent push/taxi run produced: when each aircraft came to rest, the closest approach, and
    /// how the arrival was treated while the push ran — whether it was ever capped, every second it was
    /// capped to a stop, and every second it was annotated as yielding to the pusher. The offending seconds
    /// are collected rather than asserted in the tick callback so one failure reports all of them.
    /// </summary>
    private sealed class TrailRun
    {
        /// <summary>The second the push finished and handed over to <see cref="HoldingAfterPushbackPhase"/>, or -1.</summary>
        public int PusherDoneSecond { get; set; } = -1;

        /// <summary>The second the arrival reached its gate, or -1.</summary>
        public int ArrivalParkedSecond { get; set; } = -1;

        /// <summary>The closest the two centroids ever came, in feet.</summary>
        public double MinSeparationFt { get; set; } = double.PositiveInfinity;

        /// <summary>True once the arrival has been capped to a positive speed while the pusher was reversing.</summary>
        public bool TrailLimited { get; set; }

        /// <summary>The longest unbroken run of seconds the arrival spent at a zero cap while the pusher was reversing.</summary>
        public int LongestHoldSeconds { get; set; }

        /// <summary>Seconds in the current unbroken zero-cap run, reset as soon as the cap lifts.</summary>
        public int CurrentHoldSeconds { get; set; }

        /// <summary>Every second the arrival's cap was at or below zero while the pusher was reversing.</summary>
        public List<string> ZeroCaps { get; } = [];

        /// <summary>Every second the arrival's cap was at or below zero after the push had finished, until the arrival parked.</summary>
        public List<string> ZeroCapsAfterPushDone { get; } = [];

        /// <summary>Every second the arrival was annotated as auto-yielding to the pusher.</summary>
        public List<string> YieldAnnotations { get; } = [];
    }

    /// <summary>
    /// Builds the SFO world with the pusher parked at <paramref name="pusherGate"/> and the arrival at the
    /// 28L bar on T, or null when the layout or navdata is unavailable (the harness's silent-skip
    /// convention).
    /// </summary>
    /// <param name="pusherGate">Stand the pusher starts on.</param>
    /// <param name="spotName">Spot the pusher is pushed to.</param>
    /// <param name="gateName">Gate the arrival taxis to.</param>
    /// <returns>The built alley, or null to skip.</returns>
    private Alley? Setup(string pusherGate, string spotName, string gateName)
    {
        SfoGround? built = SfoGroundHarness.Build(_output, autoCross: false);
        if (built is null)
        {
            return null;
        }

        SfoGround ground = built.Value;
        GroundNode? spot = ground.Layout.FindSpotNodeByName(spotName);
        GroundNode? parking = ground.Layout.FindParkingByName(gateName);
        Assert.True(spot is not null, $"the SFO layout has no spot named '{spotName}'");
        Assert.True(parking is not null, $"the SFO layout has no parking named '{gateName}'");

        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, PusherType, pusherGate);
        AircraftState arrival = SfoGroundHarness.SpawnAtHoldShort(ground, Arrival, ArrivalType, ("28L", "T", "B"));
        return new Alley(ground, pusher, arrival, spot!, parking!);
    }

    /// <summary>
    /// Issues the spot pushback and ticks until it has finished and handed over to
    /// <see cref="HoldingAfterPushbackPhase"/> — a ramp spot is a marking the aircraft waits on, not a stand
    /// it parks on — returning the second that happened.
    /// </summary>
    /// <param name="alley">The built alley.</param>
    /// <param name="guard">Deadlock watchdog ticked every second.</param>
    /// <param name="command">The <c>PUSH $spot</c> clearance to issue.</param>
    /// <param name="pusherGate">Stand the pusher is on, for the failure message.</param>
    /// <returns>The second the push finished and came to rest on the spot.</returns>
    private int PushToSpot(Alley alley, DeadlockGuard guard, string command, string pusherGate)
    {
        CommandResult push = alley.Ground.Engine.SendCommand(Pusher, command);
        Assert.True(push.Success, $"'{command}' from {pusherGate} failed: {push.Message}");

        bool everPushed = false;
        int pushDoneSec = SfoGroundHarness.TickUntil(
            alley.Ground.Engine,
            () => everPushed && (alley.Pusher.Phases?.CurrentPhase is HoldingAfterPushbackPhase),
            PushBudgetSeconds,
            second =>
            {
                everPushed |= alley.Pusher.Phases?.CurrentPhase is PushbackPhase;
                guard.Tick(second);
            }
        );

        Assert.True(
            pushDoneSec > 0,
            $"'{command}' never finished within {PushBudgetSeconds}s (phase={PhaseName(alley.Pusher)}, ever in PushbackPhase={everPushed})"
        );
        return pushDoneSec;
    }

    /// <summary>
    /// Ticks the concurrent push and taxi to completion, watching the arrival's speed cap and yield
    /// annotation, the closest approach, and the second each aircraft comes to rest, with a trace line every
    /// ten seconds. Seconds are counted from the push, which started <paramref name="startSecond"/> seconds before.
    /// </summary>
    private TrailRun RunTrail(Alley alley, DeadlockGuard guard, int startSecond)
    {
        var run = new TrailRun();
        bool everPushed = false;

        SfoGroundHarness.TickUntil(
            alley.Ground.Engine,
            () => (run.PusherDoneSecond > 0) && (run.ArrivalParkedSecond > 0),
            ChoreographyBudgetSeconds,
            tick =>
            {
                int second = startSecond + tick;
                guard.Tick(second);
                double separationFt = DistanceFt(alley.Pusher.Position, alley.Arrival.Position);
                run.MinSeparationFt = Math.Min(run.MinSeparationFt, separationFt);
                everPushed |= alley.Pusher.Phases?.CurrentPhase is PushbackPhase;
                if ((run.PusherDoneSecond < 0) && everPushed && (alley.Pusher.Phases?.CurrentPhase is HoldingAfterPushbackPhase))
                {
                    run.PusherDoneSecond = second;
                }

                if ((run.ArrivalParkedSecond < 0) && (alley.Arrival.Phases?.CurrentPhase is AtParkingPhase))
                {
                    run.ArrivalParkedSecond = second;
                }

                RecordCaps(run, second, alley);
                RecordYield(run, second, alley);

                if (second % 10 == 0)
                {
                    _output.WriteLine($"t={second, 3}s {Describe(alley.Pusher)} | {Describe(alley.Arrival)} | sep={separationFt:F0}ft");
                }
            }
        );

        return run;
    }

    /// <summary>
    /// Records the cap the arrival carried on one tick: a positive one while the pusher reverses is the trail
    /// limit this case is about, a zero one extends the hold the push is costing it, and a zero one once the
    /// push has finished is the thing a stopped neighbour must never do. The hold is tracked as a streak, so a
    /// pause while the tail settles and a hold that never lifts are told apart by length.
    /// </summary>
    private static void RecordCaps(TrailRun run, int second, Alley alley)
    {
        if (alley.Arrival.Ground.SpeedLimit is not { } capKts)
        {
            run.CurrentHoldSeconds = 0;
            return;
        }

        bool pushRolling = alley.Pusher.Phases?.CurrentPhase is PushbackPhase;
        bool held = capKts <= 0;
        run.TrailLimited |= (pushRolling && !held);
        if (pushRolling && held)
        {
            run.CurrentHoldSeconds++;
            run.LongestHoldSeconds = Math.Max(run.LongestHoldSeconds, run.CurrentHoldSeconds);
            run.ZeroCaps.Add(CapLine(second, alley, capKts));
        }
        else
        {
            run.CurrentHoldSeconds = 0;
        }

        if (held && (run.PusherDoneSecond > 0) && (run.ArrivalParkedSecond < 0))
        {
            run.ZeroCapsAfterPushDone.Add(CapLine(second, alley, capKts));
        }
    }

    /// <summary>One second of the arrival's cap, with the pusher's own state and the gap between them.</summary>
    private static string CapLine(int second, Alley alley, double capKts) =>
        $"  t={second}s SpeedLimit={capKts:F1}kt (gs={alley.Arrival.GroundSpeed:F1}kt, phase={PhaseName(alley.Arrival)}); "
        + $"pusher {PhaseName(alley.Pusher)} gs={alley.Pusher.GroundSpeed:F1}kt, sep={DistanceFt(alley.Pusher.Position, alley.Arrival.Position):F0}ft";

    /// <summary>Records a tick on which the detector named the pusher as the arrival's yield target.</summary>
    private static void RecordYield(TrailRun run, int second, Alley alley)
    {
        if (!string.Equals(alley.Arrival.Ground.AutoYieldTarget, Pusher, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        run.YieldAnnotations.Add(
            $"  t={second}s AutoYieldTarget={alley.Arrival.Ground.AutoYieldTarget} (gs={alley.Arrival.GroundSpeed:F1}kt, "
                + $"pusher={PhaseName(alley.Pusher)})"
        );
    }

    /// <summary>
    /// Records any tick on which the arrival was capped or annotated as yielding to the pusher, so every
    /// offending second is reported in one failure rather than the first one aborting the run.
    /// </summary>
    private static void RecordSlowdown(List<string> slowdowns, int second, AircraftState arrival)
    {
        if (arrival.Ground.SpeedLimit is { } cap)
        {
            slowdowns.Add($"  t={second}s SpeedLimit={cap:F1}kt (gs={arrival.GroundSpeed:F1}kt, phase={PhaseName(arrival)})");
        }

        if (string.Equals(arrival.Ground.AutoYieldTarget, Pusher, StringComparison.OrdinalIgnoreCase))
        {
            slowdowns.Add(
                $"  t={second}s AutoYieldTarget={arrival.Ground.AutoYieldTarget} (gs={arrival.GroundSpeed:F1}kt, phase={PhaseName(arrival)})"
            );
        }
    }

    /// <summary>
    /// The side-by-side room the pair needs to pass each other: half of each wingspan plus
    /// <see cref="GroundOutlineSweep.WingtipBufferFt"/>, the same arithmetic the detector itself applies,
    /// so the floor tracks the detector rather than a number copied out of it.
    /// </summary>
    /// <param name="moverType">ICAO type of the aircraft doing the passing.</param>
    /// <param name="obstacleType">ICAO type of the aircraft being passed.</param>
    /// <returns>The required lateral clearance in feet.</returns>
    private static double RequiredLateralFt(string moverType, string obstacleType)
    {
        double? moverSpanFt = FaaAircraftDatabase.Get(moverType)?.WingspanFt;
        double? obstacleSpanFt = FaaAircraftDatabase.Get(obstacleType)?.WingspanFt;
        Assert.True(moverSpanFt is not null, $"the FAA database carries no wingspan for {moverType}");
        Assert.True(obstacleSpanFt is not null, $"the FAA database carries no wingspan for {obstacleType}");
        return (moverSpanFt!.Value / 2) + (obstacleSpanFt!.Value / 2) + GroundOutlineSweep.WingtipBufferFt;
    }

    /// <summary>
    /// A completed <c>PUSH $spot</c> rests with the nosewheel on the marking — centroid a half fuselage
    /// back — and the nose on the spot's outbound heading, ready to taxi out.
    /// </summary>
    private static void AssertRestingOnSpot(AircraftState ac, GroundNode spot, AirportGroundLayout layout)
    {
        double halfLengthFt = (FaaAircraftDatabase.Get(ac.AircraftType)?.LengthFt ?? 110.0) / 2.0;
        double distFt = DistanceFt(ac.Position, spot.Position);
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

    private static string Describe(AircraftState ac) =>
        $"{ac.Callsign} {PhaseName(ac)} gs={ac.GroundSpeed:F1}kt lim={ac.Ground.SpeedLimit?.ToString("F1") ?? "-"} yield={ac.Ground.AutoYieldTarget ?? "-"}";

    private static double DistanceFt(LatLon from, LatLon to) => GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;

    private static string PhaseName(AircraftState ac) => ac.Phases?.CurrentPhase?.Name ?? "null";
}
