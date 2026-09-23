using System.Text.RegularExpressions;
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
public partial class PushTaxiwayTailFacingTests
{
    public PushTaxiwayTailFacingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [GeneratedRegex(@"face heading (\d{3})")]
    private static partial Regex FaceHeading();

    [Theory]
    [InlineData("PUSH Y TAIL S", 0.0)]
    [InlineData("PUSH Y FACE N", 0.0)]
    [InlineData("PUSH Y FACE S", 180.0)]
    public void PushOntoY_FromC8_FacesTheWayTheHintSays(string command, double hintMagneticDeg)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

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

        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, ctx);

        Assert.True(result.Success, $"{command} failed: {result.Message}");
        Match match = FaceHeading().Match(result.Message ?? "");
        Assert.True(match.Success, $"no facing in the readback: {result.Message}");
        double facingTrue = double.Parse(match.Groups[1].Value);
        double hintTrue = MagneticDeclination.MagneticToTrue(hintMagneticDeg, ac.Position);
        double offDeg = GeoMath.AbsBearingDifference(facingTrue, hintTrue);
        Assert.True(offDeg < 90.0, $"{command} faces {facingTrue:000}, {offDeg:F0}° from the hint ({hintTrue:F0} true)");
    }
}
