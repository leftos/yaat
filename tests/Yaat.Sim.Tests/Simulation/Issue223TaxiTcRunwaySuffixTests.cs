using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E regression for GitHub issue #223: at OAK, taxiing SWA9701 into the ramp with
/// <c>TAXI W B T TC @10</c> was rejected with
/// "Taxiway T does not reach runway TC — specify a connecting taxiway."
///
/// <c>TC</c> is a real OAK ramp-connector taxiway (the auto-pathfinder itself routes
/// <c>W5 W B T TC RAMP @10</c> for the bare <c>TAXI W B T @10</c>). The rejection was a
/// parser bug: <see cref="Yaat.Sim.Commands.CommandParser.IsRunwayArg"/> classified any
/// 2+ character token ending in L/C/R as a runway, so the trailing-runway detector peeled
/// <c>TC</c> off the path as a phantom destination runway. The explicit route through TC then
/// had no runway to reach and failed.
///
/// Recording: S1-OAK-P (A) S1 Rating Practical Exam — SWA9701 (B738 arrival) is stopped on
/// taxiway W5, on the ground, ready to taxi to parking spot 10 at t≈375.
/// </summary>
public class Issue223TaxiTcRunwaySuffixTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue223-oak-t-tc-recording.zip";
    private const string Callsign = "SWA9701";

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
    public void TaxiViaTaxiwayEndingInC_IntoRamp_IsAccepted()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=375: SWA9701 has landed and is stopped on taxiway W5, on the
        // ground, with no assigned route — just before the controller's recorded taxi
        // clearance to the ramp (sim t=380).
        engine.Replay(recording, 375);
        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.True(ac.IsOnGround, "SWA9701 should be on the ground (stopped on W5) before the TAXI command");

        var result = engine.SendCommand(Callsign, "TAXI W B T TC @10");

        var route = ac.Ground.AssignedTaxiRoute;
        if (route is not null)
        {
            output.WriteLine($"Route: {route.ToSummary()} ({route.Segments.Count} segments)");
        }

        // The command must be accepted — this is the bug (was rejected
        // "Taxiway T does not reach runway TC — specify a connecting taxiway.").
        Assert.True(result.Success, result.Message);
        Assert.NotNull(route);

        // TC must be honored as a taxiway in the resolved route, not dropped as a phantom runway.
        Assert.Contains(route.Segments, s => s.TaxiwayName == "TC");
    }
}
