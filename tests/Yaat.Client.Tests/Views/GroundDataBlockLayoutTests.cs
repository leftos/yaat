using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Ground;

namespace Yaat.Client.Tests.Views;

/// <summary>
/// Verifies the ground datablock layout: NoMC indicator, line count, and rect sizing.
/// Pure-function tests on DataBlockLayout.Compute().
/// </summary>
public class GroundDataBlockLayoutTests
{
    private static AircraftModel CreateModel()
    {
        return new AircraftModel
        {
            Callsign = "UAL238",
            AircraftType = "B738",
            FlightRules = "IFR",
            Altitude = 0,
            Destination = "KSFO",
        };
    }

    private static SKPaint CreatePaint()
    {
        return new SKPaint { TextSize = 12 };
    }

    [Fact]
    public void NoModeC_WhenTransponderModeIsCharlie_OnGround()
    {
        var ac = CreateModel();
        ac.TransponderMode = "C";
        using var paint = CreatePaint();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), paint, isAirborne: false);

        Assert.Equal("", layout.Line4);
    }

    [Fact]
    public void HasModeC_WhenTransponderModeIsStandby_OnGround()
    {
        var ac = CreateModel();
        ac.TransponderMode = "Standby";
        using var paint = CreatePaint();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), paint, isAirborne: false);

        Assert.Equal("ModeC", layout.Line4);
    }

    [Fact]
    public void HasModeC_WhenAirborneStandby()
    {
        var ac = CreateModel();
        ac.Altitude = 1500;
        ac.TransponderMode = "Standby";
        using var paint = CreatePaint();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), paint, isAirborne: true);

        Assert.NotEqual("", layout.Line3); // altitude line is present when airborne
        Assert.Equal("ModeC", layout.Line4);
    }

    [Fact]
    public void RectGrowsByExactlyLineHeight_WhenStandby_OnGround()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();

        ac.TransponderMode = "C";
        var charlie = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), paint, isAirborne: false);

        ac.TransponderMode = "Standby";
        var standby = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), paint, isAirborne: false);

        float delta = standby.Rect.Bottom - charlie.Rect.Bottom;
        Assert.Equal(charlie.LineHeight, delta, precision: 3);
    }
}
