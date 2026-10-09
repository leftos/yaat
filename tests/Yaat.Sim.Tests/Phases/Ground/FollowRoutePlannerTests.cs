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
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        AircraftState follower = KoakFollowGeometry.Spawn(
            "N2FOL",
            "C172",
            run.Chain[6].Position,
            KoakFollowGeometry.Facing(run.Chain[6], run.Chain[5])
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
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
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
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
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
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
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
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
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

    private RampFillet TaxiHalfwayRoundTheRampFillet(KoakFollowGeometry.LeadRun run)
    {
        FilletChord pose = PoseOnFilletChord(run);
        GroundNode rampEnd = run.Chain[6];
        GroundNode rampNode = pose.RampEdge.OtherNode(rampEnd);
        Assert.True(pose.RampEdge.IsRamp, $"#{rampNode.Id}-#{rampEnd.Id} is not a ramp edge");
        GroundArc fillet = rampEnd.Edges.OfType<GroundArc>().First(e => e.OtherNode(rampEnd).Id == run.Chain[5].Id);
        AircraftState follower = KoakFollowGeometry.AddTaxiing((run.Engine, run.Layout), "N2FOL", "C172", (rampNode, rampEnd), "TAXI B W 30");
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
        if (KoakFollowGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        (AirportGroundLayout Layout, AircraftState Pushed)? found = null;
        foreach (GroundNode stand in layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Edges.Count > 0)).OrderBy(n => n.Id))
        {
            (SimulationEngine Engine, AirportGroundLayout Layout) candidate = Assert.NotNull(KoakFollowGeometry.NewEngine(output, autoCross: false));
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
        AircraftState aircraft = KoakFollowGeometry.Spawn("N3PSH", "C172", stand.Position, new TrueHeading(0));
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
        if (KoakFollowGeometry.NewEngine(output, autoCross: false) is not { } setup)
        {
            return;
        }

        List<GroundNode> chain = KoakFollowGeometry.BChain(setup.Layout);
        AircraftState departure = KoakFollowGeometry.AddTaxiing(setup, "N3RTO", "C172", (chain[3], chain[2]), "TAXI B 28R");
        AircraftState lead = KoakFollowGeometry.AddTaxiing(setup, "N1LED", "C172", (chain[6], chain[5]), "TAXI B 28R");
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
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
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
    private FilletChord PoseOnFilletChord(KoakFollowGeometry.LeadRun run)
    {
        GroundNode rampEnd = run.Chain[6];
        GroundNode onB = run.Chain[5];
        Assert.Contains(rampEnd.Edges, e => (e is GroundArc) && (e.OtherNode(rampEnd).Id == onB.Id));
        GroundNode junction = onB
            .Edges.OfType<GroundEdge>()
            .Where(e => e.MatchesTaxiway("B"))
            .Select(e => e.OtherNode(onB))
            .First(n => n.Edges.Any(c => c.MatchesTaxiway("C")));
        TrueHeading heading = KoakFollowGeometry.Facing(rampEnd, onB);
        GroundNode cFar = junction
            .Edges.OfType<GroundEdge>()
            .Where(e => e.MatchesTaxiway("C"))
            .Select(e => e.OtherNode(junction))
            .OrderBy(n => Math.Abs(GeoMath.SignedBearingDifference(heading.Degrees, GeoMath.BearingTo(junction.Position, n.Position))))
            .First();
        GroundEdge rampEdge = rampEnd.Edges.OfType<GroundEdge>().First(e => e.OtherNode(rampEnd).Id != junction.Id);
        LatLon at = KoakFollowGeometry.Between(rampEnd.Position, onB.Position, 0.5);
        double offCFt = GeoMath.DistanceToSegmentFt(at, junction.Position, cFar.Position);
        output.WriteLine(
            $"on the #{rampEnd.Id}->#{onB.Id} chord, {offCFt:F1} ft off C #{junction.Id}->#{cFar.Id}; came off #{rampEdge.OtherNode(rampEnd).Id}"
        );
        Assert.Equal(cFar.Id, run.Layout.FindTaxiStartNode(at, heading)?.Id);
        return new FilletChord(KoakFollowGeometry.Spawn("N2FOL", "C172", at, heading), cFar, rampEdge);
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
        if (KoakFollowGeometry.LoadLayout(output) is not { } layout)
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
        if (KoakFollowGeometry.LoadLayout(output) is not { } layout)
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
        if (KoakFollowGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        SameEdge s = FollowBehindOnLongestB(layout, followerFacesLead: true);
        (LatLon at, TrueHeading heading) = PointAlong(s.Plan.LeadPathFromMerge, 200.0);
        AircraftState probe = KoakFollowGeometry.Spawn("N2FOL", "C172", at, heading);

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
        if (KoakFollowGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        SameEdge s = FollowBehindOnLongestB(layout, followerFacesLead: true);
        (LatLon at, TrueHeading heading) = PointAlong(s.Plan.LeadPathFromMerge, 200.0);
        AircraftState probe = KoakFollowGeometry.Spawn("N2FOL", "C172", GeoMath.ProjectPoint(at, heading + 90.0, 60.0 / GeoMath.FeetPerNm), heading);

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
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        GroundNode node = run.Chain[3];
        Assert.Contains(node.Id, TrailNodeIds(run.Lead));
        AircraftState follower = KoakFollowGeometry.Spawn("N2FOL", "C172", node.Position, KoakFollowGeometry.Facing(node, run.Chain[2]));

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
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
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
        AircraftState follower = KoakFollowGeometry.Spawn("N2FOL", "C172", far.Position, KoakFollowGeometry.Facing(far, junction));

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
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        AircraftState third = KoakFollowGeometry.SpawnAtStand(run.Layout, "N3THR");
        run.Engine.World.AddAircraft(third);
        CommandResult follow = run.Engine.SendCommand(run.Lead.Callsign, $"FOLLOWG {third.Callsign}");
        Assert.True(follow.Success, follow.Message);

        GroundEdge edge = Assert.IsType<GroundEdge>(TaxiEdgeLocator.EdgeUnder(run.Layout, run.Lead.Position, null));
        GroundNode faced = FacedEnd(edge, run.Lead);
        GroundNode back = edge.OtherNode(faced);
        run.Lead.Position = KoakFollowGeometry.Between(back.Position, faced.Position, 0.3);
        AircraftState follower = KoakFollowGeometry.Spawn(
            "N2FOL",
            "C172",
            KoakFollowGeometry.Between(back.Position, faced.Position, 0.7),
            KoakFollowGeometry.Facing(faced, back)
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
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        TaxiRoute route = Assert.IsType<TaxiRoute>(run.Lead.Ground.AssignedTaxiRoute);
        TaxiRouteSegment segment = route.Segments[PutLeadMidTurn(run, _ => true)];
        AircraftState follower = KoakFollowGeometry.Spawn(
            "N2FOL",
            "C172",
            run.Chain[6].Position,
            KoakFollowGeometry.Facing(run.Chain[6], run.Chain[5])
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
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        TaxiRoute route = Assert.IsType<TaxiRoute>(run.Lead.Ground.AssignedTaxiRoute);
        int index = PutLeadMidTurn(run, i => (i > 0) && IsStraightTaxi(route.Segments[i - 1]));
        route.CurrentSegmentIndex = index - 1;
        TaxiRouteSegment passed = route.Segments[index - 1];
        AircraftState follower = KoakFollowGeometry.Spawn(
            "N2FOL",
            "C172",
            KoakFollowGeometry.Between(passed.Edge.FromNode.Position, passed.Edge.ToNode.Position, 0.5),
            KoakFollowGeometry.Facing(passed.Edge.FromNode, passed.Edge.ToNode)
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
        if (KoakFollowGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        PushGeometry push = FindPushGeometry(layout);
        output.WriteLine($"A2 #{push.A2.Id}, A #{push.A.Id}, J #{push.J.Id}, W1 #{push.W1.Id}, E1 #{push.E1.Id}");

        AircraftState lead = KoakFollowGeometry.Spawn(
            "N1LED",
            "C560",
            KoakFollowGeometry.Between(push.J.Position, push.E1.Position, 0.5),
            KoakFollowGeometry.Facing(push.J, push.E1)
        );
        lead.Ground.TaxiEdgeTrail.Record(push.Side, push.A);
        lead.Ground.TaxiEdgeTrail.Record(KoakFollowGeometry.EdgeBetween(push.J, push.W1), push.J);
        lead.Ground.TaxiEdgeTrail.Record(KoakFollowGeometry.EdgeBetween(push.J, push.E1), push.J);
        AircraftState follower = KoakFollowGeometry.Spawn(
            "N2FOL",
            "C172",
            KoakFollowGeometry.Between(push.A2.Position, push.A.Position, 0.5),
            KoakFollowGeometry.Facing(push.A2, push.A)
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

        AircraftState lead = KoakFollowGeometry.Spawn(
            "N1LED",
            "B738",
            KoakFollowGeometry.Between(last.FromNode.Position, last.ToNode.Position, 0.5),
            KoakFollowGeometry.Facing(last.FromNode, last.ToNode)
        );
        lead.Ground.TaxiEdgeTrail.Record((GroundEdge)first.Edge, first.FromNode);
        lead.Ground.TaxiEdgeTrail.Record((GroundEdge)last.Edge, last.FromNode);
        AircraftState follower = KoakFollowGeometry.Spawn(
            "N2FOL",
            "B738",
            first.FromNode.Position,
            KoakFollowGeometry.Facing(first.FromNode, first.ToNode)
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
        if (KoakFollowGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        AircraftState lead = KoakFollowGeometry.Spawn(
            "N1LED",
            "C172",
            KoakFollowGeometry.Between(chain[1].Position, chain[2].Position, 0.5),
            KoakFollowGeometry.Facing(chain[1], chain[2])
        );
        AircraftState follower = KoakFollowGeometry.Spawn(
            "N2FOL",
            "C172",
            KoakFollowGeometry.Between(chain[4].Position, chain[5].Position, 0.5),
            KoakFollowGeometry.Facing(chain[4], chain[5])
        );
        follower.Ground.TaxiEdgeTrail.Record(KoakFollowGeometry.EdgeBetween(chain[3], chain[4]), chain[3]);
        follower.Ground.TaxiEdgeTrail.Record(KoakFollowGeometry.EdgeBetween(chain[4], chain[5]), chain[4]);

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
        AircraftState stuck = KoakFollowGeometry.Spawn(
            "N3FOL",
            "C172",
            KoakFollowGeometry.Between(mouth.Position, deadEnd.Position, 0.5),
            KoakFollowGeometry.Facing(mouth, deadEnd)
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
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        AircraftState follower = KoakFollowGeometry.SpawnOnRouteAhead(run.Lead, "N2FOL", "C172");

        Assert.IsType<FollowRoutePlan.FollowerAhead>(FollowRoutePlanner.Plan(run.Layout, follower, run.Lead));
    }

    /// <summary>A lead parked on its stand is not on a taxiway yet: the follower waits for it.</summary>
    [Fact]
    public void LeadOnStand_Waits()
    {
        if (KoakFollowGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        AircraftState lead = KoakFollowGeometry.SpawnAtStand(layout, "N1LED");
        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        AircraftState follower = KoakFollowGeometry.Spawn("N2FOL", "C172", chain[6].Position, KoakFollowGeometry.Facing(chain[6], chain[5]));

        Assert.IsType<FollowRoutePlan.WaitForLead>(FollowRoutePlanner.Plan(layout, follower, lead));
    }

    /// <summary>
    /// A trail whose consecutive edges do not meet (short edges skipped between samples) is filled in: the lead path from the
    /// merge runs edge to edge, each edge starting where the one before ends, through the skipped B nodes.
    /// </summary>
    [Fact]
    public void TrailWithGaps_LeadPathIsConnected()
    {
        if (KoakFollowGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        AircraftState lead = KoakFollowGeometry.Spawn(
            "N1LED",
            "C560",
            KoakFollowGeometry.Between(chain[2].Position, chain[1].Position, 0.5),
            KoakFollowGeometry.Facing(chain[2], chain[1])
        );
        lead.Ground.TaxiEdgeTrail.Record(KoakFollowGeometry.EdgeBetween(chain[5], chain[4]), chain[5]);
        lead.Ground.TaxiEdgeTrail.Record(KoakFollowGeometry.EdgeBetween(chain[2], chain[1]), chain[2]);
        AircraftState follower = KoakFollowGeometry.Spawn("N2FOL", "C172", chain[6].Position, KoakFollowGeometry.Facing(chain[6], chain[5]));

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
    /// A lead that is itself following has a taxi route left over from before its follow: the planner ignores it, so a follower
    /// on that stale route is not ahead of the lead, and it joins on the lead's trail or the edge it is on.
    /// </summary>
    [Fact]
    public void LeadThatFollows_StaleRouteIgnored()
    {
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        AircraftState third = KoakFollowGeometry.SpawnAtStand(run.Layout, "N3THR");
        run.Engine.World.AddAircraft(third);
        CommandResult follow = run.Engine.SendCommand(run.Lead.Callsign, $"FOLLOWG {third.Callsign}");
        Assert.True(follow.Success, follow.Message);
        Assert.IsType<FollowingPhase>(run.Lead.Phases?.CurrentPhase);
        Assert.NotNull(run.Lead.Ground.AssignedTaxiRoute);
        AircraftState follower = KoakFollowGeometry.SpawnOnRouteAhead(run.Lead, "N2FOL", "C172");

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

        AircraftState lead = KoakFollowGeometry.Spawn(
            "N1LED",
            "C560",
            KoakFollowGeometry.Between(mainEdge.Nodes[0].Position, mainEdge.Nodes[1].Position, 0.5),
            KoakFollowGeometry.Facing(mainEdge.Nodes[0], mainEdge.Nodes[1])
        );
        AircraftState follower = KoakFollowGeometry.Spawn(
            "N2FOL",
            "C172",
            KoakFollowGeometry.Between(islandEdge.Nodes[0].Position, islandEdge.Nodes[1].Position, 0.5),
            KoakFollowGeometry.Facing(islandEdge.Nodes[0], islandEdge.Nodes[1])
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
        if (KoakFollowGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        BFollow b = FollowOnB(layout);
        double edgesFt = Enumerable.Range(2, 3).Sum(i => KoakFollowGeometry.EdgeBetween(b.Chain[i + 1], b.Chain[i]).DistanceNm) * GeoMath.FeetPerNm;
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
        if (KoakFollowGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        BFollow b = FollowOnB(layout);
        AircraftState away = KoakFollowGeometry.Spawn("N1LED", "B738", b.Follower.Position, b.Follower.TrueHeading);

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
        if (KoakFollowGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        BFollow b = FollowOnB(layout);
        double halfLengthFt = AircraftLength.ResolveFt("B738") / 2.0;
        bool TailPast(double alongPathFt)
        {
            (LatLon at, TrueHeading heading) = PointAlong(b.Plan.LeadPathFromMerge, alongPathFt);
            AircraftState probe = KoakFollowGeometry.Spawn("N1LED", "B738", at, heading);
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
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        FollowRoutePlan.Joinable plan = TrailJoinFromChain6(run);
        List<TaxiRouteSegment> segments = plan.PathToMerge.Segments;
        AircraftState follower = KoakFollowGeometry.Spawn(
            "N2FOL",
            "C172",
            run.Chain[6].Position,
            KoakFollowGeometry.Facing(run.Chain[6], run.Chain[5])
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
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        FollowRoutePlan.Joinable plan = TrailJoinFromChain6(run);
        List<TaxiRouteSegment> segments = plan.PathToMerge.Segments;
        plan.PathToMerge.CurrentSegmentIndex = 1;
        TaxiRouteSegment second = segments[1];
        AircraftState follower = KoakFollowGeometry.Spawn(
            "N2FOL",
            "C172",
            KoakFollowGeometry.Between(second.Edge.FromNode.Position, second.Edge.ToNode.Position, 0.5),
            KoakFollowGeometry.Facing(second.Edge.FromNode, second.Edge.ToNode)
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
    private FollowRoutePlan.Joinable TrailJoinFromChain6(KoakFollowGeometry.LeadRun run)
    {
        AircraftState follower = KoakFollowGeometry.Spawn(
            "N2FOL",
            "C172",
            run.Chain[6].Position,
            KoakFollowGeometry.Facing(run.Chain[6], run.Chain[5])
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
        AircraftState lead = KoakFollowGeometry.Spawn(
            "N1LED",
            "B738",
            KoakFollowGeometry.Between(from.Position, to.Position, 0.7),
            KoakFollowGeometry.Facing(from, to)
        );
        lead.Ground.TaxiEdgeTrail.Record(before, before.OtherNode(from));
        lead.Ground.TaxiEdgeTrail.Record(shared, from);
        AircraftState follower = KoakFollowGeometry.Spawn(
            "N2FOL",
            "C172",
            KoakFollowGeometry.Between(from.Position, to.Position, 0.3),
            followerFacesLead ? KoakFollowGeometry.Facing(from, to) : KoakFollowGeometry.Facing(to, from)
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
        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        GroundEdge leadEdge = KoakFollowGeometry.EdgeBetween(chain[2], chain[1]);
        LatLon pieceEnd = TugMovePlanner.EdgePointsFrom(leadEdge, chain[2])[1];
        double leadAlongFt = GeoMath.DistanceNm(chain[2].Position, pieceEnd) * GeoMath.FeetPerNm / 2.0;
        var heading = new TrueHeading(GeoMath.BearingTo(chain[2].Position, pieceEnd));
        AircraftState lead = KoakFollowGeometry.Spawn(
            "N1LED",
            "B738",
            GeoMath.ProjectPoint(chain[2].Position, heading, leadAlongFt / GeoMath.FeetPerNm),
            heading
        );
        for (int i = 5; i >= 2; i--)
        {
            lead.Ground.TaxiEdgeTrail.Record(KoakFollowGeometry.EdgeBetween(chain[i], chain[i - 1]), chain[i]);
        }

        AircraftState follower = KoakFollowGeometry.Spawn("N2FOL", "C172", chain[6].Position, KoakFollowGeometry.Facing(chain[6], chain[5]));
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
            List<LatLon> points = TugMovePlanner.EdgePointsFrom(Assert.IsType<GroundEdge>(edge.Edge), edge.FromNode);
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
    private static int PutLeadMidTurn(KoakFollowGeometry.LeadRun run, Func<int, bool> alsoWhere)
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
        run.Lead.Position = KoakFollowGeometry.Between(segment.Edge.FromNode.Position, segment.Edge.ToNode.Position, 0.9);
        run.Lead.TrueHeading = KoakFollowGeometry.Facing(segment.Edge.FromNode, segment.Edge.ToNode) + 110.0;
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
