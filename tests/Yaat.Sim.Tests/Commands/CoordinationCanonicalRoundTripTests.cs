using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A coordination verb's canonical text is what the action router records and re-parses on every run kind, so
/// <see cref="CommandDescriber.DescribeCommand"/> followed by <see cref="CommandParser.Parse"/> must be the identity
/// for every shape the parser can produce. <c>RDH</c> used to drop the held text, and a list-qualified <c>RDTXT</c>
/// had no parseable form — the list id was read as the first word of the message.
/// </summary>
public class CoordinationCanonicalRoundTripTests
{
    public static TheoryData<ParsedCommand> Shapes =>
        new()
        {
            new CoordinationReleaseCommand(null),
            new CoordinationReleaseCommand("DR"),
            new CoordinationHoldCommand(null, null),
            new CoordinationHoldCommand("DR", null),
            new CoordinationHoldCommand("DR", "EXPECT 28R"),
            new CoordinationRecallCommand(null),
            new CoordinationRecallCommand("DR"),
            new CoordinationAcknowledgeCommand(null),
            new CoordinationAcknowledgeCommand("DR"),
            new CoordinationAutoAckCommand("DR", null),
            new CoordinationAutoAckCommand("DR", true),
            new CoordinationAutoAckCommand("DR", false),
            new CoordinationDeleteCommand(null),
            new CoordinationDeleteCommand("DR"),
            new CoordinationReorderCommand(null, 2),
            new CoordinationReorderCommand("DR", 2),
            new CoordinationModifyCommand(null, "EXPECT 28R"),
            new CoordinationModifyCommand("DR", "EXPECT 28R"),
        };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Canonical_RoundTripsThroughParser(ParsedCommand command)
    {
        var canonical = CommandDescriber.DescribeCommand(command);

        var reparsed = CommandParser.Parse(canonical);

        Assert.True(reparsed.IsSuccess, $"'{canonical}' failed to parse: {reparsed.Reason}");
        Assert.Equal(command, reparsed.Value);
    }

    [Theory]
    [InlineData("RDH DR EXPECT 28R", "RDH DR EXPECT 28R")]
    [InlineData("RDTXT /DR EXPECT 28R", "RDTXT /DR EXPECT 28R")]
    [InlineData("RDTXT /dr expect 28r", "RDTXT /DR EXPECT 28R")]
    [InlineData("RDTXT EXPECT 28R", "RDTXT EXPECT 28R")]
    public void Canonical_IsTheNormalizedInput(string input, string expectedCanonical)
    {
        var parsed = CommandParser.Parse(input);

        Assert.True(parsed.IsSuccess, $"'{input}' failed to parse: {parsed.Reason}");
        Assert.Equal(expectedCanonical, CommandDescriber.DescribeCommand(parsed.Value!));
    }

    [Fact]
    public void Rdtxt_ListWithoutText_IsRefused()
    {
        var parsed = CommandParser.Parse("RDTXT /DR");

        Assert.False(parsed.IsSuccess);
    }
}
