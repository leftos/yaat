using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// A repeated TAXI verb ("taxi taxi A F 28L") used to be read as a taxiway named TAXI, and the
/// pathfinder answered "Cannot find taxiway TAXI in layout." The parser refuses a leading repeat with
/// a message of its own; the single-verb forms stay unchanged. Only the leading token is checked:
/// KA and other real taxiways look like command words.
/// </summary>
public class GroundCommandParserRepeatedVerbTests
{
    private const string RepeatedTaxiMessage = "TAXI given twice: send the route once, after a single TAXI";

    [Fact]
    public void ParseTaxi_RepeatedTaxiVerb_Refused()
    {
        ParseResult<ParsedCommand> result = GroundCommandParser.ParseTaxi("TAXI A F 28L");

        Assert.False(result.IsSuccess);
        Assert.Equal(RepeatedTaxiMessage, result.Reason);
    }

    [Fact]
    public void Parse_FullLineRepeatedTaxiVerb_RefusedWithItsOwnMessage()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("TAXI TAXI A F 28L");

        Assert.False(result.IsSuccess);
        Assert.Contains(RepeatedTaxiMessage, result.Reason);
        Assert.DoesNotContain("Cannot find taxiway", result.Reason);
    }

    [Fact]
    public void ParseRwyTaxi_RepeatedTaxiVerb_Refused()
    {
        ParseResult<ParsedCommand> result = GroundCommandParser.ParseRwyTaxi("28L TAXI TAXI A");

        Assert.False(result.IsSuccess);
        Assert.Equal(RepeatedTaxiMessage, result.Reason);
    }

    [Fact]
    public void Parse_SingleTaxiVerb_StillParses()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("TAXI A F 28L");

        Assert.True(result.IsSuccess, result.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Equal(["A", "F"], taxi.Path);
        Assert.Equal("28L", taxi.DestinationRunway);
    }

    [Fact]
    public void Parse_RwyWithOneTaxiVerb_StillParses()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("RWY 28L TAXI A");

        Assert.True(result.IsSuccess, result.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(result.Value);
        Assert.Equal(["A"], taxi.Path);
        Assert.Equal("28L", taxi.DestinationRunway);
    }
}
