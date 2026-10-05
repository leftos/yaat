using Avalonia.Controls;
using Avalonia.Threading;
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
    private const int StepSeconds = 5;
    private const int MaxSeconds = 300;
    private const double ZoomFactor = 3;

    private GroundViewZoom? _zoom;

    public override string Name => "just-landed";

    protected override int TabIndex => 1;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await SceneActions.WaitUntilAsync(() => vm.Aircraft.Count > 0, TimeSpan.FromSeconds(10), "scenario aircraft to populate");
        HashSet<string> before = [.. vm.Aircraft.Select(a => a.Callsign)];

        vm.SelectedAircraft = null;
        Dispatcher.UIThread.RunJobs();
        vm.CommandText = AddCommand;
        await vm.SendCommandCommand.ExecuteAsync(null);
        await SceneActions.WaitUntilAsync(
            () => vm.Aircraft.Any(a => !before.Contains(a.Callsign)),
            TimeSpan.FromSeconds(10),
            $"the aircraft '{AddCommand}' spawns"
        );
        AircraftModel arrival = vm.Aircraft.First(a => !before.Contains(a.Callsign));

        int elapsed = 0;
        while (!arrival.IsOnGround)
        {
            if (elapsed >= MaxSeconds)
            {
                string state = $"{arrival.CurrentPhase}, {arrival.Altitude:0} ft, {arrival.GroundSpeed:0} kt";
                throw new InvalidOperationException($"{arrival.Callsign} has not touched down after {MaxSeconds} s: {state}.");
            }
            await RoomTicks.AdvancePausedAsync(vm, ctx, seconds: StepSeconds, secondsPerStep: StepSeconds);
            elapsed += StepSeconds;
        }
        Console.WriteLine($"  {arrival.Callsign} on the ground after {elapsed} s: {arrival.CurrentPhase}, {arrival.GroundSpeed:0} kt");
        _zoom = GroundViewZoom.Apply(vm.Ground, arrival.Position, ZoomFactor);
    }

    public override void AfterCapture()
    {
        _zoom?.Restore();
        _zoom = null;
    }
}
