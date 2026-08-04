using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// Pilot readbacks for EXP. The rule table is keyed on canonical type without seeing arguments or
/// aircraft state, so every EXP form used to fall onto the first-declared "expedite climb …"
/// template — a descending aircraft read back a climb, and a taxiing one read back a climb too.
///
/// The correct forms: FAA JO 7110.65 §2-1-5.b makes <c>EXP &lt;alt&gt;</c> the same clearance as
/// CM/DM with an expedite qualifier appended, so its readback mirrors the CM/DM wording and must
/// carry the altitude (AIM §4-4-7.b). §3-7-2.j gives the two ground forms as "TAXI WITHOUT DELAY"
/// and "EXIT … WITHOUT DELAY".
/// </summary>
public sealed class ExpediteReadbackTests
{
    public ExpediteReadbackTests() => TestVnasData.EnsureInitialized();

    private static AircraftState Aircraft(double altitude)
    {
        return new AircraftState
        {
            Callsign = "N2BP",
            AircraftType = "SR22",
            Position = new LatLon(37.8, -122.2),
            TrueHeading = new TrueHeading(193),
            TrueTrack = new TrueHeading(193),
            Altitude = altitude,
            IndicatedAirspeed = 115,
        };
    }

    private static PilotSpeechText Readback(AircraftState aircraft, string text)
    {
        var parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess);
        var result = PilotResponder.BuildReadback(parsed.Value!, aircraft);
        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public void ExpediteToLowerAltitude_ReadsBackADescent()
    {
        var ac = Aircraft(2658);
        ac.Targets.TargetAltitude = 2000;
        ac.Targets.AssignedAltitude = 2000;

        var result = Readback(ac, "EXP 014");

        Assert.Equal("descend and maintain 1400, expedite descent", result.Terminal);
        Assert.Contains("descend and maintain one thousand four hundred, expedite descent", result.Tts);
        Assert.DoesNotContain("climb", result.Tts);
    }

    [Fact]
    public void ExpediteToHigherAltitude_ReadsBackAClimb()
    {
        var ac = Aircraft(2000);
        ac.Targets.TargetAltitude = 2000;
        ac.Targets.AssignedAltitude = 2000;

        var result = Readback(ac, "EXP 110");

        Assert.Equal("climb and maintain 11000, expedite climb", result.Terminal);
        Assert.Contains("climb and maintain one one thousand, expedite climb", result.Tts);
        Assert.DoesNotContain("descen", result.Tts);
    }

    [Fact]
    public void BareExpedite_WhileDescending_ReadsBackADescent()
    {
        var ac = Aircraft(5000);
        ac.Targets.TargetAltitude = 2000;

        var result = Readback(ac, "EXP");

        Assert.Equal("expedite descent", result.Terminal);
        Assert.DoesNotContain("climb", result.Tts);
    }

    [Fact]
    public void BareExpedite_WhileClimbing_ReadsBackAClimb()
    {
        var ac = Aircraft(2000);
        ac.Targets.TargetAltitude = 9000;

        var result = Readback(ac, "EXP");

        Assert.Equal("expedite climb", result.Terminal);
        Assert.DoesNotContain("descen", result.Tts);
    }

    [Fact]
    public void BareExpedite_WhileTaxiing_ReadsBackTaxiWithoutDelay()
    {
        var ac = Aircraft(13);
        ac.IsOnGround = true;
        ac.Ground.AssignedTaxiRoute = new TaxiRoute { Segments = [], HoldShortPoints = [] };

        var result = Readback(ac, "EXP");

        Assert.Equal("taxi without delay", result.Terminal);
        Assert.DoesNotContain("climb", result.Tts);
    }
}
