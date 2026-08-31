// The in-process server is only referenced when the sibling yaat-server checkout exists (see the
// csproj); a yaat-only clone compiles the tool without this scene.
#if HAS_YAAT_SERVER
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Server.LiveTraffic;
using Yaat.Sim;
using Yaat.Sim.LiveTraffic;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Simulation Controls > Live Traffic. Same NCT radar picture as
// RadarViewScene, then the session's Live Traffic toggle goes on and four fake
// SWIM tracks are fed straight into the in-process server's LiveTrafficStore
// (there is no feed in the capture tool), so the capture shows dashed shadow
// targets among the scenario's aircraft, the LIVE status-bar indicator, and a
// feed that reports connected.
internal sealed class LiveTrafficScene : ScenarioSceneBase
{
    public override string Name => "live-traffic";

    protected override int TabIndex => 2;

    protected override string ScenarioFile => "01J02M96SPYP4JV55R5RMVCQBS.json";

    // Real-looking arrivals and departures inside the SFO / OAK / SJC Class B/C — the NCT TRACON scope.
    private static readonly (
        string Callsign,
        string Type,
        uint Beacon,
        double Lat,
        double Lon,
        double AltFt,
        double GsKt,
        double TrackDeg,
        double VsFpm
    )[] Tracks =
    [
        ("UAL1523", "B738", 3411, 37.52, -122.05, 6_000, 230, 300, -1_200),
        ("SWA2211", "B737", 3427, 37.93, -122.52, 8_000, 260, 130, -1_500),
        ("ASA341", "E175", 3452, 37.72, -122.02, 4_000, 210, 285, -900),
        ("DAL889", "A321", 3466, 37.62, -122.55, 7_000, 280, 45, 2_000),
    ];

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await RadarViewScene.EnableLoWestSectorAsync(vm);

        var store = ctx.ServerServices.GetRequiredService<LiveTrafficStore>();
        store.ReportFeedState(connected: true, DateTimeOffset.UtcNow);
        PublishTracks(store);

        vm.SessionLiveTrafficEnabled = true;
        Dispatcher.UIThread.RunJobs();

        // The sync only runs on a room tick, so let the sim run at 1x until the shadows have arrived,
        // republishing each second so no sample ages past the STARS staleness window meanwhile.
        if (vm.IsPaused)
        {
            await vm.TogglePauseCommand.ExecuteAsync(null);
            await SceneActions.WaitUntilAsync(() => !vm.IsPaused, TimeSpan.FromSeconds(5), "sim to unpause");
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (vm.Aircraft.Count(a => a.IsLiveTraffic) < Tracks.Length)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for live-traffic shadows to appear on the radar");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
            PublishTracks(store);
            Dispatcher.UIThread.RunJobs();
        }

        await SceneActions.WaitUntilAsync(() => vm.IsLiveTrafficStatusVisible, TimeSpan.FromSeconds(5), "LIVE status-bar indicator");

        if (!vm.IsPaused)
        {
            await vm.TogglePauseCommand.ExecuteAsync(null);
            await SceneActions.WaitUntilAsync(() => vm.IsPaused, TimeSpan.FromSeconds(5), "sim to pause");
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void PublishTracks(LiveTrafficStore store)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var t in Tracks)
        {
            var view = new LiveView(
                LiveTrafficSource.Stars,
                new LatLon(t.Lat, t.Lon),
                t.AltFt,
                t.GsKt,
                t.TrackDeg,
                t.VsFpm,
                now,
                "NCT",
                Coasting: false,
                ReceivedAtUtc: now
            );
            store.Upsert(new LiveTrack(t.Callsign, view, null, null, t.Beacon, null, $"GUFI-{t.Callsign}", null, t.Type, null));
        }
    }
}
#endif
