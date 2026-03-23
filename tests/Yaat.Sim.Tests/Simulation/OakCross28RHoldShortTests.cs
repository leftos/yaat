using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
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
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        NavigationDatabase.SetInstance(navDb);
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

        output.WriteLine($"N172SP at t=824: ({ac.Latitude:F6},{ac.Longitude:F6}) phase={ac.Phases?.CurrentPhase?.GetType().Name}");

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

        output.WriteLine($"t=824 (after TAXI): phase={ac.Phases?.CurrentPhase?.GetType().Name} pos=({ac.Latitude:F6},{ac.Longitude:F6})");

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
    /// When re-routed from a 28R destination hold-short (north side) to 28L
    /// via B, the exit-side 28R hold-short (south side) must NOT be added as
    /// a RunwayCrossing entry. The annotator should recognise the starting
    /// position as the entry side and pair the south-side node as exit.
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

        // The exit-side 28R node must not be a RunwayCrossing hold-short.
        // Only the destination 28L hold-short should remain.
        var crossing28R = route
            .HoldShortPoints.Where(h =>
                (h.Reason == HoldShortReason.RunwayCrossing) && h.TargetName is not null && RunwayIdentifier.Parse(h.TargetName).Contains("28R")
            )
            .ToList();

        Assert.Empty(crossing28R);
    }
}
