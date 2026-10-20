using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Direct to. The OAK scenario's radar view with an IFR
// jet spawned airborne 15 miles west of the field at FL350 and sent direct
// SUNOL then ALTAM, its menu opened by a right-click on it and its Direct to
// strip icon clicked, then "SU" typed in the Fix box: the flyout shows its
// title line naming the next fix, the Direct to / Append to route toggle, the
// box with the outlined row's command, the SUNOL route row with the ● and
// every other fix starting with SU, nearest first.
internal sealed class WhatsNewDirectToFlyoutScene : ScenarioSceneBase
{
    private const string AddCommand = "ADD IFR H J -270 15 35000";
    private const string DirectCommand = "DCT SUNOL ALTAM";
    private const string Typed = "SU";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The server classifies a new aircraft's situation, which picks its quick
    // commands, on the room's next tick.
    private static readonly RoomTicks.Stage Classified = new("to be classified airborne", StepSeconds: 1, MaxSeconds: 5);

    private static readonly RoomTicks.Stage Navigating = new("to navigate to SUNOL", StepSeconds: 1, MaxSeconds: 5);

    private MainViewModel? _vm;

    public override string Name => "whats-new-direct-to-flyout";

    protected override int TabIndex => 2;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        AircraftModel aircraft = await SceneActions.SpawnAsync(vm, AddCommand, Timeout);
        string callsign = aircraft.Callsign;
        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.Situation != AircraftSituation.Unknown, Classified);
        vm.SelectedAircraft = aircraft;
        TerminalEntry reply = await SceneActions.SendCommandAsync(vm, callsign, DirectCommand);
        Console.WriteLine($"  {callsign} {DirectCommand}: {reply.Kind} {reply.Message}");
        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.NavigatingTo == "SUNOL", Navigating);
        Console.WriteLine($"  {callsign} {aircraft.FiledAircraftType} {aircraft.Situation}, navigating to {aircraft.NavigatingTo}");

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, aircraft, Timeout);
        MenuFlyoutPresenter flyout = await SceneActions.OpenStripFlyoutAsync(
            window,
            menu,
            MenuIds.NavigationDirectTo,
            Timeout,
            $"the Direct to flyout of {callsign}"
        );
        if (flyout.Items.OfType<MenuItem>().FirstOrDefault()?.Header is not DirectToFlyoutHost { Child: DirectToFlyoutPanel panel })
        {
            throw new InvalidOperationException($"The Direct to flyout of {callsign} holds no Direct to panel.");
        }

        panel.Box.Text = Typed;
        Console.WriteLine($"  {callsign} {panel.RouteRows.Count} route rows, {panel.MatchRows.Count} matches for {Typed}, box {panel.BoxCommand}");
    }

    public override void AfterCapture()
    {
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
