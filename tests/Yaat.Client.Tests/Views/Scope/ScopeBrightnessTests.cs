using SkiaSharp;
using Xunit;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The Scope's brightness model, as CRC's ERAM brightness works: a percent scales each colour channel and keeps alpha, and
/// the DWELL modifier lifts a dwelled track's percent toward 100.
/// </summary>
public class ScopeBrightnessTests
{
    [Fact]
    public void Apply_At100_IsTheIdentity()
    {
        var color = new SKColor(255, 60, 7, 128);

        Assert.Equal(color, ScopeBrightness.Apply(color, 100));
    }

    [Fact]
    public void Apply_At0_IsBlack_KeepingAlpha() => Assert.Equal(new SKColor(0, 0, 0, 128), ScopeBrightness.Apply(new SKColor(255, 60, 7, 128), 0));

    [Fact]
    public void Apply_At50_HalvesEachChannel_Truncated() =>
        Assert.Equal(new SKColor(127, 30, 3, 200), ScopeBrightness.Apply(new SKColor(255, 60, 7, 200), 50));

    [Theory]
    [InlineData(50, 20, 100)]
    [InlineData(50, 0, 50)]
    [InlineData(50, 10, 75)]
    [InlineData(100, 0, 100)]
    [InlineData(100, 10, 100)]
    [InlineData(100, 20, 100)]
    [InlineData(0, 20, 100)]
    public void Lift_RaisesThePercentTowardFull(int percent, int dwell, int expected) => Assert.Equal(expected, ScopeBrightness.Lift(percent, dwell));

    [Fact]
    public void Default_KeepsTodaysPixels() => Assert.Equal(new ScopeBrightness(100, 60, 100, 100, 0, 100, 20), ScopeBrightness.Default);

    [Fact]
    public void Groups_HaveTheirRangesAndSteps_InSpinnerOrder() =>
        Assert.Equal(
            [
                ("mapa", 0, 100, 5),
                ("mapb", 0, 100, 5),
                ("targets", 0, 100, 5),
                ("fdb", 0, 100, 5),
                ("line4", -20, 0, 1),
                ("alerts", 0, 100, 5),
                ("dwell", 0, 20, 1),
            ],
            ScopeBrightness.Groups.Select(g => (g.Name, g.Min, g.Max, g.Step))
        );

    [Fact]
    public void Groups_ReadAndWriteTheirOwnMember()
    {
        var values = new ScopeBrightness(1, 2, 3, 4, -5, 6, 7);

        Assert.Equal([1, 2, 3, 4, -5, 6, 7], ScopeBrightness.Groups.Select(g => g.Get(values)));
        foreach (ScopeBrightnessGroup group in ScopeBrightness.Groups)
        {
            ScopeBrightness written = group.With(values, 0);
            Assert.Equal(0, group.Get(written));
            Assert.All(ScopeBrightness.Groups.Where(g => g != group), g => Assert.Equal(g.Get(values), g.Get(written)));
        }
    }

    [Theory]
    [InlineData("fdb", "fdb")]
    [InlineData("FDB", "fdb")]
    [InlineData("Line4", "line4")]
    public void FindGroup_IgnoresCase(string typed, string expected) => Assert.Equal(expected, ScopeBrightness.FindGroup(typed)?.Name);

    [Fact]
    public void FindGroup_Unknown_IsNull() => Assert.Null(ScopeBrightness.FindGroup("wx"));
}
