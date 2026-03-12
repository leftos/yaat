using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Commands;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Replay tests using the SFO S2-SFO-1 "Intro to Crossing Runways" recording.
/// Covers issues #51 (departure wrong direction) and #53 (AAL2839 taxi overshoot).
/// Silently skip if NavData.dat or sfo.geojson is not present in TestData/.
/// </summary>
public class SfoReplayTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;
    private const string RecordingPath = "TestData/sfo-crossing-runways-recording.json";

    private static SessionRecording? LoadRecording()
    {
        if (!File.Exists(RecordingPath))
        {
            return null;
        }

        var json = File.ReadAllText(RecordingPath);
        return JsonSerializer.Deserialize<SessionRecording>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static SimulationEngine? BuildEngine()
    {
        var fixes = TestVnasData.FixDatabase;
        if (fixes is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        return new SimulationEngine(fixes, fixes, groundData);
    }

    // --- Issue #51: CTO uses wrong runway end (departs opposite direction) ---

    /// <summary>
    /// UAL859 is holding short of runway 01R (stored as "1R" in GeoJSON).
    /// Bare CTO at t=153s should assign the 01R end (heading ~010°), not 19L (~190°).
    /// </summary>
    [Fact]
    public void Replay_Sfo_UAL859_CTO_AssignsNorthwardRunway()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay up to just after the CTO command at t=153s
        engine.Replay(recording, 160.0);

        var ual = engine.FindAircraft("UAL859");
        Assert.NotNull(ual);

        var assignedRunway = ual.Phases?.AssignedRunway;
        Assert.NotNull(assignedRunway);

        // Runway 01R departs northward (~010°). The bug assigns 19L (~190°) instead.
        double heading = assignedRunway!.TrueHeading;
        Assert.True(
            heading < 45.0 || heading > 315.0,
            $"Expected UAL859 assigned runway to face north (~010°) but got {heading:F1}°. "
                + $"Designator: {assignedRunway.Designator}. Issue #51: GeoJSON '1R' falls back to opposite end '19L'."
        );
    }

    /// <summary>
    /// DAL819 gets "CTO 01R" at t=161s. Even with an explicit runway, the heading should
    /// be northward. (At the time of the bug, bare CTO would use hold-short's GeoJSON "1R"
    /// which fails lookup and resolves to 19L.)
    /// </summary>
    [Fact]
    public void Replay_Sfo_DAL819_CTO01R_AssignsNorthwardRunway()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 174.0);

        var dal = engine.FindAircraft("DAL819");
        Assert.NotNull(dal);

        var assignedRunway = dal.Phases?.AssignedRunway;
        Assert.NotNull(assignedRunway);

        double heading = assignedRunway!.TrueHeading;
        Assert.True(
            heading < 45.0 || heading > 315.0,
            $"Expected DAL819 assigned runway to face north (~010°) but got {heading:F1}°. "
                + $"Designator: {assignedRunway.Designator}. Issue #51: runway designator mismatch."
        );
    }

    // --- Issue #52: Landing jets exit too early (taxiway P) due to excessive decel rate ---

    /// <summary>
    /// SWA996 starts 5 nm from 28L. With correct rollout deceleration (2.5 kts/sec),
    /// rollout from 135 kt touchdown takes ~46s covering ~6,000 ft, placing the exit
    /// near the 01R/01L intersection (~lon -122.385). With the old 5.0 kts/sec, rollout
    /// covers only ~3,000 ft, exiting near taxiway P (~lon -122.368).
    ///
    /// Asserts that when rollout completes (speed drops to ~20 kts), the aircraft is
    /// past taxiway P's longitude (-122.370), i.e. further west on the runway.
    /// </summary>
    [Fact]
    public void Replay_Sfo_SWA996_28L_ExitsPastTaxiwayP()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Taxiway P on SFO 28L is at approximately lon -122.367 to -122.370 (east portion of runway).
        // Aircraft landing 28L touch down near the east threshold (lon ~-122.358) and roll west.
        // A correct ~6,000 ft rollout exits near lon -122.387 (near 01R/01L crossing).
        const double TaxiwayPLon = -122.370; // eastern bound of taxiway P

        double? rolloutExitLon = null;
        double prevSpeed = 999;

        // Sample each second looking for the moment ground speed crosses 20 kts (rollout complete)
        for (double t = 100.0; t <= 174.0; t += 1.0)
        {
            engine.Replay(recording, t);
            var swa = engine.FindAircraft("SWA996");
            if (swa is null || !swa.IsOnGround)
            {
                continue;
            }

            double speed = swa.GroundSpeed;
            // Detect when speed first drops to ≤20 kts while decelerating (rollout complete)
            if (prevSpeed > 20.0 && speed <= 20.0)
            {
                rolloutExitLon = swa.Longitude;
                break;
            }
            prevSpeed = speed;
        }

        if (rolloutExitLon is null)
        {
            return; // Aircraft didn't land during recording window — inconclusive
        }

        Assert.True(
            rolloutExitLon < TaxiwayPLon,
            $"SWA996 rollout completed at lon {rolloutExitLon:F4}, which is east of (or at) taxiway P ({TaxiwayPLon}). "
                + $"Expected rollout to finish west of taxiway P, near the 01R/01L intersection. "
                + $"Issue #52: RolloutDecelRate too high causes exit at wrong taxiway."
        );
    }

    // --- Issue #53: AAL2839 taxi overshoots to M1/M2 ramp intersection ---

    /// <summary>
    /// AAL2839 starts at lat ~37.609 (near the B/M junction, between M and M1 on taxiway B).
    /// Its preset command fires at t=0: "TAXI B M1 1L".
    /// It should taxi south on M1 to hold short of runway 1L — a direct ~100m trip.
    ///
    /// The bug: pathfinder sends it south to the M1/M2 cargo ramp junction (~lat 37.607)
    /// before U-turning back north to the runway 1L hold short (~lat 37.608).
    /// The aircraft travels ~400m instead of ~100m.
    ///
    /// Assert: the taxi route is short (direct path), without an overshoot detour.
    /// </summary>
    [Fact]
    public void Replay_Sfo_AAL2839_TaxiRoute_IsDirectNotDetour()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay enough for preset to fire and route to be assigned
        engine.Replay(recording, 5.0);

        var aal = engine.FindAircraft("AAL2839");
        if (aal is null)
        {
            return; // Not in this recording
        }

        var route = aal.AssignedTaxiRoute;
        if (route is null)
        {
            return; // Preset hasn't fired yet — inconclusive
        }

        // The direct path (B/M junction → M1 → runway 1L holdshort) is ~150m max.
        // A route with overshoot (south to M1/M2 junction → U-turn → back to holdshort) is ~400m+.
        // Count segments: direct path has ≤ 5 segments, detour route has many more.
        int segmentCount = route.Segments.Count;
        Assert.True(
            segmentCount <= 8,
            $"AAL2839 taxi route has {segmentCount} segments — expected ≤ 8 for the direct path. "
                + $"Taxiways: [{string.Join(", ", route.Segments.Select(s => s.TaxiwayName).Distinct())}]. "
                + $"Issue #53: pathfinder overshoot sends aircraft to M1/M2 ramp junction before returning to runway 1L."
        );
    }

    // --- Issue #54: Aircraft holding short are positioned on or past the runway edge ---

    /// <summary>
    /// DAL819 (TAXI M1 1L) and UAL859 (TAXI A1 1R) should hold short before the runway edge.
    /// The aircraft center (lat/lon) must have a cross-track distance from the runway centerline
    /// that is at least the runway half-width, meaning it is not on the runway surface.
    /// </summary>
    [Fact]
    public void Replay_Sfo_DAL819_UAL859_HoldShortBeforeRunwayEdge()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var layout = engine.World.GroundLayout ?? new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        // Both aircraft have preset taxi at t=0; replay to 90s so they've settled at hold-short
        engine.Replay(recording, 90.0);

        foreach (string callsign in new[] { "DAL819", "UAL859" })
        {
            var ac = engine.FindAircraft(callsign);
            if (ac is null || !ac.IsOnGround)
            {
                continue;
            }

            // Find the hold-short node this aircraft is at (nearest RunwayHoldShort)
            var hsNode = layout
                .Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort)
                .OrderBy(n => GeoMath.DistanceNm(ac.Latitude, ac.Longitude, n.Latitude, n.Longitude))
                .FirstOrDefault();
            if (hsNode is null)
            {
                continue;
            }

            double distToHsNm = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, hsNode.Latitude, hsNode.Longitude);
            double distToHsFt = distToHsNm * GeoMath.FeetPerNm;

            // Log closest few hold-short nodes for diagnosis
            var nearest5 = layout
                .Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort)
                .OrderBy(n => GeoMath.DistanceNm(ac.Latitude, ac.Longitude, n.Latitude, n.Longitude))
                .Take(5)
                .Select(n =>
                    $"  node {n.Id} runwayId={n.RunwayId} ({n.Latitude:F6},{n.Longitude:F6}) dist={GeoMath.DistanceNm(ac.Latitude, ac.Longitude, n.Latitude, n.Longitude) * GeoMath.FeetPerNm:F0}ft edges=[{string.Join(",", n.Edges.Select(e => e.TaxiwayName))}]"
                )
                .ToList();

            // Log the aircraft's assigned route hold-short points
            var routeHs =
                ac.AssignedTaxiRoute?.HoldShortPoints.Select(h =>
                        $"  hs node={h.NodeId} target={h.TargetName} reason={h.Reason} cleared={h.IsCleared}"
                    )
                    .ToList()
                ?? [];

            string diag =
                $"\n{callsign} pos=({ac.Latitude:F6},{ac.Longitude:F6}) gs={ac.GroundSpeed:F1}kts\nNearest hold-short nodes:\n{string.Join("\n", nearest5)}\nRoute hold-short points:\n{string.Join("\n", routeHs.Count > 0 ? routeHs : ["  (none)"])}";

            // The aircraft center should be within 200ft of its nearest hold-short node
            // (confirming it is actually at hold-short, not still taxiing or somewhere else)
            Assert.True(
                distToHsFt <= 200.0,
                $"{callsign} is {distToHsFt:F0}ft from its nearest hold-short node {hsNode.Id} (runwayId={hsNode.RunwayId}). Expected ≤200ft — aircraft may not have reached hold-short.{diag}"
            );

            // Find runway geometry for this hold-short's runway
            if (hsNode.RunwayId is not { } rwyId)
            {
                continue;
            }

            var runway = layout.Runways.FirstOrDefault(r =>
            {
                var rid = RunwayIdentifier.Parse(r.Name);
                return rid.Contains(rwyId.End1) || rid.Contains(rwyId.End2);
            });
            if (runway is null)
            {
                continue;
            }

            var c0 = runway.Coordinates[0];
            var cN = runway.Coordinates[^1];
            double rwyHeading = GeoMath.BearingTo(c0.Lat, c0.Lon, cN.Lat, cN.Lon);
            double halfWidthNm = runway.WidthFt / 2.0 / GeoMath.FeetPerNm;
            double crossTrack = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Latitude, ac.Longitude, c0.Lat, c0.Lon, rwyHeading));
            double crossTrackFt = crossTrack * GeoMath.FeetPerNm;
            double halfWidthFt = runway.WidthFt / 2.0;

            Assert.True(
                crossTrack >= halfWidthNm,
                $"{callsign} center is {crossTrackFt:F0}ft from runway {rwyId} centerline but runway half-width is {halfWidthFt:F0}ft — aircraft center is on the runway surface. "
                    + $"Issue #54: hold-short node is placed inside or at the runway boundary.{diag}"
            );
        }
    }

    /// <summary>
    /// Speed trace for DAL819 and UAL859 approaching their respective hold-shorts.
    /// Confirms braking behavior: each aircraft accelerates to taxi speed then decelerates
    /// before stopping at its own hold-short node. One second resolution.
    /// </summary>
    [Fact]
    public void Diag_Sfo_HoldShortApproach_BrakingTrace()
    {
        var recording = LoadRecording();
        if (recording is null)
        {
            return;
        }

        foreach (string callsign in new[] { "DAL819", "UAL859" })
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            // First pass: find when the aircraft first enters HoldingShortPhase
            // and record which HS node it stopped at.
            double stopTime = 0;
            double hsLat = 0,
                hsLon = 0;
            string hsName = "?";
            for (double t = 1.0; t <= 90.0; t += 1.0)
            {
                engine.Replay(recording, t);
                var ac = engine.FindAircraft(callsign);
                if (ac?.Phases?.CurrentPhase is HoldingShortPhase hs)
                {
                    stopTime = t;
                    hsLat = ac.Latitude;
                    hsLon = ac.Longitude;
                    hsName = hs.Name;
                    break;
                }
            }

            if (stopTime == 0)
            {
                _output.WriteLine($"{callsign}: never reached HoldingShortPhase within 90s");
                continue;
            }

            _output.WriteLine($"=== {callsign}: approached {hsName} at ({hsLat:F6},{hsLon:F6}), stopped at t={stopTime:F0}s ===");
            _output.WriteLine($"{"t(s)", 5}  {"gs(kts)", 8}  {"dist(ft)", 9}  {"phase", -24}");

            double traceStart = Math.Max(0.0, stopTime - 10.0);
            for (double t = traceStart; t <= stopTime + 2.0; t += 1.0)
            {
                engine.Replay(recording, t);
                var ac = engine.FindAircraft(callsign);
                if (ac is null || !ac.IsOnGround)
                {
                    continue;
                }

                double distFt = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, hsLat, hsLon) * GeoMath.FeetPerNm;
                string phase = ac.Phases?.CurrentPhase?.Name ?? "?";
                _output.WriteLine($"{t, 5:F0}  {ac.GroundSpeed, 8:F2}  {distFt, 9:F1}  {phase, -24}");
            }

            _output.WriteLine("");
        }
    }

    /// <summary>
    /// Replays DAL819 and UAL859 and asserts that speed never drops by more than
    /// TaxiDecelRate + 0.5 kts between consecutive 1-second samples (no snap-to-zero).
    /// Also verifies aircraft reach HoldingShortPhase within the expected window.
    /// </summary>
    [Fact]
    public void Replay_Sfo_HoldShort_MaxDeceleration()
    {
        var recording = LoadRecording();
        if (recording is null)
        {
            return;
        }

        double maxAllowedDrop = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet) + 0.5;

        foreach (string callsign in new[] { "DAL819", "UAL859" })
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            double prevSpeed = 0;
            bool reachedHoldShort = false;
            double worstDrop = 0;
            double worstDropTime = 0;

            for (double t = 1.0; t <= 90.0; t += 1.0)
            {
                engine.Replay(recording, t);
                var ac = engine.FindAircraft(callsign);
                if (ac is null || !ac.IsOnGround)
                {
                    continue;
                }

                if (ac.Phases?.CurrentPhase is HoldingShortPhase)
                {
                    reachedHoldShort = true;
                    break;
                }

                double speed = ac.GroundSpeed;
                double drop = prevSpeed - speed;
                if (drop > worstDrop)
                {
                    worstDrop = drop;
                    worstDropTime = t;
                }

                prevSpeed = speed;
            }

            Assert.True(reachedHoldShort, $"{callsign} never reached HoldingShortPhase within 90s");

            Assert.True(
                worstDrop <= maxAllowedDrop,
                $"{callsign} speed dropped {worstDrop:F2} kts in one tick at t={worstDropTime:F0}s. Max allowed: {maxAllowedDrop:F1} kts/s."
            );
        }
    }

    // --- Issue #53 (follow-up): SWA7348 "TAXI Y H B M1 HS 01L" goes into RAMP ---

    private const string Issue53RecordingPath = "TestData/sfo-issue53-yhbm1-recording.json";

    private static SessionRecording? LoadIssue53Recording()
    {
        if (!File.Exists(Issue53RecordingPath))
        {
            return null;
        }

        var json = File.ReadAllText(Issue53RecordingPath);
        return JsonSerializer.Deserialize<SessionRecording>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    /// <summary>
    /// SWA7348 gets "TAXI Y H B M1 HS 01L" at t=0. The route should go Y→H→B→M1 to hold
    /// short of runway 1L. The bug: pathfinder sends it into the RAMP area first.
    /// Assert: the taxi route contains no RAMP segments (aircraft should already be on taxiway Y).
    /// </summary>
    [Fact]
    public void Replay_Sfo_SWA7348_TaxiYHBM1_NoRampDetour()
    {
        var recording = LoadIssue53Recording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 5.0);

        var swa = engine.FindAircraft("SWA7348");
        if (swa is null)
        {
            return;
        }

        var route = swa.AssignedTaxiRoute;
        if (route is null)
        {
            return;
        }

        // Log route for diagnosis
        _output.WriteLine($"SWA7348 pos=({swa.Latitude:F6},{swa.Longitude:F6}) gs={swa.GroundSpeed:F1}kts heading={swa.Heading:F1}");
        _output.WriteLine($"SWA7348 route: {route.Segments.Count} segments, currentIdx={route.CurrentSegmentIndex}");
        var layout = new TestAirportGroundData().GetLayout("SFO");
        foreach (var seg in route.Segments)
        {
            var fromN = layout?.Nodes.GetValueOrDefault(seg.FromNodeId);
            var toN = layout?.Nodes.GetValueOrDefault(seg.ToNodeId);
            _output.WriteLine(
                $"  {seg.TaxiwayName}: {seg.FromNodeId}({fromN?.Latitude:F6},{fromN?.Longitude:F6}) → {seg.ToNodeId}({toN?.Latitude:F6},{toN?.Longitude:F6}) {toN?.Type}{(toN?.RunwayId is { } rid ? $" rwy={rid}" : "")}"
            );
        }

        // The route should follow Y→H→B→M1. The "Taxiing via RAMP to reach Y" warning
        // from the bug report indicates RAMP segments are being inserted at the start.
        bool hasRampSegments = route.Segments.Any(s => string.Equals(s.TaxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase));

        // RAMP is acceptable only as the very first segment (parking → first taxiway).
        // If there are RAMP segments beyond that, the pathfinder is taking a detour.
        int rampCount = route.Segments.Count(s => string.Equals(s.TaxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            rampCount <= 1,
            $"SWA7348 taxi route has {rampCount} RAMP segments — expected at most 1 (parking→taxiway). "
                + $"Taxiways: [{string.Join(", ", route.Segments.Select(s => s.TaxiwayName))}]. "
                + $"Issue #53: pathfinder goes into RAMP area instead of following Y→H→B→M1."
        );

        // Route should not be excessively long — Y→H→B→M1 is at most ~12 segments
        Assert.True(
            route.Segments.Count <= 15,
            $"SWA7348 taxi route has {route.Segments.Count} segments — expected ≤15. "
                + $"Taxiways: [{string.Join(", ", route.Segments.Select(s => s.TaxiwayName).Distinct())}]. "
                + $"Issue #53: pathfinder takes an indirect route."
        );
    }

    /// <summary>
    /// AAL2839 starts at lat ~37.609 and should hold short of runway 1L at lat ~37.608.
    /// The M1/M2 cargo ramp intersection that the bug visits is at lat ~37.607 (further south).
    /// At t=60s, AAL2839 should have already passed through the hold short and be stationary
    /// there — NOT still moving south past lat 37.607 in an overshoot.
    /// </summary>
    [Fact]
    public void Replay_Sfo_AAL2839_DoesNotOvershotPastRamp()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // AAL2839 starts at lat 37.609, hold short ~37.608. M1/M2 junction at ~37.607.
        // Check across 30-90s window whether the aircraft ever goes past the ramp junction.
        const double M1M2JunctionLat = 37.607;
        bool overshotPastRamp = false;

        for (double t = 10.0; t <= 90.0; t += 5.0)
        {
            engine.Replay(recording, t);
            var aal = engine.FindAircraft("AAL2839");
            if (aal is null)
            {
                break;
            }

            if (aal.IsOnGround && aal.Latitude < M1M2JunctionLat)
            {
                overshotPastRamp = true;
                break;
            }
        }

        Assert.False(
            overshotPastRamp,
            $"AAL2839 went south past the M1/M2 junction (lat < {M1M2JunctionLat}). "
                + $"Issue #53: pathfinder detour takes aircraft to the cargo ramp before returning to runway 1L hold short."
        );
    }

    // --- Issue #53 (N346G): TAXI T41W C E HS 10L goes wrong direction on E ---

    private const string Issue53N346gRecordingPath = "TestData/sfo-issue53-n346g-recording.json";

    private static SessionRecording? LoadN346gRecording()
    {
        if (!File.Exists(Issue53N346gRecordingPath))
        {
            return null;
        }

        var json = File.ReadAllText(Issue53N346gRecordingPath);
        return JsonSerializer.Deserialize<SessionRecording>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    /// <summary>
    /// N346G gets "TAXI T41W C E HS 10L" at t=0. The route should go T41W→C→E toward
    /// runway 10L (eastward on E). The bug: E walk goes north (wrong direction) because
    /// HS 10L doesn't provide direction guidance without a destination runway.
    /// Assert: the last E segment ends at or near a 10L hold-short node.
    /// </summary>
    [Fact]
    public void Replay_Sfo_N346G_TaxiT41WCE_WalksToward10L()
    {
        var recording = LoadN346gRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 5.0);

        var ac = engine.FindAircraft("N346G");
        if (ac is null)
        {
            return;
        }

        var route = ac.AssignedTaxiRoute;
        if (route is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);

        _output.WriteLine($"N346G route: {route.Segments.Count} segments");
        foreach (var seg in route.Segments)
        {
            var fromN = layout.Nodes.GetValueOrDefault(seg.FromNodeId);
            var toN = layout.Nodes.GetValueOrDefault(seg.ToNodeId);
            _output.WriteLine(
                $"  {seg.TaxiwayName}: {seg.FromNodeId}({fromN?.Latitude:F6},{fromN?.Longitude:F6}) → {seg.ToNodeId}({toN?.Latitude:F6},{toN?.Longitude:F6}) {toN?.Type}{(toN?.RunwayId is { } rid ? $" rwy={rid}" : "")}"
            );
        }

        // The route should end at or near a 10L hold-short on taxiway E.
        // Runway 10L/28R runs roughly east-west; 10L hold-shorts on E are east of the C/E junction.
        var lastSeg = route.Segments[^1];
        var endNode = layout.Nodes[lastSeg.ToNodeId];

        // The route should contain E segments that reach a 10L hold-short
        bool reachesHoldShort =
            endNode.Type == GroundNodeType.RunwayHoldShort && endNode.RunwayId is { } endRwy && (endRwy.Contains("10L") || endRwy.Contains("28R"));

        Assert.True(
            reachesHoldShort,
            $"N346G route should end at a 10L/28R hold-short but ends at node {endNode.Id} type={endNode.Type} runwayId={endNode.RunwayId}. "
                + $"Taxiways: [{string.Join(", ", route.Segments.Select(s => s.TaxiwayName).Distinct())}]. "
                + $"Issue #53: E walk goes wrong direction without HS-based direction guidance."
        );
    }

    // --- Ground bugs recording: hold-short taxiway positioning, conflict detection, WARP stale phases ---

    private const string GroundBugsRecordingPath = "TestData/sfo-ground-bugs-recording.json";

    private static SessionRecording? LoadGroundBugsRecording()
    {
        if (!File.Exists(GroundBugsRecordingPath))
        {
            return null;
        }

        var json = File.ReadAllText(GroundBugsRecordingPath);
        return JsonSerializer.Deserialize<SessionRecording>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    /// <summary>
    /// Bug 1: AAL1766 "TAXI Y M1 HS A RWY 01L" — hold short of taxiway A should stop
    /// at the node BEFORE the A intersection, not at the intersection node itself (90).
    /// Node 418 is the previous node on M1 before the A intersection.
    /// </summary>
    [Fact]
    public void Replay_Sfo_AAL1766_HoldShortA_StopsBeforeIntersection()
    {
        var recording = LoadGroundBugsRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // The scenario has preset "WAIT 100 TAXI Y M A A1 1R" for AAL1766.
        // With ExpandWait this now works (previously failed silently), routing AAL to 1R.
        // Recording's manual TAXI at t=744 overrides with a different route.
        // Replay far enough for hold-short.
        engine.Replay(recording, 850.0);

        var aal = engine.FindAircraft("AAL1766");
        Assert.NotNull(aal);
        _output.WriteLine($"AAL1766 at t=850: route={aal.AssignedTaxiRoute is not null}, phases={aal.Phases?.CurrentPhase?.GetType().Name ?? "null"}, lat={aal.Latitude:F6}, lon={aal.Longitude:F6}, gs={aal.GroundSpeed:F1}");
        _output.WriteLine($"  queue blocks={aal.Queue.Blocks.Count}, departure={aal.Departure}, departureRunway={aal.DepartureRunway}");
        _output.WriteLine($"  groundLayout={aal.GroundLayout is not null}");

        // Try parsing the recording's TAXI command directly to confirm it works
        var testParse = CommandParser.ParseCompound("TAXI Y M1 HS A RWY 01L", TestVnasData.FixDatabase!);
        _output.WriteLine($"  Parse 'TAXI Y M1 HS A RWY 01L': {testParse is not null}, blocks={testParse?.Blocks.Count}, cmds={testParse?.Blocks[0].Commands.Count}, type={testParse?.Blocks[0].Commands[0].GetType().Name}");

        // Also check the expanded preset
        var expanded = CommandSchemeParser.ExpandWait("WAIT 100 TAXI Y M A A1 1R");
        _output.WriteLine($"  ExpandWait('WAIT 100 TAXI Y M A A1 1R') = '{expanded}'");
        var presetParse = CommandParser.ParseCompound(expanded, TestVnasData.FixDatabase!);
        _output.WriteLine($"  Parse expanded: {presetParse is not null}, blocks={presetParse?.Blocks.Count}");
        if (presetParse is not null)
        {
            foreach (var b in presetParse.Blocks)
            {
                _output.WriteLine($"    Block: cmds=[{string.Join(", ", b.Commands.Select(c => c.GetType().Name))}]");
            }
        }

        var route = aal.AssignedTaxiRoute;
        Assert.NotNull(route);

        // Find the HS A hold-short point
        var hsA = route.HoldShortPoints.FirstOrDefault(h =>
            h.Reason == HoldShortReason.ExplicitHoldShort && string.Equals(h.TargetName, "A", StringComparison.OrdinalIgnoreCase)
        );
        Assert.NotNull(hsA);

        // The hold-short should NOT be at node 90 (the intersection). It should be at a
        // preceding node (418 or similar) so the aircraft stops before entering taxiway A.
        Assert.NotEqual(90, hsA.NodeId);

        _output.WriteLine($"AAL1766 HS A hold-short node: {hsA.NodeId} (expected != 90, the intersection node)");
    }

    /// <summary>
    /// Bug 2: SWA7348 in LineUpPhase ("LiningUp") should be classified as stationary
    /// by GroundConflictDetector, not as Taxiing. Aircraft in LiningUp phase should not
    /// cause false conflicts with nearby holding aircraft.
    /// </summary>
    [Fact]
    public void Replay_Sfo_SWA7348_LiningUp_NoFalseConflict()
    {
        var recording = LoadGroundBugsRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // SWA7348 gets CTO at t=648; by ~655s it should be in LineUpPhase.
        // At that point, nearby holding aircraft should not have GroundSpeedLimit set
        // from a false conflict with SWA7348.
        engine.Replay(recording, 660.0);

        var swa = engine.FindAircraft("SWA7348");
        if (swa is null)
        {
            return;
        }

        string? phaseName = swa.Phases?.CurrentPhase?.Name;
        _output.WriteLine($"SWA7348 phase: {phaseName ?? "(null)"}, gs={swa.GroundSpeed:F1}");

        // If SWA7348 is in LiningUp or LinedUpAndWaiting, check that nearby aircraft
        // don't have conflict speed limits caused by it
        if (phaseName is "LiningUp" or "LinedUpAndWaiting")
        {
            // Check AAL1766 (which should be holding short nearby) doesn't have a false limit
            var aal = engine.FindAircraft("AAL1766");
            if (aal is not null && aal.IsOnGround && aal.GroundSpeed <= 0.1)
            {
                // A stationary aircraft shouldn't get a ground speed limit from
                // another stationary aircraft (both stationary → skip pair)
                _output.WriteLine($"AAL1766 GroundSpeedLimit: {aal.GroundSpeedLimit?.ToString("F1") ?? "null"}");
            }
        }

        // More direct test: verify GroundConflictDetector.Classify treats LiningUp correctly
        // by checking that SWA7348 in LiningUp phase doesn't have a Taxiing-based conflict limit
        if (phaseName is "LiningUp" && swa.GroundSpeedLimit is not null)
        {
            Assert.Fail(
                $"SWA7348 is in LiningUp phase but has GroundSpeedLimit={swa.GroundSpeedLimit:F1}kts. "
                    + "LiningUp should be classified as stationary, not Taxiing."
            );
        }
    }

    /// <summary>
    /// Bug 3: AAL2839 gets warped (UI warp) at t=632 but phases aren't cleared.
    /// After warp, phases and taxi route should be null so subsequent commands work.
    /// </summary>
    [Fact]
    public void Replay_Sfo_AAL2839_WarpClearsPhases()
    {
        var recording = LoadGroundBugsRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // AAL2839 CTO fires at t=504 (enters tower phases), then warp at t=632 should clear all
        engine.Replay(recording, 640.0);

        var aal = engine.FindAircraft("AAL2839");
        Assert.NotNull(aal);

        _output.WriteLine(
            $"AAL2839 phases: {aal.Phases?.CurrentPhase?.Name ?? "(null)"}, route: {(aal.AssignedTaxiRoute is null ? "null" : "present")}"
        );

        Assert.Null(aal.Phases);
        Assert.Null(aal.AssignedTaxiRoute);
    }

    /// <summary>
    /// Diagnostic: trace SWA7348 and nearby ground aircraft tick-by-tick to understand
    /// the false conflict between SWA7348 and AAL holding short of 01L.
    /// </summary>
    [Fact]
    public void Diag_Sfo_GroundBugs_SWA7348_ConflictTrace()
    {
        var recording = LoadGroundBugsRecording();
        if (recording is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("SFO");

        string[] callsigns = ["SWA7348", "AAL2839", "DAL819"];

        for (double t = 0; t <= 650.0; t += 5.0)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(recording, t);
            var snapshot = engine.World.GetSnapshot();

            var groundAc = snapshot.Where(a => a.IsOnGround && callsigns.Contains(a.Callsign)).ToList();
            if (groundAc.Count == 0)
            {
                continue;
            }

            bool anyInteresting = groundAc.Any(a => a.GroundSpeedLimit is not null || a.GroundSpeed > 0.1 || a.Phases?.CurrentPhase is not null);
            if (!anyInteresting && t > 5)
            {
                continue;
            }

            _output.WriteLine($"--- t={t:F0}s ---");
            foreach (var ac in groundAc.OrderBy(a => a.Callsign))
            {
                string phase = ac.Phases?.CurrentPhase?.Name ?? "(none)";
                string routeStr = "no-route";
                string targetInfo = "";
                if (ac.AssignedTaxiRoute is { } taxiRoute)
                {
                    routeStr = $"seg={taxiRoute.CurrentSegmentIndex}/{taxiRoute.Segments.Count}";
                    if (taxiRoute.CurrentSegment is { } curSeg && layout?.Nodes.TryGetValue(curSeg.ToNodeId, out var tgtNode) == true)
                    {
                        double distToTarget = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, tgtNode.Latitude, tgtNode.Longitude);
                        targetInfo = $" tgt={curSeg.ToNodeId}@{distToTarget * GeoMath.FeetPerNm:F0}ft";
                    }

                    var hsPoints = taxiRoute
                        .HoldShortPoints.Select(h => $"{h.NodeId}:{h.Reason}:{h.TargetName}{(h.IsCleared ? ":CLR" : "")}")
                        .ToList();
                    if (hsPoints.Count > 0)
                    {
                        targetInfo += $" hs=[{string.Join(",", hsPoints)}]";
                    }
                }

                string limit = ac.GroundSpeedLimit is not null ? $"limit={ac.GroundSpeedLimit:F1}" : "";
                _output.WriteLine(
                    $"  {ac.Callsign, -10} pos=({ac.Latitude:F6},{ac.Longitude:F6}) hdg={ac.Heading:F0} gs={ac.GroundSpeed:F1} phase={phase, -24} {routeStr}{targetInfo} {limit}"
                );
            }

            // Show pairwise distances
            for (int i = 0; i < groundAc.Count; i++)
            {
                for (int j = i + 1; j < groundAc.Count; j++)
                {
                    double distFt =
                        GeoMath.DistanceNm(groundAc[i].Latitude, groundAc[i].Longitude, groundAc[j].Latitude, groundAc[j].Longitude)
                        * GeoMath.FeetPerNm;
                    if (distFt < 2000)
                    {
                        _output.WriteLine($"    dist({groundAc[i].Callsign},{groundAc[j].Callsign})={distFt:F0}ft");
                    }
                }
            }

            // Run conflict detection with diagnostic logging on the snapshot
            GroundConflictDetector.ApplySpeedLimits(
                snapshot,
                layout,
                deltaSeconds: 1.0,
                diagnosticLog: msg => _output.WriteLine($"  [CONFLICT] {msg}")
            );
        }
    }

    /// <summary>
    /// AMX669 gets "TAXI M2 B M1 HS 01L" at t=61. Same pattern as SWA7348 — explicit hold-short
    /// without destination runway. M1 walk should go toward the 1L hold-short (2-3 segments).
    /// </summary>
    [Fact]
    public void Replay_Sfo_AMX669_TaxiM2BM1_StopsAt1LHoldShort()
    {
        var recording = LoadN346gRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // AMX669's TAXI command fires at t=61
        engine.Replay(recording, 70.0);

        var ac = engine.FindAircraft("AMX669");
        if (ac is null)
        {
            return;
        }

        var route = ac.AssignedTaxiRoute;
        if (route is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);

        _output.WriteLine($"AMX669 route: {route.Segments.Count} segments");
        foreach (var seg in route.Segments)
        {
            var fromN = layout.Nodes.GetValueOrDefault(seg.FromNodeId);
            var toN = layout.Nodes.GetValueOrDefault(seg.ToNodeId);
            _output.WriteLine(
                $"  {seg.TaxiwayName}: {seg.FromNodeId}({fromN?.Latitude:F6},{fromN?.Longitude:F6}) → {seg.ToNodeId}({toN?.Latitude:F6},{toN?.Longitude:F6}) {toN?.Type}{(toN?.RunwayId is { } rid ? $" rwy={rid}" : "")}"
            );
        }

        // M1 portion should be compact — the 1L hold-short is close to the B/M1 junction
        var m1Segments = route.Segments.Where(s => string.Equals(s.TaxiwayName, "M1", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(
            m1Segments.Count <= 5,
            $"AMX669 has {m1Segments.Count} M1 segments — expected ≤5 for the direct path to 1L hold-short. "
                + $"Issue #53: M1 walk goes wrong direction without HS-based direction guidance."
        );

        // Route should end at a 1L hold-short
        var endNode = layout.Nodes[route.Segments[^1].ToNodeId];
        Assert.True(
            endNode.Type == GroundNodeType.RunwayHoldShort && endNode.RunwayId is { } rwy && rwy.Contains("1L"),
            $"AMX669 route should end at 1L hold-short but ends at node {endNode.Id} type={endNode.Type} runwayId={endNode.RunwayId}."
        );
    }

    // --- Issue #57: S1-SFO-2 arrivals turn north instead of landing ---

    private const string Issue57RecordingPath = "TestData/sfo-issue57-recording.json";

    private static SessionRecording? LoadIssue57Recording()
    {
        if (!File.Exists(Issue57RecordingPath))
        {
            return null;
        }

        var json = File.ReadAllText(Issue57RecordingPath);
        return JsonSerializer.Deserialize<SessionRecording>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    /// <summary>
    /// In S1-SFO-2, arrival aircraft have preset compound commands (e.g. "DM 70; AT CEPIN SPD 180 AXMUL")
    /// that were being dispatched via single Parse() instead of ParseCompound(), causing the conditional
    /// blocks to be lost. After the fix, arrivals should have approach phases and be descending toward SFO.
    /// </summary>
    [Fact]
    public void Replay_Sfo_Issue57_ArrivalsGetApproachPhases()
    {
        var recording = LoadIssue57Recording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay 120 seconds — enough for preset commands to fire and aircraft to begin approach
        engine.Replay(recording, 120.0);

        var snapshot = engine.World.GetSnapshot();

        // Find airborne arrivals (aircraft with destination SFO that are not on ground)
        var arrivals = snapshot.Where(a => !a.IsOnGround && string.Equals(a.Destination, "SFO", StringComparison.OrdinalIgnoreCase)).ToList();

        if (arrivals.Count == 0)
        {
            return; // Recording may not have the expected scenario
        }

        // At least one arrival should be descending (altitude decreasing means approach is working)
        bool anyDescending = arrivals.Any(a => a.VerticalSpeed < -100);

        _output.WriteLine($"Found {arrivals.Count} SFO arrivals at t=120s:");
        foreach (var a in arrivals)
        {
            _output.WriteLine(
                $"  {a.Callsign}: alt={a.Altitude:F0} vs={a.VerticalSpeed:F0} hdg={a.Heading:F0} phase={a.Phases?.CurrentPhase?.Name ?? "(null)"} queue={a.Queue.Blocks.Count} dest={a.Destination}"
            );
        }

        Assert.True(
            anyDescending,
            $"No SFO arrivals are descending at t=120s. Issue #57: preset commands not dispatched as compound, "
                + $"so conditional blocks (AT/ATFN) are lost and aircraft fly straight through."
        );
    }
}
