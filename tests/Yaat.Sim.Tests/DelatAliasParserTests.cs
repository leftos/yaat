using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verifies that DELAT and its aliases (CXL, CLR) parse identically.
/// CXL and CLR were added to give controllers terser, more discoverable names
/// for clearing the pending command queue without weakening the dimension-aware
/// clearing in CommandDispatcher.ClearConflictingBlocks.
/// </summary>
public class DelatAliasParserTests
{
    [Theory]
    [InlineData("DELAT")]
    [InlineData("CXL")]
    [InlineData("CLR")]
    public void DelatAliases_BareForm_ParseToDeleteQueuedCommandWithNullBlock(string input)
    {
        var result = CommandParser.Parse(input);

        Assert.True(result.IsSuccess, $"Failed to parse '{input}': {result.Reason}");
        var cmd = Assert.IsType<DeleteQueuedCommand>(result.Value);
        Assert.Null(cmd.BlockNumber);
    }

    [Theory]
    [InlineData("DELAT 1", 1)]
    [InlineData("DELAT 5", 5)]
    [InlineData("CXL 2", 2)]
    [InlineData("CXL 7", 7)]
    [InlineData("CLR 3", 3)]
    [InlineData("CLR 12", 12)]
    public void DelatAliases_WithBlockNumber_ParseToDeleteQueuedCommandWithIndex(string input, int expectedBlock)
    {
        var result = CommandParser.Parse(input);

        Assert.True(result.IsSuccess, $"Failed to parse '{input}': {result.Reason}");
        var cmd = Assert.IsType<DeleteQueuedCommand>(result.Value);
        Assert.Equal(expectedBlock, cmd.BlockNumber);
    }

    [Theory]
    [InlineData("delat")]
    [InlineData("cxl")]
    [InlineData("clr")]
    public void DelatAliases_LowerCase_ParseSuccessfully(string input)
    {
        var result = CommandParser.Parse(input);

        Assert.True(result.IsSuccess, $"Failed to parse '{input}': {result.Reason}");
        Assert.IsType<DeleteQueuedCommand>(result.Value);
    }
}
