using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Cleared for takeoff. The OAK scenario's ground view with
// a VFR C172 spawned lined up on runway 28R and a VFR flight plan filed at
// 4,500 ft to San Carlos, its menu opened by a right-click on it and its
// Cleared for takeoff strip icon clicked: the flyout opens with the Initial
// altitude box pre-filled from the filed altitude, then the VFR departures
// (closed traffic, straight out, crosswind, downwind and 45-degree turnouts,
// fly heading, on course, turn left or right direct a fix), each showing the
// command it sends. The view is zoomed in with the aircraft near its upper
// left so the flyout fits to its right, and it and the selection are put back
// once the capture is taken.
internal sealed class WhatsNewTakeoffScene : ScenarioSceneBase
{
    private const string AddCommand = "ADD VFR S P 28R C172";
    private const string FlightPlanCommand = "VP C172 4500 KOAK DCT KSQL";
    private const int FiledFeet = 4500;
    private const double ZoomFactor = 3;

    // Where the aircraft sits in the view, as fractions of its width and height.
    private const double AircraftX = 0.12;
    private const double AircraftY = 0.15;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The server classifies a new aircraft's situation, which picks its quick
    // commands, on the room's next tick.
    private static readonly RoomTicks.Stage Classified = new("to be classified lined up", StepSeconds: 1, MaxSeconds: 5);

    private MainViewModel? _vm;
    private GroundViewZoom? _zoom;

    public override string Name => "whats-new-takeoff";

    protected override int TabIndex => 1;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        AircraftModel departure = await SceneActions.SpawnAsync(vm, AddCommand, Timeout);
        string callsign = departure.Callsign;
        await SceneActions.WaitUntilAsync(() => vm.Ground.Layout is not null, Timeout, "the ground layout");
        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.Situation == AircraftSituation.LinedUp, Classified);
        await FileFlightPlanAsync(vm, departure);
        Console.WriteLine($"  {callsign} {departure.CurrentPhase} on {departure.AssignedRunway}, filed {departure.CruiseAltitudeDisplay}");
        _zoom = GroundViewZoom.Apply(vm.Ground, departure.Position, ZoomFactor);
        SceneActions.PlaceInGroundView(window, vm, departure.Position, AircraftX, AircraftY);

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, departure, Timeout);
        MenuFlyoutPresenter flyout = await SceneActions.OpenStripFlyoutAsync(
            window,
            menu,
            MenuIds.TowerClearedForTakeoff,
            Timeout,
            $"the Cleared for takeoff flyout of {callsign}"
        );
        IEnumerable<string> rows = flyout
            .Items.OfType<MenuItem>()
            .Where(m => m.IsVisible)
            .Select(m => AutomationProperties.GetName(m) ?? $"{m.Header}");
        Console.WriteLine($"  {callsign} Cleared for takeoff: {string.Join(" | ", rows)}");
    }

    // Files the VFR flight plan whose altitude the flyout's altitude box is
    // pre-filled from, and waits until the aircraft list carries it.
    private static async Task FileFlightPlanAsync(MainViewModel vm, AircraftModel departure)
    {
        string callsign = departure.Callsign;
        vm.SelectedAircraft = departure;
        Dispatcher.UIThread.RunJobs();
        vm.CommandText = FlightPlanCommand;
        await vm.SendCommandCommand.ExecuteAsync(null);
        await SceneActions.WaitUntilAsync(
            () => SceneActions.Find(vm, callsign).FiledVfrCruiseFeet == FiledFeet,
            Timeout,
            $"{callsign} to carry the VFR flight plan '{FlightPlanCommand}'"
        );
    }

    public override void AfterCapture()
    {
        _zoom?.Restore();
        _zoom = null;
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
