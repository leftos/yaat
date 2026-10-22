using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Approach clearances. The OAK scenario's radar view in an
// RPO session, the room's OAK runways set to 28L 28R 30 with ARWY, and an IFR
// large jet spawned 15 miles east of the field at 4,000 ft, filed with FP from
// KSJC to KOAK (an airborne ADD files no destination), and told to join
// the ILS 30 final course with JFAC, so it flies inbound to KOAK on the
// approach course but is not yet cleared for the approach. Its menu is opened
// by a right-click on it and its Cleared approach strip icon clicked: the approach flyout opens with the Force toggle and the Approach
// box, the default row "Cleared ILS 30" for the assigned runway 30 pinned on
// top (28L and 28R are too short for the jet, so their rows read short, and 30
// does not), and a tab for every OAK runway, the room's arrival runways first,
// the 30 tab selected. The session's solo setting is put back once the capture
// is taken.
internal sealed class WhatsNewApproachFlyoutScene : ScenarioSceneBase
{
    private const string Runways = "ARWY OAK 28L 28R 30";
    private const string OwnAdd = "ADD IFR L J -090 15 4000";
    private const string FiledPlan = "040 KSJC DCT KOAK";

    // Runway 30, since 28L and 28R are too short for a large jet and their rows
    // would all show dimmed "short".
    private const string JoinFinal = "JFAC ILS30";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The JFAC starts the course intercept on the room's next tick, and the
    // server classifies the aircraft's situation from it.
    private static readonly RoomTicks.Stage OnApproach = new("to join the final course", StepSeconds: 1, MaxSeconds: 10);

    private MainViewModel? _vm;
    private bool _priorSolo;

    public override string Name => "whats-new-approach-flyout";

    protected override int TabIndex => 2;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        _priorSolo = vm.SessionSoloTrainingMode;
        vm.SessionSoloTrainingMode = false;

        CommandResultDto runways = await vm.Connection.SendCommandAsync("", Runways, vm.Preferences.UserInitials);
        if (!runways.Success)
        {
            throw new InvalidOperationException($"'{Runways}' was refused: {runways.Message}");
        }

        await SceneActions.WaitUntilAsync(
            () => vm.RoomActiveRunways.TryGetValue("OAK", out IReadOnlyList<string>? ends) && (ends.Count == 3),
            Timeout,
            "the room's active runways to list OAK 28L 28R 30"
        );

        AircraftModel aircraft = await SceneActions.SpawnAsync(vm, OwnAdd, Timeout);
        string callsign = aircraft.Callsign;
        // An airborne ADD files no destination, and the approach commands need one.
        string fileCommand = $"FP {aircraft.AircraftType} {FiledPlan}";
        CommandResultDto filed = await vm.Connection.SendCommandAsync(callsign, fileCommand, vm.Preferences.UserInitials);
        if (!filed.Success)
        {
            throw new InvalidOperationException($"'{fileCommand}' for {callsign} was refused: {filed.Message}");
        }

        await SceneActions.WaitUntilAsync(() => aircraft.Destination is "KOAK" or "OAK", Timeout, $"{callsign}'s destination to read KOAK");
        CommandResultDto joined = await vm.Connection.SendCommandAsync(callsign, JoinFinal, vm.Preferences.UserInitials);
        if (!joined.Success)
        {
            throw new InvalidOperationException($"'{JoinFinal}' for {callsign} was refused: {joined.Message}");
        }

        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.Situation == AircraftSituation.Approach, OnApproach);
        Console.WriteLine($"  {callsign} {aircraft.AircraftType} {aircraft.Situation} to {aircraft.Destination}, altitude {aircraft.Altitude:F0}");
        if (aircraft.Situation != AircraftSituation.Approach)
        {
            throw new InvalidOperationException($"{callsign} is {aircraft.Situation}, not Approach, so its strip has no Cleared approach icon.");
        }

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, aircraft, Timeout);
        MenuFlyoutPresenter flyout = await SceneActions.OpenStripFlyoutAsync(
            window,
            menu,
            MenuIds.ApproachCleared,
            Timeout,
            $"the Cleared approach flyout of {callsign}"
        );
        if (flyout.Items.OfType<MenuItem>().FirstOrDefault()?.Header is not ApproachFlyoutHost { Child: ApproachFlyoutPanel panel })
        {
            throw new InvalidOperationException($"The Cleared approach flyout of {callsign} holds no approach panel.");
        }

        await SceneActions.WaitUntilAsync(
            () => (panel.Strip.Tabs.Count >= 2) && (panel.PinnedRows.Count > 0) && (panel.Rows.Count > 0),
            Timeout,
            "a pinned default approach over at least two runway tabs"
        );
        Dispatcher.UIThread.RunJobs();
        SceneActions.RenderOnce(window);
        Console.WriteLine(
            $"  {callsign} tabs {string.Join(" ", panel.Strip.Tabs.Select(t => t.Label))}, {panel.PinnedRows.Count} pinned, {panel.Rows.Count} rows"
        );
    }

    public override void AfterCapture()
    {
        if (_vm is { } vm)
        {
            vm.SessionSoloTrainingMode = _priorSolo;
            vm.SelectedAircraft = null;
        }

        _vm = null;
    }
}
