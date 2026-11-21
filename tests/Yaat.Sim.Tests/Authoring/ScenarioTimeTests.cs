using Xunit;
using Yaat.Sim.Authoring;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="ScenarioTime"/> is the scenario editor's one H:M:S parser and formatter. Bare digits fill right to left in
/// pairs (seconds, minutes, hours), so "130" is one minute thirty; the colon form reads its groups right to left too.
/// </summary>
public class ScenarioTimeTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData(null, 0)]
    [InlineData("5", 5)]
    [InlineData("30", 30)]
    [InlineData("90", 90)]
    [InlineData("130", 90)]
    [InlineData("0130", 90)]
    [InlineData("190", 150)]
    [InlineData("1234", 754)]
    [InlineData("9000", 5400)]
    [InlineData("12345", 5025)]
    [InlineData("123456", 45296)]
    [InlineData("1234567", null)]
    [InlineData("1:30", 90)]
    [InlineData("1:90", 150)]
    [InlineData("1:2:3", 3723)]
    [InlineData("01:00:00", 3600)]
    [InlineData(":30", null)]
    [InlineData("1:", null)]
    [InlineData("1:2:3:4", null)]
    [InlineData("abc", null)]
    [InlineData("-5", null)]
    [InlineData("1.5", null)]
    [InlineData("1 30", null)]
    [InlineData("١٣٠", null)]
    [InlineData("1:٣٠", null)]
    [InlineData("596523:14:07", 2147483647)]
    [InlineData("596523:14:08", null)]
    [InlineData("1234567890:00", null)]
    [InlineData(" 1:30 ", 90)]
    [InlineData("1: 30", null)]
    public void Parse_ReturnsTotalSecondsOrNull(string? text, int? expected) => Assert.Equal(expected, ScenarioTime.Parse(text));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(90, "1:30")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(5025, "1:23:45")]
    public void Format_WritesMinutesSecondsUnderAnHourAndHoursPastIt(int seconds, string expected) =>
        Assert.Equal(expected, ScenarioTime.Format(seconds));

    [Fact]
    public void Format_NegativeSeconds_Throws() => Assert.Throws<ArgumentOutOfRangeException>(() => ScenarioTime.Format(-1));
}
