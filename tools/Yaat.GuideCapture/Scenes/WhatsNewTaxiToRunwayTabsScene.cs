using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Taxi to runway. The OAK scenario's ground view with a
// B738 spawned at gate 25 and assigned runway 30, its menu opened by a
// right-click on it and All Commands › Taxi to runway opened (a parked
// aircraft's strip has no Taxi to runway icon): the flyout's tab strip leads
// with "30 assigned" and the room's departure runways, the rest after them by
// runway number, and runway 30's tab shows its runway entries, each title over
// its For departure and Hold short rows. The shot waits for the selected
// tab's rows and for no tab to be searching, so no spinner shows. The view is
// zoomed in with the aircraft near its upper left so the submenus fit to its
// right, and it and the selection are put back once the capture is taken.
internal sealed class WhatsNewTaxiToRunwayTabsScene : ScenarioSceneBase
{
    private const string AddCommand = "ADD IFR L J @25 B738";
    private const string AssignedRunway = "30";
    private const double ZoomFactor = 3;

    // Where the aircraft sits in the view, as fractions of its width and height.
    private const double AircraftX = 0.12;
    private const double AircraftY = 0.15;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The server classifies a new aircraft's situation, which picks its quick
    // commands, on the room's next tick.
    private static readonly RoomTicks.Stage Classified = new("to be classified at parking", StepSeconds: 1, MaxSeconds: 5);

    // The assigned and departure runways are searched in the background as the flyout opens.
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(120);

    private MainViewModel? _vm;
    private GroundViewZoom? _zoom;

    public override string Name => "whats-new-taxi-to-runway-tabs";

    protected override int TabIndex => 1;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        AircraftModel departure = await SceneActions.SpawnAsync(vm, AddCommand, Timeout);
        string callsign = departure.Callsign;
        await SceneActions.WaitUntilAsync(() => vm.Ground.Layout is not null, Timeout, "the ground layout");
        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.Situation == AircraftSituation.AtParking, Classified);
        vm.SelectedAircraft = departure;
        Dispatcher.UIThread.RunJobs();
        TerminalEntry reply = await SceneActions.SendCommandAsync(vm, callsign, $"RWY {AssignedRunway}");
        await SceneActions.WaitUntilAsync(
            () => departure.AssignedRunway == AssignedRunway,
            Timeout,
            $"{callsign} to be assigned runway {AssignedRunway}"
        );
        string active = string.Join("; ", vm.RoomActiveRunways.Select(airport => $"{airport.Key} {string.Join(" ", airport.Value)}"));
        Console.WriteLine(
            $"  {callsign} at stand {departure.ParkingSpot}, assigned {departure.AssignedRunway} ({reply.Message}); active runways {active}"
        );
        _zoom = GroundViewZoom.Apply(vm.Ground, departure.Position, ZoomFactor);
        SceneActions.PlaceInGroundView(window, vm, departure.Position, AircraftX, AircraftY);

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, departure, Timeout);
        string label = MenuCatalog.Get(MenuIds.GroundTaxiToRunway).Label;
        MenuItem entry = await SceneActions.OpenAllCommandsEntryAsync(window, menu, label, Timeout);
        TaxiToRunwayPanel panel =
            entry.Items.OfType<MenuItem>().Select(item => item.Header).OfType<TaxiToRunwayPanel>().FirstOrDefault()
            ?? throw new InvalidOperationException($"The '{label}' entry of {callsign} holds no tab panel.");
        await SceneActions.WaitUntilAsync(() => IsSettled(panel), SearchTimeout, $"the Taxi to runway searches of {callsign} to land");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        LogPanel(callsign, panel);
    }

    // Runway 30's tab is selected and shows its rows, and no tab is searching.
    private static bool IsSettled(TaxiToRunwayPanel panel) =>
        (panel.Strip.SelectedIndex >= 0)
        && (panel.Strip.Tabs[panel.Strip.SelectedIndex].Label == $"{AssignedRunway} assigned")
        && (panel.Rows.Count > 0)
        && panel.Strip.Tabs.All(tab => tab.State != FlyoutTabState.Searching);

    // The tabs and the selected tab's lines, for checking the shot shows the
    // tabs and rows it is meant to.
    private static void LogPanel(string callsign, TaxiToRunwayPanel panel)
    {
        Console.WriteLine($"  {callsign} Taxi to runway tabs: {string.Join(" | ", panel.Strip.Tabs.Select(FlyoutTabStrip.AccessibleName))}");
        Console.WriteLine($"  {callsign} Taxi to runway {AssignedRunway}: {string.Join(" | ", panel.Lines)}");
    }

    public override void AfterCapture()
    {
        _zoom?.Restore();
        _zoom = null;
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
