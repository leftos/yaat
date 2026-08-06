using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.VStrips;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Views > Flight Strips. Per-facility Strips tabs are
// appended dynamically once MainViewModel.StripsEntries is populated, so
// the scene waits for the facility entry to land, then selects the Strips
// tab by locating the materialized VStripsView — never by hardcoded index,
// which silently captures the wrong tab whenever a static tab is added
// ahead of the dynamic ones (Controllers and METAR both did this).
internal sealed class FlightStripsScene : ScenarioSceneBase
{
    public override string Name => "flight-strips";

    // The base sets this before the dynamic Strips tab exists; the real
    // selection happens in OnSceneReadyAsync.
    protected override int TabIndex => 0;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await SceneActions.WaitUntilAsync(() => vm.StripsEntries.Count >= 1, TimeSpan.FromSeconds(5), "StripsEntries to populate");

        var tabControl =
            window.FindControl<TabControl>("MainTabControl") ?? throw new InvalidOperationException("MainTabControl not found on MainWindow");
        var stripsIndex = tabControl.Items.Cast<object?>().ToList().FindIndex(item => (item as TabItem)?.Content is VStripsView);
        if (stripsIndex < 0)
        {
            throw new InvalidOperationException("No Strips TabItem materialized on MainTabControl");
        }

        vm.SelectedTabIndex = stripsIndex;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
