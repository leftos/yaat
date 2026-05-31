using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>Detect shape-point nodes excluded from filleting.</summary>
public static class ManualArcDetector
{
    private static readonly ILogger Log = SimLog.CreateLogger("ManualArcDetector");

    public static HashSet<int> Detect(AirportGroundLayout layout)
    {
        var excluded = new HashSet<int>();
        foreach (var node in layout.Nodes.Values)
        {
            if (IsShapePointNode(node))
            {
                excluded.Add(node.Id);
            }
        }

        if ((excluded.Count > 0) && Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Excluding {Count} shape-point nodes from filleting", excluded.Count);
        }

        return excluded;
    }

    public static bool IsShapePointNode(GroundNode node)
    {
        if (node.Type != GroundNodeType.TaxiwayIntersection)
        {
            return false;
        }

        var edges = node.Edges.OfType<GroundEdge>().ToList();
        if (edges.Count != 2)
        {
            return false;
        }

        return edges[0].TaxiwayName == edges[1].TaxiwayName;
    }
}
