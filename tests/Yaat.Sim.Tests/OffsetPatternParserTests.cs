using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for OFL / OFR / OFFSETL / OFFSETR parsing: bare (default 0.5 NM),
/// explicit distance, alias parity, case insensitivity, and rejection of
/// non-numeric arguments.
/// </summary>
public class OffsetPatternParserTests
{
    [Fact]
    public void Bare_OFL_ParsesWithNullOffset()
    {
        var result = CommandParser.Parse("OFL");
        Assert.True(result.IsSuccess, result.Reason);
        var cmd = Assert.IsType<OffsetLeftPatternCommand>(result.Value);
        Assert.Null(cmd.OffsetNm);
    }

    [Fact]
    public void Bare_OFR_ParsesWithNullOffset()
    {
        var result = CommandParser.Parse("OFR");
        Assert.True(result.IsSuccess, result.Reason);
        var cmd = Assert.IsType<OffsetRightPatternCommand>(result.Value);
        Assert.Null(cmd.OffsetNm);
    }

    [Theory]
    [InlineData("OFL 0.5", 0.5)]
    [InlineData("OFL 1", 1.0)]
    [InlineData("OFL 0.25", 0.25)]
    public void OFL_WithDistance_ParsesToOffsetNm(string input, double expected)
    {
        var result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, result.Reason);
        var cmd = Assert.IsType<OffsetLeftPatternCommand>(result.Value);
        Assert.Equal(expected, cmd.OffsetNm);
    }

    [Theory]
    [InlineData("OFR 0.5", 0.5)]
    [InlineData("OFR 1.5", 1.5)]
    [InlineData("OFR 0.3", 0.3)]
    public void OFR_WithDistance_ParsesToOffsetNm(string input, double expected)
    {
        var result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, result.Reason);
        var cmd = Assert.IsType<OffsetRightPatternCommand>(result.Value);
        Assert.Equal(expected, cmd.OffsetNm);
    }

    [Theory]
    [InlineData("OFFSETL", null)]
    [InlineData("OFFSETL 0.7", 0.7)]
    public void OFFSETL_AliasParsesIdenticallyToOFL(string input, double? expected)
    {
        var result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, result.Reason);
        var cmd = Assert.IsType<OffsetLeftPatternCommand>(result.Value);
        Assert.Equal(expected, cmd.OffsetNm);
    }

    [Theory]
    [InlineData("OFFSETR", null)]
    [InlineData("OFFSETR 0.6", 0.6)]
    public void OFFSETR_AliasParsesIdenticallyToOFR(string input, double? expected)
    {
        var result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, result.Reason);
        var cmd = Assert.IsType<OffsetRightPatternCommand>(result.Value);
        Assert.Equal(expected, cmd.OffsetNm);
    }

    [Theory]
    [InlineData("ofl")]
    [InlineData("ofr 0.5")]
    [InlineData("OFFSETL 0.4")]
    public void Offset_IsCaseInsensitive(string input)
    {
        var result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, result.Reason);
    }

    [Theory]
    [InlineData("OFL ABC")]
    [InlineData("OFR -")]
    [InlineData("OFL UPWIND")]
    public void Offset_NonNumericArgument_FailsOrUnsupported(string input)
    {
        // Non-numeric arg should not produce a typed Offset command.
        // Either parse fails outright OR yields UnsupportedCommand.
        var result = CommandParser.Parse(input);
        if (result.IsSuccess)
        {
            Assert.IsType<UnsupportedCommand>(result.Value);
        }
    }
}
