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

        var layout = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint, showNoLandingClearance: false);

        Assert.Equal("", layout.Line4);
    }

    [Fact]
    public void HasModeC_WhenTransponderModeIsStandby()
    {
        var ac = CreateModel();
        ac.TransponderMode = "Standby";
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint, showNoLandingClearance: false);

        Assert.Equal("ModeC", layout.Line4);
    }

    [Fact]
    public void RectGrowsByExactlyLineHeight_WhenStandby()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();

        ac.TransponderMode = "C";
        var charlie = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint, showNoLandingClearance: false);

        ac.TransponderMode = "Standby";
        var standby = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint, showNoLandingClearance: false);

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
        var withLine3Only = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint, showNoLandingClearance: false);

        ac.TransponderMode = "Standby";
        var withBoth = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint, showNoLandingClearance: false);

        Assert.NotEqual("", withLine3Only.Line3);
        Assert.Equal("", withLine3Only.Line4);
        Assert.NotEqual("", withBoth.Line3);
        Assert.Equal("ModeC", withBoth.Line4);

        // withLine3Only has 3 lines (callsign, alt+spd+cwt, owner). withBoth has 4 (adds ModeC).
        float delta = withBoth.Rect.Bottom - withLine3Only.Rect.Bottom;
        Assert.Equal(withLine3Only.LineHeight, delta, precision: 3);
    }

    [Fact]
    public void Line2_FallsBackToPhysicalType_WhenFiledIsBlank()
    {
        // RPO guarantee: the radar datablock must always show an aircraft type when one is
        // physically known, even if the filed FP type was never set or got blanked via
        // an FP amendment. Mirrors the user-reported N775JW bug where the Aircraft List
        // showed "C182" but the radar datablock omitted the type on Line 2.
        var ac = CreateModel();
        ac.AircraftType = "C182";
        ac.FiledAircraftType = "";
        ac.CwtCode = "L";
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint, showNoLandingClearance: false);

        Assert.Contains("C182", layout.Line2);
    }

    [Fact]
    public void Line2_PrefersFiledType_WhenFiledPresent()
    {
        var ac = CreateModel();
        ac.AircraftType = "C182";
        ac.FiledAircraftType = "PA28";
        ac.CwtCode = "L";
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint, showNoLandingClearance: false);

        Assert.Contains("PA28", layout.Line2);
        Assert.DoesNotContain("C182", layout.Line2);
    }

    [Fact]
    public void NoLndgClnc_HiddenWhenWarningInactive()
    {
        var ac = CreateModel();
        ac.NoLandingClearanceWarningActive = false;
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint, showNoLandingClearance: true);

        Assert.Equal("", layout.Line5);
    }

    [Fact]
    public void NoLndgClnc_HiddenWhenUserPreferenceOff()
    {
        var ac = CreateModel();
        ac.NoLandingClearanceWarningActive = true;
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint, showNoLandingClearance: false);

        Assert.Equal("", layout.Line5);
    }

    [Fact]
    public void NoLndgClnc_HiddenWhenAutoClearedToLand()
    {
        var ac = CreateModel();
        ac.NoLandingClearanceWarningActive = true;
        ac.IsAutoClearedToLand = true;
        using var paint = CreatePaint();

        // Flash output is gated 50/50 on the wall-clock tick — sample multiple cycles so we
        // catch the on-phase too.
        for (int i = 0; i < 5; i++)
        {
            var layout = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint, showNoLandingClearance: true);
            Assert.Equal("", layout.Line5);
            // Sleep ~120 ms so the next iteration likely lands in the opposite half of the
            // 500 ms flash cycle — confirms the gate suppresses both halves.
            Thread.Sleep(120);
        }
    }

    [Fact]
    public void NoLndgClnc_FlashesOnAndOff_OverTime()
    {
        var ac = CreateModel();
        ac.NoLandingClearanceWarningActive = true;
        using var paint = CreatePaint();

        bool seenOn = false;
        bool seenOff = false;
        // The flash runs on a 500 ms cycle (Environment.TickCount64 / 500 % 2). Sampling at
        // ~120 ms intervals over ~1.4 s is enough to hit both halves at least once.
        for (int i = 0; i < 12 && (!seenOn || !seenOff); i++)
        {
            var layout = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint, showNoLandingClearance: true);
            if (layout.Line5 == "NoLndgClnc")
            {
                seenOn = true;
            }
            else if (layout.Line5 == "")
            {
                seenOff = true;
            }
            Thread.Sleep(120);
        }

        Assert.True(seenOn, "Expected the NoLndgClnc line to render at least once during the on-phase of the flash cycle.");
        Assert.True(seenOff, "Expected the NoLndgClnc line to be blank at least once during the off-phase of the flash cycle.");
    }

    [Fact]
    public void NoLndgClnc_RectReservesSpaceEvenDuringOffPhase()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();

        ac.NoLandingClearanceWarningActive = false;
        var baseline = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint, showNoLandingClearance: true);

        ac.NoLandingClearanceWarningActive = true;
        var warningHeights = new HashSet<float>();
        for (int i = 0; i < 10; i++)
        {
            var layout = RadarDatablockLayout.Compute(ac, blockX: 100, blockY: 100, paint, showNoLandingClearance: true);
            warningHeights.Add(layout.Rect.Height);
            Thread.Sleep(120);
        }

        // The warning rect must always grow by exactly one line height vs the baseline — the
        // reserved slot is what keeps the rect from pulsing with the 500 ms flash cycle.
        Assert.Single(warningHeights);
        float delta = warningHeights.First() - baseline.Rect.Height;
        Assert.Equal(baseline.LineHeight, delta, precision: 3);
    }
}
