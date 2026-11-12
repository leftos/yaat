using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// How a tug move to a node is judged against the movement-area taxiways at that node, on the real SFO layout: the
/// overshoot past each of the node's movement-area taxiways (the taxiway the aircraft starts on excepted), beside the
/// movement-area crossing rule. The planner rows plan a request and read its verdict. The check rows (<c>Check_</c>) fly
/// a move list the test chooses from the node's real geometry and judge it with <see cref="TugPathCheck"/> directly: a
/// node goal's own arrival ends on its node, on the taxiway's centreline, so only a path the test chooses can be made to
/// swing past the taxiway.
/// </summary>
public class TugNodeGoalOvershootTests
{
    private const string Narrowbody = "B738";
    private const string Regional = "CRJ7";

    /// <summary>How far from the node a check row's trace starts, feet: the start fuselage clears every edge at the node.</summary>
    private const double StartBackFt = 250.0;

    /// <summary>The least angle between a taxiway's two edges at a node for the taxiway to run straight through it, degrees.</summary>
    private const double ThroughMinDeg = 170.0;

    /// <summary>The crossing angle a junction's two taxiways meet at, degrees: within this of square.</summary>
    private const double JunctionSquareToleranceDeg = 20.0;

    /// <summary>How far off the junction's first taxiway the junction trace runs, degrees.</summary>
    private const double OffFirstTaxiwayDeg = 20.0;

    /// <summary>How far beyond the node a refused trace runs, as a multiple of the overshoot allowance.</summary>
    private const double RefusedPastFactor = 1.5;

    /// <summary>How far beyond the node an accepted junction trace runs, as a multiple of the overshoot allowance.</summary>
    private const double AcceptedPastFactor = 0.6;

    /// <summary>How clear of the allowance a staged overshoot figure must be, feet, so rounding cannot flip it.</summary>
    private const double AllowanceMarginFt = 5.0;

    /// <summary>How close to the node a trace must end, feet.</summary>
    private const double EndOnNodeToleranceFt = 3.0;

    /// <summary>How far down taxiway A from the start node the start-taxiway row's goal node lies at least, feet.</summary>
    private const double AlongTaxiwayFt = 250.0;

    /// <summary>How far past square to the goal the start-taxiway row's nose points, degrees: the goal lies behind, so it is a push.</summary>
    private const double PastSquareDeg = 5.0;

    /// <summary>How far down the D15 push line past the taxiway A node the third-taxiway row looks for a taxiway B node, feet.</summary>
    private const double BeyondAlphaFt = 400.0;

    /// <summary>How far out past taxiway A, and back past it, the out-and-back trace runs, feet: well clear of a B738's fuselage.</summary>
    private const double OutAndBackFt = 150.0;

    /// <summary>How far off taxiway A's centreline a lined-up start sits, feet: within the on-taxiway corridor.</summary>
    private const double LinedUpOffsetFt = 5.0;

    /// <summary>How far ahead of a lined-up start its swing reaches along A, feet.</summary>
    private const double SwingAheadFt = 200.0;

    /// <summary>How far down A from the goal node the crossing-away trace starts, feet.</summary>
    private const double AwayFromNodeFt = 200.0;

    /// <summary>How far short of A, on the ramp side, the crossing-away trace starts and ends, feet.</summary>
    private const double ShortOfTaxiwayFt = 100.0;

    /// <summary>How far the crossing-away trace pushes toward A, feet: the centre stays on the ramp side, the tail crosses A.</summary>
    private const double TailAcrossPushFt = 50.0;

    private static readonly IReadOnlySet<string> OnlyA = new HashSet<string>(["A"], StringComparer.OrdinalIgnoreCase);

    private readonly ITestOutputHelper _output;

    public TugNodeGoalOvershootTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("TugMovePlanner", LogLevel.Debug)
            .EnableCategory("TugPathCheck", LogLevel.Debug)
            .InitializeSimLog();
    }

    /// <summary>
    /// A node on a ramp lane (spot 6A's node: no movement-area edge) from D15: no taxiway at the node to judge, and the plan
    /// is accepted.
    /// </summary>
    [Fact]
    public void NodeOnAnApronLane_FromD15_Accepted()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode sixA = Spot(layout, "6A");
        Assert.Empty(MovementBearings(sixA, Pavement(layout)));

        TugPlan plan = PlanOrFail(layout, StandStart(layout, "D15", Narrowbody, TugGoal.AtNode(sixA, facingTrueDeg: null)));

        Assert.True(FeetBetween(plan.End.Position, sixA.Position) <= EndOnNodeToleranceFt, "the move did not end on spot 6A's node");
    }

    /// <summary>
    /// A node on taxiway A alone (A runs straight through it), from D15: the push ends on the node with the centre never
    /// more than the allowance past A, and is accepted.
    /// </summary>
    [Fact]
    public void NodeOnOneMovementAreaTaxiway_ParkedWithinTheAllowance_Accepted()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        (GroundNode alpha, double alongDeg) = ThroughNodeOfA(layout);
        var footprint = AircraftFootprint.FromType(Narrowbody);
        TugRequest request = StandStart(layout, "D15", Narrowbody, TugGoal.AtNode(alpha, facingTrueDeg: null));

        TugPlan plan = PlanOrFail(layout, request);

        double pastFt = PastLineFt(PlanPositions(plan), alpha.Position, alongDeg, request.Start.Position);
        _output.WriteLine($"centre reaches {pastFt:F1} ft past A; allowance {TugPathCheck.MaxTaxiwayOvershootFt(footprint):F1} ft");
        Assert.True(pastFt <= TugPathCheck.MaxTaxiwayOvershootFt(footprint), $"the accepted push ran {pastFt:F1} ft past A");
        Assert.True(FeetBetween(plan.End.Position, alpha.Position) <= EndOnNodeToleranceFt, $"the move did not end on node {alpha.Id}");
    }

    /// <summary>
    /// The check on a node goal on taxiway A: a trace that pushes straight across A from the ramp, half again the allowance
    /// past it, and pulls back onto the node is refused for the overshoot, naming A; a forced tow's skipped-rule notes name
    /// the same overshoot.
    /// </summary>
    [Fact]
    public void Check_NodeGoal_TraceSwingsPastItsTaxiway_RefusedForOvershoot()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        (GroundNode alpha, PushbackPose start, AircraftFootprint footprint, IReadOnlyList<TugMoveTrace> traces) = PastAAndBack(layout);

        (TugPathRefusal? refusal, List<TugPathRefusal> skipped) = Judge(layout, start, footprint, TugGoal.AtNode(alpha, facingTrueDeg: null), traces);

        TugPathRefusal refused = Assert.NotNull(refusal);
        _output.WriteLine($"refusal: {refused.Message}");
        Assert.Equal(TugPathSeverity.TaxiwayOvershoot, refused.Severity);
        Assert.Equal("A", refused.Taxiway);
        Assert.Matches(@"^Unable, the move to .+ would take the aircraft \d+ ft past taxiway A$", refused.Message);
        Assert.Contains(skipped, s => (s.Severity == TugPathSeverity.TaxiwayOvershoot) && (s.Taxiway == "A"));
    }

    /// <summary>
    /// The same trace as a forced tow's skipped-rule notes: it runs past A and its fuselage also comes back across A after
    /// leaving it, but A is named once, for the overshoot.
    /// </summary>
    [Fact]
    public void Check_ForcedNodeTow_TraceSwingsPastItsTaxiway_OneNoteForIt()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        (GroundNode alpha, PushbackPose start, AircraftFootprint footprint, IReadOnlyList<TugMoveTrace> traces) = PastAAndBack(layout);

        (_, List<TugPathRefusal> skipped) = Judge(layout, start, footprint, TugGoal.AtNode(alpha, facingTrueDeg: null), traces);

        _output.WriteLine($"notes: {string.Join(" | ", skipped.Select(s => s.Message))}");
        TugPathRefusal note = Assert.Single(skipped, s => s.Taxiway == "A");
        Assert.Equal(TugPathSeverity.TaxiwayOvershoot, note.Severity);
    }

    /// <summary>
    /// The check on a node goal whose trace starts lined up on its own taxiway — a B738 on A's line, 5 ft off the
    /// centreline with its nose along A, within the on-taxiway corridor though its fuselage crosses no A edge — pulls ahead
    /// and out past A by more than the allowance, and pushes back across A onto the node: A is the taxiway it starts on, so
    /// it is neither judged for overshoot nor refused for crossing, and the trace is accepted.
    /// </summary>
    [Fact]
    public void Check_NodeGoal_StartLinedUpOnItsTaxiway_TraceSwingsPastIt_Accepted()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        (GroundNode alpha, double alongDeg) = ThroughNodeOfA(layout);
        var footprint = AircraftFootprint.FromType(Narrowbody);
        double allowanceFt = TugPathCheck.MaxTaxiwayOvershootFt(footprint);
        double rampSideDeg = RampSideDeg(layout, alpha, alongDeg);
        double noseDeg = new TrueHeading(alongDeg + 180.0).Degrees;
        var start = new PushbackPose(Project(alpha.Position, rampSideDeg, LinedUpOffsetFt), noseDeg);
        LatLon outPast = Project(Project(start.Position, noseDeg, SwingAheadFt), rampSideDeg + 180.0, RefusedPastFactor * allowanceFt);
        IReadOnlyList<TugMoveTrace> traces = Fly(
            start,
            footprint,
            TugMove.ToPoint(PushbackLegKind.Pull, outPast),
            TugMove.ToPoint(PushbackLegKind.Push, alpha.Position)
        );

        Assert.True(LinedUpOffsetFt <= TugPlanBuilder.OnTaxiwayCorridorFt, "the start is off taxiway A's corridor");
        AssertEndsOn(traces, alpha);
        double pastFt = PastLineFt(TracePositions(traces), alpha.Position, alongDeg, start.Position);
        _output.WriteLine($"node {alpha.Id}: the trace reaches {pastFt:F1} ft past A; allowance {allowanceFt:F1} ft");
        Assert.True(pastFt > allowanceFt + AllowanceMarginFt, $"the staged trace reaches only {pastFt:F1} ft past A");

        (TugPathRefusal? refusal, _) = Judge(layout, start, footprint, TugGoal.AtNode(alpha, facingTrueDeg: null), traces);

        Assert.True(refusal is null, $"the trace was refused: {refusal?.Message}");
    }

    /// <summary>
    /// A B738 lined up on taxiway A as a <c>PUSH A FACE …</c> leaves it — on A's line, 5 ft off the centreline, nose along
    /// A — pushed to an A node at least 250 ft down the taxiway behind it: A is the taxiway it starts on, so the push is
    /// accepted.
    /// </summary>
    [Fact]
    public void StartLinedUpOnTheNodesTaxiway_Accepted()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        (GroundNode alpha, double alongDeg) = ThroughNodeOfA(layout);
        GroundNode goalNode = NodeDownA(layout, alpha, alongDeg + 180.0);
        double rampSideDeg = RampSideDeg(layout, alpha, alongDeg);
        var start = new PushbackPose(Project(alpha.Position, rampSideDeg, LinedUpOffsetFt), alongDeg);
        _output.WriteLine(
            $"start abeam node {alpha.Id} nose {alongDeg:F1}°, goal node {goalNode.Id} {FeetBetween(alpha.Position, goalNode.Position):F0} ft"
        );

        TugPlan plan = PlanOrFail(layout, OffStand(layout, start, Narrowbody, TugGoal.AtNode(goalNode, facingTrueDeg: null)));

        Assert.True(FeetBetween(plan.End.Position, goalNode.Position) <= EndOnNodeToleranceFt, $"the move did not end on node {goalNode.Id}");
    }

    /// <summary>
    /// The check on a node goal on a ramp lane (spot 6A's node) whose trace starts across taxiway A, leaves it and comes
    /// back across it onto the ramp: A is the taxiway the move starts on, exempt from the crossing rule by name however
    /// the move leaves and rejoins it, so the trace is accepted.
    /// </summary>
    [Fact]
    public void Check_NodeGoal_TraceComesBackAcrossItsStartTaxiway_Accepted()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode sixA = Spot(layout, "6A");
        Assert.Empty(MovementBearings(sixA, Pavement(layout)));
        (_, PushbackPose start, AircraftFootprint footprint, IReadOnlyList<TugMoveTrace> traces) = OutAndBackAcrossA(layout);

        (TugPathRefusal? refusal, _) = Judge(layout, start, footprint, TugGoal.AtNode(sixA, facingTrueDeg: null), traces);

        Assert.True(refusal is null, $"the trace was refused: {refusal?.Message}");
    }

    /// <summary>
    /// The check on a bare marked point (<c>~lat/lon</c>, no facing) at the end of the same out-and-back trace across
    /// taxiway A: a marked point has no pavement exempt, so coming back across A after leaving it is refused for A.
    /// </summary>
    [Fact]
    public void Check_BareMarkedPoint_TraceComesBackAcrossItsStartTaxiway_RefusedForIt()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        (_, PushbackPose start, AircraftFootprint footprint, IReadOnlyList<TugMoveTrace> traces) = OutAndBackAcrossA(layout);
        LatLon end = traces[^1].End.Position;
        var marked = TugGoal.FreePose(VirtualNode.Create(end.Lat, end.Lon), facingWord: null, facingTrueDeg: null, label: "the marked point");

        (TugPathRefusal? refusal, _) = Judge(layout, start, footprint, marked, traces);

        TugPathRefusal refused = Assert.NotNull(refusal);
        _output.WriteLine($"refusal: {refused.Message}");
        Assert.Equal(TugPathSeverity.MovementArea, refused.Severity);
        Assert.Equal("A", refused.Taxiway);
    }

    /// <summary>
    /// The check on a node goal on taxiway A whose trace, 200 ft down A from the node, pushes toward A from the ramp until
    /// the tail lies across A with the centre still short of it, then pulls back: the node's own taxiway is not exempt
    /// from the crossing rule by name, so crossing it away from the node is refused for A.
    /// </summary>
    [Fact]
    public void Check_NodeGoal_TraceCrossesItsOwnTaxiwayAwayFromTheNode_RefusedForIt()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        (GroundNode alpha, double alongDeg) = ThroughNodeOfA(layout);
        var footprint = AircraftFootprint.FromType(Narrowbody);
        double rampSideDeg = RampSideDeg(layout, alpha, alongDeg);
        LatLon abeam = Project(Project(alpha.Position, alongDeg + 180.0, AwayFromNodeFt), rampSideDeg, ShortOfTaxiwayFt);
        var start = new PushbackPose(abeam, rampSideDeg);
        IReadOnlyList<TugMoveTrace> traces = Fly(
            start,
            footprint,
            TugMove.Straight(PushbackLegKind.Push, TailAcrossPushFt),
            TugMove.Straight(PushbackLegKind.Pull, TailAcrossPushFt)
        );
        Assert.True(ShortOfTaxiwayFt - TailAcrossPushFt < footprint.LengthFt / 2.0, "the tail never reaches taxiway A");
        Assert.True(ShortOfTaxiwayFt - TailAcrossPushFt > 0.0, "the centre reaches taxiway A");

        (TugPathRefusal? refusal, _) = Judge(layout, start, footprint, TugGoal.AtNode(alpha, facingTrueDeg: null), traces);

        TugPathRefusal refused = Assert.NotNull(refusal);
        _output.WriteLine($"refusal: {refused.Message}");
        Assert.Equal(TugPathSeverity.MovementArea, refused.Severity);
        Assert.Equal("A", refused.Taxiway);
    }

    /// <summary>
    /// A spot with no outbound heading or a stand with no heading (the first at SFO, else OAK), asked for as a bare goal, is
    /// resolved as it always is for those goals, not as a node goal: the names of its node's edges are exempt from the
    /// crossing rule, no taxiway is judged by its overshoot, and the start taxiway gets no exemption by name.
    /// </summary>
    [Fact]
    public void NoHeadingSpotOrStand_NotJudgedAsANodeGoal()
    {
        if ((LoadSfo() is not { } sfo) || (new TestAirportGroundData().GetLayout("OAK") is not { } oak))
        {
            return;
        }

        (AirportGroundLayout layout, TugGoal goal) =
            NoHeadingGoal(sfo) is { } atSfo ? (sfo, atSfo)
            : NoHeadingGoal(oak) is { } atOak ? (oak, atOak)
            : throw new InvalidOperationException("neither SFO nor OAK has a spot with no outbound heading or a stand with no heading");
        GroundNode node = goal.Node!;
        _output.WriteLine($"{layout.AirportId} {goal.Label} (node {node.Id})");
        var start = new PushbackPose(Project(node.Position, 0.0, StartBackFt), 0.0);

        ResolvedTugGoal resolved = new TugPlanBuilder(layout, OffStand(layout, start, Narrowbody, goal)).ResolvedGoal(0);

        var edgeNames = new HashSet<string>(node.Edges.SelectMany(RampLaneReposition.EdgeNames), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(TugGoalShape.ToNode, resolved.Shape);
        Assert.True(edgeNames.SetEquals(resolved.ExemptNames), $"exempt {string.Join("/", resolved.ExemptNames)}");
        Assert.Empty(resolved.OvershootTaxiways);
        Assert.False(resolved.Judging.NodeGoal);
    }

    /// <summary>The first spot with no outbound heading, else the first stand with no heading, by name, as a bare goal; null when there is none.</summary>
    private static TugGoal? NoHeadingGoal(AirportGroundLayout layout)
    {
        GroundNode? spot = layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Spot) && (n.Name is not null) && !layout.TryGetSpotOutboundHeading(n, out _))
            .MinBy(n => n.Name, StringComparer.Ordinal);
        if (spot is not null)
        {
            return TugGoal.Spot(spot);
        }

        GroundNode? stand = layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null) && (n.TrueHeading is null))
            .MinBy(n => n.Name, StringComparer.Ordinal);
        return stand is null ? null : TugGoal.Stand(stand);
    }

    /// <summary>
    /// A B738 on the ramp 250 ft short of the A node nearest spot 6A, tail to A, pushed straight across A half again the
    /// allowance past it and pulled back onto the node.
    /// </summary>
    private (GroundNode Alpha, PushbackPose Start, AircraftFootprint Footprint, IReadOnlyList<TugMoveTrace> Traces) PastAAndBack(
        AirportGroundLayout layout
    )
    {
        (GroundNode alpha, double alongDeg) = ThroughNodeOfA(layout);
        var footprint = AircraftFootprint.FromType(Narrowbody);
        double allowanceFt = TugPathCheck.MaxTaxiwayOvershootFt(footprint);
        double rampSideDeg = RampSideDeg(layout, alpha, alongDeg);
        var start = new PushbackPose(Project(alpha.Position, rampSideDeg, StartBackFt), rampSideDeg);
        double beyondFt = RefusedPastFactor * allowanceFt;
        IReadOnlyList<TugMoveTrace> traces = Fly(
            start,
            footprint,
            TugMove.Straight(PushbackLegKind.Push, StartBackFt + beyondFt),
            TugMove.Straight(PushbackLegKind.Pull, beyondFt)
        );

        Assert.True(StartBackFt > footprint.LengthFt / 2.0, "the start fuselage reaches taxiway A");
        AssertEndsOn(traces, alpha);
        double pastFt = PastLineFt(TracePositions(traces), alpha.Position, alongDeg, start.Position);
        _output.WriteLine($"node {alpha.Id}: the trace reaches {pastFt:F1} ft past A; allowance {allowanceFt:F1} ft");
        Assert.True(pastFt > allowanceFt + AllowanceMarginFt, $"the staged trace reaches only {pastFt:F1} ft past A");
        return (alpha, start, footprint, traces);
    }

    /// <summary>
    /// The check on a node goal at the junction of two movement-area taxiways that cross near square: a trace that runs in
    /// along a line 20° off the first, half again the allowance beyond the node, and pulls back onto it is within the
    /// allowance of the first and well past the second, and is refused for the second.
    /// </summary>
    [Fact]
    public void Check_NodeGoal_TraceSwingsPastOneOfItsJunctionTaxiways_RefusedForThatTaxiway()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        JunctionTrace junction = FlyThroughJunction(layout, RefusedPastFactor);
        double allowanceFt = TugPathCheck.MaxTaxiwayOvershootFt(junction.Footprint);
        Assert.True(junction.PastAlongFt < allowanceFt - AllowanceMarginFt, $"the trace reaches {junction.PastAlongFt:F1} ft past {junction.Along}");
        Assert.True(
            junction.PastAcrossFt > allowanceFt + AllowanceMarginFt,
            $"the trace reaches only {junction.PastAcrossFt:F1} ft past {junction.Across}"
        );

        (TugPathRefusal? refusal, List<TugPathRefusal> skipped) = Judge(
            layout,
            junction.Start,
            junction.Footprint,
            TugGoal.AtNode(junction.Node, facingTrueDeg: null),
            junction.Traces
        );

        TugPathRefusal refused = Assert.NotNull(refusal);
        _output.WriteLine($"refusal: {refused.Message}");
        Assert.Equal(TugPathSeverity.TaxiwayOvershoot, refused.Severity);
        Assert.Equal(junction.Across, refused.Taxiway);
        Assert.DoesNotContain(skipped, s => (s.Severity == TugPathSeverity.TaxiwayOvershoot) && (s.Taxiway == junction.Along));
    }

    /// <summary>
    /// The junction trace again, beyond the node by only 0.6 of the allowance: inside the allowance of both taxiways, so
    /// nothing at the node is overshot and the trace is accepted.
    /// </summary>
    [Fact]
    public void Check_NodeGoal_TraceInsideBothJunctionAllowances_Accepted()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        JunctionTrace junction = FlyThroughJunction(layout, AcceptedPastFactor);
        double allowanceFt = TugPathCheck.MaxTaxiwayOvershootFt(junction.Footprint);
        Assert.True(junction.PastAlongFt < allowanceFt - AllowanceMarginFt, $"the trace reaches {junction.PastAlongFt:F1} ft past {junction.Along}");
        Assert.True(
            junction.PastAcrossFt < allowanceFt - AllowanceMarginFt,
            $"the trace reaches {junction.PastAcrossFt:F1} ft past {junction.Across}"
        );

        (TugPathRefusal? refusal, _) = Judge(
            layout,
            junction.Start,
            junction.Footprint,
            TugGoal.AtNode(junction.Node, facingTrueDeg: null),
            junction.Traces
        );

        Assert.True(refusal is null, $"the trace was refused: {refusal?.Message}");
    }

    /// <summary>
    /// A node on taxiway B reached from D15 straight across taxiway A: A is neither the node's taxiway nor the start's, so
    /// the movement-area crossing rule refuses the move for A.
    /// </summary>
    [Fact]
    public void PathToANodeAcrossAThirdTaxiway_RefusedByTheCrossingRule()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode d15 = Parking(layout, "D15");
        (GroundNode alpha, _) = ThroughNodeOfA(layout);
        double pushDeg = GeoMath.BearingTo(d15.Position, alpha.Position);
        LatLon beyond = Project(alpha.Position, pushDeg, BeyondAlphaFt);
        PavementClassifier pavement = Pavement(layout);
        GroundNode bravo =
            layout
                .Nodes.Values.Where(n =>
                    MovementBearings(n, pavement) is { Count: 1 } b && b.TryGetValue("B", out List<double>? onB) && RunsThrough(onB)
                )
                .MinBy(n => FeetBetween(n.Position, beyond))
            ?? throw new InvalidOperationException("SFO has no node on taxiway B alone");
        _output.WriteLine($"taxiway B node {bravo.Id} is {FeetBetween(d15.Position, bravo.Position):F0} ft from D15");

        string refusal = Refusal(layout, StandStart(layout, "D15", Narrowbody, TugGoal.AtNode(bravo, facingTrueDeg: null)));

        Assert.Matches(@"would put the aircraft on taxiway A$", refusal);
    }

    /// <summary>
    /// A CRJ7 held across taxiway A, its centre on an A node, pushed to an A node at least 250 ft down the taxiway: the push swings
    /// out past A by more than the allowance before it turns down the taxiway, but A is the taxiway it starts on, so it is
    /// not judged and the move is accepted. Nor does the crossing rule refuse the push for coming back across A after it has
    /// left it: the start taxiway is exempt from that rule by name.
    /// </summary>
    [Fact]
    public void StartTaxiwayIsTheNodesTaxiway_NotJudged_Accepted()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        (GroundNode alpha, double alongDeg) = ThroughNodeOfA(layout);
        GroundNode goalNode = NodeDownA(layout, alpha, alongDeg + 180.0);
        double toGoalDeg = GeoMath.BearingTo(alpha.Position, goalNode.Position);
        double rampSideDeg = RampSideDeg(layout, alpha, alongDeg);
        double noseDeg = new TrueHeading(toGoalDeg + (Math.Sign(DeltaDeg(toGoalDeg, rampSideDeg)) * (90.0 + PastSquareDeg))).Degrees;
        var footprint = AircraftFootprint.FromType(Regional);
        var start = new PushbackPose(alpha.Position, noseDeg);
        _output.WriteLine(
            $"start on node {alpha.Id} nose {noseDeg:F1}°, goal node {goalNode.Id} {FeetBetween(alpha.Position, goalNode.Position):F0} ft down A"
        );

        TugPlan plan = PlanOrFail(layout, OffStand(layout, start, Regional, TugGoal.AtNode(goalNode, facingTrueDeg: null)));

        double swungFt = PlanPositions(plan).Max(p => Math.Abs(CrossTrackFt(p, alpha.Position, alongDeg)));
        _output.WriteLine($"the push swings {swungFt:F1} ft off A; allowance {TugPathCheck.MaxTaxiwayOvershootFt(footprint):F1} ft");
        Assert.True(swungFt > TugPathCheck.MaxTaxiwayOvershootFt(footprint), $"the staged push swings only {swungFt:F1} ft off A");
    }

    /// <summary>The flown-path check's verdict on a node goal's trace, and a forced tow's skipped-rule notes for it.</summary>
    private static (TugPathRefusal? Refusal, List<TugPathRefusal> Skipped) Judge(
        AirportGroundLayout layout,
        PushbackPose start,
        AircraftFootprint footprint,
        TugGoal goal,
        IReadOnlyList<TugMoveTrace> traces
    )
    {
        TugRequest request = OffStand(layout, start, footprint, goal);
        ResolvedTugGoal resolved = new TugPlanBuilder(layout, request).ResolvedGoal(0);
        var check = new TugPathCheck(layout, request.MovementArea!, footprint, start.Position);
        string? markedPoint = goal.Kind == TugGoalKind.FreePose ? resolved.Name : null;
        TugPathRefusal? refusal = check.Check(traces, resolved.Judging, (resolved.Subject, markedPoint), forced: false);
        List<TugPathRefusal> skipped = check.SkippedTaxiwayHits(traces, resolved.Judging, (resolved.Subject, markedPoint));
        return (refusal, skipped);
    }

    /// <summary>
    /// A B738 across taxiway A, its centre on the A node nearest spot 6A and its nose to the ramp side, pushed straight
    /// <see cref="OutAndBackFt"/> out past A, clear of it, then pulled straight back across A as far again onto the ramp
    /// side: the trace leaves the taxiway it starts on and comes back across it, ending clear of it.
    /// </summary>
    private (GroundNode Alpha, PushbackPose Start, AircraftFootprint Footprint, IReadOnlyList<TugMoveTrace> Traces) OutAndBackAcrossA(
        AirportGroundLayout layout
    )
    {
        (GroundNode alpha, double alongDeg) = ThroughNodeOfA(layout);
        var footprint = AircraftFootprint.FromType(Narrowbody);
        var start = new PushbackPose(alpha.Position, RampSideDeg(layout, alpha, alongDeg));
        IReadOnlyList<TugMoveTrace> traces = Fly(
            start,
            footprint,
            TugMove.Straight(PushbackLegKind.Push, OutAndBackFt),
            TugMove.Straight(PushbackLegKind.Pull, 2.0 * OutAndBackFt)
        );
        Assert.True(OutAndBackFt > footprint.LengthFt / 2.0, "the trace's fuselage never leaves taxiway A");
        double endOffFt = Math.Abs(CrossTrackFt(traces[^1].End.Position, alpha.Position, alongDeg));
        _output.WriteLine($"node {alpha.Id}: the trace ends {endOffFt:F1} ft off A on the ramp side");
        Assert.True(endOffFt > footprint.LengthFt / 2.0, "the trace ends with its fuselage on taxiway A");
        return (alpha, start, footprint, traces);
    }

    /// <summary>The A-only node at least <see cref="AlongTaxiwayFt"/> from <paramref name="alpha"/>, nearest that far down A on <paramref name="bearingDeg"/>.</summary>
    private static GroundNode NodeDownA(AirportGroundLayout layout, GroundNode alpha, double bearingDeg)
    {
        PavementClassifier pavement = Pavement(layout);
        LatLon downA = Project(alpha.Position, bearingDeg, AlongTaxiwayFt);
        return layout
                .Nodes.Values.Where(n => (FeetBetween(n.Position, alpha.Position) >= AlongTaxiwayFt) && IsThroughNodeOfAOnly(n, pavement))
                .MinBy(n => FeetBetween(n.Position, downA))
            ?? throw new InvalidOperationException("SFO has no second node on taxiway A alone");
    }

    /// <summary>
    /// The junction trace: the SFO node nearest spot 6A where two movement-area taxiways run straight through and cross
    /// within 20° of square, entered along a line 20° off the alphabetically first of them, <paramref name="pastFactor"/>
    /// times the B738's allowance beyond the node, then pulled back onto it.
    /// </summary>
    private JunctionTrace FlyThroughJunction(AirportGroundLayout layout, double pastFactor)
    {
        PavementClassifier pavement = Pavement(layout);
        LatLon sixA = Spot(layout, "6A").Position;
        GroundNode node =
            layout.Nodes.Values.Where(n => IsSquareJunction(MovementBearings(n, pavement))).MinBy(n => FeetBetween(n.Position, sixA))
            ?? throw new InvalidOperationException("SFO has no square junction of two movement-area taxiways");
        Dictionary<string, List<double>> bearings = MovementBearings(node, pavement);
        string[] names = [.. bearings.Keys.Order(StringComparer.Ordinal)];
        (string along, string across) = (names[0], names[1]);
        double alongDeg = bearings[along][0];
        double travelDeg = new TrueHeading(alongDeg + OffFirstTaxiwayDeg).Degrees;
        var footprint = AircraftFootprint.FromType(Narrowbody);
        double beyondFt = pastFactor * TugPathCheck.MaxTaxiwayOvershootFt(footprint);
        double backDeg = new TrueHeading(travelDeg + 180.0).Degrees;
        var start = new PushbackPose(Project(node.Position, backDeg, StartBackFt), backDeg);
        IReadOnlyList<TugMoveTrace> traces = Fly(
            start,
            footprint,
            TugMove.Straight(PushbackLegKind.Push, StartBackFt + beyondFt),
            TugMove.Straight(PushbackLegKind.Pull, beyondFt)
        );
        AssertEndsOn(traces, node);
        List<LatLon> positions = TracePositions(traces);
        double pastAlongFt = PastLineFt(positions, node.Position, alongDeg, start.Position);
        double pastAcrossFt = PastLineFt(positions, node.Position, bearings[across][0], start.Position);
        _output.WriteLine(
            $"junction node {node.Id} of {along} and {across}: the trace reaches {pastAlongFt:F1} ft past {along}, "
                + $"{pastAcrossFt:F1} ft past {across}; allowance {TugPathCheck.MaxTaxiwayOvershootFt(footprint):F1} ft"
        );
        return new JunctionTrace(node, along, across, start, footprint, traces, pastAlongFt, pastAcrossFt);
    }

    private static bool IsSquareJunction(Dictionary<string, List<double>> bearings)
    {
        if ((bearings.Count != 2) || !bearings.Values.All(RunsThrough))
        {
            return false;
        }

        List<double>[] lines = [.. bearings.Values];
        double crossingDeg = AbsDiffDeg(lines[0][0], lines[1][0]);
        return Math.Abs(Math.Min(crossingDeg, 180.0 - crossingDeg) - 90.0) <= JunctionSquareToleranceDeg;
    }

    /// <summary>The SFO node nearest spot 6A that taxiway A alone runs straight through, and A's direction there.</summary>
    private static (GroundNode Node, double AlongDeg) ThroughNodeOfA(AirportGroundLayout layout)
    {
        PavementClassifier pavement = Pavement(layout);
        LatLon sixA = Spot(layout, "6A").Position;
        GroundNode node =
            layout.Nodes.Values.Where(n => IsThroughNodeOfAOnly(n, pavement)).MinBy(n => FeetBetween(n.Position, sixA))
            ?? throw new InvalidOperationException("SFO has no node taxiway A alone runs through");
        Dictionary<string, List<double>> bearings = MovementBearings(node, pavement);
        Assert.Equal(OnlyA, bearings.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        return (node, bearings["A"][0]);
    }

    private static bool IsThroughNodeOfAOnly(GroundNode node, PavementClassifier pavement) =>
        MovementBearings(node, pavement) is { Count: 1 } bearings && bearings.TryGetValue("A", out List<double>? onA) && RunsThrough(onA);

    /// <summary>The bearing off taxiway A at <paramref name="node"/> toward the ramp side, the side spot 6A is on.</summary>
    private static double RampSideDeg(AirportGroundLayout layout, GroundNode node, double alongDeg)
    {
        double sixAFt = CrossTrackFt(Spot(layout, "6A").Position, node.Position, alongDeg);
        return new TrueHeading(alongDeg + (sixAFt >= 0.0 ? 90.0 : -90.0)).Degrees;
    }

    /// <summary>Each movement-area taxiway at the node and the bearings of its edges away from it.</summary>
    private static Dictionary<string, List<double>> MovementBearings(GroundNode node, PavementClassifier pavement)
    {
        var bearings = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (IGroundEdge edge in node.Edges)
        {
            if (pavement.MovementAreaName(edge) is not { } name)
            {
                continue;
            }

            GroundNode far = edge.Nodes[0].Id == node.Id ? edge.Nodes[1] : edge.Nodes[0];
            if (!bearings.TryGetValue(name, out List<double>? list))
            {
                list = [];
                bearings[name] = list;
            }

            list.Add(GeoMath.BearingTo(node.Position, far.Position));
        }

        return bearings;
    }

    private static bool RunsThrough(List<double> bearings) => (bearings.Count == 2) && (AbsDiffDeg(bearings[0], bearings[1]) >= ThroughMinDeg);

    /// <summary>
    /// The farthest any point gets past the line through <paramref name="origin"/> along <paramref name="lineDeg"/>, on
    /// the side away from <paramref name="start"/>, feet; zero when none crosses it.
    /// </summary>
    private static double PastLineFt(IEnumerable<LatLon> points, LatLon origin, double lineDeg, LatLon start)
    {
        double startSide = Math.Sign(CrossTrackFt(start, origin, lineDeg));
        return Math.Max(0.0, points.Max(p => -startSide * CrossTrackFt(p, origin, lineDeg)));
    }

    /// <summary>Signed distance of <paramref name="point"/> off the line, feet; positive to the right of its direction.</summary>
    private static double CrossTrackFt(LatLon point, LatLon origin, double lineDeg) =>
        GeoMath.SignedCrossTrackDistanceNm(point, origin, new TrueHeading(lineDeg)) * GeoMath.FeetPerNm;

    private static IReadOnlyList<TugMoveTrace> Fly(PushbackPose start, AircraftFootprint footprint, params TugMove[] moves)
    {
        TugSimulation flown = TugKinematics.Simulate(start, moves, footprint, TugMovePlanner.StepFt);
        Assert.True(flown.Completed, "the staged trace ran past its travel budget");
        return flown.Moves;
    }

    private static void AssertEndsOn(IReadOnlyList<TugMoveTrace> traces, GroundNode node)
    {
        double endFt = FeetBetween(traces[^1].End.Position, node.Position);
        Assert.True(endFt <= EndOnNodeToleranceFt, $"the staged trace ended {endFt:F1} ft from node {node.Id}");
    }

    private static List<LatLon> TracePositions(IReadOnlyList<TugMoveTrace> traces) => [.. traces.SelectMany(t => t.Samples).Select(s => s.Position)];

    private static List<LatLon> PlanPositions(TugPlan plan) => [.. plan.Moves.SelectMany(m => m.Samples).Select(s => s.Position)];

    private TugPlan PlanOrFail(AirportGroundLayout layout, TugRequest request)
    {
        TugPlan? plan = TugMovePlanner.Plan(layout, request, out string refusal);
        _output.WriteLine($"refusal '{refusal}'");
        Assert.True(plan is not null, $"the plan was refused: {refusal}");
        return plan;
    }

    private string Refusal(AirportGroundLayout layout, TugRequest request)
    {
        TugPlan? plan = TugMovePlanner.Plan(layout, request, out string refusal);
        _output.WriteLine($"refusal: {refusal}");
        Assert.Null(plan);
        return refusal;
    }

    private static TugRequest StandStart(AirportGroundLayout layout, string stand, string aircraftType, TugGoal goal)
    {
        GroundNode parking = Parking(layout, stand);
        return OffStand(layout, new PushbackPose(parking.Position, parking.TrueHeading!.Value.Degrees), aircraftType, goal) with
        {
            StartsAtStand = true,
        };
    }

    private static TugRequest OffStand(AirportGroundLayout layout, PushbackPose start, string aircraftType, TugGoal goal) =>
        OffStand(layout, start, AircraftFootprint.FromType(aircraftType), goal);

    private static TugRequest OffStand(AirportGroundLayout layout, PushbackPose start, AircraftFootprint footprint, TugGoal goal) =>
        new()
        {
            Start = start,
            StartsAtStand = false,
            StandName = null,
            Footprint = footprint,
            MovementArea = MovementAreaClassification.For(layout),
            Goals = [goal],
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
            Forced = false,
        };

    private static PavementClassifier Pavement(AirportGroundLayout layout) => new(layout, MovementAreaClassification.For(layout));

    private static LatLon Project(LatLon from, double bearingDeg, double feet) =>
        GeoMath.ProjectPoint(from, new TrueHeading(bearingDeg), feet / GeoMath.FeetPerNm);

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    private static double AbsDiffDeg(double a, double b) => Math.Abs(DeltaDeg(a, b));

    /// <summary>The signed turn from <paramref name="from"/> to <paramref name="to"/>, degrees in (-180, 180].</summary>
    private static double DeltaDeg(double from, double to)
    {
        double d = (to - from) % 360.0;
        return d > 180.0 ? d - 360.0
            : d <= -180.0 ? d + 360.0
            : d;
    }

    private static AirportGroundLayout? LoadSfo() => new TestAirportGroundData().GetLayout("SFO");

    private static GroundNode Spot(AirportGroundLayout layout, string name) =>
        layout.FindSpotNodeByName(name) ?? throw new InvalidOperationException($"SFO layout carries no spot named {name}");

    private static GroundNode Parking(AirportGroundLayout layout, string name) =>
        layout.FindParkingByName(name) ?? throw new InvalidOperationException($"SFO layout carries no parking named {name}");

    private sealed record JunctionTrace(
        GroundNode Node,
        string Along,
        string Across,
        PushbackPose Start,
        AircraftFootprint Footprint,
        IReadOnlyList<TugMoveTrace> Traces,
        double PastAlongFt,
        double PastAcrossFt
    );
}
