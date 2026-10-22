using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Report field in sight. The OAK scenario's radar view in an
// RPO session, the room's OAK runways set to 28L 28R 30 with ARWY, and an IFR
// large jet (a B739 at the pinned seed) spawned 10 miles out on the 28R final
// and cleared straight-in for the ILS 28R with CAPPSI, so it flies the approach
// inbound to KOAK with the field ahead of it. Its menu is opened
// by a right-click on it and its Report field in sight strip icon clicked: the
// field advisory flyout opens with the true clock and miles pre-filled in the
// box, the dial and the Miles stepper, the line the pilot hears, and the
// field's row naming the room's arrival runways. The session's solo setting is
// put back once the capture is taken.
internal sealed class WhatsNewFieldAdvisoryFlyoutScene : ScenarioSceneBase
{
    private const string Runways = "ARWY OAK 28L 28R 30";
    private const string OwnAdd = "ADD IFR L J 28R 10";
    private const string Approach = "CAPPSI ILS28R";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The CAPPSI replaces the final with the approach navigation on the room's
    // next tick, and the server classifies the aircraft's situation from it.
    private static readonly RoomTicks.Stage OnApproach = new("to fly the approach", StepSeconds: 1, MaxSeconds: 10);

    private MainViewModel? _vm;
    private bool _priorSolo;

    public override string Name => "whats-new-field-advisory-flyout";

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
        CommandResultDto cleared = await vm.Connection.SendCommandAsync(callsign, Approach, vm.Preferences.UserInitials);
        if (!cleared.Success)
        {
            throw new InvalidOperationException($"'{Approach}' for {callsign} was refused: {cleared.Message}");
        }

        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.Situation == AircraftSituation.Approach, OnApproach);
        Console.WriteLine($"  {callsign} {aircraft.AircraftType} {aircraft.Situation} to {aircraft.Destination}, altitude {aircraft.Altitude:F0}");

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, aircraft, Timeout);
        MenuFlyoutPresenter flyout = await SceneActions.OpenStripFlyoutAsync(
            window,
            menu,
            MenuIds.ApproachReportFieldInSight,
            Timeout,
            $"the field advisory flyout of {callsign}"
        );
        if (flyout.Items.OfType<MenuItem>().FirstOrDefault()?.Header is not FieldAdvisoryFlyoutHost { Panel: { } panel })
        {
            throw new InvalidOperationException($"The Report field in sight flyout of {callsign} holds no field advisory panel.");
        }

        await SceneActions.WaitUntilAsync(
            () => panel.SpokenText.IsVisible && panel.FieldRowWhere.EndsWith("runways 28L 28R 30", StringComparison.Ordinal),
            Timeout,
            "the pre-filled advisory and the field row naming OAK's arrival runways"
        );
        Dispatcher.UIThread.RunJobs();
        SceneActions.RenderOnce(window);
        Console.WriteLine($"  {callsign} box '{panel.Box.Text}', row '{panel.FieldRowName}' / '{panel.FieldRowWhere}'");
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
