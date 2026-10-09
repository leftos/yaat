using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.GuideCapture.Capture;
using Yaat.Sim;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > FOLLOWG follows over the taxiways. The OAK scenario's
// ground view with two business jets spawned at North Field's Kaiser stands:
// the leader told to taxi C B W to runway 30 and cleared across 28R, and the
// second told FOLLOWG the leader from its stand. The paused room is run
// forward until the follower has rounded a corner onto another taxiway in
// trail, and the view is zoomed so both aircraft fill it, the follower
// selected. Both aircraft's taxi routes are pinned on; a follower carries no
// assigned route, so the drawn one is the leader's path it trails along. The
// prior taxi-route modes, the view and the selection are put back once the
// capture is taken.
internal sealed class WhatsNewGroundFollowScene : ScenarioSceneBase
{
    private const string LeaderAdd = "ADD VFR S+ J @KAI5 C56X";
    private const string FollowerAdd = "ADD VFR S+ J @KAI7 C680";
    private const string LeaderTaxi = "TAXI C B W RWY 30";

    // The leader's 28R bar sits just past the C to B corner, so the leader is
    // cleared across 28R and the follower rounds the corner behind it.
    private const string LeaderCross = "CROSS 28R";

    // How much of the view's width or height the two aircraft span, and the
    // most the view is zoomed in to get there.
    private const double SpanFraction = 0.45;
    private const double MaxZoomFactor = 16;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly RoomTicks.Stage RoundCorner = new("to round a corner behind its leader", StepSeconds: 2, MaxSeconds: 400);

    private readonly Dictionary<string, TaxiRouteDisplayMode> _priorModes = new(StringComparer.Ordinal);
    private MainViewModel? _vm;
    private GroundViewZoom? _zoom;

    public override string Name => "whats-new-ground-follow";

    protected override int TabIndex => 1;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _vm = vm;
        string leader = (await SceneActions.SpawnAsync(vm, LeaderAdd, Timeout)).Callsign;
        string follower = (await SceneActions.SpawnAsync(vm, FollowerAdd, Timeout)).Callsign;
        await SceneActions.WaitUntilAsync(
            () => SceneActions.IsParked(SceneActions.Find(vm, leader)) && SceneActions.IsParked(SceneActions.Find(vm, follower)),
            Timeout,
            $"{leader} and {follower} on their stands"
        );

        await SendAcceptedAsync(vm, leader, LeaderTaxi);
        await SendAcceptedAsync(vm, leader, LeaderCross);
        await SendAcceptedAsync(vm, follower, $"FOLLOWG {leader}");
        foreach (string callsign in new[] { leader, follower })
        {
            _priorModes[callsign] = vm.Ground.GetTaxiRouteMode(callsign);
            vm.Ground.SetTaxiRouteMode(callsign, TaxiRouteDisplayMode.AlwaysShow);
        }

        var corner = new CornerWatch(vm, leader);
        AircraftModel trailing = await RoomTicks.AdvanceUntilAsync(vm, ctx, follower, corner.HasRounded, RoundCorner);
        Console.WriteLine($"  {follower} taxi route '{trailing.TaxiRoute}', active {trailing.HasActiveTaxiRoute}");
        _zoom = FrameBoth(window, vm, SceneActions.Find(vm, leader), trailing);
    }

    private static async Task SendAcceptedAsync(MainViewModel vm, string callsign, string command)
    {
        vm.SelectedAircraft = SceneActions.Find(vm, callsign);
        Dispatcher.UIThread.RunJobs();
        TerminalEntry reply = await SceneActions.SendCommandAsync(vm, callsign, command);
        Console.WriteLine($"  {callsign} {command}: {reply.Kind} {reply.Message}");
        if (reply.Kind != TerminalEntryKind.Response)
        {
            throw new InvalidOperationException($"{callsign} did not accept '{command}': {reply.Kind} {reply.Message}");
        }
    }

    // Centres the view between the two aircraft and zooms it so they span
    // SpanFraction of its width or height, whichever they span more of.
    private static GroundViewZoom FrameBoth(Window window, MainViewModel vm, AircraftModel a, AircraftModel b)
    {
        SceneActions.RenderOnce(window);
        GroundCanvas canvas = SceneActions.ShownCanvas<GroundCanvas>(window) ?? throw new InvalidOperationException("No ground view is shown.");
        (float ax, float ay) = canvas.Viewport.LatLonToScreen(a.Position.Lat, a.Position.Lon);
        (float bx, float by) = canvas.Viewport.LatLonToScreen(b.Position.Lat, b.Position.Lon);
        double span = Math.Max(Math.Abs(ax - bx) / canvas.Viewport.PixelWidth, Math.Abs(ay - by) / canvas.Viewport.PixelHeight);
        double factor = Math.Clamp(SpanFraction / Math.Max(span, 1e-3), 1, MaxZoomFactor);
        var middle = new LatLon((a.Position.Lat + b.Position.Lat) / 2, (a.Position.Lon + b.Position.Lon) / 2);
        var zoom = GroundViewZoom.Apply(vm.Ground, middle, factor);
        SceneActions.RenderOnce(window);
        Console.WriteLine($"  framed {a.Callsign} and {b.Callsign}: span {span:0.000} of the view, zoom x{factor:0.0}");
        return zoom;
    }

    // Whether the follower, taxiing, has turned off the first taxiway it
    // moved along onto another, its heading swung by at least CornerDeg.
    private sealed class CornerWatch(MainViewModel vm, string leader)
    {
        private const double MovingKnots = 3;
        private const double CornerDeg = 45;

        private string? _firstTaxiway;
        private double _firstHeading;

        public bool HasRounded(AircraftModel follower)
        {
            AircraftModel lead = SceneActions.Find(vm, leader);
            Console.WriteLine(
                $"    {lead.Callsign} {lead.CurrentPhase} '{lead.CurrentTaxiway}' {lead.GroundSpeed:0} kt; "
                    + $"{follower.Callsign} {follower.CurrentPhase} '{follower.CurrentTaxiway}' {follower.HeadingDegrees:0} {follower.GroundSpeed:0} kt"
            );
            if ((follower.GroundSpeed < MovingKnots) || string.IsNullOrEmpty(follower.CurrentTaxiway))
            {
                return false;
            }

            _firstTaxiway ??= follower.CurrentTaxiway;
            if (follower.CurrentTaxiway == _firstTaxiway)
            {
                _firstHeading = follower.HeadingDegrees;
                return false;
            }

            double turned = Math.Abs((((follower.HeadingDegrees - _firstHeading) % 360) + 540) % 360 - 180);
            return turned >= CornerDeg;
        }
    }

    public override void AfterCapture()
    {
        _zoom?.Restore();
        _zoom = null;
        foreach ((string callsign, TaxiRouteDisplayMode mode) in _priorModes)
        {
            _vm?.Ground.SetTaxiRouteMode(callsign, mode);
        }

        _priorModes.Clear();
        _vm?.SelectedAircraft = null;
        _vm = null;
    }
}
