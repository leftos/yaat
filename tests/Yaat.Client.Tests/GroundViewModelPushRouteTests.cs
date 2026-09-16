using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests;

// Covers the ground view's "Push route..." draw mode: the clicked ramp points become PUSHM targets
// verbatim (never a graph route — a tug move is free space), each target keeps the sigil that tells a
// spot apart from a gate of the same name, and the drawn preview is whatever PushbackLegPlanner plans
// for the same inputs, refusals included.
public class GroundViewModelPushRouteTests
{
    private const double Lat0 = 37.620;
    private const double Lon0 = -122.380;

    [Fact]
    public void FinishPushRoute_SpotThenSpot_EmitsBothTargetsWithSpotSigils()
    {
        var vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        var ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2)); // Spot "A"
        Assert.True(vm.AddPushWaypoint(3)); // Spot "B"

        Assert.Equal("PUSHM $A $B", vm.FinishPushRoute());
    }

    [Fact]
    public void FinishPushRoute_PlainIntersectionTarget_EmitsNodeRef()
    {
        var vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        var ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2)); // Spot "A"
        Assert.True(vm.AddPushWaypoint(4)); // plain taxiway intersection, unnamed

        Assert.Equal("PUSHM $A #4", vm.FinishPushRoute());
    }

    [Fact]
    public void UndoPushWaypoint_DropsLastLegAndShrinksPreview()
    {
        var vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        var ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2));
        Assert.True(vm.AddPushWaypoint(3));
        Assert.True(vm.AddPushWaypoint(4));
        Assert.Equal(3, vm.PushRoutePreview!.Count);

        vm.UndoPushWaypoint();

        Assert.Equal(2, vm.PushRoutePreview!.Count);
        Assert.Equal("PUSHM $A $B", vm.FinishPushRoute());
    }

    [Fact]
    public void PushRoutePreview_MatchesPlannerForTheSameInputs()
    {
        var vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        var ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2));
        Assert.True(vm.AddPushWaypoint(3));

        var layout = vm.DomainLayout!;
        var targets = new List<PushbackTarget> { new(layout.Nodes[2], IsSpot: true), new(layout.Nodes[3], IsSpot: true) };
        var expected = PushbackLegPlanner.Plan(
            layout,
            ac.Position,
            ac.Heading.Degrees,
            startsAtStand: false,
            targets,
            explicitFinalFacingTrueDeg: null,
            GroundViewModel.CategoryFor(ac),
            out string refusal
        );

        Assert.Equal("", refusal);
        Assert.NotNull(expected);
        Assert.Equal(expected!.Select(l => l.Kind), vm.PushRoutePreview!.Select(l => l.Kind));
        Assert.Equal(expected.Select(l => l.EndTrueHeadingDeg), vm.PushRoutePreview!.Select(l => l.EndTrueHeadingDeg));
        Assert.Equal(ac.Position, vm.PushRouteStart);
    }

    [Fact]
    public void RefusedPlan_KeepsWaypointNullsPreviewAndRefusesToSend()
    {
        var vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        var ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2));
        Assert.True(vm.AddPushWaypoint(5)); // Parking "FAR", well past the tug-move length guard

        Assert.Null(vm.PushRoutePreview);
        Assert.Null(vm.PushRouteStart);
        Assert.NotNull(vm.PushRouteRefusal);
        Assert.Contains("2000 ft", vm.PushRouteRefusal);
        Assert.Null(vm.FinishPushRoute());
        // Still drawing, so the controller can undo the illegal leg with the refusal on screen.
        Assert.True(vm.IsDrawingRoute);

        vm.UndoPushWaypoint();

        // One target left is a half-built route, not an error: the banner clears rather than greeting the
        // controller with the planner's "needs at least two points" the moment they undo (or the moment they
        // pick "Push route…", which seeds exactly one). Nothing is sendable until a second point is picked.
        Assert.Null(vm.PushRoutePreview);
        Assert.Null(vm.PushRouteRefusal);
        Assert.Null(vm.FinishPushRoute());
        Assert.True(vm.IsDrawingRoute);
    }

    [Fact]
    public void StartPushRoute_MarksTheDrawModeAsPushAndAnchorsAtTheAircraftNode()
    {
        var vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        var ac = MakeAircraft();

        vm.StartPushRoute(ac);

        Assert.True(vm.IsDrawingRoute);
        Assert.Equal(DrawRouteKind.Push, vm.DrawKind);
        Assert.Equal("TST123", vm.PushRouteCallsign);
        Assert.Equal([1], vm.DrawWaypoints);
        Assert.Null(vm.PushRoutePreview);
        Assert.Null(vm.PushRouteRefusal);
    }

    [Fact]
    public void StartDrawRoute_StillGraphRoutesAndReportsTaxiKind()
    {
        var vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        var ac = MakeAircraft();

        vm.StartDrawRoute(ac);
        Assert.Equal(DrawRouteKind.Taxi, vm.DrawKind);
        Assert.Null(vm.PushRouteCallsign);
        Assert.True(vm.AddDrawWaypoint(3));

        // The taxi tool still routes through the graph: node 2 is on the path without being clicked.
        var result = vm.FinishDrawRoute();
        Assert.NotNull(result);
        Assert.Equal("#2 #3", result!.Value.NodeRefPath);
        Assert.Null(vm.PushRoutePreview);
    }

    private static GroundViewModel MakeViewModel()
    {
        var connection = new ServerConnection();
        return new GroundViewModel(connection, sendCommand: (_, _, _) => Task.CompletedTask);
    }

    // Stopped in the alley after a bare PUSH, nose south, with the ramp targets behind it to the north.
    private static AircraftModel MakeAircraft() =>
        new()
        {
            Callsign = "TST123",
            Position = new LatLon(Lat0, Lon0),
            Heading = new TrueHeading(180),
            CurrentPhase = "Holding After Pushback",
        };

    // A ramp lane running north: stand 8B, spots A and B, a plain intersection, and a stand a mile off.
    // Every edge is RAMP, so no leg transits movement-area pavement.
    private static GroundLayoutDto RampLayout() =>
        new(
            "TST",
            [
                new GroundNodeDto(1, Lat0, Lon0, "Parking", "8B", null, null),
                new GroundNodeDto(2, Lat0 + 0.0005, Lon0, "Spot", "A", null, null),
                new GroundNodeDto(3, Lat0 + 0.0010, Lon0, "Spot", "B", null, null),
                new GroundNodeDto(4, Lat0 + 0.0015, Lon0, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(5, Lat0 + 0.0300, Lon0, "Parking", "FAR", null, null),
            ],
            [
                new GroundEdgeDto(1, 2, "RAMP", 0.03, null),
                new GroundEdgeDto(2, 3, "RAMP", 0.03, null),
                new GroundEdgeDto(3, 4, "RAMP", 0.03, null),
                new GroundEdgeDto(4, 5, "RAMP", 1.71, null),
            ],
            null,
            null,
            null
        );
}
