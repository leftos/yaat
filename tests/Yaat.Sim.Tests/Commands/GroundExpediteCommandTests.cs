using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// YAAT-658: the <c>EXP</c> ("without delay", 7110.65 §3-7-2.j) modifier on TAXI, TAXIAUTO and CROSS — parsed as a reserved keyword
/// that ends the HS / CROSS / RWY lists, written last in every canonical form so it round-trips, and read back as "without delay".
/// </summary>
public sealed class GroundExpediteCommandTests
{
    public GroundExpediteCommandTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static T Parse<T>(string input)
        where T : ParsedCommand
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, $"'{input}' failed to parse: {parsed.Reason}");
        return Assert.IsType<T>(parsed.Value);
    }

    [Fact]
    public void Taxi_Exp_SetsExpedite_AndEndsTheHoldShortList()
    {
        TaxiCommand taxi = Parse<TaxiCommand>("TAXI A B HS C EXP");

        Assert.True(taxi.Expedite);
        Assert.Equal(["A", "B"], taxi.Path);
        Assert.Equal([HoldShortTarget.Parse("C")], taxi.HoldShorts);
    }

    [Fact]
    public void Taxi_Exp_EndsTheCrossList()
    {
        TaxiCommand taxi = Parse<TaxiCommand>("TAXI A CROSS 28L EXP RWY 28R");

        Assert.True(taxi.Expedite);
        Assert.Equal(["28L"], taxi.CrossRunways);
        Assert.Equal("28R", taxi.DestinationRunway);
    }

    [Fact]
    public void Taxi_WithoutExp_IsNotExpedited() => Assert.False(Parse<TaxiCommand>("TAXI A B").Expedite);

    [Fact]
    public void RwyTaxi_Exp_KeepsExpedite()
    {
        TaxiCommand taxi = Parse<TaxiCommand>("RWY 28R TAXI B EXP");

        Assert.True(taxi.Expedite);
        Assert.Equal("28R", taxi.DestinationRunway);
        Assert.Equal(["B"], taxi.Path);
    }

    [Theory]
    [InlineData("TAXIAUTO 28R EXP", "28R", null, null)]
    [InlineData("TAXIAUTO @A12 EXP", null, "A12", null)]
    [InlineData("TAXIAUTO $5 EXP", null, null, "5")]
    public void TaxiAuto_Exp_SetsExpedite_AndKeepsTheDestination(string input, string? runway, string? parking, string? spot)
    {
        TaxiAutoCommand auto = Parse<TaxiAutoCommand>(input);

        Assert.True(auto.Expedite);
        Assert.Equal(runway, auto.DestinationRunway);
        Assert.Equal(parking, auto.DestinationParking);
        Assert.Equal(spot, auto.DestinationSpot);
    }

    [Fact]
    public void TaxiAuto_WithoutExp_IsNotExpedited() => Assert.False(Parse<TaxiAutoCommand>("TAXIAUTO 28R").Expedite);

    [Fact]
    public void Cross_MultipleRunwaysExp_ExpeditesTheCommand()
    {
        CrossRunwayCommand cross = Parse<CrossRunwayCommand>("CROSS 28L 28R EXP");

        Assert.True(cross.Expedite);
        Assert.Equal(["28L", "28R"], cross.RunwayIds);
    }

    [Fact]
    public void Cross_Exp_EndsTheHoldShortList()
    {
        CrossRunwayCommand cross = Parse<CrossRunwayCommand>("CROSS 28R HS B EXP");

        Assert.True(cross.Expedite);
        Assert.Equal(["28R"], cross.RunwayIds);
        Assert.Equal([HoldShortTarget.Parse("B")], cross.HoldShorts);
    }

    [Fact]
    public void BareCrossExp_IsTheNextHoldShortExpedited()
    {
        CrossRunwayCommand cross = Parse<CrossRunwayCommand>("CROSS EXP");

        Assert.True(cross.Expedite);
        Assert.Empty(cross.RunwayIds);
        Assert.Empty(cross.HoldShorts);
    }

    [Theory]
    [InlineData("TAXI A B HS C EXP", "TAXI A B HS C EXP")]
    [InlineData("TAXI A CROSS 28L EXP RWY 28R", "TAXI A RWY 28R CROSS 28L EXP")]
    [InlineData("TAXI A B EXP NODEL", "TAXI A B NODEL EXP")]
    [InlineData("TAXIAUTO 28R EXP", "TAXIAUTO 28R EXP")]
    [InlineData("TAXIAUTO @A12 EXP", "TAXIAUTO @A12 EXP")]
    [InlineData("CROSS 28L 28R EXP", "CROSS 28L 28R EXP")]
    [InlineData("CROSS 28R HS B EXP", "CROSS 28R HS B EXP")]
    [InlineData("CROSS EXP", "CROSS EXP")]
    public void Canonical_WritesExpLast_AndRoundTrips(string input, string expected)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);

        string canonical = CommandDescriber.DescribeCommand(parsed.Value!);
        Assert.Equal(expected, canonical);

        ParseResult<ParsedCommand> reparsed = CommandParser.Parse(canonical);
        Assert.True(reparsed.IsSuccess, reparsed.Reason);
        Assert.Equal(canonical, CommandDescriber.DescribeCommand(reparsed.Value!));
    }

    [Theory]
    [InlineData("TAXI A B EXP", "Taxi via A B, without delay")]
    [InlineData("TAXI A B HS C EXP", "Taxi via A B, without delay, hold short of C")]
    [InlineData("CROSS 28R EXP", "Cross runway 28R, without delay")]
    [InlineData("CROSS EXP", "Cross next hold-short, without delay")]
    [InlineData("TAXIAUTO 28R EXP", "Taxi (auto) to runway 28R, without delay")]
    public void Natural_AddsWithoutDelay(string input, string expected)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);

        Assert.Equal(expected, CommandDescriber.DescribeNatural(parsed.Value!));
    }

    [Fact]
    public void TaxiReadback_PutsWithoutDelayAfterTheRoute_BeforeTheHoldShort() =>
        Assert.Equal(
            "taxi via B B5, without delay, hold short of runway 28R",
            PhraseologyVerbalizer.VerbalizeTerminal(new TaxiCommand(["B", "B5"], [HoldShortTarget.Parse("28R")], Expedite: true))
        );

    [Fact]
    public void TaxiReadback_WithoutHoldShort_EndsWithWithoutDelay() =>
        Assert.Equal("taxi via B B5, without delay", PhraseologyVerbalizer.VerbalizeTerminal(new TaxiCommand(["B", "B5"], [], Expedite: true)));

    [Fact]
    public void CrossReadback_SingleRunway_SaysWithoutDelay() =>
        Assert.Equal("cross runway 28R without delay", PhraseologyVerbalizer.VerbalizeTerminal(new CrossRunwayCommand(["28R"], [], Expedite: true)));

    [Fact]
    public void CrossReadback_MultipleRunways_SaysWithoutDelayBeforeTheHoldShort() =>
        Assert.Equal(
            "cross runway 28L and 28R without delay, hold short of runway 1L",
            PhraseologyVerbalizer.VerbalizeTerminal(new CrossRunwayCommand(["28L", "28R"], [HoldShortTarget.Parse("1L")], Expedite: true))
        );

    [Fact]
    public void BareCrossExpReadback_HasNoExpediteWording()
    {
        string bare = PhraseologyVerbalizer.VerbalizeTerminal(new CrossRunwayCommand([], [], Expedite: false)) ?? "";
        string expedited = PhraseologyVerbalizer.VerbalizeTerminal(new CrossRunwayCommand([], [], Expedite: true)) ?? "";

        Assert.Equal(bare, expedited);
        Assert.DoesNotContain("without delay", expedited, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossHoldShortExp_WithoutARunway_IsRefused()
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse("CROSS HS B EXP");

        Assert.False(parsed.IsSuccess);
        Assert.Contains("EXP", parsed.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("B738", true, 39.0)]
    [InlineData("B77W", true, 30.0)]
    [InlineData("A388", true, 30.0)]
    [InlineData("B738", false, 30.0)]
    public void ExitTaxiCeiling_GivesASuperOrHeavyNoExpediteIncrease(string type, bool expediting, double expectedKts) =>
        Assert.Equal(expectedKts, RunwayExitPhase.ExitTaxiCeilingKts(AircraftCategory.Jet, type, expediting), 3);

    [Fact]
    public void ExpediteAccel_IsBrisker_ExceptForHeaviesAndHelicopters()
    {
        Assert.Equal(1.5, CategoryPerformance.ExpediteTaxiAccelRate(AircraftCategory.Jet, superOrHeavy: false));
        Assert.Equal(1.5, CategoryPerformance.ExpediteTaxiAccelRate(AircraftCategory.Turboprop, superOrHeavy: false));
        Assert.Equal(1.5, CategoryPerformance.ExpediteTaxiAccelRate(AircraftCategory.Piston, superOrHeavy: false));
        Assert.Equal(1.0, CategoryPerformance.ExpediteTaxiAccelRate(AircraftCategory.Helicopter, superOrHeavy: false));
        Assert.Equal(1.0, CategoryPerformance.ExpediteTaxiAccelRate(AircraftCategory.Jet, superOrHeavy: true));
    }

    [Theory]
    [InlineData("B77W", true)]
    [InlineData("A388", true)]
    [InlineData("B738", false)]
    public void SuperOrHeavy_ComesFromTheWakeClass(string type, bool expected) =>
        Assert.Equal(expected, CategoryPerformance.IsSuperOrHeavy(type, AircraftCategory.Jet));
}
