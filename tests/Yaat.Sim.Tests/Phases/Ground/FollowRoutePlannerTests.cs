using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Phases.Ground;

/// <summary>
/// <see cref="FollowRoutePlanner.Plan"/> on the real KOAK layout: the lead taxis north-to-south along taxiway B toward 28R,
/// cleared <c>TAXI B W 30</c> with runway crossings pre-cleared, and the follower is placed around it.
/// </summary>
public class FollowRoutePlannerTests(ITestOutputHelper output)
{
    /// <summary>
    /// A follower behind the lead on B joins at the first node of the lead's path it reaches: a node of the lead's trail,
    /// behind the lead.
    /// </summary>
    [Fact]
    public void FollowerBehindOnSameTaxiway_JoinsOnTheTrail()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            run.Chain[6].Position,
            FollowCornerGeometry.Facing(run.Chain[6], run.Chain[5])
        );

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(run.Layout, follower, run.Lead));
        output.WriteLine($"merge #{plan.MergeNode}, path to merge {plan.PathToMerge.ToSummary()}");

        Assert.Contains(plan.MergeNode, TrailNodeIds(run.Lead));
        Assert.False(plan.MergeAheadOfLead);
        Assert.Equal(plan.MergeNode, plan.LeadPathFromMerge[0].FromNodeId);
        Assert.Equal(plan.MergeNode, plan.PathToMerge.Segments[^1].ToNodeId);
    }

    /// <summary>
    /// A C172 the engine taxis (<c>TAXI B W 30</c>) from the ramp node behind the B/RAMP fillet at the B/C junction, off the
    /// ramp edge and onto that fillet, has the ramp edge in its trail. Told <c>FOLLOWG</c> the lead on B there, it starts from
    /// that trail on the fillet it is partway round: the fillet, into its node on B ahead, is its follow route's first segment,
    /// never the far end of the C edge the fillet runs over (the taxi start pick, from where it would taxi round the field and
    /// back across 28R). Played on from its own point on the fillet (the navigator's no-teleport check runs every tick), over
    /// the next 10 s it never runs back along its follow route and never swings more than 90° from its heading at the command.
    /// </summary>
    [Fact]
    public void FollowerTaxiedOffTheRampOntoAFillet_StartsAheadOnB_AndNeverTurnsBack()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        RampFillet ramp = TaxiHalfwayRoundTheRampFillet(run);
        AircraftState follower = ramp.Follower;
        GroundNode start = Assert.IsType<GroundNode>(FollowRoutePlanner.StartNode(run.Layout, follower, requireAhead: true));
        output.WriteLine($"start #{start.Id}");
        Assert.NotEqual(ramp.CFar.Id, start.Id);
        Assert.Equal(run.Chain[5].Id, start.Id);
        Assert.Contains(start.Edges, e => e.MatchesTaxiway("B") && !e.IsRunwayCenterline);
        TrueHeading startHeading = follower.TrueHeading;
        Assert.True(startHeading.AbsAngleTo(new TrueHeading(GeoMath.BearingTo(follower.Position, start.Position))) <= 90.0, "the start is behind");

        CommandResult result = run.Engine.SendCommand("N2FOL", $"FOLLOWG {run.Lead.Callsign}");
        Assert.True(result.Success, result.Message);
        run.Engine.TickOneSecond();
        TaxiRoute route = Assert.IsType<TaxiRoute>(Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase).FollowRoute);
        output.WriteLine($"follow route {route.ToSummary()}");
        TaxiRouteSegment first = route.Segments[0];
        Assert.Same(ramp.Fillet, first.Edge.Edge);
        Assert.Equal((ramp.RampEnd.Id, start.Id), (first.FromNodeId, first.ToNodeId));
        AssertNeverTurnsBack(run.Engine, follower, startHeading);
    }

    /// <summary>
    /// The ramp follower above re-plans as it reaches the ramp fillet's far node on B (its lead has a new path), before its
    /// trail records an edge past the ramp edge. Another fillet leaves that ramp edge at the ramp node it came from and passes
    /// a few feet from it there; that fillet lies behind it, so the re-plan never starts on it: it is not one the follower
    /// is rounding, and entering it near its end, off its curve, would move the follower farther in one step than it drove
    /// (the navigator's no-teleport check runs every tick).
    /// </summary>
    [Fact]
    public void RampFollowerReplanningAtTheFilletsFarNode_NeverStartsOnTheFilletBehindIt()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        RampFillet ramp = TaxiHalfwayRoundTheRampFillet(run);
        AircraftState follower = ramp.Follower;
        GroundNode farNode = ramp.Fillet.OtherNode(ramp.RampEnd);
        (LatLon onTheArc, TrueHeading headingOnTheArc) = (follower.Position, follower.TrueHeading);
        follower.Position = farNode.Position;
        follower.TrueHeading = new TrueHeading(ramp.Fillet.Directed(ramp.RampEnd, farNode).ArrivalBearing);
        (int NodeA, int NodeB) rampEnds = (ramp.RampEdge.Nodes[0].Id, ramp.RampEdge.Nodes[1].Id);
        GroundArc behind = Assert.IsType<GroundArc>(TaxiEdgeLocator.FilletArcRounding(run.Layout, follower.Position, rampEnds));
        output.WriteLine($"at #{farNode.Id} the locator finds fillet #{behind.Nodes[0].Id}-#{behind.Nodes[1].Id}");
        Assert.True(behind.HasNode(ramp.RampNode.Id), "the fillet the locator finds does not leave the ramp edge at the ramp node");

        FollowRoutePlanner.RouteStart replanStart = Assert.NotNull(FollowRoutePlanner.StartOf(run.Layout, follower, requireAhead: false));
        output.WriteLine($"re-plan start #{replanStart.Node.Id}, lead-in {replanStart.LeadIn?.FromNodeId}->{replanStart.LeadIn?.ToNodeId}");
        Assert.NotSame(behind, replanStart.LeadIn?.Edge.Edge);
        Assert.NotEqual(behind.OtherNode(ramp.RampNode).Id, replanStart.Node.Id);
        (follower.Position, follower.TrueHeading) = (onTheArc, headingOnTheArc);

        CommandResult result = run.Engine.SendCommand("N2FOL", $"FOLLOWG {run.Lead.Callsign}");
        Assert.True(result.Success, result.Message);
        for (int second = 1; second <= 10; second++)
        {
            run.Engine.TickOneSecond();
            if (FollowingPhase.DrivenRouteOf(follower) is { } route)
            {
                Assert.DoesNotContain(route.Segments, s => ReferenceEquals(s.Edge.Edge, behind));
            }
        }
    }

    /// <summary>
    /// The ramp follower, stood farther than <see cref="TaxiEdgeLocator.OnFilletArcMaxOffsetFt"/> off the fillet it was
    /// rounding (nudged outward after the engine taxied it half-way round), is beside the arc rather than on it: its follow
    /// route starts as a follow off a fillet did before routes could start partway round one, from its trail's end node when
    /// that lies ahead, else from the taxi start pick, and never on the arc.
    /// </summary>
    [Fact]
    public void FollowerStandingOffTheFillet_StartsFromItsTrail_NotOnTheArc()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        RampFillet ramp = TaxiHalfwayRoundTheRampFillet(run);
        AircraftState follower = ramp.Follower;
        const double nudgeFt = 9.0;
        follower.Position = GeoMath.ProjectPoint(
            follower.Position,
            new TrueHeading((follower.TrueHeading.Degrees + 90.0) % 360.0),
            nudgeFt / GeoMath.FeetPerNm
        );
        (int NodeA, int NodeB) rampEnds = (ramp.RampEdge.Nodes[0].Id, ramp.RampEdge.Nodes[1].Id);
        Assert.Same(ramp.Fillet, TaxiEdgeLocator.FilletArcRounding(run.Layout, follower.Position, rampEnds));
        double offArcFt = Assert.NotNull(TaxiEdgeLocator.InsideArcDistanceFt(ramp.Fillet, follower.Position));
        output.WriteLine($"{offArcFt:F1} ft off the fillet #{ramp.RampEnd.Id}-#{ramp.Fillet.OtherNode(ramp.RampEnd).Id}");
        Assert.True(offArcFt > TaxiEdgeLocator.OnFilletArcMaxOffsetFt, $"only {offArcFt:F1} ft off the fillet");

        bool rampEndAhead = follower.TrueHeading.AbsAngleTo(new TrueHeading(GeoMath.BearingTo(follower.Position, ramp.RampEnd.Position))) <= 90.0;
        GroundNode? expected = rampEndAhead ? ramp.RampEnd : run.Layout.FindTaxiStartNode(follower.Position, follower.TrueHeading);
        FollowRoutePlanner.RouteStart start = Assert.NotNull(FollowRoutePlanner.StartOf(run.Layout, follower, requireAhead: true));
        output.WriteLine($"trail end #{ramp.RampEnd.Id} ahead: {rampEndAhead}; start #{start.Node.Id}");
        Assert.Equal(expected?.Id, start.Node.Id);
        Assert.NotSame(ramp.Fillet, start.LeadIn?.Edge.Edge);
    }

    /// <summary>
    /// The ramp follower half-way round the fillet with no route left to drive (its taxi route cleared, as an aircraft stopped
    /// on the fillet with nothing assigned) has only its pose and trail to go by: standing on the curve, its follow route still
    /// starts on that fillet, into its node on B.
    /// </summary>
    [Fact]
    public void FollowerOnTheFilletDrivingNoRoute_StartsOnTheArc()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        RampFillet ramp = TaxiHalfwayRoundTheRampFillet(run);
        AircraftState follower = ramp.Follower;
        follower.Ground.AssignedTaxiRoute = null;
        Assert.Null(FollowingPhase.DrivenRouteOf(follower));

        FollowRoutePlanner.RouteStart start = Assert.NotNull(FollowRoutePlanner.StartOf(run.Layout, follower, requireAhead: true));
        output.WriteLine($"start #{start.Node.Id}, lead-in {start.LeadIn?.FromNodeId}->{start.LeadIn?.ToNodeId}");
        Assert.Equal(run.Chain[5].Id, start.Node.Id);
        Assert.Same(ramp.Fillet, start.LeadIn?.Edge.Edge);
        Assert.Equal(ramp.RampEnd.Id, start.LeadIn?.FromNodeId);
    }

    /// <summary>
    /// A C172 the engine taxied (<c>TAXI B W 30</c>) off the ramp edge at <c>Chain[6]</c> and half-way round the fillet from
    /// there to <c>Chain[5]</c> on B: the follower, the fillet, the ramp edge with its two nodes, and the far end of the C edge
    /// the fillet's chord runs over (the taxi start pick from the chord).
    /// </summary>
    private sealed record RampFillet(
        AircraftState Follower,
        GroundArc Fillet,
        GroundEdge RampEdge,
        GroundNode RampNode,
        GroundNode RampEnd,
        GroundNode CFar
    );

    private RampFillet TaxiHalfwayRoundTheRampFillet(FollowCornerGeometry.LeadRun run)
    {
        FilletChord pose = PoseOnFilletChord(run);
        GroundNode rampEnd = run.Chain[6];
        GroundNode rampNode = pose.RampEdge.OtherNode(rampEnd);
        Assert.True(pose.RampEdge.IsRamp, $"#{rampNode.Id}-#{rampEnd.Id} is not a ramp edge");
        GroundArc fillet = rampEnd.Edges.OfType<GroundArc>().First(e => e.OtherNode(rampEnd).Id == run.Chain[5].Id);
        AircraftState follower = FollowCornerGeometry.AddTaxiing((run.Engine, run.Layout), "N2FOL", "C172", (rampNode, rampEnd), "TAXI B W 30");
        output.WriteLine($"follower from #{rampNode.Id}: {follower.Ground.AssignedTaxiRoute?.ToSummary()}");
        double halfFilletFt = fillet.DistanceNm * GeoMath.FeetPerNm / 2.0;
        for (int second = 0; (second < 120) && (DistanceFt(follower.Position, rampEnd.Position) < halfFilletFt); second++)
        {
            run.Engine.TickOneSecond();
        }

        string trailText = string.Join(", ", follower.Ground.TaxiEdgeTrail.Edges.Select(e => $"#{e.NodeA}-#{e.NodeB}"));
        output.WriteLine($"follower {DistanceFt(follower.Position, rampEnd.Position):F0} ft past #{rampEnd.Id} on the fillet; trail {trailText}");
        Assert.True(DistanceFt(follower.Position, rampEnd.Position) >= halfFilletFt, "the follower never got half-way round the fillet");
        Assert.Contains(follower.Ground.TaxiEdgeTrail.Edges, e => e.Is(pose.RampEdge));
        return new RampFillet(follower, fillet, pose.RampEdge, rampNode, rampEnd, pose.CFar);
    }

    /// <summary>
    /// Ticks <paramref name="engine"/> 10 s, asserting each second that <paramref name="follower"/> is no farther back along its
    /// follow route than the second before (while it drives the same route) and within 90° of <paramref name="startHeading"/>.
    /// </summary>
    private void AssertNeverTurnsBack(SimulationEngine engine, AircraftState follower, TrueHeading startHeading)
    {
        TaxiRoute? lastRoute = null;
        double lastProgressFt = 0.0;
        for (int second = 1; second <= 10; second++)
        {
            engine.TickOneSecond();
            double swingDeg = follower.TrueHeading.AbsAngleTo(startHeading);
            Assert.True(swingDeg <= 90.0, $"t={second}s: heading {follower.TrueHeading.Degrees:F0} swung {swingDeg:F0} deg");
            if (FollowingPhase.DrivenRouteOf(follower) is not { IsComplete: false } route)
            {
                lastRoute = null;
                continue;
            }

            int index = route.CurrentSegmentIndex;
            double progressFt = route.PrefixDistanceFt(index + 1) - DistanceFt(follower.Position, route.Segments[index].Edge.ToNode.Position);
            output.WriteLine($"t={second}s: {follower.GroundSpeed:F1} kt, hdg {follower.TrueHeading.Degrees:F0}, {progressFt:F0} ft along");
            if (ReferenceEquals(route, lastRoute))
            {
                Assert.True(progressFt >= lastProgressFt - 1.0, $"t={second}s: progress {progressFt:F1} ft fell from {lastProgressFt:F1} ft");
            }

            lastRoute = route;
            lastProgressFt = progressFt;
        }
    }

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>
    /// A C172 the engine pushes back from a KOAK stand (<c>PUSH</c>) ends tail-first on the push's last trail edge: the end of
    /// that edge the trail's order says it travelled toward lies behind its nose, so its follow route starts at the taxi start
    /// pick (<see cref="AirportGroundLayout.FindTaxiStartNode"/>) instead. A re-plan (<see cref="FollowRoutePlanner.Replan"/>)
    /// takes the trail's end as it is, behind the nose.
    /// </summary>
    [Fact]
    public void PushedBackAircraft_TrailEndBehindTheNose_FirstPlanStartsAtTheTaxiStartPick_ReplanAtTheTrailEnd()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        (AirportGroundLayout Layout, AircraftState Pushed)? found = null;
        foreach (GroundNode stand in layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Edges.Count > 0)).OrderBy(n => n.Id))
        {
            (SimulationEngine Engine, AirportGroundLayout Layout) candidate = Assert.NotNull(
                FollowCornerGeometry.NewEngine(output, autoCross: false)
            );
            AircraftState aircraft = PushFrom(candidate, stand);
            if (
                (TravelledTowardEnd(candidate.Layout, aircraft) is { } behind)
                && (candidate.Layout.FindTaxiStartNode(aircraft.Position, aircraft.TrueHeading)?.Id != behind.Id)
            )
            {
                found = (candidate.Layout, aircraft);
                break;
            }
        }

        (AirportGroundLayout Layout, AircraftState Pushed) setup = Assert.NotNull(found);
        AircraftState pushed = setup.Pushed;
        IReadOnlyList<TaxiTrailEdge> trail = pushed.Ground.TaxiEdgeTrail.Edges;
        output.WriteLine(
            $"after the push: {pushed.Phases?.CurrentPhase?.Name}, trail {string.Join(", ", trail.Select(e => $"#{e.NodeA}-#{e.NodeB}"))}"
        );
        GroundEdge newest = Assert.IsType<GroundEdge>(trail[^1].Resolve(setup.Layout));
        GroundNode end = Assert.IsType<GroundNode>(TravelledTowardEnd(setup.Layout, pushed));
        double offFt = GeoMath.DistanceToSegmentFt(pushed.Position, newest.Nodes[0].Position, newest.Nodes[1].Position);
        double toEndDeg = pushed.TrueHeading.AbsAngleTo(new TrueHeading(GeoMath.BearingTo(pushed.Position, end.Position)));
        GroundNode? pick = setup.Layout.FindTaxiStartNode(pushed.Position, pushed.TrueHeading);
        output.WriteLine($"trail end #{end.Id} {toEndDeg:F0} deg off the nose, {offFt:F1} ft off the edge; taxi start pick #{pick?.Id}");

        Assert.True(offFt <= TaxiEdgeLocator.OnTaxiwayMaxOffsetFt, "the pushed aircraft is off its newest trail edge");
        Assert.True(toEndDeg > 90.0, "the trail end lies ahead of the nose");
        Assert.NotEqual(end.Id, pick?.Id);
        GroundNode start = Assert.IsType<GroundNode>(FollowRoutePlanner.StartNode(setup.Layout, pushed, requireAhead: true));
        Assert.Equal(pick?.Id, start.Id);
        double toStartDeg = pushed.TrueHeading.AbsAngleTo(new TrueHeading(GeoMath.BearingTo(pushed.Position, start.Position)));
        Assert.True(toStartDeg <= 90.0, $"the planned start #{start.Id} is {toStartDeg:F0} deg off the nose");

        // A re-plan of a follow already driving its route has no ahead check: it starts at the trail's end, behind the nose.
        Assert.Equal(end.Id, FollowRoutePlanner.StartNode(setup.Layout, pushed, requireAhead: false)?.Id);
    }

    /// <summary>
    /// The end of <paramref name="aircraft"/>'s newest trail edge it travelled toward by the trail's order — the one that edge
    /// does not share with the edge recorded before it — when that end lies behind its nose; null otherwise, or with fewer than
    /// two edges, or two that do not meet.
    /// </summary>
    private static GroundNode? TravelledTowardEnd(AirportGroundLayout layout, AircraftState aircraft)
    {
        IReadOnlyList<TaxiTrailEdge> trail = aircraft.Ground.TaxiEdgeTrail.Edges;
        if ((trail.Count < 2) || (trail[^1].Resolve(layout) is not { } newest))
        {
            return null;
        }

        TaxiTrailEdge previous = trail[^2];
        bool firstShared = (newest.Nodes[0].Id == previous.NodeA) || (newest.Nodes[0].Id == previous.NodeB);
        bool secondShared = (newest.Nodes[1].Id == previous.NodeA) || (newest.Nodes[1].Id == previous.NodeB);
        if (firstShared == secondShared)
        {
            return null;
        }

        GroundNode end = firstShared ? newest.Nodes[1] : newest.Nodes[0];
        double toEndDeg = aircraft.TrueHeading.AbsAngleTo(new TrueHeading(GeoMath.BearingTo(aircraft.Position, end.Position)));
        return toEndDeg > 90.0 ? end : null;
    }

    /// <summary>
    /// A C172 parked at <paramref name="stand"/>, added to the engine, told <c>PUSH</c> and ticked until the push is done: the
    /// first stand, by node id, whose push leaves the travelled-toward end of its newest trail edge behind its nose
    /// (<see cref="TravelledTowardEnd"/>) is the one the test reads.
    /// </summary>
    private static AircraftState PushFrom((SimulationEngine Engine, AirportGroundLayout Layout) setup, GroundNode stand)
    {
        AircraftState aircraft = FollowCornerGeometry.Spawn(FollowCornerGeometry.AirportId, "N3PSH", "C172", stand.Position, new TrueHeading(0));
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, setup.Layout));
        aircraft.Ground.Layout = setup.Layout;
        setup.Engine.World.AddAircraft(aircraft);
        CommandResult result = setup.Engine.SendCommand(aircraft.Callsign, "PUSH");
        Assert.True(result.Success, result.Message);
        for (int second = 0; (second < 300) && ((second < 2) || (aircraft.Phases?.CurrentPhase is PushbackPhase)); second++)
        {
            setup.Engine.TickOneSecond();
        }

        return aircraft;
    }

    /// <summary>
    /// A C172 the engine taxis down B to 28R, clears for takeoff and stops on the runway with its takeoff clearance cancelled
    /// mid-roll: the trail is not written on a runway roll, so its newest edge, B into the bar, lies far behind it, and its
    /// follow route starts at the taxi start pick (<see cref="AirportGroundLayout.FindTaxiStartNode"/>). Told <c>FOLLOWG</c> a
    /// lead on B, it drives off the runway.
    /// </summary>
    [Fact]
    public void RejectedTakeoff_NewestTrailEdgeFarBehind_StartsAtTheTaxiStartPick()
    {
        if (FollowCornerGeometry.NewEngine(output, autoCross: false) is not { } setup)
        {
            return;
        }

        List<GroundNode> chain = FollowCornerGeometry.BChain(setup.Layout);
        AircraftState departure = FollowCornerGeometry.AddTaxiing(setup, "N3RTO", "C172", (chain[3], chain[2]), "TAXI B 28R");
        AircraftState lead = FollowCornerGeometry.AddTaxiing(setup, "N1LED", "C172", (chain[6], chain[5]), "TAXI B 28R");
        Tick(setup.Engine, 120, () => departure.Phases?.CurrentPhase is HoldingShortPhase or HoldingInPositionPhase);
        CommandResult cto = setup.Engine.SendCommand(departure.Callsign, "CTO");
        Assert.True(cto.Success, cto.Message);
        Tick(setup.Engine, 120, () => (departure.Phases?.CurrentPhase is TakeoffPhase) && (departure.GroundSpeed >= 30.0));
        CommandResult cancel = setup.Engine.SendCommand(departure.Callsign, "CTOC");
        Assert.True(cancel.Success, cancel.Message);
        Tick(setup.Engine, 120, () => departure.GroundSpeed <= 0.0);

        TaxiTrailEdge newest = Assert.IsType<TaxiTrailEdge>(departure.Ground.TaxiEdgeTrail.Newest);
        GroundEdge edge = Assert.IsType<GroundEdge>(newest.Resolve(setup.Layout));
        double offFt = GeoMath.DistanceToSegmentFt(departure.Position, edge.Nodes[0].Position, edge.Nodes[1].Position);
        GroundNode? pick = setup.Layout.FindTaxiStartNode(departure.Position, departure.TrueHeading);
        output.WriteLine(
            $"{departure.Phases?.CurrentPhase?.Name} {departure.GroundSpeed:F1} kt, "
                + $"{offFt:F0} ft off #{newest.NodeA}-#{newest.NodeB}; pick {pick?.Id}"
        );
        Assert.True(offFt > TaxiEdgeLocator.OnTaxiwayMaxOffsetFt, "the stopped departure is still on its newest trail edge");
        Assert.Equal(pick?.Id, FollowRoutePlanner.StartNode(setup.Layout, departure, requireAhead: true)?.Id);

        CommandResult follow = setup.Engine.SendCommand(departure.Callsign, $"FOLLOWG {lead.Callsign}");
        Assert.True(follow.Success, follow.Message);
        setup.Engine.TickOneSecond();
        FollowingPhase phase = Assert.IsType<FollowingPhase>(departure.Phases?.CurrentPhase);
        output.WriteLine($"follow route {phase.FollowRoute?.ToSummary()}; clearing route {phase.ClearingRoute?.ToSummary()}");
        Assert.Null(phase.ClearingRoute);
        TaxiRoute route = Assert.IsType<TaxiRoute>(phase.FollowRoute);
        TaxiRouteSegment first = route.Segments[0];
        Assert.True((first.FromNodeId == pick?.Id) || (first.ToNodeId == pick?.Id), $"the follow route does not start at #{pick?.Id}");
    }

    private static void Tick(SimulationEngine engine, int maxSeconds, Func<bool> until)
    {
        for (int second = 0; (second < maxSeconds) && !until(); second++)
        {
            engine.TickOneSecond();
        }

        Assert.True(until(), $"condition not met within {maxSeconds} s");
    }

    /// <summary>
    /// The same follower with an empty trail has nothing of its own to start from: its plan starts where a taxi from its pose
    /// does (<see cref="AirportGroundLayout.FindTaxiStartNode"/>), at the far end of the C edge it stands on.
    /// </summary>
    [Fact]
    public void FollowerOnAFilletChordBesideAnotherTaxiway_EmptyTrail_StartsAtTheTaxiStartPick()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        FilletChord pose = PoseOnFilletChord(run);
        Assert.Empty(pose.Follower.Ground.TaxiEdgeTrail.Edges);

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(run.Layout, pose.Follower, run.Lead));
        string pathText = PathText(plan.PathToMerge.Segments.Select(s => s.Edge));
        output.WriteLine($"merge #{plan.MergeNode}, path to merge {pathText}");

        TaxiRouteSegment first = plan.PathToMerge.Segments[0];
        Assert.True(
            (first.FromNodeId == pose.CFar.Id) || (first.ToNodeId == pose.CFar.Id),
            $"the plan does not start at #{pose.CFar.Id}, the taxi start pick: {pathText}"
        );
    }

    /// <summary>
    /// A follower on the B/RAMP fillet chord at the B/C junction, the far end of the C edge it stands on, and the ramp edge it
    /// came off.
    /// </summary>
    private sealed record FilletChord(AircraftState Follower, GroundNode CFar, GroundEdge RampEdge);

    /// <summary>
    /// A C172 half-way along the chord of the B/RAMP fillet from <c>Chain[6]</c> (on the ramp side of the B/C junction) to
    /// <c>Chain[5]</c> (on B), facing <c>Chain[5]</c>: the chord runs over the C edge that leaves the junction toward the 28R
    /// side, so a taxi from the pose starts at that C edge's far end. The ramp edge is <c>Chain[6]</c>'s straight edge away from
    /// the junction.
    /// </summary>
    private FilletChord PoseOnFilletChord(FollowCornerGeometry.LeadRun run)
    {
        GroundNode rampEnd = run.Chain[6];
        GroundNode onB = run.Chain[5];
        Assert.Contains(rampEnd.Edges, e => (e is GroundArc) && (e.OtherNode(rampEnd).Id == onB.Id));
        GroundNode junction = onB
            .Edges.OfType<GroundEdge>()
            .Where(e => e.MatchesTaxiway("B"))
            .Select(e => e.OtherNode(onB))
            .First(n => n.Edges.Any(c => c.MatchesTaxiway("C")));
        TrueHeading heading = FollowCornerGeometry.Facing(rampEnd, onB);
        GroundNode cFar = junction
            .Edges.OfType<GroundEdge>()
            .Where(e => e.MatchesTaxiway("C"))
            .Select(e => e.OtherNode(junction))
            .OrderBy(n => Math.Abs(GeoMath.SignedBearingDifference(heading.Degrees, GeoMath.BearingTo(junction.Position, n.Position))))
            .First();
        GroundEdge rampEdge = rampEnd.Edges.OfType<GroundEdge>().First(e => e.OtherNode(rampEnd).Id != junction.Id);
        LatLon at = FollowCornerGeometry.Between(rampEnd.Position, onB.Position, 0.5);
        double offCFt = GeoMath.DistanceToSegmentFt(at, junction.Position, cFar.Position);
        output.WriteLine(
            $"on the #{rampEnd.Id}->#{onB.Id} chord, {offCFt:F1} ft off C #{junction.Id}->#{cFar.Id}; came off #{rampEdge.OtherNode(rampEnd).Id}"
        );
        Assert.Equal(cFar.Id, run.Layout.FindTaxiStartNode(at, heading)?.Id);
        return new FilletChord(FollowCornerGeometry.Spawn(FollowCornerGeometry.AirportId, "N2FOL", "C172", at, heading), cFar, rampEdge);
    }

    /// <summary>
    /// A follower mid-edge behind its lead on the lead's own edge — KOAK's longest straight B edge — both facing the same way,
    /// joins on that edge with no path to the merge: the merge is the edge's start in the lead's direction, behind the lead,
    /// and the lead's path from it starts with the shared edge — not the edge's far end past the lead, where the follower's
    /// taxi would otherwise start.
    /// </summary>
    [Fact]
    public void FollowerBehindLeadMidEdgeOnItsEdge_JoinsOnThatEdge()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        SameEdge s = FollowBehindOnLongestB(layout, followerFacesLead: true);

        Assert.Equal(s.From.Id, s.Plan.MergeNode);
        Assert.False(s.Plan.MergeAheadOfLead);
        Assert.Equal((s.From.Id, s.To.Id), (s.Plan.LeadPathFromMerge[0].FromNodeId, s.Plan.LeadPathFromMerge[0].ToNodeId));
        Assert.Empty(s.Plan.PathToMerge.Segments);
        Assert.Equal(s.From.Id, s.Plan.LeadEdgeIntoMerge?.ToNodeId);
    }

    /// <summary>
    /// A follower behind its lead on the lead's own edge but facing away from it joins as one facing it does: on that edge,
    /// at its start, with no path to the merge. This is the pose the follow must turn about from before it falls in behind.
    /// </summary>
    [Fact]
    public void FollowerBehindLeadOnItsEdgeFacingAway_JoinsOnThatEdge_TurnAboutPose()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        SameEdge s = FollowBehindOnLongestB(layout, followerFacesLead: false);

        Assert.Equal(s.From.Id, s.Plan.MergeNode);
        Assert.False(s.Plan.MergeAheadOfLead);
        Assert.Equal((s.From.Id, s.To.Id), (s.Plan.LeadPathFromMerge[0].FromNodeId, s.Plan.LeadPathFromMerge[0].ToNodeId));
        Assert.Empty(s.Plan.PathToMerge.Segments);
    }

    /// <summary>
    /// A follower that joined behind its lead on the lead's edge is past the merge, the edge's start: its distance to the merge
    /// is minus its way along the edge.
    /// </summary>
    [Fact]
    public void FollowerToMerge_OnTheLeadsPath_IsMinusItsWayAlong()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        SameEdge s = FollowBehindOnLongestB(layout, followerFacesLead: true);
        (LatLon at, TrueHeading heading) = PointAlong(s.Plan.LeadPathFromMerge, 200.0);
        AircraftState probe = FollowCornerGeometry.Spawn(FollowCornerGeometry.AirportId, "N2FOL", "C172", at, heading);

        double? toMergeFt = FollowRoutePlanner.FollowerToMergeFt(layout, s.Plan, probe);

        Assert.NotNull(toMergeFt);
        Assert.Equal(-200.0, toMergeFt.Value, 0.5);
    }

    /// <summary>
    /// A follower past the merge whose position the path lookup misses — here 60 ft off the shared edge's centreline — has no
    /// distance to the merge, and so no along-path gap: never the straight distance back to the merge, which would read the
    /// gap as if it were still short of it.
    /// </summary>
    [Fact]
    public void FollowerToMerge_PastTheMergeOffThePath_IsNull()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        SameEdge s = FollowBehindOnLongestB(layout, followerFacesLead: true);
        (LatLon at, TrueHeading heading) = PointAlong(s.Plan.LeadPathFromMerge, 200.0);
        AircraftState probe = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            GeoMath.ProjectPoint(at, heading + 90.0, 60.0 / GeoMath.FeetPerNm),
            heading
        );

        double? toMergeFt = FollowRoutePlanner.FollowerToMergeFt(layout, s.Plan, probe);
        PathPosition? leadAt = FollowRoutePlanner.LocateOnPath(layout, s.Plan.LeadPathFromMerge, s.Lead);
        output.WriteLine($"to merge {toMergeFt}, lead at {leadAt}");

        Assert.Null(toMergeFt);
        Assert.NotNull(leadAt);
        Assert.Null(FollowRoutePlanner.AlongPathGapFt(toMergeFt, s.Plan.LeadPathFromMerge, leadAt, "C172", "B738"));
    }

    /// <summary>
    /// A follower standing on a node of the lead's trail, facing the lead's way, is at the merge already: it joins there with no
    /// path to the merge, behind the lead.
    /// </summary>
    [Fact]
    public void FollowerOnATrailNode_JoinsThereWithNoPathToTheMerge()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        GroundNode node = run.Chain[3];
        Assert.Contains(node.Id, TrailNodeIds(run.Lead));
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            node.Position,
            FollowCornerGeometry.Facing(node, run.Chain[2])
        );

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(run.Layout, follower, run.Lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}; path to merge {plan.PathToMerge.ToSummary()}");

        Assert.Equal(node.Id, plan.MergeNode);
        Assert.False(plan.MergeAheadOfLead);
        Assert.Equal(node.Id, plan.LeadPathFromMerge[0].FromNodeId);
        Assert.Empty(plan.PathToMerge.Segments);
    }

    /// <summary>
    /// A follower on a crossing taxiway, short of a junction the lead has still to reach on its route, joins at that junction,
    /// ahead of the lead.
    /// </summary>
    [Fact]
    public void FollowerOnCrossingTaxiwayAhead_JoinsAheadOfTheLead()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        TaxiRoute route = Assert.IsType<TaxiRoute>(run.Lead.Ground.AssignedTaxiRoute);
        HashSet<int> routeNodes = [.. route.Segments.SelectMany(s => new[] { s.FromNodeId, s.ToNodeId })];
        (GroundNode junction, GroundNode far, GroundEdge crossing) = route
            .Segments.Skip(route.CurrentSegmentIndex + 3)
            .Select(s => s.Edge.FromNode)
            .SelectMany(node => node.Edges.OfType<GroundEdge>().Select(edge => (Junction: node, Far: edge.OtherNode(node), Edge: edge)))
            .First(c =>
                !c.Edge.IsRunwayCenterline && !c.Edge.IsRamp && !routeNodes.Contains(c.Far.Id) && (c.Edge.DistanceNm * GeoMath.FeetPerNm >= 50.0)
            );
        output.WriteLine($"junction #{junction.Id} on B, crossing {crossing.TaxiwayName} from #{far.Id}");
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            far.Position,
            FollowCornerGeometry.Facing(far, junction)
        );

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(run.Layout, follower, run.Lead));
        output.WriteLine($"merge #{plan.MergeNode}, path to merge {plan.PathToMerge.ToSummary()}");

        Assert.Equal(junction.Id, plan.MergeNode);
        Assert.True(plan.MergeAheadOfLead);
        Assert.Equal(plan.MergeNode, plan.PathToMerge.Segments[^1].ToNodeId);
        Assert.Equal(route.Segments[^1].ToNodeId, plan.LeadPathFromMerge[^1].ToNodeId);
    }

    /// <summary>
    /// A lead with no route left to read (here, itself following) still has the edge it is on: a follower on that edge, nearer
    /// the end the lead faces than the lead is, is ahead of it.
    /// </summary>
    [Fact]
    public void FollowerAheadOnLeadsEdge_LeadWithNoRoute_IsAhead()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        AircraftState third = FollowCornerGeometry.SpawnAtStand(run.Layout, "N3THR");
        run.Engine.World.AddAircraft(third);
        CommandResult follow = run.Engine.SendCommand(run.Lead.Callsign, $"FOLLOWG {third.Callsign}");
        Assert.True(follow.Success, follow.Message);

        GroundEdge edge = Assert.IsType<GroundEdge>(TaxiEdgeLocator.EdgeUnder(run.Layout, run.Lead.Position, null));
        GroundNode faced = FacedEnd(edge, run.Lead);
        GroundNode back = edge.OtherNode(faced);
        run.Lead.Position = FollowCornerGeometry.Between(back.Position, faced.Position, 0.3);
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            FollowCornerGeometry.Between(back.Position, faced.Position, 0.7),
            FollowCornerGeometry.Facing(faced, back)
        );

        Assert.IsType<FollowRoutePlan.FollowerAhead>(FollowRoutePlanner.Plan(run.Layout, follower, run.Lead));
    }

    /// <summary>
    /// A lead stopped at the end of a route segment, part way through a sharp turn that has swung its nose more than 90° off the
    /// segment, is still going the segment's way: the planner orients the edge it is on from its route, not its heading, so the
    /// lead's path runs on along the route without doubling back over that edge.
    /// </summary>
    [Fact]
    public void LeadMidTurnAtSegmentEnd_EdgeOrientedByTheRoute()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        TaxiRoute route = Assert.IsType<TaxiRoute>(run.Lead.Ground.AssignedTaxiRoute);
        TaxiRouteSegment segment = route.Segments[PutLeadMidTurn(run, _ => true)];
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            run.Chain[6].Position,
            FollowCornerGeometry.Facing(run.Chain[6], run.Chain[5])
        );

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(run.Layout, follower, run.Lead));
        output.WriteLine($"segment #{segment.FromNodeId}>#{segment.ToNodeId}; lead path {PathText(plan.LeadPathFromMerge)}");

        Assert.False(plan.MergeAheadOfLead);
        Assert.Contains(plan.LeadPathFromMerge, e => (e.FromNodeId == segment.FromNodeId) && (e.ToNodeId == segment.ToNodeId));
        Assert.DoesNotContain(plan.LeadPathFromMerge, e => (e.FromNodeId == segment.ToNodeId) && (e.ToNodeId == segment.FromNodeId));
        Assert.Equal(route.Segments[^1].ToNodeId, plan.LeadPathFromMerge[^1].ToNodeId);
    }

    /// <summary>
    /// A lead whose route index still names the segment it has just left — held there through an entry-alignment turn, or
    /// sampled onto the next edge before it reaches the node — has passed that segment: a follower on it is not ahead of the
    /// lead, and joins its path.
    /// </summary>
    [Fact]
    public void FollowerOnASegmentTheLeadPassedBeforeItsIndexMoved_IsJoinable()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        TaxiRoute route = Assert.IsType<TaxiRoute>(run.Lead.Ground.AssignedTaxiRoute);
        int index = PutLeadMidTurn(run, i => (i > 0) && IsStraightTaxi(route.Segments[i - 1]));
        route.CurrentSegmentIndex = index - 1;
        TaxiRouteSegment passed = route.Segments[index - 1];
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            FollowCornerGeometry.Between(passed.Edge.FromNode.Position, passed.Edge.ToNode.Position, 0.5),
            FollowCornerGeometry.Facing(passed.Edge.FromNode, passed.Edge.ToNode)
        );

        FollowRoutePlan plan = FollowRoutePlanner.Plan(run.Layout, follower, run.Lead);
        output.WriteLine($"lead on segment {index}, index {route.CurrentSegmentIndex}; follower on #{passed.FromNodeId}>#{passed.ToNodeId}; {plan}");

        Assert.IsType<FollowRoutePlan.Joinable>(plan);
    }

    /// <summary>
    /// A lead pushed tail-first off a side edge and out along a taxiway, then taxied forward back the other way, has a trail
    /// that runs out and back over the same edge (A-J, J-W1 pushed, then J-E1 taxied; the trail collapses the repeat of J-W1):
    /// the lead's path keeps the trail only from where it turned, so it never goes out and back. A pushback writes trail edges
    /// as a taxi does; the trail here is recorded directly, edge by edge, as that push and taxi record it.
    /// </summary>
    [Fact]
    public void TrailOfAPushedThenTaxiingLead_HasNoUTurn()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        PushGeometry push = FindPushGeometry(layout);
        output.WriteLine($"A2 #{push.A2.Id}, A #{push.A.Id}, J #{push.J.Id}, W1 #{push.W1.Id}, E1 #{push.E1.Id}");

        AircraftState lead = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N1LED",
            "C560",
            FollowCornerGeometry.Between(push.J.Position, push.E1.Position, 0.5),
            FollowCornerGeometry.Facing(push.J, push.E1)
        );
        lead.Ground.TaxiEdgeTrail.Record(push.Side, push.A);
        lead.Ground.TaxiEdgeTrail.Record(FollowCornerGeometry.EdgeBetween(push.J, push.W1), push.J);
        lead.Ground.TaxiEdgeTrail.Record(FollowCornerGeometry.EdgeBetween(push.J, push.E1), push.J);
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            FollowCornerGeometry.Between(push.A2.Position, push.A.Position, 0.5),
            FollowCornerGeometry.Facing(push.A2, push.A)
        );

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(layout, follower, lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");

        List<(int, int)> undirected =
        [
            .. plan.LeadPathFromMerge.Select(e => (Math.Min(e.FromNodeId, e.ToNodeId), Math.Max(e.FromNodeId, e.ToNodeId))),
        ];
        Assert.True(undirected.Distinct().Count() == undirected.Count, $"the lead path goes out and back: {PathText(plan.LeadPathFromMerge)}");
    }

    /// <summary>
    /// SFO: a lead's trail on M2, the one-way outbound lane, with the edges between its two samples skipped, is rebuilt along M2
    /// the way the lead taxied it — not round a detour that would have to run M2 the other way.
    /// </summary>
    [Fact]
    public void TrailGapOnOneWayLane_RebuiltThroughTheLane()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("SFO") is not { } layout)
        {
            output.WriteLine("SKIP: KSFO layout unavailable");
            return;
        }

        List<DirectionalEdge> lane = LegalLane(layout, "M2", WakeTurbulenceData.WakeClass.Large);
        List<int> straight = [.. Enumerable.Range(0, lane.Count).Where(i => lane[i].Edge is GroundEdge)];
        Assert.True((straight.Count >= 2) && (straight[^1] - straight[0] >= 2), $"M2 lane {PathText(lane)} has no two straight edges with a gap");
        DirectionalEdge first = lane[straight[0]];
        DirectionalEdge last = lane[straight[^1]];

        AircraftState lead = FollowCornerGeometry.Spawn(
            layout.AirportId,
            "N1LED",
            "B738",
            FollowCornerGeometry.Between(last.FromNode.Position, last.ToNode.Position, 0.5),
            FollowCornerGeometry.Facing(last.FromNode, last.ToNode)
        );
        lead.Ground.TaxiEdgeTrail.Record((GroundEdge)first.Edge, first.FromNode);
        lead.Ground.TaxiEdgeTrail.Record((GroundEdge)last.Edge, last.FromNode);
        AircraftState follower = FollowCornerGeometry.Spawn(
            layout.AirportId,
            "N2FOL",
            "B738",
            first.FromNode.Position,
            FollowCornerGeometry.Facing(first.FromNode, first.ToNode)
        );

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(layout, follower, lead));
        List<DirectionalEdge> expected = lane[straight[0]..(straight[^1] + 1)];
        output.WriteLine($"merge #{plan.MergeNode}; lane {PathText(expected)}; lead path {PathText(plan.LeadPathFromMerge)}");

        Assert.Equal(first.FromNodeId, plan.MergeNode);
        Assert.Equal(expected.Select(e => (e.FromNodeId, e.ToNodeId)), plan.LeadPathFromMerge.Select(e => (e.FromNodeId, e.ToNodeId)));
    }

    /// <summary>
    /// A re-plan never turns the follower about on its own edge. Mid-way along B, driving south with that edge newest in its
    /// trail and the lead behind it on B, its route never leaves the node it drives toward back up its own edge. On a dead-end
    /// stub with the lead's path behind it, where that move back is the only join, its first plan joins and a re-plan joins
    /// nothing.
    /// </summary>
    [Fact]
    public void Replan_NeverStartsBackAlongTheFollowersOwnEdge()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        List<GroundNode> chain = FollowCornerGeometry.BChain(layout);
        AircraftState lead = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N1LED",
            "C172",
            FollowCornerGeometry.Between(chain[1].Position, chain[2].Position, 0.5),
            FollowCornerGeometry.Facing(chain[1], chain[2])
        );
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            FollowCornerGeometry.Between(chain[4].Position, chain[5].Position, 0.5),
            FollowCornerGeometry.Facing(chain[4], chain[5])
        );
        follower.Ground.TaxiEdgeTrail.Record(FollowCornerGeometry.EdgeBetween(chain[3], chain[4]), chain[3]);
        follower.Ground.TaxiEdgeTrail.Record(FollowCornerGeometry.EdgeBetween(chain[4], chain[5]), chain[4]);

        FollowRoutePlan.Joinable joinable = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Replan(layout, follower, lead));
        List<(int From, int To)> moves =
        [
            .. joinable.PathToMerge.Segments.Select(s => (s.FromNodeId, s.ToNodeId)),
            .. joinable.LeadPathFromMerge.Select(e => (e.FromNodeId, e.ToNodeId)),
        ];
        output.WriteLine($"re-plan: {string.Join(" ", moves.Select(m => $"#{m.From}>#{m.To}"))}");
        (int From, int To) leaving = Assert.Single(moves.Where(m => m.From == chain[5].Id).Take(1));
        Assert.NotEqual(chain[4].Id, leaving.To);

        GroundEdge stub = layout
            .Edges.Where(e => !e.IsRamp && !e.IsRunwayCenterline && ((e.Nodes[0].Edges.Count == 1) || (e.Nodes[1].Edges.Count == 1)))
            .Where(e => (e.DistanceNm * GeoMath.FeetPerNm) >= 100.0)
            .OrderBy(e => GeoMath.DistanceNm(e.Nodes[0].Position, lead.Position))
            .First();
        GroundNode deadEnd = stub.Nodes[0].Edges.Count == 1 ? stub.Nodes[0] : stub.Nodes[1];
        GroundNode mouth = stub.OtherNode(deadEnd);
        AircraftState stuck = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N3FOL",
            "C172",
            FollowCornerGeometry.Between(mouth.Position, deadEnd.Position, 0.5),
            FollowCornerGeometry.Facing(mouth, deadEnd)
        );
        stuck.Ground.TaxiEdgeTrail.Record(stub, mouth);
        output.WriteLine($"stub #{mouth.Id}>#{deadEnd.Id} ({stub.TaxiwayName})");
        Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(layout, stuck, lead));
        Assert.IsType<FollowRoutePlan.NoPath>(FollowRoutePlanner.Replan(layout, stuck, lead));
    }

    /// <summary>A follower standing on an edge of the lead's remaining route, ahead of the lead, is ahead of it: no follow route.</summary>
    [Fact]
    public void FollowerOnLeadsRemainingRoute_IsAhead()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        AircraftState follower = FollowCornerGeometry.SpawnOnRouteAhead(run.Lead, "N2FOL", "C172");

        Assert.IsType<FollowRoutePlan.FollowerAhead>(FollowRoutePlanner.Plan(run.Layout, follower, run.Lead));
    }

    /// <summary>A lead parked on its stand is not on a taxiway yet: the follower waits for it.</summary>
    [Fact]
    public void LeadOnStand_Waits()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        AircraftState lead = FollowCornerGeometry.SpawnAtStand(layout, "N1LED");
        List<GroundNode> chain = FollowCornerGeometry.BChain(layout);
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            chain[6].Position,
            FollowCornerGeometry.Facing(chain[6], chain[5])
        );

        Assert.IsType<FollowRoutePlan.WaitForLead>(FollowRoutePlanner.Plan(layout, follower, lead));
    }

    /// <summary>
    /// A trail whose consecutive edges do not meet (short edges skipped between samples) is filled in: the lead path from the
    /// merge runs edge to edge, each edge starting where the one before ends, through the skipped B nodes.
    /// </summary>
    [Fact]
    public void TrailWithGaps_LeadPathIsConnected()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        List<GroundNode> chain = FollowCornerGeometry.BChain(layout);
        AircraftState lead = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N1LED",
            "C560",
            FollowCornerGeometry.Between(chain[2].Position, chain[1].Position, 0.5),
            FollowCornerGeometry.Facing(chain[2], chain[1])
        );
        lead.Ground.TaxiEdgeTrail.Record(FollowCornerGeometry.EdgeBetween(chain[5], chain[4]), chain[5]);
        lead.Ground.TaxiEdgeTrail.Record(FollowCornerGeometry.EdgeBetween(chain[2], chain[1]), chain[2]);
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            chain[6].Position,
            FollowCornerGeometry.Facing(chain[6], chain[5])
        );

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(layout, follower, lead));
        output.WriteLine(
            $"merge #{plan.MergeNode}; lead path {string.Join(" ", plan.LeadPathFromMerge.Select(e => $"#{e.FromNodeId}>#{e.ToNodeId}"))}"
        );

        for (int i = 1; i < plan.LeadPathFromMerge.Count; i++)
        {
            Assert.Equal(plan.LeadPathFromMerge[i - 1].ToNodeId, plan.LeadPathFromMerge[i].FromNodeId);
        }

        HashSet<int> pathNodes = [.. plan.LeadPathFromMerge.Select(e => e.ToNodeId)];
        Assert.Contains(chain[3].Id, pathNodes);
        Assert.Contains(chain[2].Id, pathNodes);
        Assert.Contains(chain[1].Id, pathNodes);
    }

    /// <summary>
    /// A lead that rounded KOAK's C-to-J fillet at the 352 corner has a trail holding the straight stubs either side of the
    /// junction — the edges the 1 Hz sample records while it is on the arc — never the arc itself. Its path from the merge
    /// takes that arc, not the stubs the planner would otherwise stitch through the apex: the follower's gap reads the arc's
    /// 130 ft, not the 300 ft round the junction.
    /// </summary>
    [Fact]
    public void LeadRoundTheC_J_Corner_FollowerPlansOverTheArc()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        CornerFollow c = FollowRoundTheC_J_Corner(layout, "C172", "C172", leadDroveTheJunction: false);
        output.WriteLine($"C tangent #{c.Corner.TangentOnC.Id}, J tangent #{c.Corner.TangentOnJ.Id}; lead path {PathText(c.Plan.LeadPathFromMerge)}");

        Assert.Contains(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, c.Corner.Arc));
        Assert.DoesNotContain(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, c.Corner.StubOnC));
        Assert.DoesNotContain(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, c.Corner.StubOnJ));

        PathPosition? overTheArc = FollowRoutePlanner.LocateOnPath(layout, c.Plan.LeadPathFromMerge, c.Lead);
        List<DirectionalEdge> roundTheJunction = RoundTheJunction(c);
        PathPosition? round = FollowRoutePlanner.LocateOnPath(layout, roundTheJunction, c.Lead);
        // The follower is the same distance from the merge on either path, so it is measured from the merge: the term cancels
        // and what is left is what the arc saves the follower.
        double? arcGapFt = FollowRoutePlanner.AlongPathGapFt(0.0, c.Plan.LeadPathFromMerge, overTheArc, "C172", "C172");
        double? junctionGapFt = FollowRoutePlanner.AlongPathGapFt(0.0, roundTheJunction, round, "C172", "C172");

        Assert.True(overTheArc is not null, $"the lead is not on the path over the arc ({PathText(c.Plan.LeadPathFromMerge)})");
        Assert.True(round is not null, $"the lead is not on the path round the junction ({PathText(roundTheJunction)})");
        Assert.True(arcGapFt is not null, "the follower has no along-path gap over the arc");
        Assert.True(junctionGapFt is not null, "the follower has no along-path gap round the junction");
        output.WriteLine($"gap over the arc {arcGapFt!.Value:F1} ft, round the junction {junctionGapFt!.Value:F1} ft");
        Assert.True(
            junctionGapFt!.Value - arcGapFt!.Value >= 150.0,
            $"the gap over the arc {arcGapFt.Value:F1} ft is not 150 ft under the {junctionGapFt.Value:F1} ft round the junction"
        );
    }

    /// <summary>
    /// The same corner with a lead that drove straight through it instead: its trail holds the junction's own edges as well
    /// as the stubs, so the planner has no gap to fill and the follower's path goes through the junction, never over the arc
    /// the lead did not take.
    /// </summary>
    [Fact]
    public void LeadThroughTheJunction_FollowerKeepsTheSquareCorner()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        CornerFollow c = FollowRoundTheC_J_Corner(layout, "C172", "C172", leadDroveTheJunction: true);
        output.WriteLine($"lead path {PathText(c.Plan.LeadPathFromMerge)}");

        GroundEdge intoJunction = FollowCornerGeometry.EdgeBetween(c.Corner.NearOnC, c.Corner.ThroughRun[2]);
        GroundEdge outOfJunction = FollowCornerGeometry.EdgeBetween(c.Corner.ThroughRun[2], c.Corner.NearOnJ);
        Assert.Contains(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, intoJunction));
        Assert.Contains(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, outOfJunction));
        Assert.DoesNotContain(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, c.Corner.Arc));
    }

    /// <summary>
    /// A jet lead that rounded the 352 corner's C-to-J arc: the square way round turns past a jet's junction limit, so no jet
    /// path fills its trail's gap there. The arc, wide enough for a jet follower's main-gear turn radius, stands in for the
    /// stubs either side of the junction and the trail before the corner is kept.
    /// </summary>
    [Fact]
    public void JetLeadRoundTheC_J_Corner_JetFollowerPlansOverTheArc() => AssertJetLeadPlansOverTheArc("B738");

    /// <summary>The same jet lead with a C172 follower, whose main-gear turn radius the arc fits as well.</summary>
    [Fact]
    public void JetLeadRoundTheC_J_Corner_PistonFollowerPlansOverTheArc() => AssertJetLeadPlansOverTheArc("C172");

    private void AssertJetLeadPlansOverTheArc(string followerType)
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        FollowCornerGeometry.CToJCorner corner = FollowCornerGeometry.FindCToJCorner(layout);
        double radiusFt = corner.Arc.MinRadiusOfCurvatureFt;
        double apexDeg = FollowCornerGeometry.ApexTurnDeg(corner);
        output.WriteLine($"corner at #{corner.ThroughRun[2].Id}: arc radius {radiusFt:F1} ft, apex turn {apexDeg:F1}°");
        Assert.True(apexDeg > CategoryLimits.MaxHeadingChangeDeg(AircraftCategory.Jet), $"the apex turn {apexDeg:F1}° is one a jet may make");
        Assert.True(
            radiusFt >= CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet),
            $"the arc's {radiusFt:F1} ft radius is under a jet's main-gear turn radius"
        );

        CornerFollow c = FollowRoundTheC_J_Corner(layout, "B738", followerType, leadDroveTheJunction: false);

        Assert.True(
            c.Plan.LeadPathFromMerge.Any(e => ReferenceEquals(e.Edge, c.Corner.Arc)),
            $"the lead path {PathText(c.Plan.LeadPathFromMerge)} misses the {radiusFt:F1} ft radius arc at the {apexDeg:F1}° corner"
        );
        Assert.DoesNotContain(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, c.Corner.StubOnC));
        Assert.DoesNotContain(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, c.Corner.StubOnJ));
    }

    /// <summary>
    /// KOAK's C-to-G fillet arc (the first corner on a committed layout whose arc's effective radius is under a jet's 25 ft
    /// main-gear turn radius but not a piston's 15 ft, at a junction turn a jet may make): a C172 lead that rounded the arc
    /// leaves a stub of the junction in its trail, and a B738 follower's path keeps the square corner through the junction,
    /// never the arc it cannot steer.
    /// </summary>
    [Fact]
    public void ArcTighterThanFollowerRadius_FollowerTakesTheSquareCornerItCanTurn()
    {
        if (ArcTighterThanAJet() is not { } found)
        {
            return;
        }

        (AirportGroundLayout layout, FollowCornerGeometry.TaxiwayCorner tight) = found;
        CornerFollow c = FollowRoundTheCorner(layout, tight, "C172", "B738", leadDroveTheJunction: false);

        Assert.DoesNotContain(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, c.Corner.Arc));
        for (int i = 0; i + 1 < c.Corner.ThroughRun.Count; i++)
        {
            GroundEdge square = FollowCornerGeometry.EdgeBetween(c.Corner.ThroughRun[i], c.Corner.ThroughRun[i + 1]);
            Assert.True(
                c.Plan.LeadPathFromMerge.Any(e => ReferenceEquals(e.Edge, square)),
                $"the square way's edge #{c.Corner.ThroughRun[i].Id}-#{c.Corner.ThroughRun[i + 1].Id} is not on the lead path"
            );
        }
    }

    /// <summary>
    /// The control for <see cref="ArcTighterThanFollowerRadius_FollowerTakesTheSquareCornerItCanTurn"/>: at the same KOAK corner
    /// a C172 follower, whose 15 ft main-gear turn radius the arc fits, plans over the arc the C172 lead rounded, not its stubs.
    /// </summary>
    [Fact]
    public void ArcTighterThanFollowerRadius_PistonFollowerKeepsTheArc()
    {
        if (ArcTighterThanAJet() is not { } found)
        {
            return;
        }

        (AirportGroundLayout layout, FollowCornerGeometry.TaxiwayCorner tight) = found;
        CornerFollow c = FollowRoundTheCorner(layout, tight, "C172", "C172", leadDroveTheJunction: false);

        Assert.Contains(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, c.Corner.Arc));
        Assert.DoesNotContain(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, c.Corner.StubOnC));
        Assert.DoesNotContain(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, c.Corner.StubOnJ));
    }

    /// <summary>
    /// A C172 lead whose assigned route ahead rounds the same KOAK arc, with a B738 follower behind it on the edge it is on: the
    /// lead path's route ahead drives the square corner through the junction the jet can turn, never the arc it cannot steer.
    /// </summary>
    [Fact]
    public void LeadRouteAheadArc_TighterThanFollowerRadius_FollowerGetsTheSquareCorner()
    {
        if (ArcTighterThanAJet() is not { } found)
        {
            return;
        }

        (AirportGroundLayout layout, FollowCornerGeometry.TaxiwayCorner tight) = found;
        FollowCornerGeometry.CToJCorner corner = tight.Corner;
        GroundNode t1 = corner.TangentOnC;
        GroundNode t2 = corner.TangentOnJ;
        GroundEdge before = t1.Edges.OfType<GroundEdge>().First(e => e.MatchesTaxiway(tight.InTaxiway) && (e.OtherNode(t1).Id != corner.NearOnC.Id));
        GroundEdge beyond = t2.Edges.OfType<GroundEdge>().First(e => e.MatchesTaxiway(tight.OutTaxiway) && (e.OtherNode(t2).Id != corner.NearOnJ.Id));
        GroundNode beforeFar = before.OtherNode(t1);
        GroundNode beyondFar = beyond.OtherNode(t2);
        AircraftState lead = FollowCornerGeometry.Spawn(
            layout.AirportId,
            "N1LED",
            "C172",
            FollowCornerGeometry.Between(beforeFar.Position, t1.Position, 0.8),
            FollowCornerGeometry.Facing(beforeFar, t1)
        );
        lead.Ground.TaxiEdgeTrail.Record(before, beforeFar);
        lead.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment { Edge = before.Directed(beforeFar, t1), TaxiwayName = tight.InTaxiway },
                new TaxiRouteSegment { Edge = corner.Arc.Directed(t1, t2), TaxiwayName = tight.OutTaxiway },
                new TaxiRouteSegment { Edge = beyond.Directed(t2, beyondFar), TaxiwayName = tight.OutTaxiway },
            ],
            HoldShortPoints = [],
        };
        AircraftState follower = FollowCornerGeometry.Spawn(
            layout.AirportId,
            "N2FOL",
            "B738",
            FollowCornerGeometry.Between(beforeFar.Position, t1.Position, 0.3),
            FollowCornerGeometry.Facing(beforeFar, t1)
        );
        follower.Ground.TaxiEdgeTrail.Record(before, beforeFar);

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(layout, follower, lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");

        double jetRadiusFt = CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet);
        Assert.DoesNotContain(plan.LeadPathFromMerge, e => (e.Edge is GroundArc arc) && (FollowRoutePlanner.EffectiveArcRadiusFt(arc) < jetRadiusFt));
        Assert.Contains(plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, corner.StubOnC));
        Assert.Contains(plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, corner.StubOnJ));
        Assert.True(
            FollowRoutePlanner.LeadRouteMatchesPath(layout, follower, lead, plan.MergeNode, plan.LeadPathFromMerge),
            "the lead's route over the arc does not match the lead path that drives the square way round it"
        );
    }

    /// <summary>
    /// A C172 lead halfway round the same KOAK arc on its route, with a B738 follower behind it on the edge before the arc: the
    /// lead is located on the arc its route drives, not on the straight stub nearest it, so the follower's lead path runs from
    /// where the lead is — the square way round the arc the jet can turn — and still matches the lead's route.
    /// </summary>
    [Fact]
    public void Plan_LeadMidwayRoundTheRefusedArc_LocatesItOnTheArc()
    {
        if (ArcTighterThanAJet() is not { } found)
        {
            return;
        }

        (AirportGroundLayout layout, FollowCornerGeometry.TaxiwayCorner tight) = found;
        FollowCornerGeometry.CToJCorner corner = tight.Corner;
        GroundNode t1 = corner.TangentOnC;
        GroundNode t2 = corner.TangentOnJ;
        (GroundEdge before, GroundEdge beyond) = EdgesPastTheCorner(tight);
        GroundNode beforeFar = before.OtherNode(t1);
        CubicBezier curve = corner.Arc.ToBezier();
        (double midLat, double midLon) = curve.Evaluate(0.5);
        double alongCurveDeg = curve.TangentBearing(0.5);
        double headingDeg = corner.Arc.Nodes[0].Id == t1.Id ? alongCurveDeg : (alongCurveDeg + 180.0) % 360.0;
        AircraftState lead = FollowCornerGeometry.Spawn(layout.AirportId, "N1LED", "C172", new LatLon(midLat, midLon), new TrueHeading(headingDeg));
        lead.Ground.TaxiEdgeTrail.Record(before, beforeFar);
        lead.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment { Edge = before.Directed(beforeFar, t1), TaxiwayName = tight.InTaxiway },
                new TaxiRouteSegment { Edge = corner.Arc.Directed(t1, t2), TaxiwayName = tight.OutTaxiway },
                new TaxiRouteSegment { Edge = beyond.Directed(t2, beyond.OtherNode(t2)), TaxiwayName = tight.OutTaxiway },
            ],
            HoldShortPoints = [],
            CurrentSegmentIndex = 1,
        };
        AircraftState follower = FollowCornerGeometry.Spawn(
            layout.AirportId,
            "N2FOL",
            "B738",
            FollowCornerGeometry.Between(beforeFar.Position, t1.Position, 0.3),
            FollowCornerGeometry.Facing(beforeFar, t1)
        );
        follower.Ground.TaxiEdgeTrail.Record(before, beforeFar);

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(layout, follower, lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");

        Assert.NotEmpty(plan.LeadPathFromMerge);
        double jetRadiusFt = CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet);
        Assert.DoesNotContain(plan.LeadPathFromMerge, e => (e.Edge is GroundArc arc) && (FollowRoutePlanner.EffectiveArcRadiusFt(arc) < jetRadiusFt));
        Assert.Contains(plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, corner.StubOnC));
        Assert.Contains(plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, corner.StubOnJ));
        Assert.Equal(beyond.OtherNode(t2).Id, plan.LeadPathFromMerge[^1].ToNodeId);
        Assert.False(plan.MergeAheadOfLead);
        Assert.True(
            FollowRoutePlanner.LeadRouteMatchesPath(layout, follower, lead, plan.MergeNode, plan.LeadPathFromMerge),
            "the lead's route round the arc it is on does not match the lead path built from where it is"
        );
    }

    /// <summary>
    /// KOAK's K-to-L arc through node 441 (<see cref="FollowCornerGeometry.KoakKToL441"/>), refused by a B738 follower even at
    /// its tight-turn floor, with a C172 lead halfway round it whose trail is the in-taxiway stub driven from the junction to
    /// the arc's tangent node A, so the square way round would turn back along it: the lead path is cut at A, behind the lead,
    /// so the merge is not ahead of the lead, and the lead's route round the arc still matches the cut path.
    /// </summary>
    [Fact]
    public void Plan_LeadMidwayRoundAnArcCutForTheFollower_PathEndsBehindTheLead()
    {
        if (LeadMidwayRoundKToL() is not { } s)
        {
            return;
        }

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(s.Layout, s.Follower, s.Lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");

        int pathEnd = plan.LeadPathFromMerge.Count == 0 ? plan.MergeNode : plan.LeadPathFromMerge[^1].ToNodeId;
        Assert.Equal(s.Tight.Corner.TangentOnC.Id, pathEnd);
        Assert.False(plan.MergeAheadOfLead);
        Assert.True(
            FollowRoutePlanner.LeadRouteMatchesPath(s.Layout, s.Follower, s.Lead, plan.MergeNode, plan.LeadPathFromMerge),
            "the lead's route round the arc the planner cut the path at reads as a re-route"
        );
    }

    /// <summary>
    /// As <see cref="Plan_LeadMidwayRoundAnArcCutForTheFollower_PathEndsBehindTheLead"/>, with the lead's route then past the
    /// arc: its route no longer starts with the arc the path was cut at, so it no longer matches the cut path.
    /// </summary>
    [Fact]
    public void LeadRouteMatchesPath_LeadPastTheCutArc_DoesNotMatch()
    {
        if (LeadMidwayRoundKToL() is not { } s)
        {
            return;
        }

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(s.Layout, s.Follower, s.Lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");
        Assert.True(FollowRoutePlanner.LeadRouteMatchesPath(s.Layout, s.Follower, s.Lead, plan.MergeNode, plan.LeadPathFromMerge));

        TaxiRoute route = Assert.IsType<TaxiRoute>(s.Lead.Ground.AssignedTaxiRoute);
        route.CurrentSegmentIndex = route.Segments.FindIndex(seg => ReferenceEquals(seg.Edge.Edge, s.Tight.Corner.Arc)) + 1;

        Assert.False(
            FollowRoutePlanner.LeadRouteMatchesPath(s.Layout, s.Follower, s.Lead, plan.MergeNode, plan.LeadPathFromMerge),
            "a lead route past the arc the path was cut at matches the cut path"
        );
    }

    /// <summary>
    /// A C172 lead on KOAK's K-to-L in-taxiway stub, driving it away from the junction, whose route goes on round the arc a B738
    /// follower refuses even at its tight-turn floor and whose square way round would turn back along the stub: the lead path
    /// is cut at the arc, and the lead's route, which runs on past the cut, still matches it.
    /// </summary>
    [Fact]
    public void LeadRouteAheadOverACutArc_MatchesTheCutPath()
    {
        if (LeadOnTheKToLInStub() is not { } s)
        {
            return;
        }

        GroundNode t1 = s.Tight.Corner.TangentOnC;
        GroundNode t2 = s.Tight.Corner.TangentOnJ;
        SetRoute(s, [s.Tight.Corner.Arc.Directed(t1, t2), s.Beyond.Directed(t2, s.Beyond.OtherNode(t2))]);

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(s.Layout, s.Follower, s.Lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");

        Assert.Equal(t1.Id, plan.LeadPathFromMerge[^1].ToNodeId);
        Assert.DoesNotContain(plan.LeadPathFromMerge, e => e.Edge is GroundArc);
        Assert.True(
            FollowRoutePlanner.LeadRouteMatchesPath(s.Layout, s.Follower, s.Lead, plan.MergeNode, plan.LeadPathFromMerge),
            "the lead's route past the arc the planner cut the path at reads as a re-route"
        );
    }

    /// <summary>
    /// A C172 lead halfway round KOAK's K-to-L arc with no trail behind it, and a B738 follower behind it on the in-taxiway stub:
    /// the follower refuses the arc even at its tight-turn floor and has no square way round it, and with no trail the cut
    /// leaves no lead path, so the follower waits for the lead rather than giving up the follow.
    /// </summary>
    [Fact]
    public void Plan_LeadOnACutArcWithNoTrail_WaitsForTheLead()
    {
        if (LeadMidwayRoundKToL() is not { } s)
        {
            return;
        }

        s.Lead.Ground.TaxiEdgeTrail.Clear();

        FollowRoutePlan plan = FollowRoutePlanner.Plan(s.Layout, s.Follower, s.Lead);
        output.WriteLine($"plan {plan}");

        Assert.IsType<FollowRoutePlan.WaitForLead>(plan);
    }

    /// <summary>
    /// A C172 lead halfway round KOAK's C-to-G arc with no trail behind it, and a B738 follower behind it on the edge before the
    /// arc: the lead path starts with the square way round the arc in place of the arc the jet refuses, with no edge it arrived
    /// by to turn back along, and runs on along the lead's route.
    /// </summary>
    [Fact]
    public void Plan_LeadOnARefusedArcWithNoTrail_TakesTheSquareWay()
    {
        if (ArcTighterThanAJet() is not { } found)
        {
            return;
        }

        (AirportGroundLayout layout, FollowCornerGeometry.TaxiwayCorner tight) = found;
        FollowCornerGeometry.CToJCorner corner = tight.Corner;
        GroundNode t1 = corner.TangentOnC;
        GroundNode t2 = corner.TangentOnJ;
        (GroundEdge before, GroundEdge beyond) = EdgesPastTheCorner(tight);
        GroundNode beforeFar = before.OtherNode(t1);
        AircraftState lead = FollowCornerGeometry.Spawn(layout.AirportId, "N1LED", "C172", t1.Position, FollowCornerGeometry.Facing(beforeFar, t1));
        PlaceMidArc(lead, corner.Arc, t1);
        lead.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment { Edge = before.Directed(beforeFar, t1), TaxiwayName = tight.InTaxiway },
                new TaxiRouteSegment { Edge = corner.Arc.Directed(t1, t2), TaxiwayName = tight.OutTaxiway },
                new TaxiRouteSegment { Edge = beyond.Directed(t2, beyond.OtherNode(t2)), TaxiwayName = tight.OutTaxiway },
            ],
            HoldShortPoints = [],
            CurrentSegmentIndex = 1,
        };
        AircraftState follower = FollowCornerGeometry.Spawn(
            layout.AirportId,
            "N2FOL",
            "B738",
            FollowCornerGeometry.Between(beforeFar.Position, t1.Position, 0.3),
            FollowCornerGeometry.Facing(beforeFar, t1)
        );
        follower.Ground.TaxiEdgeTrail.Record(before, beforeFar);

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(layout, follower, lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");

        double jetRadiusFt = CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet);
        Assert.DoesNotContain(plan.LeadPathFromMerge, e => (e.Edge is GroundArc arc) && (FollowRoutePlanner.EffectiveArcRadiusFt(arc) < jetRadiusFt));
        Assert.Contains(plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, corner.StubOnC));
        Assert.Contains(plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, corner.StubOnJ));
        Assert.Equal(beyond.OtherNode(t2).Id, plan.LeadPathFromMerge[^1].ToNodeId);
    }

    /// <summary>
    /// A C172 lead on the edge before KOAK's K-to-L arc, driving toward its tangent node A on a route that ends there, with a
    /// C172 follower behind it: the follower's path ends at A uncut. The lead re-cleared on round the arc — refused by the C172
    /// even at its tight-turn floor, but with a square way round the piston accepts — is a re-route, not a run past a cut.
    /// </summary>
    [Fact]
    public void LeadReclearedThroughAnArcRefusedAtTheFloorWithASquareWay_DoesNotMatch()
    {
        if (KoakKToL() is not { } found)
        {
            return;
        }

        FollowCornerGeometry.CToJCorner corner = found.Corner.Corner;
        GroundNode t1 = corner.TangentOnC;
        GroundNode t2 = corner.TangentOnJ;
        (GroundEdge before, GroundEdge beyond) = EdgesPastTheCorner(found.Corner);
        StubLead s = LeadOn(found, before.Directed(before.OtherNode(t1), t1), "C172");
        SetRoute(s, []);
        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(s.Layout, s.Follower, s.Lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");
        Assert.Equal(t1.Id, plan.LeadPathFromMerge[^1].ToNodeId);

        SetRoute(s, [corner.Arc.Directed(t1, t2), beyond.Directed(t2, beyond.OtherNode(t2))]);

        Assert.False(
            FollowRoutePlanner.LeadRouteMatchesPath(s.Layout, s.Follower, s.Lead, plan.MergeNode, plan.LeadPathFromMerge),
            "a lead re-cleared round an arc the follower has a square way round matches the old path as a cut"
        );
    }

    /// <summary>
    /// KOAK's layout and its K-to-L corner through node 441 (<see cref="FollowCornerGeometry.KoakKToL441"/>); null when the
    /// layout is unavailable.
    /// </summary>
    private (AirportGroundLayout Layout, FollowCornerGeometry.TaxiwayCorner Corner)? KoakKToL()
    {
        if (LoadLayout("OAK") is not { } layout)
        {
            return null;
        }

        return (layout, FollowCornerGeometry.KoakKToL441(layout, output));
    }

    /// <summary><see cref="StubLead"/> on the K-to-L corner's in-taxiway stub, driven toward the arc, with a B738 follower.</summary>
    private StubLead? LeadOnTheKToLInStub()
    {
        if (KoakKToL() is not { } found)
        {
            return null;
        }

        FollowCornerGeometry.CToJCorner corner = found.Corner.Corner;
        return LeadOn(found, corner.StubOnC.Directed(corner.NearOnC, corner.TangentOnC), "B738");
    }

    /// <summary>
    /// <see cref="LeadOnTheKToLInStub"/> with the lead moved halfway round the arc, facing along it, its trail the stub and its
    /// route the stub, the arc and the out-taxiway edge beyond, on the arc's segment.
    /// </summary>
    private StubLead? LeadMidwayRoundKToL()
    {
        if (LeadOnTheKToLInStub() is not { } s)
        {
            return null;
        }

        GroundNode t1 = s.Tight.Corner.TangentOnC;
        GroundNode t2 = s.Tight.Corner.TangentOnJ;
        SetRoute(s, [s.Tight.Corner.Arc.Directed(t1, t2), s.Beyond.Directed(t2, s.Beyond.OtherNode(t2))]);
        Assert.IsType<TaxiRoute>(s.Lead.Ground.AssignedTaxiRoute).CurrentSegmentIndex = 1;
        PlaceMidArc(s.Lead, s.Tight.Corner.Arc, t1);
        return s;
    }

    /// <summary>Moves <paramref name="aircraft"/> to the midpoint of <paramref name="arc"/>'s curve, facing along it away from <paramref name="from"/>.</summary>
    private static void PlaceMidArc(AircraftState aircraft, GroundArc arc, GroundNode from)
    {
        CubicBezier curve = arc.ToBezier();
        (double midLat, double midLon) = curve.Evaluate(0.5);
        double alongCurveDeg = curve.TangentBearing(0.5);
        aircraft.Position = new LatLon(midLat, midLon);
        aircraft.TrueHeading = new TrueHeading(arc.Nodes[0].Id == from.Id ? alongCurveDeg : (alongCurveDeg + 180.0) % 360.0);
    }

    /// <summary>
    /// A C172 lead on the KOAK C-to-G corner's in-taxiway stub, driving it away from the junction, whose route turns back
    /// through the arc: the square way round the arc for a B738 follower starts back along that stub, so the planner refuses it,
    /// and the arc's 16.8 ft effective radius is above the jet's 15 ft tight-turn floor, so the lead path keeps the arc rather
    /// than ending where it starts — and the lead's route still matches it. No committed layout has a corner a jet rounds only
    /// at its tight-turn floor whose square way round is refused by its length or its junction turn, so the turn back along
    /// the stub is what refuses it here.
    /// </summary>
    [Fact]
    public void Plan_RefusedArcWithNoSquareWayAboveTheTightFloor_KeepsTheArc()
    {
        if (LeadOnTheInStub("B738") is not { } s)
        {
            return;
        }

        GroundNode t1 = s.Tight.Corner.TangentOnC;
        GroundNode t2 = s.Tight.Corner.TangentOnJ;
        SetRoute(s, [s.Tight.Corner.Arc.Directed(t1, t2), s.Beyond.Directed(t2, s.Beyond.OtherNode(t2))]);

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(s.Layout, s.Follower, s.Lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");

        Assert.Contains(plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, s.Tight.Corner.Arc));
        Assert.Equal(s.Beyond.OtherNode(t2).Id, plan.LeadPathFromMerge[^1].ToNodeId);
        Assert.True(
            FollowRoutePlanner.LeadRouteMatchesPath(s.Layout, s.Follower, s.Lead, plan.MergeNode, plan.LeadPathFromMerge),
            "the lead's route over the arc kept at the tight-turn floor does not match the lead path"
        );
    }

    /// <summary>
    /// As <see cref="Plan_RefusedArcWithNoSquareWayAboveTheTightFloor_KeepsTheArc"/>, with the arc missing from the lead's
    /// route: the gap from the stub to the out-taxiway edge is bridged over the arc, whose square way round turns back along the
    /// stub, so the bridge keeps the arc at the B738's tight-turn floor — and the lead's route still matches the path.
    /// </summary>
    [Fact]
    public void LeadRouteGapBridgedOverATightFloorArc_KeepsTheArcAndMatches()
    {
        if (LeadOnTheInStub("B738") is not { } s)
        {
            return;
        }

        GroundNode t2 = s.Tight.Corner.TangentOnJ;
        SetRoute(s, [s.Beyond.Directed(t2, s.Beyond.OtherNode(t2))]);

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(s.Layout, s.Follower, s.Lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");

        Assert.Contains(plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, s.Tight.Corner.Arc));
        Assert.Equal(s.Beyond.OtherNode(t2).Id, plan.LeadPathFromMerge[^1].ToNodeId);
        Assert.True(
            FollowRoutePlanner.LeadRouteMatchesPath(s.Layout, s.Follower, s.Lead, plan.MergeNode, plan.LeadPathFromMerge),
            "the lead's route past the bridge over the arc reads as a re-route"
        );
    }

    /// <summary>
    /// A lead whose route ends where the stub ends, re-routed on past that end along a straight edge: the path the follower
    /// planned from the old route ends there uncut, so the longer route is a re-route the follower plans again for.
    /// </summary>
    [Fact]
    public void LeadRouteRunOnStraightPastThePathsEnd_DoesNotMatch()
    {
        if (LeadOnTheInStub("B738") is not { } s)
        {
            return;
        }

        GroundNode t1 = s.Tight.Corner.TangentOnC;
        SetRoute(s, []);
        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(s.Layout, s.Follower, s.Lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");
        Assert.Equal(t1.Id, plan.LeadPathFromMerge[^1].ToNodeId);
        Assert.True(FollowRoutePlanner.LeadRouteMatchesPath(s.Layout, s.Follower, s.Lead, plan.MergeNode, plan.LeadPathFromMerge));

        SetRoute(s, [s.Before.Directed(t1, s.Before.OtherNode(t1))]);

        Assert.False(
            FollowRoutePlanner.LeadRouteMatchesPath(s.Layout, s.Follower, s.Lead, plan.MergeNode, plan.LeadPathFromMerge),
            "a lead route run on straight past the uncut path's end matches the old path"
        );
    }

    /// <summary>
    /// A C172 lead driving into the junction-side end of the corner's in-taxiway stub, whose route goes on by the arc: the gap to
    /// the arc is bridged along the stub, and the arc, too tight for the B738 follower's main-gear turn radius, has no square
    /// way round that does not turn back along the stub, so the path keeps it at the follower's tight-turn floor after the
    /// bridge — and the lead's route still matches the path.
    /// </summary>
    [Fact]
    public void LeadRouteBridgedThenOverATightFloorArc_KeepsTheArcAndMatches()
    {
        if (LeadIntoTheInStub() is not { } s)
        {
            return;
        }

        FollowCornerGeometry.CToJCorner corner = s.Tight.Corner;
        GroundNode t2 = corner.TangentOnJ;
        SetRoute(s, [corner.Arc.Directed(corner.TangentOnC, t2), s.Beyond.Directed(t2, s.Beyond.OtherNode(t2))]);

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(s.Layout, s.Follower, s.Lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");

        int arcAt = plan.LeadPathFromMerge.ToList().FindIndex(e => ReferenceEquals(e.Edge, corner.Arc));
        Assert.True(arcAt > 0, $"the path {PathText(plan.LeadPathFromMerge)} does not keep the arc after the bridge");
        Assert.True(ReferenceEquals(plan.LeadPathFromMerge[arcAt - 1].Edge, corner.StubOnC), "the arc does not follow the bridge along the stub");
        Assert.Equal(s.Beyond.OtherNode(t2).Id, plan.LeadPathFromMerge[^1].ToNodeId);
        Assert.True(
            FollowRoutePlanner.LeadRouteMatchesPath(s.Layout, s.Follower, s.Lead, plan.MergeNode, plan.LeadPathFromMerge),
            "the lead's route over the arc after the bridge reads as a re-route"
        );
    }

    /// <summary>
    /// A lead whose route ended where the in-taxiway stub ends, at the arc's tangent node, re-cleared on through the arc, with a
    /// C172 follower that may drive the arc: the planner never cut the path there for it, so the longer route is a re-route.
    /// </summary>
    [Fact]
    public void LeadReclearedThroughAnArcTheFollowerDrives_DoesNotMatch()
    {
        if (LeadOnTheInStub("C172") is not { } s)
        {
            return;
        }

        FollowCornerGeometry.CToJCorner corner = s.Tight.Corner;
        SetRoute(s, []);
        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(s.Layout, s.Follower, s.Lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");
        Assert.Equal(corner.TangentOnC.Id, plan.LeadPathFromMerge[^1].ToNodeId);

        GroundNode t2 = corner.TangentOnJ;
        SetRoute(s, [corner.Arc.Directed(corner.TangentOnC, t2), s.Beyond.Directed(t2, s.Beyond.OtherNode(t2))]);

        Assert.False(
            FollowRoutePlanner.LeadRouteMatchesPath(s.Layout, s.Follower, s.Lead, plan.MergeNode, plan.LeadPathFromMerge),
            "a lead route re-cleared through an arc the follower drives matches the old path"
        );
    }

    /// <summary>
    /// The stub length of the KSFO Q-to-B1 arc: its end headings, toward its control points, both aim at the junction, so the
    /// lines through its tangent nodes along them meet there and the stub length is the two tangent nodes' distances to the
    /// junction summed. A circular fillet's 2·r·tan(Δ/2) does not hold: the arc's tangent nodes stand far further from the
    /// junction than its 21.6 ft tightest radius would put them.
    /// </summary>
    [Fact]
    public void ArcStubLength_KsfoQB1_MatchesTheTangentGeometry()
    {
        if (KsfoQToB1() is not { } found)
        {
            return;
        }

        GroundArc arc = found.Corner.Corner.Arc;
        LatLon junction = found.Corner.Corner.ThroughRun[2].Position;
        double expectedFt = 0.0;
        foreach (
            (GroundNode end, LatLon control) in new[]
            {
                (arc.Nodes[0], new LatLon(arc.P1Lat, arc.P1Lon)),
                (arc.Nodes[1], new LatLon(arc.P2Lat, arc.P2Lon)),
            }
        )
        {
            double toJunctionFt = GeoMath.DistanceNm(end.Position, junction) * GeoMath.FeetPerNm;
            double toJunctionDeg = GeoMath.BearingTo(end.Position, junction);
            double toControlDeg = GeoMath.BearingTo(end.Position, control);
            output.WriteLine($"#{end.Id}: junction {toJunctionFt:F1} ft at {toJunctionDeg:F1}°, control point at {toControlDeg:F1}°");
            Assert.InRange(Math.Abs(GeoMath.SignedBearingDifference(toJunctionDeg, toControlDeg)), 0.0, 0.5);
            expectedFt += toJunctionFt;
        }

        double stubFt = FollowRoutePlanner.ArcStubLengthFt(arc);
        double circularFt = 2.0 * arc.MinRadiusOfCurvatureFt * Math.Tan(FollowCornerGeometry.ApexTurnDeg(found.Corner.Corner) * Math.PI / 360.0);
        output.WriteLine(
            $"stub {stubFt:F1} ft, to the junction and on {expectedFt:F1} ft, circular fillet of the tightest radius {circularFt:F1} ft"
        );

        Assert.InRange(stubFt, expectedFt - 1.0, expectedFt + 1.0);
    }

    /// <summary>
    /// A C172 lead past the KOAK C-to-G corner whose 1 Hz trail jumps from the in-taxiway edge before the arc straight to the
    /// out-taxiway edge beyond it, as a sample skip over the short fillet leaves it: the B738 follower's lead path keeps the trail
    /// past the corner, filled the square way round the arc it cannot steer.
    /// </summary>
    [Fact]
    public void TrailGapOverTightArc_FollowerTakesTheSquareCorner()
    {
        if (ArcTighterThanAJet() is not { } found)
        {
            return;
        }

        (AirportGroundLayout layout, FollowCornerGeometry.TaxiwayCorner tight) = found;
        FollowCornerGeometry.CToJCorner corner = tight.Corner;
        (GroundEdge before, GroundEdge beyond) = EdgesPastTheCorner(tight);
        GroundNode beforeFar = before.OtherNode(corner.TangentOnC);
        GroundNode beyondFar = beyond.OtherNode(corner.TangentOnJ);
        AircraftState lead = FollowCornerGeometry.Spawn(
            layout.AirportId,
            "N1LED",
            "C172",
            FollowCornerGeometry.Between(corner.TangentOnJ.Position, beyondFar.Position, 0.5),
            FollowCornerGeometry.Facing(corner.TangentOnJ, beyondFar)
        );
        lead.Ground.TaxiEdgeTrail.Record(before, beforeFar);
        lead.Ground.TaxiEdgeTrail.Record(beyond, corner.TangentOnJ);
        AircraftState follower = FollowCornerGeometry.Spawn(
            layout.AirportId,
            "N2FOL",
            "B738",
            FollowCornerGeometry.Between(beforeFar.Position, corner.TangentOnC.Position, 0.3),
            FollowCornerGeometry.Facing(beforeFar, corner.TangentOnC)
        );
        follower.Ground.TaxiEdgeTrail.Record(before, beforeFar);

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(layout, follower, lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");

        double jetRadiusFt = CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet);
        Assert.DoesNotContain(plan.LeadPathFromMerge, e => (e.Edge is GroundArc arc) && (FollowRoutePlanner.EffectiveArcRadiusFt(arc) < jetRadiusFt));
        Assert.Contains(plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, corner.StubOnC));
        Assert.Contains(plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, corner.StubOnJ));
    }

    /// <summary>
    /// The KOAK C-to-G corner (<see cref="ArcTighterThanAJet"/>) with a C172 lead part way along its lead edge, and a
    /// follower behind it on that edge; each with the edge in its trail.
    /// </summary>
    private sealed record StubLead(
        AirportGroundLayout Layout,
        FollowCornerGeometry.TaxiwayCorner Tight,
        DirectionalEdge LeadEdge,
        AircraftState Lead,
        AircraftState Follower,
        GroundEdge Before,
        GroundEdge Beyond
    );

    /// <summary>
    /// <see cref="StubLead"/> on the corner's in-taxiway stub, driven from the junction side toward the arc's in-taxiway tangent
    /// node, with a <paramref name="followerType"/> follower.
    /// </summary>
    private StubLead? LeadOnTheInStub(string followerType)
    {
        if (ArcTighterThanAJet() is not { } found)
        {
            return null;
        }

        FollowCornerGeometry.CToJCorner corner = found.Corner.Corner;
        return LeadOn(found, corner.StubOnC.Directed(corner.NearOnC, corner.TangentOnC), followerType);
    }

    /// <summary>
    /// <see cref="StubLead"/> on a straight edge into the in-taxiway stub's junction-side end from a node off the corner, driven
    /// toward that end, with a B738 follower: a route on from there to the arc is bridged over the stub.
    /// </summary>
    private StubLead? LeadIntoTheInStub()
    {
        if (ArcTighterThanAJet() is not { } found)
        {
            return null;
        }

        FollowCornerGeometry.CToJCorner corner = found.Corner.Corner;
        GroundNode cNear = corner.NearOnC;
        HashSet<int> onCorner = [.. corner.ThroughRun.Select(n => n.Id)];
        GroundEdge into = cNear
            .Edges.OfType<GroundEdge>()
            .First(e => !e.IsRunwayCenterline && !e.IsRamp && !onCorner.Contains(e.OtherNode(cNear).Id));
        return LeadOn(found, into.Directed(into.OtherNode(cNear), cNear), "B738");
    }

    private StubLead LeadOn(
        (AirportGroundLayout Layout, FollowCornerGeometry.TaxiwayCorner Corner) found,
        DirectionalEdge leadEdge,
        string followerType
    )
    {
        (AirportGroundLayout layout, FollowCornerGeometry.TaxiwayCorner tight) = found;
        (GroundEdge before, GroundEdge beyond) = EdgesPastTheCorner(tight);
        GroundNode from = leadEdge.FromNode;
        GroundNode to = leadEdge.ToNode;
        var edge = (GroundEdge)leadEdge.Edge;
        AircraftState lead = FollowCornerGeometry.Spawn(
            layout.AirportId,
            "N1LED",
            "C172",
            FollowCornerGeometry.Between(from.Position, to.Position, 0.7),
            FollowCornerGeometry.Facing(from, to)
        );
        lead.Ground.TaxiEdgeTrail.Record(edge, from);
        AircraftState follower = FollowCornerGeometry.Spawn(
            layout.AirportId,
            "N2FOL",
            followerType,
            FollowCornerGeometry.Between(from.Position, to.Position, 0.2),
            FollowCornerGeometry.Facing(from, to)
        );
        follower.Ground.TaxiEdgeTrail.Record(edge, from);
        output.WriteLine($"lead edge #{from.Id}>#{to.Id}: {edge.DistanceNm * GeoMath.FeetPerNm:F0} ft");
        return new StubLead(layout, tight, leadEdge, lead, follower, before, beyond);
    }

    /// <summary>Gives <paramref name="s"/>'s lead the route of the edge it is on, then <paramref name="after"/>.</summary>
    private static void SetRoute(StubLead s, IReadOnlyList<DirectionalEdge> after)
    {
        s.Lead.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment { Edge = s.LeadEdge, TaxiwayName = s.LeadEdge.Edge.TaxiwayName },
                .. after.Select(edge => new TaxiRouteSegment { Edge = edge, TaxiwayName = edge.Edge.TaxiwayName }),
            ],
            HoldShortPoints = [],
        };
    }

    /// <summary>
    /// The straight edge on past the corner's in-taxiway tangent node along the in-taxiway, away from the junction, and the one
    /// on past its out-taxiway tangent node along the out-taxiway.
    /// </summary>
    private static (GroundEdge Before, GroundEdge Beyond) EdgesPastTheCorner(FollowCornerGeometry.TaxiwayCorner tight)
    {
        FollowCornerGeometry.CToJCorner corner = tight.Corner;
        GroundEdge before = corner
            .TangentOnC.Edges.OfType<GroundEdge>()
            .First(e => e.MatchesTaxiway(tight.InTaxiway) && (e.OtherNode(corner.TangentOnC).Id != corner.NearOnC.Id));
        GroundEdge beyond = corner
            .TangentOnJ.Edges.OfType<GroundEdge>()
            .First(e => e.MatchesTaxiway(tight.OutTaxiway) && (e.OtherNode(corner.TangentOnJ).Id != corner.NearOnJ.Id));
        return (before, beyond);
    }

    /// <summary>
    /// KSFO's Q-to-B1 arc, tight only at its apex: its effective radius is its length over the heading change round the corner,
    /// wide enough for a jet's main-gear turn radius.
    /// </summary>
    [Fact]
    public void EffectiveArcRadius_KsfoQB1_IsTheArcLengthOverItsTurn()
    {
        if (KsfoQToB1() is not { } found)
        {
            return;
        }

        GroundArc arc = found.Corner.Corner.Arc;
        double lengthFt = arc.DistanceNm * GeoMath.FeetPerNm;
        double turnDeg = FollowCornerGeometry.ApexTurnDeg(found.Corner.Corner);
        double expectedFt = lengthFt / (turnDeg * Math.PI / 180.0);
        double effectiveFt = FollowRoutePlanner.EffectiveArcRadiusFt(arc);
        output.WriteLine(
            $"Q-to-B1 arc {lengthFt:F1} ft over {turnDeg:F1}°: effective radius {effectiveFt:F1} ft, tightest {arc.MinRadiusOfCurvatureFt:F1} ft"
        );

        Assert.InRange(effectiveFt, expectedFt * 0.99, expectedFt * 1.01);
        Assert.True(effectiveFt >= CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet));
    }

    /// <summary>
    /// KSFO's Q-to-B1 fillet, tight only at its apex and wide enough on its effective radius for a jet: a C172 lead that rounded
    /// it leaves the stubs either side of the junction in its trail, and a B738 follower's path takes the arc, not the stubs.
    /// </summary>
    [Fact]
    public void WideBezierFillet_JetFollowerKeepsTheArc()
    {
        if (KsfoQToB1() is not { } found)
        {
            return;
        }

        (AirportGroundLayout layout, FollowCornerGeometry.TaxiwayCorner qToB1) = found;
        CornerFollow c = FollowRoundTheCorner(layout, qToB1, "C172", "B738", leadDroveTheJunction: false);

        Assert.Contains(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, c.Corner.Arc));
        Assert.DoesNotContain(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, c.Corner.StubOnC));
        Assert.DoesNotContain(c.Plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, c.Corner.StubOnJ));
    }

    /// <summary>
    /// A lead path that runs from KOAK's 352 corner round the block by C, H, D and J and back to the corner, and a lead re-cleared
    /// over the corner's fillet arc instead: the arc is a shortcut past the loop, far longer than any square way round it the
    /// planner accepts, so the loop never stands in for the arc and the new route is a re-route.
    /// </summary>
    [Fact]
    public void LeadReclearedOverAShortcutArcPastALoopOnThePath_DoesNotMatch()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        (FollowCornerGeometry.CToJCorner corner, IReadOnlyList<GroundNode> loop) = FollowCornerGeometry.FindBlockBackToTheCorner(layout);
        GroundNode cNear = corner.NearOnC;
        GroundNode jNear = corner.NearOnJ;
        List<DirectionalEdge> path = [corner.StubOnC.Directed(cNear, corner.TangentOnC)];
        for (int i = 0; i + 1 < loop.Count; i++)
        {
            path.Add(FollowCornerGeometry.EdgeBetween(loop[i], loop[i + 1]).Directed(loop[i], loop[i + 1]));
        }

        path.Add(corner.StubOnJ.Directed(corner.TangentOnJ, jNear));
        double loopFt = path.Skip(1).SkipLast(1).Sum(e => e.DistanceNm) * GeoMath.FeetPerNm;
        double boundFt = 1.5 * FollowRoutePlanner.ArcStubLengthFt(corner.Arc);
        output.WriteLine($"loop {loopFt:F0} ft; square way bound {boundFt:F0} ft; path {PathText(path)}");
        Assert.True(loopFt > boundFt, $"the loop ({loopFt:F0} ft) is no longer than the square way bound ({boundFt:F0} ft)");

        AircraftState lead = FollowCornerGeometry.Spawn(
            layout.AirportId,
            "N1LED",
            "C172",
            cNear.Position,
            FollowCornerGeometry.Facing(cNear, corner.TangentOnC)
        );
        AircraftState follower = FollowCornerGeometry.Spawn(
            layout.AirportId,
            "N2FOL",
            "B738",
            cNear.Position,
            FollowCornerGeometry.Facing(cNear, corner.TangentOnC)
        );
        lead.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment { Edge = path[0], TaxiwayName = "C" },
                new TaxiRouteSegment { Edge = corner.Arc.Directed(corner.TangentOnC, corner.TangentOnJ), TaxiwayName = "J" },
                new TaxiRouteSegment { Edge = path[^1], TaxiwayName = "J" },
            ],
            HoldShortPoints = [],
        };

        Assert.False(
            FollowRoutePlanner.LeadRouteMatchesPath(layout, follower, lead, cNear.Id, path),
            "the loop on the old path stood in for the shortcut arc"
        );
    }

    private AirportGroundLayout? LoadLayout(string airportId)
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airportId);
        if (layout is null)
        {
            output.WriteLine($"SKIP: K{airportId} layout unavailable");
        }

        return layout;
    }

    /// <summary>
    /// KSFO's layout and its Q-to-B1 corner, a wide Bezier fillet tight only at its apex
    /// (<see cref="FollowCornerGeometry.KsfoQToB1"/>); null when the layout is unavailable.
    /// </summary>
    private (AirportGroundLayout Layout, FollowCornerGeometry.TaxiwayCorner Corner)? KsfoQToB1()
    {
        if (LoadLayout("SFO") is not { } layout)
        {
            return null;
        }

        return (layout, FollowCornerGeometry.KsfoQToB1(layout, output));
    }

    /// <summary>
    /// The first real corner on a committed layout whose arc's effective radius is under a jet's main-gear turn radius but not a
    /// piston's (<see cref="FollowCornerGeometry.ArcTighterThanAJet"/>), with its layout.
    /// </summary>
    private (AirportGroundLayout Layout, FollowCornerGeometry.TaxiwayCorner Corner)? ArcTighterThanAJet()
    {
        (string AirportId, AirportGroundLayout Layout, FollowCornerGeometry.TaxiwayCorner Corner)? found = FollowCornerGeometry.ArcTighterThanAJet(
            output,
            FollowCornerGeometry.JetRefusesSearchAirports
        );
        Assert.True(found is not null, "no committed layout has a corner whose arc a piston drives and a jet refuses");
        return (found.Value.Layout, found.Value.Corner);
    }

    /// <summary>
    /// A lead that left KOAK's 352 corner east on C, went round the block by H, D and J, and came back down J onto the corner's
    /// J stub, with the stub into the H junction missing from its 1 Hz trail: the gap fill there starts at the C tangent node
    /// the 352 fillet arc joins to the J tangent node the lead came back to. The arc stands in only for the stubs round the
    /// junction, so the planned path keeps the whole loop the lead drove, never the arc.
    /// </summary>
    [Fact]
    public void CutCorner_TrailLoopingBackToTheCorner_KeepsTheLoop()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        (FollowCornerGeometry.CToJCorner corner, IReadOnlyList<GroundNode> loop) = FollowCornerGeometry.FindBlockBackToTheCorner(layout);
        GroundNode t1 = corner.TangentOnC;
        GroundNode t2 = corner.TangentOnJ;
        GroundNode cNear = corner.NearOnC;
        GroundNode jNear = corner.NearOnJ;
        output.WriteLine($"loop {string.Join(" ", loop.Select(n => $"#{n.Id}"))}");
        GroundEdge intoBehind = FollowCornerGeometry.EdgeBetween(cNear, t1);

        AircraftState lead = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N1LED",
            "C172",
            FollowCornerGeometry.Between(t2.Position, jNear.Position, 0.5),
            FollowCornerGeometry.Facing(t2, jNear)
        );
        lead.Ground.TaxiEdgeTrail.Record(intoBehind, cNear);
        for (int i = 0; i + 1 < loop.Count; i++)
        {
            // The 1 Hz trail misses the stub from the C tangent node by the H junction into the junction.
            if (i != 1)
            {
                lead.Ground.TaxiEdgeTrail.Record(FollowCornerGeometry.EdgeBetween(loop[i], loop[i + 1]), loop[i]);
            }
        }

        lead.Ground.TaxiEdgeTrail.Record(corner.StubOnJ, t2);
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            FollowCornerGeometry.Between(cNear.Position, t1.Position, 0.5),
            FollowCornerGeometry.Facing(cNear, t1)
        );
        follower.Ground.TaxiEdgeTrail.Record(intoBehind, cNear);

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(layout, follower, lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}");

        Assert.DoesNotContain(plan.LeadPathFromMerge, e => ReferenceEquals(e.Edge, corner.Arc));
        for (int i = 0; i + 1 < loop.Count; i++)
        {
            GroundEdge loopEdge = FollowCornerGeometry.EdgeBetween(loop[i], loop[i + 1]);
            Assert.True(
                plan.LeadPathFromMerge.Any(e => ReferenceEquals(e.Edge, loopEdge)),
                $"the loop edge #{loop[i].Id}-#{loop[i + 1].Id} is not on the lead path {PathText(plan.LeadPathFromMerge)}"
            );
        }
    }

    /// <summary>
    /// A lead whose trail is the corner's two stubs (or the junction edges too, when it drove them), standing past the J
    /// tangent node on the J edge, and a follower part way along the C edge behind that corner, with that edge in its own
    /// trail: the corner and the follow's plan.
    /// </summary>
    private sealed record CornerFollow(
        FollowCornerGeometry.CToJCorner Corner,
        AircraftState Lead,
        AircraftState Follower,
        GroundEdge AheadOnJ,
        FollowRoutePlan.Joinable Plan
    );

    private CornerFollow FollowRoundTheC_J_Corner(AirportGroundLayout layout, string leadType, string followerType, bool leadDroveTheJunction) =>
        FollowRoundTheCorner(
            layout,
            new FollowCornerGeometry.TaxiwayCorner(FollowCornerGeometry.FindCToJCorner(layout), "C", "J"),
            leadType,
            followerType,
            leadDroveTheJunction
        );

    /// <summary>
    /// <see cref="FollowRoundTheC_J_Corner"/> at any corner: the lead past its out-taxiway tangent node, the follower behind
    /// its in-taxiway tangent node.
    /// </summary>
    private CornerFollow FollowRoundTheCorner(
        AirportGroundLayout layout,
        FollowCornerGeometry.TaxiwayCorner taxiwayCorner,
        string leadType,
        string followerType,
        bool leadDroveTheJunction
    )
    {
        FollowCornerGeometry.CToJCorner corner = taxiwayCorner.Corner;
        GroundNode t1 = corner.TangentOnC;
        GroundNode t2 = corner.TangentOnJ;
        GroundNode cNear = corner.NearOnC;
        GroundNode jNear = corner.NearOnJ;
        GroundEdge cBefore = t1.Edges.OfType<GroundEdge>().First(e => e.MatchesTaxiway(taxiwayCorner.InTaxiway) && (e.OtherNode(t1).Id != cNear.Id));
        GroundEdge onJ = t2.Edges.OfType<GroundEdge>().First(e => e.MatchesTaxiway(taxiwayCorner.OutTaxiway) && (e.OtherNode(t2).Id != jNear.Id));
        GroundNode cBeforeFar = cBefore.OtherNode(t1);
        GroundNode beyondJ = onJ.OtherNode(t2);
        AircraftState lead = FollowCornerGeometry.Spawn(
            layout.AirportId,
            "N1LED",
            leadType,
            FollowCornerGeometry.Between(t2.Position, beyondJ.Position, 0.5),
            FollowCornerGeometry.Facing(t2, beyondJ)
        );
        lead.Ground.TaxiEdgeTrail.Record(cBefore, cBeforeFar);
        lead.Ground.TaxiEdgeTrail.Record(corner.StubOnC, t1);
        if (leadDroveTheJunction)
        {
            lead.Ground.TaxiEdgeTrail.Record(FollowCornerGeometry.EdgeBetween(cNear, corner.ThroughRun[2]), cNear);
            lead.Ground.TaxiEdgeTrail.Record(FollowCornerGeometry.EdgeBetween(corner.ThroughRun[2], jNear), corner.ThroughRun[2]);
        }

        // Where both stubs meet at the junction, the 1 Hz trail of a lead rounding the arc holds only one of them.
        if (corner.ThroughRun.Count > 3)
        {
            lead.Ground.TaxiEdgeTrail.Record(corner.StubOnJ, jNear);
        }

        lead.Ground.TaxiEdgeTrail.Record(onJ, t2);
        AircraftState follower = FollowCornerGeometry.Spawn(
            layout.AirportId,
            "N2FOL",
            followerType,
            FollowCornerGeometry.Between(cBeforeFar.Position, t1.Position, 0.5),
            FollowCornerGeometry.Facing(cBeforeFar, t1)
        );
        follower.Ground.TaxiEdgeTrail.Record(cBefore, cBeforeFar);
        FollowRoutePlanner.RouteStart? start = FollowRoutePlanner.StartOf(layout, follower, requireAhead: true);
        string leadIn = start?.LeadIn is { } into ? $"#{into.FromNodeId}>#{into.ToNodeId}" : "none";
        output.WriteLine(
            $"follower on C #{cBeforeFar.Id}>#{t1.Id}: start #{start?.Node.Id}, lead-in {leadIn}; "
                + $"lead trail {string.Join(", ", lead.Ground.TaxiEdgeTrail.Edges.Select(e => $"#{e.NodeA}-#{e.NodeB}"))}"
        );

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(layout, follower, lead));
        output.WriteLine(
            $"merge #{plan.MergeNode}; path to merge {string.Join(" ", plan.PathToMerge.Segments.Select(s => $"#{s.FromNodeId}>#{s.ToNodeId}"))}; "
                + $"lead path {PathText(plan.LeadPathFromMerge)}"
        );
        return new CornerFollow(corner, lead, follower, onJ, plan);
    }

    /// <summary><paramref name="c"/>'s straight run round the junction, the arc's C tangent node first, then the lead's J edge on.</summary>
    private static List<DirectionalEdge> RoundTheJunction(CornerFollow c)
    {
        List<DirectionalEdge> path = [];
        for (int i = 0; i + 1 < c.Corner.ThroughRun.Count; i++)
        {
            GroundNode from = c.Corner.ThroughRun[i];
            GroundNode to = c.Corner.ThroughRun[i + 1];
            path.Add(FollowCornerGeometry.EdgeBetween(from, to).Directed(from, to));
        }

        GroundNode beyondJ = c.AheadOnJ.OtherNode(c.Corner.TangentOnJ);
        path.Add(c.AheadOnJ.Directed(c.Corner.TangentOnJ, beyondJ));
        return path;
    }

    /// <summary>
    /// A lead that is itself following has a taxi route left over from before its follow: the planner ignores it, so a follower
    /// on that stale route is not ahead of the lead, and it joins on the lead's trail or the edge it is on.
    /// </summary>
    [Fact]
    public void LeadThatFollows_StaleRouteIgnored()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        AircraftState third = FollowCornerGeometry.SpawnAtStand(run.Layout, "N3THR");
        run.Engine.World.AddAircraft(third);
        CommandResult follow = run.Engine.SendCommand(run.Lead.Callsign, $"FOLLOWG {third.Callsign}");
        Assert.True(follow.Success, follow.Message);
        Assert.IsType<FollowingPhase>(run.Lead.Phases?.CurrentPhase);
        Assert.NotNull(run.Lead.Ground.AssignedTaxiRoute);
        AircraftState follower = FollowCornerGeometry.SpawnOnRouteAhead(run.Lead, "N2FOL", "C172");

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(run.Layout, follower, run.Lead));
        output.WriteLine($"merge #{plan.MergeNode}, path to merge {plan.PathToMerge.ToSummary()}");

        GroundEdge current = Assert.IsType<GroundEdge>(TaxiEdgeLocator.EdgeUnder(run.Layout, run.Lead.Position, null));
        Assert.Empty(plan.LeadPathFromMerge);
        Assert.Contains(plan.MergeNode, new[] { current.Nodes[0].Id, current.Nodes[1].Id });
    }

    /// <summary>
    /// A follower on a part of the graph no edge joins to the lead's has no path to the lead's route: no path. The layout is the
    /// first committed test layout with such an island.
    /// </summary>
    [Fact]
    public void FollowerOnDisconnectedIsland_NoPath()
    {
        TestVnasData.EnsureInitialized();
        GraphComponents.DisconnectedPair split = Assert.IsType<GraphComponents.DisconnectedPair>(GraphComponents.FindDisconnectedPair());
        HashSet<int> island = [.. split.Island.Select(n => n.Id)];
        GroundEdge islandEdge = split
            .Island.SelectMany(n => n.Edges.OfType<GroundEdge>())
            .First(e => !e.IsRamp && !e.IsRunwayCenterline && island.Contains(e.Nodes[0].Id) && island.Contains(e.Nodes[1].Id));
        HashSet<int> main = GraphComponents.ComponentOf(split.MainNode);
        GroundEdge mainEdge = split
            .Layout.Nodes.Values.Where(n => !island.Contains(n.Id))
            .SelectMany(n => n.Edges.OfType<GroundEdge>())
            .First(e => !e.IsRamp && !e.IsRunwayCenterline && main.Contains(e.Nodes[0].Id));
        output.WriteLine($"{split.Layout.AirportId}: island edge {islandEdge.TaxiwayName} #{islandEdge.Nodes[0].Id}-#{islandEdge.Nodes[1].Id}");

        AircraftState lead = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N1LED",
            "C560",
            FollowCornerGeometry.Between(mainEdge.Nodes[0].Position, mainEdge.Nodes[1].Position, 0.5),
            FollowCornerGeometry.Facing(mainEdge.Nodes[0], mainEdge.Nodes[1])
        );
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            FollowCornerGeometry.Between(islandEdge.Nodes[0].Position, islandEdge.Nodes[1].Position, 0.5),
            FollowCornerGeometry.Facing(islandEdge.Nodes[0], islandEdge.Nodes[1])
        );

        Assert.IsType<FollowRoutePlan.NoPath>(FollowRoutePlanner.Plan(split.Layout, follower, lead));
    }

    /// <summary>
    /// The along-path gap from a follower at B <c>Chain[6]</c>, joining at <c>Chain[5]</c>, to a lead part way along the
    /// <c>Chain[2]</c>→<c>Chain[1]</c> edge is the follower's straight leg to the merge, the B edges from the merge to the lead's
    /// edge and the lead's way along it, less half of each aircraft's length.
    /// </summary>
    [Fact]
    public void AlongPathGap_IsTheEdgesBetweenLessBothHalfLengths()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        BFollow b = FollowOnB(layout);
        double edgesFt = Enumerable.Range(2, 3).Sum(i => FollowCornerGeometry.EdgeBetween(b.Chain[i + 1], b.Chain[i]).DistanceNm) * GeoMath.FeetPerNm;
        double followerLegFt = GeoMath.DistanceNm(b.Chain[6].Position, b.Chain[5].Position) * GeoMath.FeetPerNm;
        double expectedFt =
            followerLegFt + edgesFt + b.LeadAlongFt - (AircraftLength.ResolveFt("C172") / 2.0) - (AircraftLength.ResolveFt("B738") / 2.0);

        double? toMergeFt = FollowRoutePlanner.FollowerToMergeFt(layout, b.Plan, b.Follower);
        PathPosition? leadAt = FollowRoutePlanner.LocateOnPath(layout, b.Plan.LeadPathFromMerge, b.Lead);
        double? gapFt = FollowRoutePlanner.AlongPathGapFt(toMergeFt, b.Plan.LeadPathFromMerge, leadAt, "C172", "B738");
        output.WriteLine($"to merge {toMergeFt:F1} ft, lead at {leadAt}, gap {gapFt:F1} ft, expected {expectedFt:F1} ft");

        Assert.NotNull(gapFt);
        Assert.Equal(expectedFt, gapFt.Value, 1.0);
    }

    /// <summary>A lead off the planned path has no along-path gap: the caller falls back to the straight-line distance.</summary>
    [Fact]
    public void AlongPathGap_LeadOffThePath_IsNull()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        BFollow b = FollowOnB(layout);
        AircraftState away = FollowCornerGeometry.Spawn(FollowCornerGeometry.AirportId, "N1LED", "B738", b.Follower.Position, b.Follower.TrueHeading);

        PathPosition? leadAt = FollowRoutePlanner.LocateOnPath(layout, b.Plan.LeadPathFromMerge, away);

        Assert.Null(leadAt);
        Assert.Null(FollowRoutePlanner.AlongPathGapFt(0.0, b.Plan.LeadPathFromMerge, leadAt, "C172", "B738"));
    }

    /// <summary>
    /// The lead has cleared the merge once its tail — half its length behind its centre, along its path — is past the merge
    /// node: 2 ft short of that, not yet; 2 ft past it, cleared.
    /// </summary>
    [Fact]
    public void LeadTailPastMerge_FlipsWhenTheTailPassesTheMerge()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        BFollow b = FollowOnB(layout);
        double halfLengthFt = AircraftLength.ResolveFt("B738") / 2.0;
        bool TailPast(double alongPathFt)
        {
            (LatLon at, TrueHeading heading) = PointAlong(b.Plan.LeadPathFromMerge, alongPathFt);
            AircraftState probe = FollowCornerGeometry.Spawn(FollowCornerGeometry.AirportId, "N1LED", "B738", at, heading);
            PathPosition leadAt = Assert.IsType<PathPosition>(FollowRoutePlanner.LocateOnPath(layout, b.Plan.LeadPathFromMerge, probe));
            Assert.Equal(alongPathFt, FollowRoutePlanner.PathOffsetFt(b.Plan.LeadPathFromMerge, leadAt), 0.5);
            return FollowRoutePlanner.LeadTailPastMerge(b.Plan.LeadPathFromMerge, leadAt, "B738");
        }

        Assert.False(TailPast(halfLengthFt - 2.0));
        Assert.True(TailPast(halfLengthFt + 2.0));
    }

    /// <summary>
    /// A follower at B <c>Chain[6]</c>, joining the trail of a lead taxiing on B a few B edges on, has a route of several
    /// segments to the merge: its distance to the merge is its straight leg to the first segment's end, then the rest of the
    /// route's edges by their lengths.
    /// </summary>
    [Fact]
    public void FollowerToMerge_AlongARouteOfSeveralSegments_IsItsEdgesSummed()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        FollowRoutePlan.Joinable plan = TrailJoinFromChain6(run);
        List<TaxiRouteSegment> segments = plan.PathToMerge.Segments;
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            run.Chain[6].Position,
            FollowCornerGeometry.Facing(run.Chain[6], run.Chain[5])
        );
        double expectedFt =
            (GeoMath.DistanceNm(run.Chain[6].Position, segments[0].Edge.ToNode.Position) + segments.Skip(1).Sum(s => s.Edge.DistanceNm))
            * GeoMath.FeetPerNm;

        double? toMergeFt = FollowRoutePlanner.FollowerToMergeFt(run.Layout, plan, follower);

        Assert.NotNull(toMergeFt);
        Assert.Equal(expectedFt, toMergeFt.Value, 0.01);
    }

    /// <summary>
    /// A follower part way along the second segment of its route to the merge, the route's index moved on to it, is measured
    /// from there: its straight leg to that segment's end, then the segments after it.
    /// </summary>
    [Fact]
    public void FollowerToMerge_OnALaterSegment_CountsOnlyTheRouteAhead()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        FollowRoutePlan.Joinable plan = TrailJoinFromChain6(run);
        List<TaxiRouteSegment> segments = plan.PathToMerge.Segments;
        plan.PathToMerge.CurrentSegmentIndex = 1;
        TaxiRouteSegment second = segments[1];
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            FollowCornerGeometry.Between(second.Edge.FromNode.Position, second.Edge.ToNode.Position, 0.5),
            FollowCornerGeometry.Facing(second.Edge.FromNode, second.Edge.ToNode)
        );
        double expectedFt =
            (GeoMath.DistanceNm(follower.Position, second.Edge.ToNode.Position) + segments.Skip(2).Sum(s => s.Edge.DistanceNm)) * GeoMath.FeetPerNm;

        double? toMergeFt = FollowRoutePlanner.FollowerToMergeFt(run.Layout, plan, follower);

        Assert.NotNull(toMergeFt);
        Assert.Equal(expectedFt, toMergeFt.Value, 0.01);
    }

    /// <summary>
    /// The plan of a follower at B <c>Chain[6]</c> facing the runway, behind a lead taxiing on B, with three segments or more
    /// to the merge.
    /// </summary>
    private FollowRoutePlan.Joinable TrailJoinFromChain6(FollowCornerGeometry.LeadRun run)
    {
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            run.Chain[6].Position,
            FollowCornerGeometry.Facing(run.Chain[6], run.Chain[5])
        );
        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(run.Layout, follower, run.Lead));
        output.WriteLine(
            $"merge #{plan.MergeNode}; path to merge {string.Join(" ", plan.PathToMerge.Segments.Select(s => $"#{s.FromNodeId}>#{s.ToNodeId}"))}"
        );
        Assert.True(plan.PathToMerge.Segments.Count >= 3, $"only {plan.PathToMerge.Segments.Count} segments to the merge");
        return plan;
    }

    /// <summary>
    /// KOAK's longest straight B edge, from its first node to its second, a B738 lead and a C172 follower on it, and the
    /// follow's plan.
    /// </summary>
    private sealed record SameEdge(GroundNode From, GroundNode To, AircraftState Lead, FollowRoutePlan.Joinable Plan);

    /// <summary>
    /// A B738 lead 0.7 along KOAK's longest straight B edge, facing its second node, its trail the edge before and that edge;
    /// and a C172 follower 0.3 along it, behind the lead, facing the lead or away from it.
    /// </summary>
    private SameEdge FollowBehindOnLongestB(AirportGroundLayout layout, bool followerFacesLead)
    {
        GroundEdge shared = layout
            .Nodes.Values.SelectMany(n => n.Edges.OfType<GroundEdge>())
            .Where(e => e.MatchesTaxiway("B") && !e.IsRunwayCenterline && !e.IsRamp)
            .MaxBy(e => e.DistanceNm)!;
        GroundNode from = shared.Nodes[0];
        GroundNode to = shared.Nodes[1];
        GroundEdge before = from.Edges.OfType<GroundEdge>().First(e => (e != shared) && !e.IsRunwayCenterline && !e.IsRamp);
        output.WriteLine(
            $"shared edge #{from.Id}>#{to.Id}, {shared.DistanceNm * GeoMath.FeetPerNm:F0} ft; trail edge before it {before.TaxiwayName}"
        );
        Assert.True(shared.DistanceNm * GeoMath.FeetPerNm >= 400.0, "KOAK's longest B edge is under 400 ft");
        AircraftState lead = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N1LED",
            "B738",
            FollowCornerGeometry.Between(from.Position, to.Position, 0.7),
            FollowCornerGeometry.Facing(from, to)
        );
        lead.Ground.TaxiEdgeTrail.Record(before, before.OtherNode(from));
        lead.Ground.TaxiEdgeTrail.Record(shared, from);
        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            FollowCornerGeometry.Between(from.Position, to.Position, 0.3),
            followerFacesLead ? FollowCornerGeometry.Facing(from, to) : FollowCornerGeometry.Facing(to, from)
        );

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(layout, follower, lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}; path to merge {plan.PathToMerge.ToSummary()}");
        return new SameEdge(from, to, lead, plan);
    }

    /// <summary>
    /// A follower at B <c>Chain[6]</c> facing the runway, and its lead's plan on B: the lead part way along
    /// <c>Chain[2]</c>→<c>Chain[1]</c>.
    /// </summary>
    private sealed record BFollow(
        List<GroundNode> Chain,
        AircraftState Lead,
        double LeadAlongFt,
        AircraftState Follower,
        FollowRoutePlan.Joinable Plan
    );

    /// <summary>
    /// A B738 lead half way along the first straight piece of the B edge <c>Chain[2]</c>→<c>Chain[1]</c>, its trail the B edges
    /// from <c>Chain[5]</c> to it, and a C172 follower at <c>Chain[6]</c> facing <c>Chain[5]</c>, where it joins.
    /// </summary>
    private BFollow FollowOnB(AirportGroundLayout layout)
    {
        List<GroundNode> chain = FollowCornerGeometry.BChain(layout);
        GroundEdge leadEdge = FollowCornerGeometry.EdgeBetween(chain[2], chain[1]);
        LatLon pieceEnd = EdgeGeometry.PointsFrom(leadEdge, chain[2])[1];
        double leadAlongFt = GeoMath.DistanceNm(chain[2].Position, pieceEnd) * GeoMath.FeetPerNm / 2.0;
        var heading = new TrueHeading(GeoMath.BearingTo(chain[2].Position, pieceEnd));
        AircraftState lead = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N1LED",
            "B738",
            GeoMath.ProjectPoint(chain[2].Position, heading, leadAlongFt / GeoMath.FeetPerNm),
            heading
        );
        for (int i = 5; i >= 2; i--)
        {
            lead.Ground.TaxiEdgeTrail.Record(FollowCornerGeometry.EdgeBetween(chain[i], chain[i - 1]), chain[i]);
        }

        AircraftState follower = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            "N2FOL",
            "C172",
            chain[6].Position,
            FollowCornerGeometry.Facing(chain[6], chain[5])
        );
        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(layout, follower, lead));
        output.WriteLine($"merge #{plan.MergeNode}; lead path {PathText(plan.LeadPathFromMerge)}; path to merge {plan.PathToMerge.ToSummary()}");
        Assert.Equal(chain[5].Id, plan.MergeNode);
        Assert.Equal(chain[1].Id, plan.LeadPathFromMerge[^1].ToNodeId);
        return new BFollow(chain, lead, leadAlongFt, follower, plan);
    }

    /// <summary>
    /// The point <paramref name="alongFt"/> along <paramref name="path"/>'s straight edges from its start, and the way the path
    /// runs there.
    /// </summary>
    private static (LatLon At, TrueHeading Heading) PointAlong(IReadOnlyList<DirectionalEdge> path, double alongFt)
    {
        double leftFt = alongFt;
        foreach (DirectionalEdge edge in path)
        {
            List<LatLon> points = EdgeGeometry.PointsFrom(Assert.IsType<GroundEdge>(edge.Edge), edge.FromNode);
            for (int k = 1; k < points.Count; k++)
            {
                double pieceFt = GeoMath.DistanceNm(points[k - 1], points[k]) * GeoMath.FeetPerNm;
                var heading = new TrueHeading(GeoMath.BearingTo(points[k - 1], points[k]));
                if (leftFt <= pieceFt)
                {
                    return (GeoMath.ProjectPoint(points[k - 1], heading, leftFt / GeoMath.FeetPerNm), heading);
                }

                leftFt -= pieceFt;
            }
        }

        throw new InvalidOperationException($"the path is shorter than {alongFt:F0} ft");
    }

    private static HashSet<int> TrailNodeIds(AircraftState aircraft) =>
        [.. aircraft.Ground.TaxiEdgeTrail.Edges.SelectMany(e => new[] { e.NodeA, e.NodeB })];

    private static string PathText(IEnumerable<DirectionalEdge> edges) => string.Join(" ", edges.Select(e => $"#{e.FromNodeId}>#{e.ToNodeId}"));

    private static bool IsStraightTaxi(TaxiRouteSegment segment) => (segment.Edge.Edge is GroundEdge) && !segment.Edge.Edge.IsRunwayCenterline;

    /// <summary>
    /// Puts the lead at the end of the first straight route segment, from its current one on, that is clear of its trail, at
    /// least 100 ft long and passes <paramref name="alsoWhere"/> (given the segment's index): 0.9 along it, part way through a
    /// sharp turn that has swung its nose 110° off the segment. Returns the segment's index.
    /// </summary>
    private static int PutLeadMidTurn(FollowCornerGeometry.LeadRun run, Func<int, bool> alsoWhere)
    {
        TaxiRoute route = Assert.IsType<TaxiRoute>(run.Lead.Ground.AssignedTaxiRoute);
        HashSet<int> trail = TrailNodeIds(run.Lead);
        int index = Enumerable
            .Range(route.CurrentSegmentIndex, route.Segments.Count - route.CurrentSegmentIndex)
            .First(i =>
                IsStraightTaxi(route.Segments[i])
                && !trail.Contains(route.Segments[i].FromNodeId)
                && (route.Segments[i].Edge.DistanceNm * GeoMath.FeetPerNm >= 100.0)
                && alsoWhere(i)
            );
        TaxiRouteSegment segment = route.Segments[index];
        run.Lead.Position = FollowCornerGeometry.Between(segment.Edge.FromNode.Position, segment.Edge.ToNode.Position, 0.9);
        run.Lead.TrueHeading = FollowCornerGeometry.Facing(segment.Edge.FromNode, segment.Edge.ToNode) + 110.0;
        return index;
    }

    /// <summary>
    /// A junction J of three straight edges: a side edge to A, which has a straight edge of its own on to A2, and two taxiway
    /// edges on to W1 and E1. The lead pushes from A through J out to W1, then taxis back through J toward E1.
    /// </summary>
    private sealed record PushGeometry(GroundNode A2, GroundNode A, GroundNode J, GroundNode W1, GroundNode E1, GroundEdge Side);

    /// <summary>The first KOAK junction, by node id, with the shape <see cref="PushGeometry"/> describes.</summary>
    private static PushGeometry FindPushGeometry(AirportGroundLayout layout)
    {
        foreach (GroundNode j in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            List<GroundEdge> straight = [.. j.Edges.OfType<GroundEdge>().Where(e => !e.IsRunwayCenterline)];
            List<GroundEdge> taxi = [.. straight.Where(e => !e.IsRamp && (e.DistanceNm * GeoMath.FeetPerNm >= 50.0))];
            foreach (GroundEdge side in straight)
            {
                GroundNode a = side.OtherNode(j);
                List<GroundNode> ends = [.. taxi.Where(e => e != side).Select(e => e.OtherNode(j))];
                GroundEdge? beyond = a
                    .Edges.OfType<GroundEdge>()
                    .FirstOrDefault(e => !e.IsRunwayCenterline && (e.OtherNode(a).Id != j.Id) && ends.All(n => n.Id != e.OtherNode(a).Id));
                if ((ends.Count >= 2) && (beyond is not null))
                {
                    return new PushGeometry(beyond.OtherNode(a), a, j, ends[0], ends[1], side);
                }
            }
        }

        Assert.Fail("no KOAK junction has a side edge with an edge beyond it and two taxiway edges");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>The end of <paramref name="edge"/> nearer the way <paramref name="aircraft"/> faces.</summary>
    private static GroundNode FacedEnd(GroundEdge edge, AircraftState aircraft)
    {
        GroundNode a = edge.Nodes[0];
        GroundNode b = edge.Nodes[1];
        double toA = aircraft.TrueHeading.AbsAngleTo(new TrueHeading(GeoMath.BearingTo(aircraft.Position, a.Position)));
        double toB = aircraft.TrueHeading.AbsAngleTo(new TrueHeading(GeoMath.BearingTo(aircraft.Position, b.Position)));
        return toB <= toA ? b : a;
    }

    /// <summary>
    /// The edges of one-way taxiway <paramref name="taxiway"/> in their permitted direction, entry first: every move the
    /// airport's one-way constraints forbid for <paramref name="wakeClass"/> on that taxiway, reversed and chained.
    /// </summary>
    private static List<DirectionalEdge> LegalLane(AirportGroundLayout layout, string taxiway, WakeTurbulenceData.WakeClass wakeClass)
    {
        Dictionary<int, DirectionalEdge> next = [];
        foreach ((int from, int to) in OneWayResolver.GetForbiddenMoves(layout, wakeClass))
        {
            GroundNode legalFrom = layout.Nodes[to];
            if (legalFrom.Edges.FirstOrDefault(e => (e.OtherNode(legalFrom).Id == from) && e.MatchesTaxiway(taxiway)) is { } edge)
            {
                Assert.True(
                    next.TryAdd(
                        to,
                        new DirectionalEdge
                        {
                            Edge = edge,
                            FromNode = legalFrom,
                            ToNode = layout.Nodes[from],
                        }
                    ),
                    $"{taxiway} branches at #{to}"
                );
            }
        }

        HashSet<int> entered = [.. next.Values.Select(e => e.ToNodeId)];
        int node = Assert.Single(next.Keys, id => !entered.Contains(id));
        List<DirectionalEdge> lane = [];
        while (next.TryGetValue(node, out DirectionalEdge? edge))
        {
            lane.Add(edge);
            node = edge.ToNodeId;
        }

        return lane;
    }
}
