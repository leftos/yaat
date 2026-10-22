using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim.Situation;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Join airway. The OAK scenario's radar view with an IFR
// turboprop spawned airborne 10 miles west of the field at 8,000 ft, filed with
// FP out of KOAK on OAK V6 SAC to KSMF, then turned onto heading 360 (an
// @fix spawn heads straight at the field, where every airway through OAK would
// be joined at OAK, so the turn is what makes the rows name different join
// fixes). Its menu is opened by a right-click on it and All Commands ›
// Procedures › Join airway… opened: the flyout shows the Airway box, the filed
// V6 marked ◆, then the airways nearby, each row naming the fix the aircraft
// joins after intercepting on its present heading with that fix's distance and
// bearing, and its JAWY command.
internal sealed class WhatsNewJoinAirwayFlyoutScene : ScenarioSceneBase
{
    private const string AddCommand = "ADD IFR S+ T @OAK270010 080";
    private const string FiledRoute = "080 KOAK OAK V6 SAC KSMF";
    private const string FiledAirway = "V6";
    private const string TurnCommand = "FH 360";

    // The rows the shot must show: at least three airways reaching different
    // fixes.
    private const int DistinctJoinFixes = 3;

    // The default quick-command strip has no Join airway icon, so the flyout
    // opens from All Commands › Procedures.
    private const string JoinAirwayLabel = "Join airway…";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The server classifies a new aircraft's situation, which picks its quick
    // commands, on the room's next tick.
    private static readonly RoomTicks.Stage Classified = new("to be classified airborne", StepSeconds: 1, MaxSeconds: 5);

    private static readonly RoomTicks.Stage TurningNorth = new("to turn onto heading 360", StepSeconds: 5, MaxSeconds: 90);

    private MainViewModel? _vm;

    public override string Name => "whats-new-join-airway-flyout";

    protected override int TabIndex => 2;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        AircraftModel aircraft = await SceneActions.SpawnAsync(vm, AddCommand, Timeout);
        string callsign = aircraft.Callsign;
        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => a.Situation != AircraftSituation.Unknown, Classified);
        vm.SelectedAircraft = aircraft;

        string fileCommand = $"FP {aircraft.AircraftType} {FiledRoute}";
        TerminalEntry filed = await SceneActions.SendCommandAsync(vm, callsign, fileCommand);
        Console.WriteLine($"  {callsign} {fileCommand}: {filed.Kind} {filed.Message}");
        await SceneActions.WaitUntilAsync(
            () => aircraft.Route.Contains(FiledAirway, StringComparison.Ordinal),
            Timeout,
            $"{callsign}'s route to file {FiledAirway}"
        );

        TerminalEntry turned = await SceneActions.SendCommandAsync(vm, callsign, TurnCommand);
        Console.WriteLine($"  {callsign} {TurnCommand}: {turned.Kind} {turned.Message}");
        await RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, a => NearlyNorth(a.MagneticHeadingDegrees), TurningNorth);
        Console.WriteLine($"  {callsign} reached heading {aircraft.MagneticHeadingDegrees:F1}°M");
        Console.WriteLine($"  {callsign} {aircraft.AircraftType} {aircraft.Situation}, heading {aircraft.Heading}, route '{aircraft.Route}'");

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, aircraft, Timeout);
        MenuItem entry = await SceneActions.OpenAllCommandsEntryAsync(window, menu, JoinAirwayLabel, Timeout);
        if (entry.Items.OfType<MenuItem>().FirstOrDefault()?.Header is not JoinAirwayFlyoutHost { Child: JoinAirwayFlyoutPanel panel })
        {
            throw new InvalidOperationException($"The Join airway flyout of {callsign} holds no Join airway panel.");
        }

        await SceneActions.WaitUntilAsync(
            () => panel.Rows.Any(row => row.IsFiled) && panel.Rows.Any(row => !row.IsFiled),
            Timeout,
            "filed and nearby airway rows"
        );
        foreach (string line in panel.Lines)
        {
            Console.WriteLine($"  {callsign} | {line}");
        }

        if (!panel.Rows.Any(row => row.Airway == FiledAirway))
        {
            throw new InvalidOperationException(
                $"{callsign} has no {FiledAirway} row; it has {string.Join(", ", panel.Rows.Select(row => row.Airway))}."
            );
        }

        List<string> joinFixes = [.. panel.Rows.Select(row => row.AheadFix).OfType<string>().Distinct(StringComparer.Ordinal)];
        if (joinFixes.Count < DistinctJoinFixes)
        {
            throw new InvalidOperationException(
                $"{callsign}'s rows join at {joinFixes.Count} fix(es) ({string.Join(", ", joinFixes)}), not {DistinctJoinFixes}."
            );
        }

        Dispatcher.UIThread.RunJobs();
        SceneActions.RenderOnce(window);
    }

    public override void AfterCapture()
    {
        _vm?.SelectedAircraft = null;
        _vm = null;
    }

    // A magnetic heading within 5° of 360, across the 355–005 wrap.
    private static bool NearlyNorth(double magnetic)
    {
        double heading = magnetic % 360;
        return (heading >= 355) || (heading <= 5);
    }
}
