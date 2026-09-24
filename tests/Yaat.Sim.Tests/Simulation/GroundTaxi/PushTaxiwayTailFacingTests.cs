using System.Globalization;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Issue #452: <c>PUSH Y TAIL S</c> off SFO gate C8 read back "face heading 208" — nose south, the opposite of tail
/// south. Y's exit node there is the taxiway's north end, whose only straight Y edge runs south; the facing must be
/// the direction along Y's line nearest the hint, the line extended past the end included.
/// </summary>
public class PushTaxiwayTailFacingTests
{
    public PushTaxiwayTailFacingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("PUSH Y TAIL S", 0.0)]
    [InlineData("PUSH Y FACE N", 0.0)]
    public void PushOntoY_FromC8_FacesTheWayTheHintSays(string command, double hintMagneticDeg)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        (CommandResult result, AircraftState ac) = PushFromC8(layout, command);

        Assert.True(result.Success, $"{command} failed: {result.Message}");
        List<TugMove> moves = [.. ac.Phases!.Phases.OfType<PushbackPhase>().Select(p => p.Move)];
        double facingTrue = TugKinematics.Simulate(new TugPose(ac.Position, ac.TrueHeading.Degrees), moves, ac.AircraftType, 1.0).End.NoseTrueDeg;
        double hintTrue = MagneticDeclination.MagneticToTrue(hintMagneticDeg, layout.FindParkingByName("C8")!.Position);
        double offDeg = GeoMath.AbsBearingDifference(facingTrue, hintTrue);
        Assert.True(offDeg < 90.0, $"{command} faces {facingTrue:000}, {offDeg:F0}° from the hint ({hintTrue:F0} true)");
    }

    /// <summary>
    /// <c>PUSH Y FACE S</c> off C8 has to turn the nose from 339° to south onto Y's line past its north end. LayoutInspector
    /// probe (<c>--airport SFO --distance 962 481</c>): "Distance #962 → #481: 249.3 ft (0.0410 nm) straight-line, bearing
    /// 192.2°" — gate C8 (#962, hdg 339) to Y's north end at Y/BC (#481). The best line capture swings the B739's centre
    /// past Y's centreline by more than half its 117 ft span, so the push is refused for overshooting Y.
    /// </summary>
    [Fact]
    public void PushOntoY_FromC8_FaceSouth_RefusedForOvershootingY()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        (CommandResult result, _) = PushFromC8(layout, "PUSH Y FACE S");

        Assert.False(result.Success, $"PUSH Y FACE S off C8 was accepted: {result.Message}");
        const string Prefix = "Unable, the move to taxiway Y would take the aircraft ";
        string message = result.Message ?? "";
        Assert.StartsWith(Prefix, message, StringComparison.Ordinal);
        Assert.EndsWith(" ft past taxiway Y", message, StringComparison.Ordinal);
        double overshootFt = double.Parse(
            message[Prefix.Length..message.IndexOf(" ft past", StringComparison.Ordinal)],
            CultureInfo.InvariantCulture
        );
        double halfSpanFt = TugPathCheck.MaxTaxiwayOvershootFt("B739");
        Assert.True(overshootFt > halfSpanFt, $"the premise: the {overshootFt} ft overshoot is over the B739's {halfSpanFt:F1} ft half-span");
    }

    private static (CommandResult Result, AircraftState Aircraft) PushFromC8(AirportGroundLayout layout, string command)
    {
        GroundNode c8 = layout.FindParkingByName("C8") ?? throw new InvalidOperationException("SFO gate C8 missing");
        var ac = new AircraftState
        {
            Callsign = "DAL2414",
            AircraftType = "B739",
            Position = c8.Position,
            TrueHeading = c8.TrueHeading ?? new TrueHeading(339),
            TrueTrack = c8.TrueHeading ?? new TrueHeading(339),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };
        ac.Ground.Layout = layout;
        ac.Ground.ParkingSpot = "C8";
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);
        DispatchContext ctx = TestDispatch.Context(new Random(42), validateDctFixes: false, groundLayout: layout);
        return (CommandDispatcher.DispatchCompound(compound, ac, ctx), ac);
    }
}
