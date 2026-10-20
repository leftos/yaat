using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Fly heading. The OAK scenario's radar view with an IFR
// jet spawned airborne 15 miles west of the field at 10,000 ft, its menu
// opened by a right-click on it and its Fly heading strip icon clicked: the
// heading flyout opens with the Present heading row, the Fly heading / Turn
// left / Turn right toggle, the heading box, and the compass rose between the
// left and right degree chips, its needle on the aircraft's heading.
internal sealed class WhatsNewHeadingFlyoutScene : ScenarioSceneBase
{
    private const string AddCommand = "ADD IFR H J -270 15 10000";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The server classifies a new aircraft's situation, which picks its quick
    // commands, on the room's next tick.
    private static readonly RoomTicks.Stage Classified = new("to be classified airborne", StepSeconds: 1, MaxSeconds: 5);

    private MainViewModel? _vm;

    public override string Name => "whats-new-heading-flyout";

    protected override int TabIndex => 2;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        AircraftModel aircraft = await SceneActions.SpawnAsync(vm, AddCommand, Timeout);
        string callsign = aircraft.Callsign;
        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.Situation != AircraftSituation.Unknown, Classified);
        Console.WriteLine($"  {callsign} {aircraft.CurrentPhase}, heading {aircraft.HeadingDisplay}, {aircraft.Situation}");

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, aircraft, Timeout);
        MenuFlyoutPresenter flyout = await SceneActions.OpenStripFlyoutAsync(
            window,
            menu,
            MenuIds.HeadingFly,
            Timeout,
            $"the Fly heading flyout of {callsign}"
        );
        if (flyout.Items.OfType<MenuItem>().FirstOrDefault()?.Header is HeadingFlyoutHost { Child: HeadingFlyoutPanel panel })
        {
            Console.WriteLine($"  {callsign} Fly heading: {string.Join(" | ", panel.Lines)}");
        }
    }

    public override void AfterCapture()
    {
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
