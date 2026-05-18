using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Radar;

namespace Yaat.Client.Tests.Views;

/// <summary>
/// Verifies the EuroScope tag layout: ModeC field appears when squawking standby,
/// sits at the bottom row, and is absent when squawking normally.
/// </summary>
public class EuroScopeTagLayoutTests
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
            CwtCode = "D",
        };
    }

    private static SKPaint CreatePaint()
    {
        return new SKPaint { TextSize = 12 };
    }

    [Fact]
    public void NoModeCField_WhenTransponderModeIsCharlie()
    {
        var ac = CreateModel();
        ac.TransponderMode = "C";
        using var paint = CreatePaint();

        var result = EuroScopeTagLayout.Layout(ac, originX: 100, originY: 100, paint, localUserInitials: null);

        Assert.DoesNotContain(result.Fields, f => f.Field == TagFieldId.ModeC);
    }

    [Fact]
    public void HasModeCField_WhenTransponderModeIsStandby()
    {
        var ac = CreateModel();
        ac.TransponderMode = "Standby";
        using var paint = CreatePaint();

        var result = EuroScopeTagLayout.Layout(ac, originX: 100, originY: 100, paint, localUserInitials: null);

        var modeC = Assert.Single(result.Fields, f => f.Field == TagFieldId.ModeC);
        Assert.Equal("ModeC", modeC.Text);
    }

    [Fact]
    public void BoundsGrowByExactlyLineHeight_WhenStandby_NoLine4Fields()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();
        float lineH = paint.TextSize + 2;

        ac.TransponderMode = "C";
        var charlie = EuroScopeTagLayout.Layout(ac, originX: 100, originY: 100, paint, localUserInitials: null);

        ac.TransponderMode = "Standby";
        var standby = EuroScopeTagLayout.Layout(ac, originX: 100, originY: 100, paint, localUserInitials: null);

        float delta = standby.Bounds.Height - charlie.Bounds.Height;
        Assert.Equal(lineH, delta, precision: 3);
    }

    [Fact]
    public void ModeCSitsBelowLine3_WhenLine4Empty()
    {
        var ac = CreateModel();
        ac.TransponderMode = "Standby";
        using var paint = CreatePaint();
        float lineH = paint.TextSize + 2;

        var result = EuroScopeTagLayout.Layout(ac, originX: 100, originY: 100, paint, localUserInitials: null);

        // Line 3 (current alt) is always present. With no rwy/sp/handoff, line 4 is empty,
        // so ModeC should sit at line 4's slot (one row below line 3).
        var line3 = Assert.Single(result.Fields, f => f.Field == TagFieldId.CurrentAltitude);
        var modeC = Assert.Single(result.Fields, f => f.Field == TagFieldId.ModeC);
        Assert.Equal(line3.Rect.Top + lineH, modeC.Rect.Top, precision: 3);
    }

    [Fact]
    public void ModeCSitsBelowLine4_WhenLine4Present()
    {
        var ac = CreateModel();
        ac.TransponderMode = "Standby";
        ac.AssignedRunway = "28R";
        using var paint = CreatePaint();
        float lineH = paint.TextSize + 2;

        var result = EuroScopeTagLayout.Layout(ac, originX: 100, originY: 100, paint, localUserInitials: null);

        var rwy = Assert.Single(result.Fields, f => f.Field == TagFieldId.AssignedRunway);
        var modeC = Assert.Single(result.Fields, f => f.Field == TagFieldId.ModeC);
        Assert.Equal(rwy.Rect.Top + lineH, modeC.Rect.Top, precision: 3);
    }

    [Fact]
    public void TypeCwtField_FallsBackToPhysicalType_WhenFiledIsBlank()
    {
        // RPO guarantee: the radar EuroScope tag must always show an aircraft type when one
        // is physically known, even if the filed FP type was never set or got blanked via
        // an FP amendment. Mirrors the user-reported N775JW bug where the Aircraft List
        // showed "C182" but the EuroScope tag Line 2 had no TypeCwt field.
        var ac = CreateModel();
        ac.AircraftType = "C182";
        ac.FiledAircraftType = "";
        ac.CwtCode = "L";
        using var paint = CreatePaint();

        var result = EuroScopeTagLayout.Layout(ac, originX: 100, originY: 100, paint, localUserInitials: null);

        var typeCwt = Assert.Single(result.Fields, f => f.Field == TagFieldId.TypeCwt);
        Assert.Contains("C182", typeCwt.Text);
    }
}
