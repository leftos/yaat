using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > the range/bearing label clears the data blocks. The radar
// view's picture (S3-NCTC-3, as radar-view stages it) with one measurement
// latched to two aircraft 10-30 nm apart, chosen where the most other aircraft
// crowd the line's midpoint, so the label has data blocks to keep clear of.
// The measurement is removed once the capture is taken.
internal sealed class WhatsNewRblLabelScene : ScenarioSceneBase
{
    private static readonly Rect Region = new(0.1, 0.1, 0.8, 0.8);
    private const double CrowdPx = 120;
    private const int ExtraSeconds = 900;

    private RangeBearingViewState? _measure;

    public override string Name => "whats-new-rbl-label";

    protected override int TabIndex => 2;

    protected override string ScenarioFile => "01J02M96SPYP4JV55R5RMVCQBS.json";

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await RadarViewScene.EnableLoWestSectorAsync(vm, ctx);

        // radar-view's picture holds two aircraft; the scenario's delayed
        // spawns fill the scope over the next minutes.
        await RoomTicks.AdvancePausedAsync(vm, ctx, seconds: ExtraSeconds, secondsPerStep: 30);
        RangeBearingViewState measure = vm.Radar.Measure ?? throw new InvalidOperationException("The radar has no measuring tool.");
        (AircraftModel from, AircraftModel to) = RadarScreenPicks.CrowdedPair(vm, window, Region, (10, 30), CrowdPx);
        Console.WriteLine($"  measuring {from.Callsign} to {to.Callsign}");

        _ =
            measure.Place(
                RblEndpoint.OnAircraft(from.Callsign),
                RblEndpoint.OnAircraft(to.Callsign),
                RadarViewModel.MeasureView,
                vm.Radar.MeasureTrackLookup,
                RadarViewModel.MeasureUnits
            ) ?? throw new InvalidOperationException("The radar has no free measurement slot.");
        _measure = measure;
        Dispatcher.UIThread.RunJobs();
    }

    public override void AfterCapture()
    {
        _measure?.Clear();
        _measure = null;
    }
}
