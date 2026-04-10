using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Factory for creating virtual ground nodes — navigation targets that exist along graph edges
/// but aren't part of the airport layout. Virtual nodes are first-class <see cref="GroundNode"/>
/// instances with unique negative IDs and virtual edges connecting them to real nodes.
/// </summary>
public static class VirtualNode
{
    private static readonly ILogger Log = SimLog.CreateLogger("VirtualNode");

    private static int _nextId = -100;

    /// <summary>
    /// Create a <see cref="GroundNode"/> at the given position with a unique negative ID.
    /// </summary>
    public static GroundNode Create(double latitude, double longitude)
    {
        return new GroundNode
        {
            Id = Interlocked.Decrement(ref _nextId),
            Latitude = latitude,
            Longitude = longitude,
            Type = GroundNodeType.TaxiwayIntersection,
            Origin = "VirtualNode:created",
        };
    }

    /// <summary>
    /// Create a virtual edge connecting two nodes with populated node references.
    /// </summary>
    public static GroundEdge CreateEdge(GroundNode nodeA, GroundNode nodeB, string taxiwayName)
    {
        double dist = GeoMath.DistanceNm(nodeA.Latitude, nodeA.Longitude, nodeB.Latitude, nodeB.Longitude);
        return new GroundEdge
        {
            Nodes = [nodeA, nodeB],
            TaxiwayName = taxiwayName,
            DistanceNm = dist,
            Origin = "VirtualNode:edge",
        };
    }

    /// <summary>
    /// Create a <see cref="TaxiRouteSegment"/> from <paramref name="fromNode"/> to
    /// <paramref name="toNode"/> with a virtual edge. The segment carries full node references
    /// so the navigator can resolve coordinates without layout lookups.
    /// </summary>
    public static TaxiRouteSegment CreateSegment(GroundNode fromNode, GroundNode toNode, string taxiwayName)
    {
        IGroundEdge edge = CreateEdge(fromNode, toNode, taxiwayName);
        return new TaxiRouteSegment { TaxiwayName = taxiwayName, Edge = edge.Directed(fromNode, toNode) };
    }

    /// <summary>
    /// Offset BACKWARD from a node along the route toward the approach direction.
    /// Walks route segments in reverse if the offset exceeds the immediate approach edge.
    /// Used for hold-short setbacks where the aircraft stops before reaching the node.
    /// </summary>
    public static GroundNode OffsetBefore(AirportGroundLayout layout, TaxiRoute route, int nodeId, double offsetNm)
    {
        if (!layout.Nodes.TryGetValue(nodeId, out var node))
        {
            return Create(0, 0);
        }

        double remaining = offsetNm;
        int currentId = nodeId;
        GroundNode currentNode = node;
        double lastBearing = double.NaN;

        while (remaining > 0)
        {
            int approachId = FindApproachNodeId(route, currentId);
            if (approachId < 0 || !layout.Nodes.TryGetValue(approachId, out var approachNode))
            {
                break;
            }

            double edgeLen = GeoMath.DistanceNm(approachNode.Latitude, approachNode.Longitude, currentNode.Latitude, currentNode.Longitude);
            lastBearing = GeoMath.BearingTo(currentNode.Latitude, currentNode.Longitude, approachNode.Latitude, approachNode.Longitude);

            if (edgeLen < 1e-9)
            {
                currentId = approachId;
                currentNode = approachNode;
                continue;
            }

            if (remaining <= edgeLen)
            {
                var (lat, lon) = GeoMath.ProjectPointRaw(currentNode.Latitude, currentNode.Longitude, lastBearing, remaining);
                return Create(lat, lon);
            }

            remaining -= edgeLen;
            currentId = approachId;
            currentNode = approachNode;
        }

        // Ran out of route edges — project remaining distance along last known bearing,
        // or fall back to the current node position.
        if (remaining > 0 && !double.IsNaN(lastBearing))
        {
            var (lat, lon) = GeoMath.ProjectPointRaw(currentNode.Latitude, currentNode.Longitude, lastBearing, remaining);
            return Create(lat, lon);
        }

        Log.LogDebug("[VirtualNode] OffsetBefore: no approach edge for node {NodeId}", nodeId);
        return Create(currentNode.Latitude, currentNode.Longitude);
    }

    /// <summary>
    /// Offset FORWARD past a node along the graph, away from the approach direction.
    /// Infers the continuation edge from the graph (prefers same taxiway, falls back to best-aligned).
    /// Used for tail clearance past intersections and hold-short lines.
    /// </summary>
    public static GroundNode OffsetPast(AirportGroundLayout layout, GroundNode node, GroundNode approachFrom, double offsetNm)
    {
        return OffsetPastCore(layout, node, approachFrom.Latitude, approachFrom.Longitude, offsetNm);
    }

    /// <summary>
    /// Overload for when the approach direction is a position rather than a node
    /// (e.g., aircraft on runway centerline approaching an exit).
    /// </summary>
    public static GroundNode OffsetPast(AirportGroundLayout layout, GroundNode node, double approachFromLat, double approachFromLon, double offsetNm)
    {
        return OffsetPastCore(layout, node, approachFromLat, approachFromLon, offsetNm);
    }

    private static GroundNode OffsetPastCore(AirportGroundLayout layout, GroundNode node, double approachLat, double approachLon, double offsetNm)
    {
        double remaining = offsetNm;
        GroundNode currentNode = node;
        double prevLat = approachLat;
        double prevLon = approachLon;

        while (remaining > 0)
        {
            GroundNode? nextNode = FindContinuationNode(currentNode, prevLat, prevLon);
            if (nextNode is null)
            {
                double forwardBearing = GeoMath.BearingTo(prevLat, prevLon, currentNode.Latitude, currentNode.Longitude);
                var (lat, lon) = GeoMath.ProjectPointRaw(currentNode.Latitude, currentNode.Longitude, forwardBearing, remaining);
                return Create(lat, lon);
            }

            double edgeLen = GeoMath.DistanceNm(currentNode.Latitude, currentNode.Longitude, nextNode.Latitude, nextNode.Longitude);
            if (edgeLen < 1e-9)
            {
                prevLat = currentNode.Latitude;
                prevLon = currentNode.Longitude;
                currentNode = nextNode;
                continue;
            }

            if (remaining <= edgeLen)
            {
                double bearing = GeoMath.BearingTo(currentNode.Latitude, currentNode.Longitude, nextNode.Latitude, nextNode.Longitude);
                var (lat, lon) = GeoMath.ProjectPointRaw(currentNode.Latitude, currentNode.Longitude, bearing, remaining);
                return Create(lat, lon);
            }

            remaining -= edgeLen;
            prevLat = currentNode.Latitude;
            prevLon = currentNode.Longitude;
            currentNode = nextNode;
        }

        return Create(currentNode.Latitude, currentNode.Longitude);
    }

    /// <summary>
    /// Finds the next node to continue along from <paramref name="currentNode"/>, away from the
    /// previous position. Prefers edges on the same taxiway; falls back to best-aligned bearing.
    /// Uses <see cref="GroundEdge.OtherNode"/> — requires populated node references.
    /// </summary>
    private static GroundNode? FindContinuationNode(GroundNode currentNode, double prevLat, double prevLon)
    {
        double forwardBearing = GeoMath.BearingTo(prevLat, prevLon, currentNode.Latitude, currentNode.Longitude);

        // Find the approach edge's taxiway name
        string? approachTaxiway = null;
        double bestApproachAlignment = double.MaxValue;
        double approachBearing = GeoMath.BearingTo(currentNode.Latitude, currentNode.Longitude, prevLat, prevLon);

        foreach (var edge in currentNode.Edges)
        {
            var other = edge.OtherNode(currentNode);
            if (other is null)
            {
                continue;
            }

            double edgeBearing = GeoMath.BearingTo(currentNode.Latitude, currentNode.Longitude, other.Latitude, other.Longitude);
            double alignment = GeoMath.AbsBearingDifference(approachBearing, edgeBearing);
            if (alignment < bestApproachAlignment)
            {
                bestApproachAlignment = alignment;
                approachTaxiway = edge.TaxiwayName;
            }
        }

        GroundNode? bestSameTaxiway = null;
        double bestSameTaxiwayAlignment = double.MaxValue;
        GroundNode? bestAny = null;
        double bestAnyAlignment = double.MaxValue;

        foreach (var edge in currentNode.Edges)
        {
            var otherNode = edge.OtherNode(currentNode);
            if (otherNode is null)
            {
                continue;
            }

            double edgeBearing = GeoMath.BearingTo(currentNode.Latitude, currentNode.Longitude, otherNode.Latitude, otherNode.Longitude);
            double alignment = GeoMath.AbsBearingDifference(forwardBearing, edgeBearing);

            // Skip edges that point back toward where we came from (>90° off forward)
            if (alignment > 90)
            {
                continue;
            }

            if (approachTaxiway is not null && edge.TaxiwayName == approachTaxiway)
            {
                if (alignment < bestSameTaxiwayAlignment)
                {
                    bestSameTaxiwayAlignment = alignment;
                    bestSameTaxiway = otherNode;
                }
            }

            if (alignment < bestAnyAlignment)
            {
                bestAnyAlignment = alignment;
                bestAny = otherNode;
            }
        }

        return bestSameTaxiway ?? bestAny;
    }

    private static int FindApproachNodeId(TaxiRoute route, int nodeId)
    {
        foreach (var seg in route.Segments)
        {
            if (seg.ToNodeId == nodeId)
            {
                return seg.FromNodeId;
            }
        }

        return -1;
    }
}
