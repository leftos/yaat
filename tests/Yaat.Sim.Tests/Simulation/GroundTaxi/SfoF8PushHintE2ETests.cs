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
}
