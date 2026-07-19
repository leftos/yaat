using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Radar;
using Yaat.Sim;

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
            Position = new LatLon(37.0, -122.0),
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

        var result = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

        Assert.DoesNotContain(result.Fields, f => f.Field == TagFieldId.ModeC);
    }

    [Fact]
    public void HasModeCField_WhenTransponderModeIsStandby()
    {
        var ac = CreateModel();
        ac.TransponderMode = "Standby";
        using var paint = CreatePaint();

        var result = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

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
        var charlie = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

        ac.TransponderMode = "Standby";
        var standby = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

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

        var result = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

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

        var result = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

        var rwy = Assert.Single(result.Fields, f => f.Field == TagFieldId.AssignedRunway);
        var modeC = Assert.Single(result.Fields, f => f.Field == TagFieldId.ModeC);
        Assert.Equal(rwy.Rect.Top + lineH, modeC.Rect.Top, precision: 3);
    }

    [Fact]
    public void NoLandingClearance_HiddenWhenWarningInactive()
    {
        var ac = CreateModel();
        ac.NoLandingClearanceWarningActive = false;
        using var paint = CreatePaint();

        var result = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: true,
            showConflictAlerts: false,
            conflictPeer: null
        );

        Assert.DoesNotContain(result.Fields, f => f.Field == TagFieldId.NoLandingClearance);
    }

    [Fact]
    public void NoLandingClearance_HiddenWhenUserPreferenceOff()
    {
        var ac = CreateModel();
        ac.NoLandingClearanceWarningActive = true;
        using var paint = CreatePaint();

        var result = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

        Assert.DoesNotContain(result.Fields, f => f.Field == TagFieldId.NoLandingClearance);
    }

    [Fact]
    public void NoLandingClearance_HiddenWhenAutoClearedToLand()
    {
        var ac = CreateModel();
        ac.NoLandingClearanceWarningActive = true;
        ac.IsAutoClearedToLand = true;
        using var paint = CreatePaint();

        for (int i = 0; i < 5; i++)
        {
            var result = EuroScopeTagLayout.Layout(
                ac,
                originX: 100,
                originY: 100,
                paint,
                localUserInitials: null,
                showNoLandingClearance: true,
                showConflictAlerts: false,
                conflictPeer: null
            );
            Assert.DoesNotContain(result.Fields, f => f.Field == TagFieldId.NoLandingClearance);
            Thread.Sleep(120);
        }
    }

    [Fact]
    public void NoLandingClearance_FlashesOnAndOff_OverTime()
    {
        var ac = CreateModel();
        ac.NoLandingClearanceWarningActive = true;
        using var paint = CreatePaint();

        bool seenOn = false;
        bool seenOff = false;
        for (int i = 0; i < 12 && (!seenOn || !seenOff); i++)
        {
            var result = EuroScopeTagLayout.Layout(
                ac,
                originX: 100,
                originY: 100,
                paint,
                localUserInitials: null,
                showNoLandingClearance: true,
                showConflictAlerts: false,
                conflictPeer: null
            );
            if (result.Fields.Any(f => f.Field == TagFieldId.NoLandingClearance))
            {
                seenOn = true;
            }
            else
            {
                seenOff = true;
            }
            Thread.Sleep(120);
        }

        Assert.True(seenOn, "Expected the NoLandingClearance field to appear at least once during the on-phase of the flash cycle.");
        Assert.True(seenOff, "Expected the NoLandingClearance field to be absent at least once during the off-phase of the flash cycle.");
    }

    [Fact]
    public void NoLandingClearance_BoundsReserveSpaceEvenDuringOffPhase()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();

        ac.NoLandingClearanceWarningActive = false;
        var baseline = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: true,
            showConflictAlerts: false,
            conflictPeer: null
        );

        ac.NoLandingClearanceWarningActive = true;
        var warningHeights = new HashSet<float>();
        for (int i = 0; i < 10; i++)
        {
            var result = EuroScopeTagLayout.Layout(
                ac,
                originX: 100,
                originY: 100,
                paint,
                localUserInitials: null,
                showNoLandingClearance: true,
                showConflictAlerts: false,
                conflictPeer: null
            );
            warningHeights.Add(result.Bounds.Height);
            Thread.Sleep(120);
        }

        Assert.Single(warningHeights);
        float delta = warningHeights.First() - baseline.Bounds.Height;
        Assert.Equal(paint.TextSize + 2, delta, precision: 3);
    }

    /// <summary>Conflict peer 2.0 nm east of <see cref="CreateModel"/> and 800 ft below it.</summary>
    private static AircraftModel CreateConflictPeer()
    {
        return new AircraftModel
        {
            Callsign = "SWA1234",
            AircraftType = "B737",
            FiledAircraftType = "B737",
            FlightRules = "IFR",
            Position = new LatLon(37.0, -122.0 + (2.5044 / 60.0)),
            Altitude = 22200,
            Owner = "CD",
        };
    }

    [Fact]
    public void ConflictAlert_BoundsReserveSpaceEvenDuringOffPhase()
    {
        var ac = CreateModel();
        var peer = CreateConflictPeer();
        using var paint = CreatePaint();

        var baseline = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: true,
            conflictPeer: peer
        );

        ac.ConflictPeerCallsign = "SWA1234";
        var conflictHeights = new HashSet<float>();
        var conflictWidths = new HashSet<float>();
        for (int i = 0; i < 10; i++)
        {
            var result = EuroScopeTagLayout.Layout(
                ac,
                originX: 100,
                originY: 100,
                paint,
                localUserInitials: null,
                showNoLandingClearance: false,
                showConflictAlerts: true,
                conflictPeer: peer
            );
            conflictHeights.Add(result.Bounds.Height);
            conflictWidths.Add(result.Bounds.Width);
            Thread.Sleep(120);
        }

        Assert.Single(conflictHeights);
        Assert.Single(conflictWidths);
        float delta = conflictHeights.First() - baseline.Bounds.Height;
        Assert.Equal(paint.TextSize + 2, delta, precision: 3);
    }

    [Fact]
    public void ConflictAlert_FieldCarriesSeparationValues()
    {
        var ac = CreateModel();
        ac.ConflictPeerCallsign = "SWA1234";
        ac.Owner = "AB";
        using var paint = CreatePaint();

        // Sample across a full flash cycle so the on-phase is guaranteed to be observed.
        TagFieldRect? conflictField = null;
        for (int i = 0; i < 12 && conflictField is null; i++)
        {
            var result = EuroScopeTagLayout.Layout(
                ac,
                originX: 100,
                originY: 100,
                paint,
                localUserInitials: null,
                showNoLandingClearance: false,
                showConflictAlerts: true,
                conflictPeer: CreateConflictPeer()
            );
            foreach (var f in result.Fields)
            {
                if (f.Field == TagFieldId.ConflictAlert)
                {
                    conflictField = f;
                }
            }
            Thread.Sleep(120);
        }

        Assert.NotNull(conflictField);
        Assert.Equal("CA 2.0/800", conflictField.Value.Text);
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

        var result = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

        var typeCwt = Assert.Single(result.Fields, f => f.Field == TagFieldId.TypeCwt);
        Assert.Contains("C182", typeCwt.Text);
    }

    [Fact]
    public void NoteField_AbsentWhenNoNote()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();

        var result = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

        Assert.DoesNotContain(result.Fields, f => f.Field == TagFieldId.Note);
    }

    [Fact]
    public void NoteField_AppearsAtBottom_AndGrowsBounds_WhenSet()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();
        float lineH = paint.TextSize + 2;

        var baseline = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

        ac.Note = "Watch wake";
        var withNote = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

        var note = Assert.Single(withNote.Fields, f => f.Field == TagFieldId.Note);
        Assert.Equal("Watch wake", note.Text);
        float delta = withNote.Bounds.Height - baseline.Bounds.Height;
        Assert.Equal(lineH, delta, precision: 3);
    }

    // --- Beacon-code mismatch field (reported code solid + assigned code dim-pulsing, CRC STARS emulation) ---

    [Fact]
    public void SquawkField_Present_WhenAssignedDiffersAndModeC()
    {
        var ac = CreateModel();
        ac.TransponderMode = "C";
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        using var paint = CreatePaint();

        var result = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

        var squawk = Assert.Single(result.Fields, f => f.Field == TagFieldId.Squawk);
        Assert.Equal("1200 0301", squawk.Text);
    }

    [Fact]
    public void SquawkField_Absent_WhenCodesMatch()
    {
        var ac = CreateModel();
        ac.BeaconCode = 301;
        ac.AssignedBeaconCode = 301;
        using var paint = CreatePaint();

        var result = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

        Assert.DoesNotContain(result.Fields, f => f.Field == TagFieldId.Squawk);
    }

    [Fact]
    public void SquawkField_Absent_WhenNoAssignedCode()
    {
        var ac = CreateModel();
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 0;
        using var paint = CreatePaint();

        var result = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

        Assert.DoesNotContain(result.Fields, f => f.Field == TagFieldId.Squawk);
    }

    [Theory]
    [InlineData("Standby")]
    [InlineData("Off")]
    public void SquawkField_Absent_WhenTransponderNotTransmitting(string mode)
    {
        var ac = CreateModel();
        ac.TransponderMode = mode;
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        using var paint = CreatePaint();

        var result = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

        Assert.DoesNotContain(result.Fields, f => f.Field == TagFieldId.Squawk);
    }

    [Fact]
    public void SquawkField_Absent_WhenReportedIsSpecialPurposeCode()
    {
        var ac = CreateModel();
        ac.BeaconCode = 7700;
        ac.AssignedBeaconCode = 301;
        using var paint = CreatePaint();

        var result = EuroScopeTagLayout.Layout(
            ac,
            originX: 100,
            originY: 100,
            paint,
            localUserInitials: null,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null
        );

        Assert.DoesNotContain(result.Fields, f => f.Field == TagFieldId.Squawk);
    }
}
