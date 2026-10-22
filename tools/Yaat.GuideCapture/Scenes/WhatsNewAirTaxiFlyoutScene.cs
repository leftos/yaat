using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Air taxi. The OAK scenario's ground view with a helicopter
// spawned parked at North Field's Kaiser 7 stand, its situation classified
// AtParking by the room's next tick. Its menu is opened by a right-click on it
// and its Air taxi strip icon pointed at: the flyout shows the "Air taxi to"
// filter box over the Pads · parking, Spots and Runways tabs, the selected
// tab's destinations listed below. OAK's ground map has no helipad, so the Pads
// · parking tab lists parking only; when that tab comes up empty the shot
// switches to Spots. The aircraft is held near the view's upper left so the
// flyout fits to its right, and the view and the selection are put back once
// the capture is taken.
internal sealed class WhatsNewAirTaxiFlyoutScene : ScenarioSceneBase
{
    private const string AddCommand = "ADD VFR S H @KAI7 R44";

    private static readonly string[] Tabs = ["Pads · parking", "Spots", "Runways"];

    private const double ZoomFactor = 3;

    // Where the aircraft sits in the view, as fractions of its width and height.
    private const double AircraftX = 0.12;
    private const double AircraftY = 0.15;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The server classifies a new aircraft's situation, which picks its quick
    // commands, on the room's next tick.
    private static readonly RoomTicks.Stage Classified = new("to be classified at parking", StepSeconds: 1, MaxSeconds: 5);

    private MainViewModel? _vm;
    private GroundViewZoom? _zoom;

    public override string Name => "whats-new-air-taxi-flyout";

    protected override int TabIndex => 1;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        AircraftModel helicopter = await SceneActions.SpawnAsync(vm, AddCommand, Timeout);
        string callsign = helicopter.Callsign;
        await SceneActions.WaitUntilAsync(() => vm.Ground.Layout is not null, Timeout, "the ground layout");
        helicopter = await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.Situation == AircraftSituation.AtParking, Classified);
        Console.WriteLine($"  {callsign} at stand {helicopter.ParkingSpot}, {helicopter.Situation} ({helicopter.CurrentPhase})");
        if (helicopter.Situation != AircraftSituation.AtParking)
        {
            throw new InvalidOperationException($"{callsign} is {helicopter.Situation}, not AtParking, so its strip has no Air taxi icon.");
        }

        _zoom = GroundViewZoom.Apply(vm.Ground, helicopter.Position, ZoomFactor);
        SceneActions.PlaceInGroundView(window, vm, helicopter.Position, AircraftX, AircraftY);

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, helicopter, Timeout);
        MenuFlyoutPresenter flyout = await SceneActions.OpenStripFlyoutAsync(
            window,
            menu,
            MenuIds.GroundAirTaxi,
            Timeout,
            $"the Air taxi flyout of {callsign}"
        );
        if (flyout.Items.OfType<MenuItem>().FirstOrDefault()?.Header is not AirTaxiPanel panel)
        {
            throw new InvalidOperationException($"The Air taxi flyout of {callsign} holds no destination panel.");
        }

        List<string> tabs = [.. panel.Strip.Tabs.Select(tab => tab.Label)];
        Console.WriteLine($"  {callsign} Air taxi to tabs: {string.Join(" | ", tabs)}");
        foreach (string wanted in Tabs)
        {
            if (!tabs.Contains(wanted))
            {
                throw new InvalidOperationException($"The Air taxi flyout of {callsign} has no '{wanted}' tab; it has {string.Join(", ", tabs)}.");
            }
        }

        if (panel.Rows.Count == 0)
        {
            panel.Strip.Select(1);
            Dispatcher.UIThread.RunJobs();
            await SceneActions.WaitUntilAsync(() => panel.Rows.Count > 0, Timeout, "the Spots tab's destinations");
        }

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine(
            $"  {callsign} Air taxi to '{panel.BoxLabel}', tab {tabs[panel.Strip.SelectedIndex]}: rows {panel.Rows.Count}, {string.Join(" | ", panel.Lines)}"
        );
        SceneActions.RenderOnce(window);
    }

    public override void AfterCapture()
    {
        _zoom?.Restore();
        _zoom = null;
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
