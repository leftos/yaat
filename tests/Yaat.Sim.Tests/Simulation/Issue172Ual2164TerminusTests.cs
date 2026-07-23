using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #172 sub-bug #9 (UAL2164 wrong way): in the SFO recording, <c>TAXI G B</c> echoed cleanly
/// but the aircraft turned the wrong way on B — the controller had to HOLD it and WARP it back.
/// A bare final taxiway with no downstream direction must stop at the pure G/B intersection so the
/// controller can then turn it either way on B with a follow-up taxi (the user-specified behavior).
///
/// Recording: S1-SFO-4 (ZOA). UAL2164 exits 19L onto G; <c>TAXI G B</c> is issued at t=2141.
/// </summary>
public class Issue172Ual2164TerminusTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue172-sfo-taxiing-recording.yaat-bug-report-bundle.zip";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void TaxiGB_StopsAtGbIntersection_ThenTurnsOntoB()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        var gbJunction = layout.FindIntersectionNode("G", "B");
        Assert.NotNull(gbJunction);

        // Pin the recorded G exit right after LandingPhase becomes current (t=2085): with the
        // widened rapid-exit fillets, SFO's H is now a ~30 kt high-speed exit that legitimately
        // wins the default choice off 19L — but this test's subject is the TAXI G B terminus
        // rule, which needs the recording's on-G premise. Then replay to just before TAXI G B
        // (t=2141) and issue it with the current pathfinder.
        engine.Replay(recording, 2090);
        var pin = engine.SendCommand("UAL2164", "ER G");
        Assert.True(pin.Success, pin.Message);
        for (int t = 2091; t <= 2140; t++)
        {
            engine.ReplayOneSecond();
        }

        var aircraft = engine.FindAircraft("UAL2164");
        Assert.NotNull(aircraft);
        Assert.Equal("G", aircraft.Ground.CurrentTaxiway);

        var result = engine.SendCommand("UAL2164", "TAXI G B");
        output.WriteLine($"TAXI G B: success={result.Success} msg={result.Message}");
        Assert.True(result.Success, result.Message);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        // Stops at the G/B intersection and does not commit a direction along B.
        Assert.Equal(gbJunction.Id, route.Segments[^1].ToNodeId);
        Assert.DoesNotContain(route.Segments, s => s.TaxiwayName.Equals("B", StringComparison.OrdinalIgnoreCase));

        // A follow-up taxi off the junction turns it onto B (the recorded controller's recovery).
        var followUp = engine.SendCommand("UAL2164", "TAXI B Q A @F11");
        output.WriteLine($"TAXI B Q A @F11: success={followUp.Success} msg={followUp.Message}");
        Assert.True(followUp.Success, followUp.Message);

        var route2 = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route2);
        Assert.Contains(route2.Segments, s => s.TaxiwayName.Equals("B", StringComparison.OrdinalIgnoreCase));
    }
}
