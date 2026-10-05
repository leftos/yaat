using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Simulation Controls > Export Room as Scenario. The
// "Exported Scenario — Needs Review" window for a room running S3-NCTC-3. The export
// is asked of the server directly (the app's menu path then shows a save
// dialog, which a capture cannot answer); the review window is built from the
// flags it returns, as MainWindow builds it.
internal sealed class ExportRoomScenarioScene : ScenarioSceneBase
{
    private ScenarioExportReviewWindow? _review;

    public override string Name => "export-room-scenario";

    protected override int TabIndex => 0;

    // S3-NCTC-3, not the OAK default: OAK's 18 aircraft are all parked at
    // stands, which export cleanly, so its export flags nothing and the app
    // never opens the review window.
    protected override string ScenarioFile => "01J02M96SPYP4JV55R5RMVCQBS.json";

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        // Right after the load most of S3-NCTC-3's aircraft have no position
        // yet (they start at named fixes) or are still waiting to spawn, so the
        // paused room is run forward a fixed 320 sim-seconds before the export.
        await RoomTicks.AdvancePausedAsync(vm, ctx, seconds: 320, secondsPerStep: 16);

        ScenarioExportResultDto result = await vm.Connection.ExportRoomAsScenarioAsync();
        if (result.DeniedReason is not null)
        {
            throw new InvalidOperationException($"Export Room as Scenario was refused: {result.DeniedReason}");
        }
        if (result.Flags.Count == 0)
        {
            throw new InvalidOperationException(
                $"Export Room as Scenario flagged none of {result.AircraftCount} aircraft; the review window would not open."
            );
        }

        _review = new ScenarioExportReviewWindow(result.Flags) { Width = 480, Height = 360 };
        _review.Show();
        Dispatcher.UIThread.RunJobs();
        _review.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    public override Window GetCaptureTarget(Window primary) => _review ?? primary;

    public override IEnumerable<Window> ExtraWindows => _review is null ? [] : [_review];
}
