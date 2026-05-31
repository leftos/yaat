using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for GitHub issue #165 — SKW3404 (and head/Maxim's screen) spinning
/// during taxi at SFO via route TAXI A E B B3 A B1 Z S. Reported failure modes:
/// spins when turning on E, spins near B3/A and A/B1, "little wiggle on B near T".
///
/// Recording: <c>issue165-skw3404-sfo-taxi-spin-recording.zip</c> (S1-SFO-4 scenario,
/// 457s, 6 aircraft, ARTCC ZOA). At t=132s the user issues TAXI A E B B3 A B1 Z S.
/// In the recorded baseline (pre-fix code) SKW3404's IAS drops to 2-6 kt for
/// extended windows (t≈250-325, t≈410-457) — the canonical orbit-near-fillet-vertex
/// signature.
///
/// Assertion: under current code (parking-node fix + entry-alignment threshold +
/// synthesis-during-entry-align + orbit detector + cluster planner), SKW3404 must
/// not sit stuck under 5 kt for more than 20 consecutive seconds anywhere along
/// its route. Stops at hold-shorts (where ias=0 is legitimate) are exempt because
/// they correspond to a HoldingShort* phase, not the orbit signature we're
/// guarding against.
/// </summary>
public class Issue165SkwTaxiSpinTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue165-skw3404-sfo-taxi-spin-recording.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundNavigator", LogLevel.Debug)
            .EnableCategory("TaxiingPhase", LogLevel.Debug)
            .InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Skw3404_FullResolvedRoute_Diagnostic()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);
        for (int t = 1; t <= 140; t++)
        {
            engine.ReplayOneSecond();
        }

        var ac = engine.FindAircraft("SKW3404");
        Assert.NotNull(ac);
        var route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        output.WriteLine($"Resolved route summary: {route.ToSummary()}");
        output.WriteLine($"Total segments: {route.Segments.Count}");
        output.WriteLine($"Hold-shorts: [{string.Join(", ", route.HoldShortPoints.Select(h => $"{h.TargetName}@{h.NodeId}({h.Reason})"))}]");
        output.WriteLine("");

        double prevArrival = double.NaN;
        string? prevTwy = null;
        for (int i = 0; i < route.Segments.Count; i++)
        {
            var seg = route.Segments[i];
            double dep = seg.Edge.DepartureBearing;
            double arr = seg.Edge.ArrivalBearing;
            double distFt = seg.Edge.DistanceNm * 6076.12;
            string kind = seg.Edge.Edge is GroundArc ? "arc" : "    ";

            // Detect cross-segment corner (turn between prev arrival and this departure).
            string cornerNote = string.Empty;
            if (!double.IsNaN(prevArrival))
            {
                double turn = Math.Abs((((dep - prevArrival) + 540.0) % 360.0) - 180.0);
                if (turn >= 30.0)
                {
                    cornerNote = $"  <-- corner {turn:F0}deg";
                    if (turn >= 150.0)
                    {
                        cornerNote += " *** U-TURN ***";
                    }
                }
            }
            string twyChange = (prevTwy is not null && prevTwy != seg.TaxiwayName) ? $" [twy {prevTwy} -> {seg.TaxiwayName}]" : string.Empty;
            output.WriteLine(
                $"  seg[{i, 3}] twy={seg.TaxiwayName, -6} {kind} {seg.FromNodeId, 4}->{seg.ToNodeId, -4} {distFt, 6:F1}ft  dep={dep, 5:F1} arr={arr, 5:F1}{twyChange}{cornerNote}"
            );
            prevArrival = arr;
            prevTwy = seg.TaxiwayName;
        }
    }

    [Fact]
    public void Skw3404_TickByTick_Diagnostic()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);
        for (int t = 1; t <= 457; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("SKW3404");
            if (ac is null)
            {
                continue;
            }
            var route = ac.Ground.AssignedTaxiRoute;
            string phaseName = ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)";
            int segIdx = route?.CurrentSegmentIndex ?? -1;
            int segTotal = route?.Segments.Count ?? 0;
            int target = (route is not null && segIdx >= 0 && segIdx < segTotal) ? route.Segments[segIdx].ToNodeId : -1;
            output.WriteLine(
                $"t={t, 3} ias={ac.IndicatedAirspeed, 5:F1} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees, 5:F1} phase={phaseName} seg={segIdx}/{segTotal} -> {target}"
            );
        }
    }

    [Fact]
    public void Skw3404_Seg12_PathfinderDiagnostic()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        // Node 1249 is where the A-walk in seg[0] begins (first segment of the resolved route).
        // The aircraft is parked nearby and the RAMP bridge gets to 1249. Use 1249 as start.
        const int startNode = 1249;
        var diagLines = new System.Collections.Generic.List<string>();
        // SKW3404 is a CRJ (jet). The V2-native equivalent of this sequence is
        // Issue165_V2_SkwRoute_ResolvesWithoutFailure.
        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: startNode,
            taxiwayNames: ["A", "E", "B", "B3", "A", "B1", "Z", "S"],
            out string? failReason,
            new ExplicitPathOptions { AirportId = "SFO", DiagnosticLog = s => diagLines.Add(s) },
            AircraftCategory.Jet
        );

        foreach (var line in diagLines)
        {
            output.WriteLine(line);
        }

        Assert.Null(failReason);
        Assert.NotNull(route);
        output.WriteLine($"Total segments: {route.Segments.Count}");
        double prevArr = double.NaN;
        for (int i = 0; i < route.Segments.Count; i++)
        {
            var s = route.Segments[i];
            double dep = s.Edge.DepartureBearing;
            double arr = s.Edge.ArrivalBearing;
            string kind = s.Edge.Edge is GroundArc ? "arc" : "   ";
            string corner = "";
            if (!double.IsNaN(prevArr))
            {
                double turn = Math.Abs((((dep - prevArr) + 540.0) % 360.0) - 180.0);
                if (turn >= 150)
                {
                    corner = $" *** U-TURN {turn:F0}";
                }
            }
            output.WriteLine($"  seg[{i, 3}] {kind} {s.TaxiwayName, -6} {s.FromNodeId, 4}->{s.ToNodeId, -4} dep={dep, 5:F1} arr={arr, 5:F1}{corner}");
            prevArr = arr;
        }
    }

    // Regression guard for the issue #165 orbit: the SFO E->B junction (node 142) must
    // not produce a 180-degree U-turn that strands SKW3404 sub-5 kt for an extended
    // window. The pathfinder resolves the junction cleanly, so the worst stuck episode
    // outside any hold-short stays well under the 20s threshold.
    [Fact]
    public void Skw3404_DoesNotOrbitDuringTaxi()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);

        const int TotalSeconds = 457;
        const double StuckThresholdKts = 5.0;
        const int MaxStuckSecondsOutsideHoldShort = 20;

        int consecutiveStuckSec = 0;
        int maxStuckSecObserved = 0;
        double stuckEpisodeStartLat = 0;
        double stuckEpisodeStartLon = 0;
        double worstStuckLat = 0;
        double worstStuckLon = 0;
        string? worstStuckPhase = null;

        for (int t = 1; t <= TotalSeconds; t++)
        {
            engine.ReplayOneSecond();

            var ac = engine.FindAircraft("SKW3404");
            if (ac is null)
            {
                continue;
            }

            string phaseName = ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)";
            bool atHoldShort =
                phaseName.Contains("HoldingShort", StringComparison.Ordinal)
                || phaseName.Contains("HoldingInPosition", StringComparison.Ordinal)
                || phaseName.Contains("AtParking", StringComparison.Ordinal)
                || phaseName.Contains("Pushback", StringComparison.Ordinal)
                || phaseName.Contains("HoldingAfterPushback", StringComparison.Ordinal)
                || phaseName.Contains("CrossingRunway", StringComparison.Ordinal);

            if (ac.IndicatedAirspeed < StuckThresholdKts && !atHoldShort)
            {
                if (consecutiveStuckSec == 0)
                {
                    stuckEpisodeStartLat = ac.Position.Lat;
                    stuckEpisodeStartLon = ac.Position.Lon;
                }
                consecutiveStuckSec++;
                if (consecutiveStuckSec > maxStuckSecObserved)
                {
                    maxStuckSecObserved = consecutiveStuckSec;
                    worstStuckLat = ac.Position.Lat;
                    worstStuckLon = ac.Position.Lon;
                    worstStuckPhase = phaseName;
                }
            }
            else
            {
                consecutiveStuckSec = 0;
            }
        }

        output.WriteLine($"max stuck = {maxStuckSecObserved}s at ({worstStuckLat:F6},{worstStuckLon:F6}) phase={worstStuckPhase ?? "(none)"}");

        Assert.True(
            maxStuckSecObserved <= MaxStuckSecondsOutsideHoldShort,
            $"SKW3404 was stuck below {StuckThresholdKts} kt for {maxStuckSecObserved}s outside any hold-short — "
                + $"orbit signature at ({worstStuckLat:F6},{worstStuckLon:F6}) phase={worstStuckPhase ?? "(none)"}. "
                + $"Threshold {MaxStuckSecondsOutsideHoldShort}s."
        );
    }
}
