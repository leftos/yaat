using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// <see cref="TaxiPathfinder.FindRouteToNearestGoal"/>: the goal-set A* that finds the cheapest of several goal nodes in one pass,
/// on the real KOAK layout. The start is the near 28R holding position on taxiway B.
/// </summary>
public class AutoRouterGoalSetTests(ITestOutputHelper output)
{
    private const AircraftCategory Category = AircraftCategory.Jet;
    private const WakeTurbulenceData.WakeClass Wake = WakeTurbulenceData.WakeClass.Large;

    /// <summary>
    /// Of a goal node nearer in a straight line and one cheaper to taxi to, the search reaches the cheaper one: goals are ranked by
    /// path cost, not by distance.
    /// </summary>
    [Fact]
    public void ReachesTheGoalCheapestByPathCost_NotTheStraightLineNearest()
    {
        if (LoadStart() is not { } run)
        {
            return;
        }

        List<(GroundNode Node, double DistNm, double Cost)> candidates = [];
        foreach (GroundNode node in run.Layout.Nodes.Values.Where(n => n.Id != run.Start.Id).OrderBy(n => Dist(run.Start, n)).Take(60))
        {
            if (SingleGoalCost(run.Layout, run.Start.Id, node.Id) is { } cost)
            {
                candidates.Add((node, Dist(run.Start, node), cost));
            }
        }

        (int Nearer, int Cheaper)? pair = null;
        for (int i = 0; (i < candidates.Count) && (pair is null); i++)
        {
            for (int j = i + 1; (j < candidates.Count) && (pair is null); j++)
            {
                if ((candidates[i].DistNm < candidates[j].DistNm) && (candidates[j].Cost < candidates[i].Cost - 1e-6))
                {
                    pair = (i, j);
                }
            }
        }

        Assert.True(pair is not null, "no KOAK node pair near the start where the straight-line nearer node costs more to reach");
        (GroundNode Node, double DistNm, double Cost) nearer = candidates[pair.Value.Nearer];
        (GroundNode Node, double DistNm, double Cost) cheaper = candidates[pair.Value.Cheaper];
        output.WriteLine(
            $"nearer #{nearer.Node.Id} {nearer.DistNm * GeoMath.FeetPerNm:F0} ft cost {nearer.Cost:F4}; "
                + $"cheaper #{cheaper.Node.Id} {cheaper.DistNm * GeoMath.FeetPerNm:F0} ft cost {cheaper.Cost:F4}"
        );

        HashSet<int> goals = [nearer.Node.Id, cheaper.Node.Id];
        GoalRoute result = Assert.IsType<GoalRoute>(TaxiPathfinder.FindRouteToNearestGoal(run.Layout, run.Start.Id, goals, Category, Wake));

        Assert.Equal(cheaper.Node.Id, result.GoalNodeId);
        Assert.Equal(cheaper.Node.Id, result.Route.Segments[^1].ToNodeId);
    }

    /// <summary>A start that is itself one of the goals is reached at once: that goal, with no segments.</summary>
    [Fact]
    public void StartIsAGoal_ReturnsZeroLengthRoute()
    {
        if (LoadStart() is not { } run)
        {
            return;
        }

        GroundNode other = run.Layout.Nodes.Values.Where(n => n.Id != run.Start.Id).OrderBy(n => Dist(run.Start, n)).First();
        HashSet<int> goals = [other.Id, run.Start.Id];

        GoalRoute result = Assert.IsType<GoalRoute>(TaxiPathfinder.FindRouteToNearestGoal(run.Layout, run.Start.Id, goals, Category, Wake));

        Assert.Equal(run.Start.Id, result.GoalNodeId);
        Assert.Empty(result.Route.Segments);
    }

    /// <summary>An empty goal set has nothing to reach: null.</summary>
    [Fact]
    public void EmptyGoalSet_ReturnsNull()
    {
        if (LoadStart() is not { } run)
        {
            return;
        }

        Assert.Null(TaxiPathfinder.FindRouteToNearestGoal(run.Layout, run.Start.Id, new HashSet<int>(), Category, Wake));
    }

    /// <summary>
    /// Goals that no path reaches — the nodes of an island no edge joins to the start's part of the graph, on the first of the
    /// 17 committed test layouts that has one — return null.
    /// </summary>
    [Fact]
    public void UnreachableGoalSet_ReturnsNull()
    {
        TestVnasData.EnsureInitialized();
        GraphComponents.DisconnectedPair split = Assert.IsType<GraphComponents.DisconnectedPair>(GraphComponents.FindDisconnectedPair());
        output.WriteLine(
            $"{split.Layout.AirportId}: start #{split.MainNode.Id}, {split.Island.Count} unreachable goals, first #{split.Island[0].Id}"
        );

        HashSet<int> goals = [.. split.Island.Select(n => n.Id)];
        Assert.Null(TaxiPathfinder.FindRouteToNearestGoal(split.Layout, split.MainNode.Id, goals, Category, Wake));
    }

    /// <summary>
    /// The route the goal-set search returns to a goal is the one the single-goal entry point
    /// (<see cref="TaxiPathfinder.FindRoute"/>) takes to that same goal: same edges, same direction, same order.
    /// </summary>
    [Fact]
    public void RouteMatchesTheSingleGoalRoute()
    {
        if (LoadStart() is not { } run)
        {
            return;
        }

        GroundNode goal = TestLayoutNodes.RunwayHoldShortsOnTaxiway(run.Layout, "28L", "B")[0];
        TaxiRoute single = Assert.IsType<TaxiRoute>(TaxiPathfinder.FindRoute(run.Layout, run.Start.Id, goal.Id, Category, Wake));
        double goalCost = Assert.IsType<double>(SingleGoalCost(run.Layout, run.Start.Id, goal.Id));

        HashSet<int> goals = [goal.Id];
        foreach (GroundNode node in run.Layout.Nodes.Values.OrderByDescending(n => Dist(run.Start, n)).Take(40))
        {
            if (SingleGoalCost(run.Layout, run.Start.Id, node.Id) is { } cost && (cost > goalCost + 1e-6))
            {
                goals.Add(node.Id);
            }
        }

        GoalRoute result = Assert.IsType<GoalRoute>(TaxiPathfinder.FindRouteToNearestGoal(run.Layout, run.Start.Id, goals, Category, Wake));
        output.WriteLine($"{goals.Count} goals; single route {single.ToSummary()}; goal-set route {result.Route.ToSummary()}");

        Assert.Equal(goal.Id, result.GoalNodeId);
        Assert.Equal(single.Segments.Select(s => (s.FromNodeId, s.ToNodeId)), result.Route.Segments.Select(s => (s.FromNodeId, s.ToNodeId)));
    }

    /// <summary>
    /// SFO: M2 is closed to super-class aircraft both ways, so for a super an M2 node inside the lane is reachable only on the
    /// relaxed second pass, which lets one-way moves through with a warning. The goal-set search finds such goals there.
    /// </summary>
    [Fact]
    public void GoalsReachableOnlyAgainstAOneWay_FoundOnTheRelaxedPass()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("SFO") is not { } layout)
        {
            output.WriteLine("SKIP: KSFO layout unavailable");
            return;
        }

        const WakeTurbulenceData.WakeClass super = WakeTurbulenceData.WakeClass.Super;
        IReadOnlySet<(int From, int To)> forbidden = OneWayResolver.GetForbiddenMoves(layout, super);
        List<GroundNode> goals =
        [
            .. layout.Nodes.Values.Where(n => (n.Edges.Count >= 2) && n.Edges.All(e => forbidden.Contains((e.OtherNode(n).Id, n.Id)))),
        ];
        Assert.NotEmpty(goals);
        GroundNode start = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway("A")) && !n.Edges.Any(e => e.MatchesTaxiway("M2")))
            .OrderBy(n => Dist(n, goals[0]))
            .First();
        output.WriteLine($"start #{start.Id}; {goals.Count} goals inside M2, first #{goals[0].Id}");

        SearchContext firstPass = NodeContext(layout, start.Id, goals[0].Id, super);
        Assert.True(firstPass.HasHardGates);
        (GoalRoute? hardRoute, PathfindingFailure? _) = AutoRouter.RunToGoals(firstPass, goals);
        Assert.Null(hardRoute);

        HashSet<int> goalIds = [.. goals.Select(n => n.Id)];
        GoalRoute result = Assert.IsType<GoalRoute>(TaxiPathfinder.FindRouteToNearestGoal(layout, start.Id, goalIds, Category, super));
        output.WriteLine($"goal #{result.GoalNodeId}: {result.Route.ToSummary()}");

        Assert.Contains(result.GoalNodeId, goalIds);
        Assert.Equal(result.GoalNodeId, result.Route.Segments[^1].ToNodeId);
    }

    private sealed record StartRun(AirportGroundLayout Layout, GroundNode Start);

    private StartRun? LoadStart()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            output.WriteLine("SKIP: KOAK layout unavailable");
            return null;
        }

        List<GroundNode> bar28R = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "B");
        GroundNode bar28L = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28L", "B")[0];
        GroundNode start = bar28R.OrderByDescending(node => Dist(node, bar28L)).First();
        return new StartRun(layout, start);
    }

    /// <summary>
    /// The search cost of the single-goal route from <paramref name="from"/> to <paramref name="to"/>, as its first pass finds
    /// it; null when none.
    /// </summary>
    private static double? SingleGoalCost(AirportGroundLayout layout, int from, int to)
    {
        (TaxiRoute? route, PathfindingFailure? _, double cost) = AutoRouter.RunWithCost(NodeContext(layout, from, to, Wake), null, 200_000);
        return route is null ? null : cost;
    }

    /// <summary>The node-to-node auto-route context <see cref="TaxiPathfinder.FindRoute"/> compiles, for a jet of <paramref name="wake"/>.</summary>
    private static SearchContext NodeContext(AirportGroundLayout layout, int from, int to, WakeTurbulenceData.WakeClass wake) =>
        SearchContext.Compile(
            layout,
            from,
            waypointSequence: [],
            destinationRunway: null,
            destinationParking: null,
            destinationSpot: null,
            destinationNodeId: to,
            explicitHoldShorts: null,
            category: Category,
            wakeClass: wake,
            preference: RoutePreference.FewestTurns,
            diagnosticLog: null,
            waypointTurnHints: null,
            startHeadingTrue: null
        );

    private static double Dist(GroundNode a, GroundNode b) => GeoMath.DistanceNm(a.Position, b.Position);
}
