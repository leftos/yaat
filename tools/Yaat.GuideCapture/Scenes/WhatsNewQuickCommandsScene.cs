using Avalonia;
using Avalonia.Controls;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > quick commands in the right-click menu. The radar view's
// picture (S3-NCTC-3, as radar-view stages it) with the most isolated airborne
// aircraft in the upper left of the scope selected and its menu opened by a
// right-click on it: the state line, then the icon strip, its first icon
// pointed at so the label row above the icons names it. The selection is put
// back once the capture is taken.
internal sealed class WhatsNewQuickCommandsScene : ScenarioSceneBase
{
    private static readonly Rect Region = new(0.15, 0.1, 0.35, 0.35);

    private MainViewModel? _vm;

    public override string Name => "whats-new-quick-commands";

    protected override int TabIndex => 2;

    protected override string ScenarioFile => "01J02M96SPYP4JV55R5RMVCQBS.json";

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await RadarViewScene.EnableLoWestSectorAsync(vm, ctx);
        _vm = vm;
        AircraftModel aircraft = RadarScreenPicks.MostIsolated(vm, window, Region, Name);
        Console.WriteLine($"  menu on {aircraft.Callsign} ({aircraft.CurrentPhase})");

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, aircraft, TimeSpan.FromSeconds(10));
        List<Button> strip = SceneActions.StripButtons(menu);
        if (strip.Count == 0)
        {
            throw new InvalidOperationException($"The menu of {aircraft.Callsign} has no quick-command strip.");
        }

        SceneActions.PointAt(window, strip[0]);
    }

    public override void AfterCapture()
    {
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
