using Xunit;
using Yaat.Sim;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests;

/// <summary>
/// Voice-type ⟷ remarks coupling (#256). The FP remarks are canonical: a slash-delimited /v/·/r/·/t/ marker
/// declares the pilot's voice capability, full voice implied when absent. A VATSIM operational convention,
/// not an FAA construct.
/// </summary>
public class FlightPlanVoiceTests
{
    [Theory]
    [InlineData(null, FlightPlanVoice.Full)]
    [InlineData("", FlightPlanVoice.Full)]
    [InlineData("SOME REMARKS", FlightPlanVoice.Full)]
    [InlineData("/v/", FlightPlanVoice.Full)]
    [InlineData("/r/", FlightPlanVoice.ReceiveOnly)]
    [InlineData("/t/", FlightPlanVoice.TextOnly)]
    [InlineData("/V/ NEW PILOT", FlightPlanVoice.Full)]
    [InlineData("/R/ STUDENT", FlightPlanVoice.ReceiveOnly)]
    [InlineData("PBN/A1 /T/ RMK", FlightPlanVoice.TextOnly)]
    public void ParseVoiceType_DerivesFromMarker_DefaultsFull(string? remarks, int expected)
    {
        Assert.Equal(expected, FlightPlanVoice.ParseVoiceType(remarks));
    }

    [Theory]
    [InlineData("STS/HOSP")]
    [InlineData("SEL/ABCD")]
    [InlineData("RVR/2400")]
    [InlineData("DOF/250704")]
    public void ParseVoiceType_FreeTextSlashTokens_DoNotFalsePositive(string remarks)
    {
        // Only /v/ /r/ /t/ (letter bracketed by slashes) are markers — other slash tokens stay full voice.
        Assert.Equal(FlightPlanVoice.Full, FlightPlanVoice.ParseVoiceType(remarks));
    }

    [Fact]
    public void ApplyVoiceMarker_EmptyRemarks_WritesBareMarker()
    {
        Assert.Equal("/t/", FlightPlanVoice.ApplyVoiceMarker("", FlightPlanVoice.TextOnly));
        Assert.Equal("/r/", FlightPlanVoice.ApplyVoiceMarker(null, FlightPlanVoice.ReceiveOnly));
        Assert.Equal("/v/", FlightPlanVoice.ApplyVoiceMarker("", FlightPlanVoice.Full));
    }

    [Fact]
    public void ApplyVoiceMarker_PreservesFreeText_PrependsMarker()
    {
        Assert.Equal("/t/ NEW PILOT", FlightPlanVoice.ApplyVoiceMarker("NEW PILOT", FlightPlanVoice.TextOnly));
    }

    [Fact]
    public void ApplyVoiceMarker_ReplacesExistingMarker_KeepsRest()
    {
        // Switching /v/ → /r/ removes the old marker and keeps the surrounding remark text.
        var result = FlightPlanVoice.ApplyVoiceMarker("/v/ HEAVY", FlightPlanVoice.ReceiveOnly);
        Assert.Equal("/r/ HEAVY", result);
    }

    [Fact]
    public void ApplyVoiceMarker_IsIdempotent()
    {
        var once = FlightPlanVoice.ApplyVoiceMarker("HEAVY", FlightPlanVoice.TextOnly);
        var twice = FlightPlanVoice.ApplyVoiceMarker(once, FlightPlanVoice.TextOnly);
        Assert.Equal(once, twice);
        Assert.Equal(FlightPlanVoice.TextOnly, FlightPlanVoice.ParseVoiceType(twice));
    }

    [Fact]
    public void RoundTrip_ApplyThenParse_PreservesEachType()
    {
        foreach (var vt in new[] { FlightPlanVoice.Full, FlightPlanVoice.ReceiveOnly, FlightPlanVoice.TextOnly })
        {
            var remarks = FlightPlanVoice.ApplyVoiceMarker("REMARK TEXT", vt);
            Assert.Equal(vt, FlightPlanVoice.ParseVoiceType(remarks));
        }
    }

    [Theory]
    [InlineData("/t/ TEXT PILOT", FlightPlanVoice.TextOnly)]
    [InlineData("/r/", FlightPlanVoice.ReceiveOnly)]
    [InlineData("NO MARKER", FlightPlanVoice.Full)]
    [InlineData("", FlightPlanVoice.Full)]
    public void ScenarioLoad_DerivesVoiceTypeFromFiledRemarks(string remarks, int expected)
    {
        var scenarioAircraft = new ScenarioAircraft
        {
            AircraftId = "UAL238",
            AircraftType = "B738",
            FlightPlan = new ScenarioFlightPlan { Departure = "KOAK", Remarks = remarks },
        };

        var ac = ScenarioLoader.CreateBaseState(scenarioAircraft, primaryAirportId: null, primaryApproach: null);

        Assert.Equal(expected, ac.Voice.Type);
    }
}
