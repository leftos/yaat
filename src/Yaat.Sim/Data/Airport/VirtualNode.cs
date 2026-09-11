using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Factory for creating virtual ground nodes — navigation targets that exist along graph edges
/// but aren't part of the airport layout. Virtual nodes are first-class <see cref="GroundNode"/>
/// instances with negative ids derived from the position, so the same point yields the same id in
/// every process and after a snapshot restore, and virtual edges connecting them to real nodes.
/// </summary>
public static class VirtualNode
{
    private static readonly ILogger Log = SimLog.CreateLogger("VirtualNode");

    /// <summary>The least negative id a position can hash to — below every layout node id and the small negative sentinels.</summary>
    private const int FirstId = -101;

    /// <summary>Positions are quantised to this many degrees before hashing: two points inside one cell are ~1 cm apart.</summary>
    private const double PositionQuantumDeg = 1e-7;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;

    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Create a <see cref="GroundNode"/> at the given position with a negative id derived from that position.
    /// </summary>
    public static GroundNode Create(double latitude, double longitude)
    {
        return new GroundNode
        {
            Id = IdFor(latitude, longitude),
            Position = new LatLon(latitude, longitude),
            Type = GroundNodeType.TaxiwayIntersection,
            Origin = "VirtualNode:created",
        };
    }

    /// <summary>
    /// The id for a position: FNV-1a 64 over the latitude and longitude quantised to
    /// <see cref="PositionQuantumDeg"/>, folded into <c>[0, int.MaxValue - 101)</c> and reflected below
    /// <see cref="FirstId"/>. A pure function of the position, and of nothing else: a free-space leg's
    /// negative from-node id is serialised into every snapshot, so an id minted from a process-global
    /// counter makes two same-seed runs disagree byte-for-byte and makes a restore rebuild the same point
    /// under a different id. <c>string.GetHashCode</c> and <c>HashCode.Combine</c> are randomised per
    /// process and cannot be used here.
    ///
    /// <para>
    /// Unique per POSITION, not per node: two virtual nodes created at the same point share one id, which is
    /// the design — the same point is the same navigation target however many times it is minted. Stability
    /// across a snapshot therefore rests on the latitude and longitude round-tripping exactly as doubles,
    /// which the JSON and MessagePack serialisers in use do; a serialiser that rounded them would re-mint the
    /// point under a different id.
    /// </para>
    /// </summary>
    private static int IdFor(double latitude, double longitude)
    {
        ulong hash = HashInt64(FnvOffsetBasis, (long)Math.Round(latitude / PositionQuantumDeg));
        hash = HashInt64(hash, (long)Math.Round(longitude / PositionQuantumDeg));

        uint folded = (uint)(hash ^ (hash >> 32));
        int index = (int)(folded % (uint)(int.MaxValue + FirstId));
        return FirstId - index;
    }

    /// <summary>Fold the eight bytes of <paramref name="value"/> into <paramref name="hash"/> (FNV-1a 64).</summary>
    private static ulong HashInt64(ulong hash, long value)
    {
        ulong bits = (ulong)value;
        for (int i = 0; i < 8; i++)
        {
            hash ^= (bits >> (i * 8)) & 0xFF;
            hash *= FnvPrime;
        }

        return hash;
    }

    private const string EdgeOrigin = "VirtualNode:edge";

    /// <summary>
    /// Create a virtual edge connecting two nodes with populated node references.
    /// </summary>
    public static GroundEdge CreateEdge(GroundNode nodeA, GroundNode nodeB, string taxiwayName)
    {
        double dist = GeoMath.DistanceNm(nodeA.Position, nodeB.Position);
        return new GroundEdge
        {
            Nodes = [nodeA, nodeB],
            TaxiwayName = taxiwayName,
            DistanceNm = dist,
            Origin = EdgeOrigin,
        };
    }

    /// <summary>True for an edge made by <see cref="CreateEdge"/> — a free-space leg the layout never held.</summary>
    public static bool IsVirtualEdge(IGroundEdge edge) => string.Equals(edge.Origin, EdgeOrigin, StringComparison.Ordinal);

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
    ///
    /// When <paramref name="stopAtRunwayHoldShort"/> is set, the walk clamps at a
    /// <see cref="GroundNodeType.RunwayHoldShort"/> node rather than projecting past it: a taxiway
    /// hold-short just beyond a runway crossing would otherwise set the stop point back onto the
    /// runway the aircraft just crossed. Clamped, the aircraft holds at the runway hold-short line
    /// (tail over the bars) instead of reversing onto the runway.
    /// </summary>
    public static GroundNode OffsetBefore(AirportGroundLayout layout, TaxiRoute route, int nodeId, double offsetNm, bool stopAtRunwayHoldShort)
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

            double edgeLen = GeoMath.DistanceNm(approachNode.Position, currentNode.Position);
            lastBearing = GeoMath.BearingTo(currentNode.Position, approachNode.Position);

            if (edgeLen < 1e-9)
            {
                currentId = approachId;
                currentNode = approachNode;
                continue;
            }

            if (stopAtRunwayHoldShort && approachNode.Type == GroundNodeType.RunwayHoldShort && remaining > edgeLen)
            {
                // Stopping farther back would land on the runway just crossed. Clamp at the
                // runway hold-short line: the aircraft holds at the downstream taxiway line with
                // its tail over the bars instead of reversing onto the runway.
                return Create(approachNode.Position.Lat, approachNode.Position.Lon);
            }

            if (remaining <= edgeLen)
            {
                var (lat, lon) = GeoMath.ProjectPointRaw(currentNode.Position.Lat, currentNode.Position.Lon, lastBearing, remaining);
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
            var (lat, lon) = GeoMath.ProjectPointRaw(currentNode.Position.Lat, currentNode.Position.Lon, lastBearing, remaining);
            return Create(lat, lon);
        }

        Log.LogDebug("[VirtualNode] OffsetBefore: no approach edge for node {NodeId}", nodeId);
        return Create(currentNode.Position.Lat, currentNode.Position.Lon);
    }

    /// <summary>
    /// Offset FORWARD past a node along the graph, away from the approach direction.
    /// Infers the continuation edge from the graph (prefers same taxiway, falls back to best-aligned).
    /// Used for tail clearance past intersections and hold-short lines.
    /// </summary>
    public static GroundNode OffsetPast(AirportGroundLayout layout, GroundNode node, GroundNode approachFrom, double offsetNm)
    {
        return OffsetPastCore(layout, node, approachFrom.Position.Lat, approachFrom.Position.Lon, offsetNm);
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
                double forwardBearing = GeoMath.BearingTo(prevLat, prevLon, currentNode.Position.Lat, currentNode.Position.Lon);
                var (lat, lon) = GeoMath.ProjectPointRaw(currentNode.Position.Lat, currentNode.Position.Lon, forwardBearing, remaining);
                return Create(lat, lon);
            }

            double edgeLen = GeoMath.DistanceNm(currentNode.Position, nextNode.Position);
            if (edgeLen < 1e-9)
            {
                prevLat = currentNode.Position.Lat;
                prevLon = currentNode.Position.Lon;
                currentNode = nextNode;
                continue;
            }

            if (remaining <= edgeLen)
            {
                double bearing = GeoMath.BearingTo(currentNode.Position, nextNode.Position);
                var (lat, lon) = GeoMath.ProjectPointRaw(currentNode.Position.Lat, currentNode.Position.Lon, bearing, remaining);
                return Create(lat, lon);
            }

            remaining -= edgeLen;
            prevLat = currentNode.Position.Lat;
            prevLon = currentNode.Position.Lon;
            currentNode = nextNode;
        }

        return Create(currentNode.Position.Lat, currentNode.Position.Lon);
    }

    /// <summary>
    /// Finds the next node to continue along from <paramref name="currentNode"/>, away from the
    /// previous position. Prefers edges on the same taxiway; falls back to best-aligned bearing.
    /// Uses <see cref="GroundEdge.OtherNode"/> — requires populated node references.
    /// </summary>
    private static GroundNode? FindContinuationNode(GroundNode currentNode, double prevLat, double prevLon)
    {
        double forwardBearing = GeoMath.BearingTo(prevLat, prevLon, currentNode.Position.Lat, currentNode.Position.Lon);

        // Find the approach edge's taxiway name
        string? approachTaxiway = null;
        double bestApproachAlignment = double.MaxValue;
        double approachBearing = GeoMath.BearingTo(currentNode.Position.Lat, currentNode.Position.Lon, prevLat, prevLon);

        foreach (var edge in currentNode.Edges)
        {
            var other = edge.OtherNode(currentNode);
            if (other is null)
            {
                continue;
            }

            double edgeBearing = GeoMath.BearingTo(currentNode.Position, other.Position);
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

            double edgeBearing = GeoMath.BearingTo(currentNode.Position, otherNode.Position);
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
