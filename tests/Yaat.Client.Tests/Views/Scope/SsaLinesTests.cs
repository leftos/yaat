using Xunit;
using Yaat.Client.Views.Scope.Dock;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>The SSA's two lines, in CRC's format, from the time, altimeter, primary airport, sign-in state and range.</summary>
public class SsaLinesTests
{
    private static readonly DateTime At184732 = new(2026, 11, 14, 18, 47, 32, DateTimeKind.Utc);

    [Fact]
    public void InARoom_WithWeatherAndAnAirport_GivesBothLines()
    {
        var lines = SsaLines.Build(At184732, 30.02, "KSFO", true, 40);

        Assert.Equal("1847/32 30.02 KSFO", lines.Line1);
        Assert.Equal("OK/OK/NA FUSED 40NM", lines.Line2);
        Assert.Equal("OK/OK/NA", lines.Status);
        Assert.False(lines.IsStatusAlert);
    }

    [Fact]
    public void NoAltimeter_ShowsDashes()
    {
        var lines = SsaLines.Build(At184732, null, "KSFO", true, 40);

        Assert.Equal("1847/32 --.-- KSFO", lines.Line1);
    }

    [Fact]
    public void NoAirport_LineOneIsTheTimeAlone()
    {
        var lines = SsaLines.Build(At184732, 30.02, null, true, 40);

        Assert.Equal("1847/32", lines.Line1);
    }

    [Fact]
    public void NotInARoom_IsNaFlaggedRed()
    {
        var lines = SsaLines.Build(At184732, 30.02, "KSFO", false, 40);

        Assert.Equal("NA/NA/NA FUSED 40NM", lines.Line2);
        Assert.Equal("NA/NA/NA", lines.Status);
        Assert.True(lines.IsStatusAlert);
    }

    [Fact]
    public void Range_IsRounded()
    {
        var lines = SsaLines.Build(At184732, 30.02, "KSFO", true, 39.6);

        Assert.Equal("OK/OK/NA FUSED 40NM", lines.Line2);
    }

    [Fact]
    public void Midnight_IsAllZeros()
    {
        var lines = SsaLines.Build(new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc), null, null, true, 40);

        Assert.Equal("0000/00", lines.Line1);
    }
}
