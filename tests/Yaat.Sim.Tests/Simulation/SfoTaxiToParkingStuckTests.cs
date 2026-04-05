using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the TAXI @parking stuck-state bug.
///
/// Bug: ASA20 was given PUSH then TAXI @D7 to return to parking. After the taxi
/// completed, the aircraft entered a dead state where all subsequent commands
/// (PUSH, PUSH A) returned "Unknown command".
///
/// Root cause: Two issues — (1) when the aircraft is already at the destination node,
/// A* returns a 0-segment route and TaxiingPhase completes instantly without inserting
/// a successor phase, leaving CurrentPhase null; (2) even with a normal route,
/// ArriveAtNode inserts HoldingInPositionPhase instead of AtParkingPhase.
///
/// Recording: S1-SFO-2 | Ground Control 28/01.
/// Timeline: t=1212 ASA20 PUSH, t=1305 ASA20 TAXI @D7.
/// </summary>
[Collection("NavDbMutator")]
public class SfoTaxiToParkingStuckTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/09304e0c727e.zip";

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
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// After PUSH then TAXI @D7, the aircraft should end up in AtParkingPhase
    /// and accept a subsequent PUSH command.
    /// </summary>
    [Fact]
    public void ASA20_TaxiToParking_EndsInAtParkingPhase_AcceptsPush()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=1305 — this applies PUSH at t=1212 and TAXI @D7 at t=1305
        engine.Replay(recording, 1305);

        var ac = engine.FindAircraft("ASA20");
        Assert.NotNull(ac);

        output.WriteLine(
            $"ASA20 after TAXI @D7: phase={ac.Phases?.CurrentPhase?.Name ?? "null"} "
                + $"pos=({ac.Latitude:F6},{ac.Longitude:F6}) gs={ac.GroundSpeed:F1}"
        );

        // Tick until the taxi completes (aircraft stops moving) or 120s elapse
        for (int t = 1; t <= 120; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("ASA20");
            if (ac is null)
            {
                break;
            }

            if (t % 10 == 0)
            {
                output.WriteLine(
                    $"  t+{t}: phase={ac.Phases?.CurrentPhase?.Name ?? "null"} " + $"gs={ac.GroundSpeed:F1} parking={ac.ParkingSpot ?? "null"}"
                );
            }

            // If the aircraft is already at parking, stop ticking
            if (ac.Phases?.CurrentPhase is AtParkingPhase)
            {
                output.WriteLine($"  t+{t}: reached AtParkingPhase");
                break;
            }
        }

        ac = engine.FindAircraft("ASA20");
        Assert.NotNull(ac);

        // Assert: aircraft should be in AtParkingPhase after taxi to parking completes
        Assert.NotNull(ac.Phases);
        Assert.IsType<AtParkingPhase>(ac.Phases.CurrentPhase);

        // Assert: PUSH should now be accepted
        var pushResult = engine.SendCommand("ASA20", "PUSH");
        output.WriteLine($"PUSH result: success={pushResult.Success} msg={pushResult.Message}");
        Assert.True(pushResult.Success, $"PUSH should succeed but got: {pushResult.Message}");
    }

    /// <summary>
    /// After a simple PUSH, a second PUSH should be accepted from
    /// HoldingAfterPushbackPhase without needing to taxi back first.
    /// </summary>
    [Fact]
    public void ASA20_RePushAfterPushback_Accepted()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=1212 (PUSH command), then tick until pushback completes
        engine.Replay(recording, 1212);

        var ac = engine.FindAircraft("ASA20");
        Assert.NotNull(ac);

        for (int t = 1; t <= 60; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("ASA20");
            if (ac is null)
            {
                break;
            }

            if (ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase)
            {
                output.WriteLine($"  t+{t}: reached HoldingAfterPushbackPhase");
                break;
            }
        }

        ac = engine.FindAircraft("ASA20");
        Assert.NotNull(ac);
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);

        // Assert: a second PUSH should be accepted
        var pushResult = engine.SendCommand("ASA20", "PUSH");
        output.WriteLine($"Re-PUSH result: success={pushResult.Success} msg={pushResult.Message}");
        Assert.True(pushResult.Success, $"PUSH from HoldingAfterPushbackPhase should succeed but got: {pushResult.Message}");
    }
}
