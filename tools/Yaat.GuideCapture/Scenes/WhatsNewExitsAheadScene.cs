using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim;
using Yaat.Sim.Phases;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Exit left / Exit right list the named exits ahead. The
// OAK scenario's ground view with an arrival spawned on a 4 NM final to runway
// 30 (as just-landed stages it), the paused room run forward in StepSeconds
// steps until the rollout offers an exit side, then the aircraft's menu opened
// by a right-click on it and that side's strip icon clicked: the flyout lists
// the named exits on that side with their distances, the planned one marked.
// The side holding the planned exit is shown, else the side with more exits.
// The view is zoomed in on the arrival, and it and the selection are put back
// once the capture is taken.
internal sealed class WhatsNewExitsAheadScene : ScenarioSceneBase
{
    private const string AddCommand = "ADD IFR L J 30 4 B738";
    private const int StepSeconds = 2;
    private const int MaxSeconds = 300;
    private const double ZoomFactor = 3;

    private MainViewModel? _vm;
    private GroundViewZoom? _zoom;

    public override string Name => "whats-new-exits-ahead";

    protected override int TabIndex => 1;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await SceneActions.AnswerActiveRunwaysPromptAsync(vm, TimeSpan.FromSeconds(10));
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
        string callsign = vm.Aircraft.First(a => !before.Contains(a.Callsign)).Callsign;
        _vm = vm;

        AircraftModel arrival = await AdvanceToExitChoiceAsync(vm, ctx, callsign);
        ExitSide side = ShownSide(arrival.ExitsAhead ?? []);
        Console.WriteLine(
            $"  {callsign}: {arrival.CurrentPhase}, {arrival.GroundSpeed:0} kt; exits ahead "
                + string.Join(", ", (arrival.ExitsAhead ?? []).Select(e => $"{e.Taxiway} {e.Side} {e.DistanceFt} ft{(e.Planned ? " planned" : "")}"))
        );
        _zoom = GroundViewZoom.Apply(vm.Ground, arrival.Position, ZoomFactor);

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, arrival, TimeSpan.FromSeconds(10));
        string entryId = (side == ExitSide.Left) ? MenuIds.TowerExitLeft : MenuIds.TowerExitRight;
        // A pointer click on the icon closes the headless menu before its
        // flyout shows, so the icon is pointed at and its flyout opened as its
        // click handler opens it.
        Button exitButton = SceneActions.StripButton(menu, entryId);
        SceneActions.PointAt(window, exitButton);
        FlyoutBase.ShowAttachedFlyout(exitButton);
        await SceneActions.WaitForOverlayAsync<MenuFlyoutPresenter>(
            window,
            presenter => SceneActions.AreItemsLaidOut(presenter.Items),
            TimeSpan.FromSeconds(10),
            $"the {side} exits flyout of {callsign}"
        );
    }

    // Runs the paused room forward until the arrival is on the ground and its
    // menu offers an exit on either side, so the picture is the same every run.
    private static async Task<AircraftModel> AdvanceToExitChoiceAsync(MainViewModel vm, CaptureContext ctx, string callsign)
    {
        int elapsed = 0;
        AircraftModel arrival = Find(vm, callsign);
        while (
            !(
                arrival.IsOnGround
                && AircraftCommandApplicability.ShowsRunwayExit(arrival)
                && (
                    AircraftCommandApplicability.CanExitRunway(arrival, ExitSide.Left)
                    || AircraftCommandApplicability.CanExitRunway(arrival, ExitSide.Right)
                )
            )
        )
        {
            if (elapsed >= MaxSeconds)
            {
                string state = $"{arrival.CurrentPhase}, {arrival.Altitude:0} ft, {arrival.GroundSpeed:0} kt";
                throw new InvalidOperationException($"{callsign} offers no exit side after {MaxSeconds} s: {state}.");
            }
            await RoomTicks.AdvancePausedAsync(vm, ctx, seconds: StepSeconds, secondsPerStep: StepSeconds);
            elapsed += StepSeconds;
            arrival = Find(vm, callsign);
        }

        return arrival;
    }

    private static ExitSide ShownSide(IReadOnlyList<ExitAheadDto> exits)
    {
        if (exits.FirstOrDefault(e => e.Planned) is { } planned)
        {
            return planned.Side;
        }

        int left = exits.Count(e => e.Side == ExitSide.Left);
        int right = exits.Count(e => e.Side == ExitSide.Right);
        return (left >= right) ? ExitSide.Left : ExitSide.Right;
    }

    private static AircraftModel Find(MainViewModel vm, string callsign) =>
        vm.Aircraft.FirstOrDefault(a => a.Callsign == callsign) ?? throw new InvalidOperationException($"{callsign} left the aircraft list.");

    public override void AfterCapture()
    {
        _zoom?.Restore();
        _zoom = null;
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
