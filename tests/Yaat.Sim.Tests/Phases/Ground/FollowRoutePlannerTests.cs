using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
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
        lead.Ground.TaxiEdgeTrail.Record(push.Side);
        lead.Ground.TaxiEdgeTrail.Record(KoakFollowGeometry.EdgeBetween(push.J, push.W1));
        lead.Ground.TaxiEdgeTrail.Record(KoakFollowGeometry.EdgeBetween(push.J, push.E1));
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
        lead.Ground.TaxiEdgeTrail.Record((GroundEdge)first.Edge);
        lead.Ground.TaxiEdgeTrail.Record((GroundEdge)last.Edge);
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
        lead.Ground.TaxiEdgeTrail.Record(KoakFollowGeometry.EdgeBetween(chain[5], chain[4]));
        lead.Ground.TaxiEdgeTrail.Record(KoakFollowGeometry.EdgeBetween(chain[2], chain[1]));
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
        lead.Ground.TaxiEdgeTrail.Record(before);
        lead.Ground.TaxiEdgeTrail.Record(shared);
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
            lead.Ground.TaxiEdgeTrail.Record(KoakFollowGeometry.EdgeBetween(chain[i], chain[i - 1]));
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
