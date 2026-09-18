using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests;

// Covers the ground view's "Push route..." draw mode: the clicked ramp points become PUSHM targets
// verbatim (never a graph route — a tug move is free space), each target keeps the sigil that tells a
// spot apart from a gate of the same name, and the drawn preview is whatever TugMovePlanner plans for
// the goals those very tokens resolve to, refusals included.
public class GroundViewModelPushRouteTests
{
    private const double Lat0 = 37.620;
    private const double Lon0 = -122.380;

    [Fact]
    public void FinishPushRoute_SpotThenSpot_EmitsBothTargetsWithSpotSigils()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2)); // Spot "A"
        Assert.True(vm.AddPushWaypoint(3)); // Spot "B"

        Assert.Equal("PUSHM $A $B", vm.FinishPushRoute());
    }

    [Fact]
    public void FinishPushRoute_PlainIntersectionTarget_EmitsNodeRef()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2)); // Spot "A"
        Assert.True(vm.AddPushWaypoint(4)); // plain taxiway intersection, unnamed

        Assert.Equal("PUSHM $A #4", vm.FinishPushRoute());
    }

    [Fact]
    public void UndoPushWaypoint_DropsLastLegAndShrinksPreviewToTheRemainingTargets()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2));
        Assert.True(vm.AddPushWaypoint(3));
        Assert.True(vm.AddPushWaypoint(4));
        AssertSamePlan(PlanFor(vm, ac, 2, 3, 4), vm.PushRoutePreview);

        vm.UndoPushWaypoint();

        AssertSamePlan(PlanFor(vm, ac, 2, 3), vm.PushRoutePreview);
        Assert.Equal("PUSHM $A $B", vm.FinishPushRoute());
    }

    [Fact]
    public void PushRoutePreview_MatchesPlannerForTheSameInputs()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeParkedAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2));
        Assert.True(vm.AddPushWaypoint(3));

        Assert.Null(vm.PushRouteRefusal);
        AssertSamePlan(PlanFor(vm, ac, 2, 3), vm.PushRoutePreview);
    }

    [Fact]
    public void PushRoutePreview_StartsAtTheAircraftsOwnPosition()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeParkedAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2));
        Assert.True(vm.AddPushWaypoint(3));

        // The path carries its own start, so nothing else has to be held alongside it to draw the first move.
        TugPose first = vm.PushRoutePreview!.Moves[0].Samples[0];
        Assert.Equal(ac.Position.Lat, first.Position.Lat, 9);
        Assert.Equal(ac.Position.Lon, first.Position.Lon, 9);
        Assert.Equal(ac.Heading.Degrees, first.NoseTrueDeg, 9);
    }

    [Fact]
    public void OneTarget_IsAHalfBuiltRouteWithNoPreviewAndNoRefusal()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2));

        Assert.Null(vm.PushRoutePreview);
        Assert.Null(vm.PushRouteRefusal);
        Assert.Null(vm.FinishPushRoute());
        Assert.True(vm.IsDrawingRoute);
    }

    [Fact]
    public void RefusedPlan_KeepsWaypointNullsPreviewAndRefusesToSend()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2));
        Assert.True(vm.AddPushWaypoint(5)); // Parking "FAR", well past the tug-move length guard

        Assert.Null(vm.PushRoutePreview);
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
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();

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
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();

        vm.StartDrawRoute(ac);
        Assert.Equal(DrawRouteKind.Taxi, vm.DrawKind);
        Assert.Null(vm.PushRouteCallsign);
        Assert.True(vm.AddDrawWaypoint(3));

        // The taxi tool still routes through the graph: node 2 is on the path without being clicked.
        (TaxiRoute Route, string NodeRefPath, TaxiSpotDestination? Spot)? result = vm.FinishDrawRoute();
        Assert.NotNull(result);
        Assert.Equal("#2 #3", result!.Value.NodeRefPath);
        Assert.Null(vm.PushRoutePreview);
    }

    private static GroundViewModel MakeViewModel()
    {
        var connection = new ServerConnection();
        return new GroundViewModel(connection, sendCommand: (_, _, _) => Task.CompletedTask);
    }

    // What the sim would plan for the same aircraft and the same clicked nodes, through the very tokens the
    // PUSHM will carry — the preview has no planning of its own to get right.
    private static TugPlan? PlanFor(GroundViewModel vm, AircraftModel ac, params int[] nodeIds)
    {
        AirportGroundLayout layout = vm.DomainLayout!;
        var goals = new List<TugGoal>();
        foreach (int id in nodeIds)
        {
            GroundNode node = layout.Nodes[id];
            string token = node switch
            {
                { Type: GroundNodeType.Spot, Name: { Length: > 0 } spot } => $"${spot}",
                { Type: GroundNodeType.Parking or GroundNodeType.Helipad, Name: { Length: > 0 } stand } => $"@{stand}",
                _ => $"#{node.Id}",
            };
            goals.Add(GroundCommandHandler.ResolveTugGoal(layout, token)!);
        }

        var request = new TugRequest
        {
            Start = new TugPose(ac.Position, ac.Heading.Degrees),
            StartsAtStand = ac.CurrentPhase == "At Parking",
            AircraftType = ac.AircraftType,
            Goals = goals,
            FinalFacingTrueDeg = null,
            PreviousKind = null,
        };

        TugPlan? plan = TugMovePlanner.Plan(layout, request, out string refusal);
        Assert.Equal("", refusal);
        Assert.NotNull(plan);
        return plan;
    }

    private static void AssertSamePlan(TugPlan? expected, TugPlan? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.Moves.Count, actual!.Moves.Count);
        for (int i = 0; i < expected.Moves.Count; i++)
        {
            TugMove want = expected.Moves[i].Move;
            TugMove got = actual.Moves[i].Move;
            Assert.Equal(want.Kind, got.Kind);
            Assert.Equal(want.Shape, got.Shape);
            Assert.Equal(want.DwellBefore, got.DwellBefore);
        }

        double offsetFt = GeoMath.DistanceNm(expected.End.Position, actual.End.Position) * GeoMath.FeetPerNm;
        Assert.True(offsetFt < 0.01, $"end position differs by {offsetFt:F4} ft");
        Assert.Equal(expected.End.NoseTrueDeg, actual.End.NoseTrueDeg, 2);
    }

    // Stopped in the alley after a bare PUSH, nose south, with the ramp targets behind it to the north.
    private static AircraftModel MakeAircraft() =>
        new()
        {
            Callsign = "TST123",
            AircraftType = "B738",
            Position = new LatLon(Lat0, Lon0),
            Heading = new TrueHeading(180),
            CurrentPhase = "Holding After Pushback",
        };

    // The same aircraft still on stand 8B, so the plan opens with the straight push off the stand.
    private static AircraftModel MakeParkedAircraft() =>
        new()
        {
            Callsign = "TST123",
            AircraftType = "B738",
            Position = new LatLon(Lat0, Lon0),
            Heading = new TrueHeading(180),
            CurrentPhase = "At Parking",
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
