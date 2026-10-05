using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Views > Radar View. Loads the S3-NCTC-3 (Area C Complete)
// scenario — 59 airborne aircraft in NCT TRACON airspace — enables the
// LO-W_S video map (NCT TRACON's "Low West Sector" overlay), and dials the
// range out to 120 NM so the airspace fits inside the canvas with margin
// for full datablocks (callsign + altitude + groundspeed + scratchpad)
// without clipping at the canvas edges.
internal sealed class RadarViewScene : ScenarioSceneBase
{
    public override string Name => "radar-view";

    protected override int TabIndex => 2;

    protected override string ScenarioFile => "01J02M96SPYP4JV55R5RMVCQBS.json";

    protected override Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx) => EnableLoWestSectorAsync(vm, ctx);

    public static async Task EnableLoWestSectorAsync(MainViewModel vm, CaptureContext ctx)
    {
        // Wait for the server to push the initial aircraft state — vm.HasScenario
        // flips on the LoadScenarioResult arriving, but the AircraftUpdated
        // broadcasts (which populate vm.Aircraft) trickle in after.
        await SceneActions.WaitUntilAsync(() => vm.Aircraft.Count >= 30, TimeSpan.FromSeconds(20), "scenario aircraft to populate");

        // Video maps are downloaded asynchronously after scenario load; the
        // toggle list is populated as soon as the FacilityVideoMaps DTO
        // arrives, but the map's geometry data takes longer (NCT references
        // 212 maps and the first run downloads them all). Waiting only on the
        // toggle leaves UpdateActiveMaps hitting a null cache and the map
        // silently fails to draw.
        await SceneActions.WaitUntilAsync(
            () => vm.Radar.MapToggles.Any(t => string.Equals(t.ShortName, "LO-W_S", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(45),
            "LO-W_S video map toggle to populate"
        );

        VideoMapToggleItem loToggle = vm.Radar.MapToggles.First(t => string.Equals(t.ShortName, "LO-W_S", StringComparison.OrdinalIgnoreCase));
        await SceneActions.WaitUntilAsync(
            () => vm.Radar.IsMapDataCached(loToggle.MapId),
            TimeSpan.FromSeconds(120),
            "LO-W_S map geometry to be cached"
        );

        // Run the paused sim forward so fix-anchored aircraft (S3-NCTC-3 starts
        // every jet at a named fix with no resolved lat/lon) get ticks to
        // compute their position, leader line, and projected track. Without
        // this the canvas paints aircraft-less because the AircraftModel
        // positions are still at (0,0). 480 sim-seconds (8 minutes) in steps
        // of 16 is long enough for fix-anchored aircraft to spread out from
        // their start fixes, accept handoffs to TRACON positions, and have
        // steady leader-line vectors; a fixed count keeps the picture the same
        // every run.
        await RoomTicks.AdvancePausedAsync(vm, ctx, seconds: 480, secondsPerStep: 16);

        // Range first so that the SaveSettings call triggered by the toggle
        // flip writes RangeNm=120 to the prefs file. If we set range *after*
        // the toggle, the prefs file ends up with RangeNm=40 (the
        // ApplyVideoMapsDto default), and a downstream RestoreSettings would
        // then snap the radar back to 40 NM before capture.
        vm.Radar.RangeNm = 120;
        loToggle.IsEnabled = true;
        Dispatcher.UIThread.RunJobs();
    }
}
