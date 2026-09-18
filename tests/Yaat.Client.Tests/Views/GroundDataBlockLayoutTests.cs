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

    private static TextStyle CreateStyle() => new TextStyle(new SKFont { Size = 12 }, new SKPaint());

    [Fact]
    public void Line2_IncludesCwt_WhenCwtCodePresent()
    {
        AircraftModel ac = CreateModel();
        ac.CwtCode = "E";
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("E/B738 SFO", layout.Line2);
    }

    [Fact]
    public void Line2_OmitsCwt_WhenCwtCodeEmpty()
    {
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("B738 SFO", layout.Line2);
    }

    [Fact]
    public void Line2_CwtWithoutFix()
    {
        AircraftModel ac = CreateModel();
        ac.CwtCode = "E";
        ac.AsdexFix = "";
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("E/B738", layout.Line2);
    }

    [Fact]
    public void Line1_AppendsRunwayAndOrdinal_WhenInDepartureLine()
    {
        AircraftModel ac = CreateModel();
        ac.RunwayQueuePosition = 2;
        ac.RunwayQueueRunway = "28R";
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("UAL238 28R #2", layout.Line1);
    }

    [Fact]
    public void Line1_AppendsIntersection_WhenNotDepartingFullLength()
    {
        AircraftModel ac = CreateModel();
        ac.RunwayQueuePosition = 1;
        ac.RunwayQueueRunway = "28R";
        ac.RunwayQueueIntersection = "E";
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("UAL238 28R@E #1", layout.Line1);
    }

    [Fact]
    public void Line1_OrdinalWithoutRunway_WhenRunwayBlank()
    {
        AircraftModel ac = CreateModel();
        ac.RunwayQueuePosition = 1;
        ac.RunwayQueueRunway = "";
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("UAL238 #1", layout.Line1);
    }

    [Fact]
    public void Line1_NoQueueOrdinal_WhenNotInLine()
    {
        AircraftModel ac = CreateModel();
        ac.RunwayQueuePosition = 0;
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("UAL238", layout.Line1);
    }

    [Fact]
    public void Line1_QueueOrdinalFollowsAutoDeleteMarker()
    {
        AircraftModel ac = CreateModel();
        ac.AutoDeletePending = true;
        ac.RunwayQueuePosition = 3;
        ac.RunwayQueueRunway = "30";
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("UAL238* 30 #3", layout.Line1);
    }

    [Fact]
    public void NoSqStby_WhenTransponderModeIsCharlie_OnGround()
    {
        AircraftModel ac = CreateModel();
        ac.TransponderMode = "C";
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("", layout.Line4);
    }

    [Fact]
    public void HasSqStby_WhenTransponderModeIsStandby_OnGround()
    {
        AircraftModel ac = CreateModel();
        ac.TransponderMode = "Standby";
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("SqStby", layout.Line4);
    }

    [Fact]
    public void HasSqStby_WhenAirborneStandby()
    {
        AircraftModel ac = CreateModel();
        ac.Altitude = 1500;
        ac.TransponderMode = "Standby";
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: true);

        Assert.NotEqual("", layout.Line3); // altitude line is present when airborne
        Assert.Equal("SqStby", layout.Line4);
    }

    [Fact]
    public void RectGrowsByExactlyLineHeight_WhenStandby_OnGround()
    {
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();

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
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("", layout.Line5);
    }

    [Fact]
    public void Note_RendersAsLine5_AndGrowsRectByOneLine()
    {
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();

        var baseline = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        ac.Note = "Trainee struggling";
        var withNote = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("Trainee struggling", withNote.Line5);
        Assert.Equal(baseline.LineCount + 1, withNote.LineCount);
        float delta = withNote.Rect.Bottom - baseline.Rect.Bottom;
        Assert.Equal(baseline.LineHeight, delta, precision: 3);
    }

    private static AircraftModel CreateMismatchModel()
    {
        AircraftModel ac = CreateModel();
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        ac.TransponderMode = "C";
        return ac;
    }

    [Fact]
    public void SquawkLine_ShowsReportedThenAssigned_WhenMismatch()
    {
        AircraftModel baselineAc = CreateModel();
        AircraftModel ac = CreateMismatchModel();
        TextStyle style = CreateStyle();

        var baseline = DataBlockLayout.Compute(baselineAc, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);
        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("1200 0301", layout.SquawkLine);
        Assert.Equal("", layout.Line4);
        Assert.Equal(baseline.LineCount + 1, layout.LineCount);
    }

    [Fact]
    public void SquawkLine_Empty_WhenCodesMatch()
    {
        AircraftModel ac = CreateMismatchModel();
        ac.BeaconCode = 301;
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("", layout.SquawkLine);
        Assert.Equal(2, layout.LineCount);
    }

    [Fact]
    public void SquawkLine_Empty_WhenNoAssignedCode()
    {
        AircraftModel ac = CreateMismatchModel();
        ac.AssignedBeaconCode = 0;
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("", layout.SquawkLine);
    }

    [Fact]
    public void SquawkLine_Empty_WhenStandby_SqStbyShownInstead()
    {
        AircraftModel ac = CreateMismatchModel();
        ac.TransponderMode = "Standby";
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("", layout.SquawkLine);
        Assert.Equal("SqStby", layout.Line4);
        Assert.Equal(3, layout.LineCount);
    }

    [Fact]
    public void SquawkLine_Empty_WhenSpecialPurposeCode()
    {
        AircraftModel ac = CreateMismatchModel();
        ac.BeaconCode = 7700;
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("", layout.SquawkLine);
    }

    [Fact]
    public void SquawkLine_Empty_WhenCommandedSquawkVfr()
    {
        AircraftModel ac = CreateMismatchModel();
        ac.CommandedSquawkVfr = true;
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("", layout.SquawkLine);
    }

    [Fact]
    public void SquawkLine_CoexistsWithHoldStatus()
    {
        AircraftModel baselineAc = CreateModel();
        AircraftModel ac = CreateMismatchModel();
        ac.HoldKind = "HoldPosition";
        TextStyle style = CreateStyle();

        var baseline = DataBlockLayout.Compute(baselineAc, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);
        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        Assert.Equal("1200 0301", layout.SquawkLine);
        Assert.Equal("HOLD", layout.Line4);
        Assert.Equal(baseline.LineCount + 2, layout.LineCount);
    }

    [Fact]
    public void SquawkLine_PresentWithAltitude_WhenAirborne()
    {
        AircraftModel ac = CreateMismatchModel();
        ac.Altitude = 1500;
        TextStyle style = CreateStyle();

        var layout = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: true);

        Assert.Equal("015", layout.Line3);
        Assert.Equal("1200 0301", layout.SquawkLine);
        Assert.Equal(4, layout.LineCount);
    }

    [Fact]
    public void RectGrowsByExactlyLineHeight_WhenSquawkMismatch()
    {
        AircraftModel ac = CreateMismatchModel();
        TextStyle style = CreateStyle();

        ac.BeaconCode = 301;
        var matched = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        ac.BeaconCode = 1200;
        var mismatched = DataBlockLayout.Compute(ac, screenX: 100, screenY: 100, offset: new SKPoint(30, -25), style, isAirborne: false);

        float delta = mismatched.Rect.Bottom - matched.Rect.Bottom;
        Assert.Equal(matched.LineHeight, delta, precision: 3);
        Assert.True(mismatched.Rect.Width >= matched.Rect.Width);
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
        AircraftModel ac = CreateModel();
        ac.CwtCode = "E";
        TextStyle style = CreateStyle();

        var offset = new SKPoint(30, -25);
        SKRect atOrigin = DataBlockLayout.Compute(ac, 0, 0, SKPoint.Empty, style, isAirborne: false).Rect;
        SKRect positioned = DataBlockLayout.Compute(ac, 100, 200, offset, style, isAirborne: false).Rect;

        Assert.Equal(atOrigin.Left + 100 + offset.X, positioned.Left, precision: 3);
        Assert.Equal(atOrigin.Top + 200 + offset.Y, positioned.Top, precision: 3);
        Assert.Equal(atOrigin.Right + 100 + offset.X, positioned.Right, precision: 3);
        Assert.Equal(atOrigin.Bottom + 200 + offset.Y, positioned.Bottom, precision: 3);
    }
}
