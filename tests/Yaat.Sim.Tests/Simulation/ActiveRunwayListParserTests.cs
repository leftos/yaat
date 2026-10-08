using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

public sealed class ActiveRunwayListParserTests
{
    public ActiveRunwayListParserTests() => TestVnasData.EnsureInitialized();

    [Theory]
    [InlineData("28L 28R", new[] { "28L", "28R" })]
    [InlineData("28L,28R", new[] { "28L", "28R" })]
    [InlineData("28L,\n28R", new[] { "28L", "28R" })]
    [InlineData("28l , 28r", new[] { "28L", "28R" })]
    [InlineData("28L\t28R", new[] { "28L", "28R" })]
    [InlineData("  28L  \r\n  28R  ", new[] { "28L", "28R" })]
    [InlineData("D28L A28R 30", new[] { "D28L", "A28R", "30" })]
    [InlineData("", new string[0])]
    public void Parse_ValidTokens_ReturnsTheOrderedList(string text, string[] expected)
    {
        ActiveRunwayParseResult result = ActiveRunwayListParser.Parse("OAK", text, NavDb());

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, result.Runways.Select(r => r.ToToken()));
    }

    [Theory]
    [InlineData("17", "Unknown runway 17 at OAK")]
    [InlineData("0", "Not a runway: 0")]
    [InlineData("00", "Not a runway: 00")]
    [InlineData("37", "Not a runway: 37")]
    [InlineData("99", "Not a runway: 99")]
    [InlineData("+33", "Not a runway: +33")]
    [InlineData("RWY28L", "Not a runway: RWY28L")]
    [InlineData("28L D28L", "Runway 28L listed twice")]
    [InlineData("D", "Not a runway: D")]
    public void Parse_BadToken_ReturnsTheError(string text, string expectedError)
    {
        ActiveRunwayParseResult result = ActiveRunwayListParser.Parse("OAK", text, NavDb());

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedError, result.Error);
    }

    [Theory]
    [InlineData("NONE")]
    [InlineData(" none\r\n")]
    [InlineData("NONE,")]
    public void ParseWithNone_LoneNone_IsAnEmptyList(string text)
    {
        ActiveRunwayParseResult result = ActiveRunwayListParser.ParseWithNone("OAK", text, NavDb());

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Runways);
    }

    [Theory]
    [InlineData("NONE,28R")]
    [InlineData("28R\nNONE")]
    [InlineData("none 28R")]
    public void ParseWithNone_NoneBesideARunway_IsRefused(string text)
    {
        ActiveRunwayParseResult result = ActiveRunwayListParser.ParseWithNone("OAK", text, NavDb());

        Assert.False(result.IsSuccess);
        Assert.Equal("NONE must be the only runway", result.Error);
    }

    [Fact]
    public void ParseWithNone_WithoutNone_ReadsAsParse()
    {
        ActiveRunwayParseResult result = ActiveRunwayListParser.ParseWithNone("OAK", "D28L,\nA28R", NavDb());

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(["D28L", "A28R"], result.Runways.Select(r => r.ToToken()));
    }

    private static NavigationDatabase NavDb() =>
        TestVnasData.NavigationDb ?? throw new InvalidOperationException("Navigation database not initialized.");
}
