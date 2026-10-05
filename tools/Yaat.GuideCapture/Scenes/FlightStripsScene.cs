using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.VStrips;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Views > Flight Strips. Per-facility strips tabs are
// appended once MainViewModel.StripsEntries is populated; each entry's tab
// holds a VStripsSplitHost whose DataContext is the entry. The scene picks the
// first entry whose printer received the scenario's strips (OAK's, for the OAK
// clearances fixture), selects its tab, and moves every printed departure
// strip into its selected bay, as the printer modal's "move all" does, so the
// bay is populated rather than empty.
internal sealed class FlightStripsScene : ScenarioSceneBase
{
    public override string Name => "flight-strips";

    // The base sets this before the dynamic Strips tab exists; the real
    // selection happens in OnSceneReadyAsync.
    protected override int TabIndex => 0;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await SceneActions.WaitUntilAsync(
            () => vm.StripsEntries.Any(e => e.Vm.Printer.PendingCount > 0),
            TimeSpan.FromSeconds(15),
            "a strips facility to receive the scenario's printed strips"
        );
        VStripsDockEntryViewModel entry = vm.StripsEntries.First(e => e.Vm.Printer.PendingCount > 0);

        vm.SelectedTabIndex = StripsTabIndex(window, entry);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        await entry.Vm.MoveAllPrinterStripsToBayAsync(PrinterQueueKind.Departure);
        await SceneActions.WaitUntilAsync(
            () => entry.Vm.Printer.PendingCount == 0,
            TimeSpan.FromSeconds(10),
            "the printed strips to move into the bay"
        );
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private int StripsTabIndex(Window window, VStripsDockEntryViewModel entry)
    {
        TabControl tabs =
            window.FindControl<TabControl>("MainTabControl")
            ?? throw new InvalidOperationException($"Scene '{Name}': the main window has no MainTabControl.");
        for (int i = 0; i < tabs.Items.Count; i++)
        {
            if (tabs.Items[i] is TabItem { Content: VStripsSplitHost host } && ReferenceEquals(host.DataContext, entry))
            {
                return i;
            }
        }
        throw new InvalidOperationException($"Scene '{Name}': no tab holds the strips view for entry '{entry.TabTitle}'.");
    }
}
