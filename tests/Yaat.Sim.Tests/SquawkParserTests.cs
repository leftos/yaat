using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// A squawk code is exactly four octal digits. The command bar, the spoken path and the context
/// menu's Squawk box all agree on that form, and a short or non-octal code is refused rather than
/// read as a four-digit code with leading zeros.
/// </summary>
public sealed class SquawkParserTests
{
    [Theory]
    [InlineData("SQ 123", "123")]
    [InlineData("SQ 12", "12")]
    [InlineData("SQ 12345", "12345")]
    [InlineData("SQ 12A4", "12A4")]
    [InlineData("SQ +123", "+123")]
    [InlineData("SQ 1238", "1238")]
    public void Squawk_NotFourOctalDigits_IsRefused(string input, string arg)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(input);

        Assert.False(result.IsSuccess);
        Assert.Contains($"invalid squawk code '{arg}' (expected four digits 0-7)", result.Reason);
    }

    [Theory]
    [InlineData("SQ 0123", 123u)]
    [InlineData("SQ 7700", 7700u)]
    [InlineData("SQ 1200", 1200u)]
    public void Squawk_FourOctalDigits_SetsTheCode(string input, uint expected)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(input);

        Assert.True(result.IsSuccess, result.Reason);
        SquawkCommand command = Assert.IsType<SquawkCommand>(result.Value);
        Assert.Equal(expected, command.Code);
    }

    [Fact]
    public void Squawk_WithoutArgument_IsTheResetCommand()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("SQ");

        Assert.True(result.IsSuccess, result.Reason);
        Assert.IsType<SquawkResetCommand>(result.Value);
    }
}
