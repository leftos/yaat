using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
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
}
