using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// Shared geometry for sidecar constructs authored as ordered coordinate polylines (one-way edges,
/// blocked turns): snaps each waypoint to the nearest graph node, and traces the connected node span
/// between two snapped nodes (a direct edge, else a taxiway-restricted BFS along a taxiway both share).
/// Keyed on node ids so it works identically for straight edges and arcs. Used by
/// <see cref="OneWayResolver"/> and <see cref="BlockedTurnResolver"/>.
/// </summary>
internal static class PolylineSnapper
{
    /// <summary>
    /// Snaps each waypoint of <paramref name="path"/> to its nearest node. Returns null (and warns via
    /// <paramref name="log"/>) when any waypoint snaps to no node. A waypoint's optional taxiway hint that
    /// does not match the snapped node's edges is warned about but not fatal (the map may have drifted).
    /// <paramref name="context"/> labels the warnings (e.g. "One-way", "Blocked-turn").
    /// </summary>
    public static List<GroundNode>? Snap(AirportGroundLayout layout, IReadOnlyList<OneWayPoint> path, string context, ILogger log)
    {
        var nodes = new List<GroundNode>(path.Count);
        foreach (var wp in path)
        {
            var node = layout.FindNearestNode(wp.Lat, wp.Lon);
            if (node is null)
            {
                log.LogWarning(
                    "{Context} at {Airport}: waypoint ({Lat},{Lon}) snapped to no node; skipping constraint",
                    context,
                    layout.AirportId,
                    wp.Lat,
                    wp.Lon
                );
                return null;
            }

            if (wp.Taxiway is not null && !node.Edges.Any(e => e.MatchesTaxiway(wp.Taxiway)))
            {
                log.LogWarning(
                    "{Context} at {Airport}: waypoint ({Lat},{Lon}) expected taxiway {Taxiway} but snapped node {Node} carries none (map may have drifted)",
                    context,
                    layout.AirportId,
                    wp.Lat,
                    wp.Lon,
                    wp.Taxiway,
                    node.Id
                );
            }

            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>
    /// Ordered directed edge span from <paramref name="a"/> to <paramref name="b"/>: empty when they are
    /// the same node, a single edge when directly connected, otherwise a taxiway-restricted BFS along a
    /// taxiway both sit on. Null when none applies.
    /// </summary>
    public static List<(int From, int To)>? BuildSpan(AirportGroundLayout layout, GroundNode a, GroundNode b)
    {
        if (a.Id == b.Id)
        {
            return [];
        }

        if (a.Edges.Any(e => e.HasNode(b.Id)))
        {
            return [(a.Id, b.Id)];
        }

        foreach (string taxiway in TaxiwaysAt(a))
        {
            if (!IsOnTaxiway(b, taxiway))
            {
                continue;
            }

            var path = BfsAlongTaxiway(layout, a, b, taxiway);
            if (path is not null)
            {
                return path;
            }
        }

        return null;
    }

    private static List<(int From, int To)>? BfsAlongTaxiway(AirportGroundLayout layout, GroundNode start, GroundNode goal, string taxiway)
    {
        var parent = new Dictionary<int, int> { [start.Id] = -1 };
        var queue = new Queue<int>();
        queue.Enqueue(start.Id);

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            if (current == goal.Id)
            {
                break;
            }

            if (!layout.Nodes.TryGetValue(current, out var node))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                if (!edge.MatchesTaxiway(taxiway))
                {
                    continue;
                }

                int next = edge.OtherNodeId(current);
                if (parent.TryAdd(next, current))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return parent.ContainsKey(goal.Id) ? ReconstructPath(parent, goal.Id) : null;
    }

    private static List<(int From, int To)> ReconstructPath(Dictionary<int, int> parent, int goalId)
    {
        var path = new List<(int From, int To)>();
        for (int node = goalId; parent[node] != -1; node = parent[node])
        {
            path.Add((parent[node], node));
        }

        path.Reverse();
        return path;
    }

    private static IEnumerable<string> TaxiwaysAt(GroundNode node) =>
        node.Edges.SelectMany(EdgeTaxiwayNames).Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool IsOnTaxiway(GroundNode node, string taxiway) => node.Edges.Any(e => e.MatchesTaxiway(taxiway));

    private static IEnumerable<string> EdgeTaxiwayNames(IGroundEdge edge) => edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];
}
