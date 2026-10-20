using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Assign speed. The OAK scenario's radar view with an
// IFR jet spawned airborne 15 miles west of the field at FL350, its menu
// opened by a right-click on it and its Assign speed strip icon clicked:
// the speed flyout opens with its title line, the Speed box, the Resume
// normal speed and Final approach speed rows, and the knots chips with the
// Mach chips under them, the present speed's chips filled.
internal sealed class WhatsNewSpeedFlyoutScene : ScenarioSceneBase
{
    private const string AddCommand = "ADD IFR H J -270 15 35000";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The server classifies a new aircraft's situation, which picks its quick
    // commands, on the room's next tick.
    private static readonly RoomTicks.Stage Classified = new("to be classified airborne", StepSeconds: 1, MaxSeconds: 5);

    private MainViewModel? _vm;

    public override string Name => "whats-new-speed-flyout";

    protected override int TabIndex => 2;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        AircraftModel aircraft = await SceneActions.SpawnAsync(vm, AddCommand, Timeout);
        string callsign = aircraft.Callsign;
        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.Situation != AircraftSituation.Unknown, Classified);
        Console.WriteLine(
            $"  {callsign} {aircraft.FiledAircraftType} {aircraft.CurrentPhase}, altitude {aircraft.Altitude:F0}, {aircraft.Situation}"
        );

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, aircraft, Timeout);
        MenuFlyoutPresenter flyout = await SceneActions.OpenStripFlyoutAsync(
            window,
            menu,
            MenuIds.SpeedAssign,
            Timeout,
            $"the Assign speed flyout of {callsign}"
        );
        if (flyout.Items.OfType<MenuItem>().FirstOrDefault()?.Header is SpeedFlyoutHost { Child: SpeedFlyoutPanel panel })
        {
            Console.WriteLine($"  {callsign} {panel.Title}: {panel.KnotChips.Count} knots chips, {panel.MachChips.Count} Mach chips");
        }
    }

    public override void AfterCapture()
    {
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
