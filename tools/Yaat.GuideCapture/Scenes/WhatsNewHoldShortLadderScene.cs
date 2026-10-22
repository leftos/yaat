using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Hold short of… The OAK scenario's ground view with a
// business jet spawned at North Field's Kaiser 5 stand, told to taxi C B W to
// runway 30, and the paused room run forward until it is rolling off the stand.
// Its menu is opened by a right-click on it and its Hold short of… strip icon
// pointed at: the flyout lists the bars along its route nearest first, each
// with its distance and the HS it sends. The pointer rests on the second stop,
// so the route to that bar is previewed on the ground view with the bar boxed,
// the aircraft is held near the view's upper left and the view is put back once
// the capture is taken.
internal sealed class WhatsNewHoldShortLadderScene : ScenarioSceneBase
{
    private const string AddCommand = "ADD VFR S+ J @KAI5 C56X";
    private const string TaxiCommand = "TAXI C B W RWY 30";

    // The first moving speed the shot needs: the route is assigned and the
    // aircraft is off its stand, so its situation is Taxiing.
    private const double MovingKnots = 5;
    private const double ZoomFactor = 3;

    // Where the aircraft sits in the view, as fractions of its width and height.
    private const double AircraftX = 0.12;
    private const double AircraftY = 0.15;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly RoomTicks.Stage Moving = new("to taxi above 5 kt", StepSeconds: 2, MaxSeconds: 400);

    private MainViewModel? _vm;
    private GroundViewZoom? _zoom;

    public override string Name => "whats-new-hold-short-ladder";

    protected override int TabIndex => 1;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        AircraftModel aircraft = await SceneActions.SpawnAsync(vm, AddCommand, Timeout);
        string callsign = aircraft.Callsign;
        await SceneActions.WaitUntilAsync(() => vm.Ground.Layout is not null, Timeout, "the ground layout");
        await SceneActions.WaitUntilAsync(() => SceneActions.IsParked(SceneActions.Find(vm, callsign)), Timeout, $"{callsign} on its stand");

        vm.SelectedAircraft = aircraft;
        Dispatcher.UIThread.RunJobs();
        TerminalEntry reply = await SceneActions.SendCommandAsync(vm, callsign, TaxiCommand);
        Console.WriteLine($"  {callsign} {TaxiCommand}: {reply.Kind} {reply.Message}");
        if (reply.Kind != TerminalEntryKind.Response)
        {
            throw new InvalidOperationException($"{callsign} did not accept '{TaxiCommand}': {reply.Kind} {reply.Message}");
        }

        aircraft = await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.GroundSpeed > MovingKnots, Moving);
        Console.WriteLine($"  {callsign} {aircraft.Situation}, {aircraft.CurrentPhase}, {aircraft.GroundSpeed:0} kt on '{aircraft.CurrentTaxiway}'");
        if (aircraft.Situation != AircraftSituation.Taxiing)
        {
            throw new InvalidOperationException($"{callsign} is {aircraft.Situation}, not Taxiing, so its strip has no Hold short of… icon.");
        }

        _zoom = GroundViewZoom.Apply(vm.Ground, aircraft.Position, ZoomFactor);
        SceneActions.PlaceInGroundView(window, vm, aircraft.Position, AircraftX, AircraftY);

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, aircraft, Timeout);
        MenuFlyoutPresenter flyout = await SceneActions.OpenStripFlyoutAsync(
            window,
            menu,
            MenuIds.GroundHoldShort,
            Timeout,
            $"the Hold short of flyout of {callsign}"
        );
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        List<MenuItem> stops = [.. flyout.Items.OfType<MenuItem>().Where(m => m.Header is HoldShortLadderStop)];
        Console.WriteLine($"  {callsign} Hold short of: {string.Join(" | ", stops.Select(m => m.Header!.ToString()))}");
        if (stops.Count < 2)
        {
            throw new InvalidOperationException($"{callsign} lists {stops.Count} bar(s) on its route, not the two the ladder shows.");
        }

        SceneActions.PointAt(window, stops[1]);
        Dispatcher.UIThread.RunJobs();
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
