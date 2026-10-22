using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Hold at fix. The OAK scenario's radar view with an IFR
// heavy jet spawned airborne 12 miles west of the field at FL350 (an
// overflight) and sent direct SUNOL then ALTAM, its menu opened by a
// right-click on it and its Hold at fix strip icon clicked, then ALTAM typed in
// the Fix box: ALTAM lies further down the live route than SUNOL, so the empty
// Inbound course box defaults to the SUNOL→ALTAM leg course, and the flyout
// shows the Legs chips on 1½ min above 14,000 ft, the Right / Left toggle, the
// caption naming the side, the fix and the inbound course, and the route rows
// each with its HOLDP command.
internal sealed class WhatsNewHoldFlyoutScene : ScenarioSceneBase
{
    private const string AddCommand = "ADD IFR H J @OAK270012 350";
    private const string DirectCommand = "DCT SUNOL ALTAM";

    // The fix the aircraft is sent direct to first, whose leg into ALTAM the
    // Inbound course box defaults to.
    private const string NextFix = "SUNOL";

    // The later fix, typed in the Fix box.
    private const string Fix = "ALTAM";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The server classifies a new aircraft's situation, which picks its quick
    // commands, on the room's next tick.
    private static readonly RoomTicks.Stage Classified = new("to be classified airborne", StepSeconds: 1, MaxSeconds: 5);

    private static readonly RoomTicks.Stage Navigating = new("to navigate to SUNOL", StepSeconds: 1, MaxSeconds: 5);

    private MainViewModel? _vm;

    public override string Name => "whats-new-hold-flyout";

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
        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.NavigatingTo == NextFix, Navigating);
        Console.WriteLine($"  {callsign} {aircraft.FiledAircraftType} {aircraft.Situation}, navigating to {aircraft.NavigatingTo}");
        if (aircraft.Situation != AircraftSituation.IfrEnroute)
        {
            throw new InvalidOperationException($"{callsign} is {aircraft.Situation}, not IfrEnroute, so its strip has no Hold at fix icon.");
        }

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, aircraft, Timeout);
        MenuFlyoutPresenter flyout = await SceneActions.OpenStripFlyoutAsync(
            window,
            menu,
            MenuIds.HoldPattern,
            Timeout,
            $"the Hold at fix flyout of {callsign}"
        );
        if (flyout.Items.OfType<MenuItem>().FirstOrDefault()?.Header is not HoldPatternFlyoutHost { Child: HoldPatternFlyoutPanel panel })
        {
            throw new InvalidOperationException($"The Hold at fix flyout of {callsign} holds no Hold at fix panel.");
        }

        panel.Box.Text = Fix;
        Dispatcher.UIThread.RunJobs();
        SceneActions.RenderOnce(window);
        Console.WriteLine(
            $"  {callsign} {panel.RouteRows.Count} route rows, legs {panel.Leg}, turns {panel.Turns}, box {panel.BoxCommand}, caption {panel.Caption.Text}"
        );
    }

    public override void AfterCapture()
    {
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
