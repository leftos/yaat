using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// Pilot readbacks for the AP/1B route clearances: FAA JO 7110.65 §9-2-6 (CMTR, MTRA, XMTR,
/// SAYEXIT) and §9-2-13 (CAR). Terminal and spoken forms are asserted separately — they are built
/// independently and only the spoken one carries the callsign.
/// </summary>
[Collection("NavDbMutator")]
public sealed class MilitaryRouteReadbackTests
{
    public MilitaryRouteReadbackTests() => TestVnasData.EnsureInitialized();

    private static AircraftState Aircraft(string? designator)
    {
        var aircraft = new AircraftState
        {
            Callsign = "TREND21",
            AircraftType = "F16",
            Position = new LatLon(30.0, -99.0),
            Altitude = 8000,
        };
        if (designator is not null)
        {
            aircraft.MilitaryRoute.Designator = designator;
            aircraft.MilitaryRoute.Status = MilitaryRouteStatus.Established;
        }

        return aircraft;
    }

    private static PilotSpeechText Readback(AircraftState aircraft, string text)
    {
        var parsed = CommandParser.Parse(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var result = PilotResponder.BuildReadback(CommandParser.ParseCompound(text).Value!, aircraft);
        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public void Cmtr_PublishedAltitudes_ReadsBackBothForms()
    {
        var result = Readback(Aircraft(null), "CMTR IR149");

        Assert.Equal("cleared into IR149, maintain IR149 altitudes", result.Terminal);
        // §2-5-1.f: "state the letters I-R followed by the number in group form". The hyphen the
        // speller emits is normalised to a space on the way to the synthesiser.
        Assert.StartsWith("cleared into i r one forty nine, maintain i r one forty nine altitudes,", result.Tts);
    }

    [Fact]
    public void Cmtr_WithAssignedAltitude_ReadsBackTheAltitude()
    {
        // The published-altitudes pattern is the longer one, so a rule-driven readback would win
        // the tiebreaker and silently swallow the assigned altitude.
        var result = Readback(Aircraft(null), "CMTR IR149 50");

        Assert.Equal("cleared into IR149, maintain 5000", result.Terminal);
        Assert.StartsWith("cleared into i r one forty nine, maintain five thousand,", result.Tts);
    }

    [Fact]
    public void Cmtr_AtOrBelow_KeepsTheStandardAltitudeWording()
    {
        var result = Readback(Aircraft(null), "CMTR IR149 B50");

        Assert.Equal("cleared into IR149, maintain at or below 5000", result.Terminal);
        Assert.Contains("maintain at or below five thousand", result.Tts);
    }

    [Fact]
    public void Mtra_TakesItsDesignatorFromTheClearance()
    {
        // MTRA carries no designator, so the readback has to come from aircraft state.
        var result = Readback(Aircraft("IR149"), "MTRA");

        Assert.Equal("maintain IR149 altitudes", result.Terminal);
        Assert.StartsWith("maintain i r one forty nine altitudes,", result.Tts);
    }

    [Fact]
    public void Mtra_WithNoClearance_ProducesNoReadback()
    {
        var parsed = CommandParser.ParseCompound("MTRA");

        Assert.Null(PilotResponder.BuildReadback(parsed.Value!, Aircraft(null)));
    }

    [Fact]
    public void Xmtr_ReadsBackTheRouteOfFlightAndAltitude()
    {
        var result = Readback(Aircraft("IR149"), "XMTR KTCM 240 VIA V495 SEA");

        Assert.Equal("cleared to KTCM from IR149 via V495 SEA, maintain FL240", result.Terminal);
        // §2-5-1.a spells the airway phonetically and in group form; the fix keeps its own name.
        Assert.Contains("via victor four ninety five, Seattle VORTAC", result.Tts);
        Assert.Contains("maintain flight level two four zero", result.Tts);
        Assert.Contains("from i r one forty nine", result.Tts);
    }

    [Fact]
    public void Xmtr_AltitudeWithoutARoute_StillReadsBackTheAltitude()
    {
        var result = Readback(Aircraft("IR149"), "XMTR KTCM 240");

        Assert.Equal("cleared to KTCM from IR149, maintain FL240", result.Terminal);
        Assert.DoesNotContain(" via ", result.Tts);
    }

    [Fact]
    public void Xmtr_WithoutARouteOrAltitude_ReadsBackJustTheLimit()
    {
        var result = Readback(Aircraft("IR149"), "XMTR KTCM");

        Assert.Equal("cleared to KTCM from IR149", result.Terminal);
        Assert.DoesNotContain(" via ", result.Tts);
        Assert.DoesNotContain("maintain", result.Tts);
    }

    [Fact]
    public void Car_ReadsBackTheTrackWithoutSpeakingTheLetters()
    {
        // §9-2-13 names the number and puts "track" after it, unlike §2-5-1.f's "i-r ...".
        var result = Readback(Aircraft(null), "CAR AR1 240 310");

        Assert.Equal("cleared to conduct refueling along AR1 track, maintain block FL240 through FL310", result.Terminal);
        Assert.Contains("cleared to conduct refueling along one track", result.Tts);
        Assert.Contains("maintain block flight level two four zero through flight level three one zero", result.Tts);
        Assert.DoesNotContain("a-r", result.Tts);
    }

    [Fact]
    public void Car_WithoutABlock_ReadsBackTheTrackAlone()
    {
        var result = Readback(Aircraft(null), "CAR AR312");

        Assert.Equal("cleared to conduct refueling along AR312 track", result.Terminal);
        Assert.Contains("cleared to conduct refueling along three twelve track", result.Tts);
    }

    [Fact]
    public void SayExit_ProducesNoReadback_BecauseThePilotAnswersInstead()
    {
        // A request for an estimate is answered by PilotSayBuilder, not echoed back.
        var parsed = CommandParser.ParseCompound("SAYEXIT");

        Assert.Null(PilotResponder.BuildReadback(parsed.Value!, Aircraft("IR149")));
    }

    [Theory]
    // §2-5-1.a: the airway is its phonetic letter plus the number in group form; a fix keeps its
    // own pronunciation, so SEA stays the Seattle VORTAC rather than being spelled out.
    [InlineData("V495 SEA", "victor four ninety five, Seattle VORTAC")]
    [InlineData("J80", "juliet eighty")]
    [InlineData("A700", "alpha seven hundred")]
    public void SpellRouteString_SpellsAirwaysPhoneticallyInGroupForm(string route, string expected)
    {
        Assert.Equal(expected, PhraseologyVerbalizer.SpellRouteString(route));
    }
}
