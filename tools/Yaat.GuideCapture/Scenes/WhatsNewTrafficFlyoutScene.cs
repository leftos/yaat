using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Traffic in sight and Follow traffic. The OAK scenario's
// radar view in an RPO session with a VFR PA28 spawned 10 miles west of the
// field at 4,500 ft and five aircraft aloft around it, 3 to 7 miles off. Its
// menu is opened by a right-click on it and its Report traffic in sight strip
// icon clicked: the flyout lists the airborne aircraft nearest first under the
// Traffic box showing the command Enter sends, and the pointer rests on the
// nearest row, the SR22 3 miles north, so the radar rings it. The session's
// solo setting is put back once the capture is taken.
internal sealed class WhatsNewTrafficFlyoutScene : ScenarioSceneBase
{
    private const string OwnAdd = "ADD VFR S P @OAK270010 045 P28A";

    // Positions off OAK's radial and distance. The nearest, the SR22, sits 3
    // miles north of the PA28, so its ring clears the menu and the flyout,
    // which open below and to the right of the PA28.
    private static readonly string[] TrafficAdds =
    [
        "ADD VFR S P @OAK286011 055 SR22",
        "ADD VFR S P @OAK265016 035 C172",
        "ADD VFR S T @OAK250012 065 C208",
        "ADD IFR L J @OAK300005 080 B738",
        "ADD VFR S P @OAK240008 025 C152",
    ];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The server classifies a new aircraft's situation, which picks its quick
    // commands, on the room's next tick.
    private static readonly RoomTicks.Stage Classified = new("to be classified airborne", StepSeconds: 1, MaxSeconds: 5);

    private MainViewModel? _vm;
    private bool _priorSolo;

    public override string Name => "whats-new-traffic-flyout";

    protected override int TabIndex => 2;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        _priorSolo = vm.SessionSoloTrainingMode;
        vm.SessionSoloTrainingMode = false;

        AircraftModel aircraft = await SceneActions.SpawnAsync(vm, OwnAdd, Timeout);
        string callsign = aircraft.Callsign;
        List<string> traffic = [];
        foreach (string add in TrafficAdds)
        {
            AircraftModel spawned = await SceneActions.SpawnAsync(vm, add, Timeout);
            traffic.Add(spawned.Callsign);
            Console.WriteLine($"  traffic {spawned.Callsign} {spawned.AircraftType} at {spawned.Altitude:F0}");
        }

        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.Situation != AircraftSituation.Unknown, Classified);
        Console.WriteLine($"  {callsign} {aircraft.AircraftType} {aircraft.Situation}, altitude {aircraft.Altitude:F0}");

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, aircraft, Timeout);
        MenuFlyoutPresenter flyout = await SceneActions.OpenStripFlyoutAsync(
            window,
            menu,
            MenuIds.ApproachReportTrafficInSight,
            Timeout,
            $"the Report traffic in sight flyout of {callsign}"
        );
        if (flyout.Items.OfType<MenuItem>().FirstOrDefault()?.Header is not AirborneTrafficFlyoutHost { Panel: { } panel })
        {
            throw new InvalidOperationException($"The Report traffic in sight flyout of {callsign} holds no traffic panel.");
        }

        await SceneActions.WaitUntilAsync(() => panel.Rows.Count == TrafficAdds.Length, Timeout, $"{TrafficAdds.Length} traffic rows");
        AirborneTrafficRowButton pointed = panel.Rows[0];
        if (pointed.Entry.Callsign != traffic[0])
        {
            throw new InvalidOperationException($"The nearest row is {pointed.Entry.Callsign}, not the SR22 {traffic[0]} the shot rings.");
        }

        SceneActions.PointAt(window, pointed);
        Dispatcher.UIThread.RunJobs();
        SceneActions.RenderOnce(window);
        Console.WriteLine($"  {callsign} {panel.GroupLine}, pointed {pointed.Entry}, box '{panel.Box.Text}' {panel.BoxCommandText.Text}");
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
