using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Views > Ground View (Showing taxi routes). The OAK scenario's
// first parked aircraft in the Aircraft List is selected and cleared to taxi
// through the normal command path, its route is pinned on with the Taxi route
// menu's "Always show" mode, and the paused room is run forward until the
// aircraft is moving along the route. The view is zoomed in on the aircraft.
// Gate 29 is a push-back stand, so the aircraft is pushed straight back first:
// a TAXI straight off the stand is accepted but never moves. The aircraft's
// prior taxi-route mode and the ground view are put back once the capture is
// taken.
internal sealed class GroundTaxiRouteScene : ScenarioSceneBase
{
    private const string PushCommand = "PUSH";

    // The route the sim resolves from gate 29 after a straight push: TE out of
    // the alley, then U and W to runway 30.
    private const string TaxiCommand = "TAXI TE U W RWY 30";
    private const int StepSeconds = 5;
    private const int MaxStageSeconds = 240;
    private const double MovingKnots = 5;
    private const double ZoomFactor = 3;

    private GroundViewModel? _ground;
    private string? _callsign;
    private TaxiRouteDisplayMode _priorMode;
    private GroundViewZoom? _zoom;

    public override string Name => "ground-taxi-route";

    protected override int TabIndex => 1;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await SceneActions.WaitUntilAsync(
            () => vm.AircraftView.OfType<AircraftModel>().Any(IsParked),
            TimeSpan.FromSeconds(10),
            "a parked aircraft in the Aircraft List"
        );
        AircraftModel aircraft = vm.AircraftView.OfType<AircraftModel>().First(IsParked);
        vm.SelectedAircraft = aircraft;
        Dispatcher.UIThread.RunJobs();

        string callsign = aircraft.Callsign;
        await SendAcceptedAsync(vm, callsign, PushCommand);
        await AdvanceUntilAsync(vm, ctx, callsign, a => a.CurrentPhase == "Holding After Pushback", "to finish its pushback");

        await SendAcceptedAsync(vm, callsign, TaxiCommand);
        _ground = vm.Ground;
        _callsign = callsign;
        _priorMode = vm.Ground.GetTaxiRouteMode(callsign);
        vm.Ground.SetTaxiRouteMode(callsign, TaxiRouteDisplayMode.AlwaysShow);
        await AdvanceUntilAsync(vm, ctx, callsign, a => (a.GroundSpeed >= MovingKnots) && a.HasActiveTaxiRoute, "to move along its taxi route");
        _zoom = GroundViewZoom.Apply(vm.Ground, Find(vm, callsign).Position, ZoomFactor);
    }

    private static async Task SendAcceptedAsync(MainViewModel vm, string callsign, string command)
    {
        TerminalEntry reply = await SceneActions.SendCommandAsync(vm, callsign, command);
        Console.WriteLine($"  {callsign} {command}: {reply.Kind} {reply.Message}");
        if (reply.Kind != TerminalEntryKind.Response)
        {
            throw new InvalidOperationException($"{callsign} did not accept '{command}': {reply.Kind} {reply.Message}");
        }
    }

    // Runs the paused room forward in fixed steps until the aircraft meets the
    // condition, so the picture is the same every run.
    private static async Task AdvanceUntilAsync(MainViewModel vm, CaptureContext ctx, string callsign, Func<AircraftModel, bool> done, string what)
    {
        int elapsed = 0;
        AircraftModel aircraft = Find(vm, callsign);
        while (!done(aircraft))
        {
            if (elapsed >= MaxStageSeconds)
            {
                throw new InvalidOperationException(
                    $"{callsign} failed {what} within {MaxStageSeconds} s: {aircraft.CurrentPhase}, {aircraft.GroundSpeed:0} kt."
                );
            }
            await RoomTicks.AdvancePausedAsync(vm, ctx, seconds: StepSeconds, secondsPerStep: StepSeconds);
            elapsed += StepSeconds;
            aircraft = Find(vm, callsign);
            Console.WriteLine($"  {callsign} at +{elapsed} s: {aircraft.CurrentPhase}, {aircraft.GroundSpeed:0} kt");
        }
    }

    private static AircraftModel Find(MainViewModel vm, string callsign) =>
        vm.Aircraft.FirstOrDefault(a => a.Callsign == callsign) ?? throw new InvalidOperationException($"{callsign} left the aircraft list.");

    public override void AfterCapture()
    {
        _zoom?.Restore();
        _zoom = null;
        if ((_ground is not null) && (_callsign is not null))
        {
            _ground.SetTaxiRouteMode(_callsign, _priorMode);
            _ground = null;
            _callsign = null;
        }
    }

    private static bool IsParked(AircraftModel aircraft) => aircraft.IsOnGround && (!aircraft.IsDelayed);
}
