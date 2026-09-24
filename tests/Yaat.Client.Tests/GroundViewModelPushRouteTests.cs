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

    // One clicked point is a complete move, not a half-built route: plain `PUSH` takes a single destination, so
    // the preview draws the leg from the aircraft to that one point the moment it is clicked.
    [Fact]
    public void OneTarget_PreviewsTheMoveFromTheAircraft()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2)); // Spot "A"

        Assert.Null(vm.PushRouteRefusal);
        Assert.NotNull(vm.PushRoutePreview);

        // The path carries its own start, so the first leg runs from where the aircraft stands.
        TugPose first = vm.PushRoutePreview!.Moves[0].Samples[0];
        Assert.Equal(ac.Position.Lat, first.Position.Lat, 9);
        Assert.Equal(ac.Position.Lon, first.Position.Lon, 9);
        Assert.Equal(ac.Heading.Degrees, first.NoseTrueDeg, 9);
    }

    [Fact]
    public void OneTarget_FinishSendsPlainPush()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2)); // Spot "A"

        Assert.Equal("PUSH $A", vm.FinishPushRoute());
        Assert.False(vm.IsDrawingRoute);
    }

    [Fact]
    public void OneUnnamedNode_FinishSendsPushNode()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(4)); // plain taxiway intersection, unnamed

        Assert.Equal("PUSH #4", vm.FinishPushRoute());
        Assert.False(vm.IsDrawingRoute);
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

        // One target left is the move itself, not an error: the banner clears and the preview redraws it, and
        // the single point is sendable as plain `PUSH` rather than greeting the controller with the planner's
        // "needs at least two points" the moment they undo (or pick "Push route…", which seeds exactly one).
        Assert.Null(vm.PushRouteRefusal);
        Assert.NotNull(vm.PushRoutePreview);
        Assert.Equal("PUSH $A", vm.FinishPushRoute());
        Assert.False(vm.IsDrawingRoute);
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

    // An E75L on stand D4 drawn to SFO spot 5A and on to 5B, with another E75L standing on 5A itself (303 ft off,
    // inside the server's neighbour range): every candidate for the first goal ends inside it, so the server refuses
    // the push naming it. The preview plans against the same parked neighbours, so it shows that refusal before the
    // command is sent. (The simulation's D2 case puts 5A 528 ft off, outside that range.)
    [Fact]
    public void PushRoutePreview_SeesAParkedNeighbourAndRefusesLikeTheServer()
    {
        if (LoadSfoLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        GroundNode spot = SfoSpot(layout, "5A");
        AircraftModel neighbour = MakeFiveAlleyNeighbour(spot.Position, "Holding After Pushback", groundSpeedKts: 0, targetSpeedKts: null);
        GroundViewModel vm = StartFiveAlleyPush(layout, neighbour);

        Assert.Null(vm.PushRoutePreview);
        Assert.NotNull(vm.PushRouteRefusal);
        Assert.Contains("SKW3400", vm.PushRouteRefusal, StringComparison.Ordinal);
    }

    // The same push with the aircraft on 5A lining up — creeping at 2 kt under a 2 kt command. It is a mover, not a
    // parked obstacle, so the planner does not sweep against it and the push plans as it would on an empty spot.
    [Fact]
    public void PushRoutePreview_IgnoresALiningUpNeighbourCreeping()
    {
        if (LoadSfoLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        GroundNode spot = SfoSpot(layout, "5A");
        AircraftModel neighbour = MakeFiveAlleyNeighbour(spot.Position, "LiningUp", groundSpeedKts: 2, targetSpeedKts: 2);
        GroundViewModel vm = StartFiveAlleyPush(layout, neighbour);

        Assert.Null(vm.PushRouteRefusal);
        Assert.NotNull(vm.PushRoutePreview);
    }

    // The same push with another E75L parked on top of the aircraft on D4: their outlines already overlap where they
    // stand, a placement error the server refuses the tow for, naming both, rather than planning around. The
    // preview shows that refusal, word for word, instead of a clean route.
    [Fact]
    public void PushRoutePreview_OverlappingANeighbour_RefusesLikeTheServer()
    {
        if (LoadSfoLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        GroundNode stand = layout.FindParkingByName("D4")!;
        AircraftModel neighbour = MakeFiveAlleyNeighbour(stand.Position, "At Parking", groundSpeedKts: 0, targetSpeedKts: null);
        GroundViewModel vm = StartFiveAlleyPush(layout, neighbour);

        Assert.Null(vm.PushRoutePreview);
        Assert.Equal("Unable, SKW3398 is up against SKW3400 — their outlines overlap; reposition one of them before towing", vm.PushRouteRefusal);
    }

    // F8 → spot 7A → spot 7B with the 7A leg forced to a pull (the sim's ForcedPushLegPlannerTests fly F8 → 7A/PULL):
    // the target carries the suffix, the next one does not, and the preview is the planner's plan for the forced goals.
    [Fact]
    public void ForcingAnEarlierLegToPull_SendsTheSuffix_AndThePreviewMatchesTheServerPlan()
    {
        if (LoadSfoLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        (GroundViewModel vm, AircraftModel ac) = StartSfoPushFromStand(layout, "F8");
        GroundNode spot7A = SfoSpot(layout, "7A");
        GroundNode spot7B = SfoSpot(layout, "7B");
        Assert.True(vm.AddPushWaypoint(spot7A.Id));
        Assert.True(vm.AddPushWaypoint(spot7B.Id));

        vm.SetPushTargetForcedKind(1, PushbackLegKind.Pull);

        Assert.Equal(PushbackLegKind.Pull, vm.PushTargetForcedKind(1));
        Assert.Null(vm.PushTargetForcedKind(2));
        Assert.Equal(PushbackLegKind.Pull, vm.PushWaypointMarks![1].ForcedKind);
        Assert.Null(vm.PushRouteRefusal);
        AssertSamePlan(
            PlanForGoals(layout, ac, [TugGoal.Spot(spot7A) with { ForcedKind = PushbackLegKind.Pull }, TugGoal.Spot(spot7B)]),
            vm.PushRoutePreview
        );
        Assert.Equal("PUSHM $7A/PULL $7B", vm.FinishPushRoute());
    }

    [Fact]
    public void LetThePlannerChoose_ClearsTheSuffix()
    {
        if (LoadSfoLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        (GroundViewModel vm, AircraftModel ac) = StartSfoPushFromStand(layout, "F8");
        GroundNode spot7A = SfoSpot(layout, "7A");
        GroundNode spot7B = SfoSpot(layout, "7B");
        Assert.True(vm.AddPushWaypoint(spot7A.Id));
        Assert.True(vm.AddPushWaypoint(spot7B.Id));
        vm.SetPushTargetForcedKind(1, PushbackLegKind.Pull);
        Assert.Equal(PushbackLegKind.Pull, vm.PushTargetForcedKind(1));

        vm.SetPushTargetForcedKind(1, null);

        Assert.Null(vm.PushTargetForcedKind(1));
        Assert.Null(vm.PushWaypointMarks![1].ForcedKind);
        AssertSamePlan(PlanForGoals(layout, ac, [TugGoal.Spot(spot7A), TugGoal.Spot(spot7B)]), vm.PushRoutePreview);
        Assert.Equal("PUSHM $7A $7B", vm.FinishPushRoute());
    }

    // A Shift+click on the ramp north of the aircraft: the point is a marked point, not the nearest node, and the
    // preview plans the goal the sim mints for that very point.
    [Fact]
    public void AFreePoint_WithoutFacing_SendsItsMarkedPointToken()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();
        var point = new LatLon(Lat0 + 0.0012, Lon0);

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushFreePoint(point.Lat, point.Lon, null));

        PushWaypointMark mark = vm.PushWaypointMarks![1];
        Assert.Equal(point.Lat, mark.Position.Lat, 9);
        Assert.Equal(point.Lon, mark.Position.Lon, 9);
        Assert.Null(mark.FacingTrueDeg);
        Assert.Null(vm.PushRouteRefusal);
        var pose = new PushFreePose(Math.Round(point.Lat, 6), Math.Round(point.Lon, 6), null);
        TugGoal goal = GroundCommandHandler.ResolveMarkedPointGoal(pose, null, ac.Position, "the marked point");
        AssertSamePlan(PlanForGoals(vm.DomainLayout!, ac, [goal]), vm.PushRoutePreview);
        Assert.Equal($"PUSH {PositionToken(point)}", vm.FinishPushRoute());
    }

    // A Shift+drag due south from the point: the facing is the drag's true bearing converted to magnetic at the point
    // and rounded to the whole degree the token carries.
    [Fact]
    public void AFreePoint_WithADragFacing_SendsTheMagneticFacing()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();
        var point = new LatLon(Lat0 + 0.0012, Lon0);
        const double dragTrueDeg = 180.0;
        LatLon releasedAt = GeoMath.ProjectPoint(point, new TrueHeading(dragTrueDeg), 100.0 / GeoMath.FeetPerNm);
        var expected = new MagneticHeading(Math.Round(MagneticDeclination.TrueToMagnetic(dragTrueDeg, point)));

        MagneticHeading facing = GroundViewModel.FreePointFacing(point, releasedAt);

        Assert.Equal(expected, facing);
        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushFreePoint(point.Lat, point.Lon, facing));
        Assert.Equal(MagneticDeclination.MagneticToTrue(facing.Degrees, ac.Position), vm.PushWaypointMarks![1].FacingTrueDeg!.Value, 9);
        Assert.Null(vm.PushRouteRefusal);
        var pose = new PushFreePose(Math.Round(point.Lat, 6), Math.Round(point.Lon, 6), facing);
        TugGoal goal = GroundCommandHandler.ResolveMarkedPointGoal(pose, null, ac.Position, "the marked point");
        AssertSamePlan(PlanForGoals(vm.DomainLayout!, ac, [goal]), vm.PushRoutePreview);
        Assert.Equal($"PUSH {PositionToken(point)}/{expected.ToDisplayString()}", vm.FinishPushRoute());
    }

    // What the view model sends is read back by the sim's own parser as the very targets drawn: sigils, suffixes and
    // the marked point's position and facing, and the text is already the command's canonical form. The route pushes
    // north to spot A, pulls south, nose first, to a marked point ahead of where the aircraft started, then pushes
    // north again to the intersection.
    [Fact]
    public void SentPushRoute_ParsesBackToTheSameTargets()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();
        var point = new LatLon(Lat0 - 0.0005, Lon0);
        MagneticHeading facing = GroundViewModel.FreePointFacing(point, GeoMath.ProjectPoint(point, new TrueHeading(180.0), 0.02));

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2)); // Spot "A"
        Assert.True(vm.AddPushFreePoint(point.Lat, point.Lon, facing));
        Assert.True(vm.AddPushWaypoint(4)); // plain taxiway intersection, unnamed
        vm.SetPushTargetForcedKind(1, PushbackLegKind.Push);
        vm.SetPushTargetForcedKind(2, PushbackLegKind.Pull);
        Assert.Null(vm.PushRouteRefusal);

        string? sent = vm.FinishPushRoute();

        Assert.NotNull(sent);
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(sent!);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        PushbackMultiCommand move = Assert.IsType<PushbackMultiCommand>(parsed.Value);
        Assert.Equal(sent, CommandDescriber.DescribeCommand(move));
        Assert.Equal(3, move.Legs.Count);
        Assert.Equal("A", move.Legs[0].Spot);
        Assert.Equal(PushbackLegKind.Push, move.Legs[0].ForcedKind);
        Assert.Equal(new PushFreePose(Math.Round(point.Lat, 6), Math.Round(point.Lon, 6), facing), move.Legs[1].FreePose);
        Assert.Equal(PushbackLegKind.Pull, move.Legs[1].ForcedKind);
        Assert.Equal(4, move.Legs[2].NodeId);
        Assert.Null(move.Legs[2].ForcedKind);
        Assert.Null(move.FinalFacing);
    }

    [Fact]
    public void UndoingAForcedOrFreeTarget_DropsItsState()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(2));
        Assert.True(vm.AddPushWaypoint(3));
        vm.SetPushTargetForcedKind(2, PushbackLegKind.Pull);
        Assert.Equal(PushbackLegKind.Pull, vm.PushTargetForcedKind(2));

        vm.UndoPushWaypoint();
        Assert.True(vm.AddPushWaypoint(3));

        Assert.Null(vm.PushTargetForcedKind(2));
        Assert.Null(vm.PushWaypointMarks![2].ForcedKind);

        Assert.True(vm.AddPushFreePoint(Lat0 + 0.0012, Lon0, new MagneticHeading(167)));
        Assert.NotNull(vm.PushWaypointMarks![3].FacingTrueDeg);
        vm.UndoPushWaypoint();
        Assert.Equal(3, vm.PushWaypointMarks!.Count);
        Assert.True(vm.AddPushWaypoint(4));

        PushWaypointMark last = vm.PushWaypointMarks![3];
        Assert.Null(last.FacingTrueDeg);
        Assert.Equal(Lat0 + 0.0015, last.Position.Lat, 9);
        Assert.Equal("PUSHM $A $B #4", vm.FinishPushRoute());
    }

    // Markers are drawn in order, so the topmost one under the pointer is the highest index hit. The last target's
    // marker opens its leg menu with "Send route" first, an earlier one's the leg menu alone. The start (index 0) is
    // not a target and opens nothing of its own.
    [Fact]
    public void RightClickOnTheLastWaypoint_OffersTheLegMenuWithSend_AndOnAnEarlierOne_WithoutIt()
    {
        GroundViewModel vm = MakeViewModel();
        vm.SetLayoutForTesting(RampLayout());
        AircraftModel ac = MakeAircraft();

        vm.StartPushRoute(ac);
        Assert.Equal((PushRightClickTarget.NewPoint, (int?)null), vm.ClassifyPushRightClick([0]));
        Assert.True(vm.AddPushWaypoint(2));
        Assert.True(vm.AddPushWaypoint(3));
        Assert.True(vm.AddPushWaypoint(4));

        Assert.Equal((PushRightClickTarget.LastWaypoint, (int?)3), vm.ClassifyPushRightClick([3]));
        Assert.Equal((PushRightClickTarget.EarlierWaypoint, (int?)1), vm.ClassifyPushRightClick([1]));
        Assert.Equal((PushRightClickTarget.EarlierWaypoint, (int?)2), vm.ClassifyPushRightClick([2]));
        Assert.Equal((PushRightClickTarget.LastWaypoint, (int?)3), vm.ClassifyPushRightClick([1, 3]));
        Assert.Equal((PushRightClickTarget.EarlierWaypoint, (int?)2), vm.ClassifyPushRightClick([0, 2]));
        Assert.Equal((PushRightClickTarget.NewPoint, (int?)null), vm.ClassifyPushRightClick([0]));
        Assert.Equal((PushRightClickTarget.NewPoint, (int?)null), vm.ClassifyPushRightClick([]));
    }

    // F8 → spot 7A alone with its leg forced to a pull (the sim's ForcedPushLegPlannerTests fly F8 → 7A/PULL): a
    // single-target route forced from the last marker's menu sends plain PUSH with the suffix, and previews the forced plan.
    [Fact]
    public void ForcingTheLastLeg_SendsItsSuffix()
    {
        if (LoadSfoLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        (GroundViewModel vm, AircraftModel ac) = StartSfoPushFromStand(layout, "F8");
        GroundNode spot7A = SfoSpot(layout, "7A");
        Assert.True(vm.AddPushWaypoint(spot7A.Id));
        Assert.Equal((PushRightClickTarget.LastWaypoint, (int?)1), vm.ClassifyPushRightClick([1]));

        vm.SetPushTargetForcedKind(1, PushbackLegKind.Pull);

        Assert.Null(vm.PushRouteRefusal);
        AssertSamePlan(PlanForGoals(layout, ac, [TugGoal.Spot(spot7A) with { ForcedKind = PushbackLegKind.Pull }]), vm.PushRoutePreview);
        Assert.Equal("PUSH $7A/PULL", vm.FinishPushRoute());
    }

    // The sim's refusal of a /PULL to an unfaced marked point 100 ft ahead of D15's nose (ForcedPushLegPlannerTests):
    // the stand needs its push-off and no pull may follow it to an unfaced point. The preview refuses in the same words.
    [Fact]
    public void ForcedPullTheSimRefuses_IsRefusedByThePreviewInTheSameWords()
    {
        if (LoadSfoLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        (GroundViewModel vm, AircraftModel ac) = StartSfoPushFromStand(layout, "D15");
        GroundNode d15 = layout.FindParkingByName("D15")!;
        LatLon ahead = GeoMath.ProjectPoint(d15.Position, d15.TrueHeading!.Value, 100.0 / GeoMath.FeetPerNm);
        Assert.True(vm.AddPushFreePoint(ahead.Lat, ahead.Lon, null));

        vm.SetPushTargetForcedKind(1, PushbackLegKind.Pull);

        var pose = new PushFreePose(Math.Round(ahead.Lat, 6), Math.Round(ahead.Lon, 6), null);
        TugGoal goal = GroundCommandHandler.ResolveMarkedPointGoal(pose, null, ac.Position, "the marked point") with
        {
            ForcedKind = PushbackLegKind.Pull,
        };
        Assert.Null(TugMovePlanner.Plan(layout, RequestFor(ac, [goal]), out string simRefusal));
        Assert.Equal("Unable, the marked point cannot be reached by a pull", simRefusal);
        Assert.Null(vm.PushRoutePreview);
        Assert.Equal(simRefusal, vm.PushRouteRefusal);
        Assert.Null(vm.FinishPushRoute());
    }

    private static (GroundViewModel Vm, AircraftModel Aircraft) StartSfoPushFromStand(AirportGroundLayout layout, string standName)
    {
        GroundNode stand = layout.FindParkingByName(standName)!;
        var ac = new AircraftModel
        {
            Callsign = "UAL462",
            AircraftType = "B738",
            Position = stand.Position,
            Heading = stand.TrueHeading!.Value,
            CurrentPhase = "At Parking",
            ParkingSpot = standName,
        };

        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);
        vm.SetAircraftProvider(() => [ac]);
        vm.StartPushRoute(ac);
        return (vm, ac);
    }

    private static GroundViewModel StartFiveAlleyPush(AirportGroundLayout layout, AircraftModel neighbour)
    {
        GroundNode stand = layout.FindParkingByName("D4")!;
        var ac = new AircraftModel
        {
            Callsign = "SKW3398",
            AircraftType = "E75L",
            Position = stand.Position,
            Heading = stand.TrueHeading!.Value,
            CurrentPhase = "At Parking",
            ParkingSpot = "D4",
        };

        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);
        vm.SetAircraftProvider(() => [ac, neighbour]);
        vm.StartPushRoute(ac);
        Assert.True(vm.AddPushWaypoint(SfoSpot(layout, "5A").Id));
        Assert.True(vm.AddPushWaypoint(SfoSpot(layout, "5B").Id));
        return vm;
    }

    private static AircraftModel MakeFiveAlleyNeighbour(LatLon position, string phase, double groundSpeedKts, double? targetSpeedKts) =>
        new()
        {
            Callsign = "SKW3400",
            AircraftType = "E75L",
            Position = position,
            Heading = new TrueHeading(118.0),
            CurrentPhase = phase,
            GroundSpeed = groundSpeedKts,
            TargetSpeedKts = targetSpeedKts,
        };

    private static GroundNode SfoSpot(AirportGroundLayout layout, string name) =>
        layout.Nodes.Values.First(n => (n.Type == GroundNodeType.Spot) && (n.Name == name));

    private static AirportGroundLayout? LoadSfoLayout()
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("SFO", File.ReadAllText(path), null, FilletMode.Standard) : null;
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

        return PlanForGoals(layout, ac, goals);
    }

    // What the sim would plan for the same aircraft and goals, with no parked neighbours.
    private static TugPlan? PlanForGoals(AirportGroundLayout layout, AircraftModel ac, List<TugGoal> goals)
    {
        TugPlan? plan = TugMovePlanner.Plan(layout, RequestFor(ac, goals), out string refusal);
        Assert.Equal("", refusal);
        Assert.NotNull(plan);
        return plan;
    }

    // A marked point's position as the command writes it, rounded to the six decimals the parser keeps.
    private static string PositionToken(LatLon point) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"~{Math.Round(point.Lat, 6):F6}/{Math.Round(point.Lon, 6):F6}");

    private static TugRequest RequestFor(AircraftModel ac, List<TugGoal> goals) =>
        new()
        {
            Start = new TugPose(ac.Position, ac.Heading.Degrees),
            StartsAtStand = ac.CurrentPhase == "At Parking",
            AircraftType = ac.AircraftType,
            Goals = goals,
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
        };

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
