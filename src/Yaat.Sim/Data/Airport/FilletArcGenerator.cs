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
        var intersections = new List<(GroundNode Node, bool PreserveNode)>();
        foreach (var node in layout.Nodes.Values)
        {
            if (IsEligibleForFilleting(node, out bool preserve))
            {
                intersections.Add((node, preserve));
            }
        }

        int filletedCount = 0;
        int arcCount = 0;
        int mergedCount = 0;

        foreach (var (node, preserveNode) in intersections)
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

            var result = FilletNode(layout, node, preserveNode, ref nextNodeId);
            if (result.Success)
            {
                filletedCount++;
                arcCount += result.ArcsCreated;
                mergedCount += result.EdgesMerged;
            }
        }

        // --- Global cleanup: merge coincident nodes across all fillet iterations ---
        // When adjacent intersections are both filleted, their tangent-point nodes on the
        // shared edge can end up at the same position. Merge them so the graph has no
        // zero-length edges between coincident nodes.
        int nodesMerged = MergeCoincidentNodes(layout);

        layout.RebuildAdjacencyLists();

        Log.LogInformation(
            "Fillet arcs: {FilletedNodes} nodes filleted, {Arcs} arcs created, {Merged} edges merged, {NodesMerged} coincident nodes merged",
            filletedCount,
            arcCount,
            mergedCount,
            nodesMerged
        );
    }

    private static bool IsEligibleForFilleting(GroundNode node)
    {
        return IsEligibleForFilleting(node, out _);
    }

    /// <summary>
    /// Check if a node is eligible for filleting. Sets <paramref name="preserveNode"/> to true
    /// for runway threshold nodes — arcs are created but the node itself is kept in the graph,
    /// connected to tangent points via stub edges. This allows aircraft rolling to the runway
    /// end to smoothly turn onto taxiways via arcs while keeping the threshold node reachable.
    /// </summary>
    private static bool IsEligibleForFilleting(GroundNode node, out bool preserveNode)
    {
        preserveNode = false;

        // Only fillet plain intersection nodes — not hold-shorts, parking, spots, helipads
        if (node.Type != GroundNodeType.TaxiwayIntersection)
        {
            return false;
        }

        if (node.Edges.Count < 2)
        {
            return false;
        }

        // Count runway and non-runway edges
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

        // Runway threshold: exactly 1 RWY edge + at least 1 taxiway edge.
        // Create arcs but preserve the node (connected via stub edges to tangent points).
        if ((runwayEdgeCount == 1) && (nonRunwayEdgeCount > 0))
        {
            preserveNode = true;
            return true;
        }

        // Pure runway endpoint with no taxiway connections — no turn to smooth
        if ((runwayEdgeCount == 1) && (nonRunwayEdgeCount == 0))
        {
            return false;
        }

        // Mid-centerline nodes (2+ RWY edges) are fine: the collinear RWY pair merges and
        // taxiway branches get arcs.
        return true;
    }

    private static (bool Success, int ArcsCreated, int EdgesMerged) FilletNode(
        AirportGroundLayout layout,
        GroundNode intersection,
        bool preserveNode,
        ref int nextNodeId
    )
    {
        // Collect edges as concrete GroundEdge — arcs from previous iterations are skipped.
        // Deduplicate: prior fillet iterations can create multiple edges to the same node
        // with the same taxiway name (e.g., two collinear merges involving the same edge).
        var edges = new List<GroundEdge>();
        var seenEdgeKeys = new HashSet<(int OtherNodeId, string TaxiwayName)>();
        foreach (var e in intersection.Edges)
        {
            if (e is GroundEdge ge)
            {
                var other = ge.OtherNode(intersection);
                if (seenEdgeKeys.Add((other.Id, ge.TaxiwayName)))
                {
                    edges.Add(ge);
                }
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
                    Log.LogDebug(
                        "[Int#{IntId}] Pair {A}(→{OtherA})/{B}(→{OtherB}): collinear (turn={Turn:F1}°)",
                        intersection.Id,
                        edgeA.TaxiwayName,
                        otherA.Id,
                        edgeB.TaxiwayName,
                        otherB.Id,
                        turnAngle
                    );
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

                    Log.LogDebug(
                        "[Int#{IntId}] Pair {A}(→{OtherA}, {ALenFt:F0}ft)/{B}(→{OtherB}, {BLenFt:F0}ft): "
                            + "turn={Turn:F1}° radius={R:F0}ft(maxFit={MaxFit:F0}, maxType={MaxType:F0}) tangentDist={TD:F0}ft",
                        intersection.Id,
                        edgeA.TaxiwayName,
                        otherA.Id,
                        edgeALenFt,
                        edgeB.TaxiwayName,
                        otherB.Id,
                        edgeBLenFt,
                        turnAngle,
                        radiusFt,
                        maxFitRadiusFt,
                        maxRadiusFt,
                        tangentDistFt
                    );

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
            var otherNode = edge.OtherNode(intersection);
            var node = new GroundNode
            {
                Id = id,
                Latitude = lat,
                Longitude = lon,
                Type = GroundNodeType.TaxiwayIntersection,
                SourceIntersectionPosition = (intersection.Latitude, intersection.Longitude),
                Origin = $"Fillet:tangent-node@{intersection.Id} on-{edge.TaxiwayName}(→{otherNode.Id})",
            };
            layout.Nodes[id] = node;
            edgeTangentNodes[edge] = node;

            Log.LogDebug(
                "[Int#{IntId}] Phase B: tangent node #{NodeId} on {Tw}(→{Other}) at {Dist:F0}ft from intersection",
                intersection.Id,
                id,
                edge.TaxiwayName,
                otherNode.Id,
                edgeTangentSpecs[edge].TangentDistNm * GeoMath.FeetPerNm
            );
        }

        // --- Phase B': Merge coincident tangent-point nodes ---
        // Complex intersections (5+ edges) can produce multiple tangent points at
        // nearly the same position — typically when edges run close together or
        // a prior fillet iteration shortened an edge. Merge any within threshold
        // so arcs and edges reference a single node instead of coincident duplicates.
        MergeCoincidentTangentNodes(layout, edgeTangentNodes, intersection.Id);

        // --- Phase C: Create bezier arcs between tangent-point pairs ---
        int arcsCreated = 0;
        foreach (var (edgeA, edgeB, radiusFt, turnAngleDeg) in plannedArcs)
        {
            if (!edgeTangentNodes.TryGetValue(edgeA, out var tanNodeA) || !edgeTangentNodes.TryGetValue(edgeB, out var tanNodeB))
            {
                continue;
            }

            // After merging coincident nodes, both edges may point to the same tangent node.
            // No arc needed — the path goes straight through the shared node.
            if (tanNodeA.Id == tanNodeB.Id)
            {
                continue;
            }

            int idxA = edgeBearings.FindIndex(x => x.Edge == edgeA);
            int idxB = edgeBearings.FindIndex(x => x.Edge == edgeB);
            double bearingA = edgeBearings[idxA].Bearing;
            double bearingB = edgeBearings[idxB].Bearing;

            // Bezier control point depth: κ = (4/3) * tan(sweep/4)
            double sweepRad = (180.0 - turnAngleDeg) * (Math.PI / 180.0);
            double kappa = (4.0 / 3.0) * Math.Tan(sweepRad / 4.0);

            double depthA = kappa * edgeTangentSpecs[edgeA].TangentDistNm;
            double depthB = kappa * edgeTangentSpecs[edgeB].TangentDistNm;

            // P1: from tanNodeA toward intersection (reverse of edge bearing from intersection)
            double bearingAToIntersection = (bearingA + 180.0) % 360.0;
            var (p1Lat, p1Lon) = GeoMath.ProjectPointRaw(tanNodeA.Latitude, tanNodeA.Longitude, bearingAToIntersection, depthA);

            // P2: from tanNodeB toward intersection
            double bearingBToIntersection = (bearingB + 180.0) % 360.0;
            var (p2Lat, p2Lon) = GeoMath.ProjectPointRaw(tanNodeB.Latitude, tanNodeB.Longitude, bearingBToIntersection, depthB);

            var bezier = new CubicBezier(tanNodeA.Latitude, tanNodeA.Longitude, p1Lat, p1Lon, p2Lat, p2Lon, tanNodeB.Latitude, tanNodeB.Longitude);

            double minRadiusFt = bezier.MinRadiusOfCurvatureFt(tanNodeA.Latitude, 10);
            double arcLengthNm = bezier.ArcLengthNm(20);

            bool sameTaxiway = edgeA.SharesTaxiway(edgeB);

            layout.Arcs.Add(
                new GroundArc
                {
                    Nodes = [tanNodeA, tanNodeB],
                    TaxiwayNames = sameTaxiway ? [edgeA.TaxiwayName] : [edgeA.TaxiwayName, edgeB.TaxiwayName],
                    P1Lat = p1Lat,
                    P1Lon = p1Lon,
                    P2Lat = p2Lat,
                    P2Lon = p2Lon,
                    MinRadiusOfCurvatureFt = minRadiusFt,
                    DistanceNm = arcLengthNm,
                    EdgeBearingAtNode0Deg = bearingA,
                    EdgeBearingAtNode1Deg = bearingB,
                    TurnAngleDeg = turnAngleDeg,
                    Origin = $"Fillet:phase-c-arc@{intersection.Id} {edgeA.TaxiwayName}/{edgeB.TaxiwayName}",
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

            // After merging, the tangent node might be the same as the other endpoint
            // (when the tangent consumed the entire edge). Skip — no edge needed.
            if (otherNode.Id == tangentNode.Id)
            {
                consumedEdges.Add(edge);
                continue;
            }

            double newDist = GeoMath.DistanceNm(otherNode.Latitude, otherNode.Longitude, tangentNode.Latitude, tangentNode.Longitude);

            if (newDist * GeoMath.FeetPerNm < 1.0)
            {
                Log.LogDebug(
                    "Fillet: near-zero shortened edge {Taxiway} ({NodeA}->{NodeB}) is {DistFt:F1}ft at intersection {IntId}",
                    edge.TaxiwayName,
                    otherNode.Id,
                    tangentNode.Id,
                    newDist * GeoMath.FeetPerNm,
                    intersection.Id
                );
            }

            Log.LogDebug(
                "[Int#{IntId}] Phase D shorten: {Tw} #{Other}↔#{Tan} ({DistFt:F0}ft)",
                intersection.Id,
                edge.TaxiwayName,
                otherNode.Id,
                tangentNode.Id,
                newDist * GeoMath.FeetPerNm
            );

            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [otherNode, tangentNode],
                    TaxiwayName = edge.TaxiwayName,
                    DistanceNm = newDist,
                    Origin = $"Fillet:phase-d-shorten@{intersection.Id} {edge.TaxiwayName} #{otherNode.Id}↔#{tangentNode.Id}",
                }
            );
            consumedEdges.Add(edge);
        }

        // Merge collinear pairs — track which edges have been used in a merge to avoid
        // creating duplicate edges when one edge participates in multiple collinear pairs
        // (e.g., W collinear with both W1 and W2).
        int edgesMerged = 0;
        var mergedEdges = new HashSet<GroundEdge>();
        foreach (var (edgeA, otherA, edgeB, otherB) in plannedMerges)
        {
            if (mergedEdges.Contains(edgeA) || mergedEdges.Contains(edgeB))
            {
                continue;
            }

            bool aHasTangent = edgeTangentNodes.TryGetValue(edgeA, out var tanA);
            bool bHasTangent = edgeTangentNodes.TryGetValue(edgeB, out var tanB);

            // Determine the effective endpoints after shortening
            GroundNode endA = aHasTangent ? tanA! : otherA;
            GroundNode endB = bHasTangent ? tanB! : otherB;

            // Create the merged edge between the effective endpoints
            double mergedDist = GeoMath.DistanceNm(endA.Latitude, endA.Longitude, endB.Latitude, endB.Longitude);

            Log.LogDebug(
                "[Int#{IntId}] Phase D collinear merge: {Tw} #{EndA}↔#{EndB} ({DistFt:F0}ft) [tangentA={HasA}, tangentB={HasB}]",
                intersection.Id,
                edgeA.TaxiwayName,
                endA.Id,
                endB.Id,
                mergedDist * GeoMath.FeetPerNm,
                aHasTangent,
                bHasTangent
            );

            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [endA, endB],
                    TaxiwayName = edgeA.TaxiwayName,
                    DistanceNm = mergedDist,
                    Origin = $"Fillet:phase-d-merge@{intersection.Id} {edgeA.TaxiwayName} #{endA.Id}↔#{endB.Id}",
                }
            );

            consumedEdges.Add(edgeA);
            consumedEdges.Add(edgeB);
            mergedEdges.Add(edgeA);
            mergedEdges.Add(edgeB);
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
                        Origin = $"Fillet:phase-d-reconnect@{intersection.Id} {edge.TaxiwayName}",
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

        if (preserveNode)
        {
            // Threshold fillet: keep the intersection node, connect it to tangent points via stub edges.
            // This keeps the threshold reachable while arcs provide smooth turn geometry.
            foreach (var (edge, tangentNode) in edgeTangentNodes)
            {
                double stubDist = GeoMath.DistanceNm(intersection.Latitude, intersection.Longitude, tangentNode.Latitude, tangentNode.Longitude);
                layout.Edges.Add(
                    new GroundEdge
                    {
                        Nodes = [intersection, tangentNode],
                        TaxiwayName = edge.TaxiwayName,
                        DistanceNm = stubDist,
                        Origin = $"Fillet:phase-d-preserve@{intersection.Id} {edge.TaxiwayName}",
                    }
                );
            }

            // Also add stubs to collinear merge endpoints that don't have tangent points,
            // so the intersection stays connected through merged collinear paths.
            foreach (var (edgeA, otherA, edgeB, otherB) in plannedMerges)
            {
                bool aHasTangent = edgeTangentNodes.ContainsKey(edgeA);
                bool bHasTangent = edgeTangentNodes.ContainsKey(edgeB);
                if (!aHasTangent)
                {
                    double dist = GeoMath.DistanceNm(intersection.Latitude, intersection.Longitude, otherA.Latitude, otherA.Longitude);
                    layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [intersection, otherA],
                            TaxiwayName = edgeA.TaxiwayName,
                            DistanceNm = dist,
                            Origin = $"Fillet:phase-d-preserve@{intersection.Id} {edgeA.TaxiwayName}",
                        }
                    );
                }

                if (!bHasTangent)
                {
                    double dist = GeoMath.DistanceNm(intersection.Latitude, intersection.Longitude, otherB.Latitude, otherB.Longitude);
                    layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [intersection, otherB],
                            TaxiwayName = edgeB.TaxiwayName,
                            DistanceNm = dist,
                            Origin = $"Fillet:phase-d-preserve@{intersection.Id} {edgeB.TaxiwayName}",
                        }
                    );
                }
            }
        }
        else
        {
            // Standard fillet: remove the intersection node entirely
            layout.Nodes.Remove(intersection.Id);
        }

        return (true, arcsCreated, edgesMerged);
    }

    /// <summary>
    /// Global post-fillet pass: iteratively merge coincident TaxiwayIntersection nodes
    /// within 5ft. Adjusts bezier control points so curves stay smooth when endpoints
    /// move. Loops until no more merges occur (handles transitive chains).
    /// </summary>
    private static int MergeCoincidentNodes(AirportGroundLayout layout)
    {
        const double thresholdNm = 5.0 / GeoMath.FeetPerNm;
        int totalMerged = 0;

        for (int pass = 0; pass < 5; pass++)
        {
            var mergeMap = BuildMergeMap(layout, thresholdNm);
            if (mergeMap.Count == 0)
            {
                break;
            }

            Log.LogDebug("GlobalMerge pass {Pass}: {Count} merges", pass, mergeMap.Count);

            foreach (var (victimId, survivor) in mergeMap)
            {
                double distFt =
                    GeoMath.DistanceNm(layout.Nodes[victimId].Latitude, layout.Nodes[victimId].Longitude, survivor.Latitude, survivor.Longitude)
                    * GeoMath.FeetPerNm;
                Log.LogDebug(
                    "  GlobalMerge: #{Victim}→#{Survivor} ({DistFt:F1}ft apart) survivor-origin={Origin}",
                    victimId,
                    survivor.Id,
                    distFt,
                    survivor.Origin
                );
            }

            // Rewrite edge node references
            foreach (var edge in layout.Edges)
            {
                for (int k = 0; k < edge.Nodes.Length; k++)
                {
                    if (mergeMap.TryGetValue(edge.Nodes[k].Id, out var survivor))
                    {
                        edge.Nodes[k] = survivor;
                    }
                }
            }

            // Rewrite arc node references with bezier control point adjustment
            foreach (var arc in layout.Arcs)
            {
                for (int k = 0; k < arc.Nodes.Length; k++)
                {
                    if (mergeMap.TryGetValue(arc.Nodes[k].Id, out var survivor))
                    {
                        var victim = arc.Nodes[k];
                        double dLat = survivor.Latitude - victim.Latitude;
                        double dLon = survivor.Longitude - victim.Longitude;

                        // Translate the corresponding control point to preserve the
                        // tangent handle vector (P1-P0 or P2-P3) exactly.
                        if (k == 0)
                        {
                            arc.P1Lat += dLat;
                            arc.P1Lon += dLon;
                        }
                        else
                        {
                            arc.P2Lat += dLat;
                            arc.P2Lon += dLon;
                        }

                        arc.Origin += $" +merge({victim.Id}->{survivor.Id})";
                        arc.Nodes[k] = survivor;
                    }
                }
            }

            // Remove self-loop edges (both endpoints are the same node after merge)
            layout.Edges.RemoveAll(e => e.Nodes[0].Id == e.Nodes[1].Id);

            // Remove degenerate arcs (both endpoints are the same node after merge)
            layout.Arcs.RemoveAll(a => a.Nodes[0].Id == a.Nodes[1].Id);

            // Remove duplicate edges and arcs (same two nodes after merge)
            RemoveDuplicateEdges(layout);
            RemoveDuplicateArcs(layout);

            // Remove arcs that duplicate an existing straight edge (same endpoints, shared taxiway)
            RemoveRedundantArcs(layout);

            // Remove victim nodes from layout
            foreach (int victimId in mergeMap.Keys)
            {
                layout.Nodes.Remove(victimId);
            }

            totalMerged += mergeMap.Count;
        }

        // Final pass: recompute cached distances for all edges and arcs so they
        // reflect current node positions after any merges.
        if (totalMerged > 0)
        {
            RecomputeDistances(layout);
        }

        return totalMerged;
    }

    /// <summary>
    /// Recompute cached DistanceNm for all edges and MinRadiusOfCurvatureFt/DistanceNm
    /// for all arcs from their current node positions and control points.
    /// </summary>
    private static void RecomputeDistances(AirportGroundLayout layout)
    {
        foreach (var edge in layout.Edges)
        {
            edge.DistanceNm = GeoMath.DistanceNm(edge.Nodes[0].Latitude, edge.Nodes[0].Longitude, edge.Nodes[1].Latitude, edge.Nodes[1].Longitude);
        }

        foreach (var arc in layout.Arcs)
        {
            var bezier = arc.ToBezier();
            arc.MinRadiusOfCurvatureFt = bezier.MinRadiusOfCurvatureFt(arc.Nodes[0].Latitude, 10);
            arc.DistanceNm = bezier.ArcLengthNm(20);
        }
    }

    /// <summary>
    /// Build a merge map of coincident TaxiwayIntersection node pairs within the given threshold.
    /// Returns victimId → survivorNode mapping. Later nodes in the candidate list are victims.
    /// </summary>
    private static Dictionary<int, GroundNode> BuildMergeMap(AirportGroundLayout layout, double thresholdNm)
    {
        var candidates = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.TaxiwayIntersection).ToList();
        var mergeMap = new Dictionary<int, GroundNode>();

        for (int i = 0; i < candidates.Count; i++)
        {
            if (mergeMap.ContainsKey(candidates[i].Id))
            {
                continue;
            }

            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (mergeMap.ContainsKey(candidates[j].Id))
                {
                    continue;
                }

                double dist = GeoMath.DistanceNm(candidates[i].Latitude, candidates[i].Longitude, candidates[j].Latitude, candidates[j].Longitude);

                if (dist <= thresholdNm)
                {
                    Log.LogDebug(
                        "Fillet cleanup: merging node {VictimId} into {SurvivorId} ({DistFt:F1}ft apart)",
                        candidates[j].Id,
                        candidates[i].Id,
                        dist * GeoMath.FeetPerNm
                    );
                    mergeMap[candidates[j].Id] = candidates[i];
                }
            }
        }

        return mergeMap;
    }

    private static void RemoveDuplicateEdges(AirportGroundLayout layout)
    {
        var seen = new HashSet<(int, int, string)>();
        var toRemove = new List<GroundEdge>();

        foreach (var edge in layout.Edges)
        {
            int a = Math.Min(edge.Nodes[0].Id, edge.Nodes[1].Id);
            int b = Math.Max(edge.Nodes[0].Id, edge.Nodes[1].Id);
            var key = (a, b, edge.TaxiwayName);
            if (!seen.Add(key))
            {
                toRemove.Add(edge);
            }
        }

        foreach (var edge in toRemove)
        {
            layout.Edges.Remove(edge);
        }
    }

    private static void RemoveDuplicateArcs(AirportGroundLayout layout)
    {
        var seen = new HashSet<(int, int, string)>();
        var toRemove = new List<GroundArc>();

        foreach (var arc in layout.Arcs)
        {
            int a = Math.Min(arc.Nodes[0].Id, arc.Nodes[1].Id);
            int b = Math.Max(arc.Nodes[0].Id, arc.Nodes[1].Id);
            var key = (a, b, arc.TaxiwayName);
            if (!seen.Add(key))
            {
                toRemove.Add(arc);
            }
        }

        foreach (var arc in toRemove)
        {
            layout.Arcs.Remove(arc);
        }
    }

    private static void RemoveRedundantArcs(AirportGroundLayout layout)
    {
        // Build set of straight edge endpoints
        var edgeKeys = new HashSet<(int, int, string)>();
        foreach (var edge in layout.Edges)
        {
            int a = Math.Min(edge.Nodes[0].Id, edge.Nodes[1].Id);
            int b = Math.Max(edge.Nodes[0].Id, edge.Nodes[1].Id);
            edgeKeys.Add((a, b, edge.TaxiwayName));
        }

        // Remove arcs whose endpoints + any taxiway name match a straight edge
        layout.Arcs.RemoveAll(arc =>
        {
            int a = Math.Min(arc.Nodes[0].Id, arc.Nodes[1].Id);
            int b = Math.Max(arc.Nodes[0].Id, arc.Nodes[1].Id);
            foreach (string name in arc.TaxiwayNames)
            {
                if (edgeKeys.Contains((a, b, name)))
                {
                    return true;
                }
            }

            return false;
        });
    }

    /// <summary>
    /// Merge tangent-point nodes that are within 5ft of each other within a single
    /// FilletNode call. Keeps the first node in each cluster; redirects edgeTangentNodes
    /// references from victims to the survivor. Removes victim nodes from the layout.
    /// </summary>
    private static void MergeCoincidentTangentNodes(
        AirportGroundLayout layout,
        Dictionary<GroundEdge, GroundNode> edgeTangentNodes,
        int intersectionId
    )
    {
        const double thresholdNm = 5.0 / GeoMath.FeetPerNm;

        // Build list of unique tangent nodes
        var tangentNodes = edgeTangentNodes.Values.Distinct().ToList();
        if (tangentNodes.Count < 2)
        {
            return;
        }

        // Map from victim node → survivor node
        var mergeMap = new Dictionary<int, GroundNode>();

        for (int i = 0; i < tangentNodes.Count; i++)
        {
            // Skip if this node was already merged into something else
            if (mergeMap.ContainsKey(tangentNodes[i].Id))
            {
                continue;
            }

            for (int j = i + 1; j < tangentNodes.Count; j++)
            {
                if (mergeMap.ContainsKey(tangentNodes[j].Id))
                {
                    continue;
                }

                double dist = GeoMath.DistanceNm(
                    tangentNodes[i].Latitude,
                    tangentNodes[i].Longitude,
                    tangentNodes[j].Latitude,
                    tangentNodes[j].Longitude
                );

                if (dist <= thresholdNm)
                {
                    double distFt = dist * GeoMath.FeetPerNm;
                    Log.LogDebug(
                        "Fillet: merging coincident tangent nodes {VictimId} into {SurvivorId} ({DistFt:F1}ft apart) at intersection {IntId}",
                        tangentNodes[j].Id,
                        tangentNodes[i].Id,
                        distFt,
                        intersectionId
                    );
                    mergeMap[tangentNodes[j].Id] = tangentNodes[i];
                }
            }
        }

        if (mergeMap.Count == 0)
        {
            return;
        }

        // Update edgeTangentNodes: redirect victim references to survivors
        foreach (var edge in edgeTangentNodes.Keys.ToList())
        {
            var node = edgeTangentNodes[edge];
            if (mergeMap.TryGetValue(node.Id, out var survivor))
            {
                edgeTangentNodes[edge] = survivor;
            }
        }

        // Remove victim nodes from the layout
        foreach (int victimId in mergeMap.Keys)
        {
            layout.Nodes.Remove(victimId);
        }
    }

    private static void RecordTangentPoint(
        Dictionary<GroundEdge, (double Lat, double Lon, double TangentDistNm)> specs,
        GroundEdge edge,
        GroundNode intersection,
        double bearing,
        double tangentDistNm
    )
    {
        double tangentDistFt = tangentDistNm * GeoMath.FeetPerNm;

        // Keep the largest tangent distance (farthest from intersection) so the
        // largest arc radius is honored when the same edge participates in multiple pairs.
        if (specs.TryGetValue(edge, out var existing) && (existing.TangentDistNm >= tangentDistNm))
        {
            Log.LogDebug(
                "[Int#{IntId}]   TangentPoint on {Tw}(→{Other}): KEEP existing {ExFt:F0}ft > new {NewFt:F0}ft",
                intersection.Id,
                edge.TaxiwayName,
                edge.OtherNode(intersection).Id,
                existing.TangentDistNm * GeoMath.FeetPerNm,
                tangentDistFt
            );
            return;
        }

        bool replaced = specs.ContainsKey(edge);
        var (lat, lon) = GeoMath.ProjectPointRaw(intersection.Latitude, intersection.Longitude, bearing, tangentDistNm);
        specs[edge] = (lat, lon, tangentDistNm);

        Log.LogDebug(
            "[Int#{IntId}]   TangentPoint on {Tw}(→{Other}): {Action} at {Dist:F0}ft, bearing={Brg:F1}°",
            intersection.Id,
            edge.TaxiwayName,
            edge.OtherNode(intersection).Id,
            replaced ? "REPLACE" : "SET",
            tangentDistFt,
            bearing
        );
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
        bool hasRunway = edgeA.IsRunwayCenterline || edgeB.IsRunwayCenterline;
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
