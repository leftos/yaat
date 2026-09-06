using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// The CRC console's <c>C{receiving}{sending}[+]</c> entry is recorded as the typed consolidation verb's canonical
/// text and re-parsed by the router, so <see cref="CommandDescriber.DescribeCommand"/> followed by
/// <see cref="CommandParser.Parse"/> must be the identity. The full form used to be written <c>CON … FULL</c>, which
/// the parser refused — the parser's full form is the <c>CON+</c> verb.
/// </summary>
public class ConsolidationCanonicalRoundTripTests
{
    public static TheoryData<ParsedCommand> Shapes =>
        new() { new ConsolidateCommand("1N", "1R", Full: false), new ConsolidateCommand("1N", "1R", Full: true), new DeconsolidateCommand("1R") };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Canonical_RoundTripsThroughParser(ParsedCommand command)
    {
        var canonical = CommandDescriber.DescribeCommand(command);

        var reparsed = CommandParser.Parse(canonical);

        Assert.True(reparsed.IsSuccess, $"'{canonical}' failed to parse: {reparsed.Reason}");
        Assert.Equal(command, reparsed.Value);
    }

    [Fact]
    public void FullConsolidation_IsTheConPlusVerb()
    {
        Assert.Equal("CON+ 1N 1R", CommandDescriber.DescribeCommand(new ConsolidateCommand("1N", "1R", Full: true)));
    }
}
