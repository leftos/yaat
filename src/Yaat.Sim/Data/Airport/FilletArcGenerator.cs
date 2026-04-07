using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Replaces eligible intersection nodes with fillet arcs. For each intersection node
/// with 2+ edges, every edge pair gets a fillet: a <see cref="GroundArc"/> for angled
/// pairs (≥15° turn), or a merged straight <see cref="GroundEdge"/> for collinear pairs.
/// The intersection node is deleted after all its edge pairs are processed.
/// </summary>
public static class FilletArcGenerator
{
    private static readonly ILogger Log = SimLog.CreateLogger("FilletArcGenerator");

    private const double MinFilletAngleDeg = 15.0;
    private const double CollinearThresholdDeg = 15.0;
    private const double DefaultRadiusFt = 75.0;
    private const double HighSpeedExitRadiusFt = 150.0;
    private const double RunwayExitRadiusFt = 100.0;
    private const double RampRadiusFt = 50.0;

    /// <summary>
    /// Apply fillet arcs to all eligible intersection nodes in the layout.
    /// Mutates the layout in place: inserts tangent-point nodes, creates arcs,
    /// shortens/removes original edges, and deletes filleted intersection nodes.
    /// </summary>
    public static void Apply(AirportGroundLayout layout)
    {
        int nextNodeId = layout.Nodes.Keys.DefaultIfEmpty(0).Max() + 1;

        // Snapshot the intersection nodes to process — we'll be mutating the graph.
        var intersections = layout.Nodes.Values.Where(IsEligibleForFilleting).ToList();

        int filletedCount = 0;
        int arcCount = 0;
        int mergedCount = 0;

        foreach (var node in intersections)
        {
            // Skip nodes already removed by a prior iteration (e.g., collinear merge
            // removed both endpoints of a pair that shared this node).
            if (!layout.Nodes.ContainsKey(node.Id))
            {
                continue;
            }

            // Rebuild adjacency so this node sees the current graph state
            // (prior iterations may have shortened or removed edges).
            layout.RebuildAdjacencyLists();

            if (node.Edges.Count < 2)
            {
                continue;
            }

            var result = FilletNode(layout, node, ref nextNodeId);
            if (result.Success)
            {
                filletedCount++;
                arcCount += result.ArcsCreated;
                mergedCount += result.EdgesMerged;
            }
        }

        layout.RebuildAdjacencyLists();

        Log.LogInformation(
            "Fillet arcs: {FilletedNodes} nodes filleted, {Arcs} arcs created, {Merged} edges merged",
            filletedCount,
            arcCount,
            mergedCount
        );
    }

    private static bool IsEligibleForFilleting(GroundNode node)
    {
        // Only fillet plain intersection nodes — not hold-shorts, parking, spots, helipads
        if (node.Type != GroundNodeType.TaxiwayIntersection)
        {
            return false;
        }

        if (node.Edges.Count < 2)
        {
            return false;
        }

        // Skip runway endpoints — nodes with exactly one RWY edge (threshold / end of centerline).
        // Mid-centerline nodes (2+ RWY edges) are fine: the collinear RWY pair merges and
        // taxiway branches get arcs.
        int runwayEdgeCount = 0;
        foreach (var edge in node.Edges)
        {
            if (edge.IsRunway)
            {
                runwayEdgeCount++;
            }
        }

        if (runwayEdgeCount == 1)
        {
            return false;
        }

        return true;
    }

    private static (bool Success, int ArcsCreated, int EdgesMerged) FilletNode(
        AirportGroundLayout layout,
        GroundNode intersection,
        ref int nextNodeId
    )
    {
        // Collect edges as concrete GroundEdge — arcs from previous iterations are skipped
        var edges = new List<GroundEdge>();
        foreach (var e in intersection.Edges)
        {
            if (e is GroundEdge ge)
            {
                edges.Add(ge);
            }
        }

        if (edges.Count < 2)
        {
            return (false, 0, 0);
        }

        // Compute bearing from intersection along each edge (toward the other end)
        var edgeBearings = new List<(GroundEdge Edge, GroundNode OtherNode, double Bearing)>();
        foreach (var edge in edges)
        {
            var other = edge.OtherNode(intersection);
            double bearing = InitialBearing(intersection, other, edge);
            edgeBearings.Add((edge, other, bearing));
        }

        // --- Phase A: Compute all fillets without mutating the graph ---
        // Each edge can have at most one tangent point (closest to the intersection).
        // For 3+ way intersections the same edge participates in multiple pairs but
        // gets only one tangent point — the one closest to the intersection (largest
        // tangent distance wins because the radius is largest).

        // Maps edge → (tangent lat, tangent lon, tangent distance from intersection)
        var edgeTangentSpecs = new Dictionary<GroundEdge, (double Lat, double Lon, double TangentDistNm)>();

        // Planned arcs and collinear merges
        var plannedArcs = new List<(GroundEdge EdgeA, GroundEdge EdgeB, double RadiusFt, double TurnAngleDeg)>();
        var plannedMerges = new List<(GroundEdge EdgeA, GroundNode OtherA, GroundEdge EdgeB, GroundNode OtherB)>();

        for (int i = 0; i < edgeBearings.Count; i++)
        {
            for (int j = i + 1; j < edgeBearings.Count; j++)
            {
                var (edgeA, otherA, bearingA) = edgeBearings[i];
                var (edgeB, otherB, bearingB) = edgeBearings[j];

                double turnAngle = ComputeTurnAngle(bearingA, bearingB);

                if (turnAngle < CollinearThresholdDeg)
                {
                    plannedMerges.Add((edgeA, otherA, edgeB, otherB));
                }
                else if (turnAngle >= MinFilletAngleDeg)
                {
                    double halfAngleRad = (turnAngle / 2.0) * (Math.PI / 180.0);
                    double tanHalf = Math.Tan(halfAngleRad);

                    // Compute edge lengths from intersection to the other endpoint
                    double edgeALenFt =
                        GeoMath.DistanceNm(intersection.Latitude, intersection.Longitude, otherA.Latitude, otherA.Longitude) * GeoMath.FeetPerNm;
                    double edgeBLenFt =
                        GeoMath.DistanceNm(intersection.Latitude, intersection.Longitude, otherB.Latitude, otherB.Longitude) * GeoMath.FeetPerNm;

                    // Max radius that fits both edges: R = edgeLen / tan(θ/2)
                    double maxFitRadiusFt = Math.Min(edgeALenFt, edgeBLenFt) / tanHalf;

                    // Clamp to the configured max for this edge type
                    double maxRadiusFt = SelectMaxRadius(edgeA, edgeB, turnAngle);
                    double radiusFt = Math.Min(maxFitRadiusFt, maxRadiusFt);

                    double tangentDistFt = radiusFt * tanHalf;
                    double tangentDistNm = tangentDistFt / GeoMath.FeetPerNm;

                    // Record tangent point for each edge — keep the one farthest from intersection
                    // (largest tangent distance) so the arc radius is honored
                    RecordTangentPoint(edgeTangentSpecs, edgeA, intersection, bearingA, tangentDistNm);
                    RecordTangentPoint(edgeTangentSpecs, edgeB, intersection, bearingB, tangentDistNm);

                    plannedArcs.Add((edgeA, edgeB, radiusFt, turnAngle));
                }
            }
        }

        if ((plannedArcs.Count + plannedMerges.Count) == 0)
        {
            return (false, 0, 0);
        }

        // --- Phase B: Create tangent-point nodes ---
        var edgeTangentNodes = new Dictionary<GroundEdge, GroundNode>();
        foreach (var (edge, (lat, lon, _)) in edgeTangentSpecs)
        {
            int id = nextNodeId++;
            var node = new GroundNode
            {
                Id = id,
                Latitude = lat,
                Longitude = lon,
                Type = GroundNodeType.TaxiwayIntersection,
            };
            layout.Nodes[id] = node;
            edgeTangentNodes[edge] = node;
        }

        // --- Phase C: Create arcs between tangent-point pairs ---
        int arcsCreated = 0;
        foreach (var (edgeA, edgeB, radiusFt, turnAngleDeg) in plannedArcs)
        {
            if (!edgeTangentNodes.TryGetValue(edgeA, out var tanNodeA) || !edgeTangentNodes.TryGetValue(edgeB, out var tanNodeB))
            {
                continue;
            }

            int idxA = edgeBearings.FindIndex(x => x.Edge == edgeA);
            int idxB = edgeBearings.FindIndex(x => x.Edge == edgeB);
            double bearingA = edgeBearings[idxA].Bearing;
            double bearingB = edgeBearings[idxB].Bearing;

            var (centerLat, centerLon) = ComputeArcCenter(intersection, bearingA, bearingB, radiusFt);

            double sweepRad = (180.0 - turnAngleDeg) * (Math.PI / 180.0);
            double arcLengthNm = (radiusFt * sweepRad) / GeoMath.FeetPerNm;

            bool sameTaxiway = edgeA.SharesTaxiway(edgeB);

            layout.Arcs.Add(
                new GroundArc
                {
                    Nodes = [tanNodeA, tanNodeB],
                    TaxiwayNames = sameTaxiway ? [edgeA.TaxiwayName] : [edgeA.TaxiwayName, edgeB.TaxiwayName],
                    CenterLat = centerLat,
                    CenterLon = centerLon,
                    RadiusFt = radiusFt,
                    DistanceNm = arcLengthNm,
                }
            );
            arcsCreated++;
        }

        // --- Phase D: Rebuild edges ---
        // Each original edge at this intersection is either:
        //   (a) Has a tangent point → shorten: otherNode ↔ tangentNode
        //   (b) Part of a collinear merge without tangent points → merge: otherA ↔ otherB
        //   (c) Neither (orphan) → reconnect to nearest tangent node

        var consumedEdges = new HashSet<GroundEdge>();

        // Shorten edges that have tangent points
        foreach (var (edge, tangentNode) in edgeTangentNodes)
        {
            var otherNode = edge.OtherNode(intersection);
            double newDist = GeoMath.DistanceNm(otherNode.Latitude, otherNode.Longitude, tangentNode.Latitude, tangentNode.Longitude);

            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [otherNode, tangentNode],
                    TaxiwayName = edge.TaxiwayName,
                    DistanceNm = newDist,
                }
            );
            consumedEdges.Add(edge);
        }

        // Merge collinear pairs
        int edgesMerged = 0;
        foreach (var (edgeA, otherA, edgeB, otherB) in plannedMerges)
        {
            bool aHasTangent = edgeTangentNodes.TryGetValue(edgeA, out var tanA);
            bool bHasTangent = edgeTangentNodes.TryGetValue(edgeB, out var tanB);

            // Determine the effective endpoints after shortening
            GroundNode endA = aHasTangent ? tanA! : otherA;
            GroundNode endB = bHasTangent ? tanB! : otherB;

            // Create the merged edge between the effective endpoints
            double mergedDist = GeoMath.DistanceNm(endA.Latitude, endA.Longitude, endB.Latitude, endB.Longitude);
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [endA, endB],
                    TaxiwayName = edgeA.TaxiwayName,
                    DistanceNm = mergedDist,
                }
            );

            consumedEdges.Add(edgeA);
            consumedEdges.Add(edgeB);
            edgesMerged++;
        }

        // Collect all candidate reconnection nodes: tangent points + merge endpoints
        var reconnectCandidates = new List<GroundNode>(edgeTangentNodes.Values);
        foreach (var (_, otherA, _, otherB) in plannedMerges)
        {
            reconnectCandidates.Add(otherA);
            reconnectCandidates.Add(otherB);
        }

        // Reconnect orphaned edges (e.g., parking edges to this intersection)
        foreach (var edge in edges)
        {
            if (consumedEdges.Contains(edge))
            {
                continue;
            }

            var otherNode = edge.OtherNode(intersection);
            GroundNode? bestTarget = FindNearestNode(otherNode, reconnectCandidates);
            if (bestTarget is not null)
            {
                double newDist = GeoMath.DistanceNm(otherNode.Latitude, otherNode.Longitude, bestTarget.Latitude, bestTarget.Longitude);
                layout.Edges.Add(
                    new GroundEdge
                    {
                        Nodes = [otherNode, bestTarget],
                        TaxiwayName = edge.TaxiwayName,
                        DistanceNm = newDist,
                    }
                );
            }
            else
            {
                Log.LogWarning(
                    "Fillet: orphaned edge {Taxiway} from node {NodeId} to filleted node {IntId} — no node to reconnect",
                    edge.TaxiwayName,
                    otherNode.Id,
                    intersection.Id
                );
            }
            consumedEdges.Add(edge);
        }

        // Remove all original edges at this intersection
        layout.Edges.RemoveAll(e => consumedEdges.Contains(e));

        // Remove the intersection node
        layout.Nodes.Remove(intersection.Id);

        return (true, arcsCreated, edgesMerged);
    }

    private static void RecordTangentPoint(
        Dictionary<GroundEdge, (double Lat, double Lon, double TangentDistNm)> specs,
        GroundEdge edge,
        GroundNode intersection,
        double bearing,
        double tangentDistNm
    )
    {
        // Keep the largest tangent distance (farthest from intersection) so the
        // largest arc radius is honored when the same edge participates in multiple pairs.
        if (specs.TryGetValue(edge, out var existing) && (existing.TangentDistNm >= tangentDistNm))
        {
            return;
        }

        var (lat, lon) = GeoMath.ProjectPointRaw(intersection.Latitude, intersection.Longitude, bearing, tangentDistNm);
        specs[edge] = (lat, lon, tangentDistNm);
    }

    private static GroundNode? FindNearestNode(GroundNode target, List<GroundNode> candidates)
    {
        GroundNode? best = null;
        double bestDist = double.MaxValue;
        foreach (var candidate in candidates)
        {
            double dist = GeoMath.DistanceNm(target.Latitude, target.Longitude, candidate.Latitude, candidate.Longitude);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = candidate;
            }
        }

        return best;
    }

    private static (double Lat, double Lon) ComputeArcCenter(GroundNode intersection, double bearingA, double bearingB, double radiusFt)
    {
        // The arc center is at distance R / cos(halfAngle) from the intersection,
        // along the bisector of the two edge bearings, on the inside of the turn.
        double bisector = ComputeInsideBisector(bearingA, bearingB);

        // The angle from the bisector to either edge
        double halfAngleDeg = GeoMath.AbsBearingDifference(bearingA, bisector);
        double halfAngleRad = halfAngleDeg * (Math.PI / 180.0);

        double centerDistFt = radiusFt / Math.Cos(halfAngleRad);
        double centerDistNm = centerDistFt / GeoMath.FeetPerNm;

        return GeoMath.ProjectPointRaw(intersection.Latitude, intersection.Longitude, bisector, centerDistNm);
    }

    /// <summary>
    /// Computes the bisector direction on the inside of the turn formed by two bearings
    /// (both going away from the intersection).
    /// </summary>
    private static double ComputeInsideBisector(double bearingA, double bearingB)
    {
        // The two bearings go away from the intersection along each edge.
        // The "inside" of the turn is between the two edges.
        // The bisector of the angle between them points to the inside.
        double a = bearingA * (Math.PI / 180.0);
        double b = bearingB * (Math.PI / 180.0);

        // Average the unit vectors
        double x = Math.Cos(a) + Math.Cos(b);
        double y = Math.Sin(a) + Math.Sin(b);

        double bisectorRad = Math.Atan2(y, x);
        double bisectorDeg = bisectorRad * (180.0 / Math.PI);
        if (bisectorDeg < 0)
        {
            bisectorDeg += 360.0;
        }

        return bisectorDeg;
    }

    /// <summary>
    /// Compute the turn angle between two edges meeting at a node.
    /// Both bearings go FROM the intersection TO the other node.
    /// The turn angle is the supplement of the angle between the bearings
    /// (i.e., 180° minus the angle between the outbound directions).
    /// Two edges going in opposite directions (180° apart) = 0° turn.
    /// Two edges going in the same direction (0° apart) = 180° turn (U-turn).
    /// </summary>
    private static double ComputeTurnAngle(double bearingA, double bearingB)
    {
        double diff = GeoMath.AbsBearingDifference(bearingA, bearingB);
        return 180.0 - diff;
    }

    /// <summary>
    /// Get the initial bearing from an intersection along an edge, accounting for
    /// intermediate points (use the first intermediate point if present).
    /// </summary>
    private static double InitialBearing(GroundNode intersection, GroundNode otherNode, GroundEdge edge)
    {
        if (edge.IntermediatePoints.Count > 0)
        {
            // Determine which end is the intersection
            if (edge.Nodes[0].Id == intersection.Id)
            {
                var pt = edge.IntermediatePoints[0];
                return GeoMath.BearingTo(intersection.Latitude, intersection.Longitude, pt.Lat, pt.Lon);
            }
            else
            {
                var pt = edge.IntermediatePoints[^1];
                return GeoMath.BearingTo(intersection.Latitude, intersection.Longitude, pt.Lat, pt.Lon);
            }
        }

        return GeoMath.BearingTo(intersection.Latitude, intersection.Longitude, otherNode.Latitude, otherNode.Longitude);
    }

    private static double SelectMaxRadius(GroundEdge edgeA, GroundEdge edgeB, double turnAngleDeg)
    {
        bool hasRunway = edgeA.IsRunway || edgeB.IsRunway;
        bool hasRamp = edgeA.IsRamp || edgeB.IsRamp;

        if (hasRamp)
        {
            return RampRadiusFt;
        }

        if (hasRunway && (turnAngleDeg <= 45.0))
        {
            return HighSpeedExitRadiusFt;
        }

        if (hasRunway)
        {
            return RunwayExitRadiusFt;
        }

        return DefaultRadiusFt;
    }
}
