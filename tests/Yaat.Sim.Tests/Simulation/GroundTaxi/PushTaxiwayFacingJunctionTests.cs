using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Issue #460 part B: <c>PUSH A F1</c> off SFO gate D7 was refused "the move to taxiway A would put the aircraft on
/// taxiway F1" while <c>PUSH A</c> from the same gate went. F1 meets A at A's exit node there, so an aircraft lined up
/// on A facing toward F1 lies across F1's junction pavement without ever going onto F1. A push onto a taxiway is no
/// longer judged by the taxiways it sweeps over, only by how far its centre goes past the taxiway it was sent onto
/// (user decision 2026-09-23).
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
        Assert.Equal("Pushing back onto A facing F1", faced.Message);
    }

    /// <summary>
    /// F1 meets A at D7's exit node, so the bearing from the exit node to F1's nearest node says nothing (it read 0°, and
    /// the push took A's direction nearest north by accident). The aircraft ends lined up on A in the direction along A
    /// nearest the way F1's own centreline leaves the junction; were F1 to leave square to A (both of A's directions
    /// within 5° of the same angle to it), on A's direction nearest its nose at the gate, the least swing.
    /// </summary>
    [Fact]
    public void PushOntoTaxiwayFacingJunction_EndsFacingAlongTheTaxiwayTheWayTheFacingTaxiwayLeads()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode d7 = Gate(ground.Layout, "D7");
        GroundNode exit = ground.Layout.FindExitByTaxiway(d7.Position, "A") ?? throw new InvalidOperationException("no A exit");
        GroundEdge f1 = exit.Edges.OfType<GroundEdge>().Single(e => e.MatchesTaxiway("F1"));
        double leadDeg = GeoMath.BearingTo(exit.Position, f1.OtherNode(exit).Position);
        double towardDeg = ground.Layout.GetEdgeBearingForTaxiway(exit, "A", leadDeg) ?? throw new InvalidOperationException("no A edge");
        double awayDeg =
            ground.Layout.GetEdgeBearingForTaxiway(exit, "A", new TrueHeading(towardDeg).ToReciprocal().Degrees)
            ?? throw new InvalidOperationException("no A edge");
        double squareDeg = Math.Abs(GeoMath.AbsBearingDifference(towardDeg, leadDeg) - GeoMath.AbsBearingDifference(awayDeg, leadDeg));
        double noseDeg = d7.TrueHeading!.Value.Degrees;
        double leastSwingDeg =
            GeoMath.AbsBearingDifference(towardDeg, noseDeg) <= GeoMath.AbsBearingDifference(awayDeg, noseDeg) ? towardDeg : awayDeg;
        double expectedDeg = squareDeg <= 5.0 ? leastSwingDeg : towardDeg;
        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL460", AircraftType, "D7");

        CommandResult result = ground.Engine.SendCommand(ac.Callsign, "PUSH A F1");
        Assert.True(result.Success, result.Message);
        SfoGroundHarness.TickUntil(ground.Engine, () => ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase, 600, null);

        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
        double offDeg = GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, expectedDeg);
        output.WriteLine(
            $"F1 leads off on {leadDeg:F1}; A runs {towardDeg:F1}/{awayDeg:F1} ({squareDeg:F1}° from square); expected {expectedDeg:F1}; ended on {ac.TrueHeading.Degrees:F1}"
        );
        Assert.True(offDeg < 10.0, $"ended on {ac.TrueHeading.Degrees:F1}°, {offDeg:F0}° from the expected direction along A ({expectedDeg:F1}°)");
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
    /// taxiway is judged by how far its centre goes past that taxiway, not by the taxiways it sweeps over (user decision
    /// 2026-09-23), so <c>PUSH A F1</c> off F3 is accepted and ends lined up on A.
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
        Assert.Equal("Pushing back onto A facing F1", result.Message);
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
