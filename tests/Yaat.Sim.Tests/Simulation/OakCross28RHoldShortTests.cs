using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for OAK taxiway B crossing runway 28R hold-short behavior.
///
/// Bug: N172SP given TAXI B 28L then CROSS 28R stopped at the exit hold-short
/// point for 28R instead of continuing through the crossing. The aircraft should
/// not stop at any 28R hold-short after CROSS 28R is issued.
///
/// Recording: S2-OAK-1 VFR Takeoff/Landing — N172SP (C172) taxiing from ramp
/// to runway 28L via taxiway B, crossing runway 28R.
/// </summary>
public class OakCross28RHoldShortTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-cross-28r-recording.yaat-recording.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Diagnostic: logs all RunwayHoldShort nodes on the B taxi route from
    /// N172SP's position to 28L, confirming how many 28R hold-short points
    /// the annotator creates.
    /// </summary>
    [Fact]
    public void Diagnostic_LogHoldShortNodesOnTaxiB()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to just after TAXI B 28L command at t=823
        engine.Replay(recording, 824);

        var ac = engine.FindAircraft("N172SP");
        if (ac is null)
        {
            output.WriteLine("N172SP not found at t=824");
            return;
        }

        output.WriteLine($"N172SP at t=824: ({ac.Position.Lat:F6},{ac.Position.Lon:F6}) phase={ac.Phases?.CurrentPhase?.GetType().Name}");

        var route = ac.AssignedTaxiRoute;
        if (route is null)
        {
            output.WriteLine("No taxi route assigned");
            return;
        }

        output.WriteLine($"\n=== Taxi Route Segments ({route.Segments.Count} total) ===");
        var layout = GeoJsonParser.Parse("OAK", File.ReadAllText("TestData/oak.geojson"), null);

        int hsNodeCount28R = 0;
        for (int i = 0; i < route.Segments.Count; i++)
        {
            var seg = route.Segments[i];
            string nodeInfo = "";
            if (layout.Nodes.TryGetValue(seg.ToNodeId, out var node))
            {
                nodeInfo = $" type={node.Type}";
                if (node.Type == GroundNodeType.RunwayHoldShort)
                {
                    nodeInfo += $" rwyId={node.RunwayId}";
                    if (node.RunwayId?.ToString().Contains("28R") == true || node.RunwayId?.ToString().Contains("10L") == true)
                    {
                        hsNodeCount28R++;
                        nodeInfo += " *** 28R/10L ***";
                    }
                }
            }

            output.WriteLine($"  seg[{i}]: {seg.TaxiwayName} -> node {seg.ToNodeId}{nodeInfo}");
        }

        output.WriteLine($"\nTotal 28R/10L RunwayHoldShort nodes on path: {hsNodeCount28R}");

        output.WriteLine($"\n=== HoldShortPoints ({route.HoldShortPoints.Count} total) ===");
        foreach (var hs in route.HoldShortPoints)
        {
            output.WriteLine($"  nodeId={hs.NodeId} reason={hs.Reason} target={hs.TargetName} cleared={hs.IsCleared}");
        }

        int hsPoints28R = route.HoldShortPoints.Count(h =>
            h.TargetName is not null && (h.TargetName.Contains("28R") || h.TargetName.Contains("10L"))
        );
        output.WriteLine($"\nHoldShortPoints for 28R/10L: {hsPoints28R}");

        // If there are 3+ HS nodes for 28R, the annotator creates multiple entries
        if (hsNodeCount28R >= 3)
        {
            output.WriteLine("\n*** CONFIRMED: 3+ RunwayHoldShort nodes for 28R on path — annotator creates extra entries ***");
        }
    }

    /// <summary>
    /// Diagnostic: replays the recording through TAXI and CROSS commands, logging
    /// aircraft state at each step to trace the bug.
    /// </summary>
    [Fact]
    public void Diagnostic_ReplayTaxiAndCross()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to just after TAXI B 28L at t=823
        engine.Replay(recording, 824);
        var ac = engine.FindAircraft("N172SP");
        if (ac is null)
        {
            return;
        }

        output.WriteLine($"t=824 (after TAXI): phase={ac.Phases?.CurrentPhase?.GetType().Name} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6})");

        // Replay second by second from 824 to 900
        for (int t = 825; t <= 900; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N172SP");
            if (ac is null)
            {
                break;
            }

            string phaseName = ac.Phases?.CurrentPhase?.GetType().Name ?? "null";
            bool isHoldingShort = ac.Phases?.CurrentPhase is HoldingShortPhase;

            if (t == 873 || t == 874 || isHoldingShort || t % 10 == 0)
            {
                string holdInfo = "";
                if (ac.Phases?.CurrentPhase is HoldingShortPhase hsPhase)
                {
                    holdInfo = $" holdTarget={hsPhase.HoldShort.TargetName} nodeId={hsPhase.HoldShort.NodeId} cleared={hsPhase.HoldShort.IsCleared}";
                }

                output.WriteLine($"t={t}: phase={phaseName} gs={ac.GroundSpeed:F1}{holdInfo}");
            }
        }
    }

    /// <summary>
    /// When taxiing via B across 28R, the south-side (exit) 28R hold-short
    /// node (186) must NOT be added as a RunwayCrossing entry in the route.
    /// The original bug: N172SP stopped at 186 AFTER being cleared to cross
    /// 28R, because the annotator added both sides of the crossing as
    /// independent hold-shorts. Paired exit-side skipping fixes this.
    ///
    /// Note: this test does NOT require the north-side (entry) HS to be
    /// absent — whether an entry-side RunwayCrossing appears in the route
    /// depends on the aircraft's starting position. If the aircraft is
    /// already at the entry-side HS (recorded state), the pre-seed skips
    /// it. If the aircraft is a few feet short of the line (more accurate
    /// stop kinematics), the entry-side HS is legitimately added and the
    /// aircraft holds there before crossing. Either outcome is valid. The
    /// invariant is solely: node 186 (south side) is never a crossing HS.
    /// </summary>
    [Fact]
    public void RerouteFrom28R_ExitSideHoldShort_NotAddedAsCrossing()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to just after TAXI B 28L at t=823 — the route is now assigned
        engine.Replay(recording, 824);

        var ac = engine.FindAircraft("N172SP");
        if (ac is null)
        {
            return;
        }

        var route = ac.AssignedTaxiRoute;
        Assert.NotNull(route);

        output.WriteLine($"Route: {route.ToSummary()}");
        output.WriteLine($"Starting node (first seg FromNodeId): {route.Segments[0].FromNodeId}");
        foreach (var hs in route.HoldShortPoints)
        {
            output.WriteLine($"  HS: nodeId={hs.NodeId} reason={hs.Reason} target={hs.TargetName}");
        }

        // Node 186 is the south side (exit side) of the B crossing of 28R/10L.
        // It must never be added as a RunwayCrossing hold-short.
        const int exitSideNodeId = 186;
        Assert.DoesNotContain(route.HoldShortPoints, h => (h.NodeId == exitSideNodeId) && (h.Reason == HoldShortReason.RunwayCrossing));
    }

    /// <summary>
    /// Tick-by-tick recorder for the reroute-from-28R scenario. Writes
    /// <c>.tmp/oak-reroute28r-ticks.csv</c> capturing the full aircraft
    /// trajectory from TAXI issuance to stop at the crossing hold-short.
    /// Render with LayoutInspector --ticks.
    /// </summary>
    [Fact]
    public void Diagnostic_RecordRerouteTicks()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay from t=700 to capture approach to the first hold-short,
        // the TAXI B 28L reroute at 823, the subsequent hold at the crossing,
        // the CROSS 28R clearance, and the taxi toward the departure runway.
        engine.Replay(recording, 700);
        var ac = engine.FindAircraft("N172SP");
        if (ac is null)
        {
            return;
        }

        var recorder = new TickRecorder(ac);
        recorder.Record(700);

        for (int t = 701; t <= 1000; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N172SP");
            if (ac is null)
            {
                break;
            }
            recorder.Record(t);
        }
        if (ac is not null)
        {
            output.WriteLine(
                $"[diag] final at t=1000 phase={ac.Phases?.CurrentPhase?.Name} gs={ac.GroundSpeed:F2} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6})"
            );
        }

        string csvPath = Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "oak-reroute28r-ticks.csv");
        recorder.WriteCsv(csvPath);
        output.WriteLine($"[diag] wrote {recorder.Count} ticks to {csvPath}");
    }
}
