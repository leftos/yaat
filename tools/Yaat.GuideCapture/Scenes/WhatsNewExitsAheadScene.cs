using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private const double ZoomFactor = 3;

    private static readonly RoomTicks.Stage ExitChoice = new("to list the exits ahead on a side it can exit", StepSeconds: 2, MaxSeconds: 300);

    private MainViewModel? _vm;
    private GroundViewZoom? _zoom;

    public override string Name => "whats-new-exits-ahead";

    protected override int TabIndex => 1;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await SceneActions.AnswerActiveRunwaysPromptAsync(vm, TimeSpan.FromSeconds(10));
        AircraftModel spawned = await SceneActions.SpawnAsync(vm, AddCommand, TimeSpan.FromSeconds(10));
        string callsign = spawned.Callsign;
        _vm = vm;

        AircraftModel arrival = await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, OffersExitChoice, ExitChoice);
        IReadOnlyList<ExitAheadDto> exits = arrival.ExitsAhead ?? [];
        ExitSide side = ShownSide(callsign, exits);
        Console.WriteLine(
            $"  {callsign}: exits ahead "
                + string.Join(", ", exits.Select(e => $"{e.Taxiway} {e.Side} {e.DistanceFt} ft{(e.Planned ? " planned" : "")}"))
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

    // On the ground, its menu offering an exit on either side, and the exits
    // ahead listed: CanExitRunway already holds while ExitsAhead is still
    // empty, and the flyout then shows no named exits.
    private static bool OffersExitChoice(AircraftModel arrival) =>
        arrival.IsOnGround
        && AircraftCommandApplicability.ShowsRunwayExit(arrival)
        && (AircraftCommandApplicability.CanExitRunway(arrival, ExitSide.Left) || AircraftCommandApplicability.CanExitRunway(arrival, ExitSide.Right))
        && (arrival.ExitsAhead is { Count: > 0 });

    // The side of the planned exit, else the side with more exits ahead.
    private static ExitSide ShownSide(string callsign, IReadOnlyList<ExitAheadDto> exits)
    {
        ExitSide side;
        if (exits.FirstOrDefault(e => e.Planned) is { } planned)
        {
            side = planned.Side;
        }
        else
        {
            int left = exits.Count(e => e.Side == ExitSide.Left);
            int right = exits.Count(e => e.Side == ExitSide.Right);
            side = (left >= right) ? ExitSide.Left : ExitSide.Right;
        }

        if (!exits.Any(e => e.Side == side))
        {
            throw new InvalidOperationException($"{callsign} lists no exit ahead on the {side} side; it lists {exits.Count} exit(s) in all.");
        }

        return side;
    }

    public override void AfterCapture()
    {
        _zoom?.Restore();
        _zoom = null;
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
