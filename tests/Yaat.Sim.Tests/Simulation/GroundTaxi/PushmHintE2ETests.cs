using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// <c>PUSHM</c>'s pass-through hints beyond the F8 reference case (<see cref="SfoF8PushHintE2ETests"/>), flown end to end
/// on the real SFO ramp: a hint no arrival passes, which the tow is taken through on a move of its own, and a hint that is
/// a graph node rather than a spot. The engine is driven rather than a phase ticked directly: <see cref="FlightPhysics"/>
/// is the only integrator of ground speed. Every node is resolved by name or from the plan; a missing SFO layout silently
/// skips.
/// </summary>
public class PushmHintE2ETests(ITestOutputHelper output)
{
    private const string AircraftType = "B738";

    /// <summary>Tick budget for a whole move off a gate, seconds.</summary>
    private const int MoveBudgetSeconds = 600;

    /// <summary>Ground speed (kt) below which the aircraft counts as stopped.</summary>
    private const double AtRestSpeedKts = 0.5;

    /// <summary>How close to the spot's rest point the move must finish, feet.</summary>
    private const double RestPositionToleranceFt = 3.0;

    /// <summary>How close to the spot's nose-out heading the move must finish, degrees.</summary>
    private const double RestNoseToleranceDeg = 1.0;

    /// <summary>How close to a hint's point the move a pass-through tow takes onto it ends, feet.</summary>
    private const double PassOntoPointFt = 3.0;

    /// <summary>How close to <c>PUSH $spot</c>'s path the graph node chosen as a hint lies, feet.</summary>
    private const double NodeOnPathFt = 30.0;

    /// <summary>How far from the gate the graph node chosen as a hint lies at least, feet, so it is not the push-off.</summary>
    private const double NodeClearOfGateFt = 100.0;

    /// <summary>
    /// Off SFO gate C9, no candidate for spot 5B passes spot 5A (<c>PUSH $5B</c>'s own plan comes no closer than half the
    /// wingspan to 5A's rest point before its last reversal), so <c>PUSHM $5A $5B</c> is accepted as a pass-through tow:
    /// a move ends on 5A's rest point with no creep onto it, the tow stands still only for a planned reversal, and it comes
    /// to rest nose-out on 5B.
    /// </summary>
    [Fact]
    public void Pushm_HintNoCandidatePasses_FallsBackToPassThroughTow()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL790", AircraftType, "C9");
        using IDisposable recording = TickRecorder.Attach(ground.Engine, RecordingPath("c9-pushm-5a-5b.json"), ac.Callsign);
        LatLon hint = SpotRest(ground.Layout, "5A").Position;
        TugPlan pushToEnd = Plan(ground.Layout, ac, [TugGoal.Spot(Spot(ground.Layout, "5B"))]);
        double pushMissFt = ClosestBeforeLastReversalFt(pushToEnd, hint);
        output.WriteLine(
            $"PUSH $5B off C9: {Describe(pushToEnd.Moves.Select(t => t.Move))}; closest to 5A before its last reversal {pushMissFt:F1} ft"
        );
        Assert.True(pushMissFt > HalfSpanFt(), $"PUSH $5B passes {pushMissFt:F1} ft from 5A, so 5A is no hint its arrival misses");

        CommandResult result = ground.Engine.SendCommand(ac.Callsign, "PUSHM $5A $5B");
        output.WriteLine($"'PUSHM $5A $5B' → success={result.Success} \"{result.Message}\"");
        Assert.True(result.Success, $"'PUSHM $5A $5B' off C9 was refused: {result.Message}");
        List<PushbackPhase> plan = [.. ac.Phases!.Phases.OfType<PushbackPhase>()];
        output.WriteLine($"PUSHM $5A $5B: {Describe(plan.Select(p => p.Move))}");
        Assert.All(plan.Take(plan.Count - 1), p => Assert.False(p.Move.Creep, $"{p.Kind} {p.Move.Shape} creeps before the final move"));

        MoveRun run = TickMove(ground, ac);

        Assert.True(run.CompletedSecond > 0, $"the move never finished within {MoveBudgetSeconds}s");
        double passFt = run.Samples.Min(s => DistanceFt(s.Position, hint));
        output.WriteLine($"the reference point passed {passFt:F1} ft from 5A's rest point");
        Assert.True(passFt <= PassOntoPointFt, $"the pass-through tow came no closer than {passFt:F1} ft to 5A's rest point");
        AssertNoStopButAtReversals(run, plan);
        AssertRestsOnSpot(ground.Layout, ac, "5B");
    }

    /// <summary>
    /// A hint that is a graph node, not a spot: off F8, the lane node nearest <c>PUSH $7B</c>'s path before its reversal.
    /// <c>PUSHM #node $7B</c> flies <c>PUSH $7B</c>'s moves — the same kinds and shapes — passing the node within half the
    /// wingspan with no stop but the one reversal, and comes to rest nose-out on 7B.
    /// </summary>
    [Fact]
    public void Pushm_NodeHintOnTheWay_PassedWithoutAStop()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL791", AircraftType, "F8");
        using IDisposable recording = TickRecorder.Attach(ground.Engine, RecordingPath("f8-pushm-node-7b.json"), ac.Callsign);
        TugPlan pushToEnd = Plan(ground.Layout, ac, [TugGoal.Spot(Spot(ground.Layout, "7B"))]);
        GroundNode node = NodeOnThePath(ground.Layout, ac, pushToEnd);

        string command = $"PUSHM #{node.Id} $7B";
        CommandResult result = ground.Engine.SendCommand(ac.Callsign, command);
        output.WriteLine($"'{command}' → success={result.Success} \"{result.Message}\"");
        Assert.True(result.Success, $"'{command}' off F8 was refused: {result.Message}");
        List<PushbackPhase> plan = [.. ac.Phases!.Phases.OfType<PushbackPhase>()];
        List<TugMove> expected = [.. pushToEnd.Moves.Select(t => t.Move)];
        output.WriteLine($"PUSH $7B: {Describe(expected)}");
        output.WriteLine($"{command}: {Describe(plan.Select(p => p.Move))}");
        Assert.Equal([.. expected.Select(m => (m.Kind, m.Shape))], [.. plan.Select(p => (p.Move.Kind, p.Move.Shape))]);
        Assert.Equal(1, plan.Count(p => p.Move.DwellBefore));

        MoveRun run = TickMove(ground, ac);

        Assert.True(run.CompletedSecond > 0, $"the move never finished within {MoveBudgetSeconds}s");
        double passFt = run.Samples.Min(s => DistanceFt(s.Position, node.Position));
        output.WriteLine($"the reference point passed {passFt:F1} ft from node {node.Id} (half span {HalfSpanFt():F1} ft)");
        Assert.True(passFt <= HalfSpanFt(), $"the reference point passed {passFt:F1} ft from node {node.Id}, beyond half the wingspan");
        AssertNoStopButAtReversals(run, plan);
        AssertRestsOnSpot(ground.Layout, ac, "7B");
    }

    /// <summary>
    /// The non-spot, non-stand, non-holding-position graph node nearest <paramref name="plan"/>'s path before its last
    /// reversal, within <see cref="NodeOnPathFt"/> of it, at least <see cref="NodeClearOfGateFt"/> from the gate and more
    /// than half the wingspan from the plan's end.
    /// </summary>
    private GroundNode NodeOnThePath(AirportGroundLayout layout, AircraftState ac, TugPlan plan)
    {
        List<LatLon> path = [.. PathBeforeLastReversal(plan)];
        (GroundNode Node, double OffFt)? nearest = layout
            .Nodes.Values.Where(n => n.Type == GroundNodeType.TaxiwayIntersection)
            .Where(n => (DistanceFt(n.Position, ac.Position) >= NodeClearOfGateFt) && (DistanceFt(n.Position, plan.End.Position) > HalfSpanFt()))
            .Select(n => (Node: n, OffFt: path.Min(p => DistanceFt(p, n.Position))))
            .Where(c => c.OffFt <= NodeOnPathFt)
            .OrderBy(c => c.OffFt)
            .Cast<(GroundNode Node, double OffFt)?>()
            .FirstOrDefault();
        Assert.True(nearest is not null, $"no graph node lies within {NodeOnPathFt:F0} ft of PUSH $7B's path off F8");
        (GroundNode node, double offFt) = nearest.Value;
        string names = string.Join("/", node.Edges.SelectMany(RampLaneReposition.EdgeNames).Distinct());
        output.WriteLine($"hint: node {node.Id} on {names}, {offFt:F1} ft off PUSH $7B's path");
        return node;
    }

    private static IEnumerable<LatLon> PathBeforeLastReversal(TugPlan plan)
    {
        int lastReversal = plan.Moves.ToList().FindLastIndex(t => t.Move.DwellBefore);
        return plan.Moves.Take(lastReversal >= 0 ? lastReversal : plan.Moves.Count).SelectMany(t => t.Samples).Select(s => s.Position);
    }

    private static double ClosestBeforeLastReversalFt(TugPlan plan, LatLon point) => PathBeforeLastReversal(plan).Min(p => DistanceFt(p, point));

    /// <summary>The planner's plan off the gate the aircraft is parked on, with no neighbours.</summary>
    private static TugPlan Plan(AirportGroundLayout layout, AircraftState ac, IReadOnlyList<TugGoal> goals)
    {
        var request = new TugRequest
        {
            Start = new TugPose(ac.Position, ac.TrueHeading.Degrees),
            StartsAtStand = true,
            AircraftType = AircraftType,
            Goals = goals,
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
        };
        return TugMovePlanner.Plan(layout, request, out string refusal) ?? throw new InvalidOperationException($"the plan was refused: {refusal}");
    }

    /// <summary>
    /// After the stand push-off and before the final move, the aircraft is below <see cref="AtRestSpeedKts"/> only in a
    /// move that reverses the one before it or in the move that stops for such a reversal.
    /// </summary>
    private static void AssertNoStopButAtReversals(MoveRun run, List<PushbackPhase> plan)
    {
        int from = run.Samples.ToList().FindLastIndex(s => ReferenceEquals(s.Phase, plan[0])) + 1;
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

    private sealed record Sample(int Second, LatLon Position, double GroundSpeedKts, PushbackPhase? Phase);

    private sealed record MoveRun(IReadOnlyList<Sample> Samples, int CompletedSecond);

    private static MoveRun TickMove(SfoGround ground, AircraftState ac)
    {
        var samples = new List<Sample> { SampleOf(0, ac) };
        int completed = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => ac.Phases?.CurrentPhase is not PushbackPhase,
            MoveBudgetSeconds,
            second => samples.Add(SampleOf(second, ac))
        );
        return new MoveRun(samples, completed);
    }

    private static Sample SampleOf(int second, AircraftState ac) =>
        new(second, ac.Position, ac.GroundSpeed, ac.Phases?.CurrentPhase as PushbackPhase);

    private void AssertRestsOnSpot(AirportGroundLayout layout, AircraftState ac, string spotName)
    {
        (LatLon position, double outHeadingDeg) = SpotRest(layout, spotName);
        double offRestFt = DistanceFt(ac.Position, position);
        double offNoseOutDeg = new TrueHeading(outHeadingDeg).AbsAngleTo(ac.TrueHeading);
        output.WriteLine($"at rest {offRestFt:F2} ft off the {spotName} rest point, nose {offNoseOutDeg:F2}° off nose-out, gs={ac.GroundSpeed:F2}kt");
        Assert.True(offRestFt <= RestPositionToleranceFt, $"the move ended {offRestFt:F2} ft from spot {spotName}'s rest point");
        Assert.True(offNoseOutDeg <= RestNoseToleranceDeg, $"the nose finished {offNoseOutDeg:F2}° off spot {spotName}'s nose-out heading");
        Assert.True(ac.GroundSpeed <= AtRestSpeedKts, $"the aircraft was still moving at {ac.GroundSpeed:F2} kt when the move ended");
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary>
    /// Where a spot arrival leaves the aircraft: the reference point a half-fuselage behind the marking along the spot's
    /// nose-out heading, recomputed from the layout rather than read off the planner.
    /// </summary>
    private static (LatLon Position, double OutHeadingDeg) SpotRest(AirportGroundLayout layout, string spotName)
    {
        GroundNode spot = Spot(layout, spotName);
        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double outBearingDeg), $"spot '{spotName}' has no outbound heading in the layout");
        double halfLengthNm = (Assert.IsType<double>(FaaAircraftDatabase.Get(AircraftType)?.LengthFt) / 2.0) / GeoMath.FeetPerNm;
        return (GeoMath.ProjectPoint(spot.Position, new TrueHeading(outBearingDeg).ToReciprocal(), halfLengthNm), outBearingDeg);
    }

    private static string Describe(IEnumerable<TugMove> moves) =>
        string.Join(", ", moves.Select(m => $"{m.Kind} {m.Shape}{(m.DwellBefore ? " dwell" : "")}{(m.Creep ? " creep" : "")}"));

    private static double HalfSpanFt() => Assert.IsType<double>(FaaAircraftDatabase.Get(AircraftType)?.WingspanFt) / 2.0;

    private static GroundNode Spot(AirportGroundLayout layout, string spotName) =>
        layout.FindSpotNodeByName(spotName) ?? throw new InvalidOperationException($"the SFO layout has no spot named '{spotName}'");

    private static string RecordingPath(string fileName) => Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "pushm", fileName);

    private static double DistanceFt(LatLon from, LatLon to) => GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;
}
