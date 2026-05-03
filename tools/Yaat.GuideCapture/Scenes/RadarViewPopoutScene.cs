using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Views > Radar View (popped out). Setting IsRadarViewPoppedOut
// on the MainViewModel triggers MainWindow to spawn a RadarViewWindow with the
// shared Radar VM. We capture that secondary window via GetCaptureTarget.
//
// Mirrors RadarViewScene's NCT C + LO-W_S + 120 NM setup so the popped-out
// window shows the same useful airspace view, just sans MainWindow chrome.
internal sealed class RadarViewPopoutScene : ScenarioSceneBase
{
    public override string Name => "radar-view-window";

    // Tab index irrelevant once the radar is popped out, but the base requires
    // a value. Aircraft List is a sensible fallback for the (now empty) main.
    protected override int TabIndex => 0;

    protected override string ScenarioFile => "01J02M96SPYP4JV55R5RMVCQBS.json";

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await RadarViewScene.EnableLoWestSectorAsync(vm);

        vm.IsRadarViewPoppedOut = true;
        Dispatcher.UIThread.RunJobs();

        var main = (MainWindow)window;
        var radar = main.RadarViewWindow ?? throw new InvalidOperationException("RadarViewWindow was not created by MainWindow.");
        radar.Width = 1400;
        radar.Height = 900;
        Dispatcher.UIThread.RunJobs();
        radar.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    public override Window GetCaptureTarget(Window primary)
    {
        var main = (MainWindow)primary;
        return main.RadarViewWindow ?? throw new InvalidOperationException("RadarViewWindow missing at capture time.");
    }
}
