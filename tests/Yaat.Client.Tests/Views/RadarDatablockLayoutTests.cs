using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Sim;
using Yaat.Sim.Data.Vnas;

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

    private static TextStyle CreateStyle() => new(new SKFont { Size = 12 }, new SKPaint());

    /// <summary>Both alert overlays off with no peers — the baseline for every case that isn't about CA or ATPA.</summary>
    private static DatablockOverlays None => new(false, null, false, null);

    [Fact]
    public void NoModeC_WhenTransponderModeIsCharlie()
    {
        AircraftModel ac = CreateModel();
        ac.TransponderMode = "C";
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("", layout.Line4);
    }

    [Fact]
    public void HasModeC_WhenTransponderModeIsStandby()
    {
        AircraftModel ac = CreateModel();
        ac.TransponderMode = "Standby";
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("ModeC", layout.Line4);
    }

    [Fact]
    public void RectGrowsByExactlyLineHeight_WhenStandby()
    {
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();

        ac.TransponderMode = "C";
        var charlie = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        ac.TransponderMode = "Standby";
        var standby = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        float delta = standby.Rect.Bottom - charlie.Rect.Bottom;
        Assert.Equal(charlie.LineHeight, delta, precision: 3);
    }

    [Fact]
    public void Standby_BothLine3AndLine4_RectGrowsByTwoLines()
    {
        AircraftModel ac = CreateModel();
        ac.AssignedTo = "AB";
        TextStyle style = CreateStyle();

        ac.TransponderMode = "C";
        var withLine3Only = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        ac.TransponderMode = "Standby";
        var withBoth = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
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
        AircraftModel ac = CreateModel();
        ac.AircraftType = "C182";
        ac.FiledAircraftType = "";
        ac.CwtCode = "L";
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Contains("C182", layout.Line2);
    }

    [Fact]
    public void Line2_PrefersFiledType_WhenFiledPresent()
    {
        AircraftModel ac = CreateModel();
        ac.AircraftType = "C182";
        ac.FiledAircraftType = "PA28";
        ac.CwtCode = "L";
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Contains("PA28", layout.Line2);
        Assert.DoesNotContain("C182", layout.Line2);
    }

    [Fact]
    public void TypeTokenSpan_CoversTheCwtTypeToken_OnLine2()
    {
        // The renderer tints this span amber when the filed type differs from the physical type, so the
        // span must land exactly on the "cwt/type" token the line carries — text unchanged either way.
        AircraftModel ac = CreateModel();
        ac.AircraftType = "A388";
        ac.FiledAircraftType = "B744";
        ac.CwtCode = "L";
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.NotEqual(-1, layout.TypeTokenStart);
        Assert.Equal("L/B744", layout.Line2.Substring(layout.TypeTokenStart, layout.TypeTokenLength));
    }

    [Fact]
    public void TypeTokenSpan_IsAbsent_WhenNoTypeOrCwt()
    {
        AircraftModel ac = CreateModel();
        ac.AircraftType = "";
        ac.FiledAircraftType = "";
        ac.CwtCode = "";
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal(-1, layout.TypeTokenStart);
        Assert.Equal(0, layout.TypeTokenLength);
    }

    [Fact]
    public void NoLndgClnc_HiddenWhenWarningInactive()
    {
        AircraftModel ac = CreateModel();
        ac.NoLandingClearanceWarningActive = false;
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: true,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("", layout.Line5);
    }

    [Fact]
    public void NoLndgClnc_HiddenWhenUserPreferenceOff()
    {
        AircraftModel ac = CreateModel();
        ac.NoLandingClearanceWarningActive = true;
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("", layout.Line5);
    }

    [Fact]
    public void NoLndgClnc_HiddenWhenAutoClearedToLand()
    {
        AircraftModel ac = CreateModel();
        ac.NoLandingClearanceWarningActive = true;
        ac.IsAutoClearedToLand = true;
        TextStyle style = CreateStyle();

        // Flash output is gated 50/50 on the wall-clock tick — sample multiple cycles so we
        // catch the on-phase too.
        for (int i = 0; i < 5; i++)
        {
            var layout = RadarDatablockLayout.Compute(
                ac,
                blockX: 100,
                blockY: 100,
                style,
                showNoLandingClearance: true,
                overlays: None,
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
        AircraftModel ac = CreateModel();
        ac.NoLandingClearanceWarningActive = true;
        TextStyle style = CreateStyle();

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
                style,
                showNoLandingClearance: true,
                overlays: None,
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
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();

        ac.NoLandingClearanceWarningActive = false;
        var baseline = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: true,
            overlays: None,
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
                style,
                showNoLandingClearance: true,
                overlays: None,
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
        AircraftModel ac = CreateModel();
        ac.ConflictPeerCallsign = "SWA1234";
        AircraftModel peer = CreateConflictPeer();
        TextStyle style = CreateStyle();

        bool seenOn = false;
        bool seenOff = false;
        // Same 500 ms cycle as the handoff / NoLndgClnc indicators.
        for (int i = 0; i < 12 && (!seenOn || !seenOff); i++)
        {
            var layout = RadarDatablockLayout.Compute(
                ac,
                blockX: 100,
                blockY: 100,
                style,
                showNoLandingClearance: false,
                overlays: new DatablockOverlays(true, peer, false, null),
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
        AircraftModel ac = CreateModel();
        ac.Owner = "AB";
        AircraftModel peer = CreateConflictPeer();
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
        AircraftModel ac = CreateModel();
        ac.Altitude = 5099;
        ac.Owner = "AB";
        AircraftModel peer = CreateConflictPeer();
        peer.Altitude = 4900;
        peer.Owner = "CD";

        Assert.Equal("CA 2.0/100", RadarDatablockLayout.BuildConflictLine(ac, peer));
    }

    [Fact]
    public void ConflictAlert_CoAltitudePairPadsVerticalToThreeDigits()
    {
        AircraftModel ac = CreateModel();
        ac.Owner = "AB";
        AircraftModel peer = CreateConflictPeer();
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
        AircraftModel ac = CreateModel();
        ac.Owner = "AB";
        AircraftModel peer = CreateConflictPeer();

        Assert.StartsWith("MCI ", RadarDatablockLayout.BuildConflictLine(ac, peer), StringComparison.Ordinal);
    }

    [Fact]
    public void ModeCIntruder_ShowsOnBothMembers_SinceItClassifiesThePair()
    {
        // The tracked side of the pair must read MCI too — the alert type is a property of the pair,
        // so the two datablocks can't disagree about what kind of alert is active.
        AircraftModel ac = CreateModel();
        ac.Owner = "AB";
        AircraftModel peer = CreateConflictPeer();

        Assert.StartsWith("MCI ", RadarDatablockLayout.BuildConflictLine(ac, peer), StringComparison.Ordinal);
        Assert.StartsWith("MCI ", RadarDatablockLayout.BuildConflictLine(peer, ac), StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictAlert_WhenUntrackedPeerIsCorrelatedByFlightPlan()
    {
        // An untracked target that still correlates to a flight plan is a known aircraft, so the pair
        // is a CA, not an MCI.
        AircraftModel ac = CreateModel();
        ac.Owner = "AB";
        AircraftModel peer = CreateConflictPeer();
        peer.Destination = "KSFO";

        Assert.StartsWith("CA ", RadarDatablockLayout.BuildConflictLine(ac, peer), StringComparison.Ordinal);
        Assert.StartsWith("CA ", RadarDatablockLayout.BuildConflictLine(peer, ac), StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictAlert_FallsBackToBareCa_WhenPeerUnresolved()
    {
        AircraftModel ac = CreateModel();

        // The peer can be absent when it has left the scope or its first position update hasn't
        // landed yet — the alert must still show rather than vanishing.
        Assert.Equal("CA", RadarDatablockLayout.BuildConflictLine(ac, peer: null));
    }

    [Fact]
    public void ConflictAlert_RectReservesSpaceEvenDuringOffPhase()
    {
        AircraftModel ac = CreateModel();
        AircraftModel peer = CreateConflictPeer();
        TextStyle style = CreateStyle();

        var baseline = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: new DatablockOverlays(true, peer, false, null),
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
                style,
                showNoLandingClearance: false,
                overlays: new DatablockOverlays(true, peer, false, null),
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
        AircraftModel ac = CreateModel();
        ac.ConflictPeerCallsign = "SWA1234";
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: new DatablockOverlays(false, CreateConflictPeer(), false, null),
            callsignMarker: ""
        );

        Assert.Equal("", layout.ConflictLine);
    }

    // --- Student-scope collapsed datablock content (mirrors CRC BuildLdb/BuildPdb) ---

    [Fact]
    public void CollapsedLdb_ShowsBeaconThenAltitude()
    {
        AircraftModel ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Limited;
        ac.BeaconCode = 1200;
        ac.Altitude = 3500;

        IReadOnlyList<string> lines = RadarDatablockLayout.BuildCollapsedLines(ac);

        // LDB default = beacon code + altitude; ground speed is hidden unless queried.
        Assert.Equal(["1200 035"], lines);
    }

    [Fact]
    public void CollapsedPdb_ShowsAltitudeHandoffGroundSpeed()
    {
        AircraftModel ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Partial;
        ac.Altitude = 3500;
        ac.GroundSpeed = 120;
        ac.HandoffPeerSectorCode = "2S";

        IReadOnlyList<string> lines = RadarDatablockLayout.BuildCollapsedLines(ac);

        // PDB mirrors the FDB altitude line: altitude, receiving sector during handoff, ground-speed tens.
        Assert.Equal(["035 2S 12"], lines);
    }

    [Fact]
    public void CollapsedPdb_OmitsHandoffAndAddsScratchpadLine()
    {
        AircraftModel ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Partial;
        ac.Altitude = 3500;
        ac.GroundSpeed = 120;
        ac.Scratchpad1 = "CCR";

        IReadOnlyList<string> lines = RadarDatablockLayout.BuildCollapsedLines(ac);

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

    // --- Automatic primary scratchpad (destination fallback, GitHub issue #303) ---

    [Fact]
    public void AutoScratchpad1_RendersLikeRealScratchpad_WhenSp1Empty()
    {
        AircraftModel ac = CreateModel();
        ac.OwnerSectorCode = "2S";
        ac.AutoScratchpad1 = "OAK";
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("2S .OAK", layout.Line3);
    }

    [Fact]
    public void RealScratchpad1_WinsOverAutoScratchpad1()
    {
        AircraftModel ac = CreateModel();
        ac.OwnerSectorCode = "2S";
        ac.Scratchpad1 = "ABC";
        ac.AutoScratchpad1 = "OAK";
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("2S .ABC", layout.Line3);
    }

    [Fact]
    public void NoScratchpads_OmitsTokenEntirely()
    {
        AircraftModel ac = CreateModel();
        ac.OwnerSectorCode = "2S";

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            CreateStyle(),
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("2S", layout.Line3);
        Assert.DoesNotContain(".", layout.Line3, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoScratchpad1_AppearsOnCollapsedPdb()
    {
        AircraftModel ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Partial;
        ac.Altitude = 3500;
        ac.GroundSpeed = 120;
        ac.AutoScratchpad1 = "SFO";

        IReadOnlyList<string> lines = RadarDatablockLayout.BuildCollapsedLines(ac);

        Assert.Equal(["035 12", "SFO"], lines);
    }

    [Fact]
    public void EffectiveScratchpad1_PrefersRealThenAuto()
    {
        AircraftModel ac = CreateModel();
        Assert.Null(RadarDatablockLayout.EffectiveScratchpad1(ac));

        ac.AutoScratchpad1 = "OAK";
        Assert.Equal("OAK", RadarDatablockLayout.EffectiveScratchpad1(ac));

        ac.Scratchpad1 = "ABC";
        Assert.Equal("ABC", RadarDatablockLayout.EffectiveScratchpad1(ac));
    }

    // --- Pending outgoing point-out indicator (e.g. 3E*) on the owner/scratchpad line ---

    [Fact]
    public void OutgoingPointout_RendersTcpStarAfterOwner_BeforeScratchpads()
    {
        AircraftModel ac = CreateModel();
        ac.OwnerSectorCode = "2S";
        ac.Scratchpad1 = "ABC";
        ac.Scratchpad2 = "XY";
        ac.PointoutToTcpCode = "3E";
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("2S 3E* .ABC +XY", layout.Line3);
    }

    [Fact]
    public void OutgoingPointout_AbsentByDefault()
    {
        AircraftModel ac = CreateModel();
        ac.OwnerSectorCode = "2S";
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("2S", layout.Line3);
    }

    [Fact]
    public void Note_BlankWhenNoNote()
    {
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("", layout.Line6);
    }

    [Fact]
    public void Note_RendersAsLine6_AndGrowsRectByOneLine()
    {
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();

        var baseline = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        ac.Note = "Watch wake";
        var withNote = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
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
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();
        SKRect rectAtOrigin = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "").Rect;
        var manual = new SKPoint(5, 5);

        SKPoint result = RadarDatablockLayout.ResolveBlockOffset(
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
        AircraftModel ac = CreateModel();
        ac.StudentLeaderDirection = 8; // North, non-default
        TextStyle style = CreateStyle();
        SKRect rectAtOrigin = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "").Rect;
        var deconflict = new SKPoint(99, 99);

        SKPoint result = RadarDatablockLayout.ResolveBlockOffset(ac, syncLeader: true, hasManual: false, default, rectAtOrigin, deconflict);

        Assert.Equal(deconflict, result);
    }

    [Fact]
    public void ResolveBlockOffset_DeconflictBeatsDefault()
    {
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();
        SKRect rectAtOrigin = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "").Rect;
        var deconflict = new SKPoint(99, 99);

        SKPoint result = RadarDatablockLayout.ResolveBlockOffset(ac, syncLeader: false, hasManual: false, default, rectAtOrigin, deconflict);

        Assert.Equal(deconflict, result);
    }

    [Fact]
    public void ResolveBlockOffset_NullDeconflict_ReproducesLeaderDirection()
    {
        AircraftModel ac = CreateModel();
        ac.StudentLeaderDirection = 8; // North
        TextStyle style = CreateStyle();
        SKRect rectAtOrigin = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "").Rect;

        SKPoint leaderResult = RadarDatablockLayout.ResolveBlockOffset(
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
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();
        SKRect rectAtOrigin = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "").Rect;

        SKPoint result = RadarDatablockLayout.ResolveBlockOffset(
            ac,
            syncLeader: false,
            hasManual: false,
            default,
            rectAtOrigin,
            deconflictOffset: null
        );

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
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();

        SKRect atOrigin = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "").Rect;
        SKRect atOffset = RadarDatablockLayout.Compute(ac, 137, -52, style, showNoLandingClearance: false, overlays: None, callsignMarker: "").Rect;

        Assert.Equal(atOrigin.Left + 137, atOffset.Left, precision: 3);
        Assert.Equal(atOrigin.Top - 52, atOffset.Top, precision: 3);
        Assert.Equal(atOrigin.Right + 137, atOffset.Right, precision: 3);
        Assert.Equal(atOrigin.Bottom - 52, atOffset.Bottom, precision: 3);
    }

    // --- Owner/handoff slot stability (hit-test now shares RadarDatablockLayout.Compute) ---

    [Fact]
    public void HandoffOnly_ReservesOwnerSlot_RegardlessOfFlash()
    {
        AircraftModel ac = CreateModel();
        ac.HandoffPeerSectorCode = "3E"; // handoff with no owner: the token flashes blank, slot must persist
        TextStyle style = CreateStyle();

        // ReserveOwnerSlot is computed from the stable (handoff-always) line, so it is flash-independent.
        var layout = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "");

        Assert.True(layout.ReserveOwnerSlot);
        Assert.Equal(3, layout.LineCount); // callsign, alt+spd, reserved owner/handoff slot
    }

    [Fact]
    public void OwnerHandoff_RectStableAcrossFlashCycle()
    {
        AircraftModel ac = CreateModel();
        ac.OwnerSectorCode = "2S";
        ac.HandoffPeerSectorCode = "APPROACH"; // long enough that line 3 drives the block width
        ac.Scratchpad1 = "RESET";
        TextStyle style = CreateStyle();

        var first = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "");
        // Sample across at least one full 500 ms flash cycle — the reserved slot keeps width + count constant.
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(120);
            var sample = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "");
            Assert.Equal(first.Rect.Width, sample.Rect.Width, precision: 3);
            Assert.Equal(first.LineCount, sample.LineCount);
        }
    }

    // --- Beacon-code mismatch line (reported code solid + assigned code dim-pulsing, CRC STARS emulation) ---

    [Fact]
    public void SquawkMismatch_LinePresent_WhenAssignedDiffersAndModeC()
    {
        AircraftModel ac = CreateModel();
        ac.TransponderMode = "C";
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        // Reported code solid on the left, assigned code (which the renderer dim-pulses) on the right.
        Assert.Equal("1200 0301", layout.SquawkLine);
    }

    [Fact]
    public void SquawkMismatch_LineAbsent_WhenCodesMatch()
    {
        AircraftModel ac = CreateModel();
        ac.BeaconCode = 301;
        ac.AssignedBeaconCode = 301;
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("", layout.SquawkLine);
    }

    [Fact]
    public void SquawkMismatch_LineAbsent_WhenNoAssignedCode()
    {
        // VFR cold-call: squawking 1200 with nothing assigned yet — not a mismatch.
        AircraftModel ac = CreateModel();
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 0;
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("", layout.SquawkLine);
    }

    [Theory]
    [InlineData("Standby")]
    [InlineData("Off")]
    public void SquawkMismatch_LineAbsent_WhenTransponderNotTransmitting(string mode)
    {
        AircraftModel ac = CreateModel();
        ac.TransponderMode = mode;
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
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
        AircraftModel ac = CreateModel();
        ac.BeaconCode = reported;
        ac.AssignedBeaconCode = 301;
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("", layout.SquawkLine);
    }

    [Fact]
    public void SquawkMismatch_LineShows_WhenSquawkingVfr1200VsDiscrete()
    {
        // The motivating case: a VFR aircraft assigned a discrete code but still squawking 1200.
        AircraftModel ac = CreateModel();
        ac.FlightRules = "VFR";
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 4321;
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("1200 4321", layout.SquawkLine);
    }

    [Fact]
    public void SquawkMismatch_LineAbsent_WhenCommandedSquawkVfr()
    {
        // Once the pilot is told to squawk VFR (SQVFR/SQV), the latch suppresses the RPO mismatch flash
        // even though the assigned discrete code still differs from the reported 1200.
        AircraftModel ac = CreateModel();
        ac.TransponderMode = "C";
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        ac.CommandedSquawkVfr = true;
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("", layout.SquawkLine);
    }

    [Fact]
    public void SquawkMismatch_LineShows_WhenAssignedButNotSquawked_AndNotCommandedVfr()
    {
        // The intended RPO aid: an assigned-but-not-yet-squawked code flashes at any datablock level
        // (unlike CRC's FDB-only line). The latch is set only once the pilot is told to squawk VFR.
        AircraftModel ac = CreateModel();
        ac.TransponderMode = "C";
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        ac.CommandedSquawkVfr = false;
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal("1200 0301", layout.SquawkLine);
    }

    [Fact]
    public void SquawkMismatch_RectGrowsByExactlyLineHeight()
    {
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();

        ac.BeaconCode = 301;
        ac.AssignedBeaconCode = 301;
        var matched = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        ac.BeaconCode = 1200;
        var mismatched = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );

        Assert.Equal(matched.LineCount + 1, mismatched.LineCount);
        float delta = mismatched.Rect.Bottom - matched.Rect.Bottom;
        Assert.Equal(matched.LineHeight, delta, precision: 3);
    }

    [Fact]
    public void SquawkMismatch_RectIsTranslationInvariant()
    {
        AircraftModel ac = CreateModel();
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        TextStyle style = CreateStyle();

        SKRect atOrigin = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "").Rect;
        SKRect atOffset = RadarDatablockLayout.Compute(ac, 137, -52, style, showNoLandingClearance: false, overlays: None, callsignMarker: "").Rect;

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
        AircraftModel ac = CreateModel();
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        TextStyle style = CreateStyle();

        var first = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "");
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(120);
            var sample = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "");
            Assert.Equal(first.Rect.Width, sample.Rect.Width, precision: 3);
            Assert.Equal(first.LineCount, sample.LineCount);
            Assert.Equal("1200 0301", sample.SquawkLine);
        }
    }

    [Fact]
    public void Line2_ShowsCwtType_WhenNotIdenting()
    {
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "");

        Assert.Equal("230 25 D/B738", layout.Line2);
        Assert.False(layout.IdentActive);
    }

    [Fact]
    public void Line2_AppendsIdAfterCwtType_WhileIdenting()
    {
        // The ident is appended at the end of the altitude line and keeps the type readout.
        AircraftModel ac = CreateModel();
        ac.IsIdenting = true;
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "");

        Assert.Equal("230 25 D/B738 ID", layout.Line2);
        Assert.True(layout.IdentActive);
    }

    [Fact]
    public void Line2_ReservesIdWidthAcrossFlashCycle()
    {
        // The ident blinks fully off in the renderer, so the layout must keep the token in the measured
        // string on both phases — otherwise the rect (and the hit area and leader endpoint) would pulse.
        AircraftModel ac = CreateModel();
        ac.IsIdenting = true;
        TextStyle style = CreateStyle();

        var first = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "");
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(120);
            var sample = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "");
            Assert.Equal("230 25 D/B738 ID", sample.Line2);
            Assert.Equal(first.Rect.Width, sample.Rect.Width, precision: 3);
            Assert.Equal(first.LineCount, sample.LineCount);
        }
    }

    [Fact]
    public void Line2_WidensToFitId_WhileIdenting()
    {
        // The ident lengthens line 2 rather than displacing the type, so the block has to grow to fit it.
        AircraftModel ac = CreateModel();
        TextStyle style = CreateStyle();

        float idle = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "").Rect.Width;

        ac.IsIdenting = true;
        float identing = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "").Rect.Width;

        Assert.True(identing > idle, "the reserved ident token must widen the block");
    }

    [Fact]
    public void Line2_ShowsIdWithNoCwtOrType_WhileIdenting()
    {
        // An aircraft with neither CWT nor type normally has a bare "alt speed" line 2. The ident
        // still has to land on it — the type token is absent, not an empty slot to skip past.
        AircraftModel ac = CreateModel();
        ac.AircraftType = "";
        ac.FiledAircraftType = "";
        ac.CwtCode = "";
        ac.IsIdenting = true;
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: None, callsignMarker: "");

        Assert.Equal("230 25 ID", layout.Line2);
    }

    [Fact]
    public void CollapsedLimited_AppendsId_WhileIdenting()
    {
        // CRC's BuildLdb appends "ID" after the beacon-code/altitude text on the same line.
        AircraftModel ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Limited;
        ac.BeaconCode = 301;

        Assert.Equal(["0301 230"], RadarDatablockLayout.BuildCollapsedLines(ac));

        ac.IsIdenting = true;
        Assert.Equal(["0301 230 ID"], RadarDatablockLayout.BuildCollapsedLines(ac));
    }

    [Fact]
    public void CollapsedPartial_AppendsId_WhileIdenting()
    {
        // CRC's BuildPdb appends "ID" after the altitude/ground-speed line.
        AircraftModel ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Partial;

        Assert.Equal(["230 25"], RadarDatablockLayout.BuildCollapsedLines(ac));

        ac.IsIdenting = true;
        Assert.Equal(["230 25 ID"], RadarDatablockLayout.BuildCollapsedLines(ac));
    }

    [Fact]
    public void CollapsedPartial_KeepsScratchpadLine_WhileIdenting()
    {
        // The ident rides on line 0 only; a scratchpad line 1 must survive untouched.
        AircraftModel ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Partial;
        ac.Scratchpad1 = "OAK";
        ac.IsIdenting = true;

        Assert.Equal(["230 25 ID", "OAK"], RadarDatablockLayout.BuildCollapsedLines(ac));
    }

    [Fact]
    public void MinifiedLine_AppendsId_WhileIdenting()
    {
        AircraftModel ac = CreateModel();

        Assert.Equal("230 D", RadarDatablockLayout.BuildMinifiedLine(ac));

        ac.IsIdenting = true;
        Assert.Equal("230 D ID", RadarDatablockLayout.BuildMinifiedLine(ac));
    }

    [Fact]
    public void MinifiedLine_AppendsId_WhenNoCwt()
    {
        AircraftModel ac = CreateModel();
        ac.CwtCode = "";
        ac.IsIdenting = true;

        Assert.Equal("230 ID", RadarDatablockLayout.BuildMinifiedLine(ac));
    }

    /// <summary>
    /// ATPA lead 3.2 nm due east of <see cref="CreateModel"/>'s position — the same easting the conflict
    /// peer uses (2.5044' ≈ 2.0 nm at 37N), scaled by 1.6.
    /// </summary>
    private static AircraftModel CreateAtpaLead()
    {
        return new AircraftModel
        {
            Callsign = "SWA1234",
            AircraftType = "B737",
            FiledAircraftType = "B737",
            FlightRules = "IFR",
            Position = new LatLon(37.0, -122.0 + (4.00704 / 60.0)),
            Altitude = 23000,
            GroundSpeed = 250,
        };
    }

    /// <summary>The trailing member of an ATPA pairing: lead callsign, required separation, cone state.</summary>
    private static AircraftModel CreateAtpaTrailer()
    {
        AircraftModel ac = CreateModel();
        ac.AtpaLeadCallsign = "SWA1234";
        ac.AtpaAllowedSeparationNm = 3.0;
        ac.AtpaConeState = AtpaConeState.Warning;
        return ac;
    }

    [Fact]
    public void Atpa_LineShowsInTrailTenths()
    {
        AircraftModel ac = CreateAtpaTrailer();
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: new DatablockOverlays(false, null, true, CreateAtpaLead()),
            callsignMarker: ""
        );

        // Live in-trail distance, not the required 3.0 nm separation the broadcast carries.
        Assert.Equal("3.2", layout.AtpaLine);
    }

    [Fact]
    public void Atpa_LineAbsent_WhenToggleOff()
    {
        AircraftModel ac = CreateAtpaTrailer();
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: new DatablockOverlays(false, null, false, CreateAtpaLead()),
            callsignMarker: ""
        );

        Assert.Equal("", layout.AtpaLine);
    }

    [Fact]
    public void Atpa_LineAbsent_WhenLeadUnresolved()
    {
        // The lead left the scope (or its first position update hasn't landed): the distance is the
        // field's entire content, so there is nothing to draw.
        AircraftModel ac = CreateAtpaTrailer();
        TextStyle style = CreateStyle();

        var layout = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: new DatablockOverlays(false, null, true, null),
            callsignMarker: ""
        );

        Assert.Equal("", layout.AtpaLine);
    }

    [Fact]
    public void Atpa_RectGrowsByExactlyLineHeight()
    {
        AircraftModel ac = CreateAtpaTrailer();
        TextStyle style = CreateStyle();

        var without = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: None,
            callsignMarker: ""
        );
        var with = RadarDatablockLayout.Compute(
            ac,
            blockX: 100,
            blockY: 100,
            style,
            showNoLandingClearance: false,
            overlays: new DatablockOverlays(false, null, true, CreateAtpaLead()),
            callsignMarker: ""
        );

        Assert.Equal(without.LineCount + 1, with.LineCount);
        float delta = with.Rect.Bottom - without.Rect.Bottom;
        Assert.Equal(without.LineHeight, delta, precision: 3);
    }

    [Fact]
    public void Atpa_RectStableAcrossFlashCycle()
    {
        // The in-trail line is steady, so — unlike the conflict field — both its width and its line slot
        // must hold across a full 500 ms cycle without any reservation flag.
        AircraftModel ac = CreateAtpaTrailer();
        AircraftModel lead = CreateAtpaLead();
        TextStyle style = CreateStyle();

        var first = RadarDatablockLayout.Compute(
            ac,
            0,
            0,
            style,
            showNoLandingClearance: false,
            overlays: new DatablockOverlays(false, null, true, lead),
            callsignMarker: ""
        );
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(120);
            var sample = RadarDatablockLayout.Compute(
                ac,
                0,
                0,
                style,
                showNoLandingClearance: false,
                overlays: new DatablockOverlays(false, null, true, lead),
                callsignMarker: ""
            );
            Assert.Equal(first.Rect.Width, sample.Rect.Width, precision: 3);
            Assert.Equal(first.LineCount, sample.LineCount);
            Assert.Equal("3.2", sample.AtpaLine);
        }
    }

    [Fact]
    public void Atpa_RectIsTranslationInvariant()
    {
        AircraftModel ac = CreateAtpaTrailer();
        AircraftModel lead = CreateAtpaLead();
        TextStyle style = CreateStyle();

        SKRect atOrigin = RadarDatablockLayout
            .Compute(ac, 0, 0, style, showNoLandingClearance: false, overlays: new DatablockOverlays(false, null, true, lead), callsignMarker: "")
            .Rect;
        SKRect atOffset = RadarDatablockLayout
            .Compute(ac, 137, -52, style, showNoLandingClearance: false, overlays: new DatablockOverlays(false, null, true, lead), callsignMarker: "")
            .Rect;

        Assert.Equal(atOrigin.Left + 137, atOffset.Left, precision: 3);
        Assert.Equal(atOrigin.Top - 52, atOffset.Top, precision: 3);
        Assert.Equal(atOrigin.Right + 137, atOffset.Right, precision: 3);
        Assert.Equal(atOrigin.Bottom - 52, atOffset.Bottom, precision: 3);
    }
}
