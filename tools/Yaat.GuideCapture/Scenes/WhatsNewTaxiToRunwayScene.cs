using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Taxi to runway. The OAK scenario's ground view with the
// room's active runways set to 30 and a B738 departure spawned at stand 3, its
// menu opened by a right-click on it, All Commands opened and Taxi to runway
// opened: runway 30's group lists the full-length entry, then each
// intersection long enough for the type with the runway it leaves, the
// full-length and nearest usable rows highlighted, then Other runways. The
// nearest usable row is pointed at, which previews its route, and its
// For departure / Hold short choices opened beside it. The view is zoomed in
// with the aircraft near its upper left so the cascade fits to its right, and
// it and the selection are put back once the capture is taken.
internal sealed class WhatsNewTaxiToRunwayScene : ScenarioSceneBase
{
    private const string Airport = "OAK";
    private const string Runway = "30";
    private const string AddCommand = "ADD IFR L J @3 B738";
    private const double ZoomFactor = 3;

    // Where the aircraft sits in the view, as fractions of its width and height.
    private const double AircraftX = 0.12;
    private const double AircraftY = 0.15;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private MainViewModel? _vm;
    private GroundViewZoom? _zoom;

    public override string Name => "whats-new-taxi-to-runway";

    protected override int TabIndex => 1;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        await SetActiveRunwayAsync(vm);
        AircraftModel departure = await SceneActions.SpawnAsync(vm, AddCommand, Timeout);
        string callsign = departure.Callsign;
        await SceneActions.WaitUntilAsync(
            () => SceneActions.Find(vm, callsign).IsOnGround && (vm.Ground.Layout is not null),
            Timeout,
            $"{callsign} on its stand and the ground layout"
        );
        Console.WriteLine($"  {callsign} at stand 3, assigned runway '{departure.AssignedRunway}'");
        _zoom = GroundViewZoom.Apply(vm.Ground, departure.Position, ZoomFactor);
        SceneActions.PlaceInGroundView(window, vm, departure.Position, AircraftX, AircraftY);

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, departure, Timeout);
        MenuItem allCommands =
            menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == AircraftMenuBuilder.AllCommandsHeader)
            ?? throw new InvalidOperationException($"The menu of {callsign} has no {AircraftMenuBuilder.AllCommandsHeader}.");
        await OpenAsync(window, allCommands, AircraftMenuBuilder.AllCommandsHeader);
        MenuItem taxiToRunway = await SceneActions.OpenSubmenuAsync(allCommands, "Taxi to runway", Timeout);

        List<(MenuItem Item, MenuCommandRow Row)> rows =
        [
            .. taxiToRunway.Items.OfType<MenuItem>().Where(m => m.Header is MenuCommandRow).Select(m => (m, (MenuCommandRow)m.Header!)),
        ];
        Console.WriteLine(
            $"  {callsign} Taxi to runway: "
                + string.Join(" | ", rows.Select(r => $"{r.Row.Name}{(r.Row.IsHighlighted ? " [hl]" : "")} {r.Row.Detail}"))
        );
        MenuItem shown = ShownRow(callsign, rows);
        SceneActions.PointAt(window, shown);
        await OpenAsync(window, shown, ((MenuCommandRow)shown.Header!).Name);
    }

    private static async Task SetActiveRunwayAsync(MainViewModel vm)
    {
        vm.SelectedAircraft = null;
        Dispatcher.UIThread.RunJobs();
        vm.CommandText = $"ARWY {Airport} {Runway}";
        await vm.SendCommandCommand.ExecuteAsync(null);
        await SceneActions.WaitUntilAsync(
            () => vm.RoomActiveRunways.TryGetValue(Airport, out IReadOnlyList<string>? ends) && ends.SequenceEqual([Runway]),
            Timeout,
            $"{Airport}'s active runways to be {Runway}"
        );
    }

    // The nearest usable row: the second highlighted row (the first is the
    // full-length entry), else the first highlighted, else the first row.
    private static MenuItem ShownRow(string callsign, List<(MenuItem Item, MenuCommandRow Row)> rows)
    {
        List<MenuItem> highlighted = [.. rows.Where(r => r.Row.IsHighlighted && (r.Item.Items.Count > 0)).Select(r => r.Item)];
        return highlighted.Skip(1).FirstOrDefault()
            ?? highlighted.FirstOrDefault()
            ?? rows.Select(r => r.Item).FirstOrDefault(i => i.Items.Count > 0)
            ?? throw new InvalidOperationException($"Taxi to runway of {callsign} lists no runway entry with choices.");
    }

    // Opens the item's submenu and waits until its rows are laid out and its
    // host is arranged, as SceneActions.OpenSubmenuAsync does for an item it
    // finds by a string header.
    private static async Task OpenAsync(Window window, MenuItem item, string what)
    {
        item.Open();
        await SceneActions.WaitUntilAsync(() => item.IsSubMenuOpen && SceneActions.AreItemsLaidOut(item.Items), Timeout, $"submenu '{what}' to open");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    public override void AfterCapture()
    {
        _zoom?.Restore();
        _zoom = null;
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
