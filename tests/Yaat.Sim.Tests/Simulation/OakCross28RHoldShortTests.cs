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
    private const string RecordingPath = "TestData/921b8c537a44.zip";

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

        var route = ac.Ground.AssignedTaxiRoute;
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
}
