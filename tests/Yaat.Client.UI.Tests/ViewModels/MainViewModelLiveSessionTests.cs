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
    [InlineData(false, false, false, true, null, false, "")]
    [InlineData(true, false, false, true, null, false, "LIVE")]
    [InlineData(true, false, false, false, null, false, "LIVE · feed lost")]
    [InlineData(true, true, false, true, null, false, "PAUSED")]
    [InlineData(true, true, true, true, null, false, "PLAYBACK")]
    [InlineData(true, false, true, false, null, false, "PLAYBACK")]
    [InlineData(true, false, false, true, 125.0, false, "LIVE −02:05")]
    [InlineData(true, false, false, true, 3725.0, false, "LIVE −1:02:05")]
    [InlineData(true, false, false, false, 125.0, false, "LIVE −02:05 · feed lost")]
    [InlineData(true, false, false, true, 0.4, false, "LIVE")]
    [InlineData(true, false, false, true, 125.0, true, "PREPARING")]
    [InlineData(true, true, false, true, 125.0, true, "PAUSED")]
    public void DescribeLiveSession_ProjectsTheRoomState(
        bool live,
        bool paused,
        bool playback,
        bool connected,
        double? behind,
        bool preparing,
        string expected
    )
    {
        Assert.Equal(expected, MainViewModel.DescribeLiveSession(live, paused, playback, connected, behind, preparing));
    }

    [AvaloniaFact]
    public void BehindRealTime_OffersGoLive_WhileRunning()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.IsLiveSession = true;
        vm.LiveTrafficStatus = new LiveTrafficStatusDto(
            true,
            true,
            2,
            5,
            FeedTimeUtc: DateTimeOffset.UtcNow.AddMinutes(-2),
            BehindSeconds: 120,
            Preparing: false
        );

        Assert.True(vm.IsBehindRealTime);
        Assert.True(vm.ShowGoLive);
        Assert.Equal("LIVE −02:00", vm.LiveSessionBadgeText);

        vm.LiveTrafficStatus = new LiveTrafficStatusDto(true, true, 2, 5, FeedTimeUtc: DateTimeOffset.UtcNow, BehindSeconds: null, Preparing: false);
        Assert.False(vm.IsBehindRealTime);
        Assert.False(vm.ShowGoLive);
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("  ", null, null)]
    [InlineData("17:30", "2026-08-29T17:30:00Z", null)]
    [InlineData("19:05", "2026-08-28T19:05:00Z", null)]
    [InlineData("18:00:30", "2026-08-29T18:00:30Z", null)]
    [InlineData("nope", null, "Use HH:mm (UTC)")]
    public void ParseStartAt_ResolvesTheMostRecentInstantNotAfterNow(string text, string? expectedIso, string? expectedError)
    {
        var now = new DateTimeOffset(2026, 8, 29, 18, 30, 0, TimeSpan.Zero);
        var parsed = Yaat.Client.Views.LiveSessionWindow.ParseStartAt(text, now, out var error);
        Assert.Equal(expectedError, error);
        Assert.Equal(expectedIso is null ? null : DateTimeOffset.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture), parsed);
    }

    [AvaloniaFact]
    public void Badge_ReflectsPauseAndPlayback_AndHidesTheScenarioPlaybackChrome()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.IsLiveSession = true;
        vm.LiveTrafficStatus = new LiveTrafficStatusDto(true, true, 2, 5, null, null, false);

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
        vm.LiveTrafficStatus = new LiveTrafficStatusDto(true, false, null, 0, null, null, false);

        Assert.Equal("LIVE · feed lost", vm.LiveSessionBadgeText);
        Assert.Equal(MainViewModel.FeedLostBadgeColor, vm.LiveSessionBadgeBrush);
    }

    [AvaloniaFact]
    public void CanStartLiveSession_RequiresAConfiguredFeed()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        Assert.False(vm.CanStartLiveSession);
        Assert.Contains("not enabled", vm.StartLiveSessionToolTip);

        vm.LiveTrafficStatus = new LiveTrafficStatusDto(true, true, null, 0, null, null, false);
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
