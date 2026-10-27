using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-1262: at SFO an arrival that exits 28R onto Q must pull up to Q's 28L hold-short (as a K exit does) and hold
/// there with a stored route, so a bare <c>CROSS</c> or <c>CROSS 28L</c> clears it across.
///
/// Recording fixture: S1-SFO-3 (A), Ground Control 28RT, trimmed to t=0/1100/1130. UAL292 is on its 28R rollout at
/// t=1100 and was told <c>EXIT Q</c> at t=1117. Live, it stopped past Q's 28R bar in HoldingAfterExit with no taxi
/// route, and <c>CROSS 28L</c> answered "No taxi route assigned".
///
/// Q's walk from its 28L bar (#214) across 28L to the far bar (#850) is 11 edges, 617 ft: Q's GeoJSON line carries
/// dense vertices across the runway. The K crossing that does pull up is 4 edges.
/// </summary>
public sealed class SfoExitQParallelPullUpTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/yaat1262-sfo-28r-exit-q-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "UAL292";
    private const int RestoreSecond = 1100;
    private const int ExitCommandSecond = 1117;
    private const int MaxSecondsToHold = 180;

    [Fact]
    public void ExitQOff28R_PullsUpTo28LHoldShort_AndCrossIsAccepted()
    {
        TestVnasData.EnsureInitialized();
        Assert.NotNull(TestVnasData.NavigationDb);

        using RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        Assert.NotNull(archive);
        TimedSnapshot? snapshot = archive.ReadSnapshotAt(RestoreSecond);
        Assert.NotNull(snapshot);

        var groundData = RecordingGroundData.VersionPinned(RecordingPath, "the UAL292 exit onto Q off 28R and Q's bars as recorded");
        Assert.True(groundData.Bundles("sfo"), "the recording bundles no SFO layout");

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("RunwayExitPhase", LogLevel.Debug)
            .EnableCategory("AirportGroundLayout", LogLevel.Debug)
            .InitializeSimLog();

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.Replay(archive.ToBaseSessionRecording(), 0);
        engine.RestoreFromSnapshot(snapshot.State);
        Assert.NotNull(engine.Scenario);
        engine.Scenario.CommandRunDelayMinSeconds = 0;
        engine.Scenario.CommandRunDelayMaxSeconds = 0;

        for (int t = RestoreSecond; t < ExitCommandSecond; t++)
        {
            engine.TickOneSecond();
        }

        CommandResult exit = engine.SendCommand(Callsign, "EXIT Q");
        Assert.True(exit.Success, $"EXIT Q: {exit.Message}");

        HoldingShortPhase? hold = null;
        for (int t = 0; (t < MaxSecondsToHold) && (hold is null); t++)
        {
            engine.TickOneSecond();
            AircraftState? ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            hold = ac.Phases?.CurrentPhase as HoldingShortPhase;
            if (ac.Phases?.CurrentPhase is HoldingAfterExitPhase)
            {
                output.WriteLine(
                    $"t+{t}: HoldingAfterExit at ({ac.Position.Lat:F6},{ac.Position.Lon:F6}) route={ac.Ground.AssignedTaxiRoute is not null}"
                );
                break;
            }
        }

        Assert.True(hold is not null, $"{Callsign} should pull up to Q's 28L hold-short after exiting 28R at Q");
        Assert.True(RunwayIdentifier.Parse(hold.HoldShort.TargetName!).Contains("28L"), $"held short of {hold.HoldShort.TargetName}, not 28L");

        CommandResult cross = engine.SendCommand(Callsign, "CROSS 28L");
        Assert.True(cross.Success, $"CROSS 28L at Q's 28L bar: {cross.Message}");
    }
}
