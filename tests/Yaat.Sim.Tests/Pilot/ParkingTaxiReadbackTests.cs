using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// A taxi clearance to a parking position (<c>@B12</c>) or spot (<c>$7A</c>) reads the destination
/// back — "taxi to parking bravo one two" / "taxi via alpha, bravo to spot seven alpha" — for TAXI and
/// TAXIAUTO alike. Before this the destination was silently dropped from every readback, and the
/// path-less forms produced no readback at all.
/// </summary>
public class ParkingTaxiReadbackTests(ITestOutputHelper output)
{
    private string Spoken(ParsedCommand cmd)
    {
        TestVnasData.EnsureInitialized();
        var spoken = PhraseologyVerbalizer.Verbalize(cmd);
        output.WriteLine($"spoken:   {spoken}");
        return spoken ?? "(null)";
    }

    private string Terminal(ParsedCommand cmd)
    {
        TestVnasData.EnsureInitialized();
        var terminal = PhraseologyVerbalizer.VerbalizeTerminal(cmd);
        output.WriteLine($"terminal: {terminal}");
        return terminal ?? "(null)";
    }

    [Fact]
    public void PathlessParking_SpokenAndTerminal()
    {
        var taxi = new TaxiCommand([], [], DestinationParking: "B12");

        Assert.Equal("taxi to parking bravo one two", Spoken(taxi));
        Assert.Equal("taxi to parking B12", Terminal(taxi));
    }

    [Fact]
    public void PathlessSpot_SpokenAndTerminal()
    {
        var taxi = new TaxiCommand([], [], DestinationSpot: "7A");

        Assert.Equal("taxi to spot seven alpha", Spoken(taxi));
        Assert.Equal("taxi to spot 7A", Terminal(taxi));
    }

    [Fact]
    public void RouteThenParking_VoicesBoth()
    {
        Assert.Equal("taxi via alpha, bravo to parking bravo one two", Spoken(new TaxiCommand(["A", "B"], [], DestinationParking: "B12")));
        Assert.Equal("taxi via alpha, bravo to spot seven alpha", Spoken(new TaxiCommand(["A", "B"], [], DestinationSpot: "7A")));
    }

    [Fact]
    public void TaxiAutoToParking_ReadsBackLikePathlessTaxi()
    {
        Assert.Equal("taxi to parking bravo one two", Spoken(new TaxiAutoCommand(DestinationParking: "B12")));
    }

    [Fact]
    public void HoldShortAndParking_DestinationComesBeforeTheHoldShort()
    {
        // 7110.65 §3-7-2.a: the route, then the hold-short — the mandatory item is read back last.
        var spoken = Spoken(new TaxiCommand(["A", "B"], [HoldShortTarget.Parse("28R")], DestinationParking: "B12"));

        Assert.Equal("taxi via alpha, bravo to parking bravo one two, hold short of runway two eight right", spoken);
    }

    [Fact]
    public void CrossAndParking_KeepsTheCrossingClearance()
    {
        var spoken = Spoken(new TaxiCommand(["A", "B"], [], DestinationParking: "B12", CrossRunways: ["28R"]));

        Assert.Equal("taxi via alpha, bravo to parking bravo one two, cross runway two eight right", spoken);
    }

    [Fact]
    public void ParkingNameWithDash_IsSpelledOut()
    {
        Assert.Equal("taxi to parking four one dash one zero", Spoken(new TaxiCommand([], [], DestinationParking: "41-10")));
    }

    [Theory]
    [InlineData("CARGO1", "taxi to parking cargo one")]
    [InlineData("JANET", "taxi to parking janet")]
    [InlineData("HELI1", "taxi to parking heli one")]
    [InlineData("ATLANTIC1", "taxi to parking atlantic one")]
    [InlineData("FDX1", "taxi to parking foxtrot delta xray one")]
    [InlineData("SBE4", "taxi to parking sierra bravo echo four")]
    [InlineData("A13V", "taxi to parking alpha one three victor")]
    public void WordLikeParkingNames_AreSpokenAsWords(string name, string expected)
    {
        Assert.Equal(expected, Spoken(new TaxiCommand([], [], DestinationParking: name)));
    }

    [Fact]
    public void SpotNamedWithTheNoun_DropsTheRepeatedNoun()
    {
        Assert.Equal("taxi to spot seven", Spoken(new TaxiCommand([], [], DestinationSpot: "SPOT7")));
        Assert.Equal("taxi to spot SPOT7", Terminal(new TaxiCommand([], [], DestinationSpot: "SPOT7")));
    }

    [Fact]
    public void ParkingAndSpotBothSet_SpotWinsLikeTheCanonicalForm()
    {
        Assert.Equal("taxi to spot seven alpha", Spoken(new TaxiCommand([], [], DestinationParking: "B12", DestinationSpot: "7A")));
    }
}
