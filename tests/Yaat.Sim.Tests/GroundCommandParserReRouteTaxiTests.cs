using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// <c>RTAXI &lt;path&gt; [HS …] [CROSS …] [EXP]</c> parses by the TAXI grammar into a <see cref="ReRouteTaxiCommand"/>, needs a
/// path, and its canonical text round-trips.
/// </summary>
public class GroundCommandParserReRouteTaxiTests
{
    [Fact]
    public void ParseReRouteTaxi_PathAndHoldShort_Parses()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("RTAXI A B HS C");

        Assert.True(result.IsSuccess, result.Reason);
        ReRouteTaxiCommand reRoute = Assert.IsType<ReRouteTaxiCommand>(result.Value);
        Assert.Equal(["A", "B"], reRoute.Route.Path);
        Assert.Equal(["C"], reRoute.Route.HoldShorts.Select(h => h.ToCanonical()));
        Assert.Null(reRoute.Route.DestinationRunway);
        Assert.Null(reRoute.Route.DestinationParking);
        Assert.Null(reRoute.Route.DestinationSpot);
    }

    [Theory]
    [InlineData("RTAXI")]
    [InlineData("RTAXI 28L")]
    public void ParseReRouteTaxi_NoPath_Fails(string input)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(input);

        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData("RTAXI A B HS C")]
    [InlineData("RTAXI A B CROSS 28L EXP")]
    [InlineData("RTAXI A B @B9")]
    public void ReRouteTaxi_CanonicalText_RoundTrips(string input)
    {
        ParsedCommand parsed = CommandParser.Parse(input).Value!;
        string canonical = CommandDescriber.DescribeCommand(parsed);

        ParseResult<ParsedCommand> reparsed = CommandParser.Parse(canonical);

        Assert.True(reparsed.IsSuccess, reparsed.Reason);
        Assert.Equal(canonical, CommandDescriber.DescribeCommand(reparsed.Value!));
        Assert.StartsWith("RTAXI A B", canonical, StringComparison.Ordinal);
    }
}
