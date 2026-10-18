using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// <c>CVA [{runway}] [{airport}] [LEFT|RIGHT] [FOLLOW {callsign}]</c>: the runway comes first when given, an airport alone
/// is the airport-level visual (7110.65 7-4-3.c.4), and a keyword is never read as the runway. Each form round-trips
/// through the canonical text the describer prints.
/// </summary>
public class CvaGrammarTests
{
    [Theory]
    [InlineData("CVA", null, null, null, null, false)]
    [InlineData("CVA 28R", "28R", null, null, null, false)]
    [InlineData("CVA 9", "9", null, null, null, false)]
    [InlineData("CVA 28R OAK", "28R", "OAK", null, null, false)]
    [InlineData("CVA OAK", null, "OAK", null, null, false)]
    [InlineData("CVA KOAK", null, "KOAK", null, null, false)]
    [InlineData("CVA 28R LEFT", "28R", null, PatternDirection.Left, null, false)]
    [InlineData("CVA 28R FOLLOW AAL1", "28R", null, null, "AAL1", false)]
    [InlineData("CVA 28R LEFT FOLLOW AAL1", "28R", null, PatternDirection.Left, "AAL1", false)]
    [InlineData("CVA 28R OAK RIGHT FOLLOW AAL1", "28R", "OAK", PatternDirection.Right, "AAL1", false)]
    [InlineData("CVA LEFT", null, null, PatternDirection.Left, null, false)]
    [InlineData("CVA FOLLOW AAL1", null, null, null, "AAL1", false)]
    [InlineData("CVA OAK FOLLOW AAL1", null, "OAK", null, "AAL1", false)]
    [InlineData("CVAF 28R", "28R", null, null, null, true)]
    [InlineData("CVAF O69", null, "O69", null, null, true)]
    [InlineData("VISUAL 28r oak", "28R", "OAK", null, null, false)]
    public void Parse_ReadsEachForm(string input, string? runway, string? airport, PatternDirection? direction, string? follow, bool force)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(input);

        Assert.True(result.IsSuccess, result.Reason);
        ClearedVisualApproachCommand cva = Assert.IsType<ClearedVisualApproachCommand>(result.Value);
        Assert.Equal(new ClearedVisualApproachCommand(runway, airport, direction, follow, force), cva);
    }

    /// <summary>
    /// The typed input path (<see cref="CommandSchemeParser.ParseCompound(string, CommandScheme)"/>, which the client sends
    /// through) gates a verb on its registry overloads before the parser runs: the bare and airport-only forms must pass it.
    /// </summary>
    [Theory]
    [InlineData("CVA")]
    [InlineData("CVA O69")]
    [InlineData("CVAF")]
    [InlineData("CVAF O69")]
    [InlineData("CVA 28R OAK")]
    public void SchemeParser_AcceptsTheBareAndAirportForms(string input)
    {
        CompoundParseResult? typed = CommandSchemeParser.ParseCompound(input, CommandScheme.Default());

        Assert.NotNull(typed);
        Assert.IsType<ClearedVisualApproachCommand>(CommandParser.Parse(typed.CanonicalString).Value);
    }

    [Theory]
    [InlineData("CVA OAK 28R")]
    [InlineData("CVA 28R OAK SFO")]
    public void Parse_RejectsAnAirportBeforeTheRunwayOrASecondAirport(string input) => Assert.False(CommandParser.Parse(input).IsSuccess);

    [Theory]
    [InlineData("CVA")]
    [InlineData("CVA 28R")]
    [InlineData("CVA 28R OAK")]
    [InlineData("CVA OAK")]
    [InlineData("CVA LEFT")]
    [InlineData("CVA OAK FOLLOW AAL1")]
    [InlineData("CVA 28R OAK RIGHT FOLLOW AAL1")]
    [InlineData("CVAF O69")]
    public void Canonical_RoundTrips(string input)
    {
        ParsedCommand parsed = CommandParser.Parse(input).Value!;

        string canonical = CommandDescriber.DescribeCommand(parsed);

        Assert.Equal(input, canonical);
        Assert.Equal(parsed, CommandParser.Parse(canonical).Value);
    }

    [Theory]
    [InlineData("CVA", "Cleared visual approach")]
    [InlineData("CVA OAK", "Cleared visual approach to OAK")]
    [InlineData("CVA 28R", "Cleared visual approach runway 28R")]
    [InlineData("CVA OAK FOLLOW AAL1", "Cleared visual approach to OAK, follow AAL1")]
    public void Natural_NamesTheAirportWhenNoRunway(string input, string expected) =>
        Assert.Equal(expected, CommandDescriber.DescribeNatural(CommandParser.Parse(input).Value!));
}
