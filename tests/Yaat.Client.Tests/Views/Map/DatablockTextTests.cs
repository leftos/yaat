using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Map;

/// <summary>
/// Verifies the shared STARS datablock text builders: the conflict field, the collapsed LDB/PDB lines, the
/// student-level marker and the owner/scratchpad line. Pure-function tests on <see cref="DatablockText"/>.
/// </summary>
public class DatablockTextTests
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
    public void ConflictAlert_ListsHorizontalNmAndVerticalFeet()
    {
        AircraftModel ac = CreateModel();
        ac.Owner = "AB";
        AircraftModel peer = CreateConflictPeer();
        peer.Owner = "CD";

        // Horizontal to one decimal in nm; vertical to the nearest 100 ft, matching Mode C reporting
        // granularity. 23000 - 22200 = 800. Both tracked, so this is CA rather than MCI.
        Assert.Equal("CA 2.0/800", DatablockText.BuildConflictLine(ac, peer));
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

        Assert.Equal("CA 2.0/100", DatablockText.BuildConflictLine(ac, peer));
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
        Assert.Equal("CA 2.0/000", DatablockText.BuildConflictLine(ac, peer));
    }

    [Fact]
    public void ModeCIntruder_WhenPeerIsUntrackedAndUncorrelated()
    {
        // P/CG: a conflict between a tracked target and an untracked one is a Mode C Intruder alert,
        // not a conflict alert. 7110.65 §5-14-6 treats CA and MCI as distinct alert types.
        AircraftModel ac = CreateModel();
        ac.Owner = "AB";
        AircraftModel peer = CreateConflictPeer();

        Assert.StartsWith("MCI ", DatablockText.BuildConflictLine(ac, peer), StringComparison.Ordinal);
    }

    [Fact]
    public void ModeCIntruder_ShowsOnBothMembers_SinceItClassifiesThePair()
    {
        // The tracked side of the pair must read MCI too — the alert type is a property of the pair,
        // so the two datablocks can't disagree about what kind of alert is active.
        AircraftModel ac = CreateModel();
        ac.Owner = "AB";
        AircraftModel peer = CreateConflictPeer();

        Assert.StartsWith("MCI ", DatablockText.BuildConflictLine(ac, peer), StringComparison.Ordinal);
        Assert.StartsWith("MCI ", DatablockText.BuildConflictLine(peer, ac), StringComparison.Ordinal);
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

        Assert.StartsWith("CA ", DatablockText.BuildConflictLine(ac, peer), StringComparison.Ordinal);
        Assert.StartsWith("CA ", DatablockText.BuildConflictLine(peer, ac), StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictAlert_FallsBackToBareCa_WhenPeerUnresolved()
    {
        AircraftModel ac = CreateModel();

        // The peer can be absent when it has left the scope or its first position update hasn't
        // landed yet — the alert must still show rather than vanishing.
        Assert.Equal("CA", DatablockText.BuildConflictLine(ac, peer: null));
    }

    // --- Student-scope collapsed datablock content (mirrors CRC BuildLdb/BuildPdb) ---

    [Fact]
    public void CollapsedLdb_ShowsBeaconThenAltitude()
    {
        AircraftModel ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Limited;
        ac.BeaconCode = 1200;
        ac.Altitude = 3500;

        IReadOnlyList<string> lines = DatablockText.BuildCollapsedLines(ac);

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

        IReadOnlyList<string> lines = DatablockText.BuildCollapsedLines(ac);

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

        IReadOnlyList<string> lines = DatablockText.BuildCollapsedLines(ac);

        Assert.Equal(["035 12", "CCR"], lines);
    }

    [Fact]
    public void StudentLevelMarker_MapsEachLevel()
    {
        Assert.Equal(" (LDB)", DatablockText.StudentLevelMarker(StarsDatablockLevel.Limited));
        Assert.Equal(" (PDB)", DatablockText.StudentLevelMarker(StarsDatablockLevel.Partial));
        Assert.Equal("", DatablockText.StudentLevelMarker(StarsDatablockLevel.Full));
        Assert.Equal("", DatablockText.StudentLevelMarker(null));
    }

    [Fact]
    public void OwnerWithStudentSymbol_DuringAHandoff_RendersOwnerSymbolThenHandoff()
    {
        AircraftModel ac = CreateModel();
        ac.OwnerSectorCode = "3Y";
        ac.StudentOwnerSymbol = "1";
        ac.HandoffPeerSectorCode = "1F";

        // The flash's on phase, which shows the handoff target after the owner.
        string? line = DatablockText.BuildOwnerScratchpadLine(ac, showHandoff: true);

        Assert.NotNull(line);
        Assert.Contains("3Y(1) >1F", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoScratchpad1_AppearsOnCollapsedPdb()
    {
        AircraftModel ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Partial;
        ac.Altitude = 3500;
        ac.GroundSpeed = 120;
        ac.AutoScratchpad1 = "SFO";

        IReadOnlyList<string> lines = DatablockText.BuildCollapsedLines(ac);

        Assert.Equal(["035 12", "SFO"], lines);
    }

    [Fact]
    public void EffectiveScratchpad1_PrefersRealThenAuto()
    {
        AircraftModel ac = CreateModel();
        Assert.Null(DatablockText.EffectiveScratchpad1(ac));

        ac.AutoScratchpad1 = "OAK";
        Assert.Equal("OAK", DatablockText.EffectiveScratchpad1(ac));

        ac.Scratchpad1 = "ABC";
        Assert.Equal("ABC", DatablockText.EffectiveScratchpad1(ac));
    }

    [Fact]
    public void CollapsedLimited_AppendsId_WhileIdenting()
    {
        // CRC's BuildLdb appends "ID" after the beacon-code/altitude text on the same line.
        AircraftModel ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Limited;
        ac.BeaconCode = 301;

        Assert.Equal(["0301 230"], DatablockText.BuildCollapsedLines(ac));

        ac.IsIdenting = true;
        Assert.Equal(["0301 230 ID"], DatablockText.BuildCollapsedLines(ac));
    }

    [Fact]
    public void CollapsedPartial_AppendsId_WhileIdenting()
    {
        // CRC's BuildPdb appends "ID" after the altitude/ground-speed line.
        AircraftModel ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Partial;

        Assert.Equal(["230 25"], DatablockText.BuildCollapsedLines(ac));

        ac.IsIdenting = true;
        Assert.Equal(["230 25 ID"], DatablockText.BuildCollapsedLines(ac));
    }

    [Fact]
    public void CollapsedPartial_KeepsScratchpadLine_WhileIdenting()
    {
        // The ident rides on line 0 only; a scratchpad line 1 must survive untouched.
        AircraftModel ac = CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Partial;
        ac.Scratchpad1 = "OAK";
        ac.IsIdenting = true;

        Assert.Equal(["230 25 ID", "OAK"], DatablockText.BuildCollapsedLines(ac));
    }

    /// <summary>
    /// The full block <see cref="CreateModel"/> draws, at text origin (0, 0) in a 12-point style: the rect today's Radar
    /// layout computed for it on the Windows builder, frozen so the offset tests need no layout. Only its centre and
    /// extent feed the leader-direction placement, so another machine's font width changes nothing these tests assert.
    /// </summary>
    private static readonly SKRect FullBlockRectAtOrigin = new(-3f, -15f, 81.29297f, 17f);

    // --- Offset precedence (datablock deconfliction): manual > deconflict > leader-dir > default ---

    [Fact]
    public void ResolveBlockOffset_ManualBeatsDeconflict()
    {
        AircraftModel ac = CreateModel();
        SKRect rectAtOrigin = FullBlockRectAtOrigin;
        var manual = new SKPoint(5, 5);

        SKPoint result = DatablockText.ResolveBlockOffset(
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
        SKRect rectAtOrigin = FullBlockRectAtOrigin;
        var deconflict = new SKPoint(99, 99);

        SKPoint result = DatablockText.ResolveBlockOffset(ac, syncLeader: true, hasManual: false, default, rectAtOrigin, deconflict);

        Assert.Equal(deconflict, result);
    }

    [Fact]
    public void ResolveBlockOffset_DeconflictBeatsDefault()
    {
        AircraftModel ac = CreateModel();
        SKRect rectAtOrigin = FullBlockRectAtOrigin;
        var deconflict = new SKPoint(99, 99);

        SKPoint result = DatablockText.ResolveBlockOffset(ac, syncLeader: false, hasManual: false, default, rectAtOrigin, deconflict);

        Assert.Equal(deconflict, result);
    }

    [Fact]
    public void ResolveBlockOffset_NullDeconflict_ReproducesLeaderDirection()
    {
        AircraftModel ac = CreateModel();
        ac.StudentLeaderDirection = 8; // North
        SKRect rectAtOrigin = FullBlockRectAtOrigin;

        SKPoint leaderResult = DatablockText.ResolveBlockOffset(
            ac,
            syncLeader: true,
            hasManual: false,
            default,
            rectAtOrigin,
            deconflictOffset: null
        );

        Assert.NotEqual(DatablockText.DefaultOffset, leaderResult);
    }

    [Fact]
    public void ResolveBlockOffset_NullDeconflict_NoLeader_ReturnsDefault()
    {
        AircraftModel ac = CreateModel();
        SKRect rectAtOrigin = FullBlockRectAtOrigin;

        SKPoint result = DatablockText.ResolveBlockOffset(ac, syncLeader: false, hasManual: false, default, rectAtOrigin, deconflictOffset: null);

        Assert.Equal(DatablockText.DefaultOffset, result);
    }
}
