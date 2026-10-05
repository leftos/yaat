using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// GETTING_STARTED.md > Step 5. The S3-NCTC-3 scenario (as radar-view) on the
// Aircraft List tab, its load report closed: the first active (not delayed)
// airborne aircraft in the grid's order is selected, FH 270 is sent to it
// through the normal command path, and once the terminal shows the response
// CM 100 is typed into the command bar without being sent.
internal sealed class FirstCommandScene : ScenarioSceneBase
{
    public override string Name => "first-command";

    protected override int TabIndex => 0;

    protected override string ScenarioFile => "01J02M96SPYP4JV55R5RMVCQBS.json";

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await SendFirstCommandAsync(vm);

        vm.CommandText = "CM 100";
        Dispatcher.UIThread.RunJobs();
    }

    // Selects the first active airborne aircraft in the Aircraft List and sends
    // it FH 270, returning once the terminal shows the aircraft's response.
    // Shared with the terminal-panel scene, which shows the same exchange.
    public static async Task<AircraftModel> SendFirstCommandAsync(MainViewModel vm)
    {
        await SceneActions.WaitUntilAsync(
            () => vm.AircraftView.OfType<AircraftModel>().Any(IsActiveAirborne),
            TimeSpan.FromSeconds(10),
            "an active airborne aircraft in the Aircraft List"
        );
        AircraftModel aircraft = vm.AircraftView.OfType<AircraftModel>().First(IsActiveAirborne);
        vm.SelectedAircraft = aircraft;
        Dispatcher.UIThread.RunJobs();

        long sentAfter = TerminalEntry.LastSequence;
        vm.CommandText = "FH 270";
        await vm.SendCommandCommand.ExecuteAsync(null);
        await SceneActions.WaitUntilAsync(
            () =>
                vm.TerminalEntries.Any(e => (e.Sequence > sentAfter) && (e.Kind == TerminalEntryKind.Response) && (e.Callsign == aircraft.Callsign)),
            TimeSpan.FromSeconds(10),
            $"the terminal response to FH 270 for {aircraft.Callsign}"
        );
        return aircraft;
    }

    private static bool IsActiveAirborne(AircraftModel aircraft) => (!aircraft.IsOnGround) && (!aircraft.IsDelayed);
}
