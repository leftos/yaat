using Avalonia.Controls;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Views > Ground View (runway exits). The ground view of the
// OAK scenario with an arrival rolling out on runway 30 just after touchdown.
// The arrival is spawned on a 4 NM final with ADD (the scenario's room
// clears arrivals to land on its own), and the paused room is run forward in
// 5 s steps until the aircraft is on the ground. The view is zoomed in on the
// arrival, and put back once the capture is taken.
internal sealed class JustLandedScene : ScenarioSceneBase
{
    private const string AddCommand = "ADD IFR L J 30 4 B738";
    private const double ZoomFactor = 3;

    private static readonly RoomTicks.Stage TouchDown = new("to touch down", StepSeconds: 5, MaxSeconds: 300);

    private GroundViewZoom? _zoom;

    public override string Name => "just-landed";

    protected override int TabIndex => 1;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        AircraftModel spawned = await SceneActions.SpawnAsync(vm, AddCommand, TimeSpan.FromSeconds(10));
        AircraftModel arrival = await RoomTicks.AdvanceUntilAsync(vm, ctx, spawned.Callsign, a => a.IsOnGround, TouchDown);
        _zoom = GroundViewZoom.Apply(vm.Ground, arrival.Position, ZoomFactor);
    }

    public override void AfterCapture()
    {
        _zoom?.Restore();
        _zoom = null;
    }
}
