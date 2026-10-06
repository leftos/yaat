using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
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
