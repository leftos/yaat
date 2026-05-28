using System.Collections.Immutable;

namespace Yaat.Sim.Data.Airport.V2;

/// <summary>
/// Immutable partial route during search. Linked list via <see cref="Previous"/> — no copying on extension.
/// </summary>
public sealed record PartialRoute(
    int HeadNodeId,
    double ArrivalBearing,
    IGroundEdge? LastEdge,
    string LastTaxiwayName,
    PartialRoute? Previous,
    int Depth,
    double AccumulatedCost,
    ImmutableHashSet<int> VisitedNodeIds
)
{
    /// <summary>
    /// Starting route — no edges yet, arrival bearing unknown (0).
    /// </summary>
    public static PartialRoute StartAt(int nodeId) =>
        new(
            HeadNodeId: nodeId,
            ArrivalBearing: 0.0,
            LastEdge: null,
            LastTaxiwayName: string.Empty,
            Previous: null,
            Depth: 0,
            AccumulatedCost: 0.0,
            VisitedNodeIds: ImmutableHashSet<int>.Empty.Add(nodeId)
        );

    /// <summary>
    /// Walk the <see cref="Previous"/> linked list and produce a flat forward-ordered
    /// list of <see cref="DirectionalEdge"/> for materialisation.
    /// </summary>
    public List<DirectionalEdge> MaterialiseEdges()
    {
        int depth = Depth;
        if (depth == 0)
        {
            return [];
        }

        var result = new DirectionalEdge[depth];
        var current = this;

        for (int i = depth - 1; i >= 0; i--)
        {
            var layout = current!;
            var edge = layout.LastEdge!;
            var prevNodeId = layout.Previous!.HeadNodeId;
            var prevNode = FindNode(layout, prevNodeId);
            var headNode = FindNode(layout, layout.HeadNodeId);
            result[i] = edge.Directed(prevNode, headNode);
            current = layout.Previous;
        }

        return [.. result];
    }

    private static GroundNode FindNode(PartialRoute route, int nodeId)
    {
        if (route.LastEdge is not null)
        {
            foreach (var n in route.LastEdge.Nodes)
            {
                if (n.Id == nodeId)
                {
                    return n;
                }
            }
        }

        var cursor = route.Previous;
        while (cursor?.LastEdge is not null)
        {
            foreach (var n in cursor.LastEdge.Nodes)
            {
                if (n.Id == nodeId)
                {
                    return n;
                }
            }

            cursor = cursor.Previous;
        }

        throw new InvalidOperationException($"Node {nodeId} not found in partial route chain.");
    }
}

/// <summary>
/// Priority queue entry for the A* open set. Ordered by ascending f-score.
/// </summary>
public sealed record SearchFrontierEntry(PartialRoute Route, double FScore) : IComparable<SearchFrontierEntry>
{
    public int CompareTo(SearchFrontierEntry? other)
    {
        if (other is null)
        {
            return 1;
        }

        int cmp = FScore.CompareTo(other.FScore);
        if (cmp != 0)
        {
            return cmp;
        }

        // Tie-break by depth descending (prefer deeper routes — closer to goal).
        return other.Route.Depth.CompareTo(Route.Depth);
    }
}

/// <summary>
/// Accumulated cost breakdown for diagnostics and variant scoring.
/// </summary>
public sealed record RouteCostBreakdown(
    double DistanceNm,
    double TurnBudgetDeg,
    int TaxiwayTransitions,
    int RunwayCrossings,
    int DirectionReversals,
    int ReverseArcPenalties,
    double UnauthorizedTaxiwayCost
);
