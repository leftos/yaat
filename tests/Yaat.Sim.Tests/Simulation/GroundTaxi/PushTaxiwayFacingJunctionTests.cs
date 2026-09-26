using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Issue #460 part B: <c>PUSH A F1</c> off SFO gate D7 was refused "the move to taxiway A would put the aircraft on
/// taxiway F1" while <c>PUSH A</c> from the same gate went. F1 meets A at A's exit node there, so an aircraft lined up
/// on A facing toward F1 lies across F1's junction pavement without ever going onto F1. A push onto a taxiway is no
/// longer judged by the taxiways it sweeps over, only by how far its centre goes past the taxiway it was sent onto.
/// </summary>
public class PushTaxiwayFacingJunctionTests(ITestOutputHelper output)
{
    private const string AircraftType = "B738";

    [Fact]
    public void PushOntoTaxiwayFacingJunction_IsNotRefusedForTheFacingTaxiwaysJunctionPavement()
    {
        if (SfoLayout() is not { } layout)
        {
            return;
        }

        GroundNode d7 = Gate(layout, "D7");
        CommandResult bare = Push(layout, d7, "PUSH A");
        Assert.True(bare.Success, $"PUSH A off D7 is the premise: {bare.Message}");

        CommandResult faced = Push(layout, d7, "PUSH A F1");

        Assert.True(faced.Success, $"PUSH A F1 off D7 was refused: {faced.Message}");
        Assert.Equal("Push onto A, face taxiway F1", faced.Message);
    }

    /// <summary>
    /// F1 meets A at D7's exit node. The push takes whichever direction along A ends with the nose toward the A/F1
    /// junction on the least turn from the gate, so the aircraft can taxi forward and turn
    /// onto F1: of A's two directions at the junction, the nose ends on the one nearest the stand heading, lined up on
    /// A with the nose tip at least one routine turn radius short of the junction.
    /// </summary>
    [Fact]
    public void PushOntoTaxiwayFacingJunction_EndsNoseTowardTheJunctionOnTheLeastTurn()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode d7 = Gate(ground.Layout, "D7");
        GroundNode junction = Junction(ground.Layout, d7);
        double[] towardJunctionDeg =
        [
            .. junction
                .Edges.OfType<GroundEdge>()
                .Where(e => e.MatchesTaxiway("A"))
                .Select(e => new TrueHeading(GeoMath.BearingTo(junction.Position, e.OtherNode(junction).Position)).ToReciprocal().Degrees),
        ];
        double standDeg = d7.TrueHeading!.Value.Degrees;
        double expectedDeg = towardJunctionDeg.MinBy(deg => GeoMath.AbsBearingDifference(deg, standDeg));
        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL460", AircraftType, "D7");

        CommandResult result = ground.Engine.SendCommand(ac.Callsign, "PUSH A F1");
        Assert.True(result.Success, result.Message);
        SfoGroundHarness.TickUntil(ground.Engine, () => ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase, 600, null);

        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
        double offDeg = GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, expectedDeg);
        double shortFt = NoseTipShortOfFt(ac, junction);
        output.WriteLine(
            $"nose-toward-junction headings {string.Join("/", towardJunctionDeg.Select(d => d.ToString("F1")))}; stand {standDeg:F1}; expected {expectedDeg:F1}; "
                + $"ended on {ac.TrueHeading.Degrees:F1}, nose tip {shortFt:F1} ft short of the junction"
        );
        Assert.True(
            offDeg < 10.0,
            $"ended on {ac.TrueHeading.Degrees:F1}°, {offDeg:F0}° from the least-turn direction toward the junction ({expectedDeg:F1}°)"
        );
        Assert.True(shortFt >= RoutineRadiusFt - 1.0, $"the nose tip ended {shortFt:F1} ft short of the junction, inside one routine radius");
    }

    /// <summary>
    /// D7 <c>PUSH A F1</c>: tail-north, the nose on A's southbound
    /// direction, stopped with the nose tip about 79 ft short of the A/F1 junction — at least one routine radius, and no
    /// long tow past it.
    /// </summary>
    [Fact]
    public void PushAF1_FromD7_EndsTailNorthOneRadiusShort()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode junction = Junction(ground.Layout, Gate(ground.Layout, "D7"));
        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL461", AircraftType, "D7");

        CommandResult result = ground.Engine.SendCommand(ac.Callsign, "PUSH A F1");
        Assert.True(result.Success, result.Message);
        SfoGroundHarness.TickUntil(ground.Engine, () => ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase, 600, null);

        double shortFt = NoseTipShortOfFt(ac, junction);
        output.WriteLine($"D7: ended on {ac.TrueHeading.Degrees:F1}°, nose tip {shortFt:F1} ft short of the A/F1 junction");
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.True(
            GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, 184.6) < 5.0,
            $"ended on {ac.TrueHeading.Degrees:F1}°, not nose-south (tail-north)"
        );
        Assert.InRange(shortFt, RoutineRadiusFt - 1.0, 100.0);
    }

    /// <summary>
    /// F3 <c>PUSH A F1</c>: A's line through F3's A exit lies about 1,285 ft from F3 across the ramp, and the A/F1 junction
    /// about 2,560 ft from that exit. The push is accepted but warned: it carries a long-push warning for
    /// A — the tow before it is lined up, about 1,400 ft, since the tug cannot reach a line 1,285 ft off in less — ends as
    /// soon as the aircraft is lined up on A at F3's exit with the nose along A toward the junction, tows no further along
    /// A after that, and the plan says the facing taxiway is far.
    /// </summary>
    [Fact]
    public void PushAF1_FromF3_FarJunction_LongPushWarned_StopsWhenLinedUp()
    {
        if (SfoLayout() is not { } layout)
        {
            return;
        }

        GroundNode f3 = Gate(layout, "F3");
        GroundNode exit = layout.FindExitByTaxiway(f3.Position, "A") ?? throw new InvalidOperationException("no A exit off F3");
        GroundNode junction = Junction(layout, f3);
        double towardF1Deg =
            layout.GetEdgeBearingForTaxiway(exit, "A", GeoMath.BearingTo(exit.Position, junction.Position))
            ?? throw new InvalidOperationException("no A edge");

        TugPlan plan = PlanPushAF1(layout, "F3");

        double pathFt = plan.Moves.Sum(m => m.PathLengthFt);
        double junctionFt = GeoMath.DistanceNm(exit.Position, junction.Position) * GeoMath.FeetPerNm;
        double lineOffFt = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(f3.Position, exit.Position, new TrueHeading(towardF1Deg))) * GeoMath.FeetPerNm;
        double noseOffADeg = GeoMath.AbsBearingDifference(plan.End.NoseTrueDeg, towardF1Deg);
        double afterLinedUpFt = TowAfterLinedUpFt(plan.Moves[^1]);
        output.WriteLine(
            $"F3: A's line {lineOffFt:F0} ft off the gate; exit {exit.Id}, junction {junction.Id} {junctionFt:F0} ft from it; moves "
                + $"{string.Join(", ", plan.Moves.Select(m => $"{m.Move.Kind} {m.Move.Shape} {m.PathLengthFt:F0} ft"))}; total {pathFt:F0} ft, "
                + $"{afterLinedUpFt:F1} ft after lining up; end ({plan.End.Position.Lat:F6}, {plan.End.Position.Lon:F6}) nose {plan.End.NoseTrueDeg:F1}°, "
                + $"{noseOffADeg:F1}° off A toward F1 ({towardF1Deg:F1}°); facing junction {plan.FacingJunctionFt:F0} ft, far {plan.FacingTaxiwayIsFar}; "
                + $"warnings {string.Join(", ", plan.Warnings)}"
        );
        TugLongPushWarning warning = Assert.Single(plan.Warnings.OfType<TugLongPushWarning>());
        Assert.Equal("A", warning.Taxiway);
        Assert.Equal(TugPlanWarningKind.LongPushToTaxiway, warning.Kind);
        Assert.InRange(warning.DistanceFt, 1250.0, 1500.0);
        Assert.True(noseOffADeg < 10.0, $"ended with the nose {noseOffADeg:F1}° off A's direction toward F1 ({towardF1Deg:F1}°)");
        Assert.True(afterLinedUpFt <= 10.0, $"towed {afterLinedUpFt:F1} ft along A after it was lined up; the facing is only a direction hint");
        Assert.True(
            plan.FacingTaxiwayIsFar,
            $"the plan does not say the facing taxiway is far (junction {plan.FacingJunctionFt:F0} ft from the end)"
        );
    }

    /// <summary>
    /// How far the last move of a push onto a taxiway carries on after it is first lined up — within 1 ft of its line
    /// and its travel within 1° of the line's — feet; the whole move when it never lines up.
    /// </summary>
    private static double TowAfterLinedUpFt(TugMoveTrace last)
    {
        TugMove move = last.Move;
        var line = new TrueHeading(move.LineTravelTrueDeg);
        double afterFt = 0.0;
        bool linedUp = false;
        LatLon? previous = null;
        foreach (TugPose sample in last.Samples)
        {
            afterFt += (linedUp && (previous is { } from)) ? GeoMath.DistanceNm(from, sample.Position) * GeoMath.FeetPerNm : 0.0;
            previous = sample.Position;
            double offFt = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(sample.Position, move.Point, line)) * GeoMath.FeetPerNm;
            linedUp |= (offFt <= 1.0) && (GeoMath.AbsBearingDifference(sample.TravelTrueDeg(move.Kind), move.LineTravelTrueDeg) <= 1.0);
        }

        return linedUp ? afterFt : last.PathLengthFt;
    }

    /// <summary>
    /// Plans <c>PUSH A F1</c> off <paramref name="gate"/> straight through the planner: the goal is A's line through the
    /// gate's A exit, facing the way A leads toward the nearest A/F1 junction. No neighbours.
    /// </summary>
    private static TugPlan PlanPushAF1(AirportGroundLayout layout, string gate)
    {
        GroundNode stand = Gate(layout, gate);
        GroundNode exit = layout.FindExitByTaxiway(stand.Position, "A") ?? throw new InvalidOperationException($"no A exit off {gate}");
        GroundNode junction = Junction(layout, stand);
        double facingDeg =
            layout.GetEdgeBearingForTaxiway(exit, "A", GeoMath.BearingTo(exit.Position, junction.Position))
            ?? throw new InvalidOperationException("no A edge");
        var request = new TugRequest
        {
            Start = new TugPose(stand.Position, stand.TrueHeading!.Value.Degrees),
            StartsAtStand = true,
            AircraftType = AircraftType,
            Goals = [TugGoal.TaxiwayLine(exit, "A", facingDeg) with { FacingTaxiwayName = "F1" }],
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
            Forced = false,
        };

        TugPlan? plan = TugMovePlanner.Plan(layout, request, out string refusal);
        Assert.True(plan is not null, $"PUSH A F1 off {gate} was refused: {refusal}");
        return plan;
    }

    /// <summary>
    /// D10 <c>PUSH A F1</c>: the push-off, then one push onto A
    /// through D10's exit, ending lined up on A with the nose on 135.6°, the A/F1 junction about 629 ft ahead of the nose,
    /// done in about a minute.
    /// </summary>
    [Fact]
    public void PushAF1_FromD10_AsRendered()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode junction = Junction(ground.Layout, Gate(ground.Layout, "D10"));
        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL462", AircraftType, "D10");

        CommandResult result = ground.Engine.SendCommand(ac.Callsign, "PUSH A F1");
        Assert.True(result.Success, result.Message);
        PushbackLegKind[] kinds = [.. ac.Phases!.Phases.OfType<PushbackPhase>().Select(p => p.Move.Kind)];
        int doneSecond = SfoGroundHarness.TickUntil(ground.Engine, () => ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase, 600, null);

        double shortFt = NoseTipShortOfFt(ac, junction);
        output.WriteLine(
            $"D10: moves {string.Join(", ", kinds)}; done at {doneSecond} s on {ac.TrueHeading.Degrees:F1}°, junction {shortFt:F1} ft ahead of the nose tip"
        );
        Assert.Equal([PushbackLegKind.Push, PushbackLegKind.Push], kinds);
        Assert.True(
            GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, 135.6) < 3.0,
            $"ended on {ac.TrueHeading.Degrees:F1}°, not the rendered 135.6°"
        );
        Assert.InRange(shortFt, 600.0, 660.0);
        Assert.InRange(doneSecond, 1, 75);

        TugPlan plan = PlanPushAF1(ground.Layout, "D10");
        output.WriteLine($"D10 plan: facing junction {plan.FacingJunctionFt:F0} ft from the end, far {plan.FacingTaxiwayIsFar}");
        Assert.False(plan.FacingTaxiwayIsFar, $"D10's A/F1 junction, {plan.FacingJunctionFt:F0} ft from the end, is flagged far");
    }

    /// <summary>A B738's routine tug turn radius, feet: its wheelbase.</summary>
    private static double RoutineRadiusFt => TugKinematics.TurnRadiusFt(AircraftType, tight: false);

    /// <summary>The A/F1 junction nearest the gate's A exit: the node carrying both taxiways' straight edges.</summary>
    private static GroundNode Junction(AirportGroundLayout layout, GroundNode gate)
    {
        GroundNode exit = layout.FindExitByTaxiway(gate.Position, "A") ?? throw new InvalidOperationException($"no A exit off {gate.Name}");
        return layout
                .Nodes.Values.Where(n =>
                    n.Edges.OfType<GroundEdge>().Any(e => e.MatchesTaxiway("A")) && n.Edges.OfType<GroundEdge>().Any(e => e.MatchesTaxiway("F1"))
                )
                .MinBy(n => GeoMath.DistanceNm(n.Position, exit.Position))
            ?? throw new InvalidOperationException("no A/F1 junction");
    }

    /// <summary>How far ahead of the aircraft's nose tip the node lies along the nose heading, feet.</summary>
    private static double NoseTipShortOfFt(AircraftState ac, GroundNode node)
    {
        LatLon noseTip = GeoMath.ProjectPoint(ac.Position, ac.TrueHeading, AircraftLength.ResolveFt(ac.AircraftType) / 2.0 / GeoMath.FeetPerNm);
        return GeoMath.AlongTrackDistanceNm(node.Position, noseTip, ac.TrueHeading) * GeoMath.FeetPerNm;
    }

    /// <summary>
    /// T meets A at E11U's exit node: its stub there is A's own intersection pavement, and <c>PUSH A F1</c> no longer
    /// refuses for lying across it.
    /// </summary>
    [Fact]
    public void PushOntoTaxiway_IsNotRefusedForAnotherTaxiwayMeetingItWithinTheLimit()
    {
        if (SfoLayout() is not { } layout)
        {
            return;
        }

        CommandResult result = Push(layout, Gate(layout, "E11U"), "PUSH A F1");

        Assert.True(result.Success, $"PUSH A F1 off E11U was refused: {result.Message}");
    }

    /// <summary>
    /// Off F3 the tow onto A lies across K's pavement, which meets A 75 ft along it from the exit node. A push onto a
    /// taxiway is judged by how far its centre goes past that taxiway, not by the taxiways it sweeps over, so
    /// <c>PUSH A F1</c> off F3 is accepted and ends lined up on A.
    /// </summary>
    [Fact]
    public void PushOntoTaxiway_F3_AcrossK_AcceptedAndEndsLinedUpOnA() => AssertPushEndsLinedUpOnA("F3");

    /// <summary>
    /// Off D10 E meets A at the exit node and the tow crosses an E/F fillet on the way. As off F3, the crossing is not a
    /// refusal and the push ends lined up on A.
    /// </summary>
    [Fact]
    public void PushOntoTaxiway_D10_AcrossTheEfFillet_AcceptedAndEndsLinedUpOnA() => AssertPushEndsLinedUpOnA("D10");

    /// <summary>
    /// <c>PUSH A F1</c> off <paramref name="gate"/> is accepted and the tow, flown to its end, stops lined up on the line
    /// it was sent to capture — A's edge through the exit node: the reference point within 3 ft of that line (the
    /// planner's end tolerance) and the nose within 10° of it. A bends near D10, so the nearest A chord is not that line.
    /// </summary>
    private void AssertPushEndsLinedUpOnA(string gate)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL460", AircraftType, gate);

        CommandResult result = ground.Engine.SendCommand(ac.Callsign, "PUSH A F1");

        Assert.True(result.Success, $"PUSH A F1 off {gate} was refused: {result.Message}");
        Assert.StartsWith("Push onto A, face taxiway F1", result.Message);
        SfoGroundHarness.TickUntil(ground.Engine, () => ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase, 600, null);
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
        GroundNode exit =
            ground.Layout.FindExitByTaxiway(Gate(ground.Layout, gate).Position, "A") ?? throw new InvalidOperationException($"no A exit off {gate}");
        double lineDeg =
            ground.Layout.GetEdgeBearingForTaxiway(exit, "A", ac.TrueHeading.Degrees) ?? throw new InvalidOperationException("no A edge");
        double fromExitFt = GeoMath.DistanceNm(exit.Position, ac.Position) * GeoMath.FeetPerNm;
        double offFt = Math.Abs(fromExitFt * Math.Sin((GeoMath.BearingTo(exit.Position, ac.Position) - lineDeg) * Math.PI / 180.0));
        double noseOffDeg = Math.Min(
            GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, lineDeg),
            GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, new TrueHeading(lineDeg).ToReciprocal().Degrees)
        );
        output.WriteLine($"{gate}: ended {offFt:F1} ft off A, nose {ac.TrueHeading.Degrees:F1}° against A's {lineDeg:F1}°");
        Assert.True(offFt <= 3.0, $"{gate}: ended {offFt:F1} ft off A's centreline");
        Assert.True(noseOffDeg < 10.0, $"{gate}: ended with the nose {noseOffDeg:F0}° off A");
    }

    private static CommandResult Push(AirportGroundLayout layout, GroundNode stand, string command)
    {
        var ac = new AircraftState
        {
            Callsign = "UAL460",
            AircraftType = AircraftType,
            Position = stand.Position,
            TrueHeading = stand.TrueHeading ?? throw new InvalidOperationException($"SFO gate {stand.Name} has no heading"),
            TrueTrack = stand.TrueHeading.Value,
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };
        ac.Ground.Layout = layout;
        ac.Ground.ParkingSpot = stand.Name;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);
        DispatchContext ctx = TestDispatch.Context(new Random(42), validateDctFixes: false, groundLayout: layout);
        return CommandDispatcher.DispatchCompound(compound, ac, ctx);
    }

    private static AirportGroundLayout? SfoLayout()
    {
        TestVnasData.EnsureInitialized();
        return new TestAirportGroundData().GetLayout("SFO");
    }

    private static GroundNode Gate(AirportGroundLayout layout, string name) =>
        layout.FindParkingByName(name) ?? throw new InvalidOperationException($"SFO gate {name} missing");
}
