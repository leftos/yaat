using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Soak;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

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
    private const double EndPositionToleranceFt = 1.5;
    private const double OffGraphSearchPadDeg = 0.002;
    private const double EndFacingToleranceDeg = 2.0;
    private const double LegHandoverToleranceFt = 3.0;
    private const double AlongTaxiwayProbeFt = 1000.0;

    /// <summary>How many log records a test's tap keeps: enough that a planning run's early lines are never evicted.</summary>
    private const int TapCapacity = 200_000;

    /// <summary>The planner's same-kind wander limit, degrees: a push off a stand may pivot onto a spot's lane in one capture.</summary>
    private const double MaxSameKindWanderDeg = 150.0;

    /// <summary>How far past an edge's end a point may project and still count as on that edge, feet.</summary>
    private const double ExtentSlackFt = 1.0;

    /// <summary>How far off an edge's line a point may lie and still be on that taxiway's pavement, feet.</summary>
    private const double CorridorFt = TugPlanBuilder.OnTaxiwayCorridorFt;

    /// <summary>
    /// The most a bare <c>PUSH</c> onto a taxiway alongside may run, feet, when the capture of its centreline already
    /// lands in the taxiway's corridor: the stand push-off plus the S-curve, with room to spare. Measured at OAK
    /// gate 32, where the capture takes 372 ft and carrying on to the edge's nearest point took 430 ft.
    /// </summary>
    private const double OnceOnTheTaxiwayPathFt = 400.0;

    /// <summary>How far a pinned move's path length may drift and still be the same move, feet.</summary>
    private const double PinnedPathToleranceFt = 0.5;

    /// <summary>
    /// The most the nose may turn from the push-off's heading on F8 → 7A before the aircraft nears 7A's stop point, degrees
    /// (<see cref="SwingBeforeStopDeg"/>); the old plan swung 179.8°.
    /// </summary>
    private const double F8MaxSwingDeg = 100.0;

    /// <summary>
    /// The deepest F8 → 7A may reach into a taxiway's object-free area, feet: the least any candidate reaches (19.4 ft into
    /// A's object-free area plus the 5 ft planning margin, 14.4 ft into the area itself; the push-off alone puts the tail
    /// 8.5 ft in), with half a foot to spare.
    /// </summary>
    private const double F8MaxPenetrationFt = 19.9;

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
    public void D15ToSixA_PushesStraightThenOntoTheLineThenPullsOntoIt()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode sixA = Spot(layout, "6A");
        TugPlan plan = PlanFromD15(layout, "6A");

        AssertMoves(
            plan,
            (PushbackLegKind.Push, TugMoveShape.Straight),
            (PushbackLegKind.Push, TugMoveShape.Straight),
            (PushbackLegKind.Push, TugMoveShape.ViaLine),
            (PushbackLegKind.Pull, TugMoveShape.ViaLine)
        );
        Assert.True(plan.Moves[3].Move.DwellBefore, "the pull onto 6A reverses the push before it, so it has to dwell first");
        Assert.True(plan.Moves[3].Move.Creep, "the pull onto 6A is the creep onto the mark");
        AssertEndsOnSpot(layout, plan, sixA);
    }

    [Fact]
    public void D15ToSixAThenSixB_ReversesAcrossTheLanesAndCreepsOntoSixB()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode sixB = Spot(layout, "6B");
        TugPlan plan = PlanFromD15(layout, "6A", "6B");
        LogSideTest(layout, Spot(layout, "6A"), sixB, plan.Moves[3].End);

        AssertMoves(
            plan,
            (PushbackLegKind.Push, TugMoveShape.Straight),
            (PushbackLegKind.Push, TugMoveShape.Straight),
            (PushbackLegKind.Push, TugMoveShape.ViaLine),
            (PushbackLegKind.Pull, TugMoveShape.ViaLine),
            (PushbackLegKind.Push, TugMoveShape.ViaLine),
            (PushbackLegKind.Pull, TugMoveShape.ViaLine)
        );
        GroundNode sixA = Spot(layout, "6A");
        Assert.True(layout.TryGetSpotOutboundHeading(sixA, out double sixAOutDeg));
        LatLon sixAStop = TugMovePlanner.SpotStopGeometry(sixA, sixAOutDeg, Narrowbody).Stop;
        double halfSpanFt = TugMovePlanner.WingspanFt(Narrowbody) / 2.0;
        double passFt = plan.Moves[3].Samples.Min(s => FeetBetween(s.Position, sixAStop));
        Assert.False(plan.Moves[3].Move.Creep, "the pull onto 6A creeps, though 6A is a pass-through hint and only 6B is arrived at");
        Assert.True(passFt <= halfSpanFt, $"the pull onto 6A passes {passFt:F1} ft from its rest point, beyond half the span ({halfSpanFt:F1} ft)");
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
        TugPlan plan = PlanFromD15(layout, "6A");

        double totalFt = plan.Moves.Sum(m => m.PathLengthFt);
        _output.WriteLine($"D15 → 6A: straight line {straightFt:F1} ft, flown {totalFt:F1} ft ({totalFt / straightFt:F2}×)");
        Assert.True(totalFt <= 2.0 * straightFt, $"flew {totalFt:F1} ft for a {straightFt:F1} ft move");
    }

    /// <summary>
    /// D15 → 6A stays clear of every protected taxiway's object-free area, so the stepped candidates and the overswing
    /// filter never come into it: the plan is the ranking's own, move for move (pinned from the planner before the
    /// stepped template existed).
    /// </summary>
    [Fact]
    public void D15ToSixA_ClearOfTheTaxiways_KeepsTheRankingsPlanMoveForMove()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        using var tap = new CapturingSimLogProvider(LogLevel.Debug, 1000);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        TugPlan plan = PlanFromD15(layout, "6A");

        var planner = tap.Drain().Where(r => r.Category == "TugMovePlanner").Select(r => r.Message).ToList();
        Assert.Contains(planner, m => m.Contains(": chose T", StringComparison.Ordinal));
        Assert.DoesNotContain(planner, m => m.Contains("T4 stepped", StringComparison.Ordinal));

        (PushbackLegKind Kind, TugMoveShape Shape, bool Dwell, bool Creep, double PathFt)[] expected =
        [
            (PushbackLegKind.Push, TugMoveShape.Straight, false, false, 65.0),
            (PushbackLegKind.Push, TugMoveShape.Straight, false, false, 223.0),
            (PushbackLegKind.Push, TugMoveShape.ViaLine, false, false, 88.0),
            (PushbackLegKind.Pull, TugMoveShape.ViaLine, true, true, 447.0),
        ];
        Assert.Empty(plan.Warnings.OfType<TugFoulsTaxiwayWarning>());
        Assert.Equal(expected.Length, plan.Moves.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            TugMoveTrace trace = plan.Moves[i];
            Assert.Equal(
                (expected[i].Kind, expected[i].Shape, expected[i].Dwell, expected[i].Creep),
                (trace.Move.Kind, trace.Move.Shape, trace.Move.DwellBefore, trace.Move.Creep)
            );
            Assert.True(
                Math.Abs(trace.PathLengthFt - expected[i].PathFt) <= PinnedPathToleranceFt,
                $"move {i + 1} runs {trace.PathLengthFt:F1} ft, pinned at {expected[i].PathFt:F0} ft"
            );
        }
    }

    /// <summary>
    /// F8 → 7A: every plan fouls taxiway A's object-free area (the push-off alone puts the tail in it), and the ranking's
    /// own choice swings the nose about 180° toward another gate. The stepped template and the overswing filter keep the
    /// nose within <see cref="F8MaxSwingDeg"/> of the push-off's heading until it nears 7A, for no deeper a penetration, on
    /// one run of pushes ending in the creep pull onto 7A.
    /// </summary>
    [Fact]
    public void F8ToSevenA_FoulingTaxiwayA_StepsOntoTheLaneWithoutOverswinging()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode sevenA = Spot(layout, "7A");
        TugPlan plan = PlanOrFail(layout, StandStart(Parking(layout, "F8"), TugGoal.Spot(sevenA)));

        double swingDeg = SwingBeforeStopDeg(layout, plan, sevenA);
        double peakFt = plan.Warnings.OfType<TugFoulsTaxiwayWarning>().Select(w => w.PeakPenetrationFt).DefaultIfEmpty(0.0).Max();
        _output.WriteLine(
            $"F8 → 7A: nose swung at most {swingDeg:F1}° from the push-off's heading; {peakFt:F1} ft into a taxiway's object-free area "
                + "plus the 5 ft planning margin"
        );
        Assert.True(swingDeg <= F8MaxSwingDeg, $"the nose swung {swingDeg:F1}° from the push-off's heading, past {F8MaxSwingDeg:F0}°");
        Assert.True(
            peakFt <= F8MaxPenetrationFt,
            $"the plan reaches {peakFt:F1} ft into a taxiway's object-free area plus the 5 ft planning margin, past {F8MaxPenetrationFt:F1} ft"
        );
        Assert.Single(plan.Moves.Take(plan.Moves.Count - 1).Select(m => m.Move.Kind).Distinct());
        Assert.True(plan.Moves[^1].Move is { Kind: PushbackLegKind.Pull, Creep: true }, "the last move is the creep pull onto 7A");
    }

    /// <summary>
    /// When every candidate fouls, the ones reaching within the 5 ft planning margin of the shallowest are equally deep:
    /// among them the one swinging the nose least wins, and a candidate deeper than the band loses however little it
    /// swings.
    /// </summary>
    [Fact]
    public void LeastFoulingOf_WithinTheClearanceMargin_TheLeastSwingWins()
    {
        var start = new TugPose(new LatLon(37.62, -122.386), 254.0);
        TugFoulingCandidate Fouling(string template, double peakFt, double exposureFtFt, double swingDeg) =>
            new(
                new TugCandidate(template, Narrowbody, start, previousKind: null),
                new TugTaxiwayFouling("A", TugFootprintPart.RightWing, peakFt, exposureFtFt),
                new TugNoseSwing(swingDeg, 0.0)
            );
        List<TugFoulingCandidate> pool =
        [
            Fouling("shallowest, swings about", 19.30, 10.0, 179.0),
            Fouling("within the margin, swings least of those", 19.35 + TugTaxiwayClearance.ClearanceMarginFt - 0.1, 50.0, 82.0),
            Fouling("past the margin, swings least of all", 19.30 + TugTaxiwayClearance.ClearanceMarginFt + 0.1, 1.0, 10.0),
        ];

        List<TugFoulingCandidate> tiedSwings =
        [
            Fouling("swings least, more exposed", 19.30, 50.0, 84.0),
            Fouling("swings within 5° of the least, less exposed", 20.60, 20.0, 88.0),
            Fouling("swings past the tie, least exposed", 19.40, 1.0, 95.0),
        ];

        TugFoulingCandidate kept = TugPlanBuilder.LeastFoulingOf(pool);
        TugFoulingCandidate keptOfTied = TugPlanBuilder.LeastFoulingOf(tiedSwings);

        Assert.Equal("within the margin, swings least of those", kept.Candidate.Template);
        Assert.Equal("swings within 5° of the least, less exposed", keptOfTied.Candidate.Template);
    }

    /// <summary>
    /// The most the planned nose turns from its heading at the end of the push-off until the reference point first comes
    /// within half a wingspan of the spot's stop point, degrees — the stretch <c>SfoF8PushHintE2ETests</c> bounds in flight.
    /// The turn is accumulated sample to sample, so a swing past 180° reads as itself. The whole plan cannot be bounded
    /// tighter than the lane's own rotation, which it ends on.
    /// </summary>
    private static double SwingBeforeStopDeg(AirportGroundLayout layout, TugPlan plan, GroundNode spot)
    {
        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double outDeg));
        LatLon stop = TugMovePlanner.SpotStopGeometry(spot, outDeg, Narrowbody).Stop;
        double halfSpanFt = TugMovePlanner.WingspanFt(Narrowbody) / 2.0;
        var previous = new TrueHeading(plan.Moves[0].End.NoseTrueDeg);
        double runningDeg = 0.0;
        double maxDeg = 0.0;
        foreach (TugPose sample in plan.Moves.Skip(1).SelectMany(m => m.Samples))
        {
            var nose = new TrueHeading(sample.NoseTrueDeg);
            runningDeg += previous.SignedAngleTo(nose);
            previous = nose;
            maxDeg = Math.Max(maxDeg, Math.Abs(runningDeg));
            if (FeetBetween(sample.Position, stop) <= halfSpanFt)
            {
                break;
            }
        }

        return maxDeg;
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
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        PlanFromD15(layout, "6A", "6B");

        var legTwo = tap.Drain()
            .Where(r => r.Category == "TugMovePlanner")
            .Select(r => r.Message)
            .Where(m => m.StartsWith("Tug the move to spot 6B: ", StringComparison.Ordinal))
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

        TugPlan plan = PlanFromD15(layout, targets);
        var alpha = layout.AllEdges.Where(e => e.MatchesTaxiway("A")).ToList();
        Assert.NotEmpty(alpha);
        double halfLengthNm = (Assert.NotNull(FaaAircraftDatabase.Get(Narrowbody)?.LengthFt) / 2.0) / GeoMath.FeetPerNm;

        double closestFt = double.PositiveInfinity;
        for (int m = 0; m < plan.Moves.Count; m++)
        {
            foreach (TugPose sample in plan.Moves[m].Samples)
            {
                LatLon nose = GeoMath.ProjectPoint(sample.Position, new TrueHeading(sample.NoseTrueDeg), halfLengthNm);
                LatLon tail = GeoMath.ProjectPoint(sample.Position, new TrueHeading(sample.NoseTrueDeg + 180.0), halfLengthNm);
                foreach (IGroundEdge? edge in alpha)
                {
                    LatLon a = edge.Nodes[0].Position;
                    LatLon b = edge.Nodes[1].Position;
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

        GroundNode d16 = Parking(layout, "D16");
        double standDeg = Assert.NotNull(d16.TrueHeading).Degrees;
        TugPlan plan = PlanFromD15(layout, "6A", "@D16");

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
    public void EverySameKindRunWithoutATurn_StaysWithin150DegreesOfItsStartTravel(params string[] targets)
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        TugPlan plan = PlanFromD15(layout, targets);

        AssertNoLoop(plan, MaxSameKindWanderDeg);
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

        GroundNode stand = Parking(layout, "B12");
        GroundNode exit = layout.FindExitByTaxiway(stand.Position, "Y") ?? throw new InvalidOperationException("no taxiway Y exit near B12");
        GroundNode towardA1 = layout.FindExitByTaxiway(exit.Position, "A1") ?? throw new InvalidOperationException("no taxiway A1 exit near Y");
        double facingDeg = Assert.NotNull(layout.GetEdgeBearingForTaxiway(exit, "Y", GeoMath.BearingTo(exit.Position, towardA1.Position)));
        _output.WriteLine($"B12 heading {Assert.NotNull(stand.TrueHeading).Degrees:F1}°, facing along Y toward A1 {facingDeg:F1}°");

        TugPlan plan = PlanOrFail(layout, StandStart(stand, TugGoal.TaxiwayLine(exit, "Y", facingDeg)));

        AssertKinds(plan, PushbackLegKind.Push, PushbackLegKind.Push);
        Assert.Equal(TugMoveShape.Straight, plan.Moves[0].Move.Shape);
        TugMoveTrace line = plan.Moves[1];
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

        GroundNode stand = Parking(layout, "D15");
        double facingDeg = Assert.NotNull(stand.TrueHeading).Degrees + 150.0;
        double radiusFt = TugKinematics.TurnRadiusFt(Narrowbody, tight: true);

        TugPlan plan = PlanOrFail(layout, StandStart(stand, TugGoal.Facing(facingDeg)));

        TugMoveTrace turn = Assert.Single(plan.Moves, m => m.Move.Shape == TugMoveShape.TurnTo);
        Assert.Equal(PushbackLegKind.Push, turn.Move.Kind);
        Assert.True(turn.Move.Tight, "a facing change over 135° is flown on the tight radius");
        Assert.True(turn.Samples.Count >= 5, "too few samples to fit an arc");

        LatLon origin = turn.Samples[0].Position;
        (double X, double Y) centre = Circumcentre(
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

        TugPlan plan = PlanOrFail(layout, StandStart(Parking(layout, "D15"), TugGoal.Clear()));

        TugMove move = Assert.Single(plan.Moves).Move;
        Assert.Equal(PushbackLegKind.Push, move.Kind);
        Assert.Equal(TugMoveShape.Straight, move.Shape);
        Assert.Equal(CategoryPerformance.SimplePushbackDistanceNm(Narrowbody) * GeoMath.FeetPerNm, move.StraightDistanceFt, 6);
    }

    /// <summary>
    /// An airport with no ground layout still takes a bare <c>PUSH</c> and a <c>PUSH FACE</c>: both plan with no
    /// flown-path check, the same moves they plan with a layout. Any goal that names a place needs the layout.
    /// </summary>
    [Fact]
    public void NoLayout_PlansClearAndFacingWithoutThePathCheck_AndRejectsPlacedGoals()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode stand = Parking(layout, "D15");
        var standPose = new TugPose(stand.Position, Assert.NotNull(stand.TrueHeading).Degrees);
        double facingDeg = standPose.NoseTrueDeg + 90.0;

        TugPlan clear = Assert.IsType<TugPlan>(TugMovePlanner.Plan(null, StandStart(stand, TugGoal.Clear()), out string clearRefusal));
        TugPlan facing = Assert.IsType<TugPlan>(TugMovePlanner.Plan(null, StandStart(stand, TugGoal.Facing(facingDeg)), out string facingRefusal));
        TugPlan withLayout = PlanOrFail(layout, StandStart(stand, TugGoal.Facing(facingDeg)));
        _output.WriteLine(
            $"no layout: clear {clear.Moves.Count} move(s), facing {string.Join(", ", facing.Moves.Select(m => $"{m.Move.Kind} {m.Move.Shape}"))}"
        );

        Assert.Empty(clearRefusal);
        Assert.Empty(facingRefusal);
        TugMove straight = Assert.Single(clear.Moves).Move;
        Assert.Equal(TugMoveShape.Straight, straight.Shape);
        Assert.Equal(CategoryPerformance.SimplePushbackDistanceNm(Narrowbody) * GeoMath.FeetPerNm, straight.StraightDistanceFt, 6);
        Assert.Equal(withLayout.Moves.Select(m => m.Move), facing.Moves.Select(m => m.Move));
        Assert.True(AbsDiffDeg(facing.End.NoseTrueDeg, facingDeg) <= 1.0, $"ended with the nose on {facing.End.NoseTrueDeg:F1}°");
        Assert.Throws<ArgumentException>(() => TugMovePlanner.Plan(null, StandStart(stand, TugGoal.Spot(Spot(layout, "6A"))), out _));
    }

    /// <summary>Gate B12 turned about: taxiway Y, normally behind the stand, is now ahead of the nose.</summary>
    [Fact]
    public void StraightBackToATaxiwayAheadOfTheNose_Refused()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode stand = Parking(layout, "B12");
        GroundNode exit = layout.FindExitByTaxiway(stand.Position, "Y") ?? throw new InvalidOperationException("no taxiway Y exit near B12");
        var start = new TugPose(stand.Position, Assert.NotNull(stand.TrueHeading).Degrees + 180.0);

        string refusal = Refusal(layout, OffStand(start, TugGoal.StraightBackTo(exit, "Y")));

        Assert.Equal("Unable, taxiway Y is not behind the aircraft", refusal);
    }

    /// <summary>Spot 18 to spot 33 is 1,872 ft — under the sanity guard, but straight across 28L/10R and 28R/10L.</summary>
    [Fact]
    public void SpotEighteenToThirtyThree_RefusedForTheRunway()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode eighteen = Spot(layout, "18");
        var goal = TugGoal.Spot(Spot(layout, "33"));
        var start = new TugPose(eighteen.Position, GeoMath.BearingTo(eighteen.Position, goal.Node!.Position));

        string refusal = Refusal(layout, OffStand(start, goal));

        Assert.Contains(goal.Label, refusal, StringComparison.Ordinal);
        Assert.Matches(RunwayPavement, refusal);
    }

    /// <summary>
    /// Gate D1 to spot 34's node is 1,035 ft and cuts clean across taxiway A well short of the spot. Asked for as a
    /// node goal, whose one candidate has no shape to break; asked for as a spot, every candidate breaks a shape rule
    /// first, and the refusal is the generic one.
    /// </summary>
    [Fact]
    public void D1ToSpotThirtyFour_RefusedForTheTaxiway()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        var goal = TugGoal.AtNode(Spot(layout, "34"), facingTrueDeg: null);

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

        GroundNode eighteen = Spot(layout, "18");
        GroundNode? hold = layout
            .Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort)
            .MinBy(n => FeetBetween(n.Position, eighteen.Position));
        Assert.NotNull(hold);
        _output.WriteLine($"hold-short node {hold.Id} is {FeetBetween(hold.Position, eighteen.Position):F0} ft from spot 18");
        var goal = TugGoal.AtNode(hold, facingTrueDeg: null);
        var start = new TugPose(eighteen.Position, GeoMath.BearingTo(eighteen.Position, hold.Position));

        string refusal = Refusal(layout, OffStand(start, goal));

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

    /// <summary>
    /// Gate B24 to gate B21 with the nose on 180° true, 113° off B21's own heading — sideways in the stand. Every
    /// candidate breaks a shape rule, and one of them would also cross taxiway Y. That crossing is not why the move
    /// is refused: the refusal is the generic one, and the log still shows the crossing.
    /// </summary>
    [Fact]
    public void OnlyShapeDroppedCandidatesCrossPavement_RefusedWithTheGenericReason()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        using var tap = new CapturingSimLogProvider(LogLevel.Debug, 1000);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);
        TugRequest request = StandStart(Parking(layout, "B24"), TugGoal.Stand(Parking(layout, "B21"))) with { FinalFacingTrueDeg = 180.0 };

        string refusal = Refusal(layout, request);

        var dropped = tap.Drain()
            .Where(r => r.Category == "TugMovePlanner")
            .Select(r => r.Message)
            .Where(m => m.StartsWith("Tug the move to B21: dropped ", StringComparison.Ordinal))
            .ToList();
        dropped.ForEach(_output.WriteLine);
        Assert.Equal("Unable, cannot line up on B21 from here", refusal);
        Assert.NotEmpty(dropped);
        Assert.DoesNotContain(dropped, m => m.Contains("): Unable, ", StringComparison.Ordinal));
        Assert.Contains(
            dropped,
            m => m.Contains("(its flown path: Unable, the move to B21 would put the aircraft on taxiway ", StringComparison.Ordinal)
        );
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

    /// <summary>
    /// A <c>PUSHM</c> whose last target is forced to a kind no plan of that kind flies is refused naming the target: D15 via
    /// 6A to 6B forced <c>/PULL</c>. No arrival off D15 passes 6A, and from 6A, abeam it, 6B is no pull either, so the first
    /// arrival's refusal stands.
    /// </summary>
    [Fact]
    public void PushmLastTargetForcedToAnUnflyableKind_RefusedNamingTheTarget()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        TugGoal sixB = TugGoal.Spot(Spot(layout, "6B")) with { ForcedKind = PushbackLegKind.Pull };

        string refusal = Refusal(layout, StandStart(Parking(layout, "D15"), TugGoal.Spot(Spot(layout, "6A")), sixB));

        Assert.Equal("Unable, spot 6B cannot be reached by a pull", refusal);
    }

    /// <summary>
    /// A marked point with a facing no plan can end on — where the aircraft stands, nose reversed — is refused naming
    /// the facing as the controller gave it.
    /// </summary>
    [Fact]
    public void MarkedPointFacingNoPlanEndsOn_RefusedNamingTheFacing()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode spot = Spot(layout, "6A");
        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double outbound));
        LatLon stop = TugMovePlanner.SpotStopGeometry(spot, outbound, Narrowbody).Stop;
        double reversed = new TrueHeading(outbound).ToReciprocal().Degrees;
        var goal = TugGoal.FreePose(VirtualNode.Create(stop.Lat, stop.Lon), "north", reversed, "the marked point");

        string refusal = Refusal(layout, OffStand(new TugPose(stop, outbound), goal));

        Assert.Equal("Unable, the marked point cannot be reached facing north", refusal);
    }

    /// <summary>A marked point with no facing ahead of the nose off a stand cannot be reached by the push the stand needs.</summary>
    [Fact]
    public void UnfacedMarkedPointAheadOfTheNoseOffAStand_RefusedAsNotReachableByAPush()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode d15 = Parking(layout, "D15");
        LatLon ahead = GeoMath.ProjectPoint(d15.Position, d15.TrueHeading!.Value, 100.0 / GeoMath.FeetPerNm);
        var goal = TugGoal.FreePose(VirtualNode.Create(ahead.Lat, ahead.Lon), null, null, "the marked point");

        string refusal = Refusal(layout, StandStart(d15, goal));

        Assert.Equal("Unable, the marked point cannot be reached by a push", refusal);
    }

    /// <summary>
    /// Gate B12 to gate B13 (the <c>SfoPushbackTests</c> case): the push onto B13's lead-in line puts the tail on
    /// taxiway Y, 186 ft behind the B gates. Y is the taxiway straight behind the stand, so the push clearance
    /// covers it.
    /// </summary>
    [Fact]
    public void B12ToB13_PushesOffOntoTheLeadInLineAcrossYankeeAndPullsIn()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        TugPlan plan = PlanOrFail(layout, StandStart(Parking(layout, "B12"), TugGoal.Stand(Parking(layout, "B13"))));

        AssertKinds(plan, PushbackLegKind.Push, PushbackLegKind.Push, PushbackLegKind.Pull);
        Assert.Equal(TugMoveShape.Straight, plan.Moves[0].Move.Shape);
        Assert.Equal(TugMoveShape.ViaLine, plan.Moves[1].Move.Shape);
        Assert.True(plan.Moves[2].Move.DwellBefore, "the pull into B13 reverses the push before it, so it has to dwell first");
    }

    /// <summary>
    /// A bare <c>PUSH A</c> off B12 with a B752 (the <c>SfoYankeePushTests</c> control): the straight push crosses
    /// taxiway Y, the taxiway straight behind the stand, and stops on taxiway A.
    /// </summary>
    [Fact]
    public void B12StraightBackToAlpha_AcceptedAcrossYankee_StopsOnAlpha()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode stand = Parking(layout, "B12");
        GroundNode exit = layout.FindExitByTaxiway(stand.Position, "A") ?? throw new InvalidOperationException("no taxiway A exit near B12");

        TugPlan plan = PlanOrFail(layout, StandStart(stand, "B752", TugGoal.StraightBackTo(exit, "A")));

        AssertKinds(plan, PushbackLegKind.Push);
        AssertEndsOnTaxiway(layout, plan, "A");
    }

    /// <summary>
    /// A bare <c>PUSH B</c> off B12: the push crosses taxiways Y and A on the way. A push onto a taxiway is judged by how
    /// far its centre goes past that taxiway, not by the taxiways it sweeps over, and a
    /// straight push stops with its centre on B — accepted.
    /// </summary>
    [Fact]
    public void B12StraightBackToBravo_AcceptedAcrossYankeeAndAlpha_StopsOnBravo()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode stand = Parking(layout, "B12");
        GroundNode exit = layout.FindExitByTaxiway(stand.Position, "B") ?? throw new InvalidOperationException("no taxiway B exit near B12");

        TugPlan plan = PlanOrFail(layout, StandStart(stand, TugGoal.StraightBackTo(exit, "B")));

        AssertKinds(plan, PushbackLegKind.Push);
        AssertEndsOnTaxiway(layout, plan, "B");
    }

    /// <summary>
    /// SFO taxiway F reaches a runway holding position about 100 ft from its junction with AF. A B738 standing on F 30 ft
    /// past the bar, nose toward the runway, pushed straight back onto AF crosses the bar: still refused for the holding
    /// position, whatever the overshoot rule says about AF.
    /// </summary>
    [Fact]
    public void StraightBackOntoTaxiway_AcrossARunwayHoldingPosition_Refused()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        List<GroundNode> holds = [.. layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && HasStraightEdge(n, "F"))];
        List<GroundNode> junctions = [.. layout.Nodes.Values.Where(n => HasStraightEdge(n, "F") && HasStraightEdge(n, "AF"))];
        (GroundNode hold, GroundNode junction, double apartFt) = holds
            .SelectMany(h => junctions.Select(j => (Hold: h, Junction: j, ApartFt: FeetBetween(h.Position, j.Position))))
            .MinBy(p => p.ApartFt);
        _output.WriteLine($"F's holding position #{hold.Id} lies {apartFt:F0} ft from the F/AF junction #{junction.Id}");
        double towardRunwayDeg = GeoMath.BearingTo(junction.Position, hold.Position);
        LatLon start = GeoMath.ProjectPoint(hold.Position, new TrueHeading(towardRunwayDeg), 30.0 / GeoMath.FeetPerNm);

        string refusal = Refusal(layout, OffStand(new TugPose(start, towardRunwayDeg), TugGoal.StraightBackTo(junction, "AF")));

        Assert.Equal("Unable, the move to taxiway AF reaches a runway holding position", refusal);
    }

    private static bool HasStraightEdge(GroundNode node, string taxiway) => node.Edges.OfType<GroundEdge>().Any(e => e.MatchesTaxiway(taxiway));

    /// <summary>
    /// A bare <c>PUSH M4</c> off gate B2 with issue #172's B737: M4 runs alongside the push about 87 ft to the side, so
    /// the push ray crosses none of it. The push-off, then a push onto M4's centreline in an S-curve that carries on
    /// along the line until it reaches M4's nearest straight edge running shallower than 45° to the push, ending on that edge
    /// with the nose along it.
    /// </summary>
    [Fact]
    public void B2StraightBackToM4Alongside_PushesOffThenCurvesOntoTheCentreline()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode stand = Parking(layout, "B2");
        GroundNode exit = layout.FindExitByTaxiway(stand.Position, "M4") ?? throw new InvalidOperationException("no taxiway M4 exit near B2");
        (IGroundEdge? edge, double behindFt) = NearestAlongsideEdge(layout, stand, "M4");

        TugPlan plan = PlanOrFail(layout, StandStart(stand, "B737", TugGoal.StraightBackTo(exit, "M4")));

        AssertKinds(plan, PushbackLegKind.Push, PushbackLegKind.Push);
        Assert.Equal(TugMoveShape.Straight, plan.Moves[0].Move.Shape);
        Assert.Equal(TugMoveShape.ViaLine, plan.Moves[1].Move.Shape);
        LatLon a = edge.Nodes[0].Position;
        LatLon b = edge.Nodes[1].Position;
        double lineDeg = GeoMath.BearingTo(a, b);
        double offEdgeFt = GeoMath.DistanceToSegmentFt(plan.End.Position, a, b);
        double noseOffLineDeg = OffLineDeg(plan.End.NoseTrueDeg, lineDeg);
        _output.WriteLine(
            $"M4 edge {edge.Nodes[0].Id}-{edge.Nodes[1].Id} on {lineDeg:F1}°, nearest point {behindFt:F1} ft behind B2: ended {offEdgeFt:F2} ft "
                + $"off the edge, nose {noseOffLineDeg:F2}° off it"
        );
        Assert.True(offEdgeFt <= 1.0, $"ended {offEdgeFt:F2} ft off M4 edge {edge.Nodes[0].Id}-{edge.Nodes[1].Id}");
        Assert.True(noseOffLineDeg <= 1.0, $"ended with the nose {noseOffLineDeg:F2}° off M4's direction");
    }

    /// <summary>
    /// A bare <c>PUSH</c> onto a real taxiway that is neither crossed by the push nor running alongside it behind
    /// the aircraft: taxiway H lies about 1,100 ft from gate B2, and none of its straight edges crosses B2's push ray
    /// at 45° or more, nor runs shallower than 45° to it with its nearest point behind the aircraft and within the
    /// goal-distance guard — the nearest edge that runs that way is over 2,000 ft off to the side.
    /// </summary>
    [Fact]
    public void B2StraightBackToATaxiwayNeitherCrossedNorAlongside_Refused()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode stand = Parking(layout, "B2");
        GroundNode exit = layout.FindExitByTaxiway(stand.Position, "H") ?? throw new InvalidOperationException("no taxiway H exit near B2");
        _output.WriteLine($"taxiway H exit node {exit.Id} is {FeetBetween(stand.Position, exit.Position):F0} ft from B2");

        string refusal = Refusal(layout, StandStart(stand, "B737", TugGoal.StraightBackTo(exit, "H")));

        Assert.Equal("Unable, taxiway H is not behind the aircraft", refusal);
    }

    /// <summary>
    /// A bare <c>PUSH TE</c> off OAK gate 25 with issue #222's B737: the stand row is angled, so the push ray meets a
    /// TE edge almost end-on and far behind the stand. That is a taxiway running alongside the push, not one across
    /// it, so the plan is the push-off and a capture of TE's centreline, not one long straight push down the row.
    /// </summary>
    [Fact]
    public void PushTe_FromOakGate25_CapturesTeAlongsideInsteadOfALongStraightPush()
    {
        if (LoadOak() is not { } layout)
        {
            return;
        }

        GroundNode stand = layout.FindParkingByName("25") ?? throw new InvalidOperationException("OAK layout carries no parking named 25");
        GroundNode exit = layout.FindExitByTaxiway(stand.Position, "TE") ?? throw new InvalidOperationException("no taxiway TE exit near gate 25");
        _output.WriteLine($"gate 25 heading {Assert.NotNull(stand.TrueHeading).Degrees:F1}°, TE exit node {exit.Id}");

        TugPlan plan = PlanOrFail(layout, StandStart(stand, "B737", TugGoal.StraightBackTo(exit, "TE")));

        AssertKinds(plan, PushbackLegKind.Push, PushbackLegKind.Push);
        Assert.Equal(TugMoveShape.Straight, plan.Moves[0].Move.Shape);
        Assert.Equal(TugMoveShape.ViaLine, plan.Moves[1].Move.Shape);
        AssertEndsOnTaxiwayLine(layout, plan, "TE");
    }

    /// <summary>
    /// A bare <c>PUSH TE</c> off OAK gate 32: the capture of TE's centreline already leaves the aircraft in TE's
    /// pavement corridor, so the push is done there rather than carrying on down the line to the captured edge's
    /// nearest point (user, 2026-09-16). TE bends at the alley merge, so the aircraft ends abeam one piece of it with
    /// the nose along the other: the assertions take any straight TE edge whose extent the end lies in the corridor
    /// of, not <see cref="AssertEndsOnTaxiwayLine"/>'s nearest edge, which at a bend need not be the one the nose is
    /// along.
    /// </summary>
    [Fact]
    public void PushTe_FromOakGate32_StopsOnceOnTe()
    {
        if (LoadOak() is not { } layout)
        {
            return;
        }

        GroundNode stand = layout.FindParkingByName("32") ?? throw new InvalidOperationException("OAK layout carries no parking named 32");
        GroundNode exit = layout.FindExitByTaxiway(stand.Position, "TE") ?? throw new InvalidOperationException("no taxiway TE exit near gate 32");
        _output.WriteLine($"gate 32 heading {Assert.NotNull(stand.TrueHeading).Degrees:F1}°, TE exit node {exit.Id}");

        TugPlan plan = PlanOrFail(layout, StandStart(stand, Narrowbody, TugGoal.StraightBackTo(exit, "TE")));

        AssertKinds(plan, PushbackLegKind.Push, PushbackLegKind.Push);
        AssertEndsInTheTaxiwayCorridor(layout, plan, "TE");
        (IGroundEdge? capturedEdge, double behindFt) = NearestAlongsideEdge(layout, stand, "TE");
        double capturedLineDeg = GeoMath.BearingTo(capturedEdge.Nodes[0].Position, capturedEdge.Nodes[1].Position);
        double noseOffLineDeg = OffLineDeg(plan.End.NoseTrueDeg, capturedLineDeg);
        double pathFt = plan.Moves.Sum(m => m.PathLengthFt);
        _output.WriteLine(
            $"captured TE edge {capturedEdge.Nodes[0].Id}-{capturedEdge.Nodes[1].Id} on {capturedLineDeg:F1}°, its nearest point "
                + $"{behindFt:F1} ft behind gate 32: ended with the nose {noseOffLineDeg:F2}° off it"
        );
        Assert.True(noseOffLineDeg <= 1.0, $"ended with the nose {noseOffLineDeg:F2}° off the taxiway TE centreline the push captured");
        _output.WriteLine($"gate 32 PUSH TE total path {pathFt:F1} ft");
        Assert.True(
            pathFt < OnceOnTheTaxiwayPathFt,
            $"the push ran {pathFt:F1} ft, past the {OnceOnTheTaxiwayPathFt:F0} ft the capture itself takes"
        );
    }

    /// <summary>
    /// The five-alley spot pushes whose capture of the lane used to wander past 120°: gate D2 to spot 5A (issue #233's
    /// E75L, and a CRJ7) and gate C9 to spot 5B (an E75L). Each plans without a loop — no same-kind run wanders past the
    /// planner's 150° limit — and ends on the spot's rest pose.
    /// </summary>
    [Theory]
    [InlineData("D2", "5A", "E75L")]
    [InlineData("D2", "5A", "CRJ7")]
    [InlineData("C9", "5B", "E75L")]
    public void FiveAlleyStandToSpot_PlansWithoutALoopAndEndsOnTheRestPose(string standName, string spotName, string aircraftType)
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode spot = Spot(layout, spotName);
        _output.WriteLine($"{standName} → {spotName}, {aircraftType}");

        TugPlan plan = PlanOrFail(layout, StandStart(Parking(layout, standName), aircraftType, TugGoal.Spot(spot)));

        AssertNoLoop(plan, MaxSameKindWanderDeg);
        AssertEndsOnSpot(layout, plan, spot, aircraftType);
    }

    /// <summary>The regional jet the five-alley stands at SFO are pushed off in the field cases.</summary>
    private const string FiveAlleyRegional = "E75L";

    /// <summary>
    /// The field case from the SFO GC 28/01 bundle: D2 → spot 5A with an E75L parked at the adjacent stand D1. The lane
    /// turns the nose 152.9° right; every shape turning onto it from where the ordinary templates pivot swings within the
    /// 24.5 ft floor of D1's outline, and the three-point turn that clears it swings the nose 207° the wrong way. The
    /// planner pushes further straight back before the turn onto the lane (an extended straight): the push is accepted,
    /// keeps the floor on every planned sample, turns right no further than the lane's own rotation + 10°, reverses once
    /// and ends on 5A.
    /// </summary>
    [Fact]
    public void PushToSpot_WithNeighbourAtAdjacentStand_PushesFurtherBackBeforeTurningOntoTheLane()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode spot = Spot(layout, "5A");
        GroundNode stand = Parking(layout, "D2");
        GroundNode neighbourStand = Parking(layout, "D1");
        var neighbour = new TugParkedNeighbour
        {
            Callsign = "SKW3398",
            Position = neighbourStand.Position,
            TrueHeadingDeg = 4.9,
            AircraftType = FiveAlleyRegional,
            StandName = "D1",
        };
        TugRequest request = StandStart(stand, FiveAlleyRegional, TugGoal.Spot(spot)) with { ParkedNeighbours = [neighbour] };
        _output.WriteLine($"D2 → 5A, {FiveAlleyRegional}, {neighbour.Describe()} {FeetBetween(stand.Position, neighbourStand.Position):F0} ft away");

        TugPlan plan = PlanOrFail(layout, request);

        double closestFt = ClosestSweptClearanceFt(plan.Moves, request, neighbour);
        double floorFt = GroundOutlineSweep.FloorFt(StartClearanceFt(request, neighbour));
        double laneTurnDeg = new TrueHeading(request.Start.NoseTrueDeg).SignedAngleTo(new TrueHeading(plan.End.NoseTrueDeg));
        var swing = TugNoseSwing.From(request.Start.NoseTrueDeg, plan.Moves.SelectMany(m => m.Samples).Select(s => s.NoseTrueDeg));
        int reversals = plan.Moves.Count(m => m.Move.DwellBefore);
        _output.WriteLine(
            $"the plan comes within {closestFt:F1} ft of {neighbour.Describe()} (floor {floorFt:F1} ft); lane turn {laneTurnDeg:F1}°, swing right "
                + $"{swing.RightDeg:F1}° left {swing.LeftDeg:F1}°; {reversals} reversal(s); moves "
                + string.Join(" + ", plan.Moves.Select(m => $"{m.Move.Kind} {m.Move.Shape} {m.PathLengthFt:F0} ft"))
        );
        Assert.True(floorFt >= 24.5, $"the floor is {floorFt:F1} ft; the case is set up for the 24.5 ft floor of two outlines 54 ft apart");
        Assert.True(
            closestFt >= floorFt,
            $"the chosen plan swings within {closestFt:F1} ft of {neighbour.Describe()}, under its {floorFt:F1} ft floor"
        );
        Assert.True(laneTurnDeg > 0.0, $"the lane turns the nose {laneTurnDeg:F1}°; the case is a right turn");
        Assert.True(
            swing.MaxDeg <= laneTurnDeg + 10.0,
            $"the nose swung {swing.MaxDeg:F1}° for a lane turn of {laneTurnDeg:F1}° — past the lane's rotation + 10°"
        );
        Assert.True(swing.LeftDeg <= 5.0, $"the nose turned {swing.LeftDeg:F1}° left, against the lane's right turn");
        Assert.Equal(1, reversals);
        AssertEndsOnSpot(layout, plan, spot, FiveAlleyRegional);
    }

    /// <summary>
    /// The multi-point path (template T5) the planner falls back to when no other shape keeps a parked neighbour's floor
    /// without overswinging, built for the D2 → 5A field case. No stand-to-spot push at SFO with a same-type aircraft on
    /// one of the two nearest stands needs it — the extended straight takes D2 → 5A, and every other case either keeps to
    /// an earlier shape or is refused for its neighbour whatever the shape — so its construction is pinned here through
    /// the builder. Every path it keeps pushes straight until the reference point is past the lane's centreline, turns
    /// the nose the lane's way on the push, pulls forward, pushes back onto the lane and creeps forward onto 5A: three
    /// reversals, each dwelt, clear of D1's floor on every sample, within the lane's turn.
    /// </summary>
    [Fact]
    public void MultiPointPath_ForD2ToFiveAWithD1Occupied_PushesPastTheLaneThenTurnsPullsAndCreepsOntoTheSpot()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode spot = Spot(layout, "5A");
        GroundNode neighbourStand = Parking(layout, "D1");
        var neighbour = new TugParkedNeighbour
        {
            Callsign = "SKW3398",
            Position = neighbourStand.Position,
            TrueHeadingDeg = 4.9,
            AircraftType = FiveAlleyRegional,
            StandName = "D1",
        };
        TugRequest request = StandStart(Parking(layout, "D2"), FiveAlleyRegional, TugGoal.Spot(spot)) with { ParkedNeighbours = [neighbour] };
        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double facingDeg), "spot 5A has no nose-out heading");
        LatLon stop = TugMovePlanner.SpotStopGeometry(spot, facingDeg, FiveAlleyRegional).Stop;
        double LaneSideFt(LatLon point) => GeoMath.SignedCrossTrackDistanceNm(point, stop, new TrueHeading(facingDeg)) * GeoMath.FeetPerNm;
        double laneTurnDeg = new TrueHeading(request.Start.NoseTrueDeg).SignedAngleTo(new TrueHeading(facingDeg));
        double floorFt = GroundOutlineSweep.FloorFt(StartClearanceFt(request, neighbour));

        List<TugCandidate> kept = new TugPlanBuilder(layout, request).AcceptableMultiPointPaths();

        _output.WriteLine($"{kept.Count} multi-point paths kept; lane turn {laneTurnDeg:F1}°, floor {floorFt:F1} ft");
        Assert.NotEmpty(kept);
        foreach (TugCandidate path in kept)
        {
            IReadOnlyList<TugMoveTrace> moves = path.Traces;
            var swing = TugNoseSwing.From(request.Start.NoseTrueDeg, moves.SelectMany(m => m.Samples).Select(s => s.NoseTrueDeg));
            double closestFt = ClosestSweptClearanceFt(moves, request, neighbour);
            _output.WriteLine(
                $"{path.Template}: {path.Describe()}; closest {closestFt:F1} ft, swing right {swing.RightDeg:F1}° left {swing.LeftDeg:F1}°"
            );
            Assert.Equal(
                [PushbackLegKind.Push, PushbackLegKind.Push, PushbackLegKind.Push, PushbackLegKind.Pull, PushbackLegKind.Push, PushbackLegKind.Pull],
                moves.Select(m => m.Move.Kind)
            );
            Assert.Equal([TugMoveShape.Straight, TugMoveShape.Straight, TugMoveShape.TurnTo], moves.Take(3).Select(m => m.Move.Shape));
            Assert.Equal(TugMoveShape.ViaLine, moves[4].Move.Shape);
            Assert.True(moves[5].Move.Creep, "the last pull onto 5A is not a creep");
            Assert.Equal([false, false, false, true, true, true], moves.Select(m => m.Move.DwellBefore));
            Assert.True(
                Math.Sign(LaneSideFt(moves[1].End.Position)) != Math.Sign(LaneSideFt(request.Start.Position)),
                $"the straight push ended {LaneSideFt(moves[1].End.Position):F1} ft off the lane, short of its centreline"
            );
            Assert.True(closestFt >= floorFt, $"{path.Template} swings within {closestFt:F1} ft of SKW3398, under its {floorFt:F1} ft floor");
            Assert.True(
                swing.MaxDeg <= laneTurnDeg + 10.0,
                $"{path.Template} swings the nose {swing.MaxDeg:F1}° for a lane turn of {laneTurnDeg:F1}°"
            );
            Assert.True(swing.LeftDeg <= 5.0, $"{path.Template} turns the nose {swing.LeftDeg:F1}° against the lane's right turn");
            AssertEndsOnSpot(layout, new TugPlan([.. moves], path.End, [], null, null) { ForcedOverrides = [] }, spot, FiveAlleyRegional);
        }
    }

    /// <summary>
    /// The closest any sample of the plan's flown path comes to the neighbour's outline, feet, each sample's outline carrying
    /// the tug's lead ahead of the nose on a pull — the outline the planner's neighbour sweep measures.
    /// </summary>
    private static double ClosestSweptClearanceFt(IEnumerable<TugMoveTrace> moves, TugRequest request, TugParkedNeighbour neighbour)
    {
        var frame = new GroundOutlineFrame(request.Start.Position);
        var parked = GroundOutline.At(
            frame.ToLocal(neighbour.Position),
            neighbour.TrueHeadingDeg,
            GroundOutlineSize.Of(neighbour.AircraftType, towedNoseFirst: false)
        );
        return moves
            .SelectMany(m =>
                m.Samples.Select(pose =>
                    GroundOutline.Clearance(
                        GroundOutline.At(
                            frame.ToLocal(pose.Position),
                            pose.NoseTrueDeg,
                            GroundOutlineSize.Of(request.AircraftType, towedNoseFirst: m.Move.Kind == PushbackLegKind.Pull)
                        ),
                        parked
                    )
                )
            )
            .DefaultIfEmpty(0.0)
            .Min();
    }

    /// <summary>
    /// The split the plan-time neighbour sweep is drawn on: only a goal with templates to choose between is judged by
    /// it. A bare <c>PUSH TE</c> off OAK gate 25 with a B738 parked crossways on the push line is one shape and one
    /// shape only, so it is planned as it always was and the tug creeps up and stops short of the neighbour — the
    /// behaviour <c>GroundConflictDetector</c> owns. The plan really does sweep through the neighbour, which is what
    /// makes this a pin and not a vacuous pass.
    /// </summary>
    [Fact]
    public void BarePushToTaxiway_WithANeighbourOnThePushLine_IsStillPlanned()
    {
        if (LoadOak() is not { } layout)
        {
            return;
        }

        GroundNode stand = Parking(layout, "25");
        GroundNode exit = layout.FindExitByTaxiway(stand.Position, "TE") ?? throw new InvalidOperationException("no taxiway TE exit near gate 25");
        double pushDeg = new TrueHeading(stand.TrueHeading!.Value.Degrees + 180.0).Degrees;
        var neighbour = new TugParkedNeighbour
        {
            Callsign = "PRK1",
            Position = GeoMath.ProjectPoint(stand.Position, new TrueHeading(pushDeg), 230.0 / GeoMath.FeetPerNm),
            TrueHeadingDeg = new TrueHeading(pushDeg + 90.0).Degrees,
            AircraftType = Narrowbody,
            StandName = null,
        };
        TugRequest request = StandStart(stand, Narrowbody, TugGoal.StraightBackTo(exit, "TE")) with { ParkedNeighbours = [neighbour] };

        TugPlan plan = PlanOrFail(layout, request);

        double closestFt = ClosestSweptClearanceFt(plan.Moves, request, neighbour);
        _output.WriteLine($"the planned push sweeps to {closestFt:F1} ft of {neighbour.Describe()} and is still planned");
        Assert.True(closestFt <= 0.0, $"test setup: the planned push passes {closestFt:F1} ft clear of the neighbour, so it pins nothing");
    }

    /// <summary>
    /// The other side of that split: a faced goal whose every template fouls is refused naming the neighbour. Another
    /// E75L is standing on spot 5A itself, so every candidate ends inside it, whichever side and template it is flown
    /// on.
    /// </summary>
    [Fact]
    public void PushToSpot_WithTheSpotOccupied_IsRefusedNamingTheNeighbour()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode spot = Spot(layout, "5A");
        var neighbour = new TugParkedNeighbour
        {
            Callsign = "SKW3400",
            Position = spot.Position,
            TrueHeadingDeg = 118.0,
            AircraftType = FiveAlleyRegional,
            StandName = null,
        };
        TugRequest request = StandStart(Parking(layout, "D2"), FiveAlleyRegional, TugGoal.Spot(spot)) with { ParkedNeighbours = [neighbour] };

        string refusal = Refusal(layout, request);

        Assert.Contains("SKW3400", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// SFO C11 → spot 23, B738, with another B738 parked on the neighbouring stand C9V. Every one of the goal's own
    /// templates whose shape is sound is dropped for swinging into it, so the push is refused naming it. The search past
    /// the neighbour then builds fallback shapes, and some of those cross taxiway G — a more severe reason, which used to
    /// replace the neighbour in the refusal. The fallback pools may open that search, but the refusal is the templates'
    /// own.
    /// </summary>
    [Fact]
    public void PushToSpot_RefusedForTheNeighbour_KeepsTheNeighbourRefusalWhateverTheFallbackShapesCross()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        const string type = "B738";
        GroundNode neighbourStand = Parking(layout, "C9V");
        var neighbour = new TugParkedNeighbour
        {
            Callsign = "UAL1402",
            Position = neighbourStand.Position,
            TrueHeadingDeg = neighbourStand.TrueHeading!.Value.Degrees,
            AircraftType = type,
            StandName = "C9V",
        };
        TugRequest request = StandStart(Parking(layout, "C11"), type, TugGoal.Spot(Spot(layout, "23"))) with { ParkedNeighbours = [neighbour] };

        string refusal = Refusal(layout, request);

        Assert.Equal("Unable, the move to spot 23 would swing into UAL1402 at C9V", refusal);
    }

    /// <summary>
    /// The multi-point paths (template T5) reached through the keep's pools: SFO C11 → spot 23, B738, with another B738
    /// standing on spot 23 itself. A parked neighbour drops the goal's templates, the stand push-off alone keeps its floor,
    /// and no shape built before the multi-point paths keeps the floor without overswinging, so the keep builds them. They
    /// end on the spot too, so the push is still refused naming the aircraft on it. No request at SFO with the neighbour on
    /// a real stand opens that search (a sweep of 565 stand-to-spot requests, 2026-09-25): only an aircraft on the spot
    /// itself does, which is why the shape is pinned through the builder as well.
    /// </summary>
    [Fact]
    public void PushToSpot_WithTheSpotOccupied_SearchesTheMultiPointPathsBeforeRefusing()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        const string type = "B738";
        GroundNode spot = Spot(layout, "23");
        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double facingDeg), "spot 23 has no nose-out heading");
        var neighbour = new TugParkedNeighbour
        {
            Callsign = "UAL1402",
            Position = spot.Position,
            TrueHeadingDeg = facingDeg,
            AircraftType = type,
            StandName = null,
        };
        TugRequest request = StandStart(Parking(layout, "C11"), type, TugGoal.Spot(spot)) with { ParkedNeighbours = [neighbour] };
        using var tap = new CapturingSimLogProvider(LogLevel.Debug, 50000);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        string refusal = Refusal(layout, request);

        List<string> planner = [.. tap.Drain().Where(r => r.Category == "TugMovePlanner").Select(r => r.Message)];
        Assert.Contains(planner, m => m.Contains("trying the multi-point paths", StringComparison.Ordinal));
        Assert.Contains(planner, m => m.Contains("T5 multi-point", StringComparison.Ordinal));
        Assert.Equal("Unable, the move to spot 23 would swing into UAL1402", refusal);
    }

    /// <summary>The clearance the plan's start pose has from the neighbour, feet — the pose its first move's floor is anchored to.</summary>
    private static double StartClearanceFt(TugRequest request, TugParkedNeighbour neighbour) =>
        OutlineClearanceFt(request.Start, request.AircraftType, neighbour);

    /// <summary>One pose's outline clearance from the neighbour, feet.</summary>
    private static double OutlineClearanceFt(TugPose pose, string aircraftType, TugParkedNeighbour neighbour)
    {
        var frame = new GroundOutlineFrame(pose.Position);
        return GroundOutline.Clearance(
            GroundOutline.At(frame.ToLocal(pose.Position), pose.NoseTrueDeg, GroundOutlineSize.Of(aircraftType, towedNoseFirst: false)),
            GroundOutline.At(
                frame.ToLocal(neighbour.Position),
                neighbour.TrueHeadingDeg,
                GroundOutlineSize.Of(neighbour.AircraftType, towedNoseFirst: false)
            )
        );
    }

    /// <summary>
    /// A two-goal move whose second leg starts on movement-area pavement: D15 pushes onto the taxiway A node nearest
    /// the six-alley mouth, and from there back onto spot 6A. Leg 2 begins with the fuselage lying across taxiway A —
    /// the pavement leg 1 was cleared onto — so leaving it is allowed for as long as the fuselage is still across it,
    /// however far that is from where the plan started.
    /// </summary>
    [Fact]
    public void PushmThroughATaxiwayNode_SecondLegMayLeaveThePavementItStartsOn()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode sixA = Spot(layout, "6A");
        GroundNode alpha = NearestMovementAreaNode(layout, "A", sixA.Position);
        _output.WriteLine(
            $"taxiway A node {alpha.Id} is {FeetBetween(alpha.Position, sixA.Position):F0} ft from spot 6A "
                + $"and {FeetBetween(alpha.Position, Parking(layout, "D15").Position):F0} ft from D15"
        );

        TugPlan plan = PlanOrFail(layout, StandStart(Parking(layout, "D15"), TugGoal.AtNode(alpha, facingTrueDeg: null), TugGoal.Spot(sixA)));

        int legOneEnd = plan
            .Moves.Select((m, i) => (Move: m, Index: i))
            .Where(m => FeetBetween(m.Move.End.Position, alpha.Position) <= LegHandoverToleranceFt)
            .Select(m => m.Index)
            .DefaultIfEmpty(-1)
            .First();
        _output.WriteLine($"leg 1 ends with move {legOneEnd + 1} of {plan.Moves.Count}");
        Assert.True(legOneEnd >= 0, $"no move ended within {LegHandoverToleranceFt:F0} ft of taxiway A node {alpha.Id}");
        Assert.True(legOneEnd < (plan.Moves.Count - 1), "leg 2 flew no move of its own");
        double legTwoStartFt = FeetBetween(plan.Moves[legOneEnd + 1].Samples[0].Position, alpha.Position);
        Assert.True(legTwoStartFt <= LegHandoverToleranceFt, $"leg 2 started {legTwoStartFt:F2} ft from taxiway A node {alpha.Id}");
        AssertEndsOnSpot(layout, plan, sixA);
    }

    /// <summary>
    /// A <c>PUSHM</c> whose arrival is refused before any candidate is built is refused as it stands, with no pass-through
    /// tow: D15 via the taxiway A node nearest the six-alley mouth to the taxiway B node a thousand feet down the field. The
    /// B node lies ahead of D15's nose, and a node goal ahead of the nose off a stand is refused; no candidate was dropped
    /// for missing the A node, so the tow is not taken onto it first.
    /// </summary>
    [Fact]
    public void PushmToANodeAheadOfTheNose_ViaATaxiwayNode_RefusedAsTheArrivalStands()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode alpha = NearestMovementAreaNode(layout, "A", Spot(layout, "6A").Position);
        double alongDeg = AlongTaxiwayDeg(layout, "A", alpha);
        LatLon downField = GeoMath.ProjectPoint(alpha.Position, new TrueHeading(alongDeg), AlongTaxiwayProbeFt / GeoMath.FeetPerNm);
        GroundNode bravo = NearestMovementAreaNode(layout, "B", downField);
        _output.WriteLine(
            $"taxiway A node {alpha.Id} runs on {alongDeg:F1}°; taxiway B node {bravo.Id} is {FeetBetween(alpha.Position, bravo.Position):F0} ft "
                + $"away on {GeoMath.BearingTo(alpha.Position, bravo.Position):F1}°, so the leg runs down A at a shallow angle"
        );

        string refusal = Refusal(
            layout,
            StandStart(Parking(layout, "D15"), TugGoal.AtNode(alpha, facingTrueDeg: null), TugGoal.AtNode(bravo, facingTrueDeg: null))
        );

        Assert.Equal($"Unable, node {bravo.Id} is ahead of the nose — the aircraft has to be pushed back off the stand first", refusal);
    }

    /// <summary>
    /// A <c>PUSHM</c> whose arrival is refused outright — spot 1 lies past the sanity guard from D5 — with a hint pending
    /// that every candidate would pass (the end of the stand push-off) is refused as the arrival stands: the refusal names
    /// spot 1 and no pass-through tow is tried.
    /// </summary>
    [Fact]
    public void PushmArrivalPastTheSanityGuard_WithAHintPending_RefusedWithoutAPassThroughTow()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        using var tap = new CapturingSimLogProvider(LogLevel.Debug, TapCapacity);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);
        GroundNode d5 = Parking(layout, "D5");

        string refusal = Refusal(layout, StandStart(d5, PushOffEndPoint(d5, Narrowbody), TugGoal.Spot(Spot(layout, "1"))));

        Assert.StartsWith("Unable, spot 1 is ", refusal, StringComparison.Ordinal);
        Assert.Contains("sanity guard", refusal, StringComparison.Ordinal);
        Assert.Empty(FallbackLog(tap));
    }

    /// <summary>
    /// A <c>PUSHM</c> whose arrival is refused for a neighbour — another E75L stands on spot 5A — with a hint every candidate
    /// passes (the end of D2's stand push-off): no candidate misses the hint, so the neighbour's refusal stands and no
    /// pass-through tow is tried.
    /// </summary>
    [Fact]
    public void PushmArrivalRefusedForANeighbour_WithAHintEveryCandidatePasses_RefusedWithoutAPassThroughTow()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        using var tap = new CapturingSimLogProvider(LogLevel.Debug, TapCapacity);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);
        GroundNode spot = Spot(layout, "5A");
        GroundNode d2 = Parking(layout, "D2");
        var neighbour = new TugParkedNeighbour
        {
            Callsign = "SKW3400",
            Position = spot.Position,
            TrueHeadingDeg = 118.0,
            AircraftType = FiveAlleyRegional,
            StandName = null,
        };
        TugRequest request = StandStart(d2, FiveAlleyRegional, PushOffEndPoint(d2, FiveAlleyRegional), TugGoal.Spot(spot)) with
        {
            ParkedNeighbours = [neighbour],
        };

        string refusal = Refusal(layout, request);

        Assert.StartsWith("Unable, the move to spot 5A ", refusal, StringComparison.Ordinal);
        Assert.Contains("SKW3400", refusal, StringComparison.Ordinal);
        Assert.Empty(FallbackLog(tap));
    }

    /// <summary>
    /// A mid-push <c>PUSHM</c> whose first move reverses the running one (<see cref="TugRequest.PreviousKind"/> a pull): from
    /// spot 7A's rest pose, a marked point 150 ft straight behind the tail with a hint halfway to it. The one push reverses
    /// the running pull, not into a final arrival, so it passes the hint and flies as the move without the hint does.
    /// </summary>
    [Fact]
    public void MidPushPushm_FirstMoveReversingTheRunningOne_PassesAHintOnItsPath()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode spot = Spot(layout, "7A");
        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double outbound));
        LatLon rest = TugMovePlanner.SpotStopGeometry(spot, outbound, Narrowbody).Stop;
        TrueHeading behind = new TrueHeading(outbound).ToReciprocal();
        TugRequest alone = OffStand(new TugPose(rest, outbound), MarkedPoint(rest, behind, 150.0, "the marked point")) with
        {
            PreviousKind = PushbackLegKind.Pull,
            Forced = false,
        };
        TugPlan direct = PlanOrFail(layout, alone);

        using var tap = new CapturingSimLogProvider(LogLevel.Debug, TapCapacity);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);
        TugPlan hinted = PlanOrFail(
            layout,
            alone with
            {
                Goals = [MarkedPoint(rest, behind, 75.0, "marked point 1"), MarkedPoint(rest, behind, 150.0, "marked point 2")],
            }
        );

        Assert.True(hinted.Moves[0].Move.DwellBefore, "the first move does not reverse the running pull");
        Assert.Equal([.. direct.Moves.Select(m => (m.Move.Kind, m.Move.Shape))], [.. hinted.Moves.Select(m => (m.Move.Kind, m.Move.Shape))]);
        Assert.Empty(FallbackLog(tap));
    }

    /// <summary>
    /// Two hints on <c>PUSH $7B</c>'s path off F8, given in the reverse of the order it passes them: no candidate passes the
    /// later point first and then the earlier one, so the tow falls back to a pass-through tow onto the first hint.
    /// </summary>
    [Fact]
    public void PushmHintsInReverseOrder_MissAndFallBackToAPassThroughTow()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode f8 = Parking(layout, "F8");
        TugPlan direct = PlanOrFail(layout, StandStart(f8, TugGoal.Spot(Spot(layout, "7B"))));
        List<LatLon> passing = [.. PassingPositions(direct.Moves)];
        LatLon early = passing[passing.Count / 4];
        LatLon late = passing[(passing.Count * 3) / 4];
        _output.WriteLine($"early and late points {FeetBetween(early, late):F0} ft apart on PUSH $7B's path before its last reversal");
        Assert.True(FeetBetween(early, late) > HalfSpanFt(), "the two points lie within half the span of each other");

        using var tap = new CapturingSimLogProvider(LogLevel.Debug, TapCapacity);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);
        TugPlan? plan = TugMovePlanner.Plan(
            layout,
            StandStart(f8, MarkedPoint(late, "marked point 1"), MarkedPoint(early, "marked point 2"), TugGoal.Spot(Spot(layout, "7B"))),
            out string refusal
        );
        _output.WriteLine($"plan {(plan is null ? "refused" : "accepted")}: '{refusal}'");

        List<string> fallbacks = FallbackLog(tap);
        fallbacks.ForEach(_output.WriteLine);
        Assert.Contains(fallbacks, m => m.Contains("no arrival passes marked point 1", StringComparison.Ordinal));
    }

    /// <summary>
    /// C9 <c>PUSHM $5A ~point $5B</c>, the point on the arrival <c>PUSHM $5A $5B</c> flies from 5A after its pass-through tow:
    /// no candidate off C9 passes 5A, so the tow falls back onto 5A once, and the arrival from there passes the point.
    /// </summary>
    [Fact]
    public void PushmFirstHintFallsBack_ArrivalStillPassesTheSecond()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode c9 = Parking(layout, "C9");
        GroundNode fiveA = Spot(layout, "5A");
        Assert.True(layout.TryGetSpotOutboundHeading(fiveA, out double fiveAOutDeg));
        LatLon fiveARest = TugMovePlanner.SpotStopGeometry(fiveA, fiveAOutDeg, Narrowbody).Stop;
        TugPlan viaFiveA = PlanOrFail(layout, StandStart(c9, TugGoal.Spot(fiveA), TugGoal.Spot(Spot(layout, "5B"))));
        int towEnd = viaFiveA.Moves.ToList().FindIndex(m => FeetBetween(m.End.Position, fiveARest) <= LegHandoverToleranceFt);
        Assert.True((towEnd >= 0) && (towEnd < (viaFiveA.Moves.Count - 1)), "PUSHM $5A $5B off C9 took no pass-through tow onto 5A");
        IReadOnlyList<TugPose> arrivalFirst = viaFiveA.Moves[towEnd + 1].Samples;
        LatLon point = arrivalFirst[arrivalFirst.Count / 2].Position;
        _output.WriteLine($"the point is {FeetBetween(point, fiveARest):F0} ft from 5A's rest point, on the arrival's first move");

        using var tap = new CapturingSimLogProvider(LogLevel.Debug, TapCapacity);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);
        TugPlan? plan = TugMovePlanner.Plan(
            layout,
            StandStart(c9, TugGoal.Spot(fiveA), MarkedPoint(point, "the marked point"), TugGoal.Spot(Spot(layout, "5B"))),
            out string refusal
        );
        Assert.True(plan is not null, $"the plan was refused: {refusal}");

        List<string> fallbacks = FallbackLog(tap);
        fallbacks.ForEach(_output.WriteLine);
        Assert.Single(fallbacks);
        Assert.Contains("no arrival passes spot 5A", fallbacks[0], StringComparison.Ordinal);
        double passFt = plan.Moves.SelectMany(m => m.Samples).Min(s => FeetBetween(s.Position, point));
        Assert.True(passFt <= HalfSpanFt(), $"the plan passes {passFt:F1} ft from the marked point, beyond half the span");
    }

    /// <summary>
    /// The hint step's bound on <c>PUSH $7B</c>'s path off F8: a hint half the span less a foot from the path is passed,
    /// one half the span and a foot from it is missed.
    /// </summary>
    [Fact]
    public void HintMiss_HalfTheSpanLessAFootPasses_AndAFootMoreMisses()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode f8 = Parking(layout, "F8");
        TugPlan direct = PlanOrFail(layout, StandStart(f8, TugGoal.Spot(Spot(layout, "7B"))));
        List<LatLon> passing = [.. PassingPositions(direct.Moves)];
        double halfSpanFt = HalfSpanFt();
        (LatLon inside, LatLon outside) = AbeamPoints(passing, passing.Count / 2, halfSpanFt);
        double outsideFt = passing.Min(p => FeetBetween(p, outside));
        _output.WriteLine(
            $"half span {halfSpanFt:F2} ft; path to inside {passing.Min(p => FeetBetween(p, inside)):F2} ft, to outside {outsideFt:F2} ft"
        );
        Assert.True(outsideFt > halfSpanFt + 0.5, $"the outside point lies {outsideFt:F2} ft from the path, not a foot beyond half the span");

        Assert.Null(TugPlanBuilder.HintMiss([ResolvedHint(layout, f8, MarkedPoint(inside, "the marked point"))], direct.Moves, halfSpanFt));
        string? miss = TugPlanBuilder.HintMiss([ResolvedHint(layout, f8, MarkedPoint(outside, "the marked point"))], direct.Moves, halfSpanFt);
        Assert.NotNull(miss);
        Assert.Contains("it passes no closer than", miss, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hint forced to a pull on <c>PUSH $7B</c>'s path off F8, where every move before the last reversal is a push: passed
    /// while pushing only, so it is missed; the same hint forced to a push is passed.
    /// </summary>
    [Fact]
    public void HintMiss_ForcedPullHintPassedOnlyWhilePushing_Misses()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode f8 = Parking(layout, "F8");
        TugPlan direct = PlanOrFail(layout, StandStart(f8, TugGoal.Spot(Spot(layout, "7B"))));
        int lastReversal = LastReversal(direct.Moves);
        Assert.All(direct.Moves.Take(lastReversal), m => Assert.Equal(PushbackLegKind.Push, m.Move.Kind));
        List<LatLon> passing = [.. PassingPositions(direct.Moves)];
        TugGoal onThePath = MarkedPoint(passing[passing.Count / 2], "the marked point");

        string? pulling = TugPlanBuilder.HintMiss(
            [ResolvedHint(layout, f8, onThePath with { ForcedKind = PushbackLegKind.Pull })],
            direct.Moves,
            HalfSpanFt()
        );
        string? pushing = TugPlanBuilder.HintMiss(
            [ResolvedHint(layout, f8, onThePath with { ForcedKind = PushbackLegKind.Push })],
            direct.Moves,
            HalfSpanFt()
        );

        _output.WriteLine($"forced pull: {pulling}");
        Assert.NotNull(pulling);
        Assert.Contains("the marked point while pulling", pulling, StringComparison.Ordinal);
        Assert.Null(pushing);
    }

    /// <summary>
    /// A hint on <c>PUSH @B13</c>'s path off B12 that the path reaches only after its last reversal — the pull into B13, the
    /// final arrival — is missed. (F8 → 7B's final pull retraces its push, so no point on it lies clear of the path before.)
    /// </summary>
    [Fact]
    public void HintMiss_HintReachedOnlyAfterTheLastReversal_Misses()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode b12 = Parking(layout, "B12");
        TugPlan direct = PlanOrFail(layout, StandStart(b12, TugGoal.Stand(Parking(layout, "B13"))));
        List<LatLon> passing = [.. PassingPositions(direct.Moves)];
        LatLon afterReversal = direct
            .Moves.Skip(LastReversal(direct.Moves))
            .SelectMany(m => m.Samples)
            .Select(s => s.Position)
            .MaxBy(p => passing.Min(q => FeetBetween(p, q)));
        double beforeFt = passing.Min(p => FeetBetween(p, afterReversal));
        _output.WriteLine($"the point on the final arrival lies {beforeFt:F1} ft from the path before the last reversal");
        Assert.True(beforeFt > HalfSpanFt(), "every point on the final arrival lies within half the span of the path before it");

        string? miss = TugPlanBuilder.HintMiss(
            [ResolvedHint(layout, b12, MarkedPoint(afterReversal, "the marked point"))],
            direct.Moves,
            HalfSpanFt()
        );

        Assert.NotNull(miss);
        Assert.Contains("before its final arrival", miss, StringComparison.Ordinal);
    }

    /// <summary>A marked point where a stand's push-off ends: half the fuselage straight back from the stand.</summary>
    private static TugGoal PushOffEndPoint(GroundNode stand, string aircraftType) =>
        MarkedPoint(stand.Position, stand.TrueHeading!.Value.ToReciprocal(), AircraftLength.ResolveFt(aircraftType) / 2.0, "the marked point");

    private static TugGoal MarkedPoint(LatLon from, TrueHeading bearing, double distanceFt, string label) =>
        MarkedPoint(GeoMath.ProjectPoint(from, bearing, distanceFt / GeoMath.FeetPerNm), label);

    private static TugGoal MarkedPoint(LatLon point, string label) => TugGoal.FreePose(VirtualNode.Create(point.Lat, point.Lon), null, null, label);

    /// <summary>A pass-through hint as the planner resolves it: the first of two goals off <paramref name="stand"/>.</summary>
    private static ResolvedTugGoal ResolvedHint(AirportGroundLayout layout, GroundNode stand, TugGoal hint) =>
        TugGoalResolver.Resolve(layout, StandStart(stand, hint, TugGoal.Clear()), 0);

    /// <summary>The index of a plan's last reversal after its first move, or the move count when it has none.</summary>
    private static int LastReversal(IReadOnlyList<TugMoveTrace> moves)
    {
        int last = moves.ToList().FindLastIndex(m => m.Move.DwellBefore);
        return last >= 1 ? last : moves.Count;
    }

    /// <summary>Where the reference point is in every move before the plan's last reversal.</summary>
    private static IEnumerable<LatLon> PassingPositions(IReadOnlyList<TugMoveTrace> moves) =>
        moves.Take(LastReversal(moves)).SelectMany(m => m.Samples).Select(s => s.Position);

    /// <summary>
    /// Two points abeam the path at sample <paramref name="index"/>, on the side the path stays furthest from: half the
    /// span less a foot and half the span and a foot from it.
    /// </summary>
    private static (LatLon Inside, LatLon Outside) AbeamPoints(List<LatLon> path, int index, double halfSpanFt)
    {
        LatLon at = path[index];
        var travel = new TrueHeading(GeoMath.BearingTo(path[index - 1], path[index + 1]));
        TrueHeading side = new[] { travel.Degrees + 90.0, travel.Degrees - 90.0 }
            .Select(deg => new TrueHeading(deg))
            .MaxBy(h => path.Min(p => FeetBetween(p, GeoMath.ProjectPoint(at, h, (halfSpanFt + 1.0) / GeoMath.FeetPerNm))));
        return (
            GeoMath.ProjectPoint(at, side, (halfSpanFt - 1.0) / GeoMath.FeetPerNm),
            GeoMath.ProjectPoint(at, side, (halfSpanFt + 1.0) / GeoMath.FeetPerNm)
        );
    }

    private static double HalfSpanFt() => TugMovePlanner.WingspanFt(Narrowbody) / 2.0;

    /// <summary>The planner's pass-through fallback log lines.</summary>
    private static List<string> FallbackLog(CapturingSimLogProvider tap) =>
        [
            .. tap.Drain()
                .Where(r => r.Category == "TugMovePlanner")
                .Select(r => r.Message)
                .Where(m => m.Contains("falling back to a pass-through tow", StringComparison.Ordinal)),
        ];

    /// <summary>The node nearest <paramref name="near"/> on one of the taxiway's straight movement-area edges.</summary>
    private static GroundNode NearestMovementAreaNode(AirportGroundLayout layout, string taxiway, LatLon near) =>
        MovementAreaEdges(layout, taxiway).SelectMany(e => e.Nodes).DistinctBy(n => n.Id).MinBy(n => GeoMath.DistanceNm(n.Position, near))
        ?? throw new InvalidOperationException($"SFO layout carries no movement-area taxiway {taxiway} edge");

    /// <summary>The direction the taxiway runs at one of its nodes, taken from the longest of its edges there.</summary>
    private static double AlongTaxiwayDeg(AirportGroundLayout layout, string taxiway, GroundNode node)
    {
        GroundEdge edge =
            MovementAreaEdges(layout, taxiway)
                .Where(e => e.Nodes.Any(n => n.Id == node.Id))
                .MaxBy(e => GeoMath.DistanceNm(e.Nodes[0].Position, e.Nodes[1].Position))
            ?? throw new InvalidOperationException($"node {node.Id} is on no movement-area taxiway {taxiway} edge");
        GroundNode far = edge.Nodes[0].Id == node.Id ? edge.Nodes[1] : edge.Nodes[0];
        return GeoMath.BearingTo(node.Position, far.Position);
    }

    private static IEnumerable<GroundEdge> MovementAreaEdges(AirportGroundLayout layout, string taxiway)
    {
        var pavement = new TugPavementClassifier(layout);
        return layout.AllEdges.OfType<GroundEdge>().Where(e => e.MatchesTaxiway(taxiway) && (pavement.MovementAreaName(e) is not null));
    }

    private static AirportGroundLayout? LoadSfo() => new TestAirportGroundData().GetLayout("SFO");

    private static AirportGroundLayout? LoadOak() => new TestAirportGroundData().GetLayout("OAK");

    /// <summary>A stand start off D15 through <paramref name="targets"/>: a spot name, or <c>@stand</c>.</summary>
    private TugPlan PlanFromD15(AirportGroundLayout layout, params string[] targets)
    {
        TugGoal[] goals = [.. targets.Select(t => t.StartsWith('@') ? TugGoal.Stand(Parking(layout, t[1..])) : TugGoal.Spot(Spot(layout, t)))];
        _output.WriteLine($"D15 → {string.Join(" → ", targets)}");
        return PlanOrFail(layout, StandStart(Parking(layout, "D15"), goals));
    }

    private static TugRequest StandStart(GroundNode stand, params TugGoal[] goals) => StandStart(stand, Narrowbody, goals);

    private static TugRequest StandStart(GroundNode stand, string aircraftType, params TugGoal[] goals) =>
        new()
        {
            Start = new TugPose(stand.Position, stand.TrueHeading!.Value.Degrees),
            StartsAtStand = true,
            AircraftType = aircraftType,
            Goals = goals,
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
            Forced = false,
        };

    /// <summary>A narrowbody not on a stand and not under tow, sent to <paramref name="goal"/>.</summary>
    private static TugRequest OffStand(TugPose start, TugGoal goal) =>
        new()
        {
            Start = start,
            StartsAtStand = false,
            AircraftType = Narrowbody,
            Goals = [goal],
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
            Forced = false,
        };

    private TugPlan PlanOrFail(AirportGroundLayout layout, TugRequest request)
    {
        var watch = Stopwatch.StartNew();
        TugPlan? plan = TugMovePlanner.Plan(layout, request, out string refusal);
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
        TugPlan? plan = TugMovePlanner.Plan(layout, request, out string refusal);
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
            TugMoveTrace trace = plan.Moves[i];
            TugMove move = trace.Move;
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
        LatLon firstStop = TugMovePlanner.SpotStopGeometry(first, firstDeg, Narrowbody).Stop;
        LatLon secondStop = TugMovePlanner.SpotStopGeometry(second, secondDeg, Narrowbody).Stop;
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
        Assert.Equal(expected, [.. plan.Moves.Select(m => m.Move.Kind)]);

    private static void AssertMoves(TugPlan plan, params (PushbackLegKind Kind, TugMoveShape Shape)[] expected)
    {
        (PushbackLegKind Kind, TugMoveShape Shape)[] planned = [.. plan.Moves.Select(m => (m.Move.Kind, m.Move.Shape))];
        Assert.Equal(expected, planned);
    }

    /// <summary>Every maximal run of same-kind moves without a turn stays within <paramref name="maxDeg"/> of the travel it started with.</summary>
    private void AssertNoLoop(TugPlan plan, double maxDeg)
    {
        int runStart = 0;
        while (runStart < plan.Moves.Count)
        {
            PushbackLegKind kind = plan.Moves[runStart].Move.Kind;
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
            Assert.True(hasTurn || (worstDeg <= maxDeg), $"the {kind} run of moves {runStart + 1}-{runEnd + 1} wandered {worstDeg:F1}°");
            runStart = runEnd + 1;
        }
    }

    /// <summary>
    /// The taxiway's straight centreline edge nearest a stand among those running within
    /// <see cref="TugPlanBuilder.AcrossAngleDeg"/> of its push direction (either way along the edge) whose nearest
    /// point lies behind the stand, and how far behind that point lies.
    /// </summary>
    private static (IGroundEdge Edge, double BehindFt) NearestAlongsideEdge(AirportGroundLayout layout, GroundNode stand, string taxiway)
    {
        double pushRad = (Assert.NotNull(stand.TrueHeading).Degrees + 180.0) * Math.PI / 180.0;
        IEnumerable<(IGroundEdge edge, double behindFt)> candidates =
            from edge in layout.AllEdges
            where (edge is GroundEdge) && edge.MatchesTaxiway(taxiway)
            let a = LocalFt(stand.Position, edge.Nodes[0].Position)
            let b = LocalFt(stand.Position, edge.Nodes[1].Position)
            let dx = b.X - a.X
            let dy = b.Y - a.Y
            let length = Math.Sqrt(Sq(dx) + Sq(dy))
            where length > 0.0
            let t = Math.Clamp(-((a.X * dx) + (a.Y * dy)) / Sq(length), 0.0, 1.0)
            let nearestX = a.X + (t * dx)
            let nearestY = a.Y + (t * dy)
            let behindFt = (nearestX * Math.Sin(pushRad)) + (nearestY * Math.Cos(pushRad))
            let offPushDeg = Math.Acos(Math.Min(1.0, Math.Abs((dx * Math.Sin(pushRad)) + (dy * Math.Cos(pushRad))) / length)) * 180.0 / Math.PI
            where (offPushDeg <= TugPlanBuilder.AcrossAngleDeg) && (behindFt > 0.0)
            orderby Math.Sqrt(Sq(nearestX) + Sq(nearestY))
            select (edge, behindFt);
        return candidates.First();
    }

    /// <summary>The plan ends with the reference point on one of the taxiway's straight centreline edges.</summary>
    private void AssertEndsOnTaxiway(AirportGroundLayout layout, TugPlan plan, string taxiway)
    {
        double offFt = layout
            .AllEdges.Where(e => (e is GroundEdge) && e.MatchesTaxiway(taxiway))
            .Min(e => GeoMath.DistanceToSegmentFt(plan.End.Position, e.Nodes[0].Position, e.Nodes[1].Position));
        _output.WriteLine($"ended {offFt:F2} ft from the nearest taxiway {taxiway} edge");
        Assert.True(offFt <= EndPositionToleranceFt, $"ended {offFt:F2} ft from taxiway {taxiway}, past the {EndPositionToleranceFt} ft tolerance");
    }

    /// <summary>The plan ends on the taxiway's nearest straight centreline edge with the nose along it, either way.</summary>
    private void AssertEndsOnTaxiwayLine(AirportGroundLayout layout, TugPlan plan, string taxiway)
    {
        (GroundEdge Edge, double OffFt) nearest = layout
            .AllEdges.OfType<GroundEdge>()
            .Where(e => e.MatchesTaxiway(taxiway))
            .Select(e => (Edge: e, OffFt: GeoMath.DistanceToSegmentFt(plan.End.Position, e.Nodes[0].Position, e.Nodes[1].Position)))
            .OrderBy(e => e.OffFt)
            .First();
        double lineDeg = GeoMath.BearingTo(nearest.Edge.Nodes[0].Position, nearest.Edge.Nodes[1].Position);
        double noseOffLineDeg = OffLineDeg(plan.End.NoseTrueDeg, lineDeg);
        _output.WriteLine(
            $"taxiway {taxiway} edge {nearest.Edge.Nodes[0].Id}-{nearest.Edge.Nodes[1].Id} on {lineDeg:F1}°: ended {nearest.OffFt:F2} ft off it, "
                + $"nose {noseOffLineDeg:F2}° off it"
        );
        Assert.True(nearest.OffFt <= 1.0, $"ended {nearest.OffFt:F2} ft off the nearest taxiway {taxiway} edge");
        Assert.True(noseOffLineDeg <= 1.0, $"ended with the nose {noseOffLineDeg:F2}° off taxiway {taxiway}'s direction");
    }

    /// <summary>
    /// The plan ends on the taxiway's pavement: the end lies within <see cref="CorridorFt"/> of the extent of at
    /// least one straight centreline edge of the taxiway — projecting between that edge's own ends, within
    /// <see cref="ExtentSlackFt"/>. Reports every edge whose corridor the end is in, and how far off each the nose
    /// ended: where a taxiway bends, the piece the aircraft is abeam of is not the piece its nose is along, so the
    /// nose is asserted against the edge whose centreline the push captured, not against these.
    /// </summary>
    private void AssertEndsInTheTaxiwayCorridor(AirportGroundLayout layout, TugPlan plan, string taxiway)
    {
        var projections = layout
            .AllEdges.OfType<GroundEdge>()
            .Where(e => e.MatchesTaxiway(taxiway))
            .Select(e => Project(plan.End.Position, e))
            .Where(p => p.LengthFt > 0.0)
            .OrderBy(p => p.CrossFt)
            .ToList();
        var inCorridor = projections
            .Where(p => (p.AlongFt >= -ExtentSlackFt) && (p.AlongFt <= (p.LengthFt + ExtentSlackFt)) && (p.CrossFt <= CorridorFt))
            .ToList();
        EdgeProjection nearest = projections[0];
        _output.WriteLine(
            $"nearest taxiway {taxiway} edge {nearest.Edge}: ended {nearest.AlongFt:F1} ft along its {nearest.LengthFt:F1} ft, "
                + $"{nearest.CrossFt:F2} ft off its {nearest.LineDeg:F1}° line; nose {plan.End.NoseTrueDeg:F1}°"
        );
        foreach (EdgeProjection? p in inCorridor)
        {
            _output.WriteLine(
                $"in the {CorridorFt:F0} ft corridor: edge {p.Edge} on {p.LineDeg:F1}°, {p.AlongFt:F1} ft along its "
                    + $"{p.LengthFt:F1} ft, {p.CrossFt:F2} ft off its line, nose {OffLineDeg(plan.End.NoseTrueDeg, p.LineDeg):F2}° off it"
            );
        }

        Assert.True(
            inCorridor.Count > 0,
            $"ended outside the {CorridorFt:F0} ft corridor of every taxiway {taxiway} edge; the nearest ({nearest.Edge}) "
                + $"projects {nearest.AlongFt:F1} ft along its {nearest.LengthFt:F1} ft, {nearest.CrossFt:F2} ft off its line"
        );
    }

    /// <summary>
    /// Where a point falls on an edge: how far along from its first node, the edge's length, how far off its line,
    /// feet, and the direction the edge runs, degrees true.
    /// </summary>
    private static EdgeProjection Project(LatLon point, GroundEdge edge)
    {
        (double X, double Y) a = LocalFt(point, edge.Nodes[0].Position);
        (double X, double Y) b = LocalFt(point, edge.Nodes[1].Position);
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthFt = Math.Sqrt(Sq(dx) + Sq(dy));
        string name = $"{edge.Nodes[0].Id}-{edge.Nodes[1].Id}";
        if (lengthFt <= 0.0)
        {
            return new EdgeProjection(name, 0.0, 0.0, double.PositiveInfinity, 0.0);
        }

        return new EdgeProjection(
            name,
            -((a.X * dx) + (a.Y * dy)) / lengthFt,
            lengthFt,
            Math.Abs((a.X * dy) - (a.Y * dx)) / lengthFt,
            GeoMath.BearingTo(edge.Nodes[0].Position, edge.Nodes[1].Position)
        );
    }

    private void AssertEndsOnSpot(AirportGroundLayout layout, TugPlan plan, GroundNode spot) => AssertEndsOnSpot(layout, plan, spot, Narrowbody);

    private void AssertEndsOnSpot(AirportGroundLayout layout, TugPlan plan, GroundNode spot, string aircraftType)
    {
        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double facingDeg), $"spot {spot.Name} has no nose-out heading");
        LatLon stop = TugMovePlanner.SpotStopGeometry(spot, facingDeg, aircraftType).Stop;
        double endFt = FeetBetween(plan.End.Position, stop);
        double endDeg = AbsDiffDeg(plan.End.NoseTrueDeg, facingDeg);
        _output.WriteLine($"spot {spot.Name} nose-out {facingDeg:F1}°: ended {endFt:F2} ft from the stop point, nose {endDeg:F2}° off");
        Assert.True(endFt <= EndPositionToleranceFt, $"ended {endFt:F2} ft from spot {spot.Name}'s stop point");
        Assert.True(endDeg <= EndFacingToleranceDeg, $"ended with the nose {endDeg:F2}° off spot {spot.Name}'s nose-out heading");
    }

    private static double AbsDiffDeg(double a, double b) => new TrueHeading(a).AbsAngleTo(new TrueHeading(b));

    /// <summary>How far a heading is off a line, whichever way along the line: 0° to 90°.</summary>
    private static double OffLineDeg(double headingDeg, double lineDeg) =>
        Math.Min(AbsDiffDeg(headingDeg, lineDeg), AbsDiffDeg(headingDeg, lineDeg + 180.0));

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

    /// <summary>
    /// Where a point falls on one edge, for the corridor assertion: the edge's node pair, the projection in feet, and
    /// the direction the edge runs, degrees true.
    /// </summary>
    private sealed record EdgeProjection(string Edge, double AlongFt, double LengthFt, double CrossFt, double LineDeg);
}
