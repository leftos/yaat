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

        // Replay to just before the recorded TAXI (t=2100): N629PU is lined up and waiting on C.
        engine.Replay(recording, 2095);
        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.True(ac.IsOnGround, "N629PU should be on the ground (lined up) before the TAXI command");

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

        // The route must continue onto taxiway B (the clearance's last leg), not stop just past 33.
        Assert.Contains(route.Segments, s => s.TaxiwayName == "B");

        // The route must hold short of 28R (the runway it was not cleared across) — the same place the
        // recorded RWY-28R workaround stopped — proving it produced the correct, safe C→B route rather
        // than truncating at 33 or blowing through a runway.
        Assert.Contains(
            route.HoldShortPoints,
            h =>
                (h.Reason == HoldShortReason.RunwayCrossing)
                && (h.TargetName is not null)
                && h.TargetName.Contains("28R", StringComparison.OrdinalIgnoreCase)
                && !h.IsCleared
        );
    }
}
