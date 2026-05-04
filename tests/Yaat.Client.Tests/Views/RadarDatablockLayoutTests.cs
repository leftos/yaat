using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Radar;

namespace Yaat.Client.Tests.Views;

/// <summary>
/// Verifies the radar full datablock layout: NoMC indicator, line count, and rect sizing.
/// Pure-function tests on RadarDatablockLayout.Compute().
/// </summary>
public class RadarDatablockLayoutTests
{
    private static AircraftModel CreateModel()
    {
        return new AircraftModel
        {
            Callsign = "UAL238",
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            Altitude = 23000,
            GroundSpeed = 250,
            CwtCode = "D",
        };
    }

    private static SKPaint CreatePaint()
    {
        return new SKPaint { TextSize = 12 };
    }

    [Fact]
    public void NoModeC_WhenTransponderModeIsCharlie()
    {
        var ac = CreateModel();
        ac.TransponderMode = "C";
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint);

        Assert.Equal("", layout.Line4);
    }

    [Fact]
    public void HasModeC_WhenTransponderModeIsStandby()
    {
        var ac = CreateModel();
        ac.TransponderMode = "Standby";
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint);

        Assert.Equal("ModeC", layout.Line4);
    }

    [Fact]
    public void RectGrowsByExactlyLineHeight_WhenStandby()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();

        ac.TransponderMode = "C";
        var charlie = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint);

        ac.TransponderMode = "Standby";
        var standby = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint);

        float delta = standby.Rect.Bottom - charlie.Rect.Bottom;
        Assert.Equal(charlie.LineHeight, delta, precision: 3);
    }

    [Fact]
    public void Standby_BothLine3AndLine4_RectGrowsByTwoLines()
    {
        var ac = CreateModel();
        ac.AssignedTo = "AB";
        using var paint = CreatePaint();

        ac.TransponderMode = "C";
        var withLine3Only = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint);

        ac.TransponderMode = "Standby";
        var withBoth = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint);

        Assert.NotEqual("", withLine3Only.Line3);
        Assert.Equal("", withLine3Only.Line4);
        Assert.NotEqual("", withBoth.Line3);
        Assert.Equal("ModeC", withBoth.Line4);

        // withLine3Only has 3 lines (callsign, alt+spd+cwt, owner). withBoth has 4 (adds ModeC).
        float delta = withBoth.Rect.Bottom - withLine3Only.Rect.Bottom;
        Assert.Equal(withLine3Only.LineHeight, delta, precision: 3);
    }
}
