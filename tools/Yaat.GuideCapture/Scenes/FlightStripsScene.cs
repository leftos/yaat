using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Views > Flight Strips. Per-facility Strips tabs are
// appended dynamically once MainViewModel.StripsEntries is populated, so
// the scene waits for the facility entry to land before selecting tab 3.
internal sealed class FlightStripsScene : ScenarioSceneBase
{
    public override string Name => "flight-strips";

    // Static tabs occupy 0..2 (Aircraft List / Ground / Radar). The first
    // dynamic strips tab lands at index 3.
    protected override int TabIndex => 3;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await SceneActions.WaitUntilAsync(() => vm.StripsEntries.Count >= 1, TimeSpan.FromSeconds(5), "StripsEntries to populate");

        // Re-set the tab index — the base set it once before the dynamic tab
        // existed; now it's valid.
        vm.SelectedTabIndex = 3;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
