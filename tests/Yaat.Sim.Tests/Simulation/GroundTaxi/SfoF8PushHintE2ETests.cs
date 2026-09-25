using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// <c>PUSHM</c>'s intermediate targets are pass-through hints, flown end to end on the real SFO ramp off gate F8:
/// <c>PUSH $7A</c> is the reference shape — the push off the gate lines up along taxilane T7A — and
/// <c>PUSHM $7A $7B</c> must fly that same first leg, pass spot 7A without stopping, and come to rest on 7B the way a
/// <c>PUSH $7B</c> arrival does. Beside them, twelve <c>PUSH $spot</c> cases fly off F8 (nearest taxiway A) and F5
/// (deepest in the alley) to each of 7A, 7 and 7B with a CRJ7 and a B738, each coming to rest on its spot with no
/// reversal before the final creep and no overswing. The engine is driven rather than a phase ticked directly:
/// <see cref="FlightPhysics"/> is the only integrator of ground speed.
///
/// <para>Each run is sampled once per simulated second and recorded with <see cref="TickRecorder"/> into
/// <c>.tmp/pushm/</c> so LayoutInspector can play the runs back side by side. Every node is resolved by name; a
/// missing SFO layout silently skips.</para>
///
/// <para>Two more theories carry the neighbouring-stand investigation. <see cref="MeasureStandEncroachment_ReportsEveryStandTheFlownOutlineEnters"/>
/// sweeps each flown outline against every parking stand's footprint — the planner's own <see cref="GroundOutline"/> cross — and
/// reports the stands it enters, with the part of the outline that goes in deepest. <see cref="PushPastAnOccupiedStand_RefusesOrKeepsTheOutlinesApart"/>
/// then parks a B738 on each such stand and asserts the push is either refused with a reason or flown with the two
/// outlines never overlapping, recording each occupied run for playback beside its empty-stand twin.</para>
/// </summary>
public class SfoF8PushHintE2ETests(ITestOutputHelper output)
{
    private const string Gate = "F8";
    private const string HintSpot = "7A";
    private const string EndSpot = "7B";
    private const string AircraftType = "B738";

    /// <summary>Tick budget for a whole move off F8, seconds.</summary>
    private const int MoveBudgetSeconds = 450;

    /// <summary>Ground speed (kt) below which the aircraft counts as stopped.</summary>
    private const double AtRestSpeedKts = 0.5;

    /// <summary>How close to the spot's rest point the move must finish, feet.</summary>
    private const double RestPositionToleranceFt = 3.0;

    /// <summary>How close to the spot's nose-out heading the move must finish, degrees.</summary>
    private const double RestNoseToleranceDeg = 1.0;

    /// <summary>
    /// The most the nose may turn away from its heading at the end of the push-off before the aircraft reaches 7A's
    /// neighbourhood on <c>PUSHM $7A $7B</c>, degrees. The bug swung it about 180° toward another gate.
    /// </summary>
    private const double MaxSwingBeforeHintDeg = 100.0;

    /// <summary>
    /// The bound for <c>PUSH $spot</c> onto the alley's spots, degrees: off F8 every B738 plan to 7A fouls taxiway A's
    /// object-free area, and the old fallback swung the nose about 179° toward another gate before it reached 7A.
    /// </summary>
    private const double MaxPushSwingBeforeHintDeg = 130.0;

    /// <summary>How often the per-second trajectory sample is written to the test output.</summary>
    private const int TrajectoryLogInterval = 5;

    /// <summary>
    /// <c>PUSH $7A</c> off F8, the reference shape: accepted, flown to rest on 7A's rest point nose-out, with the nose
    /// never swinging more than <see cref="MaxPushSwingBeforeHintDeg"/> from where the push-off left it before the aircraft
    /// reaches 7A's neighbourhood.
    /// </summary>
    [Fact]
    public void PushToSevenA_LinesUpAlongTheLane()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL781", AircraftType, Gate);
        using IDisposable recording = TickRecorder.Attach(ground.Engine, RecordingPath("f8-push-7a.json"), ac.Callsign);
        LogGeometry(ground.Layout, ac);

        const string command = $"PUSH ${HintSpot}";
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, command);
        output.WriteLine($"'{command}' → success={result.Success} \"{result.Message}\"");
        Assert.True(result.Success, $"'{command}' off {Gate} was refused: {result.Message}");

        MoveRun run = TickMove(ground, ac);

        Assert.True(run.CompletedSecond > 0, $"the move never finished within {MoveBudgetSeconds}s (phase={PhaseName(ac)})");
        double swingDeg = SwingBeforeHintDeg(ground.Layout, run);
        output.WriteLine(
            $"PUSH ${HintSpot}: the nose swung at most {swingDeg:F1}° from the push-off's end before reaching {HintSpot}'s neighbourhood"
        );
        Assert.True(
            swingDeg <= MaxPushSwingBeforeHintDeg,
            $"PUSH ${HintSpot}'s own nose swung {swingDeg:F1}° before reaching {HintSpot} — past {MaxPushSwingBeforeHintDeg:F0}°"
        );
        AssertRestsOnSpot(ground.Layout, ac, HintSpot);
    }

    /// <summary>
    /// <c>PUSH $spot</c> to each of the three spots on the alley, 7A, 7 and 7B, with a regional jet and an airliner, off F8
    /// (nearest taxiway A) and off F5 (the F gate deepest in the alley, about 550 ft down the lane from where A crosses
    /// it, so the push runs longest before the lane): accepted, flown to rest on the spot's rest point nose-out, with no
    /// push/pull reversal before the final creep, and the nose never swinging more than
    /// <see cref="MaxPushSwingBeforeHintDeg"/> from where the push-off left it before the aircraft reaches the spot's
    /// neighbourhood.
    /// </summary>
    [Theory]
    [InlineData("F8", "CRJ7", "7A")]
    [InlineData("F8", "CRJ7", "7")]
    [InlineData("F8", "CRJ7", "7B")]
    [InlineData("F8", "B738", "7A")]
    [InlineData("F8", "B738", "7")]
    [InlineData("F8", "B738", "7B")]
    [InlineData("F5", "CRJ7", "7A")]
    [InlineData("F5", "CRJ7", "7")]
    [InlineData("F5", "CRJ7", "7B")]
    [InlineData("F5", "B738", "7A")]
    [InlineData("F5", "B738", "7")]
    [InlineData("F5", "B738", "7B")]
    public void PushToEachLaneSpot_RestsOnItWithoutOverswinging(string gate, string aircraftType, string spotName)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL783", aircraftType, gate);
        string recordingName = $"{gate.ToLowerInvariant()}-push-{spotName.ToLowerInvariant()}-{aircraftType.ToLowerInvariant()}.json";
        using IDisposable recording = TickRecorder.Attach(ground.Engine, RecordingPath(recordingName), ac.Callsign);

        string command = $"PUSH ${spotName}";
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, command);
        output.WriteLine($"{aircraftType} '{command}' off {gate} → success={result.Success} \"{result.Message}\"");
        Assert.True(result.Success, $"{aircraftType} '{command}' off {gate} was refused: {result.Message}");

        MoveRun run = TickMove(ground, ac);

        Assert.True(run.CompletedSecond > 0, $"the move never finished within {MoveBudgetSeconds}s (phase={PhaseName(ac)})");
        double swingDeg = SwingBeforeSpotDeg(ground.Layout, run, spotName, aircraftType);
        output.WriteLine(
            $"{aircraftType} {command}: the nose swung at most {swingDeg:F1}° from the push-off's end before reaching {spotName}'s neighbourhood"
        );
        Assert.True(
            swingDeg <= MaxPushSwingBeforeHintDeg,
            $"{aircraftType} {command}: the nose swung {swingDeg:F1}° before reaching {spotName} — past {MaxPushSwingBeforeHintDeg:F0}°"
        );
        AssertNoReversalBeforeFinalCreep(run);
        AssertRestsOnSpot(ground.Layout, ac, spotName, aircraftType);
        Assert.True(ac.GroundSpeed <= AtRestSpeedKts, $"the aircraft was still moving at {ac.GroundSpeed:F2} kt when the move ended");
    }

    /// <summary>Every flown move but the last continues the one before it, and the last is the creep onto the spot.</summary>
    private static void AssertNoReversalBeforeFinalCreep(MoveRun run)
    {
        List<PushbackPhase> moves = [.. run.Samples.Select(s => s.Phase).OfType<PushbackPhase>().Distinct()];
        Assert.NotEmpty(moves);
        for (int i = 0; i < moves.Count - 1; i++)
        {
            Assert.False(moves[i].Move.DwellBefore, $"move {i + 1} ({moves[i].Kind} {moves[i].Move.Shape}) reverses the one before the final creep");
        }

        Assert.True(moves[^1].Move.Creep, $"the last move ({moves[^1].Kind} {moves[^1].Move.Shape}) is no creep onto the spot");
    }

    /// <summary>
    /// <c>PUSHM $7A $7B</c> off F8: (a) the nose never turns more than <see cref="MaxSwingBeforeHintDeg"/> from its
    /// heading at the end of the push-off before the aircraft reaches 7A's neighbourhood; (b) the tow never stops
    /// between the push-off and the final approach to 7B except at a planned reversal, and plans none at 7A when
    /// <c>PUSH $7A</c> reaches 7A without one; (c) the reference point passes within half a wingspan of 7A's rest point;
    /// (d) it comes to rest on 7B as a <c>PUSH $7B</c> arrival does.
    /// </summary>
    [Fact(Skip = "PUSHM still treats 7A as a stop, not a pass-through hint; see docs/plans/MAIN.md")]
    public void PushmSevenASevenB_PassesSevenAAndComesToRestOnSevenB()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL782", AircraftType, Gate);
        using IDisposable recording = TickRecorder.Attach(ground.Engine, RecordingPath("f8-pushm-7a-7b.json"), ac.Callsign);
        LogGeometry(ground.Layout, ac);
        bool pushShapeReversesBeforeHint = PushShapeReversesBeforeHint(ground.Layout, ac);

        const string command = $"PUSHM ${HintSpot} ${EndSpot}";
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, command);
        output.WriteLine($"'{command}' → success={result.Success} \"{result.Message}\"");
        Assert.True(result.Success, $"'{command}' off {Gate} was refused: {result.Message}");
        List<PushbackPhase> plan = [.. ac.Phases!.Phases.OfType<PushbackPhase>()];

        MoveRun run = TickMove(ground, ac);

        Assert.True(run.CompletedSecond > 0, $"the move never finished within {MoveBudgetSeconds}s (phase={PhaseName(ac)})");
        double swingDeg = SwingBeforeHintDeg(ground.Layout, run);
        output.WriteLine($"PUSHM: the nose swung at most {swingDeg:F1}° from the push-off's end before reaching {HintSpot}'s neighbourhood");
        Assert.True(
            swingDeg <= MaxSwingBeforeHintDeg,
            $"the nose swung {swingDeg:F1}° from the push-off's heading before the aircraft reached {HintSpot} — past {MaxSwingBeforeHintDeg:F0}°"
        );
        AssertNoStopButAtReversals(run, plan);
        AssertNoReversalAtHint(ground.Layout, run, plan, pushShapeReversesBeforeHint);
        double passFt = ClosestPassFt(ground.Layout, run);
        output.WriteLine($"PUSHM: the reference point passed {passFt:F1} ft from {HintSpot}'s rest point (half span {HalfSpanFt():F1} ft)");
        Assert.True(passFt <= HalfSpanFt(), $"the reference point passed {passFt:F1} ft from {HintSpot}, beyond half the wingspan");
        AssertRestsOnSpot(ground.Layout, ac, EndSpot);
        Assert.True(ac.GroundSpeed <= AtRestSpeedKts, $"the aircraft was still moving at {ac.GroundSpeed:F2} kt when the move ended");
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary>
    /// Between the end of the push-off and the start of the final move onto 7B, the aircraft is below
    /// <see cref="AtRestSpeedKts"/> only in a move that reverses the one before it or in the move that stops for such a
    /// reversal.
    /// </summary>
    private static void AssertNoStopButAtReversals(MoveRun run, List<PushbackPhase> plan)
    {
        int from = PushOffEndIndex(run);
        int to = run.Samples.ToList().FindIndex(s => ReferenceEquals(s.Phase, plan[^1]));
        Assert.True(to > 0, "the final move never ran");
        for (int i = from; i < to; i++)
        {
            Sample sample = run.Samples[i];
            if ((sample.GroundSpeedKts >= AtRestSpeedKts) || (sample.Phase is not { } phase))
            {
                continue;
            }

            int index = plan.IndexOf(phase);
            bool reversing = phase.Move.DwellBefore || ((index + 1 < plan.Count) && plan[index + 1].Move.DwellBefore);
            Assert.True(
                reversing,
                $"t={sample.Second}s: the tow stood at {sample.GroundSpeedKts:F2} kt in move {index + 1} ({phase.Kind} {phase.Move.Shape}), "
                    + "which is no reversal"
            );
        }
    }

    /// <summary>
    /// When <c>PUSH $7A</c> reaches 7A's neighbourhood without reversing, no reversal of the <c>PUSHM</c> starts within
    /// half a wingspan of 7A's rest point.
    /// </summary>
    private void AssertNoReversalAtHint(AirportGroundLayout layout, MoveRun run, List<PushbackPhase> plan, bool pushShapeReversesBeforeHint)
    {
        LatLon hint = SpotRest(layout, HintSpot).Position;
        foreach (PushbackPhase reversal in plan.Where(p => p.Move.DwellBefore))
        {
            Sample? first = run.Samples.FirstOrDefault(s => ReferenceEquals(s.Phase, reversal));
            if (first is null)
            {
                continue;
            }

            double offFt = DistanceFt(first.Position, hint);
            output.WriteLine($"reversal into {reversal.Kind} {reversal.Move.Shape} at t={first.Second}s, {offFt:F1} ft from {HintSpot}");
            Assert.True(
                pushShapeReversesBeforeHint || (offFt > HalfSpanFt()),
                $"the tow reversed {offFt:F1} ft from {HintSpot}, where PUSH ${HintSpot} passes without reversing"
            );
        }
    }

    /// <summary>Whether <c>PUSH $7A</c>'s plan off the gate reverses before its path first comes within half a wingspan of 7A.</summary>
    private bool PushShapeReversesBeforeHint(AirportGroundLayout layout, AircraftState ac)
    {
        var request = new TugRequest
        {
            Start = new TugPose(ac.Position, ac.TrueHeading.Degrees),
            StartsAtStand = true,
            AircraftType = AircraftType,
            Goals = [TugGoal.Spot(Spot(layout, HintSpot))],
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
        };
        TugPlan plan =
            TugMovePlanner.Plan(layout, request, out string refusal) ?? throw new InvalidOperationException($"PUSH ${HintSpot}: {refusal}");
        LatLon hint = SpotRest(layout, HintSpot).Position;
        foreach (TugMoveTrace trace in plan.Moves)
        {
            if (trace.Move.DwellBefore)
            {
                output.WriteLine($"PUSH ${HintSpot} reverses into {trace.Move.Kind} {trace.Move.Shape} before reaching {HintSpot}");
                return true;
            }

            if (trace.Samples.Any(s => DistanceFt(s.Position, hint) <= HalfSpanFt()))
            {
                output.WriteLine($"PUSH ${HintSpot} reaches {HintSpot}'s neighbourhood without reversing");
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// The most the nose turned from its heading at the end of the push-off, over the samples from there until the
    /// reference point first comes within half a wingspan of 7A's rest point (or the end of the run), degrees.
    /// </summary>
    private static double SwingBeforeHintDeg(AirportGroundLayout layout, MoveRun run) => SwingBeforeSpotDeg(layout, run, HintSpot, AircraftType);

    /// <summary>
    /// <see cref="SwingBeforeHintDeg"/> for any spot and aircraft type: the most the nose turned from its heading at the
    /// end of the push-off until the reference point first comes within half a wingspan of the spot's rest point, degrees.
    /// </summary>
    private static double SwingBeforeSpotDeg(AirportGroundLayout layout, MoveRun run, string spotName, string aircraftType)
    {
        LatLon rest = SpotRest(layout, spotName, aircraftType).Position;
        int from = PushOffEndIndex(run);
        var previous = new TrueHeading(run.Samples[from].NoseDeg);
        double runningDeg = 0.0;
        double maxDeg = 0.0;
        for (int i = from; i < run.Samples.Count; i++)
        {
            Sample sample = run.Samples[i];
            var nose = new TrueHeading(sample.NoseDeg);
            runningDeg += previous.SignedAngleTo(nose);
            previous = nose;
            maxDeg = Math.Max(maxDeg, Math.Abs(runningDeg));
            if (DistanceFt(sample.Position, rest) <= HalfSpanFt(aircraftType))
            {
                break;
            }
        }

        return maxDeg;
    }

    /// <summary>The closest the reference point came to 7A's rest point over the run, feet.</summary>
    private static double ClosestPassFt(AirportGroundLayout layout, MoveRun run)
    {
        LatLon hint = SpotRest(layout, HintSpot).Position;
        return run.Samples.Min(s => DistanceFt(s.Position, hint));
    }

    /// <summary>The first sample after the stand push-off (the plan's first move) stopped running.</summary>
    private static int PushOffEndIndex(MoveRun run)
    {
        PushbackPhase? pushOff = run.Samples.Select(s => s.Phase).FirstOrDefault(p => p is { StartsAtStand: true });
        Assert.True(pushOff is not null, "the move had no stand push-off");
        int last = run.Samples.ToList().FindLastIndex(s => ReferenceEquals(s.Phase, pushOff));
        return Math.Min(last + 1, run.Samples.Count - 1);
    }

    private void LogGeometry(AirportGroundLayout layout, AircraftState ac)
    {
        output.WriteLine($"{Gate}: ({ac.Position.Lat:F6},{ac.Position.Lon:F6}) nose {ac.TrueHeading.Degrees:F1}°");
        foreach (string name in new[] { HintSpot, EndSpot })
        {
            GroundNode spot = Spot(layout, name);
            (LatLon rest, double outDeg) = SpotRest(layout, name);
            output.WriteLine(
                $"spot {name}: mark ({spot.Position.Lat:F6},{spot.Position.Lon:F6}), rest ({rest.Lat:F6},{rest.Lon:F6}), nose-out {outDeg:F1}°, "
                    + $"{DistanceFt(ac.Position, rest):F0} ft from {Gate} bearing {GeoMath.BearingTo(ac.Position, rest):F0}°"
            );
        }

        (LatLon hint, _) = SpotRest(layout, HintSpot);
        (LatLon end, _) = SpotRest(layout, EndSpot);
        output.WriteLine($"{HintSpot} → {EndSpot}: {DistanceFt(hint, end):F0} ft bearing {GeoMath.BearingTo(hint, end):F0}°");
    }

    /// <summary>The aircraft at the end of one simulated second.</summary>
    private sealed record Sample(int Second, LatLon Position, double NoseDeg, double GroundSpeedKts, PushbackPhase? Phase);

    /// <summary>
    /// A sample per second, starting before the first tick, and the second no move was running any more (-1 when the
    /// budget ran out).
    /// </summary>
    private sealed record MoveRun(IReadOnlyList<Sample> Samples, int CompletedSecond);

    /// <summary>Ticks the engine until no tug move is running, sampling the aircraft every second and logging the moves that ran.</summary>
    private MoveRun TickMove(SfoGround ground, AircraftState ac)
    {
        var samples = new List<Sample> { SampleOf(0, ac) };
        int completed = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => ac.Phases?.CurrentPhase is not PushbackPhase,
            MoveBudgetSeconds,
            second =>
            {
                Sample sample = SampleOf(second, ac);
                samples.Add(sample);
                if ((second % TrajectoryLogInterval) == 0)
                {
                    output.WriteLine(
                        $"  t={second, 3}s {sample.Phase?.Kind.ToString() ?? "-", -4} {sample.Phase?.Move.Shape.ToString() ?? "-", -8} "
                            + $"gs={sample.GroundSpeedKts, 5:F2}kt nose={sample.NoseDeg, 6:F1}° "
                            + $"pos=({sample.Position.Lat:F6},{sample.Position.Lon:F6})"
                    );
                }
            }
        );

        List<PushbackPhase> moves = [.. samples.Select(s => s.Phase).OfType<PushbackPhase>().Distinct()];
        for (int i = 0; i < moves.Count; i++)
        {
            TugMove move = moves[i].Move;
            int first = samples.FindIndex(s => ReferenceEquals(s.Phase, moves[i]));
            int last = samples.FindLastIndex(s => ReferenceEquals(s.Phase, moves[i]));
            output.WriteLine(
                $"move {i + 1}: {move.Kind} {move.Shape}{(move.DwellBefore ? " dwell" : "")}{(move.Creep ? " creep" : "")} t={samples[first].Second}-"
                    + $"{samples[last].Second}s"
            );
        }

        output.WriteLine($"finished t={completed}s, {moves.Count} moves, {moves.Count(m => m.Move.DwellBefore)} reversals, phase={PhaseName(ac)}");
        return new MoveRun(samples, completed);
    }

    private static Sample SampleOf(int second, AircraftState ac) =>
        new(second, ac.Position, ac.TrueHeading.Degrees, ac.GroundSpeed, ac.Phases?.CurrentPhase as PushbackPhase);

    /// <summary>Where the aircraft comes to rest: within the tolerances of the spot's rest point and nose-out heading.</summary>
    private void AssertRestsOnSpot(AirportGroundLayout layout, AircraftState ac, string spotName) =>
        AssertRestsOnSpot(layout, ac, spotName, AircraftType);

    private void AssertRestsOnSpot(AirportGroundLayout layout, AircraftState ac, string spotName, string aircraftType)
    {
        (LatLon position, double outHeadingDeg) = SpotRest(layout, spotName, aircraftType);
        double offRestFt = DistanceFt(ac.Position, position);
        double offNoseOutDeg = new TrueHeading(outHeadingDeg).AbsAngleTo(ac.TrueHeading);
        output.WriteLine($"at rest {offRestFt:F2} ft off the {spotName} rest point, nose {offNoseOutDeg:F2}° off nose-out, gs={ac.GroundSpeed:F2}kt");
        Assert.True(offRestFt <= RestPositionToleranceFt, $"the move ended {offRestFt:F2} ft from spot {spotName}'s rest point");
        Assert.True(offNoseOutDeg <= RestNoseToleranceDeg, $"the nose finished {offNoseOutDeg:F2}° off spot {spotName}'s nose-out heading");
    }

    /// <summary>
    /// Where a spot arrival leaves the aircraft: the reference point a half-fuselage behind the marking along the spot's
    /// nose-out heading, recomputed from the layout rather than read off the planner.
    /// </summary>
    private static (LatLon Position, double OutHeadingDeg) SpotRest(AirportGroundLayout layout, string spotName) =>
        SpotRest(layout, spotName, AircraftType);

    private static (LatLon Position, double OutHeadingDeg) SpotRest(AirportGroundLayout layout, string spotName, string aircraftType)
    {
        GroundNode spot = Spot(layout, spotName);
        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double outBearingDeg), $"spot '{spotName}' has no outbound heading in the layout");
        double halfLengthNm = (Assert.IsType<double>(FaaAircraftDatabase.Get(aircraftType)?.LengthFt) / 2.0) / GeoMath.FeetPerNm;
        return (GeoMath.ProjectPoint(spot.Position, new TrueHeading(outBearingDeg).ToReciprocal(), halfLengthNm), outBearingDeg);
    }

    private static double HalfSpanFt() => HalfSpanFt(AircraftType);

    private static double HalfSpanFt(string aircraftType) => Assert.IsType<double>(FaaAircraftDatabase.Get(aircraftType)?.WingspanFt) / 2.0;

    private static GroundNode Spot(AirportGroundLayout layout, string spotName) =>
        layout.FindSpotNodeByName(spotName) ?? throw new InvalidOperationException($"the SFO layout has no spot named '{spotName}'");

    private static string RecordingPath(string fileName) => Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "pushm", fileName);

    private static double DistanceFt(LatLon from, LatLon to) => GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;

    private static string PhaseName(AircraftState ac) => ac.Phases?.CurrentPhase?.Name ?? "null";

    // ─── Stand encroachment measurement ───
    //
    // The footprint model is the planner's own: `GroundOutline`, the cross the neighbour sweep measures (docs/ground/pushback.md,
    // "Parked neighbours"). A stand's footprint is that same outline for a parked aircraft of the measurement's type on the
    // stand's own heading, and two outlines overlap when `GroundOutline.Clearance` is zero. The flown path is the per-second
    // samples of the run, interpolated in position and nose heading at `FlownSubSamplesPerSecond` times that rate; at the 5 kt
    // tow speed one second is about 8 ft, so an interpolated pose sits well inside a foot of the flown one.

    /// <summary>Interpolated poses per recorded second when sweeping the flown outline.</summary>
    private const int FlownSubSamplesPerSecond = 10;

    /// <summary>Points sampled along each of an outline's three segments.</summary>
    private const int OutlineSamplesPerSegment = 20;

    /// <summary>How far outside a stand's outline the flown path may stay and still be measured against it, feet.</summary>
    private const double StandMeasureWindowFt = 300.0;

    /// <summary>Callsign of the aircraft being pushed in the occupied-stand cases.</summary>
    private const string PusherCallsign = "UAL783";

    /// <summary>Seconds ticked for a refused occupied-stand case, so its recording shows the two aircraft as they stand.</summary>
    private const int RefusedSnapshotSeconds = 10;

    /// <summary>Callsign and type of the aircraft parked on the neighbouring stand in the occupied-stand cases.</summary>
    private const string ParkedCallsign = "SKW3398";
    private const string ParkedType = "B738";

    /// <summary>Points sampled along each outline segment when measuring its reach toward a taxiway centreline.</summary>
    private const int TaxiwaySamplesPerSegment = 6;

    /// <summary>How far outside the flown path's extent a taxiway edge is dropped before the reach is measured, feet.</summary>
    private const double TaxiwayEdgeWindowFt = 400.0;

    /// <summary>One flown pose, with whether the aircraft is being pulled (so the tug leads its nose).</summary>
    private sealed record FlownPose(LatLon Position, double NoseDeg, bool TowedNoseFirst);

    /// <summary>
    /// One parked footprint measured against a flown path: how close the outlines came, and — where they overlapped —
    /// the part of the flown outline that reached deepest and by how much (feet, into the footprint's hull).
    /// </summary>
    private sealed record StandSweepResult(string StandName, double ClosestFt, string? Part, double DepthFt, int Second);

    /// <summary>
    /// For every <c>PUSH $spot</c> case, every parking stand other than the gate pushed from whose footprint the flown
    /// outline enters — the part that goes in and how deep. Report only, one <c>ENCROACH</c> line per stand.
    /// </summary>
    [Theory]
    [InlineData("F8", "CRJ7", "7A")]
    [InlineData("F8", "CRJ7", "7")]
    [InlineData("F8", "CRJ7", "7B")]
    [InlineData("F8", "B738", "7A")]
    [InlineData("F8", "B738", "7")]
    [InlineData("F8", "B738", "7B")]
    [InlineData("F5", "CRJ7", "7A")]
    [InlineData("F5", "CRJ7", "7")]
    [InlineData("F5", "CRJ7", "7B")]
    [InlineData("F5", "B738", "7A")]
    [InlineData("F5", "B738", "7")]
    [InlineData("F5", "B738", "7B")]
    public void MeasureStandEncroachment_ReportsEveryStandTheFlownOutlineEnters(string gate, string aircraftType, string spotName)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL783", aircraftType, gate);
        string command = $"PUSH ${spotName}";
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, command);
        Assert.True(result.Success, $"{aircraftType} '{command}' off {gate} was refused: {result.Message}");

        MoveRun run = TickMove(ground, ac);
        Assert.True(run.CompletedSecond > 0, $"the move never finished within {MoveBudgetSeconds}s (phase={PhaseName(ac)})");

        List<StandSweepResult> sweeps = SweepStands(ground.Layout, run, aircraftType, aircraftType, gate);
        List<StandSweepResult> entered = [.. sweeps.Where(s => s.DepthFt > 0.0).OrderByDescending(s => s.DepthFt)];
        if (entered.Count == 0)
        {
            StandSweepResult nearest = sweeps.MinBy(s => s.ClosestFt)!;
            output.WriteLine(
                $"ENCROACH {aircraftType} {gate}->{spotName}: none; nearest stand {nearest.StandName} {nearest.ClosestFt:F1} ft clear "
                    + $"of the outline"
            );
            return;
        }

        foreach (StandSweepResult sweep in entered)
        {
            output.WriteLine(
                $"ENCROACH {aircraftType} {gate}->{spotName}: stand {sweep.StandName} {sweep.Part} {sweep.DepthFt:F1} ft deep at "
                    + $"t={sweep.Second}s (closest outline clearance {sweep.ClosestFt:F1} ft)"
            );
        }
    }

    /// <summary>
    /// Step 2: with a B738 parked on the stand the empty-stand push swings its outline through, the same
    /// <c>PUSH $spot</c> must either be refused with a reason or fly with the pushing aircraft's outline never
    /// overlapping the parked aircraft's. The empty-stand run of the same case is flown and recorded beside it, and both
    /// plans are reported (swing, clearance to taxiway A, moves, duration).
    /// </summary>
    [Theory]
    [InlineData("F5", "CRJ7", "7A", "F6")]
    [InlineData("F5", "B738", "7A", "F6")]
    [InlineData("F5", "CRJ7", "7A", "E1")]
    [InlineData("F5", "B738", "7A", "E1")]
    [InlineData("F5", "CRJ7", "7", "F6")]
    [InlineData("F5", "B738", "7", "F6")]
    [InlineData("F5", "CRJ7", "7B", "F6")]
    [InlineData("F5", "B738", "7B", "F6")]
    public void PushPastAnOccupiedStand_RefusesOrKeepsTheOutlinesApart(string gate, string pusherType, string spotName, string standName)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } empty)
        {
            return;
        }

        if (SfoGroundHarness.Build(output, autoCross: false) is not { } occupied)
        {
            return;
        }

        string command = $"PUSH ${spotName}";
        AircraftState emptyPusher = SfoGroundHarness.SpawnParked(empty, PusherCallsign, pusherType, gate);
        using IDisposable emptyRecording = TickRecorder.Attach(
            empty.Engine,
            RecordingPath($"EMPTY-{gate}-{pusherType}-{spotName}.json"),
            emptyPusher.Callsign
        );
        CommandResult emptyResult = empty.Engine.SendCommand(emptyPusher.Callsign, command);
        Assert.True(emptyResult.Success, $"{pusherType} '{command}' off {gate} with {standName} empty was refused: {emptyResult.Message}");
        MoveRun emptyRun = TickMove(empty, emptyPusher);

        AircraftState parked = SfoGroundHarness.SpawnParked(occupied, ParkedCallsign, ParkedType, standName);
        AircraftState pusher = SfoGroundHarness.SpawnParked(occupied, PusherCallsign, pusherType, gate);
        using IDisposable occupiedRecording = TickRecorder.Attach(
            occupied.Engine,
            RecordingPath($"OCCUPIED-{gate}-{pusherType}-{spotName}-{standName}.json"),
            pusher.Callsign,
            parked.Callsign
        );
        CommandResult result = occupied.Engine.SendCommand(pusher.Callsign, command);
        output.WriteLine(
            $"{pusherType} '{command}' off {gate} with {ParkedType} {parked.Callsign} on {standName}: success={result.Success} \"{result.Message}\""
        );
        if (!result.Success)
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Message), "the push was refused without a reason");
            output.WriteLine($"REFUSED {pusherType} {gate}->{spotName} with {standName} occupied: \"{result.Message}\"");
            for (int second = 1; second <= RefusedSnapshotSeconds; second++)
            {
                occupied.Engine.TickOneSecond();
            }

            return;
        }

        MoveRun run = TickMove(occupied, pusher);
        double emptySwingDeg = SwingBeforeSpotDeg(empty.Layout, emptyRun, spotName, pusherType);
        double swingDeg = SwingBeforeSpotDeg(occupied.Layout, run, spotName, pusherType);
        double emptyClearanceFt = ClosestOutlineToTaxiwayFt(empty.Layout, emptyRun, pusherType, "A");
        double clearanceFt = ClosestOutlineToTaxiwayFt(occupied.Layout, run, pusherType, "A");
        output.WriteLine(
            $"COMPARE {pusherType} {gate}->{spotName} with {standName} occupied: swing {emptySwingDeg:F1}°→{swingDeg:F1}°, outline-to-A "
                + $"{emptyClearanceFt:F1}→{clearanceFt:F1} ft, empty plan {MoveShapeSummary(emptyRun)}, occupied plan {MoveShapeSummary(run)}"
        );

        StandSweepResult? overlap = SweepRun(run, pusherType, parked.Position, parked.TrueHeading.Degrees, ParkedType, parked.Callsign);
        Assert.True(
            (overlap is null) || (overlap.DepthFt <= 0.0),
            $"{pusherType} '{command}' off {gate} flew its outline {overlap?.DepthFt:F1} ft into {parked.Callsign} on {standName} "
                + $"({overlap?.Part}) at t={overlap?.Second}s"
        );
    }

    /// <summary>Every parking stand on the layout other than <paramref name="startGate"/>, measured against the flown outline.</summary>
    private static List<StandSweepResult> SweepStands(AirportGroundLayout layout, MoveRun run, string moverType, string standType, string startGate)
    {
        List<FlownPose> path = FlownPath(run);
        var frame = new GroundOutlineFrame(path[0].Position);
        var results = new List<StandSweepResult>();
        foreach (GroundNode node in layout.Nodes.Values)
        {
            if ((node.Type != GroundNodeType.Parking) || string.Equals(node.Name, startGate, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            StandSweepResult? sweep = SweepPath(
                path,
                frame,
                moverType,
                node.Position,
                (node.TrueHeading ?? new TrueHeading(0)).Degrees,
                standType,
                node.Name ?? $"#{node.Id}"
            );
            if (sweep is not null)
            {
                results.Add(sweep);
            }
        }

        return results;
    }

    /// <summary>
    /// The flown path the pusher's outline is swept along: the run's per-second samples with their positions and nose
    /// headings interpolated between them.
    /// </summary>
    private static List<FlownPose> FlownPath(MoveRun run)
    {
        var path = new List<FlownPose>();
        for (int i = 0; i < run.Samples.Count; i++)
        {
            Sample sample = run.Samples[i];
            bool towed = sample.Phase?.Kind == PushbackLegKind.Pull;
            path.Add(new FlownPose(sample.Position, sample.NoseDeg, towed));
            if (i + 1 >= run.Samples.Count)
            {
                continue;
            }

            Sample next = run.Samples[i + 1];
            double turnDeg = new TrueHeading(sample.NoseDeg).SignedAngleTo(new TrueHeading(next.NoseDeg));
            for (int step = 1; step < FlownSubSamplesPerSecond; step++)
            {
                double fraction = (double)step / FlownSubSamplesPerSecond;
                var position = new LatLon(
                    sample.Position.Lat + (fraction * (next.Position.Lat - sample.Position.Lat)),
                    sample.Position.Lon + (fraction * (next.Position.Lon - sample.Position.Lon))
                );
                path.Add(new FlownPose(position, new TrueHeading(sample.NoseDeg + (fraction * turnDeg)).Degrees, towed));
            }
        }

        return path;
    }

    /// <summary>
    /// Sweeps one mover outline along a flown path against one parked outline, the mover carrying the tug's lead on
    /// pull sub-samples as the planner's sweep does. Null when no pose comes within <see cref="StandMeasureWindowFt"/>
    /// of the obstacle, where the outlines cannot meet.
    /// </summary>
    private static StandSweepResult? SweepPath(
        IReadOnlyList<FlownPose> path,
        GroundOutlineFrame frame,
        string moverType,
        LatLon obstaclePosition,
        double obstacleNoseDeg,
        string obstacleType,
        string obstacleLabel
    )
    {
        var obstacleSize = GroundOutlineSize.Of(obstacleType, towedNoseFirst: false);
        OutlinePoint obstacleCentre = frame.ToLocal(obstaclePosition);
        var obstacle = GroundOutline.At(obstacleCentre, obstacleNoseDeg, obstacleSize);
        OutlinePoint[] hull = FootprintHull(obstacle);
        var moverPushing = GroundOutlineSize.Of(moverType, towedNoseFirst: false);
        var moverPulling = GroundOutlineSize.Of(moverType, towedNoseFirst: true);
        double windowFt = StandMeasureWindowFt + moverPulling.ReachFt + obstacleSize.ReachFt;
        double closestFt = double.MaxValue;
        double depthFt = 0.0;
        string? part = null;
        int second = 0;
        bool inWindow = false;
        for (int index = 0; index < path.Count; index++)
        {
            FlownPose pose = path[index];
            OutlinePoint reference = frame.ToLocal(pose.Position);
            if (OutlinePoint.Distance(reference, obstacleCentre) > windowFt)
            {
                continue;
            }

            inWindow = true;
            var mover = GroundOutline.At(reference, pose.NoseDeg, pose.TowedNoseFirst ? moverPulling : moverPushing);
            closestFt = Math.Min(closestFt, GroundOutline.Clearance(mover, obstacle));
            if (closestFt > 0.0)
            {
                continue;
            }

            (string Part, double Depth) deepest = DeepestPart(mover, reference, pose.NoseDeg, hull);
            if (deepest.Depth > depthFt)
            {
                depthFt = deepest.Depth;
                part = deepest.Part;
                second = index / FlownSubSamplesPerSecond;
            }
        }

        return inWindow ? new StandSweepResult(obstacleLabel, closestFt, part, depthFt, second) : null;
    }

    /// <summary>
    /// The part of a moved outline that reaches deepest inside a parked footprint's hull, and how deep, feet. Each
    /// segment is sampled finely enough that a chord across the hull cannot slip between two samples.
    /// </summary>
    private static (string Part, double Depth) DeepestPart(GroundOutline mover, OutlinePoint reference, double noseDeg, OutlinePoint[] hull)
    {
        (OutlineSegment Segment, string Name)[] segments = [(mover.Fuselage, "fuselage"), (mover.Wing, "wing"), (mover.Tailplane, "tail")];
        double bestFt = 0.0;
        string bestPart = "none";
        foreach ((OutlineSegment segment, string name) in segments)
        {
            for (int step = 0; step <= OutlineSamplesPerSegment; step++)
            {
                OutlinePoint point = AlongSegment(segment, (double)step / OutlineSamplesPerSegment);
                double depth = HullDepthFt(point, hull);
                if (depth > bestFt)
                {
                    bestFt = depth;
                    bestPart = name == "fuselage" ? FuselageEnd(point, reference, noseDeg) : name;
                }
            }
        }

        return (bestPart, bestFt);
    }

    /// <summary>The point <paramref name="t"/> of the way along a segment, feet from the frame origin.</summary>
    private static OutlinePoint AlongSegment(OutlineSegment segment, double t) =>
        new(segment.A.EastFt + (t * (segment.B.EastFt - segment.A.EastFt)), segment.A.NorthFt + (t * (segment.B.NorthFt - segment.A.NorthFt)));

    /// <summary>Which end of the fuselage a point sits on: the nose ahead of the reference point, else the tail.</summary>
    private static string FuselageEnd(OutlinePoint point, OutlinePoint reference, double noseDeg)
    {
        double noseRad = noseDeg * Math.PI / 180.0;
        double forwardFt = ((point.EastFt - reference.EastFt) * Math.Sin(noseRad)) + ((point.NorthFt - reference.NorthFt) * Math.Cos(noseRad));
        return forwardFt > 0.0 ? "nose" : "tail";
    }

    /// <summary>
    /// How far inside a convex footprint hull a point lies, feet; zero when the point is on or outside the boundary.
    /// Every edge is a supporting half-plane, so the inward distance is the least signed distance to one.
    /// </summary>
    private static double HullDepthFt(OutlinePoint point, IReadOnlyList<OutlinePoint> hull)
    {
        double leastLeftFt = double.MaxValue;
        double leastRightFt = double.MaxValue;
        for (int i = 0; i < hull.Count; i++)
        {
            OutlinePoint a = hull[i];
            OutlinePoint b = hull[(i + 1) % hull.Count];
            double edgeFt = OutlinePoint.Distance(a, b);
            if (edgeFt <= 0.0)
            {
                continue;
            }

            double crossFt = Turn(a, b, point) / edgeFt;
            leastLeftFt = Math.Min(leastLeftFt, crossFt);
            leastRightFt = Math.Min(leastRightFt, -crossFt);
        }

        return Math.Max(0.0, Math.Max(leastLeftFt, leastRightFt));
    }

    /// <summary>
    /// The convex hull of an outline's six endpoints — the closed footprint region the overlap depth is measured into.
    /// The outline itself is three zero-width segments; the hull is the region those segments bound.
    /// </summary>
    private static OutlinePoint[] FootprintHull(GroundOutline outline)
    {
        List<OutlinePoint> sorted =
        [
            outline.Fuselage.A,
            outline.Fuselage.B,
            outline.Wing.A,
            outline.Wing.B,
            outline.Tailplane.A,
            outline.Tailplane.B,
        ];
        sorted.Sort((a, b) => (a.EastFt != b.EastFt) ? a.EastFt.CompareTo(b.EastFt) : a.NorthFt.CompareTo(b.NorthFt));
        var hull = new List<OutlinePoint>();
        foreach (OutlinePoint point in sorted)
        {
            while ((hull.Count >= 2) && (Turn(hull[^2], hull[^1], point) <= 0.0))
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(point);
        }

        int lowerCount = hull.Count + 1;
        for (int i = sorted.Count - 2; i >= 0; i--)
        {
            OutlinePoint point = sorted[i];
            while ((hull.Count >= lowerCount) && (Turn(hull[^2], hull[^1], point) <= 0.0))
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(point);
        }

        hull.RemoveAt(hull.Count - 1);
        return [.. hull];
    }

    /// <summary>Twice the signed area of the triangle a-b-c; positive when c lies left of a→b.</summary>
    private static double Turn(OutlinePoint a, OutlinePoint b, OutlinePoint c) =>
        ((b.EastFt - a.EastFt) * (c.NorthFt - a.NorthFt)) - ((b.NorthFt - a.NorthFt) * (c.EastFt - a.EastFt));

    /// <summary><see cref="SweepRun"/> for one parked aircraft, its outline swept against the flown path.</summary>
    private static StandSweepResult? SweepRun(
        MoveRun run,
        string moverType,
        LatLon obstaclePosition,
        double obstacleNoseDeg,
        string obstacleType,
        string obstacleLabel
    )
    {
        List<FlownPose> path = FlownPath(run);
        return SweepPath(path, new GroundOutlineFrame(path[0].Position), moverType, obstaclePosition, obstacleNoseDeg, obstacleType, obstacleLabel);
    }

    /// <summary>The moves the run flew, in shape and kind, with the reversal count and the duration.</summary>
    private static string MoveShapeSummary(MoveRun run)
    {
        List<PushbackPhase> moves = [.. run.Samples.Select(s => s.Phase).OfType<PushbackPhase>().Distinct()];
        string shapes = string.Join(
            "+",
            moves.Select(m => $"{m.Kind} {m.Move.Shape}{(m.Move.Creep ? "/creep" : "")}{(m.Move.DwellBefore ? "/dwell" : "")}")
        );
        string duration = run.CompletedSecond > 0 ? $"{run.CompletedSecond}s" : $"unfinished in {MoveBudgetSeconds}s";
        return $"{moves.Count} moves [{shapes}] {moves.Count(m => m.Move.DwellBefore)} reversals {duration}";
    }

    /// <summary>
    /// The closest the flown outline of <paramref name="moverType"/> came to any straight centreline edge of
    /// <paramref name="taxiway"/>, feet. The planner's object-free clearance measures against the same straight
    /// centrelines; this reads the flown path where that one reads the plan, so the two are comparable.
    /// </summary>
    private static double ClosestOutlineToTaxiwayFt(AirportGroundLayout layout, MoveRun run, string moverType, string taxiway)
    {
        List<FlownPose> path = FlownPath(run);
        var frame = new GroundOutlineFrame(path[0].Position);
        double minEastFt = double.MaxValue;
        double maxEastFt = double.MinValue;
        double minNorthFt = double.MaxValue;
        double maxNorthFt = double.MinValue;
        foreach (FlownPose pose in path)
        {
            OutlinePoint reference = frame.ToLocal(pose.Position);
            minEastFt = Math.Min(minEastFt, reference.EastFt);
            maxEastFt = Math.Max(maxEastFt, reference.EastFt);
            minNorthFt = Math.Min(minNorthFt, reference.NorthFt);
            maxNorthFt = Math.Max(maxNorthFt, reference.NorthFt);
        }

        List<(OutlinePoint A, OutlinePoint B)> edges = [];
        foreach (GroundEdge edge in layout.Edges)
        {
            if (!edge.MatchesTaxiway(taxiway))
            {
                continue;
            }

            OutlinePoint a = frame.ToLocal(edge.Nodes[0].Position);
            OutlinePoint b = frame.ToLocal(edge.Nodes[1].Position);
            bool near =
                (Math.Min(a.EastFt, b.EastFt) <= maxEastFt + TaxiwayEdgeWindowFt)
                && (Math.Max(a.EastFt, b.EastFt) >= minEastFt - TaxiwayEdgeWindowFt)
                && (Math.Min(a.NorthFt, b.NorthFt) <= maxNorthFt + TaxiwayEdgeWindowFt)
                && (Math.Max(a.NorthFt, b.NorthFt) >= minNorthFt - TaxiwayEdgeWindowFt);
            if (near)
            {
                edges.Add((a, b));
            }
        }

        if (edges.Count == 0)
        {
            return double.PositiveInfinity;
        }

        double closestFt = double.MaxValue;
        foreach (FlownPose pose in path)
        {
            var outline = GroundOutline.At(frame.ToLocal(pose.Position), pose.NoseDeg, GroundOutlineSize.Of(moverType, pose.TowedNoseFirst));
            foreach (OutlineSegment segment in new[] { outline.Fuselage, outline.Wing, outline.Tailplane })
            {
                for (int step = 0; step <= TaxiwaySamplesPerSegment; step++)
                {
                    OutlinePoint point = AlongSegment(segment, (double)step / TaxiwaySamplesPerSegment);
                    foreach ((OutlinePoint a, OutlinePoint b) in edges)
                    {
                        closestFt = Math.Min(closestFt, PointToSegmentFt(point, a, b));
                    }
                }
            }
        }

        return closestFt;
    }

    /// <summary>The distance from a point to a segment, in the frame's flat feet.</summary>
    private static double PointToSegmentFt(OutlinePoint point, OutlinePoint a, OutlinePoint b)
    {
        double alongEastFt = b.EastFt - a.EastFt;
        double alongNorthFt = b.NorthFt - a.NorthFt;
        double lengthSqFt = (alongEastFt * alongEastFt) + (alongNorthFt * alongNorthFt);
        double t =
            lengthSqFt <= 0.0
                ? 0.0
                : Math.Clamp((((point.EastFt - a.EastFt) * alongEastFt) + ((point.NorthFt - a.NorthFt) * alongNorthFt)) / lengthSqFt, 0.0, 1.0);
        return OutlinePoint.Distance(point, new OutlinePoint(a.EastFt + (t * alongEastFt), a.NorthFt + (t * alongNorthFt)));
    }
}
