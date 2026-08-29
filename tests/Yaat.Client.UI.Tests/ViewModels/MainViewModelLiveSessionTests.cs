using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The live-session badge and "Go Live" affordance are pure projections of <c>IsLiveSession</c>, <c>IsPaused</c>,
/// <c>IsPlaybackMode</c> and the feed status; these pin the projection. Like the Take Control tests, a
/// unit-constructed VM has no connection, so the server call inside GoLive throws and the status text is the observable.
/// </summary>
public class MainViewModelLiveSessionTests
{
    [Theory]
    [InlineData(false, false, false, true, "")]
    [InlineData(true, false, false, true, "LIVE")]
    [InlineData(true, false, false, false, "LIVE · feed lost")]
    [InlineData(true, true, false, true, "PAUSED")]
    [InlineData(true, true, true, true, "PLAYBACK")]
    [InlineData(true, false, true, false, "PLAYBACK")]
    public void DescribeLiveSession_ProjectsTheRoomState(bool live, bool paused, bool playback, bool connected, string expected)
    {
        Assert.Equal(expected, MainViewModel.DescribeLiveSession(live, paused, playback, connected));
    }

    [AvaloniaFact]
    public void Badge_ReflectsPauseAndPlayback_AndHidesTheScenarioPlaybackChrome()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.IsLiveSession = true;
        vm.LiveTrafficStatus = new LiveTrafficStatusDto(true, true, 2, 5);

        Assert.Equal("LIVE", vm.LiveSessionBadgeText);
        Assert.Equal(MainViewModel.LiveBadgeColor, vm.LiveSessionBadgeBrush);
        Assert.False(vm.ShowGoLive);

        vm.IsPaused = true;
        Assert.Equal("PAUSED", vm.LiveSessionBadgeText);
        Assert.Equal(MainViewModel.PausedBadgeColor, vm.LiveSessionBadgeBrush);
        Assert.True(vm.ShowGoLive);

        vm.IsPlaybackMode = true;
        Assert.Equal("PLAYBACK", vm.LiveSessionBadgeText);
        Assert.Equal(MainViewModel.PlaybackBadgeColor, vm.LiveSessionBadgeBrush);
        Assert.True(vm.ShowGoLive);
        Assert.False(vm.ShowPlaybackBadge);
        Assert.False(vm.ShowTakeControl);
    }

    [AvaloniaFact]
    public void ScenarioRoom_KeepsThePlaybackBadgeAndTakeControl()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.IsPlaybackMode = true;

        Assert.False(vm.IsLiveSession);
        Assert.True(vm.ShowPlaybackBadge);
        Assert.True(vm.ShowTakeControl);
        Assert.False(vm.ShowGoLive);
        Assert.Equal("", vm.LiveSessionBadgeText);
    }

    [AvaloniaFact]
    public void FeedLoss_ShowsInTheBadge_WhileRunning()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.IsLiveSession = true;
        vm.LiveTrafficStatus = new LiveTrafficStatusDto(true, false, null, 0);

        Assert.Equal("LIVE · feed lost", vm.LiveSessionBadgeText);
        Assert.Equal(MainViewModel.FeedLostBadgeColor, vm.LiveSessionBadgeBrush);
    }

    [AvaloniaFact]
    public void CanStartLiveSession_RequiresAConfiguredFeed()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        Assert.False(vm.CanStartLiveSession);
        Assert.Contains("not enabled", vm.StartLiveSessionToolTip);

        vm.LiveTrafficStatus = new LiveTrafficStatusDto(true, true, null, 0);
        Assert.DoesNotContain("not enabled", vm.StartLiveSessionToolTip);
        // Still not in a room, so the gate that CanLoadScenario carries keeps it off.
        Assert.False(vm.CanStartLiveSession);
    }

    [AvaloniaFact]
    public async Task GoLive_WithoutAConnection_ReportsTheError_AndLeavesPlaybackAlone()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.IsLiveSession = true;
        vm.IsPlaybackMode = true;
        vm.PlaybackTapeEnd = 90;
        vm.StatusText = "sentinel";

        await vm.GoLiveCommand.ExecuteAsync(null);

        Assert.StartsWith("Go live error", vm.StatusText);
        Assert.True(vm.IsPlaybackMode);
        Assert.Equal(90, vm.PlaybackTapeEnd);
    }
}
