using Xunit;
using Yaat.Client.Views;

namespace Yaat.Client.Tests.Views;

/// <summary>
/// Tests for the custom-delay parser used by the aircraft-list right-click
/// "Change spawn delay → Custom..." input on a delayed (deferred) spawn.
/// </summary>
public class DelayedSpawnDelayParserTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("30", 30)]
    [InlineData("90", 90)]
    [InlineData("3600", 3600)]
    public void Parse_BareDigits_TreatedAsSeconds(string input, int expected)
    {
        Assert.Equal(expected, DataGridView.ParseDelayInput(input));
    }

    [Theory]
    [InlineData("23s", 23)]
    [InlineData("0s", 0)]
    [InlineData("1m", 60)]
    [InlineData("5m", 300)]
    [InlineData("1h", 3600)]
    [InlineData("2h", 7200)]
    [InlineData("2m15s", 135)]
    [InlineData("1h30m", 5400)]
    [InlineData("1h2m3s", 3723)]
    public void Parse_UnitSuffixes_AccumulatesCorrectly(string input, int expected)
    {
        Assert.Equal(expected, DataGridView.ParseDelayInput(input));
    }

    [Theory]
    [InlineData("  30  ", 30)]
    [InlineData("  2m 15s ", 135)]
    [InlineData("1H30M", 5400)]
    [InlineData("2M15S", 135)]
    public void Parse_IsCaseInsensitiveAndTrimsWhitespace(string input, int expected)
    {
        Assert.Equal(expected, DataGridView.ParseDelayInput(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("-30")]
    [InlineData("1.5m")]
    [InlineData("m")]
    [InlineData("s")]
    [InlineData("1d")]
    public void Parse_InvalidInputs_ReturnsNull(string? input)
    {
        Assert.Null(DataGridView.ParseDelayInput(input));
    }
}
