using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Test-only routing oracle: a verbatim copy of <see cref="AutoRouter"/>'s node-to-node A*
/// loop whose ONLY difference from production is the closed-set key. Production keys its
/// best-g-score dictionary by node id alone (<c>AutoRouter.cs</c>); this oracle keys by
/// <c>(nodeId, arrival-bearing-bucket)</c>.
///
/// <para>
/// Because onward-edge admissibility (<see cref="GeometricAdmissibility.IsAdmissible"/>)
/// depends on arrival bearing, bucketing the closed set by bearing makes this search
/// strictly more complete than production: it explores a superset of production's states,
/// so it can only ever find an equal-or-better route — or reach a destination production
/// declares unreachable. Any diff between the two is therefore exactly a case where
/// production's node-id-only pruning loses (the deferred state-aware-pruning fix, #4).
/// </para>
///
/// <para>
/// <paramref name="bearingBucketDeg"/> &lt;= 0 disables g-score dedup entirely — pure
/// exhaustive search bounded only by the per-path visited set and <paramref name="maxExpansions"/>.
/// That is the gold ground truth (no bucket-boundary error), used to spot-check the
/// bucketed oracle on diff pairs.
/// </para>
/// </summary>
public static class OracleAutoRouter
{
    public sealed record OracleResult(TaxiRoute? Route, string? FailReason, int Expansions, bool Exhausted);

    public static OracleResult Run(SearchContext ctx, int bearingBucketDeg, int maxExpansions)
    {
        if (ctx.Destination.TargetNodeId is not { } destId || !ctx.Layout.Nodes.TryGetValue(destId, out var destinationNode))
        {
            return new OracleResult(null, "Oracle requires a resolved Node destination.", 0, false);
        }

        if (!ctx.Layout.Nodes.TryGetValue(ctx.StartNodeId, out var startNode))
        {
            return new OracleResult(null, "StartNodeUnreachable", 0, false);
        }

        if (ctx.StartNodeId == destinationNode.Id)
        {
            return new OracleResult(RouteMaterialiser.Materialise([], ctx, []), null, 0, false);
        }

        bool dedup = bearingBucketDeg > 0;
        var openSet = new PriorityQueue<PartialRoute, double>();
        var bestG = new Dictionary<(int Node, int Bucket), double>();

        var startRoute = PartialRoute.StartAt(ctx.StartNodeId);
        double h0 = RouteCostFunction.Heuristic(startNode, destinationNode);
        if (dedup)
        {
            bestG[Key(startRoute.HeadNodeId, startRoute.ArrivalBearing, bearingBucketDeg)] = startRoute.AccumulatedCost;
        }

        openSet.Enqueue(startRoute, startRoute.AccumulatedCost + h0);

        int expansions = 0;
        while (openSet.Count > 0)
        {
            var current = openSet.Dequeue();
            expansions++;

            if (expansions > maxExpansions)
            {
                return new OracleResult(null, "SearchExhausted", expansions, true);
            }

            if (
                dedup
                && bestG.TryGetValue(Key(current.HeadNodeId, current.ArrivalBearing, bearingBucketDeg), out double recorded)
                && (current.AccumulatedCost > recorded + 1e-9)
            )
            {
                continue;
            }

            if (current.HeadNodeId == destinationNode.Id)
            {
                var edges = current.MaterialiseEdges();
                return new OracleResult(RouteMaterialiser.Materialise(edges, ctx, []), null, expansions, false);
            }

            if (!ctx.Layout.Nodes.TryGetValue(current.HeadNodeId, out var headNode))
            {
                continue;
            }

            foreach (var edge in headNode.Edges)
            {
                GroundNode nextNode = edge.OtherNode(headNode);

                if (current.VisitedNodeIds.Contains(nextNode.Id))
                {
                    continue;
                }

                if (!GeometricAdmissibility.IsAdmissible(current, edge, nextNode, ctx.Category))
                {
                    continue;
                }

                double incrementalCost = RouteCostFunction.IncrementalCost(current, edge, nextNode, ctx);
                double newGScore = current.AccumulatedCost + incrementalCost;

                // Compute the propagated arrival bearing BEFORE the dedup check so the
                // closed-set key reflects the real heading (matches AutoRouter's no-op handling).
                double arrivalBearing = GeometricAdmissibility.IsNoOpEdge(edge)
                    ? current.ArrivalBearing
                    : GeometricAdmissibility.GetArrivalBearing(edge, headNode, nextNode);

                if (dedup)
                {
                    var key = Key(nextNode.Id, arrivalBearing, bearingBucketDeg);
                    if (bestG.TryGetValue(key, out double existingBest) && (newGScore >= existingBest - 1e-9))
                    {
                        continue;
                    }

                    bestG[key] = newGScore;
                }

                string taxiwayName = RouteCostFunction.ResolveTaxiwayName(edge, current.HeadNodeId);

                var extended = current with
                {
                    HeadNodeId = nextNode.Id,
                    ArrivalBearing = arrivalBearing,
                    LastEdge = edge,
                    LastTaxiwayName = taxiwayName,
                    Previous = current,
                    Depth = current.Depth + 1,
                    AccumulatedCost = newGScore,
                    VisitedNodeIds = current.VisitedNodeIds.Add(nextNode.Id),
                };

                double heuristic = RouteCostFunction.Heuristic(nextNode, destinationNode);
                double priority = (newGScore + heuristic) - (extended.Depth * 1e-9);
                openSet.Enqueue(extended, priority);
            }
        }

        return new OracleResult(null, "DestinationUnreachable", expansions, false);
    }

    private static (int Node, int Bucket) Key(int nodeId, double bearing, int bucketDeg)
    {
        double normalized = bearing % 360.0;
        if (normalized < 0.0)
        {
            normalized += 360.0;
        }

        return (nodeId, (int)(normalized / bucketDeg));
    }
}
