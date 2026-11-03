using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// A pilot names an approach as the chart does (7110.65 4-8-1, 2-4-16, 2-4-17; AIM 5-4-5): the type, the variant letter as
/// its ICAO word, then "runway" and the runway digits with their side. Only an id no type names is spelled, and then only a
/// trailing side letter after the runway digits is a word. An expect-approach names its own type, or says "approach" alone
/// when the id names none, and never a type the controller did not issue.
/// </summary>
public class SpellApproachTests
{
    public SpellApproachTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("ILS28R", "ILS runway two eight right")]
    [InlineData("I28R", "ILS runway two eight right")]
    [InlineData("RNAV28RZ", "RNAV Zulu runway two eight right")]
    [InlineData("R28RZ", "RNAV Zulu runway two eight right")]
    [InlineData("H28RZ", "RNAV Zulu runway two eight right")]
    [InlineData("RNAV28R", "RNAV runway two eight right")]
    [InlineData("R28R", "RNAV runway two eight right")]
    [InlineData("LOC28R", "localizer runway two eight right")]
    [InlineData("L28R", "localizer runway two eight right")]
    [InlineData("B13", "localizer back course runway one three")]
    [InlineData("V13", "VOR runway one three")]
    [InlineData("D31", "VOR DME runway three one")]
    [InlineData("X36", "LDA runway three six")]
    [InlineData("VOR-A", "VOR Alpha")]
    [InlineData("RNV-B", "RNAV Bravo")]
    [InlineData("28R", "runway two eight right")]
    public void SpellApproach_NamesTheApproachTheWayTheChartDoes(string approachId, string spoken) =>
        Assert.Equal(spoken, PhraseologyVerbalizer.SpellApproach(approachId));

    /// <summary>Circling ids published in the test CIFP: a type prefix, a dash and a letter, and no runway.</summary>
    [Theory]
    [InlineData("GPS-A", "GPS Alpha")]
    [InlineData("LDA-C", "LDA Charlie")]
    [InlineData("LOC-A", "localizer Alpha")]
    [InlineData("LBC-B", "localizer back course Bravo")]
    [InlineData("NDB-A", "NDB Alpha")]
    [InlineData("VDM-A", "VOR DME Alpha")]
    public void SpellApproach_NamesACirclingApproachByItsTypeAndLetter(string approachId, string spoken) =>
        Assert.Equal(spoken, PhraseologyVerbalizer.SpellApproach(approachId));

    /// <summary>Type codes whose name no other row in the table reaches.</summary>
    [Theory]
    [InlineData("S08", "VOR runway eight")]
    [InlineData("N13", "NDB runway one three")]
    [InlineData("Q13", "NDB DME runway one three")]
    [InlineData("P13", "GPS runway one three")]
    [InlineData("T13", "TACAN runway one three")]
    [InlineData("U13", "SDF runway one three")]
    public void SpellApproach_NamesEveryTypeCode(string approachId, string spoken) =>
        Assert.Equal(spoken, PhraseologyVerbalizer.SpellApproach(approachId));

    /// <summary>
    /// The ids that are not a name plus a runway: a dashed variant is a variant, not a circling letter; an id the parser rejects,
    /// an unknown circling prefix and a dash with no letter after it are spelled.
    /// </summary>
    [Theory]
    [InlineData("H12-Z", "RNAV Zulu runway one two")]
    [InlineData("VORA", "victor oscar romeo alpha")]
    [InlineData("ZZZ-A", "zulu zulu zulu - alpha")]
    [InlineData("VOR-1", "victor oscar romeo - one")]
    public void SpellApproach_EdgeIds(string approachId, string spoken) => Assert.Equal(spoken, PhraseologyVerbalizer.SpellApproach(approachId));

    [Theory]
    [InlineData("ils28r", "ILS runway two eight right")]
    [InlineData("rnav28rz", "RNAV Zulu runway two eight right")]
    [InlineData("vor-a", "VOR Alpha")]
    [InlineData("28r", "runway two eight right")]
    public void SpellApproach_IsCaseInsensitive(string approachId, string spoken) =>
        Assert.Equal(spoken, PhraseologyVerbalizer.SpellApproach(approachId));

    [Fact]
    public void SpellApproach_TypeNoReadbackNames_SpellsOnlyTheRunwaySideLetter() =>
        Assert.Equal("zulu zulu two eight left", PhraseologyVerbalizer.SpellApproach("ZZ28L"));

    /// <summary>
    /// An expect-approach whose id names a runway but no type reads "expect approach runway two eight right": the controller
    /// issued no type, so the readback may not add ILS's (the clearance path reads "cleared approach" for the same id).
    /// </summary>
    [Fact]
    public void Eapp_IdNamingNoType_ReadsTheApproachWithoutAType()
    {
        var cmd = new ExpectApproachCommand("Z28R", null);

        Assert.Equal("expect approach runway two eight right", PhraseologyVerbalizer.Verbalize(cmd));
        Assert.Equal("expect approach runway 28R", PhraseologyVerbalizer.VerbalizeTerminal(cmd));
    }

    /// <summary>A circling expect-approach names its own type and letter, never the ILS the first expect rule says.</summary>
    [Fact]
    public void Eapp_CirclingId_ReadsTheCirclingName()
    {
        PilotSpeechText? readback = PilotResponder.BuildReadback(
            CommandParser.ParseCompound("EAPP VOR-A").Value!,
            MakeArrival(),
            ActiveRunways.Empty
        );

        Assert.NotNull(readback);
        Assert.Contains("expect VOR Alpha approach", readback.Tts);
        Assert.Contains("expect VOR A approach", readback.Terminal);
    }

    /// <summary>
    /// The pilot's full readback of an expect-approach names the type once and "runway" once, and never spells the id
    /// (7110.65 4-8-1's type-then-runway form).
    /// </summary>
    [Fact]
    public void Eapp_ReadsBackTheApproachWithoutSpellingIt()
    {
        AircraftState aircraft = MakeArrival();
        string? readback = PilotResponder.BuildReadback(CommandParser.ParseCompound("EAPP ILS28R").Value!, aircraft, ActiveRunways.Empty)?.Tts;

        Assert.NotNull(readback);
        Assert.Equal(1, CountOf(readback, "ILS runway two eight right"));
        Assert.DoesNotContain("india", readback);
        Assert.DoesNotContain("lima", readback);
        Assert.DoesNotContain("sierra", readback);
        Assert.DoesNotContain("romeo", readback);
    }

    /// <summary>Five miles east of KOAK 28R, heading for it, as <c>ApproachClearanceReadbackTests</c> sets its arrival.</summary>
    private static AircraftState MakeArrival() =>
        new()
        {
            Callsign = "N123AB",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(295),
            Altitude = 5000,
            IndicatedAirspeed = 180,
            Position = new LatLon(37.735, -122.12),
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
