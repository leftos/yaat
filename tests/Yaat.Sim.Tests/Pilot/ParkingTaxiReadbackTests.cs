using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// A taxi clearance to a parking position (<c>@B12</c>) or spot (<c>$7A</c>) names the destination first,
/// directly after "taxi" — "taxi to gate bravo one two via alpha, bravo", "taxi to spot seven alpha"
/// (AIM 4-3-18.d.3 "taxi to Page via taxiways echo three, echo one, and echo niner"; 7110.65 3-7-2.a
/// "TAXI TO (location)") — for TAXI and TAXIAUTO alike. A gate-shaped parking name says "gate", every
/// other parking name "parking".
/// </summary>
public class ParkingTaxiReadbackTests(ITestOutputHelper output)
{
    private string Spoken(ParsedCommand cmd)
    {
        TestVnasData.EnsureInitialized();
        string? spoken = PhraseologyVerbalizer.Verbalize(cmd);
        output.WriteLine($"spoken:   {spoken}");
        return spoken ?? "(null)";
    }

    private string Terminal(ParsedCommand cmd)
    {
        TestVnasData.EnsureInitialized();
        string? terminal = PhraseologyVerbalizer.VerbalizeTerminal(cmd);
        output.WriteLine($"terminal: {terminal}");
        return terminal ?? "(null)";
    }

    [Fact]
    public void PathlessParking_SpokenAndTerminal()
    {
        var taxi = new TaxiCommand([], [], Expedite: false, DestinationParking: "B12");

        Assert.Equal("taxi to gate bravo one two", Spoken(taxi));
        Assert.Equal("taxi to gate B12", Terminal(taxi));
    }

    [Fact]
    public void PathlessSpot_SpokenAndTerminal()
    {
        var taxi = new TaxiCommand([], [], Expedite: false, DestinationSpot: "7A");

        Assert.Equal("taxi to spot seven alpha", Spoken(taxi));
        Assert.Equal("taxi to spot 7A", Terminal(taxi));
    }

    [Fact]
    public void ParkingThenRoute_VoicesBoth()
    {
        Assert.Equal(
            "taxi to gate bravo one two via alpha, bravo",
            Spoken(new TaxiCommand(["A", "B"], [], Expedite: false, DestinationParking: "B12"))
        );
        Assert.Equal("taxi to gate B12 via A B", Terminal(new TaxiCommand(["A", "B"], [], Expedite: false, DestinationParking: "B12")));
        Assert.Equal("taxi to spot seven alpha via alpha, bravo", Spoken(new TaxiCommand(["A", "B"], [], Expedite: false, DestinationSpot: "7A")));
    }

    [Fact]
    public void TaxiAutoToParking_ReadsBackLikePathlessTaxi()
    {
        Assert.Equal(
            "taxi to gate bravo one two",
            Spoken(new TaxiAutoCommand(DestinationRunway: null, DestinationParking: "B12", DestinationSpot: null, Expedite: false))
        );
    }

    [Fact]
    public void HoldShortAndParking_DestinationComesBeforeTheHoldShort()
    {
        // 7110.65 §3-7-2.a: the route, then the hold-short — the mandatory item is read back last.
        string spoken = Spoken(new TaxiCommand(["A", "B"], [HoldShortTarget.Parse("28R")], Expedite: false, DestinationParking: "B12"));

        Assert.Equal("taxi to gate bravo one two via alpha, bravo, hold short of runway two eight right", spoken);
    }

    [Fact]
    public void CrossAndParking_KeepsTheCrossingClearance()
    {
        string spoken = Spoken(new TaxiCommand(["A", "B"], [], Expedite: false, DestinationParking: "B12", CrossRunways: ["28R"]));

        Assert.Equal("taxi to gate bravo one two via alpha, bravo, cross runway two eight right", spoken);
    }

    [Fact]
    public void RouteWithARunwaySegment_NamesTheGateFirstThenTheRoute() =>
        Assert.Equal(
            "taxi to gate bravo one two via bravo, on runway two eight right, golf",
            Spoken(new TaxiCommand(["B", "28R", "G"], [], Expedite: false, DestinationParking: "B12"))
        );

    [Fact]
    public void ParkingNameWithDash_IsSpelledOut() =>
        Assert.Equal("taxi to parking four one dash one zero", Spoken(new TaxiCommand([], [], Expedite: false, DestinationParking: "41-10")));

    [Theory]
    [InlineData("CARGO1", "taxi to parking cargo one")]
    [InlineData("JANET", "taxi to parking janet")]
    [InlineData("HELI1", "taxi to parking heli one")]
    [InlineData("ATLANTIC1", "taxi to parking atlantic one")]
    [InlineData("FDX1", "taxi to parking foxtrot delta xray one")]
    [InlineData("SBE4", "taxi to parking sierra bravo echo four")]
    [InlineData("A13V", "taxi to gate alpha one three victor")]
    public void WordLikeParkingNames_AreSpokenAsWords(string name, string expected) =>
        Assert.Equal(expected, Spoken(new TaxiCommand([], [], Expedite: false, DestinationParking: name)));

    [Fact]
    public void SpotNamedWithTheNoun_DropsTheRepeatedNoun()
    {
        Assert.Equal("taxi to spot seven", Spoken(new TaxiCommand([], [], Expedite: false, DestinationSpot: "SPOT7")));
        Assert.Equal("taxi to spot SPOT7", Terminal(new TaxiCommand([], [], Expedite: false, DestinationSpot: "SPOT7")));
    }

    [Fact]
    public void ParkingAndSpotBothSet_SpotWinsLikeTheCanonicalForm() =>
        Assert.Equal("taxi to spot seven alpha", Spoken(new TaxiCommand([], [], Expedite: false, DestinationParking: "B12", DestinationSpot: "7A")));
}
