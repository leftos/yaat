using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Report traffic in sight in solo training. The OAK
// scenario's radar view in a solo session ("Pre-fill traffic advisories" on
// True values, "Live advisory check" on) with a VFR PA28 spawned 10 miles west
// of the field at 4,500 ft and four aircraft aloft around it. Its menu is opened
// by a right-click on it and its Report traffic in sight strip icon clicked: the
// solo advisory flyout opens, the nearest row is pointed and Tab fills the
// radar-form call from it part by part, the check line names that aircraft, the
// Builder is unfolded under the box, and the radar rings the pointed aircraft
// with the bearing line. The session settings are put back once the capture is
// taken.
internal sealed class WhatsNewSoloAdvisoryFlyoutScene : ScenarioSceneBase
{
    private const string OwnAdd = "ADD VFR S P @OAK270010 045 P28A";

    // Clock, miles, direction, type, altitude: one Tab per part.
    private const int CallParts = 5;

    // Positions off OAK's radial and distance; around the PA28 they fall at its
    // 9, 6, 4 and 11 o'clock, 3 to 6 miles off. The nearest, the SR22, sits 3
    // miles north of it, so its ring and bearing line clear the menu and the
    // flyout, which open below and to the right of the PA28.
    private static readonly string[] TrafficAdds =
    [
        "ADD VFR S P @OAK286011 055 SR22",
        "ADD VFR S P @OAK265016 035 C172",
        "ADD VFR S T @OAK250012 065 C208",
        "ADD IFR L J @OAK300005 080 B738",
    ];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The server classifies a new aircraft's situation, which picks its quick
    // commands, on the room's next tick.
    private static readonly RoomTicks.Stage Classified = new("to be classified airborne", StepSeconds: 1, MaxSeconds: 5);

    private MainViewModel? _vm;
    private bool _priorSolo;
    private SoloAdvisoryPrefill _priorPrefill;
    private bool _priorLiveCheck;

    public override string Name => "whats-new-solo-advisory-flyout";

    protected override int TabIndex => 2;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        _priorSolo = vm.SessionSoloTrainingMode;
        _priorPrefill = vm.SessionSoloAdvisoryPrefill;
        _priorLiveCheck = vm.SessionSoloAdvisoryLiveCheck;
        vm.SessionSoloTrainingMode = true;
        vm.SessionSoloAdvisoryPrefill = SoloAdvisoryPrefill.TrueValues;
        vm.SessionSoloAdvisoryLiveCheck = true;

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
            $"the solo advisory flyout of {callsign}"
        );
        if (flyout.Items.OfType<MenuItem>().FirstOrDefault()?.Header is not SoloAdvisoryFlyoutHost { Panel: { } panel })
        {
            throw new InvalidOperationException($"The Report traffic in sight flyout of {callsign} holds no solo advisory panel.");
        }

        await SceneActions.WaitUntilAsync(() => panel.Rows.Count == TrafficAdds.Length, Timeout, $"{TrafficAdds.Length} traffic rows");
        SoloTrafficRowButton pointed = panel.Rows[0];
        if (pointed.Callsign != traffic[0])
        {
            throw new InvalidOperationException($"The nearest row is {pointed.Callsign}, not the SR22 {traffic[0]} the shot rings.");
        }
        pointed.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < CallParts; i++)
        {
            panel.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
        }

        panel.BuilderToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SceneActions.WaitUntilAsync(
            () => panel.CheckText.StartsWith($"→ {pointed.Callsign} ", StringComparison.Ordinal) && !panel.IsCheckFlashing,
            Timeout,
            $"the live check to resolve the call to {pointed.Callsign}"
        );
        Dispatcher.UIThread.RunJobs();
        SceneActions.RenderOnce(window);
        Console.WriteLine($"  {callsign} pointed {pointed.Callsign}, box '{panel.Box.Text}', check '{panel.CheckText}'");
    }

    public override void AfterCapture()
    {
        if (_vm is { } vm)
        {
            vm.SessionSoloAdvisoryLiveCheck = _priorLiveCheck;
            vm.SessionSoloAdvisoryPrefill = _priorPrefill;
            vm.SessionSoloTrainingMode = _priorSolo;
            vm.SelectedAircraft = null;
        }

        _vm = null;
    }
}
