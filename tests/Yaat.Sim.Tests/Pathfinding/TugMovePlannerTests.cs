using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Soak;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// The tug move planner, measured against the real SFO ground layout with a B738. Every case resolves its nodes by
/// name — ids renumber whenever the layout is regenerated.
///
/// <para>The reference case is the documented ZOA SFO technique: a D-pier stand pushes across the six alley onto
/// spot 6A, then across the lanes onto spot 6B (140.5 ft apart). A tug cannot pivot the aircraft in place, so
/// each spot is reached by pushing past it onto its lane and pulling forward onto the mark.</para>
/// </summary>
public class TugMovePlannerTests
{
    private const string Narrowbody = "B738";
    private const string Widebody = "B77W";
    private const double EndPositionToleranceFt = 1.5;
    private const double OffGraphSearchPadDeg = 0.002;
    private const double EndFacingToleranceDeg = 2.0;
    private const double MaxRunDeviationDeg = 120.0;

    private static readonly Regex RunwayPavement = new(@"would put the aircraft on runway \S+", RegexOptions.CultureInvariant);
    private static readonly Regex TaxiwayPavement = new(@"would put the aircraft on taxiway \S+", RegexOptions.CultureInvariant);

    private readonly ITestOutputHelper _output;

    public TugMovePlannerTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("TugMovePlanner", LogLevel.Debug)
            .EnableCategory("TugPathCheck", LogLevel.Debug)
            .InitializeSimLog();
    }

    [Fact]
    public void D15ToSixA_PushesOffPushesPastTheMarkThenPullsOntoIt()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var sixA = Spot(layout, "6A");
        var plan = PlanFromD15(layout, "6A");

        AssertKinds(plan, PushbackLegKind.Push, PushbackLegKind.Push, PushbackLegKind.Pull);
        Assert.True(plan.Moves[2].Move.DwellBefore, "the pull onto 6A reverses the push before it, so it has to dwell first");
        AssertEndsOnSpot(layout, plan, sixA);
    }

    [Fact]
    public void D15ToSixAThenSixB_ReversesAcrossTheLanesAndCreepsOntoSixB()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var sixB = Spot(layout, "6B");
        var plan = PlanFromD15(layout, "6A", "6B");
        LogSideTest(layout, Spot(layout, "6A"), sixB, plan.Moves[2].End);

        AssertKinds(plan, PushbackLegKind.Push, PushbackLegKind.Push, PushbackLegKind.Pull, PushbackLegKind.Push, PushbackLegKind.Pull);
        Assert.True(plan.Moves[^1].Move.Creep, "the last pull onto 6B is the creep onto the mark");
        AssertEndsOnSpot(layout, plan, sixB);
    }

    [Fact]
    public void D15ToSixA_FliesAtMostTwiceTheStraightLineDistance()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        double straightFt = FeetBetween(Parking(layout, "D15").Position, Spot(layout, "6A").Position);
        var plan = PlanFromD15(layout, "6A");

        double totalFt = plan.Moves.Sum(m => m.PathLengthFt);
        _output.WriteLine($"D15 → 6A: straight line {straightFt:F1} ft, flown {totalFt:F1} ft ({totalFt / straightFt:F2}×)");
        Assert.True(totalFt <= 2.0 * straightFt, $"flew {totalFt:F1} ft for a {straightFt:F1} ft move");
    }

    /// <summary>
    /// 6A and 6B sit abeam, so the side test for the second goal lands within a degree of its 90° boundary: the
    /// planner builds both sides' candidates and lets the ranking choose.
    /// </summary>
    [Fact]
    public void D15ToSixAThenSixB_BuildsBothSidesForTheSecondGoal()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        using var tap = new CapturingSimLogProvider(LogLevel.Debug, 1000);
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        PlanFromD15(layout, "6A", "6B");

        var legTwo = tap.Drain()
            .Where(r => r.Category == "TugMovePlanner")
            .Select(r => r.Message)
            .Where(m => m.StartsWith("Tug leg 2 to spot 6B: ", StringComparison.Ordinal))
            .ToList();
        legTwo.ForEach(_output.WriteLine);
        Assert.Contains(legTwo, m => m.EndsWith("building candidates for the Push and Pull side", StringComparison.Ordinal));
        Assert.Contains(legTwo, m => m.Contains("T1 direct, Push side", StringComparison.Ordinal));
        Assert.Contains(legTwo, m => m.Contains("T1 direct, Pull side", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("6A")]
    [InlineData("6A", "6B")]
    public void D15IntoTheSixAlley_FuselageNeverTouchesTaxiwayA(params string[] targets)
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var plan = PlanFromD15(layout, targets);
        var alpha = layout.AllEdges.Where(e => e.MatchesTaxiway("A")).ToList();
        Assert.NotEmpty(alpha);
        double halfLengthNm = (Assert.NotNull(FaaAircraftDatabase.Get(Narrowbody)?.LengthFt) / 2.0) / GeoMath.FeetPerNm;

        double closestFt = double.PositiveInfinity;
        for (int m = 0; m < plan.Moves.Count; m++)
        {
            foreach (var sample in plan.Moves[m].Samples)
            {
                var nose = GeoMath.ProjectPoint(sample.Position, new TrueHeading(sample.NoseTrueDeg), halfLengthNm);
                var tail = GeoMath.ProjectPoint(sample.Position, new TrueHeading(sample.NoseTrueDeg + 180.0), halfLengthNm);
                foreach (var edge in alpha)
                {
                    var a = edge.Nodes[0].Position;
                    var b = edge.Nodes[1].Position;
                    closestFt = Math.Min(closestFt, Math.Min(GeoMath.DistanceToSegmentFt(nose, a, b), GeoMath.DistanceToSegmentFt(tail, a, b)));
                    Assert.True(
                        GeoMath.SegmentsIntersect(nose, tail, a, b) is null,
                        $"move {m + 1} puts the fuselage across taxiway A edge '{edge.TaxiwayName}' "
                            + $"at ({sample.Position.Lat:F6}, {sample.Position.Lon:F6})"
                    );
                }
            }
        }

        _output.WriteLine($"closest nose/tail approach to a taxiway A edge: {closestFt:F1} ft");
    }

    [Fact]
    public void D15ToSixAThenD16_EndsWithAPullOntoTheStand()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var d16 = Parking(layout, "D16");
        double standDeg = Assert.NotNull(d16.TrueHeading).Degrees;
        var plan = PlanFromD15(layout, "6A", "@D16");

        double endFt = FeetBetween(plan.End.Position, d16.Position);
        double endDeg = AbsDiffDeg(plan.End.NoseTrueDeg, standDeg);
        _output.WriteLine($"D16 heading {standDeg:F1}°: ended {endFt:F2} ft from the stand, nose {endDeg:F2}° off");
        Assert.Equal(PushbackLegKind.Pull, plan.Moves[^1].Move.Kind);
        Assert.True(endFt <= EndPositionToleranceFt, $"ended {endFt:F2} ft from D16");
        Assert.True(endDeg <= EndFacingToleranceDeg, $"ended with the nose {endDeg:F2}° off D16's heading");
    }

    [Theory]
    [InlineData("6A")]
    [InlineData("6A", "6B")]
    [InlineData("6A", "@D16")]
    public void EverySameKindRunWithoutATurn_StaysWithin120DegreesOfItsStartTravel(params string[] targets)
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var plan = PlanFromD15(layout, targets);

        int runStart = 0;
        while (runStart < plan.Moves.Count)
        {
            var kind = plan.Moves[runStart].Move.Kind;
            int runEnd = runStart;
            while ((runEnd + 1 < plan.Moves.Count) && (plan.Moves[runEnd + 1].Move.Kind == kind))
            {
                runEnd++;
            }

            var run = plan.Moves.Skip(runStart).Take(runEnd - runStart + 1).ToList();
            double startTravel = run[0].Samples[0].TravelTrueDeg(kind);
            double worstDeg = run.SelectMany(m => m.Samples).Max(s => AbsDiffDeg(s.TravelTrueDeg(kind), startTravel));
            bool hasTurn = run.Any(m => m.Move.Shape == TugMoveShape.TurnTo);
            _output.WriteLine($"run of moves {runStart + 1}-{runEnd + 1} ({kind}): worst travel deviation {worstDeg:F1}°, turn: {hasTurn}");
            Assert.True(hasTurn || (worstDeg <= MaxRunDeviationDeg), $"the {kind} run of moves {runStart + 1}-{runEnd + 1} wandered {worstDeg:F1}°");
            runStart = runEnd + 1;
        }
    }

    /// <summary>
    /// <c>PUSH Y A1</c> off SFO gate B12 (the <c>SfoYankeeTaxiOutPinTests</c> case): the facing is taxiway Y's edge
    /// direction at the exit node nearest the stand, toward A1, exactly as the pushback handler derives it.
    /// </summary>
    [Fact]
    public void B12PushYankeeFacingA1_PushesOffThenLinesUpOnTheCentreline()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var stand = Parking(layout, "B12");
        var exit = layout.FindExitByTaxiway(stand.Position, "Y") ?? throw new InvalidOperationException("no taxiway Y exit near B12");
        var towardA1 = layout.FindExitByTaxiway(exit.Position, "A1") ?? throw new InvalidOperationException("no taxiway A1 exit near Y");
        double facingDeg = Assert.NotNull(layout.GetEdgeBearingForTaxiway(exit, "Y", GeoMath.BearingTo(exit.Position, towardA1.Position)));
        _output.WriteLine($"B12 heading {Assert.NotNull(stand.TrueHeading).Degrees:F1}°, facing along Y toward A1 {facingDeg:F1}°");

        var plan = PlanOrFail(layout, StandStart(stand, TugGoal.TaxiwayLine(exit, "Y", facingDeg)));

        AssertKinds(plan, PushbackLegKind.Push, PushbackLegKind.Push);
        Assert.Equal(TugMoveShape.Straight, plan.Moves[0].Move.Shape);
        var line = plan.Moves[1];
        Assert.Equal(TugMoveShape.ViaLine, line.Move.Shape);
        Assert.Null(line.Move.StopAt);
        double crossFt = Assert.NotNull(line.EndCrossTrackFt);
        double noseDeg = AbsDiffDeg(plan.End.NoseTrueDeg, facingDeg);
        Assert.True(Math.Abs(crossFt) <= 1.0, $"ended {crossFt:F2} ft off Y's centreline");
        Assert.True(noseDeg <= 1.0, $"ended with the nose {noseDeg:F2}° off the facing");
    }

    [Fact]
    public void FacingChangeOver135DegreesOffAStand_FliesTheTightRadius()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var stand = Parking(layout, "D15");
        double facingDeg = Assert.NotNull(stand.TrueHeading).Degrees + 150.0;
        double radiusFt = TugKinematics.TurnRadiusFt(Narrowbody, tight: true);

        var plan = PlanOrFail(layout, StandStart(stand, TugGoal.Facing(facingDeg)));

        var turn = Assert.Single(plan.Moves, m => m.Move.Shape == TugMoveShape.TurnTo);
        Assert.Equal(PushbackLegKind.Push, turn.Move.Kind);
        Assert.True(turn.Move.Tight, "a facing change over 135° is flown on the tight radius");
        Assert.True(turn.Samples.Count >= 5, "too few samples to fit an arc");

        var origin = turn.Samples[0].Position;
        var centre = Circumcentre(
            LocalFt(origin, turn.Samples[0].Position),
            LocalFt(origin, turn.Samples[turn.Samples.Count / 2].Position),
            LocalFt(origin, turn.Samples[^1].Position)
        );
        double worstErrorFt = turn
            .Samples.Select(s => LocalFt(origin, s.Position))
            .Max(p => Math.Abs(Math.Sqrt(Sq(p.X - centre.X) + Sq(p.Y - centre.Y)) - radiusFt));
        _output.WriteLine($"tight R={radiusFt:F2} ft, arc {turn.PathLengthFt:F1} ft, worst radius error {worstErrorFt:F3} ft");
        Assert.True(worstErrorFt <= radiusFt * 0.01, $"the turn deviates {worstErrorFt:F3} ft from R={radiusFt:F2} ft");
        Assert.True(AbsDiffDeg(plan.End.NoseTrueDeg, facingDeg) <= 1.0, $"ended with the nose on {plan.End.NoseTrueDeg:F1}°");
    }

    [Fact]
    public void Clear_IsOneStraightPushOfTheSimplePushbackDistance()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var plan = PlanOrFail(layout, StandStart(Parking(layout, "D15"), TugGoal.Clear()));

        var move = Assert.Single(plan.Moves).Move;
        Assert.Equal(PushbackLegKind.Push, move.Kind);
        Assert.Equal(TugMoveShape.Straight, move.Shape);
        Assert.Equal(CategoryPerformance.SimplePushbackDistanceNm(Narrowbody) * GeoMath.FeetPerNm, move.StraightDistanceFt, 6);
    }

    /// <summary>Gate B12 turned about: taxiway Y, normally behind the stand, is now ahead of the nose.</summary>
    [Fact]
    public void StraightBackToATaxiwayAheadOfTheNose_Refused()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var stand = Parking(layout, "B12");
        var exit = layout.FindExitByTaxiway(stand.Position, "Y") ?? throw new InvalidOperationException("no taxiway Y exit near B12");
        var start = new TugPose(stand.Position, Assert.NotNull(stand.TrueHeading).Degrees + 180.0);

        string refusal = Refusal(layout, new TugRequest(start, false, Narrowbody, [TugGoal.StraightBackTo(exit, "Y")], null));

        Assert.Equal("Unable, taxiway Y is not behind the aircraft", refusal);
    }

    /// <summary>
    /// A B77W turned about on gate B12 (nose toward taxiway Y, tail to the terminal), sent onto Y's centreline
    /// facing A1: before it can head for Y the capture has to swing the tail a full turning radius into the
    /// terminal, where the ground graph has no edges.
    /// </summary>
    [Fact]
    public void TaxiwayLineCaptureSwingingIntoTheTerminal_RefusedForLeavingTheRamp()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var stand = Parking(layout, "B12");
        var exit = layout.FindExitByTaxiway(stand.Position, "Y") ?? throw new InvalidOperationException("no taxiway Y exit near B12");
        var towardA1 = layout.FindExitByTaxiway(exit.Position, "A1") ?? throw new InvalidOperationException("no taxiway A1 exit near Y");
        double facingDeg = Assert.NotNull(layout.GetEdgeBearingForTaxiway(exit, "Y", GeoMath.BearingTo(exit.Position, towardA1.Position)));
        var start = new TugPose(stand.Position, Assert.NotNull(stand.TrueHeading).Degrees + 180.0);
        _output.WriteLine(
            $"B12 turned about, nose {start.NoseTrueDeg:F1}°; Y facing {facingDeg:F1}°; R={TugKinematics.TurnRadiusFt(Widebody, false):F1} ft"
        );

        string refusal = Refusal(layout, new TugRequest(start, false, Widebody, [TugGoal.TaxiwayLine(exit, "Y", facingDeg)], null));

        Assert.Equal("Unable, the move to taxiway Y would leave the ramp", refusal);
    }

    /// <summary>Spot 18 to spot 33 is 1,872 ft — under the sanity guard, but straight across 28L/10R and 28R/10L.</summary>
    [Fact]
    public void SpotEighteenToThirtyThree_RefusedForTheRunway()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var eighteen = Spot(layout, "18");
        var goal = TugGoal.Spot(Spot(layout, "33"));
        var start = new TugPose(eighteen.Position, GeoMath.BearingTo(eighteen.Position, goal.Node!.Position));

        string refusal = Refusal(layout, new TugRequest(start, false, Narrowbody, [goal], null));

        Assert.Contains(goal.Label, refusal, StringComparison.Ordinal);
        Assert.Matches(RunwayPavement, refusal);
    }

    /// <summary>Gate D1 to spot 34 is 1,035 ft and cuts clean across taxiway A well short of the spot.</summary>
    [Fact]
    public void D1ToSpotThirtyFour_RefusedForTheTaxiway()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var goal = TugGoal.Spot(Spot(layout, "34"));

        string refusal = Refusal(layout, StandStart(Parking(layout, "D1"), goal));

        Assert.Contains(goal.Label, refusal, StringComparison.Ordinal);
        Assert.Matches(TaxiwayPavement, refusal);
    }

    /// <summary>The runway holding-position node nearest spot 18, asked for as a node goal.</summary>
    [Fact]
    public void GoalOnARunwayHoldingPosition_Refused()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var eighteen = Spot(layout, "18");
        var hold = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort).MinBy(n => FeetBetween(n.Position, eighteen.Position));
        Assert.NotNull(hold);
        _output.WriteLine($"hold-short node {hold.Id} is {FeetBetween(hold.Position, eighteen.Position):F0} ft from spot 18");
        var goal = TugGoal.AtNode(hold, facingTrueDeg: null);
        var start = new TugPose(eighteen.Position, GeoMath.BearingTo(eighteen.Position, hold.Position));

        string refusal = Refusal(layout, new TugRequest(start, false, Narrowbody, [goal], null));

        Assert.Contains(goal.Label, refusal, StringComparison.Ordinal);
        Assert.Contains("reaches a runway holding position", refusal, StringComparison.Ordinal);
    }

    /// <summary>Gate D5 to spot 1 is 3,092 ft — a mis-click, not a tug move.</summary>
    [Fact]
    public void GoalBeyondTheSanityGuard_Refused()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var goal = TugGoal.Spot(Spot(layout, "1"));

        string refusal = Refusal(layout, StandStart(Parking(layout, "D5"), goal));

        Assert.Contains(goal.Label, refusal, StringComparison.Ordinal);
        Assert.Contains("sanity guard", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeGoalAheadOfTheNoseOffAStand_Refused()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var goal = TugGoal.AtNode(Spot(layout, "6A"), facingTrueDeg: null);

        string refusal = Refusal(layout, StandStart(Parking(layout, "D5"), goal));

        Assert.Contains(goal.Label, refusal, StringComparison.Ordinal);
        Assert.Contains("is ahead of the nose — the aircraft has to be pushed back off the stand first", refusal, StringComparison.Ordinal);
    }

    private static AirportGroundLayout? LoadSfo() => new TestAirportGroundData().GetLayout("SFO");

    /// <summary>A stand start off D15 through <paramref name="targets"/>: a spot name, or <c>@stand</c>.</summary>
    private TugPlan PlanFromD15(AirportGroundLayout layout, params string[] targets)
    {
        var goals = targets.Select(t => t.StartsWith('@') ? TugGoal.Stand(Parking(layout, t[1..])) : TugGoal.Spot(Spot(layout, t))).ToArray();
        _output.WriteLine($"D15 → {string.Join(" → ", targets)}");
        return PlanOrFail(layout, StandStart(Parking(layout, "D15"), goals));
    }

    private static TugRequest StandStart(GroundNode stand, params TugGoal[] goals) =>
        new(new TugPose(stand.Position, stand.TrueHeading!.Value.Degrees), true, Narrowbody, goals, null);

    private TugPlan PlanOrFail(AirportGroundLayout layout, TugRequest request)
    {
        var watch = Stopwatch.StartNew();
        var plan = TugMovePlanner.Plan(layout, request, out string refusal);
        double firstMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        TugMovePlanner.Plan(layout, request, out _);
        _output.WriteLine($"planned in {firstMs:F0} ms (again: {watch.Elapsed.TotalMilliseconds:F0} ms); refusal '{refusal}'");
        Assert.True(plan is not null, $"the plan was refused: {refusal}");
        Assert.Equal(string.Empty, refusal);
        LogPlan(layout, request.Start, plan);
        return plan;
    }

    private string Refusal(AirportGroundLayout layout, TugRequest request)
    {
        var plan = TugMovePlanner.Plan(layout, request, out string refusal);
        _output.WriteLine($"refusal: {refusal}");
        if (plan is not null)
        {
            LogPlan(layout, request.Start, plan);
        }

        Assert.Null(plan);
        return refusal;
    }

    private void LogPlan(AirportGroundLayout layout, TugPose start, TugPlan plan)
    {
        _output.WriteLine(
            $"start ({start.Position.Lat:F6}, {start.Position.Lon:F6}) nose {start.NoseTrueDeg:F1}°; "
                + $"total path {plan.Moves.Sum(m => m.PathLengthFt):F1} ft; largest off-graph distance {LargestOffGraphFt(layout, plan):F1} ft"
        );
        for (int i = 0; i < plan.Moves.Count; i++)
        {
            var trace = plan.Moves[i];
            var move = trace.Move;
            string flags = $"{(move.DwellBefore ? " dwell" : "")}{(move.Tight ? " tight" : "")}{(move.Creep ? " creep" : "")}";
            _output.WriteLine(
                $"move {i + 1}: {move.Kind} {move.Shape}{flags}, path {trace.PathLengthFt:F1} ft, max deviation {trace.MaxTravelDeviationDeg:F1}°, "
                    + $"end ({trace.End.Position.Lat:F6}, {trace.End.Position.Lon:F6}) nose {trace.End.NoseTrueDeg:F1}°, "
                    + $"cross {trace.EndCrossTrackFt:F2} ft, overshoot {trace.EndOvershootFt:F2} ft"
            );
        }
    }

    /// <summary>
    /// The side test for the second goal: how far off 6B's nose-out the bearing to 6B's stop point is, seen from
    /// 6A's stop point and from where the first goal actually ended. At or under 90° the approach is a pull.
    /// </summary>
    private void LogSideTest(AirportGroundLayout layout, GroundNode first, GroundNode second, TugPose firstGoalEnd)
    {
        Assert.True(layout.TryGetSpotOutboundHeading(first, out double firstDeg));
        Assert.True(layout.TryGetSpotOutboundHeading(second, out double secondDeg));
        var firstStop = TugMovePlanner.SpotStopGeometry(first, firstDeg, Narrowbody).Stop;
        var secondStop = TugMovePlanner.SpotStopGeometry(second, secondDeg, Narrowbody).Stop;
        double fromStopDeg = AbsDiffDeg(GeoMath.BearingTo(firstStop, secondStop), secondDeg);
        double fromEndDeg = AbsDiffDeg(GeoMath.BearingTo(firstGoalEnd.Position, secondStop), secondDeg);
        _output.WriteLine(
            $"side test for {second.Name} (nose-out {secondDeg:F2}°, {first.Name} nose-out {firstDeg:F2}°): "
                + $"from {first.Name}'s stop point {fromStopDeg:F2}° off, from the first goal's end "
                + $"({FeetBetween(firstGoalEnd.Position, firstStop):F2} ft from that stop point) {fromEndDeg:F2}° off"
        );
    }

    /// <summary>The farthest any sampled reference point lies from its nearest ground-graph edge, feet.</summary>
    private static double LargestOffGraphFt(AirportGroundLayout layout, TugPlan plan)
    {
        var samples = plan.Moves.SelectMany(m => m.Samples).Select(s => s.Position).ToList();
        if (samples.Count == 0)
        {
            return 0.0;
        }

        double minLat = samples.Min(p => p.Lat) - OffGraphSearchPadDeg;
        double maxLat = samples.Max(p => p.Lat) + OffGraphSearchPadDeg;
        double minLon = samples.Min(p => p.Lon) - OffGraphSearchPadDeg;
        double maxLon = samples.Max(p => p.Lon) + OffGraphSearchPadDeg;
        var edges = layout
            .AllEdges.Select(e => (A: e.Nodes[0].Position, B: e.Nodes[1].Position))
            .Where(e =>
                (Math.Max(e.A.Lat, e.B.Lat) >= minLat)
                && (Math.Min(e.A.Lat, e.B.Lat) <= maxLat)
                && (Math.Max(e.A.Lon, e.B.Lon) >= minLon)
                && (Math.Min(e.A.Lon, e.B.Lon) <= maxLon)
            )
            .ToList();
        return samples.Max(p => edges.Count == 0 ? double.PositiveInfinity : edges.Min(e => GeoMath.DistanceToSegmentFt(p, e.A, e.B)));
    }

    private static void AssertKinds(TugPlan plan, params PushbackLegKind[] expected) =>
        Assert.Equal(expected, plan.Moves.Select(m => m.Move.Kind).ToArray());

    private void AssertEndsOnSpot(AirportGroundLayout layout, TugPlan plan, GroundNode spot)
    {
        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double facingDeg), $"spot {spot.Name} has no nose-out heading");
        var stop = TugMovePlanner.SpotStopGeometry(spot, facingDeg, Narrowbody).Stop;
        double endFt = FeetBetween(plan.End.Position, stop);
        double endDeg = AbsDiffDeg(plan.End.NoseTrueDeg, facingDeg);
        _output.WriteLine($"spot {spot.Name} nose-out {facingDeg:F1}°: ended {endFt:F2} ft from the stop point, nose {endDeg:F2}° off");
        Assert.True(endFt <= EndPositionToleranceFt, $"ended {endFt:F2} ft from spot {spot.Name}'s stop point");
        Assert.True(endDeg <= EndFacingToleranceDeg, $"ended with the nose {endDeg:F2}° off spot {spot.Name}'s nose-out heading");
    }

    private static double AbsDiffDeg(double a, double b) => new TrueHeading(a).AbsAngleTo(new TrueHeading(b));

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>Flat east/north feet from <paramref name="origin"/>, the same frame <c>GeoMath.ProjectPoint</c> steps in.</summary>
    private static (double X, double Y) LocalFt(LatLon origin, LatLon point)
    {
        double y = (point.Lat - origin.Lat) * 60.0 * GeoMath.FeetPerNm;
        double x = (point.Lon - origin.Lon) * 60.0 * Math.Cos(origin.Lat * Math.PI / 180.0) * GeoMath.FeetPerNm;
        return (x, y);
    }

    private static (double X, double Y) Circumcentre((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        double d = 2.0 * ((a.X * (b.Y - c.Y)) + (b.X * (c.Y - a.Y)) + (c.X * (a.Y - b.Y)));
        double aa = Sq(a.X) + Sq(a.Y);
        double bb = Sq(b.X) + Sq(b.Y);
        double cc = Sq(c.X) + Sq(c.Y);
        double x = ((aa * (b.Y - c.Y)) + (bb * (c.Y - a.Y)) + (cc * (a.Y - b.Y))) / d;
        double y = ((aa * (c.X - b.X)) + (bb * (a.X - c.X)) + (cc * (b.X - a.X))) / d;
        return (x, y);
    }

    private static double Sq(double v) => v * v;

    private static GroundNode Spot(AirportGroundLayout layout, string name) =>
        layout.FindSpotNodeByName(name) ?? throw new InvalidOperationException($"SFO layout carries no spot named {name}");

    private static GroundNode Parking(AirportGroundLayout layout, string name) =>
        layout.FindParkingByName(name) ?? throw new InvalidOperationException($"SFO layout carries no parking named {name}");
}
