using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// Pilot readbacks for runway-exit instructions, end to end through <see cref="PilotResponder"/>.
/// 7110.65 §3-7-2.a gives the controller's "EXIT (taxiway)" and §3-10-9 the "TURN LEFT/RIGHT
/// (taxiway)" forms; neither prescribes a pilot readback, so the pilot mirrors the instruction it
/// was given — "exit W3", "exit left W3" — and never speaks the recognition-only filler words
/// ("when", "if able", "at", "on", "to") the speech-input rules carry.
/// </summary>
public sealed class ExitReadbackTests
{
    public ExitReadbackTests() => TestVnasData.EnsureInitialized();

    private static AircraftState Aircraft()
    {
        return new AircraftState
        {
            Callsign = "N2BP",
            AircraftType = "SR22",
            Position = new LatLon(37.8, -122.2),
            TrueHeading = new TrueHeading(193),
            TrueTrack = new TrueHeading(193),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };
    }

    private static PilotSpeechText Readback(string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess);
        PilotSpeechText? result = PilotResponder.BuildReadback(parsed.Value!, Aircraft(), ActiveRunways.Empty);
        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public void ExitTaxiway_ReadsBackTheMirroredInstruction()
    {
        PilotSpeechText result = Readback("EXIT W3");

        Assert.Contains("exit W3", result.Terminal);
        Assert.DoesNotContain("when", result.Terminal);
        Assert.Contains("exit whiskey three", result.Tts);
    }

    [Fact]
    public void ExitLeft_ReadsBackTheMirroredInstruction()
    {
        PilotSpeechText result = Readback("EL W3");

        Assert.Contains("exit left W3", result.Terminal);
        Assert.DoesNotContain("when", result.Terminal);
        Assert.Contains("exit left whiskey three", result.Tts);
    }
}
