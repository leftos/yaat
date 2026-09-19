using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Force Altitude answers to both CMN and DMN, the way the climb and descend verbs it snaps to are CM and
/// DM. DMN and DM are separate verbs that resolve by exact lookup when they are typed with a space, and the
/// one place they could genuinely collide is concatenated input (<c>DMN240</c> vs <c>DM240</c>), where the
/// parser splits the verb off a run of digits — so both spellings are pinned here.
/// </summary>
public class ForceAltitudeAliasParserTests
{
    [Theory]
    [InlineData("DMN 50")]
    [InlineData("dmn 50")]
    public void ForceAltitude_DmnAlias_ParsesLikeCmn(string input)
    {
        ParseResult<ParsedCommand> canonical = CommandParser.Parse("CMN 50");
        ParseResult<ParsedCommand> result = CommandParser.Parse(input);

        Assert.True(canonical.IsSuccess, $"Failed to parse 'CMN 50': {canonical.Reason}");
        Assert.True(result.IsSuccess, $"Failed to parse '{input}': {result.Reason}");
        ForceAltitudeCommand forced = Assert.IsType<ForceAltitudeCommand>(result.Value);
        Assert.Equal(canonical.Value, result.Value);
        Assert.Equal(5000, forced.Altitude);
    }

    [Fact]
    public void ForceAltitude_DmnConcatenated_StaysDistinctFromDm()
    {
        ParseResult<ParsedCommand> forced = CommandParser.Parse("DMN240");
        ParseResult<ParsedCommand> descend = CommandParser.Parse("DM240");

        Assert.True(forced.IsSuccess, $"Failed to parse 'DMN240': {forced.Reason}");
        Assert.True(descend.IsSuccess, $"Failed to parse 'DM240': {descend.Reason}");
        ForceAltitudeCommand forcedCmd = Assert.IsType<ForceAltitudeCommand>(forced.Value);
        DescendMaintainCommand descendCmd = Assert.IsType<DescendMaintainCommand>(descend.Value);
        Assert.Equal(24000, forcedCmd.Altitude);
        Assert.Equal(24000, descendCmd.Altitude);
    }

    [Fact]
    public void ForceAltitude_DmnAndDm_StayDistinct()
    {
        ParseResult<ParsedCommand> descend = CommandParser.Parse("DM 50");
        ParseResult<ParsedCommand> forced = CommandParser.Parse("DMN 50");

        Assert.True(descend.IsSuccess, $"Failed to parse 'DM 50': {descend.Reason}");
        Assert.True(forced.IsSuccess, $"Failed to parse 'DMN 50': {forced.Reason}");
        DescendMaintainCommand descendCmd = Assert.IsType<DescendMaintainCommand>(descend.Value);
        Assert.Equal(5000, descendCmd.Altitude);
        Assert.IsType<ForceAltitudeCommand>(forced.Value);
    }
}
