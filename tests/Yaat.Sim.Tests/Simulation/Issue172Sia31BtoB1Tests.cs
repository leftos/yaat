using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #172 sub-case (SIA31 "transition infeasible B-&gt;B1"): SIA31 taxied <c>B B5 $10</c> to spot
/// $10 and held there — sitting on the B5 connector just NE of the B/B5 junction (node 117), heading
/// ~74°. When the controller then issued <c>TAXI B B1 Z S S3 10R</c> to send it back out to the
/// runway, it was rejected with "No valid path from B to B1 — transition infeasible from node 117",
/// even though B and B1 connect at a proper junction (node 131) a short distance NE along B (the
/// junction carries admissible B-B1 corner arcs). The controller's workaround
/// <c>TAXI A Q B1 Z S S3 10R</c> routed fine.
///
/// Naming B (the taxiway adjacent to the aircraft's B5 connector) as the first cleared taxiway must
/// anchor the start so the onward B-&gt;B1 transition stays feasible — the same start-anchoring family
/// as WJA1521 sub-bug #7 (<see cref="Issue172Wja1521CurrentTaxiwayTests"/>).
///
/// Recording: issue172-sfo-taxiing-recording (ZOA/SFO). SIA31 holds at spot $10 ~t=1200; the
/// workaround was issued at t=1233.
/// </summary>
public class Issue172Sia31BtoB1Tests(ITestOutputHelper output)
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

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .EnableCategory("TaxiPathfinder", LogLevel.Debug)
            .InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Diagnostic_StateBeforeReroute()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("SFO");
        engine.Replay(recording, 1230);
        var ac = engine.FindAircraft("SIA31");
        if (ac is null)
        {
            output.WriteLine("SIA31 not found");
            return;
        }

        output.WriteLine(
            $"SIA31 @1230: twy={ac.Ground.CurrentTaxiway} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees:F0}"
        );
        if (layout is not null)
        {
            NearestNodeHelper.Log(output, "  SIA31:", ac, layout);
        }
    }

    [Fact]
    public void TaxiBB1_FromB5SpotHold_Succeeds()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 1230);
        var aircraft = engine.FindAircraft("SIA31");
        Assert.NotNull(aircraft);

        var result = engine.SendCommand("SIA31", "TAXI B B1 Z S S3 10R");
        output.WriteLine($"TAXI B B1 Z S S3 10R: success={result.Success} msg={result.Message}");

        Assert.True(result.Success, $"TAXI B B1 Z S S3 10R should resolve but failed: {result.Message}");
        Assert.DoesNotContain("infeasible", result.Message ?? "", StringComparison.OrdinalIgnoreCase);

        // The aircraft must actually taxi the route — not stall or spin. Tick forward and confirm it
        // makes meaningful progress.
        var start = aircraft.Position;
        for (int t = 0; t < 120; t++)
        {
            engine.TickOneSecond();
            Assert.NotNull(engine.FindAircraft("SIA31"));
        }

        var end = engine.FindAircraft("SIA31")!.Position;
        double traveledFt = GeoMath.DistanceNm(start, end) * 6076.0;
        output.WriteLine($"traveled={traveledFt:F0}ft");
        Assert.True(traveledFt > 200.0, $"SIA31 should make progress along the route, only moved {traveledFt:F0}ft (possible spin/stall)");
    }
}
