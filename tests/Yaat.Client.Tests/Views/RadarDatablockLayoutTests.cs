using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Radar;
using Yaat.Sim;

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
            Position = new LatLon(37.0, -122.0),
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

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal("", layout.Line4);
    }

    [Fact]
    public void HasModeC_WhenTransponderModeIsStandby()
    {
        var ac = CreateModel();
        ac.TransponderMode = "Standby";
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal("ModeC", layout.Line4);
    }

    [Fact]
    public void RectGrowsByExactlyLineHeight_WhenStandby()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();

        ac.TransponderMode = "C";
        var charlie = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        ac.TransponderMode = "Standby";
        var standby = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

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
        var withLine3Only = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        ac.TransponderMode = "Standby";
        var withBoth = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

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

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

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

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Contains("PA28", layout.Line2);
        Assert.DoesNotContain("C182", layout.Line2);
    }

    [Fact]
    public void NoLndgClnc_HiddenWhenWarningInactive()
    {
        var ac = CreateModel();
        ac.NoLandingClearanceWarningActive = false;
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: true,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal("", layout.Line5);
    }

    [Fact]
    public void NoLndgClnc_HiddenWhenUserPreferenceOff()
    {
        var ac = CreateModel();
        ac.NoLandingClearanceWarningActive = true;
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

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
            var layout = RadarDatablockLayout.Compute(
                ac,
                blockX: 100,
                blockY: 100,
                paint,
                showNoLandingClearance: true,
                showConflictAlerts: false,
                conflictPeer: null,
                callsignMarker: ""
            );
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
            var layout = RadarDatablockLayout.Compute(
                ac,
                blockX: 100,
                blockY: 100,
                paint,
                showNoLandingClearance: true,
                showConflictAlerts: false,
                conflictPeer: null,
                callsignMarker: ""
            );
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
        var baseline = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: true,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        ac.NoLandingClearanceWarningActive = true;
        var warningHeights = new HashSet<float>();
        for (int i = 0; i < 10; i++)
        {
            var layout = RadarDatablockLayout.Compute(
                ac,
                blockX: 100,
                blockY: 100,
                paint,
                showNoLandingClearance: true,
                showConflictAlerts: false,
                conflictPeer: null,
                callsignMarker: ""
            );
            warningHeights.Add(layout.Rect.Height);
            Thread.Sleep(120);
        }

        // The warning rect must always grow by exactly one line height vs the baseline — the
        // reserved slot is what keeps the rect from pulsing with the 500 ms flash cycle.
        Assert.Single(warningHeights);
        float delta = warningHeights.First() - baseline.Rect.Height;
        Assert.Equal(baseline.LineHeight, delta, precision: 3);
    }

    /// <summary>
    /// Conflict peer 2.0 nm due east of <see cref="CreateModel"/>'s position and 800 ft below it.
    /// 1 minute of longitude at 37N is ~0.7986 nm, so 2.5044' of longitude ≈ 2.0 nm.
    /// </summary>
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
            GroundSpeed = 250,
        };
    }

    [Fact]
    public void ConflictAlert_FlashesOnAndOff_OverTime()
    {
        var ac = CreateModel();
        ac.ConflictPeerCallsign = "SWA1234";
        var peer = CreateConflictPeer();
        using var paint = CreatePaint();

        bool seenOn = false;
        bool seenOff = false;
        // Same 500 ms cycle as the handoff / NoLndgClnc indicators.
        for (int i = 0; i < 12 && (!seenOn || !seenOff); i++)
        {
            var layout = RadarDatablockLayout.Compute(
                ac,
                blockX: 100,
                blockY: 100,
                paint,
                showNoLandingClearance: false,
                showConflictAlerts: true,
                conflictPeer: peer,
                callsignMarker: ""
            );
            if (layout.ConflictLine.Length > 0)
            {
                seenOn = true;
            }
            else if (layout.ConflictLine == "")
            {
                seenOff = true;
            }
            Thread.Sleep(120);
        }

        Assert.True(seenOn, "Expected the CA field to render at least once during the on-phase of the flash cycle.");
        Assert.True(seenOff, "Expected the CA field to be blank at least once during the off-phase of the flash cycle.");
    }

    [Fact]
    public void ConflictAlert_ListsHorizontalNmAndVerticalFeet()
    {
        var ac = CreateModel();
        ac.Owner = "AB";
        var peer = CreateConflictPeer();
        peer.Owner = "CD";

        // Horizontal to one decimal in nm; vertical to the nearest 100 ft, matching Mode C reporting
        // granularity. 23000 - 22200 = 800. Both tracked, so this is CA rather than MCI.
        Assert.Equal("CA 2.0/800", RadarDatablockLayout.BuildConflictLine(ac, peer));
    }

    [Fact]
    public void ConflictAlert_VerticalMatchesTheTruncatedAltitudeReadouts()
    {
        // 5099 ft displays as "050" on line 2 and 4900 ft as "049" — one hundred apart. Differencing
        // the raw altitudes and then rounding gives 199 -> 200, contradicting the two readouts directly
        // above. Quantizing each altitude first keeps the field consistent with what's on the scope.
        var ac = CreateModel();
        ac.Altitude = 5099;
        ac.Owner = "AB";
        var peer = CreateConflictPeer();
        peer.Altitude = 4900;
        peer.Owner = "CD";

        Assert.Equal("CA 2.0/100", RadarDatablockLayout.BuildConflictLine(ac, peer));
    }

    [Fact]
    public void ConflictAlert_CoAltitudePairPadsVerticalToThreeDigits()
    {
        var ac = CreateModel();
        ac.Owner = "AB";
        var peer = CreateConflictPeer();
        peer.Altitude = ac.Altitude;
        peer.Owner = "CD";

        // "000" rather than a bare "0" — matches the D3 altitude convention and reads as a measured
        // zero (the most alarming case) rather than a missing value.
        Assert.Equal("CA 2.0/000", RadarDatablockLayout.BuildConflictLine(ac, peer));
    }

    [Fact]
    public void ModeCIntruder_WhenPeerIsUntrackedAndUncorrelated()
    {
        // P/CG: a conflict between a tracked target and an untracked one is a Mode C Intruder alert,
        // not a conflict alert. 7110.65 §5-14-6 treats CA and MCI as distinct alert types.
        var ac = CreateModel();
        ac.Owner = "AB";
        var peer = CreateConflictPeer();

        Assert.StartsWith("MCI ", RadarDatablockLayout.BuildConflictLine(ac, peer), StringComparison.Ordinal);
    }

    [Fact]
    public void ModeCIntruder_ShowsOnBothMembers_SinceItClassifiesThePair()
    {
        // The tracked side of the pair must read MCI too — the alert type is a property of the pair,
        // so the two datablocks can't disagree about what kind of alert is active.
        var ac = CreateModel();
        ac.Owner = "AB";
        var peer = CreateConflictPeer();

        Assert.StartsWith("MCI ", RadarDatablockLayout.BuildConflictLine(ac, peer), StringComparison.Ordinal);
        Assert.StartsWith("MCI ", RadarDatablockLayout.BuildConflictLine(peer, ac), StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictAlert_WhenUntrackedPeerIsCorrelatedByFlightPlan()
    {
        // An untracked target that still correlates to a flight plan is a known aircraft, so the pair
        // is a CA, not an MCI.
        var ac = CreateModel();
        ac.Owner = "AB";
        var peer = CreateConflictPeer();
        peer.Destination = "KSFO";

        Assert.StartsWith("CA ", RadarDatablockLayout.BuildConflictLine(ac, peer), StringComparison.Ordinal);
        Assert.StartsWith("CA ", RadarDatablockLayout.BuildConflictLine(peer, ac), StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictAlert_FallsBackToBareCa_WhenPeerUnresolved()
    {
        var ac = CreateModel();

        // The peer can be absent when it has left the scope or its first position update hasn't
        // landed yet — the alert must still show rather than vanishing.
        Assert.Equal("CA", RadarDatablockLayout.BuildConflictLine(ac, peer: null));
    }

    [Fact]
    public void ConflictAlert_RectReservesSpaceEvenDuringOffPhase()
    {
        var ac = CreateModel();
        var peer = CreateConflictPeer();
        using var paint = CreatePaint();

        var baseline = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: true,
            conflictPeer: peer,
            callsignMarker: ""
        );

        ac.ConflictPeerCallsign = "SWA1234";
        var conflictHeights = new HashSet<float>();
        var conflictWidths = new HashSet<float>();
        for (int i = 0; i < 10; i++)
        {
            var layout = RadarDatablockLayout.Compute(
                ac,
                blockX: 100,
                blockY: 100,
                paint,
                showNoLandingClearance: false,
                showConflictAlerts: true,
                conflictPeer: peer,
                callsignMarker: ""
            );
            conflictHeights.Add(layout.Rect.Height);
            conflictWidths.Add(layout.Rect.Width);
            Thread.Sleep(120);
        }

        // The reserved slot is what keeps the rect — and thus the leader endpoint and hit area —
        // from pulsing with the 500 ms flash cycle. Width matters as much as height here: the CA
        // field is wider than the callsign line once separation values are appended.
        Assert.Single(conflictHeights);
        Assert.Single(conflictWidths);
        float delta = conflictHeights.First() - baseline.Rect.Height;
        Assert.Equal(baseline.LineHeight, delta, precision: 3);
    }

    [Fact]
    public void ConflictAlert_Suppressed_WhenToggleOff()
    {
        var ac = CreateModel();
        ac.ConflictPeerCallsign = "SWA1234";
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: CreateConflictPeer(),
            callsignMarker: ""
        );

        Assert.Equal("", layout.ConflictLine);
    }

    // --- Student-scope collapsed datablock content (mirrors CRC BuildLdb/BuildPdb) ---

    [Fact]
    public void CollapsedLdb_ShowsBeaconThenAltitude()
    {
        var ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Limited;
        ac.BeaconCode = 1200;
        ac.Altitude = 3500;

        var lines = RadarDatablockLayout.BuildCollapsedLines(ac);

        // LDB default = beacon code + altitude; ground speed is hidden unless queried.
        Assert.Equal(["1200 035"], lines);
    }

    [Fact]
    public void CollapsedPdb_ShowsAltitudeHandoffGroundSpeed()
    {
        var ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Partial;
        ac.Altitude = 3500;
        ac.GroundSpeed = 120;
        ac.HandoffPeerSectorCode = "2S";

        var lines = RadarDatablockLayout.BuildCollapsedLines(ac);

        // PDB mirrors the FDB altitude line: altitude, receiving sector during handoff, ground-speed tens.
        Assert.Equal(["035 2S 12"], lines);
    }

    [Fact]
    public void CollapsedPdb_OmitsHandoffAndAddsScratchpadLine()
    {
        var ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Partial;
        ac.Altitude = 3500;
        ac.GroundSpeed = 120;
        ac.Scratchpad1 = "CCR";

        var lines = RadarDatablockLayout.BuildCollapsedLines(ac);

        Assert.Equal(["035 12", "CCR"], lines);
    }

    [Fact]
    public void StudentLevelMarker_MapsEachLevel()
    {
        Assert.Equal(" (LDB)", RadarDatablockLayout.StudentLevelMarker(StarsDatablockLevel.Limited));
        Assert.Equal(" (PDB)", RadarDatablockLayout.StudentLevelMarker(StarsDatablockLevel.Partial));
        Assert.Equal("", RadarDatablockLayout.StudentLevelMarker(StarsDatablockLevel.Full));
        Assert.Equal("", RadarDatablockLayout.StudentLevelMarker(null));
    }

    // --- Pending outgoing point-out indicator (e.g. 3E*) on the owner/scratchpad line ---

    [Fact]
    public void OutgoingPointout_RendersTcpStarAfterOwner_BeforeScratchpads()
    {
        var ac = CreateModel();
        ac.OwnerSectorCode = "2S";
        ac.Scratchpad1 = "ABC";
        ac.Scratchpad2 = "XY";
        ac.PointoutToTcpCode = "3E";
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal("2S 3E* .ABC +XY", layout.Line3);
    }

    [Fact]
    public void OutgoingPointout_AbsentByDefault()
    {
        var ac = CreateModel();
        ac.OwnerSectorCode = "2S";
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal("2S", layout.Line3);
    }

    [Fact]
    public void Note_BlankWhenNoNote()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal("", layout.Line6);
    }

    [Fact]
    public void Note_RendersAsLine6_AndGrowsRectByOneLine()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();

        var baseline = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        ac.Note = "Watch wake";
        var withNote = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal("Watch wake", withNote.Line6);
        Assert.Equal(baseline.LineCount + 1, withNote.LineCount);
        float delta = withNote.Rect.Bottom - baseline.Rect.Bottom;
        Assert.Equal(baseline.LineHeight, delta, precision: 3);
    }

    // --- Offset precedence (datablock deconfliction): manual > deconflict > leader-dir > default ---

    [Fact]
    public void ResolveBlockOffset_ManualBeatsDeconflict()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();
        var rectAtOrigin = RadarDatablockLayout
            .Compute(ac, 0, 0, paint, showNoLandingClearance: false, showConflictAlerts: false, conflictPeer: null, callsignMarker: "")
            .Rect;
        var manual = new SKPoint(5, 5);

        var result = RadarDatablockLayout.ResolveBlockOffset(
            ac,
            syncLeader: true,
            hasManual: true,
            manual,
            rectAtOrigin,
            deconflictOffset: new SKPoint(99, 99)
        );

        Assert.Equal(manual, result);
    }

    [Fact]
    public void ResolveBlockOffset_DeconflictBeatsLeaderDirection()
    {
        var ac = CreateModel();
        ac.StudentLeaderDirection = 8; // North, non-default
        using var paint = CreatePaint();
        var rectAtOrigin = RadarDatablockLayout
            .Compute(ac, 0, 0, paint, showNoLandingClearance: false, showConflictAlerts: false, conflictPeer: null, callsignMarker: "")
            .Rect;
        var deconflict = new SKPoint(99, 99);

        var result = RadarDatablockLayout.ResolveBlockOffset(ac, syncLeader: true, hasManual: false, default, rectAtOrigin, deconflict);

        Assert.Equal(deconflict, result);
    }

    [Fact]
    public void ResolveBlockOffset_DeconflictBeatsDefault()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();
        var rectAtOrigin = RadarDatablockLayout
            .Compute(ac, 0, 0, paint, showNoLandingClearance: false, showConflictAlerts: false, conflictPeer: null, callsignMarker: "")
            .Rect;
        var deconflict = new SKPoint(99, 99);

        var result = RadarDatablockLayout.ResolveBlockOffset(ac, syncLeader: false, hasManual: false, default, rectAtOrigin, deconflict);

        Assert.Equal(deconflict, result);
    }

    [Fact]
    public void ResolveBlockOffset_NullDeconflict_ReproducesLeaderDirection()
    {
        var ac = CreateModel();
        ac.StudentLeaderDirection = 8; // North
        using var paint = CreatePaint();
        var rectAtOrigin = RadarDatablockLayout
            .Compute(ac, 0, 0, paint, showNoLandingClearance: false, showConflictAlerts: false, conflictPeer: null, callsignMarker: "")
            .Rect;

        var leaderResult = RadarDatablockLayout.ResolveBlockOffset(
            ac,
            syncLeader: true,
            hasManual: false,
            default,
            rectAtOrigin,
            deconflictOffset: null
        );

        Assert.NotEqual(RadarDatablockLayout.DefaultOffset, leaderResult);
    }

    [Fact]
    public void ResolveBlockOffset_NullDeconflict_NoLeader_ReturnsDefault()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();
        var rectAtOrigin = RadarDatablockLayout
            .Compute(ac, 0, 0, paint, showNoLandingClearance: false, showConflictAlerts: false, conflictPeer: null, callsignMarker: "")
            .Rect;

        var result = RadarDatablockLayout.ResolveBlockOffset(ac, syncLeader: false, hasManual: false, default, rectAtOrigin, deconflictOffset: null);

        Assert.Equal(RadarDatablockLayout.DefaultOffset, result);
    }

    /// <summary>
    /// The block rect is translation-invariant: computing at origin and translating by (blockX, blockY)
    /// reproduces computing at (blockX, blockY). Deconfliction assembles its input rects at origin and
    /// translates them by anchor+offset, so draw and hit-test geometry agree only if this holds.
    /// </summary>
    [Fact]
    public void Compute_RectIsTranslationInvariant()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();

        var atOrigin = RadarDatablockLayout
            .Compute(ac, 0, 0, paint, showNoLandingClearance: false, showConflictAlerts: false, conflictPeer: null, callsignMarker: "")
            .Rect;
        var atOffset = RadarDatablockLayout
            .Compute(ac, 137, -52, paint, showNoLandingClearance: false, showConflictAlerts: false, conflictPeer: null, callsignMarker: "")
            .Rect;

        Assert.Equal(atOrigin.Left + 137, atOffset.Left, precision: 3);
        Assert.Equal(atOrigin.Top - 52, atOffset.Top, precision: 3);
        Assert.Equal(atOrigin.Right + 137, atOffset.Right, precision: 3);
        Assert.Equal(atOrigin.Bottom - 52, atOffset.Bottom, precision: 3);
    }

    // --- Owner/handoff slot stability (hit-test now shares RadarDatablockLayout.Compute) ---

    [Fact]
    public void HandoffOnly_ReservesOwnerSlot_RegardlessOfFlash()
    {
        var ac = CreateModel();
        ac.HandoffPeerSectorCode = "3E"; // handoff with no owner: the token flashes blank, slot must persist
        using var paint = CreatePaint();

        // ReserveOwnerSlot is computed from the stable (handoff-always) line, so it is flash-independent.
        var layout = RadarDatablockLayout.Compute(
            ac,
            0,
            0,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.True(layout.ReserveOwnerSlot);
        Assert.Equal(3, layout.LineCount); // callsign, alt+spd, reserved owner/handoff slot
    }

    [Fact]
    public void OwnerHandoff_RectStableAcrossFlashCycle()
    {
        var ac = CreateModel();
        ac.OwnerSectorCode = "2S";
        ac.HandoffPeerSectorCode = "APPROACH"; // long enough that line 3 drives the block width
        ac.Scratchpad1 = "RESET";
        using var paint = CreatePaint();

        var first = RadarDatablockLayout.Compute(
            ac,
            0,
            0,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );
        // Sample across at least one full 500 ms flash cycle — the reserved slot keeps width + count constant.
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(120);
            var sample = RadarDatablockLayout.Compute(
                ac,
                0,
                0,
                paint,
                showNoLandingClearance: false,
                showConflictAlerts: false,
                conflictPeer: null,
                callsignMarker: ""
            );
            Assert.Equal(first.Rect.Width, sample.Rect.Width, precision: 3);
            Assert.Equal(first.LineCount, sample.LineCount);
        }
    }

    // --- Beacon-code mismatch line (reported code solid + assigned code dim-pulsing, CRC STARS emulation) ---

    [Fact]
    public void SquawkMismatch_LinePresent_WhenAssignedDiffersAndModeC()
    {
        var ac = CreateModel();
        ac.TransponderMode = "C";
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        // Reported code solid on the left, assigned code (which the renderer dim-pulses) on the right.
        Assert.Equal("1200 0301", layout.SquawkLine);
    }

    [Fact]
    public void SquawkMismatch_LineAbsent_WhenCodesMatch()
    {
        var ac = CreateModel();
        ac.BeaconCode = 301;
        ac.AssignedBeaconCode = 301;
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal("", layout.SquawkLine);
    }

    [Fact]
    public void SquawkMismatch_LineAbsent_WhenNoAssignedCode()
    {
        // VFR cold-call: squawking 1200 with nothing assigned yet — not a mismatch.
        var ac = CreateModel();
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 0;
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal("", layout.SquawkLine);
    }

    [Theory]
    [InlineData("Standby")]
    [InlineData("Off")]
    public void SquawkMismatch_LineAbsent_WhenTransponderNotTransmitting(string mode)
    {
        var ac = CreateModel();
        ac.TransponderMode = mode;
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal("", layout.SquawkLine);
    }

    [Theory]
    [InlineData(7500u)]
    [InlineData(7600u)]
    [InlineData(7700u)]
    public void SquawkMismatch_LineAbsent_WhenReportedIsSpecialPurposeCode(uint reported)
    {
        // An emergency/special code takes visual priority; the mismatch indicator is suppressed.
        var ac = CreateModel();
        ac.BeaconCode = reported;
        ac.AssignedBeaconCode = 301;
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal("", layout.SquawkLine);
    }

    [Fact]
    public void SquawkMismatch_LineShows_WhenSquawkingVfr1200VsDiscrete()
    {
        // The motivating case: a VFR aircraft assigned a discrete code but still squawking 1200.
        var ac = CreateModel();
        ac.FlightRules = "VFR";
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 4321;
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal("1200 4321", layout.SquawkLine);
    }

    [Fact]
    public void SquawkMismatch_LineAbsent_WhenCommandedSquawkVfr()
    {
        // Once the pilot is told to squawk VFR (SQVFR/SQV), the latch suppresses the RPO mismatch flash
        // even though the assigned discrete code still differs from the reported 1200.
        var ac = CreateModel();
        ac.TransponderMode = "C";
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        ac.CommandedSquawkVfr = true;
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal("", layout.SquawkLine);
    }

    [Fact]
    public void SquawkMismatch_LineShows_WhenAssignedButNotSquawked_AndNotCommandedVfr()
    {
        // The intended RPO aid: an assigned-but-not-yet-squawked code flashes at any datablock level
        // (unlike CRC's FDB-only line). The latch is set only once the pilot is told to squawk VFR.
        var ac = CreateModel();
        ac.TransponderMode = "C";
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        ac.CommandedSquawkVfr = false;
        using var paint = CreatePaint();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal("1200 0301", layout.SquawkLine);
    }

    [Fact]
    public void SquawkMismatch_RectGrowsByExactlyLineHeight()
    {
        var ac = CreateModel();
        using var paint = CreatePaint();

        ac.BeaconCode = 301;
        ac.AssignedBeaconCode = 301;
        var matched = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        ac.BeaconCode = 1200;
        var mismatched = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );

        Assert.Equal(matched.LineCount + 1, mismatched.LineCount);
        float delta = mismatched.Rect.Bottom - matched.Rect.Bottom;
        Assert.Equal(matched.LineHeight, delta, precision: 3);
    }

    [Fact]
    public void SquawkMismatch_RectIsTranslationInvariant()
    {
        var ac = CreateModel();
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        using var paint = CreatePaint();

        var atOrigin = RadarDatablockLayout
            .Compute(ac, 0, 0, paint, showNoLandingClearance: false, showConflictAlerts: false, conflictPeer: null, callsignMarker: "")
            .Rect;
        var atOffset = RadarDatablockLayout
            .Compute(ac, 137, -52, paint, showNoLandingClearance: false, showConflictAlerts: false, conflictPeer: null, callsignMarker: "")
            .Rect;

        Assert.Equal(atOrigin.Left + 137, atOffset.Left, precision: 3);
        Assert.Equal(atOrigin.Top - 52, atOffset.Top, precision: 3);
        Assert.Equal(atOrigin.Right + 137, atOffset.Right, precision: 3);
        Assert.Equal(atOrigin.Bottom - 52, atOffset.Bottom, precision: 3);
    }

    [Fact]
    public void SquawkMismatch_RectStableAcrossFlashCycle()
    {
        // The mismatch condition itself never flashes (only the assigned token dims in the renderer),
        // so the reserved width + line count stay constant across a full 500 ms cycle.
        var ac = CreateModel();
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        using var paint = CreatePaint();

        var first = RadarDatablockLayout.Compute(
            ac,
            0,
            0,
            paint,
            showNoLandingClearance: false,
            showConflictAlerts: false,
            conflictPeer: null,
            callsignMarker: ""
        );
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(120);
            var sample = RadarDatablockLayout.Compute(
                ac,
                0,
                0,
                paint,
                showNoLandingClearance: false,
                showConflictAlerts: false,
                conflictPeer: null,
                callsignMarker: ""
            );
            Assert.Equal(first.Rect.Width, sample.Rect.Width, precision: 3);
            Assert.Equal(first.LineCount, sample.LineCount);
            Assert.Equal("1200 0301", sample.SquawkLine);
        }
    }
}
