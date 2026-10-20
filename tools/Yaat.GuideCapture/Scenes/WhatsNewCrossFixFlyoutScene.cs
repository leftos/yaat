using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Commands;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Cross fix. The OAK scenario's radar view with an IFR
// jet spawned airborne 15 miles west of the field at FL350 and sent direct
// SUNOL then ALTAM, its menu opened by a right-click on it and its Cross fix
// strip icon clicked, then SUNOL typed in the Fix box, At or above picked on
// the Cross at toggle, 110 typed in the Altitude box and 250 in the Speed box:
// the flyout shows the box's command (CFIX SUNOL A110 250), the caption
// "Cross SUNOL at or above 11,000 at 250 knots" and the outlined SUNOL row.
internal sealed class WhatsNewCrossFixFlyoutScene : ScenarioSceneBase
{
    private const string AddCommand = "ADD IFR H J -270 15 35000";
    private const string DirectCommand = "DCT SUNOL ALTAM";
    private const string Fix = "SUNOL";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The server classifies a new aircraft's situation, which picks its quick
    // commands, on the room's next tick.
    private static readonly RoomTicks.Stage Classified = new("to be classified airborne", StepSeconds: 1, MaxSeconds: 5);

    private static readonly RoomTicks.Stage Navigating = new("to navigate to SUNOL", StepSeconds: 1, MaxSeconds: 5);

    private MainViewModel? _vm;

    public override string Name => "whats-new-cross-fix-flyout";

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
        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.NavigatingTo == Fix, Navigating);
        Console.WriteLine($"  {callsign} {aircraft.FiledAircraftType} {aircraft.Situation}, navigating to {aircraft.NavigatingTo}");

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, aircraft, Timeout);
        MenuFlyoutPresenter flyout = await SceneActions.OpenStripFlyoutAsync(
            window,
            menu,
            MenuIds.ProceduresCrossFix,
            Timeout,
            $"the Cross fix flyout of {callsign}"
        );
        if (flyout.Items.OfType<MenuItem>().FirstOrDefault()?.Header is not CrossFixFlyoutHost { Child: CrossFixFlyoutPanel panel })
        {
            throw new InvalidOperationException($"The Cross fix flyout of {callsign} holds no Cross fix panel.");
        }

        panel.Box.Text = Fix;
        panel.Toggle(CrossFixAltitudeType.AtOrAbove).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        panel.AltitudeBox.Text = "110";
        panel.SpeedBox.Text = "250";
        Console.WriteLine($"  {callsign} {panel.RouteRows.Count} route rows, box {panel.BoxCommand}, caption {panel.Caption.Text}");
    }

    public override void AfterCapture()
    {
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
