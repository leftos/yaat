using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// <c>TTP &lt;path&gt; [HS …] [CROSS …] [NODEL] [EXP]</c> expands by the TAXI grammar to the <c>TAXI &lt;path&gt; @GATE</c> it
/// clears the aircraft by: the flight-plan-remark stand is the destination, a trailing runway stays a path segment, and the
/// expanded text round-trips as a TAXI.
/// </summary>
public class GroundCommandParserTaxiToParkingTests
{
    private const string NeedsRoute = "TTP needs a taxi route; use TAXIAUTO @GATE to route automatically";

    private const string OtherStand = "TTP taxis to the gate in the flight plan remarks; use TAXI ... @{stand} for another stand";

    [Fact]
    public void ParseTaxiToParking_Path_ParsesToTheRemarkGate()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("TTP A B K");

        Assert.True(result.IsSuccess, result.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Equal(["A", "B", "K"], taxi.Path);
        Assert.Equal("GATE", taxi.DestinationParking);
        Assert.Null(taxi.DestinationRunway);
        Assert.Empty(taxi.HoldShorts);
    }

    [Fact]
    public void ParseTaxiToParking_HoldShort_PlacesTheGateAheadOfTheClause()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("TTP A B HS 28L");

        Assert.True(result.IsSuccess, result.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Equal(["A", "B"], taxi.Path);
        Assert.Equal("GATE", taxi.DestinationParking);
        Assert.Equal(["28L"], taxi.HoldShorts.Select(h => h.ToCanonical()));
    }

    [Fact]
    public void ParseTaxiToParking_CrossAndExpedite_Parses()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("TTP A B CROSS 28L EXP");

        Assert.True(result.IsSuccess, result.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Equal("GATE", taxi.DestinationParking);
        Assert.NotNull(taxi.CrossRunways);
        Assert.Equal(["28L"], taxi.CrossRunways);
        Assert.True(taxi.Expedite);
    }

    [Fact]
    public void ParseTaxiToParking_TrailingRunway_StaysAPathSegment()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("TTP G 28R");

        Assert.True(result.IsSuccess, result.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Equal(["G", "28R"], taxi.Path);
        Assert.Equal("GATE", taxi.DestinationParking);
        Assert.Null(taxi.DestinationRunway);
    }

    [Theory]
    [InlineData("TTP")]
    [InlineData("TTP HS 28L")]
    public void ParseTaxiToParking_NoPath_Fails(string input)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(input);

        Assert.False(result.IsSuccess);
        Assert.StartsWith($"\"TTP\" {NeedsRoute}", result.Reason);
    }

    [Theory]
    [InlineData("TTP A @B12")]
    [InlineData("TTP RWY 28L A")]
    public void ParseTaxiToParking_OtherStandOrRunwayKeyword_Fails(string input)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(input);

        Assert.False(result.IsSuccess);
        Assert.StartsWith($"\"TTP\" {OtherStand}", result.Reason);
    }

    [Fact]
    public void TaxiToParking_CanonicalText_ExpandsToTaxiAtGate()
    {
        ParsedCommand parsed = CommandParser.Parse("TTP A B K").Value!;

        Assert.Equal("TAXI A B K @GATE", CommandDescriber.DescribeCommand(parsed));
    }

    [Fact]
    public void ParseTaxiToParking_LowerCase_Parses()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("ttp a b hs 28l");

        Assert.True(result.IsSuccess, result.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Equal(["A", "B"], taxi.Path);
        Assert.Equal("GATE", taxi.DestinationParking);
        Assert.Equal(["28L"], taxi.HoldShorts.Select(h => h.ToCanonical()));
    }

    [Fact]
    public void ParseTaxiToParking_ExpediteBeforeTheHoldShort_Parses()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("TTP A EXP HS 28L");

        Assert.True(result.IsSuccess, result.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Equal(["A"], taxi.Path);
        Assert.True(taxi.Expedite);
        Assert.Equal(["28L"], taxi.HoldShorts.Select(h => h.ToCanonical()));
    }

    [Fact]
    public void ParseTaxiToParking_NoDelete_Parses()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("TTP A NODEL");

        Assert.True(result.IsSuccess, result.Reason);
        Assert.True(Assert.IsType<TaxiCommand>(result.Value).NoDelete);
    }

    [Fact]
    public void ParseTaxiToParking_RepeatedTaxi_Fails()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("TTP TAXI A");

        Assert.False(result.IsSuccess);
        Assert.StartsWith("\"TTP\" TAXI given twice", result.Reason);
    }

    [Fact]
    public void ParseTaxiToParking_MidPathRunway_StaysAPathSegment()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("TTP A 28R B");

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(["A", "28R", "B"], Assert.IsType<TaxiCommand>(result.Value).Path);
    }

    [Fact]
    public void ParseTaxiToParking_SpotVia_StaysAPathSegment()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("TTP A $7B");

        Assert.True(result.IsSuccess, result.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Equal(["A", "$7B"], taxi.Path);
        Assert.Null(taxi.DestinationSpot);
        Assert.Equal("GATE", taxi.DestinationParking);
    }

    [Fact]
    public void TaxiToParking_DescribedText_RoundTrips()
    {
        string described = CommandDescriber.DescribeCommand(CommandParser.Parse("TTP A B HS 28L").Value!);

        ParseResult<ParsedCommand> reparsed = CommandParser.Parse(described);

        Assert.Equal("TAXI A B @GATE HS 28L", described);
        Assert.True(reparsed.IsSuccess, reparsed.Reason);
        Assert.Equal(described, CommandDescriber.DescribeCommand(reparsed.Value!));
    }
}
