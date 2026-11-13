using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The main window's rewind overlay: once the view model shows it, the window shows the line naming the target and who
/// rewinds, and the bar at the re-run's progress; while the jump runs the timeline's commands and track are disabled.
/// The overlay's show delay is replaced by one that has already elapsed, so nothing waits on the clock.
/// </summary>
public class RewindOverlayViewTests
{
    [AvaloniaFact]
    public void AnotherMembersJump_ShowsTheOverlayWithItsTextAndProgress_AndDisablesTheTimeline()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            vm.RewindOverlay.Delay = _ => Task.CompletedTask;
            TextBlock title = main.FindControl<TextBlock>("RewindOverlayTitle")!;
            ProgressBar bar = main.FindControl<ProgressBar>("RewindOverlayProgress")!;
            Grid track = main.FindControl<Grid>("TimelineTrack")!;
            Assert.False(title.IsEffectivelyVisible);

            vm.RewindOverlay.BeginJump(1, 2530, "JD", false);
            vm.RewindOverlay.ApplyProgress(1, 30, 75);
            MainWindowHost.Pump();

            Assert.True(title.IsEffectivelyVisible);
            Assert.Equal("Rewinding to 42:10 — by JD", title.Text);
            Assert.False(bar.IsIndeterminate);
            Assert.Equal(0.4, bar.Value, 6);
            Assert.False(vm.CanScrubTimeline);
            Assert.False(track.IsEnabled);
            Assert.False(vm.RewindToStartCommand.CanExecute(null));
            Assert.False(vm.JumpToEndCommand.CanExecute(null));

            vm.RewindOverlay.ApplyProgress(1, 75, 75);
            MainWindowHost.Pump();

            Assert.False(title.IsEffectivelyVisible);
            Assert.True(track.IsEnabled);
            Assert.True(vm.RewindToStartCommand.CanExecute(null));
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    /// <summary>
    /// A's <c>ScenarioRewound</c> can reach this client after B's start (A's result is sent once A left the room's gate);
    /// it must not end B's jump, which keeps the timeline locked until B's own final notice.
    /// </summary>
    [AvaloniaFact]
    public async Task AnEarlierJumpsScenarioRewound_DoesNotUnlockTheTimelineWhileTheNextJumpRuns()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            vm.RewindOverlay.Delay = _ => Task.CompletedTask;
            vm.RewindOverlay.BeginJump(1, 100, "AA", false);
            vm.RewindOverlay.ApplyProgress(1, 40, 40);
            vm.RewindOverlay.BeginJump(2, 200, "BB", false);
            vm.OnScenarioRewound([]);
            MainWindowHost.Pump();

            Assert.False(vm.CanScrubTimeline);
            Assert.False(vm.RewindBack15Command.CanExecute(null));
            RewindOutcome refused = await vm.RewindToSeconds(50);
            Assert.False(refused.Rewound);
            Assert.Equal("Rewind unavailable while BB's jump runs", vm.StatusText);

            vm.RewindOverlay.ApplyProgress(2, 70, 70);
            MainWindowHost.Pump();
            Assert.True(vm.CanScrubTimeline);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }
}
