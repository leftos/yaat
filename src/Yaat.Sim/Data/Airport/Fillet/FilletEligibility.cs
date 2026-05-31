using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>Intersection eligibility rules for the fillet pass.</summary>
public static class FilletEligibility
{
    private static readonly ILogger Log = SimLog.CreateLogger("FilletEligibility");

    public static bool IsEligible(GroundNode node) => IsEligible(node, out _);

    public static bool IsEligible(GroundNode node, out bool preserveNode)
    {
        preserveNode = false;

        if (node.Origin?.StartsWith("RunwayCrossing:centerline-projection", StringComparison.Ordinal) == true)
        {
            return false;
        }

        if (node.Type != GroundNodeType.TaxiwayIntersection)
        {
            return false;
        }

        if (node.Edges.Count < 2)
        {
            return false;
        }

        int runwayEdgeCount = 0;
        int nonRunwayEdgeCount = 0;
        foreach (var edge in node.Edges)
        {
            if (edge.IsRunwayCenterline)
            {
                runwayEdgeCount++;
            }
            else
            {
                nonRunwayEdgeCount++;
            }
        }

        if ((runwayEdgeCount == 1) && (nonRunwayEdgeCount > 0))
        {
            preserveNode = true;
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("[Eligibility] Node #{Id}: preserve=true (rwy={Rwy}, nonRwy={NonRwy})", node.Id, runwayEdgeCount, nonRunwayEdgeCount);
            }

            return true;
        }

        if ((runwayEdgeCount == 1) && (nonRunwayEdgeCount == 0))
        {
            return false;
        }

        if ((runwayEdgeCount == 0) && (nonRunwayEdgeCount == 2))
        {
            var edges = node.Edges.OfType<GroundEdge>().ToList();
            if ((edges.Count == 2) && (edges[0].TaxiwayName == edges[1].TaxiwayName))
            {
                return false;
            }
        }

        return true;
    }
}
