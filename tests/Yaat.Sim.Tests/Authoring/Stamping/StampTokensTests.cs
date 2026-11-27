using Xunit;
using Yaat.Sim.Authoring.Stamping;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Authoring.Stamping;

/// <summary><see cref="StampTokens.Expand"/> on a fixed aircraft: every token, case, a zero cruise and an unknown token.</summary>
public class StampTokensTests
{
    public StampTokensTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Expand_EveryToken_JoinsTheAircraftFieldsAsWritten()
    {
        string expanded = StampTokens.Expand("{CS} {TYP} {RTE} {RMK} {ALT} {DEP} {ARR}", Aircraft(35000));

        Assert.Equal("UAL12 B738 SSTIK5 SSTIK DCT LAX /V/ 35000 KSFO KLAX", expanded);
    }

    [Fact]
    public void Expand_LowerCaseTokens_ExpandToo()
    {
        string expanded = StampTokens.Expand("DM {alt} at {Dep}, {cs}", Aircraft(35000));

        Assert.Equal("DM 35000 at KSFO, UAL12", expanded);
    }

    [Fact]
    public void Expand_CruiseZero_AltExpandsToEmpty()
    {
        string expanded = StampTokens.Expand("CM {ALT}", Aircraft(0));

        Assert.Equal("CM ", expanded);
    }

    [Fact]
    public void Expand_UnknownToken_StaysAsWritten()
    {
        string expanded = StampTokens.Expand("{XYZ} {CS}", Aircraft(35000));

        Assert.Equal("{XYZ} UAL12", expanded);
    }

    [Fact]
    public void Expand_NoFlightPlan_PlanTokensExpandToEmpty()
    {
        var aircraft = new ScenarioAircraft { AircraftId = "UAL12", AircraftType = "B738" };

        Assert.Equal("    ", StampTokens.Expand("{RTE} {RMK} {ALT} {DEP} {ARR}", aircraft));
    }

    [Fact]
    public void Expand_NegativeCruise_AltExpandsToEmpty() => Assert.Equal("", StampTokens.Expand("{ALT}", Aircraft(-1000)));

    private static ScenarioAircraft Aircraft(int cruise) =>
        new()
        {
            AircraftId = "UAL12",
            AircraftType = "B738",
            FlightPlan = new ScenarioFlightPlan
            {
                Rules = "IFR",
                Departure = "KSFO",
                Destination = "KLAX",
                CruiseAltitude = cruise,
                Route = "SSTIK5 SSTIK DCT LAX",
                Remarks = "/V/",
                AircraftType = "B738/L",
            },
        };
}
