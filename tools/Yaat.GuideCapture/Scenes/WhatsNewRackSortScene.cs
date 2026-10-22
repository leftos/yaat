using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.VStrips;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Sorting a rack. The OAK scenario's OAK vStrips tab with
// the printer's departure strips moved into the selected bay, as the printer
// modal's "move all" does, then the bay's first rack turned auto-sort on with
// the canonical RACKSORT command every viewer of the bay shares. The bay shows
// rack 1 with its auto-sort banner and the strips in callsign order, A to Z.
internal sealed class WhatsNewRackSortScene : ScenarioSceneBase
{
    // The rack the printer's strips move into, 0-based in the view model and
    // rack 1 on the wire.
    private const int RackIndex = 0;
    private const int MinStrips = 5;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public override string Name => "whats-new-rack-sort";

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

        StripBayViewModel bay = entry.Vm.SelectedBay ?? throw new InvalidOperationException("The OAK strips view has no selected bay.");
        StripRackViewModel rack =
            (bay.Racks.Count > RackIndex)
                ? bay.Racks[RackIndex]
                : throw new InvalidOperationException($"Bay {bay.FacilityId}/{bay.Name} has {bay.Racks.Count} rack(s), not the one the shot sorts.");

        // The strips view sends through the view's own command path, as its
        // RACKSORT row does; the command bar cannot send a strip command.
        string command = VStripsCanonicalBuilder.BuildRackSort(bay.FacilityId, bay.Name, RackIndex, on: true);
        CommandResultDto? result = await vm.SendCommandForViewCoreAsync("", command, "");
        Console.WriteLine($"  {command}: {result?.Success} {result?.Message}");
        if (result?.Success != true)
        {
            throw new InvalidOperationException($"'{command}' was refused: {result?.Message ?? "the send threw"}");
        }

        await SceneActions.WaitUntilAsync(() => IsSorted(rack), Timeout, "rack 1 to hold its callsigns A to Z");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"  {bay.FacilityId}/{bay.Name} rack 1, auto-sort {rack.IsAutoSorted}: {string.Join(", ", Callsigns(rack))}");
    }

    private static bool IsSorted(StripRackViewModel rack)
    {
        List<string> callsigns = Callsigns(rack);
        return (callsigns.Count >= MinStrips) && callsigns.SequenceEqual(callsigns.OrderBy(callsign => callsign, StringComparer.OrdinalIgnoreCase));
    }

    // The rack's callsigns top to bottom: a rack's index 0 is its visual bottom
    // (every presenter is docked to Bottom), so the display order is reversed.
    private static List<string> Callsigns(StripRackViewModel rack) => [.. rack.Strips.Reverse().Select(strip => strip.AircraftIdField)];

    private static int StripsTabIndex(Window window, VStripsDockEntryViewModel entry)
    {
        TabControl tabs =
            window.FindControl<TabControl>("MainTabControl") ?? throw new InvalidOperationException("The main window has no MainTabControl.");
        for (int i = 0; i < tabs.Items.Count; i++)
        {
            if (tabs.Items[i] is TabItem { Content: VStripsSplitHost host } && ReferenceEquals(host.DataContext, entry))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"No tab holds the strips view for entry '{entry.TabTitle}'.");
    }
}
