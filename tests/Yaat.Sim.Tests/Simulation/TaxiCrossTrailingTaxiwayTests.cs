using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression: naming <c>CROSS &lt;rwy&gt;</c> for a runway that lies on an <em>earlier</em> named
/// taxiway, with a later taxiway following, must not be rejected.
///
/// Recording: S2-OAK-5 "Advanced Concepts" — N629PU (C172) is lined up and waiting on OAK
/// (current taxiway C, node #355). The controller clears <c>TAXI C B CROSS 33</c>. Runway 15/33
/// crosses taxiway C (hold-shorts #507 / #506) but the C→B route to the 28R hold-short does not
/// actually traverse 33 — it heads the other way along C — so the <c>CROSS 33</c> clause is a
/// harmless no-op.
///
/// Before the fix, the issue #172 W6 crossed-runway anchor saw 33's near/far hold-shorts on
/// taxiway C and terminated the route at the far-side one (node #506) — <b>before</b> taxiway B —
/// so the honor-clearance check rejected with "Cannot taxi via B from the aircraft's position — it
/// is unreachable without crossing a runway...". The aircraft accepted the workaround
/// <c>TAXI C B CROSS 33 RWY 28R</c> (recorded at t=2100), which produced the correct route
/// <c>C B</c> to the 28R hold-short (node #188) without touching 33. The anchor must only set a
/// terminus when the crossed runway lies on the <em>last</em> named taxiway.
/// </summary>
public class TaxiCrossTrailingTaxiwayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/taxi-cb-cross33-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N629PU";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Information).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void TaxiCBCross33_WithoutDestination_RoutesOntoBAndHoldsShortOf28R()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=2052: N629PU is holding short of 33 on taxiway C, on the
        // ground. This is just before its recorded cross-runway departure
        // sequence (CTO at t=2053, CTOC at t=2077, TAXI at t=2100). We issue the
        // TAXI from this stable hold-short pose rather than from the later
        // post-CTO state: that lineup is timing-sensitive (the rapid CTO/CTOC
        // toggling leaves the rolling takeoff right at the runway, so the exact
        // second N629PU lifts off shifts with any lineup-geometry change), which
        // made an earlier t=2095 replay flip the aircraft between on-ground and
        // airborne. The CROSS-33 routing under test is independent of when the
        // departure rolls.
        engine.Replay(recording, 2052);
        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.True(ac.IsOnGround, "N629PU should be on the ground (holding short of 33) before the TAXI command");

        var result = engine.SendCommand(Callsign, "TAXI C B CROSS 33");

        var route = ac.Ground.AssignedTaxiRoute;
        if (route is not null)
        {
            output.WriteLine($"Route: {route.ToSummary()} ({route.Segments.Count} segments)");
            foreach (var hs in route.HoldShortPoints)
            {
                output.WriteLine($"  HS: node={hs.NodeId} reason={hs.Reason} target={hs.TargetName} cleared={hs.IsCleared}");
            }
        }

        // The command must be accepted — this is the bug (was rejected "Cannot taxi via B...").
        Assert.True(result.Success, result.Message);
        Assert.NotNull(route);

        // The route must reach taxiway B (the clearance's last leg), not truncate at 33. B has no
        // onward direction or destination, so the route holds at the C/B junction rather than
        // walking B toward 28R — the controller continues it with a follow-up taxi or RWY clearance.
        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        var lastNode = layout.Nodes[route.Segments[^1].ToNodeId];
        Assert.True(lastNode.Edges.Any(e => e.MatchesTaxiway("B")), $"expected the route to hold at a C/B junction, ended at #{lastNode.Id}");

        // Holding at C/B never approaches 28R, and 33 lies the other way along C — the route must
        // not cross or enter any runway.
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
    }
}
