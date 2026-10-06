using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The timeline slider's thumb in playback: a state update that moves the elapsed time — a rewind into
/// playback, a take-control out of it — leaves the thumb at the playhead, not clamped to the old
/// maximum.
/// </summary>
public sealed class TimelineSliderTests
{
    // After a rewind from the live end of a 2:00 tape to 1:00, the server's sim state puts the room in
    // playback at 1:00 with the tape ending at 2:00; the thumb must sit at 1:00, not at the start.
    [AvaloniaFact]
    public void Slider_AfterRewindIntoPlayback_SitsAtThePlayhead()
    {
        using var harness = TimelineTestHarness.Boot();
        harness.Vm.ApplySimState(paused: true, rate: 1, elapsed: TimelineTestHarness.TapeEndSeconds, isPlayback: false, tapeEnd: 0);
        harness.Settle();

        harness.Vm.ApplySimState(paused: true, rate: 1, elapsed: 60, isPlayback: true, tapeEnd: TimelineTestHarness.TapeEndSeconds);
        harness.Settle();

        Slider slider = harness.TimelineSlider();
        Assert.Equal(TimelineTestHarness.TapeEndSeconds, slider.Maximum);
        Assert.Equal(60, slider.Value);
    }

    // A client that joins a room already in playback gets one state update that moves the elapsed time
    // and grows the maximum together; the thumb must still land at the playhead.
    [AvaloniaFact]
    public void Slider_IntoPlaybackInOneUpdate_SitsAtThePlayhead()
    {
        using var harness = TimelineTestHarness.Boot();

        harness.Vm.ApplySimState(paused: true, rate: 1, elapsed: 60, isPlayback: true, tapeEnd: TimelineTestHarness.TapeEndSeconds);
        harness.Settle();

        Slider slider = harness.TimelineSlider();
        Assert.Equal(TimelineTestHarness.TapeEndSeconds, slider.Maximum);
        Assert.Equal(60, slider.Value);
    }

    // Leaving playback (Take Control at 1:00 of a 2:00 tape) makes 1:00 the live end of the timeline.
    [AvaloniaFact]
    public void Slider_AfterLeavingPlayback_SitsAtTheLiveEnd()
    {
        using var harness = TimelineTestHarness.BootInPlayback();

        harness.Vm.ApplySimState(paused: true, rate: 1, elapsed: 60, isPlayback: false, tapeEnd: 0);
        harness.Settle();

        Slider slider = harness.TimelineSlider();
        Assert.Equal(60, slider.Maximum);
        Assert.Equal(60, slider.Value);
    }
}

/// <summary>
/// Boots a MainWindow with the timeline bar on and holds the per-process preference cleanup in one
/// place. Shared by the slider tests and the marker-tick tests, which boot the same window.
/// </summary>
internal sealed class TimelineTestHarness : IDisposable
{
    internal const double TapeEndSeconds = 120;

    private readonly Window _window;

    private TimelineTestHarness(Window window, MainViewModel vm)
    {
        _window = window;
        Vm = vm;
    }

    internal MainViewModel Vm { get; }

    internal Window Window => _window;

    internal static TimelineTestHarness Boot()
    {
        var window = new MainWindow { Width = 1200, Height = 700 };
        var vm = (MainViewModel)window.DataContext!;
        vm.ActiveScenarioName = "Timeline test";
        vm.ShowTimelineBar = true;
        window.Show();
        Settle(window);
        return new TimelineTestHarness(window, vm);
    }

    internal static TimelineTestHarness BootInPlayback()
    {
        TimelineTestHarness harness = Boot();
        harness.Vm.ApplySimState(paused: true, rate: 1, elapsed: 60, isPlayback: true, tapeEnd: TapeEndSeconds);
        harness.Settle();
        return harness;
    }

    internal Slider TimelineSlider() =>
        _window.FindControl<Slider>("TimelineSlider") ?? throw new InvalidOperationException("MainWindow has no TimelineSlider");

    internal void Settle() => Settle(_window);

    // Show Timeline Bar is saved to the per-process preferences.json every MainWindow reads, so it is
    // turned back off once the test is done.
    public void Dispose() => Vm.ShowTimelineBar = false;

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
