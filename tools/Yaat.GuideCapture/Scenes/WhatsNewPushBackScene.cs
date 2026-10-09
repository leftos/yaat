using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Push back to. The OAK scenario's ground view with a B738
// spawned at gate 12, whose push targets fill both sections, its menu opened
// by a right-click on it and its Push back to strip icon clicked: the flyout
// lists the taxilanes and taxiway behind the aircraft, each with the facing
// chips it can end on, then taxi spot C, every row with its distance and
// command. The shot waits for the live plan to land, so the seed's Refining
// targets row is gone. The view is zoomed in with
// the aircraft near its upper left so the flyout fits to its right, and it and
// the selection are put back once the capture is taken.
internal sealed class WhatsNewPushBackScene : ScenarioSceneBase
{
    private const string AddCommand = "ADD IFR L J @12 B738";
    private const double ZoomFactor = 3;

    // Where the aircraft sits in the view, as fractions of its width and height.
    private const double AircraftX = 0.12;
    private const double AircraftY = 0.15;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The server classifies a new aircraft's situation, which picks its quick
    // commands, on the room's next tick.
    private static readonly RoomTicks.Stage Classified = new("to be classified at parking", StepSeconds: 1, MaxSeconds: 5);

    // The stand's live plan runs in the background once the menu asks for it.
    private static readonly TimeSpan PlanTimeout = TimeSpan.FromSeconds(120);

    private MainViewModel? _vm;
    private GroundViewZoom? _zoom;

    public override string Name => "whats-new-push-back";

    protected override int TabIndex => 1;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        AircraftModel departure = await SceneActions.SpawnAsync(vm, AddCommand, Timeout);
        string callsign = departure.Callsign;
        await SceneActions.WaitUntilAsync(() => vm.Ground.Layout is not null, Timeout, "the ground layout");
        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.Situation == AircraftSituation.AtParking, Classified);
        Console.WriteLine($"  {callsign} at stand {departure.ParkingSpot} ({departure.CurrentPhase})");
        _zoom = GroundViewZoom.Apply(vm.Ground, departure.Position, ZoomFactor);
        SceneActions.PlaceInGroundView(window, vm, departure.Position, AircraftX, AircraftY);

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, departure, Timeout);
        MenuFlyoutPresenter flyout = await SceneActions.OpenStripFlyoutAsync(
            window,
            menu,
            MenuIds.GroundPushbackTo,
            Timeout,
            $"the Push back to flyout of {callsign}"
        );
        await SceneActions.WaitUntilAsync(() => IsSettled(flyout), PlanTimeout, $"the Push back to plan of {callsign} to land");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        LogRows(callsign, flyout);
    }

    // The live plan has landed once neither the seed's Refining row nor the
    // Computing row leads the flyout, and its rows are laid out again.
    private static bool IsSettled(MenuFlyoutPresenter flyout) =>
        (!flyout.Items.OfType<MenuItem>().Any(m => m.Header is PushbackToMenu.RefiningText or PushbackToMenu.ComputingText))
        && SceneActions.AreItemsLaidOut(flyout.Items);

    // Each row's text and its chips (facings, or push anyway), for checking
    // the shot shows the chips it is meant to.
    private static void LogRows(string callsign, MenuFlyoutPresenter flyout)
    {
        IEnumerable<string> rows = flyout
            .Items.OfType<MenuItem>()
            .Select(m =>
            {
                string chips = string.Join(" ", m.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string));
                return (m.Header is string header) ? header : $"{AutomationProperties.GetName(m)} [{chips}]";
            });
        Console.WriteLine($"  {callsign} Push back to: {string.Join(" | ", rows)}");
    }

    public override void AfterCapture()
    {
        _zoom?.Restore();
        _zoom = null;
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
