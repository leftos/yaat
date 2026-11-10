using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Simulation.RunwayExitPullUpAtSpeedTests;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <see cref="RunwayExitPullUpAtSpeedTests"/> on SFO 28R's exit Q, the pull-up with a corner before the 28L bar: UAL292 on its
/// rollout told <c>EXIT Q</c>, restored from the recording <see cref="SfoExitQParallelPullUpTests"/> uses, on the airport as
/// recorded.
/// </summary>
public sealed class RunwayExitPullUpAtSpeedQTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/yaat1262-sfo-28r-exit-q-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "UAL292";

    [Fact]
    public void PullUp_HandsOffAtSpeed_NoStopBeforeTheParallelBar() => AssertHandsOffAtSpeed(StageExitQ(), output);

    [Fact]
    public void PullUp_NeverExceedsTheHandOffSpeedBeforeTheParallelBar() => AssertNeverExceedsTheHandOffSpeed(StageExitQ(), output);

    [Fact]
    public void PullUp_StopsAtTheParallelBarWithinTheTaxiBrakingRate() => AssertStopsWithinTheTaxiBrakingRate(StageExitQ(), output);

    [Fact]
    public void PullUp_NeverTrailsThePullUpBrakingCurve() => AssertRidesThePullUpCurve(StageExitQ(), output);

    [Fact]
    public void PullUp_UnableParallelBar_GetsTheTaxiCurveSlackBeforeFirmBraking() =>
        AssertUnableParallelBarGetsTheTaxiCurveSlack(StageExitQ(), output);

    private Staged StageExitQ()
    {
        TestVnasData.EnsureInitialized();
        using RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        Assert.NotNull(archive);
        TimedSnapshot? snapshot = archive.ReadSnapshotAt(1100);
        Assert.NotNull(snapshot);
        var groundData = RecordingGroundData.VersionPinned(RecordingPath, "the UAL292 exit onto Q off 28R and Q's bars as recorded");
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.Replay(archive.ToBaseSessionRecording(), 0);
        engine.RestoreFromSnapshot(snapshot.State);
        Assert.NotNull(engine.Scenario);
        engine.Scenario.CommandRunDelayMinSeconds = 0;
        engine.Scenario.CommandRunDelayMaxSeconds = 0;
        engine.Scenario.AutoPullUpToParallel = true;
        for (int t = 1100; t < 1117; t++)
        {
            engine.TickOneSecond();
        }

        CommandResult exit = engine.SendCommand(Callsign, "EXIT Q");
        Assert.True(exit.Success, $"EXIT Q: {exit.Message}");
        return new Staged(engine, Callsign, "28L");
    }
}
