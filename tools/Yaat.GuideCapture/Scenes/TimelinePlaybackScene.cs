using Avalonia.Controls;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Simulation Controls > Timeline / Rewind. The main window
// after a rewind: the timeline bar shows the PLAYBACK badge, Take Control,
// the elapsed time (1:00) against the tape end (2:00), and the two bookmark
// ticks on the rail.
internal sealed class TimelinePlaybackScene : TimelineSceneBase
{
    public override string Name => "timeline-playback";

    protected override int TabIndex => 0;

    protected override Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx) => StagePlaybackAsync(vm, ctx);
}
