using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// Scenes that show the timeline bar (or need it on, as the terminal's Rewind
// item does). The bar is off by default and View > Show Timeline Bar saves the
// choice to preferences, which every later MainWindow reads, so the scene turns
// it back to what it was once the capture is taken.
//
// StagePlaybackAsync builds the playback state the timeline shots share: the
// default OAK scenario run forward a paused 2:00 with bookmarks placed at 0:30
// and 1:30 along the way, then rewound to 1:00, which puts the room in
// playback with a 2:00 tape.
internal abstract class TimelineSceneBase : ScenarioSceneBase
{
    private const double RewindTargetSeconds = 60;

    private MainViewModel? _timelineVm;
    private bool _priorShowTimelineBar;

    protected void ShowTimelineBar(MainViewModel vm)
    {
        _timelineVm = vm;
        _priorShowTimelineBar = vm.ShowTimelineBar;
        vm.ShowTimelineBar = true;
        Dispatcher.UIThread.RunJobs();
    }

    public override void AfterCapture()
    {
        _timelineVm?.ShowTimelineBar = _priorShowTimelineBar;
        _timelineVm = null;
    }

    protected async Task StagePlaybackAsync(MainViewModel vm, CaptureContext ctx)
    {
        ShowTimelineBar(vm);

        await RoomTicks.AdvancePausedAsync(vm, ctx, seconds: 30, secondsPerStep: 15);
        await QuickAddBookmarkAsync(vm, expectedCount: 1);
        await RoomTicks.AdvancePausedAsync(vm, ctx, seconds: 60, secondsPerStep: 15);
        await QuickAddBookmarkAsync(vm, expectedCount: 2);
        await RoomTicks.AdvancePausedAsync(vm, ctx, seconds: 30, secondsPerStep: 15);

        RewindOutcome outcome = await vm.RewindToSeconds(RewindTargetSeconds);
        if (!outcome.Rewound)
        {
            throw new InvalidOperationException($"The rewind to {RewindTargetSeconds:0}s failed: {outcome.Status}");
        }
        await SceneActions.WaitUntilAsync(() => vm.IsPlaybackMode, TimeSpan.FromSeconds(10), "the room to enter playback");
        await WaitForStableAircraftCountAsync(vm);

        // The window polls the session report for timeline markers every 5 s.
        // Refreshing them here makes the capture show the room's markers
        // whether or not a poll has landed yet; a later poll rebuilds the same
        // set from the same paused room.
        await vm.RefreshTimelineMarkersAsync();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task QuickAddBookmarkAsync(MainViewModel vm, int expectedCount)
    {
        await vm.QuickAddBookmarkCommand.ExecuteAsync(null);
        await SceneActions.WaitUntilAsync(
            () => vm.Bookmarks.Count == expectedCount,
            TimeSpan.FromSeconds(10),
            $"bookmark {expectedCount} to reach the client"
        );
    }

    // A rewind clears the aircraft list and refills it from the rewind result,
    // and the broadcasts that follow can still add or drop aircraft, so the
    // count must hold still for a second before the capture.
    private static async Task WaitForStableAircraftCountAsync(MainViewModel vm)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        int lastCount = -1;
        DateTime stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            int count = vm.Aircraft.Count;
            if (count != lastCount)
            {
                lastCount = count;
                stableSince = DateTime.UtcNow;
            }
            else if ((count > 0) && (DateTime.UtcNow - stableSince >= TimeSpan.FromSeconds(1)))
            {
                return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"Timeout waiting for the aircraft count to settle after the rewind (last count {lastCount}).");
    }
}
