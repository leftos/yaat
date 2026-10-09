using Avalonia.Threading;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
#if HAS_YAAT_SERVER
using Microsoft.Extensions.DependencyInjection;
using Yaat.Server.Simulation;
using Yaat.Sim.Simulation;
#endif

namespace Yaat.GuideCapture.Capture;

// Runs the scene's room forward by an exact number of sim-seconds while it
// stays paused, so a capture shows the same traffic picture every run. Running
// the room unpaused for a stretch of real time does not: how many seconds the
// hosted tick loop gets through depends on the machine.
//
// Each step advances the room through RoomEngine.AdvanceLiveSecond (the call
// the hosted loop makes per sim-second) under the room's tick gate. The hosted
// loop still runs a pass every second for a paused room (detect changes, then
// send AircraftUpdated), so the step then waits for two passes to start: the
// first sees the new state, and the second only starts once the first has sent
// its updates. A sim-state broadcast sent after that reaches the client behind
// the updates, so when the client shows the new elapsed time it holds the
// step's aircraft state too. Stepping in chunks lets the radar build its
// position history as it would in a live run.
internal static class RoomTicks
{
    // One state a scene runs the paused room towards: what the aircraft must
    // do (for the failure message), the seconds per step, and the seconds
    // after which the scene gives up.
    public sealed record Stage(string What, int StepSeconds, int MaxSeconds);

    // Runs the paused room forward in the stage's steps until the aircraft
    // meets done, so the picture is the same every run, and returns it as it
    // then stands. Throws with the aircraft's state once MaxSeconds pass.
    public static async Task<AircraftModel> AdvanceUntilAsync(
        MainViewModel vm,
        CaptureContext ctx,
        string callsign,
        Func<AircraftModel, bool> done,
        Stage stage
    )
    {
        int elapsed = 0;
        AircraftModel aircraft = SceneActions.Find(vm, callsign);
        while (!done(aircraft))
        {
            if (elapsed >= stage.MaxSeconds)
            {
                throw new InvalidOperationException(
                    $"{callsign} failed {stage.What} within {stage.MaxSeconds} s: "
                        + $"{aircraft.CurrentPhase}, {aircraft.Altitude:0} ft, {aircraft.GroundSpeed:0} kt."
                );
            }
            await AdvancePausedAsync(vm, ctx, seconds: stage.StepSeconds, secondsPerStep: stage.StepSeconds);
            elapsed += stage.StepSeconds;
            aircraft = SceneActions.Find(vm, callsign);
        }

        Console.WriteLine($"  {callsign} {stage.What} after {elapsed} s: {aircraft.CurrentPhase}, {aircraft.GroundSpeed:0} kt");
        return aircraft;
    }

    public static async Task AdvancePausedAsync(MainViewModel vm, CaptureContext ctx, int seconds, int secondsPerStep)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(secondsPerStep);

        if (!vm.IsPaused)
        {
            await vm.TogglePauseCommand.ExecuteAsync(null);
            await SceneActions.WaitUntilAsync(() => vm.IsPaused, TimeSpan.FromSeconds(5), "sim to pause");
        }

#if HAS_YAAT_SERVER
        string roomId = vm.ActiveRoomId ?? throw new InvalidOperationException("The client is in no room.");
        TrainingRoom room =
            ctx.ServerServices.GetRequiredService<TrainingRoomManager>().GetRoom(roomId)
            ?? throw new InvalidOperationException($"The server has no room '{roomId}'.");
        RoomEngine engine = room.Engine ?? throw new InvalidOperationException($"Room '{roomId}' has no engine.");
        ITrainingBroadcast broadcast = ctx.ServerServices.GetRequiredService<ITrainingBroadcast>();

        for (int done = 0; done < seconds; done += secondsPerStep)
        {
            int step = Math.Min(secondsPerStep, seconds - done);
            await room.EnterTickGateAsync();
            try
            {
                for (int s = 0; s < step; s++)
                {
                    engine.AdvanceLiveSecond();
                }
            }
            finally
            {
                room.ExitTickGate();
            }

            await WaitForLoopPassAsync(room);
            await WaitForLoopPassAsync(room);

            broadcast.BroadcastSimState(room);
            SimScenarioState scenario = room.ActiveScenario ?? throw new InvalidOperationException($"Room '{roomId}' has no active scenario.");
            double target = scenario.ElapsedSeconds;
            await SceneActions.WaitUntilAsync(
                () => vm.ScenarioElapsedSeconds >= target,
                TimeSpan.FromSeconds(10),
                $"the client to show sim time {target:0}s"
            );
        }
        Dispatcher.UIThread.RunJobs();
#else
        throw new InvalidOperationException("Advancing a room needs the in-process yaat-server.");
#endif
    }

#if HAS_YAAT_SERVER
    // Every pass of the hosted loop starts by stamping a paused room's
    // PausedSinceUtc if it is unset (TrainingRoom.UpdatePausedSince), before
    // its change detection and broadcasts. Clearing the stamp and waiting for
    // it to come back waits for the next pass to start. The stamp only drives
    // the retirement of rooms left paused for an hour, which a capture never
    // reaches.
    private static async Task WaitForLoopPassAsync(TrainingRoom room)
    {
        room.PausedSinceUtc = null;
        await SceneActions.WaitUntilAsync(
            () => room.PausedSinceUtc is not null,
            TimeSpan.FromSeconds(10),
            $"the hosted tick loop to run a pass for room '{room.RoomId}'"
        );
    }
#endif
}
