using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Exhaustive E2E test for OAK ground operations using scenario S2-OAK-4.
///
/// Tests two bugs:
/// 1. N346G and N172SP overlap at the 28R hold-short — the ground conflict detector
///    must keep them separated when queueing behind N436MS.
/// 2. N569SX stops well short of SIG1 parking after TAXI G @SIG1 — the braking curve
///    must target the node itself, not the arrival threshold.
///
/// Scenario: 01HG3N8Q5PPR7QXZK33ZPC4D5M (S2-OAK-4 VFR Transitions/Radar Concepts).
/// Preset commands at t=0: N436MS TAXI B 28R, N172SP TAXI D C B 28R, N346G TAXI C B 28R.
/// Manual commands: N569SX CLAND (when on final), then TAXI G @SIG1 (after runway exit).
/// </summary>
public class OakGroundE2ETests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue134-oak-runway-exit-recording.json";

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

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void OAK_FullGroundSequence_NoOverlapAndSIG1Reached()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=1 — scenario loads, preset TAXI commands fire for N436MS/N172SP/N346G
        engine.Replay(recording, 1);

        // Find node 508 (28R hold-short on B) for distance reference
        var n436 = engine.FindAircraft("N436MS");
        Assert.NotNull(n436);
        double node508Lat = 0;
        double node508Lon = 0;
        if (n436.GroundLayout is not null && n436.GroundLayout.Nodes.TryGetValue(508, out var node508))
        {
            node508Lat = node508.Latitude;
            node508Lon = node508.Longitude;
        }

        // Find SIG1 parking node for distance reference
        double sig1Lat = 0;
        double sig1Lon = 0;
        if (n436.GroundLayout is not null)
        {
            var sig1Node = n436.GroundLayout.FindHelipadByName("SIG1") ?? n436.GroundLayout.FindParkingByName("SIG1");
            if (sig1Node is not null)
            {
                sig1Lat = sig1Node.Latitude;
                sig1Lon = sig1Node.Longitude;
            }
        }

        output.WriteLine($"Node 508 (28R HS): ({node508Lat:F6}, {node508Lon:F6})");
        output.WriteLine($"SIG1 parking:      ({sig1Lat:F6}, {sig1Lon:F6})");
        output.WriteLine("");

        // --- Phase 1: Send CLAND to N569SX ---
        var n569 = engine.FindAircraft("N569SX");
        Assert.NotNull(n569);
        var clandResult = engine.SendCommand("N569SX", "CLAND");
        Assert.True(clandResult.Success, $"CLAND failed: {clandResult.Message}");
        output.WriteLine($"t=1: N569SX CLAND — {clandResult.Message}");

        // --- Phase 2: Tick until N569SX exits runway (HoldingAfterExitPhase) ---
        bool n569ExitedRunway = false;
        int exitTime = 0;

        for (int t = 1; t <= 600; t++)
        {
            engine.TickOneSecond();
            n569 = engine.FindAircraft("N569SX");
            if (n569 is null)
            {
                break;
            }

            string phaseName = n569.Phases?.CurrentPhase?.Name ?? "(none)";

            if (phaseName == "Holding After Exit" && !n569ExitedRunway)
            {
                n569ExitedRunway = true;
                exitTime = t;
                output.WriteLine($"t={t}: N569SX exited runway at ({n569.Latitude:F6}, {n569.Longitude:F6}) on {n569.CurrentTaxiway}");

                // Send TAXI G @SIG1
                var taxiResult = engine.SendCommand("N569SX", "TAXI G @SIG1");
                Assert.True(taxiResult.Success, $"TAXI G @SIG1 failed: {taxiResult.Message}");
                output.WriteLine($"t={t}: N569SX TAXI G @SIG1 — {taxiResult.Message}");
            }

            // Log N569SX progress toward SIG1 after taxi command
            if (n569ExitedRunway && t % 10 == 0)
            {
                double distSig1Ft = GeoMath.DistanceNm(n569.Latitude, n569.Longitude, sig1Lat, sig1Lon) * GeoMath.FeetPerNm;
                output.WriteLine(
                    $"t={t}: N569SX {phaseName, -24} gs={n569.GroundSpeed:F1} distSIG1={distSig1Ft:F0}ft pos=({n569.Latitude:F6}, {n569.Longitude:F6})"
                );
            }

            // Check if N569SX route completed
            if (n569ExitedRunway && (phaseName == "Holding In Position" || phaseName == "At Parking"))
            {
                double distSig1Ft = GeoMath.DistanceNm(n569.Latitude, n569.Longitude, sig1Lat, sig1Lon) * GeoMath.FeetPerNm;
                output.WriteLine($"t={t}: N569SX stopped — {phaseName} at distSIG1={distSig1Ft:F0}ft");
                break;
            }
        }

        Assert.True(n569ExitedRunway, "N569SX never exited the runway");

        // --- Phase 3: Tick additional time for all aircraft to settle ---
        // N172SP has a long route (31 segments), give it time to arrive
        for (int t = 0; t < 300; t++)
        {
            engine.TickOneSecond();
        }

        // --- Assertions ---
        output.WriteLine("");
        output.WriteLine("=== Final state ===");

        // Assert 1: N569SX should be close to SIG1
        n569 = engine.FindAircraft("N569SX");
        Assert.NotNull(n569);
        double n569DistSig1Ft = GeoMath.DistanceNm(n569.Latitude, n569.Longitude, sig1Lat, sig1Lon) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"N569SX: distSIG1={n569DistSig1Ft:F0}ft pos=({n569.Latitude:F6}, {n569.Longitude:F6}) phase={n569.Phases?.CurrentPhase?.Name}"
        );
        Assert.True(n569DistSig1Ft < 30, $"N569SX stopped {n569DistSig1Ft:F0}ft from SIG1 — should be within 30ft");

        // Assert 2: N346G and N172SP should not be stacked
        var n172 = engine.FindAircraft("N172SP");
        var n346 = engine.FindAircraft("N346G");
        Assert.NotNull(n172);
        Assert.NotNull(n346);

        double n172Dist508Ft = GeoMath.DistanceNm(n172.Latitude, n172.Longitude, node508Lat, node508Lon) * GeoMath.FeetPerNm;
        double n346Dist508Ft = GeoMath.DistanceNm(n346.Latitude, n346.Longitude, node508Lat, node508Lon) * GeoMath.FeetPerNm;
        double separationFt = GeoMath.DistanceNm(n172.Latitude, n172.Longitude, n346.Latitude, n346.Longitude) * GeoMath.FeetPerNm;

        output.WriteLine(
            $"N436MS: dist508={GeoMath.DistanceNm(engine.FindAircraft("N436MS")!.Latitude, engine.FindAircraft("N436MS")!.Longitude, node508Lat, node508Lon) * GeoMath.FeetPerNm:F0}ft phase={engine.FindAircraft("N436MS")!.Phases?.CurrentPhase?.Name}"
        );
        output.WriteLine($"N346G:  dist508={n346Dist508Ft:F0}ft phase={n346.Phases?.CurrentPhase?.Name}");
        output.WriteLine($"N172SP: dist508={n172Dist508Ft:F0}ft phase={n172.Phases?.CurrentPhase?.Name}");
        output.WriteLine($"N346G <-> N172SP separation: {separationFt:F0}ft");

        Assert.True(separationFt > 5, $"N346G and N172SP are stacked ({separationFt:F0}ft apart)");
    }
}
