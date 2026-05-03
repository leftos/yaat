using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Views > Ground View (popped out).
internal sealed class GroundViewPopoutScene : ScenarioSceneBase
{
    public override string Name => "ground-view-window";

    protected override int TabIndex => 0;

    protected override Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        vm.IsGroundViewPoppedOut = true;
        Dispatcher.UIThread.RunJobs();

        var main = (MainWindow)window;
        var ground = main.GroundViewWindow ?? throw new InvalidOperationException("GroundViewWindow was not created by MainWindow.");
        ground.Width = 1400;
        ground.Height = 900;
        Dispatcher.UIThread.RunJobs();
        ground.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return Task.CompletedTask;
    }

    public override Window GetCaptureTarget(Window primary)
    {
        var main = (MainWindow)primary;
        return main.GroundViewWindow ?? throw new InvalidOperationException("GroundViewWindow missing at capture time.");
    }
}
