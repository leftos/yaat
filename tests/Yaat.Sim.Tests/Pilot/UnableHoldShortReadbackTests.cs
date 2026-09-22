using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// A hold-short the aircraft is already inside its braking distance of cannot be flown, and the crew says so:
/// "unable to hold short of tango, stopping". P/CG "UNABLE" is the word for it — "indicates inability to
/// comply with a specific instruction, request, or clearance" — and AIM 4-4-7.c leaves the call to the crew:
/// "it is the responsibility of the pilot to accept or refuse the clearance issued". What is being refused is
/// AIM 2-3-5.b.3's hold: "the pilot MUST STOP so that no part of the aircraft extends beyond the holding
/// position marking". The bar the command handler could not make carries
/// <see cref="HoldShortPoint.Unable"/>, which is what tells the readback which of the two answers to give;
/// every other hold-short reads back unchanged, so the rule table — and with it STT — is untouched.
/// </summary>
public sealed class UnableHoldShortReadbackTests
{
    public UnableHoldShortReadbackTests() => TestVnasData.EnsureInitialized();

    private static AircraftState Aircraft(bool unable)
    {
        var aircraft = new AircraftState
        {
            Callsign = "SKW5416",
            AircraftType = "CRJ7",
            Position = new LatLon(37.6207, -122.3827),
            TrueHeading = new TrueHeading(270),
            Altitude = 13,
            IndicatedAirspeed = 28,
            IsOnGround = true,
        };

        aircraft.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "T",
                    Unable = unable,
                },
            ],
        };
        return aircraft;
    }

    private static PilotSpeechText Readback(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        PilotSpeechText? result = PilotResponder.BuildReadback(parsed.Value!, aircraft);
        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public void UnmakeableHoldShort_Spoken_SaysUnableAndStopping() =>
        Assert.StartsWith("unable to hold short of tango, stopping", Readback(Aircraft(unable: true), "HS T").Tts, StringComparison.Ordinal);

    [Fact]
    public void UnmakeableHoldShort_Terminal_SaysUnableAndStopping() =>
        Assert.Equal("unable to hold short of T, stopping", Readback(Aircraft(unable: true), "HS T").Terminal);

    [Fact]
    public void MakeableHoldShort_Terminal_ReadsBackTheHoldUnchanged() =>
        Assert.Equal("hold short of T", Readback(Aircraft(unable: false), "HS T").Terminal);

    [Fact]
    public void MakeableHoldShort_Spoken_ReadsBackTheHoldUnchanged() =>
        Assert.StartsWith("hold short of tango", Readback(Aircraft(unable: false), "HS T").Tts, StringComparison.Ordinal);
}
