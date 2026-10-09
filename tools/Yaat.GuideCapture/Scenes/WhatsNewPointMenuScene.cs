using Avalonia.Controls;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;
using Yaat.Sim;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > point menus. The OAK scenario's ground view with the
// first parked aircraft in the Aircraft List selected and the nearest taxiway
// intersection at least MinFeet from it right-clicked: the point menu titled
// with the aircraft and the place, its strip of point commands, the first one
// pointed at. The view is zoomed in between the two, and it and the selection
// are put back once the capture is taken.
internal sealed class WhatsNewPointMenuScene : ScenarioSceneBase
{
    private const double MinFeet = 600;
    private const double FeetPerNm = 6076.12;
    private const double ZoomFactor = 3;

    private MainViewModel? _vm;
    private GroundViewZoom? _zoom;

    public override string Name => "whats-new-point-menu";

    protected override int TabIndex => 1;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await SceneActions.AnswerActiveRunwaysPromptAsync(vm, TimeSpan.FromSeconds(10));
        await SceneActions.WaitUntilAsync(
            () => vm.AircraftView.OfType<AircraftModel>().Any(IsParked) && (vm.Ground.Layout is not null),
            TimeSpan.FromSeconds(10),
            "a parked aircraft and the ground layout"
        );
        _vm = vm;
        AircraftModel aircraft = vm.AircraftView.OfType<AircraftModel>().First(IsParked);
        GroundNodeDto node = NearestIntersection(vm.Ground, aircraft.Position);
        var place = new LatLon(node.Latitude, node.Longitude);
        Console.WriteLine($"  {aircraft.Callsign} point menu at node {node.Id} ({string.Join(" / ", vm.Ground.GetNodeTaxiwayNames(node.Id))})");

        var middle = new LatLon((aircraft.Position.Lat + place.Lat) / 2, (aircraft.Position.Lon + place.Lon) / 2);
        _zoom = GroundViewZoom.Apply(vm.Ground, middle, ZoomFactor);

        ContextMenu menu = await SceneActions.OpenGroundPointMenuAsync(window, vm, aircraft, node.Id, TimeSpan.FromSeconds(10));
        List<Button> strip = SceneActions.StripButtons(menu);
        if (strip.Count == 0)
        {
            throw new InvalidOperationException($"The point menu of {aircraft.Callsign} at node {node.Id} has no strip.");
        }

        SceneActions.PointAt(window, strip[0]);
    }

    // The nearest node at least MinFeet away where two or more named taxiways meet.
    private static GroundNodeDto NearestIntersection(GroundViewModel ground, LatLon from)
    {
        List<GroundNodeDto> nodes = ground.Layout?.Nodes ?? throw new InvalidOperationException("The ground view has no layout.");
        return nodes
                .Select(n => (Node: n, Feet: GeoMath.DistanceNm(from, new LatLon(n.Latitude, n.Longitude)) * FeetPerNm))
                .Where(n => (n.Feet >= MinFeet) && (ground.GetNodeTaxiwayNames(n.Node.Id).Count >= 2))
                .OrderBy(n => n.Feet)
                .ThenBy(n => n.Node.Id)
                .Select(n => n.Node)
                .FirstOrDefault()
            ?? throw new InvalidOperationException($"No taxiway intersection is {MinFeet} ft or more from the aircraft.");
    }

    public override void AfterCapture()
    {
        _zoom?.Restore();
        _zoom = null;
        _vm?.SelectedAircraft = null;
        _vm = null;
    }

    private static bool IsParked(AircraftModel aircraft) => aircraft.IsOnGround && (!aircraft.IsDelayed);
}
