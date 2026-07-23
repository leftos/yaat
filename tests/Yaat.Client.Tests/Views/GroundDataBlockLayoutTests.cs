using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Map;

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
            // Ground datablock line 2 shows the server-resolved ASDE-style fix (already normalized),
            // not the raw destination.
            AsdexFix = "SFO",
        };
    }

    private static TextStyle CreateStyle()
    {
        return new TextStyle(new SKFont { Size = 12 }, new SKPaint());
    }

    [Fact]
    public void Line2_IncludesCwt_WhenCwtCodePresent()
    {
        var ac = CreateModel();
        ac.CwtCode = "E";
        var style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("E/B738 SFO", layout.Line2);
    }

    [Fact]
    public void Line2_OmitsCwt_WhenCwtCodeEmpty()
    {
        var ac = CreateModel();
        var style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("B738 SFO", layout.Line2);
    }

    [Fact]
    public void Line2_CwtWithoutFix()
    {
        var ac = CreateModel();
        ac.CwtCode = "E";
        ac.AsdexFix = "";
        var style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("E/B738", layout.Line2);
    }

    [Fact]
    public void Line1_AppendsRunwayAndOrdinal_WhenInDepartureLine()
    {
        var ac = CreateModel();
        ac.RunwayQueuePosition = 2;
        ac.RunwayQueueRunway = "28R";
        var style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("UAL238 28R #2", layout.Line1);
    }

    [Fact]
    public void Line1_OrdinalWithoutRunway_WhenRunwayBlank()
    {
        var ac = CreateModel();
        ac.RunwayQueuePosition = 1;
        ac.RunwayQueueRunway = "";
        var style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("UAL238 #1", layout.Line1);
    }

    [Fact]
    public void Line1_NoQueueOrdinal_WhenNotInLine()
    {
        var ac = CreateModel();
        ac.RunwayQueuePosition = 0;
        var style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("UAL238", layout.Line1);
    }

    [Fact]
    public void Line1_QueueOrdinalFollowsAutoDeleteMarker()
    {
        var ac = CreateModel();
        ac.AutoDeletePending = true;
        ac.RunwayQueuePosition = 3;
        ac.RunwayQueueRunway = "30";
        var style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("UAL238* 30 #3", layout.Line1);
    }

    [Fact]
    public void NoSqStby_WhenTransponderModeIsCharlie_OnGround()
    {
        var ac = CreateModel();
        ac.TransponderMode = "C";
        var style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("", layout.Line4);
    }

    [Fact]
    public void HasSqStby_WhenTransponderModeIsStandby_OnGround()
    {
        var ac = CreateModel();
        ac.TransponderMode = "Standby";
        var style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("SqStby", layout.Line4);
    }

    [Fact]
    public void HasSqStby_WhenAirborneStandby()
    {
        var ac = CreateModel();
        ac.Altitude = 1500;
        ac.TransponderMode = "Standby";
        var style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: true);

        Assert.NotEqual("", layout.Line3); // altitude line is present when airborne
        Assert.Equal("SqStby", layout.Line4);
    }

    [Fact]
    public void RectGrowsByExactlyLineHeight_WhenStandby_OnGround()
    {
        var ac = CreateModel();
        var style = CreateStyle();

        ac.TransponderMode = "C";
        var charlie = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        ac.TransponderMode = "Standby";
        var standby = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        float delta = standby.Rect.Bottom - charlie.Rect.Bottom;
        Assert.Equal(charlie.LineHeight, delta, precision: 3);
    }

    [Fact]
    public void Note_BlankWhenNoNote()
    {
        var ac = CreateModel();
        var style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("", layout.Line5);
    }

    [Fact]
    public void Note_RendersAsLine5_AndGrowsRectByOneLine()
    {
        var ac = CreateModel();
        var style = CreateStyle();

        var baseline = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        ac.Note = "Trainee struggling";
        var withNote = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("Trainee struggling", withNote.Line5);
        Assert.Equal(baseline.LineCount + 1, withNote.LineCount);
        float delta = withNote.Rect.Bottom - baseline.Rect.Bottom;
        Assert.Equal(baseline.LineHeight, delta, precision: 3);
    }

    /// <summary>
    /// The block rect is translation-invariant: computing at origin (offset 0) and translating by
    /// (screen + offset) reproduces computing at that screen position with the offset. Deconfliction
    /// builds its input rect at origin and translates by anchor+offset, so draw and hit-test geometry
    /// agree only if this holds.
    /// </summary>
    [Fact]
    public void Compute_RectIsTranslationInvariant()
    {
        var ac = CreateModel();
        ac.CwtCode = "E";
        var style = CreateStyle();

        var offset = new SKPoint(30, -25);
        var atOrigin = DataBlockLayout.Compute(ac, 0, 0, SKPoint.Empty, style, isAirborne: false).Rect;
        var positioned = DataBlockLayout.Compute(ac, 100, 200, offset, style, isAirborne: false).Rect;

        Assert.Equal(atOrigin.Left + 100 + offset.X, positioned.Left, precision: 3);
        Assert.Equal(atOrigin.Top + 200 + offset.Y, positioned.Top, precision: 3);
        Assert.Equal(atOrigin.Right + 100 + offset.X, positioned.Right, precision: 3);
        Assert.Equal(atOrigin.Bottom + 200 + offset.Y, positioned.Bottom, precision: 3);
    }
}
