using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Maintain altitude. The OAK scenario's radar view with an
// IFR jet spawned airborne 15 miles west of the field at 10,000 ft, its menu
// opened by a right-click on it and its Maintain altitude strip icon clicked:
// the altitude flyout opens with its title line, the Altitude box, and the
// 300 px list of altitudes centred on the aircraft's own row, each row with
// its mark and the CM / DM command it sends, the MVA line among them.
internal sealed class WhatsNewAltitudeFlyoutScene : ScenarioSceneBase
{
    private const string AddCommand = "ADD IFR H J -270 15 10000";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The server classifies a new aircraft's situation, which picks its quick
    // commands, on the room's next tick.
    private static readonly RoomTicks.Stage Classified = new("to be classified airborne", StepSeconds: 1, MaxSeconds: 5);

    private MainViewModel? _vm;

    public override string Name => "whats-new-altitude-flyout";

    protected override int TabIndex => 2;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        AircraftModel aircraft = await SceneActions.SpawnAsync(vm, AddCommand, Timeout);
        string callsign = aircraft.Callsign;
        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.Situation != AircraftSituation.Unknown, Classified);
        Console.WriteLine($"  {callsign} {aircraft.CurrentPhase}, altitude {aircraft.Altitude:F0}, {aircraft.Situation}");

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, aircraft, Timeout);
        MenuFlyoutPresenter flyout = await SceneActions.OpenStripFlyoutAsync(
            window,
            menu,
            MenuIds.AltitudeMaintain,
            Timeout,
            $"the Maintain altitude flyout of {callsign}"
        );
        if (flyout.Items.OfType<MenuItem>().FirstOrDefault()?.Header is AltitudeFlyoutHost { Child: MaintainAltitudePanel panel })
        {
            Console.WriteLine($"  {callsign} Maintain altitude: {panel.List.Title} · {panel.List.Subtitle}, {panel.Rows.Count} rows");
        }
    }

    public override void AfterCapture()
    {
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
