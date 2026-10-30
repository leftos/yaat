using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// GitHub #1144: UPS947 (B763), told <c>FOLG SWA6139</c> while the two met head-on on KOAK taxiway B, taxied through the B/T
/// junction, turned about and came back onto T. SWA6139 turns off B onto T through the B→T fillet between them, so the follower
/// carries on to the lead's turn and turns in behind it; it never merges where joining the lead's path turns it about.
/// </summary>
public class Issue1144FollowHeadOnMergeTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue1144-folg-head-on-recording.yaat-bug-report-bundle.zip";

    /// <summary>The second before the recorded <c>FOLLOWG SWA6139</c>, which lands at t=1019.</summary>
    private const int BeforeFollow = 1018;

    /// <summary>A second after SWA6139 has rounded the B→T fillet, with its B and T edges either side of it still in its trail.</summary>
    private const int OnTAfterFillet = 1036;

    /// <summary>The recording's last second.</summary>
    private const int RecordingEnd = 1078;

    /// <summary>
    /// The lowest heading UPS947 may reach turning from B (228°) onto T (130°): below it, it has swung past T's heading, as the
    /// turn about through the junction does (75° at t=1058).
    /// </summary>
    private const double LowestHeadingOntoT = 115.0;

    /// <summary>
    /// Length (ft) of the stub the no-op staging splits off a bar, well under
    /// <see cref="GeometricAdmissibility.NoOpEdgeThresholdNm"/>.
    /// </summary>
    private const double StagedNoOpFt = 0.5;

    /// <summary>
    /// At t=1018 the plan joins SWA6139's path where UPS947 can turn onto it within its category's junction limit: the B→T fillet
    /// start (#724), not the B node past the junction (#725), where the lead's path leaves back the way UPS947 arrives.
    /// </summary>
    [Fact]
    public void UPS947_FollowgHeadOnLead_MergesWhereItCanTurnOntoTheLeadsPath()
    {
        if (ReplayToBeforeFollow() is not { } engine)
        {
            return;
        }

        AircraftState ups = Assert.IsType<AircraftState>(engine.FindAircraft("UPS947"));
        AircraftState swa = Assert.IsType<AircraftState>(engine.FindAircraft("SWA6139"));
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(ups.Ground.Layout);

        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(layout, ups, swa, () => []));
        Assert.NotEmpty(plan.LeadPathFromMerge);
        double arrival = plan.PathToMerge.Segments.Count > 0 ? plan.PathToMerge.Segments[^1].Edge.ArrivalBearing : ups.TrueHeading.Degrees;
        double leave = plan.LeadPathFromMerge[0].DepartureBearing;
        double turn = RouteCostFunction.HeadingDelta(arrival, leave);
        double limit = CategoryLimits.MaxHeadingChangeDeg(AircraftCategorization.Categorize(ups.AircraftType));
        output.WriteLine(
            $"merge #{plan.MergeNode}: route {string.Join("→", plan.PathToMerge.Segments.Select(s => s.FromNodeId))}, arrives {arrival:F1}°, "
                + $"lead leaves {leave:F1}°, turn {turn:F1}°"
        );

        Assert.True(
            turn <= limit,
            $"UPS947 merges at node {plan.MergeNode} arriving {arrival:F1}° where SWA6139's path leaves {leave:F1}°: a {turn:F1}° turn, "
                + $"past the {limit:F0}° junction limit"
        );
    }

    /// <summary>
    /// At t=1036 SWA6139 has turned off B onto T through the B→T fillet, and its trail holds a B edge then a T edge either side
    /// of it. Rounding the fillet, the lead's centre can lie over B's straight edge past the fillet start and sample it into the
    /// trail between them; staged that way, its lead path never turns, between consecutive edges, past SWA6139's own junction
    /// limit: it does not run on along B past the fillet start and back to take the fillet.
    /// </summary>
    [Fact]
    public void SWA6139_LeadPathAfterTurningOntoT_NeverTurnsAbout()
    {
        if (ReplayToBeforeFollow() is not { } engine)
        {
            return;
        }

        for (int t = BeforeFollow + 1; t <= OnTAfterFillet; t++)
        {
            engine.ReplayOneSecond();
        }

        AircraftState ups = Assert.IsType<AircraftState>(engine.FindAircraft("UPS947"));
        AircraftState swa = Assert.IsType<AircraftState>(engine.FindAircraft("SWA6139"));
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(swa.Ground.Layout);
        (_, GroundNode bEnd, _, int newerIndex) = RoundedBToTFillet(layout, swa);
        List<TaxiTrailEdge> trail = [.. swa.Ground.TaxiEdgeTrail.Edges];
        GroundEdge arrival = Assert.IsType<GroundEdge>(trail[newerIndex - 1].Resolve(layout));
        GroundEdge pastStart = Assert.Single(bEnd.Edges.OfType<GroundEdge>(), e => (e.TaxiwayName == "B") && !ReferenceEquals(e, arrival));
        swa.Ground.TaxiEdgeTrail.Clear();
        for (int i = 0; i < trail.Count; i++)
        {
            if (i == newerIndex)
            {
                swa.Ground.TaxiEdgeTrail.Record(pastStart, bEnd);
            }

            swa.Ground.TaxiEdgeTrail.Record(Assert.IsType<GroundEdge>(trail[i].Resolve(layout)), layout.Nodes[trail[i].EntryNodeId]);
        }

        output.WriteLine($"staged trail: {string.Join(" ", swa.Ground.TaxiEdgeTrail.Edges.Select(e => $"{e.NodeA}-{e.NodeB}"))}");
        IReadOnlyList<DirectionalEdge> path = Assert.IsAssignableFrom<IReadOnlyList<DirectionalEdge>>(
            FollowRoutePlanner.LeadPathOf(layout, swa, ups)
        );
        output.WriteLine(string.Join(" > ", path.Select(e => $"{e.FromNodeId}-{e.ToNodeId}({e.DepartureBearing:F0}/{e.ArrivalBearing:F0})")));
        AssertNeverTurnsPastLimit(path, swa);
    }

    /// <summary>
    /// The B→T fillet the lead rounded, read from its own trail: a B edge followed by a T edge, joined by a fillet arc whose
    /// taxiway names hold B and T, from a node of the older edge to a node of the newer. Returns the arc, its B end, its T end
    /// and the newer (T) edge's index in the trail.
    /// </summary>
    private static (GroundArc Arc, GroundNode BEnd, GroundNode TEnd, int NewerIndex) RoundedBToTFillet(AirportGroundLayout layout, AircraftState lead)
    {
        IReadOnlyList<TaxiTrailEdge> trail = lead.Ground.TaxiEdgeTrail.Edges;
        for (int i = 1; i < trail.Count; i++)
        {
            GroundEdge? older = trail[i - 1].Resolve(layout);
            GroundEdge? newer = trail[i].Resolve(layout);
            if ((older?.TaxiwayName != "B") || (newer?.TaxiwayName != "T"))
            {
                continue;
            }

            foreach (GroundNode b in older.Nodes)
            {
                foreach (GroundArc arc in b.Edges.OfType<GroundArc>().Where(a => a.TaxiwayNames.Contains("B") && a.TaxiwayNames.Contains("T")))
                {
                    GroundNode t = arc.OtherNode(b);
                    if (newer.HasNode(t.Id))
                    {
                        return (arc, b, t, i);
                    }
                }
            }
        }

        throw new Xunit.Sdk.XunitException("the lead's trail has no B edge then T edge joined by a B/T fillet arc");
    }

    private static void AssertNeverTurnsPastLimit(IReadOnlyList<DirectionalEdge> path, AircraftState lead)
    {
        double limit = CategoryLimits.MaxHeadingChangeDeg(AircraftCategorization.Categorize(lead.AircraftType));
        for (int i = 1; i < path.Count; i++)
        {
            double turn = RouteCostFunction.HeadingDelta(path[i - 1].ArrivalBearing, path[i].DepartureBearing);
            Assert.True(
                turn <= limit,
                $"{lead.Callsign}'s lead path turns {turn:F1}° at node {path[i].FromNodeId} ({path[i - 1].FromNodeId}-{path[i - 1].ToNodeId} "
                    + $"arriving {path[i - 1].ArrivalBearing:F1}°, then {path[i].FromNodeId}-{path[i].ToNodeId} leaving "
                    + $"{path[i].DepartureBearing:F1}°), past its {limit:F0}° junction limit"
            );
        }
    }

    /// <summary>
    /// Replayed from t=1018 with the recorded <c>FOLLOWG SWA6139</c> through the recording's end, UPS947 turns from B onto T
    /// behind SWA6139 and, at the end, heads along the edge of its follow route it is driving rather than swinging past it into
    /// a turn about.
    /// </summary>
    [Fact]
    public void UPS947_FollowgHeadOnLead_TurnsOntoTWithoutTurningAbout()
    {
        if (ReplayToBeforeFollow() is not { } engine)
        {
            return;
        }

        AircraftState ups = Assert.IsType<AircraftState>(engine.FindAircraft("UPS947"));
        double lowest = ups.TrueHeading.Degrees;
        int lowestAt = BeforeFollow;
        bool followed = false;
        for (int t = BeforeFollow + 1; t <= RecordingEnd; t++)
        {
            engine.ReplayOneSecond();
            followed |= ups.Phases?.CurrentPhase is FollowingPhase;
            double heading = ups.TrueHeading.Degrees;
            output.WriteLine($"t={t} hdg={heading:F1} gs={ups.GroundSpeed:F1} phase={ups.Phases?.CurrentPhase?.Name}");
            if (heading < lowest)
            {
                lowest = heading;
                lowestAt = t;
            }
        }

        Assert.True(followed, "the recorded FOLLOWG SWA6139 never put UPS947 in its follow");
        Assert.True(
            lowest >= LowestHeadingOntoT,
            $"UPS947's heading fell to {lowest:F1}° at t={lowestAt}, past T's heading: it turned about instead of turning onto T"
        );

        FollowingPhase follow = Assert.IsType<FollowingPhase>(ups.Phases?.CurrentPhase);
        Assert.Equal("SWA6139", follow.TargetCallsign);
        Assert.Equal("T", ups.Ground.CurrentTaxiway);
        Assert.True(ups.GroundSpeed >= MovingKts, $"UPS947 is not moving at t={RecordingEnd}: {ups.GroundSpeed:F1} kt");
        // The recording ends on a stretch the lead itself drove at about 142°, so the pin is the bearing of the follow route's
        // current edge, not T's nominal heading: the follower still tracks its route. The edge's own bearing, not the bearing
        // to its end node, which swings round once the 1 Hz sample lands a few feet past that node before the segment advances.
        TaxiRoute route = Assert.IsType<TaxiRoute>(follow.FollowRoute);
        TaxiRouteSegment current = route.Segments[route.CurrentSegmentIndex];
        double edgeDeg = current.Edge.DepartureBearing;
        double offEdge = Math.Abs(GeoMath.SignedBearingDifference(ups.TrueHeading.Degrees, edgeDeg));
        Assert.True(
            offEdge <= 10.0,
            $"UPS947 heads {ups.TrueHeading.Degrees:F1}° at t={RecordingEnd}, {offEdge:F1}° off its follow route's edge "
                + $"#{current.FromNodeId}-#{current.ToNodeId} at {edgeDeg:F1}°"
        );
        AircraftState swa = Assert.IsType<AircraftState>(engine.FindAircraft("SWA6139"));
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(ups.Ground.Layout);
        List<DirectionalEdge> ahead = [.. route.Segments.Skip(route.CurrentSegmentIndex).Select(s => s.Edge)];
        Assert.True(
            FollowRoutePlanner.LocateOnPath(layout, ahead, swa) is not null,
            $"SWA6139 is not ahead of UPS947 along its follow route {route.ToSummary()}"
        );
    }

    /// <summary>The ground speed (kt) at or above which UPS947 counts as moving.</summary>
    private const double MovingKts = 1.0;

    /// <summary>
    /// A true same-taxiway head-on stays a refusal: a B763 on the KOAK lead's route ahead of it, facing back along it, where the
    /// lead's path never turns off between them, plans as <see cref="FollowRoutePlan.FollowerAhead"/>, never
    /// <see cref="FollowRoutePlan.NoPath"/>.
    /// </summary>
    [Fact]
    public void FollowgSameTaxiwayHeadOn_StaysFollowerAhead_NotNoPath()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        AircraftState follower = FollowCornerGeometry.SpawnOnRouteAhead(run.Lead, "UPS947", "B763");
        follower.Ground.Layout = run.Layout;
        follower.Phases = new PhaseList();

        Assert.IsType<FollowRoutePlan.FollowerAhead>(FollowRoutePlanner.Plan(run.Layout, follower, run.Lead, () => []));
    }

    /// <summary>
    /// The route to the merge never runs head-on down the lead's path: for a B738 on every straight KOAK taxiway edge within
    /// 1,000 ft of the route of a lead taxiing <c>B W 30</c> (off the lead's path, facing either way), a joinable plan's route to
    /// the merge drives no lead path edge against the lead's direction and no edge of the lead's route ahead of it. A follower
    /// whose only admissible merge lies past such a stretch is ahead of the lead, never joined head-on.
    /// </summary>
    [Fact]
    public void FollowgRouteToTheMerge_NeverRunsHeadOnDownTheLeadsPath()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        AircraftState lead = run.Lead;
        TaxiRoute leadRoute = Assert.IsType<TaxiRoute>(lead.Ground.AssignedTaxiRoute);
        HashSet<(int, int)> aheadKeys = [.. leadRoute.Segments.Skip(leadRoute.CurrentSegmentIndex).Select(s => EdgeKey(s.FromNodeId, s.ToNodeId))];
        List<GroundNode> routeNodes = [.. leadRoute.Segments.Select(s => s.Edge.FromNode)];
        List<string> headOn = [];
        int joinable = 0;
        foreach (GroundEdge edge in NearbyStraightEdges(run.Layout, routeNodes, 1000.0))
        {
            foreach ((GroundNode from, GroundNode to) in new[] { (edge.Nodes[0], edge.Nodes[1]), (edge.Nodes[1], edge.Nodes[0]) })
            {
                AircraftState follower = FollowCornerGeometry.Spawn(
                    FollowCornerGeometry.AirportId,
                    "UPS947",
                    "B738",
                    FollowCornerGeometry.Between(from.Position, to.Position, 0.5),
                    FollowCornerGeometry.Facing(from, to)
                );
                follower.Ground.Layout = run.Layout;
                if (FollowRoutePlanner.LeadPathOf(run.Layout, lead, follower) is not { } leadPath)
                {
                    continue;
                }

                HashSet<(int, int)> leadKeys = [.. leadPath.Select(e => EdgeKey(e.FromNodeId, e.ToNodeId))];
                if (leadKeys.Contains(EdgeKey(from.Id, to.Id)) || aheadKeys.Contains(EdgeKey(from.Id, to.Id)))
                {
                    continue;
                }

                if (FollowRoutePlanner.Plan(run.Layout, follower, lead, () => []) is not FollowRoutePlan.Joinable plan)
                {
                    continue;
                }

                joinable++;
                HashSet<(int, int)> againstLead = [.. leadPath.Select(e => (e.ToNodeId, e.FromNodeId))];
                if (
                    plan.PathToMerge.Segments.FirstOrDefault(s =>
                        againstLead.Contains((s.FromNodeId, s.ToNodeId)) || aheadKeys.Contains(EdgeKey(s.FromNodeId, s.ToNodeId))
                    ) is
                    { } bad
                )
                {
                    string route = string.Join("→", plan.PathToMerge.Segments.Select(s => s.FromNodeId));
                    headOn.Add($"from {from.Id}→{to.Id}: merge #{plan.MergeNode} via {route} drives {bad.FromNodeId}→{bad.ToNodeId}");
                }
            }
        }

        output.WriteLine($"{joinable} joinable plans; {headOn.Count} run head-on down the lead's path:");
        headOn.ForEach(output.WriteLine);
        Assert.True(joinable >= 20, $"only {joinable} joinable plans staged: the sweep proves nothing");
        Assert.Empty(headOn);
    }

    /// <summary>
    /// A follower led into its route's start judges the turn onto the lead's path there from the bearing its lead-in arrives on,
    /// not its heading: a B738 three-quarters along a straight KOAK edge into a node of the path of a lead taxiing <c>B W 30</c>,
    /// where that path leaves 90–130° off the edge, yawed 60° away from the turn (as half-way round a corner), merges at that
    /// node, a turn it makes from the edge's bearing, rather than driving through it.
    /// </summary>
    [Fact]
    public void FollowgLedIntoAMergeNode_JudgesTheTurnFromTheLeadInBearing()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        AirportGroundLayout layout = run.Layout;
        AircraftState probe = FollowCornerGeometry.Spawn(FollowCornerGeometry.AirportId, "UPS947", "B738", run.Lead.Position, run.Lead.TrueHeading);
        List<DirectionalEdge> path =
        [
            .. Assert.IsAssignableFrom<IReadOnlyList<DirectionalEdge>>(FollowRoutePlanner.LeadPathOf(layout, run.Lead, probe)),
        ];
        HashSet<(int, int)> pathKeys = [.. path.Select(e => EdgeKey(e.FromNodeId, e.ToNodeId))];
        double limit = CategoryLimits.MaxHeadingChangeDeg(AircraftCategory.Jet);
        int staged = 0;
        List<string> missed = [];
        foreach (DirectionalEdge onward in path.Where((edge, i) => path.FindLastIndex(e => e.FromNodeId == edge.FromNodeId) == i))
        {
            GroundNode node = onward.FromNode;
            foreach (GroundEdge edge in node.Edges.OfType<GroundEdge>().Where(e => !e.IsRunwayCenterline && !e.IsRamp))
            {
                GroundNode far = edge.OtherNode(node);
                double arrival = GeoMath.BearingTo(far.Position, node.Position);
                double turn = GeoMath.SignedBearingDifference(arrival, onward.DepartureBearing);
                if (
                    pathKeys.Contains(EdgeKey(far.Id, node.Id))
                    || (edge.DistanceNm * GeoMath.FeetPerNm < 100.0)
                    || (Math.Abs(turn) <= 90.0)
                    || (Math.Abs(turn) > limit - 5.0)
                )
                {
                    continue;
                }

                var yawed = new TrueHeading(arrival - (Math.Sign(turn) * 60.0));
                AircraftState follower = FollowCornerGeometry.Spawn(
                    FollowCornerGeometry.AirportId,
                    "UPS947",
                    "B738",
                    FollowCornerGeometry.Between(far.Position, node.Position, 0.75),
                    yawed
                );
                follower.Ground.Layout = layout;
                if (
                    FollowRoutePlanner.StartOf(layout, follower, requireAhead: true, () => []) is not { LeadIn: not null } start
                    || (start.Node.Id != node.Id)
                )
                {
                    continue;
                }

                staged++;
                FollowRoutePlan plan = FollowRoutePlanner.Plan(layout, follower, run.Lead, () => []);
                output.WriteLine(
                    $"led in {far.Id}→{node.Id} ({arrival:F0}°, yawed {yawed.Degrees:F0}°), lead leaves {onward.DepartureBearing:F0}°: {plan}"
                );
                if (plan is not FollowRoutePlan.Joinable { MergeNode: var merge } || (merge != node.Id))
                {
                    missed.Add($"led in {far.Id}→{node.Id}: {plan}");
                }
            }
        }

        Assert.True(staged >= 1, "no follower staged led into a node of the lead's path with a 90-130° turn onto it");
        Assert.Empty(missed);
    }

    /// <summary>
    /// A zero-length no-op edge out of a merge candidate turns nothing: on a lead path through a no-op edge, the turn a
    /// follower must make at the node before it is judged onto the first edge past it that moves, and a path that ends in the
    /// no-op edge keeps it. The bar mint keeps bars clear of edge ends, so KHKY has no zero-length edge of its own; the test
    /// stages one.
    /// </summary>
    [Fact]
    public void OnwardEdges_SkipAZeroLengthEdgeToTheNextEdgeThatMoves()
    {
        TestVnasData.EnsureInitialized();
        // A fresh parse: the staging splits an edge, and the shared HKY layout must stay as parsed for every other test.
        if (TestAirportGroundData.LoadUncached("HKY") is not { } hky)
        {
            return;
        }

        StageNoOpEdgeBesideABar(hky);

        (GroundNode Before, GroundNode Goal, IGroundEdge NoOp, GroundNode Past, GroundNode Beyond, IGroundEdge In, IGroundEdge Out)? found = null;
        IEnumerable<IGroundEdge> noOps = hky
            .Nodes.Values.OrderBy(n => n.Id)
            .SelectMany(n => n.Edges)
            .Where(e => GeometricAdmissibility.IsNoOpEdge(e));
        foreach (IGroundEdge noOp in noOps)
        {
            GroundNode goal = noOp.Nodes[0];
            GroundNode past = noOp.Nodes[1];
            IGroundEdge? into = goal.Edges.FirstOrDefault(e => !ReferenceEquals(e, noOp) && !GeometricAdmissibility.IsNoOpEdge(e));
            IGroundEdge? onward = past.Edges.FirstOrDefault(e =>
                !ReferenceEquals(e, noOp) && !GeometricAdmissibility.IsNoOpEdge(e) && (e.OtherNode(past).Id != goal.Id)
            );
            if ((into is not null) && (onward is not null) && (into.OtherNode(goal).Id != onward.OtherNode(past).Id))
            {
                found = (into.OtherNode(goal), goal, noOp, past, onward.OtherNode(past), into, onward);
                break;
            }
        }

        Assert.True(found is not null, "the staged no-op edge is not between two edges that move");
        (GroundNode before, GroundNode at, IGroundEdge zero, GroundNode pastNode, GroundNode beyond, IGroundEdge inEdge, IGroundEdge outEdge) =
            found.Value;
        var first = new DirectionalEdge
        {
            Edge = inEdge,
            FromNode = before,
            ToNode = at,
        };
        var noOpEdge = new DirectionalEdge
        {
            Edge = zero,
            FromNode = at,
            ToNode = pastNode,
        };
        var moving = new DirectionalEdge
        {
            Edge = outEdge,
            FromNode = pastNode,
            ToNode = beyond,
        };

        Dictionary<int, DirectionalEdge> onwardEdges = FollowRoutePlanner.OnwardEdges(
            [first, noOpEdge, moving],
            [before.Id, at.Id, pastNode.Id, beyond.Id]
        );
        Assert.Same(first, onwardEdges[before.Id]);
        Assert.Same(moving, onwardEdges[at.Id]);
        Assert.Same(moving, onwardEdges[pastNode.Id]);
        Assert.False(onwardEdges.ContainsKey(beyond.Id));
        Assert.Same(noOpEdge, FollowRoutePlanner.OnwardEdges([first, noOpEdge], [at.Id])[at.Id]);
    }

    /// <summary>
    /// Stages a zero-length no-op edge between two edges that move: splits the longest edge off a hold-short bar whose other
    /// edges all move, <see cref="StagedNoOpFt"/> from the bar, with the build's own
    /// <see cref="RunwayCrossingDetector.SplitEdgeAtOneNode"/>. Asserts the stub is under the threshold and both edges around
    /// it are over it.
    /// </summary>
    private static void StageNoOpEdgeBesideABar(AirportGroundLayout layout)
    {
        double thresholdFt = GeometricAdmissibility.NoOpEdgeThresholdNm * GeoMath.FeetPerNm;
        List<string> bars = [];
        foreach (GroundNode bar in layout.Nodes.Values.OrderBy(node => node.Id))
        {
            if (bar.Origin?.StartsWith("RunwayCrossing:hold-short@", StringComparison.Ordinal) != true)
            {
                continue;
            }

            List<GroundEdge> edges = [.. bar.Edges.OfType<GroundEdge>()];
            bars.Add($"#{bar.Id}: {string.Join(", ", edges.Select(edge => $"{edge.DistanceNm * GeoMath.FeetPerNm:F3} ft"))}");
            if ((edges.Count < 2) || edges.Any(GeometricAdmissibility.IsNoOpEdge))
            {
                continue;
            }

            GroundEdge longest = edges.MaxBy(edge => edge.DistanceNm)!;
            if ((longest.DistanceNm * GeoMath.FeetPerNm) < (StagedNoOpFt + thresholdFt))
            {
                continue;
            }

            GroundNode far = longest.OtherNode(bar);
            double fraction = StagedNoOpFt / longest.DistanceNm / GeoMath.FeetPerNm;
            var stubNode = new GroundNode
            {
                Id = layout.Nodes.Keys.Max() + 1,
                Position = new LatLon(
                    bar.Position.Lat + (fraction * (far.Position.Lat - bar.Position.Lat)),
                    bar.Position.Lon + (fraction * (far.Position.Lon - bar.Position.Lon))
                ),
                Type = GroundNodeType.TaxiwayIntersection,
                Origin = "test:staged no-op node (YAAT-1381)",
            };
            layout.Nodes[stubNode.Id] = stubNode;
            RunwayCrossingDetector.SplitEdgeAtOneNode(layout, longest, stubNode);
            layout.RebuildAdjacencyLists();

            List<GroundEdge> pieces = [.. layout.Edges.Where(edge => edge.HasNode(stubNode.Id))];
            string pieceFt = string.Join(", ", pieces.Select(edge => $"{edge.DistanceNm * GeoMath.FeetPerNm:F3} ft"));
            List<GroundEdge> staged = [.. pieces.Where(GeometricAdmissibility.IsNoOpEdge)];
            Assert.True(staged.Count == 1, $"the staged split made {staged.Count} no-op edges, not 1: {pieceFt}");

            List<IGroundEdge> around =
            [
                .. bar.Edges.Where(edge => !ReferenceEquals(edge, staged[0])),
                .. stubNode.Edges.Where(edge => !ReferenceEquals(edge, staged[0])),
            ];
            string aroundFt = string.Join(", ", around.Select(edge => $"{edge.DistanceNm * GeoMath.FeetPerNm:F3} ft"));
            Assert.True(
                around.All(edge => !GeometricAdmissibility.IsNoOpEdge(edge)),
                $"the staged no-op edge is not between two edges that move: {aroundFt}"
            );
            return;
        }

        throw new Xunit.Sdk.XunitException(
            $"KHKY has no hold-short bar with two edges that move to stage a no-op edge beside: {string.Join("; ", bars)}"
        );
    }

    /// <summary>
    /// A follower standing at a node of a taxiing lead's route ahead of the lead (on the taxiway it came in by, within the node's
    /// tolerance), facing in along another taxiway it could turn from onto the lead's path, is ahead of the lead: away from a
    /// runway bar it plans as <see cref="FollowRoutePlan.FollowerAhead"/> and never drives off ahead of the lead; held at a bar it
    /// merges where it stands, the merge ahead of the lead, and waits there for the lead to pass
    /// (<see cref="FollowingPhase.WaitsAtBarForLead"/>'s empty-path rule): for a B738 at each such node of the route of a KOAK
    /// lead taxiing <c>B W 30</c>.
    /// </summary>
    [Fact]
    public void FollowgStandingOnTheLeadsRouteAhead_AwayFromABarIsAheadOfTheLead_AtABarWaitsForIt()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        AirportGroundLayout layout = run.Layout;
        List<(GroundNode Node, GroundNode Far, AircraftState Follower)> staged = StandingOnTheLeadsRouteAhead(run);
        List<string> missed = [];
        foreach ((GroundNode node, GroundNode far, AircraftState follower) in staged)
        {
            FollowRoutePlan plan = FollowRoutePlanner.PlanForCommand(layout, follower, run.Lead, () => []);
            bool atBar = node.Type == GroundNodeType.RunwayHoldShort;
            bool waits =
                atBar && FollowingPhase.WaitsAtBarForLead(ContextOf(follower, layout), layout, run.Lead, new FollowingPhase.BarCrossing(node, []));
            output.WriteLine($"at #{node.Id} ({node.Type}) facing in from #{far.Id}: {plan}; waits {waits}");
            bool asRuled = atBar
                ? (plan is FollowRoutePlan.Joinable { PathToMerge.Segments.Count: 0, MergeAheadOfLead: true } joinable)
                    && (joinable.MergeNode == node.Id)
                    && waits
                : plan is FollowRoutePlan.FollowerAhead;
            if (!asRuled)
            {
                missed.Add($"at #{node.Id} ({node.Type}) from #{far.Id}: {plan}, waits {waits}");
            }
        }

        Assert.True(staged.Count >= 1, "no follower staged at a node of the lead's route ahead of it, facing in along another taxiway");
        Assert.Empty(missed);
    }

    /// <summary>
    /// <c>FOLLOWG</c> to a B738 standing at a node of a taxiing KOAK lead's route ahead of the lead, away from any runway bar, is
    /// refused with the readback that it is ahead of the lead on its route, and the follower stays where it stands.
    /// </summary>
    [Fact]
    public void FollowgStandingOnTheLeadsRouteAheadAwayFromABar_IsRefusedAndStaysPut()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        (GroundNode node, _, AircraftState follower) = StandingOnTheLeadsRouteAhead(run).First(s => s.Node.Type != GroundNodeType.RunwayHoldShort);
        run.Engine.World.AddAircraft(follower);
        LatLon before = follower.Position;
        CommandResult result = run.Engine.SendCommand(follower.Callsign, $"FOLLOWG {run.Lead.Callsign}");
        output.WriteLine($"FOLLOWG at #{node.Id} -> success={result.Success} msg={result.Message}");
        Assert.False(result.Success, $"FOLLOWG ahead of {run.Lead.Callsign} on its route was accepted: {result.Message}");
        Assert.StartsWith($"unable, ahead of {run.Lead.Callsign} on its route", result.Message, StringComparison.Ordinal);

        for (int second = 1; second <= StandStillSeconds; second++)
        {
            run.Engine.TickOneSecond();
        }

        double movedFt = GeoMath.DistanceNm(before, follower.Position) * GeoMath.FeetPerNm;
        Assert.True(movedFt < 1.0, $"the refused follower moved {movedFt:F1} ft from where it stood");
    }

    /// <summary>
    /// A follow running from a node of a taxiing KOAK lead's route ahead of the lead, away from any runway bar — put straight into
    /// the follow, as <c>FOLLOWG</c> refuses it there — holds where it stands and plans again each tick, and the pilot says once,
    /// the first time it holds so, that it cannot follow and wants taxi instructions: never again on the later re-plans.
    /// </summary>
    [Fact]
    public void FollowingFromTheLeadsRouteAhead_HoldsAndSaysUnableOnce()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        (GroundNode node, _, AircraftState follower) = StandingOnTheLeadsRouteAhead(run).First(s => s.Node.Type != GroundNodeType.RunwayHoldShort);
        FollowRoutePlan plan = FollowRoutePlanner.Plan(run.Layout, follower, run.Lead, () => []);
        output.WriteLine($"at #{node.Id}: {plan}");
        Assert.True(
            plan is FollowRoutePlan.Joinable { PathToMerge.Segments.Count: 0, MergeAheadOfLead: true },
            $"the follow at #{node.Id} does not plan to hold where it stands ahead of {run.Lead.Callsign}: {plan}"
        );
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase(run.Lead.Callsign));
        follower.Phases.Start(CommandDispatcher.BuildMinimalContext(follower, ActiveRunways.Empty, run.Layout));
        run.Engine.World.AddAircraft(follower);
        List<string> unable = [];
        void Note(string from, string text)
        {
            if ((from == follower.Callsign) && text.Contains("no taxi route", StringComparison.OrdinalIgnoreCase))
            {
                unable.Add(text);
            }
        }

        run.Engine.WarningEmitted += Note;
        run.Engine.TerminalEntryEmitted += entry => Note(entry.Callsign, entry.Message);
        LatLon before = follower.Position;
        for (int second = 1; second <= StandStillSeconds; second++)
        {
            run.Engine.TickOneSecond();
            output.WriteLine($"t={second} at #{node.Id}: {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} calls={unable.Count}");
        }

        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.False(follow.IsUnjoinable, "the follow standing on the lead's route ahead stopped planning");
        Assert.True(GeoMath.DistanceNm(before, follower.Position) * GeoMath.FeetPerNm < 1.0, "the follower drove off ahead of its lead");
        Assert.Equal([$"Unable to follow {run.Lead.Callsign}, no taxi route to its path, request taxi instructions"], unable);
    }

    /// <summary>
    /// The route a follower could take only with no move forbidden marks it ahead of the lead
    /// (<see cref="FollowRoutePlanner.RunsOnTheLeadsEdgeOrRouteAhead"/>) only when it drives the edge the lead is on or the lead's
    /// route ahead, either way: for a KOAK lead taxiing <c>B W 30</c>, a route along its trail behind it, either way, does not
    /// (that follower has no path, <see cref="FollowRoutePlan.NoPath"/>); one along the lead's own edge or back down its route
    /// ahead does (<see cref="FollowRoutePlan.FollowerAhead"/>).
    /// </summary>
    [Fact]
    public void FollowerAheadLabel_OnlyForARouteOnTheLeadsEdgeOrRouteAhead()
    {
        if (FollowCornerGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        AircraftState probe = FollowCornerGeometry.Spawn(FollowCornerGeometry.AirportId, "UPS947", "B738", run.Lead.Position, run.Lead.TrueHeading);
        List<DirectionalEdge> path =
        [
            .. Assert.IsAssignableFrom<IReadOnlyList<DirectionalEdge>>(FollowRoutePlanner.LeadPathOf(run.Layout, run.Lead, probe)),
        ];
        PathPosition? located = FollowRoutePlanner.LocateOnPath(run.Layout, path, run.Lead);
        Assert.NotNull(located);
        int at = located.Value.EdgeIndex;
        output.WriteLine($"lead on edge {at} of {string.Join(" > ", path.Select(e => $"{e.FromNodeId}-{e.ToNodeId}"))}");
        Assert.True((at >= 1) && (at < path.Count - 1), $"the lead on edge {at} of {path.Count} has no trail behind it or no route ahead");

        Assert.False(FollowRoutePlanner.RunsOnTheLeadsEdgeOrRouteAhead(RouteOn(Reversed(path[at - 1])), path, at));
        Assert.False(FollowRoutePlanner.RunsOnTheLeadsEdgeOrRouteAhead(RouteOn(path[at - 1]), path, at));
        Assert.True(FollowRoutePlanner.RunsOnTheLeadsEdgeOrRouteAhead(RouteOn(path[at]), path, at));
        Assert.True(FollowRoutePlanner.RunsOnTheLeadsEdgeOrRouteAhead(RouteOn(Reversed(path[^1])), path, at));
    }

    /// <summary>A one-segment route along <paramref name="edge"/>.</summary>
    private static TaxiRoute RouteOn(DirectionalEdge edge) =>
        new() { Segments = [new TaxiRouteSegment { Edge = edge, TaxiwayName = edge.Edge.TaxiwayName ?? "" }], HoldShortPoints = [] };

    /// <summary><paramref name="edge"/> driven the other way.</summary>
    private static DirectionalEdge Reversed(DirectionalEdge edge) =>
        new()
        {
            Edge = edge.Edge,
            FromNode = edge.ToNode,
            ToNode = edge.FromNode,
        };

    /// <summary>How long (s) a refused follower is ticked to show it stays where it stands.</summary>
    private const int StandStillSeconds = 20;

    /// <summary>A jet's phase context for <paramref name="follower"/> on <paramref name="layout"/>.</summary>
    private static PhaseContext ContextOf(AircraftState follower, AirportGroundLayout layout) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = follower,
            Targets = follower.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.25,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            GroundLayout = layout,
        };

    /// <summary>
    /// B738s, one per node of <paramref name="run"/>'s lead route ahead of the lead and per taxiway into it it could turn from onto
    /// that route: each at the node, within its tolerance on that taxiway, facing in, with the node and that taxiway's far node.
    /// </summary>
    private static List<(GroundNode Node, GroundNode Far, AircraftState Follower)> StandingOnTheLeadsRouteAhead(FollowCornerGeometry.LeadRun run)
    {
        AirportGroundLayout layout = run.Layout;
        TaxiRoute leadRoute = Assert.IsType<TaxiRoute>(run.Lead.Ground.AssignedTaxiRoute);
        HashSet<(int, int)> routeKeys = [.. leadRoute.Segments.Select(s => EdgeKey(s.FromNodeId, s.ToNodeId))];
        double limit = CategoryLimits.MaxHeadingChangeDeg(AircraftCategory.Jet);
        List<(GroundNode Node, GroundNode Far, AircraftState Follower)> staged = [];
        foreach (TaxiRouteSegment onward in leadRoute.Segments.Skip(leadRoute.CurrentSegmentIndex + 1))
        {
            GroundNode node = onward.Edge.FromNode;
            foreach (GroundEdge edge in node.Edges.OfType<GroundEdge>().Where(e => !e.IsRunwayCenterline && !e.IsRamp))
            {
                GroundNode far = edge.OtherNode(node);
                double arrival = GeoMath.BearingTo(far.Position, node.Position);
                if (
                    routeKeys.Contains(EdgeKey(far.Id, node.Id))
                    || (edge.DistanceNm * GeoMath.FeetPerNm < 100.0)
                    || (Math.Abs(GeoMath.SignedBearingDifference(arrival, onward.Edge.DepartureBearing)) > limit - 5.0)
                )
                {
                    continue;
                }

                LatLon atNode = GeoMath.ProjectPoint(
                    node.Position,
                    new TrueHeading(arrival + 180.0),
                    AirportGroundLayout.AtNodeToleranceFt / 2.0 / GeoMath.FeetPerNm
                );
                AircraftState follower = FollowCornerGeometry.Spawn(
                    FollowCornerGeometry.AirportId,
                    "UPS947",
                    "B738",
                    atNode,
                    new TrueHeading(arrival)
                );
                follower.Ground.Layout = layout;
                staged.Add((node, far, follower));
            }
        }

        return staged;
    }

    private static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);

    /// <summary>
    /// The straight, non-runway, non-ramp edges of <paramref name="layout"/> with a node within <paramref name="radiusFt"/> of one
    /// of <paramref name="near"/>.
    /// </summary>
    private static List<GroundEdge> NearbyStraightEdges(AirportGroundLayout layout, List<GroundNode> near, double radiusFt) =>
        [
            .. layout
                .Nodes.Values.SelectMany(node => node.Edges.OfType<GroundEdge>())
                .Where(edge => !edge.IsRunwayCenterline && !edge.IsRamp && (edge.DistanceNm * GeoMath.FeetPerNm >= 40.0))
                .Where(edge => edge.Nodes.Any(n => near.Any(r => GeoMath.DistanceNm(n.Position, r.Position) * GeoMath.FeetPerNm <= radiusFt)))
                .DistinctBy(edge => EdgeKey(edge.Nodes[0].Id, edge.Nodes[1].Id))
                .OrderBy(edge => EdgeKey(edge.Nodes[0].Id, edge.Nodes[1].Id)),
        ];

    private SimulationEngine? ReplayToBeforeFollow()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = RecordingGroundData.Evergreen("a FOLLOWG head-on merge turning onto the lead's path on today's KOAK B/T junction");
        if ((groundData.GetLayout("OAK") is null) || (RecordingLoader.Load(RecordingPath) is not { } recording))
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.Replay(recording, BeforeFollow);
        return engine;
    }
}
