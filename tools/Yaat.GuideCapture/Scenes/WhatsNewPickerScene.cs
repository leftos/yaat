using Avalonia;
using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Radar;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > smarter pickers. The radar view's picture (S3-NCTC-3, as
// radar-view stages it), the most isolated airborne aircraft in the upper left
// of the scope right-clicked, and the Maintain altitude icon on its strip
// clicked: the altitude picker opens with the current and assigned rows marked
// and the MVA line where the aircraft is. The selection is put back once the
// capture is taken.
internal sealed class WhatsNewPickerScene : ScenarioSceneBase
{
    private static readonly Rect Region = new(0.15, 0.1, 0.35, 0.35);

    private MainViewModel? _vm;

    public override string Name => "whats-new-picker";

    protected override int TabIndex => 2;

    protected override string ScenarioFile => "01J02M96SPYP4JV55R5RMVCQBS.json";

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await RadarViewScene.EnableLoWestSectorAsync(vm, ctx);
        await SceneActions.AnswerActiveRunwaysPromptAsync(vm, TimeSpan.FromSeconds(10));
        _vm = vm;
        RadarCanvas canvas = RadarScreenPicks.Canvas(window);
        AircraftModel aircraft = RadarScreenPicks.MostIsolated(vm, window, canvas, Region);
        Console.WriteLine($"  picker on {aircraft.Callsign} ({aircraft.CurrentPhase})");

        ContextMenu menu = await SceneActions.OpenAircraftMenuAsync(window, vm, aircraft, TimeSpan.FromSeconds(10));
        SceneActions.Click(window, SceneActions.StripButton(menu, MenuIds.AltitudeMaintain));
        ListBox picker = await SceneActions.WaitForOverlayAsync<ListBox>(
            window,
            list => list.ItemCount > 0,
            TimeSpan.FromSeconds(10),
            $"the Maintain altitude picker of {aircraft.Callsign}"
        );
        if (TopLevel.GetTopLevel(picker) != window)
        {
            throw new InvalidOperationException(
                $"The Maintain altitude picker opened in {TopLevel.GetTopLevel(picker)?.GetType().Name}, not in the captured window."
            );
        }
    }

    public override void AfterCapture()
    {
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
