using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>Connected parts of a ground graph, by its edges.</summary>
internal static class GraphComponents
{
    /// <summary>The committed test layouts, in the order <see cref="FindDisconnectedPair"/> searches them.</summary>
    private static readonly string[] AirportIds =
    [
        "OAK",
        "SFO",
        "SJC",
        "SMF",
        "FAT",
        "MER",
        "HWD",
        "RNO",
        "LAX",
        "SEA",
        "ATL",
        "AUS",
        "COS",
        "FLL",
        "IAH",
        "MIA",
        "MSY",
    ];

    /// <summary>
    /// A layout whose graph has a part no edge joins to its largest part: a node of the largest part with an edge, and the
    /// nodes of the first other part with at least one edge (an island of pavement, not a lone node).
    /// </summary>
    internal sealed record DisconnectedPair(AirportGroundLayout Layout, GroundNode MainNode, List<GroundNode> Island);

    /// <summary>The first committed test layout with a disconnected island, or null when every one is a single connected graph.</summary>
    internal static DisconnectedPair? FindDisconnectedPair()
    {
        var groundData = new TestAirportGroundData();
        foreach (string airportId in AirportIds)
        {
            if ((groundData.GetLayout(airportId) is { } layout) && (SplitOf(layout) is { } split))
            {
                return split;
            }
        }

        return null;
    }

    private static DisconnectedPair? SplitOf(AirportGroundLayout layout)
    {
        List<HashSet<int>> parts = [];
        HashSet<int> placed = [];
        foreach (GroundNode node in layout.Nodes.Values.Where(n => n.Edges.Count > 0).OrderBy(n => n.Id))
        {
            if (!placed.Contains(node.Id))
            {
                HashSet<int> part = ComponentOf(node);
                placed.UnionWith(part);
                parts.Add(part);
            }
        }

        if (parts.Count < 2)
        {
            return null;
        }

        HashSet<int> main = parts.MaxBy(p => p.Count)!;
        HashSet<int> island = parts.First(p => p != main);
        return new DisconnectedPair(layout, layout.Nodes[main.Min()], [.. island.Order().Select(id => layout.Nodes[id])]);
    }

    /// <summary>The ids of every node an edge path joins to <paramref name="start"/>, itself included.</summary>
    internal static HashSet<int> ComponentOf(GroundNode start)
    {
        HashSet<int> seen = [start.Id];
        Queue<GroundNode> queue = new([start]);
        while (queue.Count > 0)
        {
            GroundNode node = queue.Dequeue();
            foreach (IGroundEdge edge in node.Edges)
            {
                GroundNode next = edge.OtherNode(node);
                if (seen.Add(next.Id))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return seen;
    }
}
