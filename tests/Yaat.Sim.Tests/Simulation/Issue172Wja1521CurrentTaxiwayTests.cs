using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #172 sub-bug #7 (WJA1521 "M4 unreachable"): WJA1521 pushed back onto taxiway M4, then
/// <c>TAXI M4 M2 $2</c> was rejected — "Cannot taxi via M4 from the aircraft's position — it is
/// unreachable without crossing a runway or leaving the movement area." — even though the aircraft
/// is ON M4. Re-issuing <c>TAXI M2 $2</c> (omitting the current taxiway) worked (`M5 M2 $2`). Naming
/// the taxiway the aircraft is already on as the first cleared taxiway must not make it unreachable.
///
/// Recording: S1-SFO-4 (ZOA). PUSH M4 at t=1903; TAXI M4 M2 $2 issued shortly after.
/// </summary>
public class Issue172Wja1521CurrentTaxiwayTests(ITestOutputHelper output)
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
    public void Diagnostic_StateAtPushTime()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("SFO");
        engine.Replay(recording, 1945);
        var ac = engine.FindAircraft("WJA1521");
        if (ac is null)
        {
            output.WriteLine("WJA1521 not found");
            return;
        }

        output.WriteLine(
            $"WJA1521 @1945: twy={ac.Ground.CurrentTaxiway} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees:F0}"
        );
        if (layout is not null)
        {
            NearestNodeHelper.Log(output, "  WJA1521:", ac, layout);
        }
    }

    [Fact]
    public void TaxiM4M2_FromM4_Succeeds()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 1945);
        var aircraft = engine.FindAircraft("WJA1521");
        Assert.NotNull(aircraft);

        // Precondition: WJA1521 pushed back onto M4 (it sits on an M4 node, post-pushback, so
        // CurrentTaxiway is not yet latched). The clearance names M4 as the first cleared taxiway.
        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        var nearest = layout.FindNearestNode(aircraft.Position.Lat, aircraft.Position.Lon);
        Assert.NotNull(nearest);
        Assert.Contains(nearest.Edges, e => e.MatchesTaxiway("M4"));

        var result = engine.SendCommand("WJA1521", "TAXI M4 M2 $2");
        output.WriteLine($"TAXI M4 M2 $2: success={result.Success} msg={result.Message}");

        Assert.True(result.Success, $"TAXI M4 M2 $2 should succeed from M4 but failed: {result.Message}");
        Assert.DoesNotContain("unreachable", result.Message, StringComparison.OrdinalIgnoreCase);

        // The aircraft must actually taxi the route — not stall or orbit. Tick forward and confirm it
        // covers a meaningful distance, never sits still while taxiing, and reaches its spot.
        var start = aircraft.Position;
        int taxiingSeconds = 0;
        int stalledSeconds = 0;
        bool arrived = false;
        var prev = start;
        for (int t = 0; t < 120; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft("WJA1521");
            Assert.NotNull(ac);

            // Count stationary ticks only while the aircraft is still taxiing. It reaches spot 2 partway
            // through the window and parks, so a plain moving/total ratio would score arrival as a stall.
            if (ac.Phases?.CurrentPhase is TaxiingPhase)
            {
                taxiingSeconds++;
                if (GeoMath.DistanceNm(prev, ac.Position) * 6076.0 <= 1.0)
                {
                    stalledSeconds++;
                }
            }
            else if (taxiingSeconds > 0)
            {
                arrived = true;
            }

            prev = ac.Position;
        }

        var end = engine.FindAircraft("WJA1521")!.Position;
        double traveledFt = GeoMath.DistanceNm(start, end) * 6076.0;
        output.WriteLine($"traveled={traveledFt:F0}ft taxiing={taxiingSeconds}s stalled={stalledSeconds}s arrived={arrived}");
        Assert.True(traveledFt > 200.0, $"WJA1521 should make progress along the route, only moved {traveledFt:F0}ft (possible spin/stall)");
        Assert.True(stalledSeconds <= 5, $"WJA1521 sat still for {stalledSeconds}s of {taxiingSeconds}s taxiing (possible spin/stall/deadlock)");
        Assert.True(arrived, "WJA1521 should reach its destination spot within the window, not still be taxiing");
    }
}
