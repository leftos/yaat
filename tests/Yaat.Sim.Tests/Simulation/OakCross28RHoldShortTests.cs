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
    /// When taxiing via B across 28R, the far-side (exit) 28R hold-short node
    /// must NOT be added as a RunwayCrossing entry in the route. The original
    /// bug: N172SP stopped at that bar AFTER being cleared to cross 28R,
    /// because the annotator added both sides of the crossing as independent
    /// hold-shorts. Paired exit-side skipping fixes this.
    ///
    /// Note: this test does NOT require the north-side (entry) HS to be
    /// absent — whether an entry-side RunwayCrossing appears in the route
    /// depends on the aircraft's starting position. If the aircraft is
    /// already at the entry-side HS (recorded state), the pre-seed skips
    /// it. If the aircraft is a few feet short of the line (more accurate
    /// stop kinematics), the entry-side HS is legitimately added and the
    /// aircraft holds there before crossing. Either outcome is valid. The
    /// invariant is solely: the exit-side bar is never a crossing HS.
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
        Assert.NotNull(ac);

        var route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        var layout = ac.Ground.Layout;
        Assert.NotNull(layout);

        // Resolve the exit-side bar by geometry, not by id: B meets 28R/10L at a paired hold short, and
        // the exit side is simply the bar on the far side of the runway from where the aircraft sits.
        var bars = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "B");
        Assert.Equal(2, bars.Count);
        var exitSideBar = bars.OrderByDescending(n => GeoMath.DistanceNm(ac.Position.Lat, ac.Position.Lon, n.Position.Lat, n.Position.Lon)).First();

        output.WriteLine($"Route: {route.ToSummary()}");
        output.WriteLine($"Starting node (first seg FromNodeId): {route.Segments[0].FromNodeId}");
        foreach (var hs in route.HoldShortPoints)
        {
            output.WriteLine($"  HS: nodeId={hs.NodeId} reason={hs.Reason} target={hs.TargetName}");
        }

        output.WriteLine($"Exit-side 28R bar on B: node {exitSideBar.Id}");
        Assert.DoesNotContain(route.HoldShortPoints, h => (h.NodeId == exitSideBar.Id) && (h.Reason == HoldShortReason.RunwayCrossing));
    }
}
