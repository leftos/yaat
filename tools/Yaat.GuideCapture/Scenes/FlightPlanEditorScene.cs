using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Views > Flight Plan Editor. After scenario load, opens the
// Flight Plan Editor on the first aircraft (SWA5456 in the OAK scenario)
// and captures that window. The amend callback is a no-op — we never click
// Save, only the rendered editor matters.
internal sealed class FlightPlanEditorScene : ScenarioSceneBase
{
    private FlightPlanEditorWindow? _editor;

    public override string Name => "flight-plan-editor";

    protected override int TabIndex => 0;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await SceneActions.WaitUntilAsync(() => vm.Aircraft.Count > 0, TimeSpan.FromSeconds(5), "scenario aircraft to populate");

        var aircraft = vm.Aircraft[0];
        _editor = new FlightPlanEditorWindow(aircraft, (_, _) => { }, _ => Task.CompletedTask);
        _editor.Show();
        Dispatcher.UIThread.RunJobs();
        _editor.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();
    }

    public override Window GetCaptureTarget(Window primary) => _editor ?? primary;
}
