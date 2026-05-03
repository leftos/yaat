using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Interface Overview > Tabs and Pop-Out. Captures the main
// window after both Ground and Radar views have been popped out — only the
// Aircraft List + Strips tabs remain in the main window's tab strip,
// demonstrating the docked-vs-popped-out trade-off.
internal sealed class MainWindowPoppedOutScene : ScenarioSceneBase
{
    public override string Name => "main-window-popped-out";

    protected override int TabIndex => 0;

    protected override Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        vm.IsGroundViewPoppedOut = true;
        vm.IsRadarViewPoppedOut = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return Task.CompletedTask;
    }
}
