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

        // Pre-pass: detect pre-existing manual arcs (chains of shape-point nodes
        // forming smooth curves) and exclude them from filleting.
        var manualArcNodes = DetectManualArcNodes(layout);
        if (manualArcNodes.Count > 0)
        {
            Log.LogDebug("Excluding {Count} nodes in pre-existing manual arc chains from filleting", manualArcNodes.Count);
        }

        // Snapshot the intersection nodes to process — we'll be mutating the graph.
        var intersections = new List<(GroundNode Node, bool PreserveNode)>();
        foreach (var node in layout.Nodes.Values)
        {
            if (manualArcNodes.Contains(node.Id))
            {
                continue;
            }

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

            var result = FilletNode(layout, node, preserveNode, manualArcNodes, ref nextNodeId);
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

        // Shape-point nodes: exactly 2 non-runway edges on the same taxiway.
        // These exist to add curvature to the GeoJSON geometry and are not real
        // intersections. Filleting them destroys the original taxiway geometry.
        if ((runwayEdgeCount == 0) && (nonRunwayEdgeCount == 2))
        {
            var edges = node.Edges.OfType<GroundEdge>().ToList();
            if ((edges.Count == 2) && (edges[0].TaxiwayName == edges[1].TaxiwayName))
            {
                return false;
            }
        }

        // Mid-centerline nodes (2+ RWY edges) are fine: the collinear RWY pair merges and
        // taxiway branches get arcs.
        return true;
    }

    private static (bool Success, int ArcsCreated, int EdgesMerged) FilletNode(
        AirportGroundLayout layout,
        GroundNode intersection,
        bool preserveNode,
        HashSet<int> manualArcNodes,
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
        // Each pair computes its own tangent positions independently. An edge can have
        // multiple tangent nodes when different pairs need different distances (e.g., a
        // genuine 90° turn vs a near-collinear 176° pair). Coincident positions on the
        // same edge are deduplicated in GetOrCreateTangentNode.

        // Pre-compute taxiway walks per edge: how far along the taxiway chain we can
        // extend if the first edge is too short for the desired tangent distance.
        var edgeWalks = new Dictionary<GroundEdge, TaxiwayWalkResult>();
        foreach (var (edge, _, _) in edgeBearings)
        {
            edgeWalks[edge] = WalkTaxiway(edge, intersection, manualArcNodes);
        }

        // Per-pair tangent placements: each arc pair computes its own tangent positions
        // independently, so a near-collinear pair's large tangent distance doesn't corrupt
        // other pairs' arcs via max-wins. Pairs that want the same distance on a shared
        // edge will share a tangent node (deduplicated in Phase B).
        var plannedArcs =
            new List<(
                GroundEdge EdgeA,
                GroundEdge EdgeB,
                double RadiusFt,
                double TurnAngleDeg,
                TangentPlacement PlacementA,
                TangentPlacement PlacementB
            )>();
        var plannedMerges = new List<(GroundEdge EdgeA, GroundNode OtherA, GroundEdge EdgeB, GroundNode OtherB)>();

        for (int i = 0; i < edgeBearings.Count; i++)
        {
            for (int j = i + 1; j < edgeBearings.Count; j++)
            {
                var (edgeA, otherA, bearingA) = edgeBearings[i];
                var (edgeB, otherB, bearingB) = edgeBearings[j];

                // Skip pairs where both edges go to the same node — they're overlapping
                // edges (e.g., B and B5 sharing the same physical segment), not a real turn.
                if (otherA.Id == otherB.Id)
                {
                    continue;
                }

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

                    var walkA = edgeWalks[edgeA];
                    var walkB = edgeWalks[edgeB];
                    double availableAFt = walkA.AvailableLengthFt;
                    double availableBFt = walkB.AvailableLengthFt;

                    bool capA = IsEligibleForFilleting(walkA.TerminalNode) && (walkA.TerminalNode.SourceIntersectionPosition is null);
                    bool capB = IsEligibleForFilleting(walkB.TerminalNode) && (walkB.TerminalNode.SourceIntersectionPosition is null);
                    double maxTangentAFt = capA ? availableAFt / 2.0 : availableAFt;
                    double maxTangentBFt = capB ? availableBFt / 2.0 : availableBFt;

                    double maxFitRadiusFt = Math.Min(maxTangentAFt, maxTangentBFt) / tanHalf;
                    double maxRadiusFt = SelectMaxRadius(edgeA, edgeB, turnAngle);
                    double radiusFt = Math.Min(maxFitRadiusFt, maxRadiusFt);

                    double tangentDistFt = radiusFt * tanHalf;
                    double tangentDistNm = tangentDistFt / GeoMath.FeetPerNm;

                    Log.LogDebug(
                        "[Int#{IntId}] Pair {A}(→{OtherA}, avail={AAvail:F0}ft)/{B}(→{OtherB}, avail={BAvail:F0}ft): "
                            + "turn={Turn:F1}° radius={R:F0}ft(maxFit={MaxFit:F0}, maxType={MaxType:F0}) tangentDist={TD:F0}ft",
                        intersection.Id,
                        edgeA.TaxiwayName,
                        otherA.Id,
                        availableAFt,
                        edgeB.TaxiwayName,
                        otherB.Id,
                        availableBFt,
                        turnAngle,
                        radiusFt,
                        maxFitRadiusFt,
                        maxRadiusFt,
                        tangentDistFt
                    );

                    var placementA = ComputeTangentPlacement(edgeA, intersection, bearingA, tangentDistNm, walkA);
                    var placementB = ComputeTangentPlacement(edgeB, intersection, bearingB, tangentDistNm, walkB);

                    plannedArcs.Add((edgeA, edgeB, radiusFt, turnAngle, placementA, placementB));
                }
            }
        }

        if ((plannedArcs.Count + plannedMerges.Count) == 0)
        {
            return (false, 0, 0);
        }

        // Preserve the intersection node when collinear pairs exist. The straight-through
        // paths need the center node so each side keeps its correct taxiway name
        // (e.g., W3 south of W, U north of W). Stubs from tangent nodes to the center
        // replace the old collinear merge that lost name boundaries.
        if (plannedMerges.Count > 0)
        {
            preserveNode = true;
        }

        // --- Phase B + C: Create tangent nodes and arcs per pair ---
        // Each pair creates its own tangent nodes. Coincident tangent nodes on the same
        // edge (when multiple pairs want the same distance) are deduplicated by position.
        int arcsCreated = 0;
        var edgeTangentNodes = new Dictionary<GroundEdge, List<(GroundNode Node, TangentPlacement Placement)>>();

        foreach (var (edgeA, edgeB, radiusFt, turnAngleDeg, placementA, placementB) in plannedArcs)
        {
            var tanNodeA = GetOrCreateTangentNode(layout, edgeTangentNodes, edgeA, placementA, intersection, ref nextNodeId);
            var tanNodeB = GetOrCreateTangentNode(layout, edgeTangentNodes, edgeB, placementB, intersection, ref nextNodeId);

            if (tanNodeA.Id == tanNodeB.Id)
            {
                continue;
            }

            int idxA = edgeBearings.FindIndex(x => x.Edge == edgeA);
            int idxB = edgeBearings.FindIndex(x => x.Edge == edgeB);
            double bearingA = edgeBearings[idxA].Bearing;
            double bearingB = edgeBearings[idxB].Bearing;

            double bearingAToIntersection = placementA.BearingTowardIntersectionDeg ?? (bearingA + 180.0) % 360.0;
            double bearingBToIntersection = placementB.BearingTowardIntersectionDeg ?? (bearingB + 180.0) % 360.0;

            double effectiveTurnDeg = 180.0 - GeoMath.AbsBearingDifference(bearingAToIntersection, bearingBToIntersection);

            double sweepRad = (180.0 - effectiveTurnDeg) * (Math.PI / 180.0);
            double kappa = (4.0 / 3.0) * Math.Tan(sweepRad / 4.0);

            double depthA = kappa * placementA.TangentDistNm;
            double depthB = kappa * placementB.TangentDistNm;

            var (p1Lat, p1Lon) = GeoMath.ProjectPointRaw(tanNodeA.Latitude, tanNodeA.Longitude, bearingAToIntersection, depthA);
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
                    TurnAngleDeg = effectiveTurnDeg,
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
        var deferredShapeNodes = new List<GroundNode>();

        // Shorten edges that have tangent points. Per-pair tangents mean an edge can
        // have multiple tangent nodes at different distances. Sort by distance (farthest
        // first = nearest to the far end), connect them with edge segments, and shorten
        // the original edge to the farthest tangent.
        foreach (var (edge, tangentEntries) in edgeTangentNodes)
        {
            var otherNode = edge.OtherNode(intersection);

            // Sort tangent nodes on this edge by distance from intersection (farthest first)
            var sorted = tangentEntries.OrderByDescending(t => t.Placement.TangentDistNm).ToList();

            // Consume walked-through edges from the farthest tangent (it walks the most)
            var farthest = sorted[0];

            if (farthest.Placement.LandsInManualArc)
            {
                // Manual arc tangent: the chain edges stay intact. Only split the edge
                // where the tangent node lands — remove it and replace with two sub-edges.
                var splitEdge = farthest.Placement.SplitEdge;
                if (splitEdge is not null)
                {
                    var splitNodeA = splitEdge.Nodes[0];
                    var splitNodeB = splitEdge.Nodes[1];
                    consumedEdges.Add(splitEdge);

                    double distA = GeoMath.DistanceNm(splitNodeA.Latitude, splitNodeA.Longitude, farthest.Node.Latitude, farthest.Node.Longitude);
                    layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [splitNodeA, farthest.Node],
                            TaxiwayName = edge.TaxiwayName,
                            DistanceNm = distA,
                            Origin = $"Fillet:phase-d-arc-split@{intersection.Id} {edge.TaxiwayName} #{splitNodeA.Id}↔#{farthest.Node.Id}",
                        }
                    );
                    double distB = GeoMath.DistanceNm(farthest.Node.Latitude, farthest.Node.Longitude, splitNodeB.Latitude, splitNodeB.Longitude);
                    layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [farthest.Node, splitNodeB],
                            TaxiwayName = edge.TaxiwayName,
                            DistanceNm = distB,
                            Origin = $"Fillet:phase-d-arc-split@{intersection.Id} {edge.TaxiwayName} #{farthest.Node.Id}↔#{splitNodeB.Id}",
                        }
                    );
                }
                // Also consume any non-manual-arc walked edges before the chain
                foreach (var walkedEdge in farthest.Placement.WalkedEdges)
                {
                    consumedEdges.Add(walkedEdge);
                }
                deferredShapeNodes.AddRange(farthest.Placement.WalkedShapeNodes);
                foreach (var ptNode in farthest.Placement.PassthroughNodes)
                {
                    double ptToTanDist = GeoMath.DistanceNm(ptNode.Latitude, ptNode.Longitude, farthest.Node.Latitude, farthest.Node.Longitude);
                    layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [ptNode, farthest.Node],
                            TaxiwayName = edge.TaxiwayName,
                            DistanceNm = ptToTanDist,
                            Origin = $"Fillet:phase-d-passthrough@{intersection.Id} {edge.TaxiwayName} #{ptNode.Id}↔#{farthest.Node.Id}",
                        }
                    );
                }

                // When farthest is in a manual arc but there are nearer tangents on the
                // first edge (before the chain), create a shorten edge from otherNode to
                // the nearest non-manual-arc tangent so the first edge isn't left dangling.
                if (sorted.Count > 1)
                {
                    var nearest = sorted[^1];
                    if (!nearest.Placement.LandsInManualArc)
                    {
                        double shortenDist = GeoMath.DistanceNm(
                            otherNode.Latitude,
                            otherNode.Longitude,
                            nearest.Node.Latitude,
                            nearest.Node.Longitude
                        );
                        layout.Edges.Add(
                            new GroundEdge
                            {
                                Nodes = [otherNode, nearest.Node],
                                TaxiwayName = edge.TaxiwayName,
                                DistanceNm = shortenDist,
                                Origin = $"Fillet:phase-d-shorten@{intersection.Id} {edge.TaxiwayName} #{otherNode.Id}↔#{nearest.Node.Id}",
                            }
                        );
                    }
                }
            }
            else
            {
                // Standard walk: consume edges, remove shape nodes, reconnect passthrough
                foreach (var walkedEdge in farthest.Placement.WalkedEdges)
                {
                    consumedEdges.Add(walkedEdge);
                }
                deferredShapeNodes.AddRange(farthest.Placement.WalkedShapeNodes);

                var farNode = farthest.Placement.WalkFarNode ?? otherNode;
                foreach (var ptNode in farthest.Placement.PassthroughNodes)
                {
                    double ptToTanDist = GeoMath.DistanceNm(ptNode.Latitude, ptNode.Longitude, farthest.Node.Latitude, farthest.Node.Longitude);
                    layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [ptNode, farthest.Node],
                            TaxiwayName = edge.TaxiwayName,
                            DistanceNm = ptToTanDist,
                            Origin = $"Fillet:phase-d-passthrough@{intersection.Id} {edge.TaxiwayName} #{ptNode.Id}↔#{farthest.Node.Id}",
                        }
                    );
                    double ptToFarDist = GeoMath.DistanceNm(ptNode.Latitude, ptNode.Longitude, farNode.Latitude, farNode.Longitude);
                    layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [ptNode, farNode],
                            TaxiwayName = edge.TaxiwayName,
                            DistanceNm = ptToFarDist,
                            Origin = $"Fillet:phase-d-passthrough@{intersection.Id} {edge.TaxiwayName} #{ptNode.Id}↔#{farNode.Id}",
                        }
                    );
                }

                // Shortened edge: farNode ↔ farthest tangent
                if (farNode.Id != farthest.Node.Id)
                {
                    double shortenDist = GeoMath.DistanceNm(farNode.Latitude, farNode.Longitude, farthest.Node.Latitude, farthest.Node.Longitude);
                    layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [farNode, farthest.Node],
                            TaxiwayName = edge.TaxiwayName,
                            DistanceNm = shortenDist,
                            Origin = $"Fillet:phase-d-shorten@{intersection.Id} {edge.TaxiwayName} #{farNode.Id}↔#{farthest.Node.Id}",
                        }
                    );
                }
            }

            // Connect intermediate tangent nodes with edge segments (farthest → next → ... → nearest).
            // Skip tangent-links that would span across manual arc chains — the existing
            // chain edges provide the connectivity between tangent nodes.
            for (int t = 0; t < sorted.Count - 1; t++)
            {
                var fromTan = sorted[t];
                var toTan = sorted[t + 1];
                if (fromTan.Node.Id == toTan.Node.Id)
                {
                    continue;
                }
                if (fromTan.Placement.LandsInManualArc || toTan.Placement.LandsInManualArc)
                {
                    continue;
                }
                double segDist = GeoMath.DistanceNm(fromTan.Node.Latitude, fromTan.Node.Longitude, toTan.Node.Latitude, toTan.Node.Longitude);
                layout.Edges.Add(
                    new GroundEdge
                    {
                        Nodes = [fromTan.Node, toTan.Node],
                        TaxiwayName = edge.TaxiwayName,
                        DistanceNm = segDist,
                        Origin = $"Fillet:phase-d-tangent-link@{intersection.Id} {edge.TaxiwayName} #{fromTan.Node.Id}↔#{toTan.Node.Id}",
                    }
                );
            }

            consumedEdges.Add(edge);
        }

        // Merge collinear pairs — skip when preserving the intersection node, because
        // the preserve stubs handle straight-through connectivity with correct taxiway names.
        // In preserve mode, still consume the original collinear edges so they don't become orphans.
        int edgesMerged = 0;
        var mergedEdges = new HashSet<GroundEdge>();
        if (preserveNode)
        {
            foreach (var (edgeA, _, edgeB, _) in plannedMerges)
            {
                consumedEdges.Add(edgeA);
                consumedEdges.Add(edgeB);
            }
        }
        else
        {
            // Track which edges have been used in a merge to avoid creating duplicate edges
            // when one edge participates in multiple collinear pairs (e.g., W collinear with both W1 and W2).
            foreach (var (edgeA, otherA, edgeB, otherB) in plannedMerges)
            {
                if (mergedEdges.Contains(edgeA) || mergedEdges.Contains(edgeB))
                {
                    continue;
                }

                bool aHasTangent = edgeTangentNodes.TryGetValue(edgeA, out var tanListA);
                bool bHasTangent = edgeTangentNodes.TryGetValue(edgeB, out var tanListB);

                // Determine the effective endpoints after shortening — use the farthest tangent
                GroundNode endA = aHasTangent ? tanListA!.MaxBy(t => t.Placement.TangentDistNm).Node : otherA;
                GroundNode endB = bHasTangent ? tanListB!.MaxBy(t => t.Placement.TangentDistNm).Node : otherB;

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
        }

        // Collect all candidate reconnection nodes: tangent points + merge endpoints
        var reconnectCandidates = new List<GroundNode>();
        foreach (var entries in edgeTangentNodes.Values)
        {
            foreach (var (node, _) in entries)
            {
                reconnectCandidates.Add(node);
            }
        }
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
        foreach (var ce in consumedEdges)
        {
            Log.LogDebug(
                "[Int#{IntId}] Consuming edge {Tw}({A}↔{B}) origin={Origin}",
                intersection.Id,
                ce.TaxiwayName,
                ce.Nodes[0].Id,
                ce.Nodes[1].Id,
                ce.Origin
            );
        }
        layout.Edges.RemoveAll(e => consumedEdges.Contains(e));

        // Remove walked shape nodes. Deferred until after consumedEdges cleanup so
        // we only remove nodes that truly have no remaining edges. Edges to surviving
        // neighbors (not consumed by the walk) are left intact.
        foreach (var shapeNode in deferredShapeNodes)
        {
            var remainingEdges = layout.Edges.Where(e => (e.Nodes[0].Id == shapeNode.Id) || (e.Nodes[1].Id == shapeNode.Id)).ToList();
            var remainingArcs = layout.Arcs.Where(a => (a.Nodes[0].Id == shapeNode.Id) || (a.Nodes[1].Id == shapeNode.Id)).ToList();
            if ((remainingEdges.Count == 0) && (remainingArcs.Count == 0))
            {
                layout.Nodes.Remove(shapeNode.Id);
                Log.LogDebug("[Int#{IntId}] Removed shape node #{NodeId} (no remaining edges)", intersection.Id, shapeNode.Id);
            }
            else
            {
                Log.LogDebug(
                    "[Int#{IntId}] Kept shape node #{NodeId} ({Edges} edges, {Arcs} arcs surviving: {Detail})",
                    intersection.Id,
                    shapeNode.Id,
                    remainingEdges.Count,
                    remainingArcs.Count,
                    string.Join(", ", remainingEdges.Select(e => $"{e.TaxiwayName}({e.Nodes[0].Id}↔{e.Nodes[1].Id})"))
                );
            }
        }

        if (preserveNode)
        {
            // Preserve: keep the intersection node, connect to the nearest tangent on each edge.
            // When the tangent lands in a manual arc chain, connect to the edge's far node
            // instead — the chain provides connectivity from there to the tangent.
            foreach (var (edge, tangentEntries) in edgeTangentNodes)
            {
                var nearest = tangentEntries.MinBy(t => t.Placement.TangentDistNm);
                var stubTarget = nearest.Placement.LandsInManualArc ? edge.OtherNode(intersection) : nearest.Node;
                double stubDist = GeoMath.DistanceNm(intersection.Latitude, intersection.Longitude, stubTarget.Latitude, stubTarget.Longitude);
                layout.Edges.Add(
                    new GroundEdge
                    {
                        Nodes = [intersection, stubTarget],
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
            // Standard fillet: remove the intersection node entirely.
            // Also remove any edges/arcs still referencing it — earlier fillet iterations
            // may have created edges (shorten, passthrough, tangent-link) pointing to this
            // node, and original pre-fillet edges may have survived consumedEdges.
            int intId = intersection.Id;
            var danglingEdges = layout.Edges.Where(e => (e.Nodes[0].Id == intId) || (e.Nodes[1].Id == intId)).ToList();
            var danglingArcs = layout.Arcs.Where(a => (a.Nodes[0].Id == intId) || (a.Nodes[1].Id == intId)).ToList();
            foreach (var de in danglingEdges)
            {
                Log.LogDebug(
                    "[Int#{IntId}] Node removal purging edge {Tw}({A}↔{B}) origin={Origin}",
                    intId,
                    de.TaxiwayName,
                    de.Nodes[0].Id,
                    de.Nodes[1].Id,
                    de.Origin
                );
            }
            foreach (var da in danglingArcs)
            {
                Log.LogDebug(
                    "[Int#{IntId}] Node removal purging arc {Tw}({A}↔{B}) origin={Origin}",
                    intId,
                    da.TaxiwayName,
                    da.Nodes[0].Id,
                    da.Nodes[1].Id,
                    da.Origin
                );
            }
            layout.Edges.RemoveAll(e => (e.Nodes[0].Id == intId) || (e.Nodes[1].Id == intId));
            layout.Arcs.RemoveAll(a => (a.Nodes[0].Id == intId) || (a.Nodes[1].Id == intId));
            layout.Nodes.Remove(intId);
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

    private static TangentPlacement ComputeTangentPlacement(
        GroundEdge edge,
        GroundNode intersection,
        double bearing,
        double tangentDistNm,
        TaxiwayWalkResult walk
    )
    {
        double tangentDistFt = tangentDistNm * GeoMath.FeetPerNm;
        double firstEdgeFt = walk.Steps[0].CumulativeDistFt;

        double lat;
        double lon;
        double? bearingAtTangent;
        List<GroundEdge> walkedEdges;
        List<GroundNode> walkedShapeNodes;
        List<GroundNode> passthroughNodes;
        GroundNode? walkFarNode;
        bool landsInManualArc;
        GroundEdge? splitEdge;

        if (tangentDistFt <= firstEdgeFt)
        {
            (lat, lon) = GeoMath.ProjectPointRaw(intersection.Latitude, intersection.Longitude, bearing, tangentDistNm);
            bearingAtTangent = null;
            walkedEdges = [];
            walkedShapeNodes = [];
            passthroughNodes = [];
            walkFarNode = null;
            landsInManualArc = false;
            splitEdge = null;
        }
        else
        {
            (lat, lon, double walkBearing, walkedEdges, walkedShapeNodes, passthroughNodes, walkFarNode, landsInManualArc, splitEdge) =
                InterpolateAlongWalk(walk, intersection, tangentDistFt);
            bearingAtTangent = walkBearing;
            Log.LogDebug(
                "[Int#{IntId}]   TangentPoint on {Tw}(→{Other}): walked {WalkEdges} extra edges past first ({FirstFt:F0}ft)",
                intersection.Id,
                edge.TaxiwayName,
                edge.OtherNode(intersection).Id,
                walkedEdges.Count,
                firstEdgeFt
            );
        }

        Log.LogDebug(
            "[Int#{IntId}]   TangentPoint on {Tw}(→{Other}): at {Dist:F0}ft, bearing={Brg:F1}°",
            intersection.Id,
            edge.TaxiwayName,
            edge.OtherNode(intersection).Id,
            tangentDistFt,
            bearing
        );

        return new TangentPlacement(
            lat,
            lon,
            tangentDistNm,
            bearingAtTangent,
            walkedEdges,
            walkedShapeNodes,
            passthroughNodes,
            walkFarNode,
            landsInManualArc,
            splitEdge
        );
    }

    /// <summary>
    /// Detect chains of shape-point nodes that form pre-existing manual arcs.
    /// A manual arc is a chain of 3+ TaxiwayIntersection nodes where each interior
    /// node has exactly 2 edges on the same taxiway, and the cumulative bearing
    /// change from start to end exceeds 30°.
    /// Returns the set of interior node IDs that should be excluded from filleting.
    /// </summary>
    private static HashSet<int> DetectManualArcNodes(AirportGroundLayout layout)
    {
        var excluded = new HashSet<int>();

        // All shape-point nodes (2 edges, same taxiway) are geometry nodes, not
        // real intersections. Exclude them from filleting and protect their edges
        // during walks — they provide the original taxiway curve geometry.
        foreach (var node in layout.Nodes.Values)
        {
            if (IsShapePointNode(node))
            {
                excluded.Add(node.Id);
            }
        }

        if (excluded.Count > 0)
        {
            Log.LogDebug("Excluding {Count} shape-point nodes from filleting and walk consumption", excluded.Count);
        }

        return excluded;
    }

    /// <summary>
    /// A shape-point node is a TaxiwayIntersection with exactly 2 GroundEdge edges
    /// on the same taxiway (no other taxiways, no arcs in the mix).
    /// </summary>
    private static bool IsShapePointNode(GroundNode node)
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

    /// <summary>
    /// Get or create a tangent node for a specific edge+placement. If an existing tangent
    /// node on the same edge is within 5ft of the desired position, reuse it (deduplication
    /// for pairs that want the same tangent distance on a shared edge).
    /// </summary>
    private static GroundNode GetOrCreateTangentNode(
        AirportGroundLayout layout,
        Dictionary<GroundEdge, List<(GroundNode Node, TangentPlacement Placement)>> edgeTangentNodes,
        GroundEdge edge,
        TangentPlacement placement,
        GroundNode intersection,
        ref int nextNodeId
    )
    {
        const double dedupeThresholdNm = 5.0 / GeoMath.FeetPerNm;

        if (edgeTangentNodes.TryGetValue(edge, out var existing))
        {
            foreach (var (node, _) in existing)
            {
                double dist = GeoMath.DistanceNm(node.Latitude, node.Longitude, placement.Lat, placement.Lon);
                if (dist <= dedupeThresholdNm)
                {
                    return node;
                }
            }
        }
        else
        {
            existing = [];
            edgeTangentNodes[edge] = existing;
        }

        var otherNode = edge.OtherNode(intersection);
        int id = nextNodeId++;
        var newNode = new GroundNode
        {
            Id = id,
            Latitude = placement.Lat,
            Longitude = placement.Lon,
            Type = GroundNodeType.TaxiwayIntersection,
            SourceIntersectionPosition = (intersection.Latitude, intersection.Longitude),
            Origin = $"Fillet:tangent-node@{intersection.Id} on-{edge.TaxiwayName}(→{otherNode.Id})",
        };
        layout.Nodes[id] = newNode;
        existing.Add((newNode, placement));

        Log.LogDebug(
            "[Int#{IntId}] Phase B: tangent node #{NodeId} on {Tw}(→{Other}) at {Dist:F0}ft from intersection",
            intersection.Id,
            id,
            edge.TaxiwayName,
            otherNode.Id,
            placement.TangentDistNm * GeoMath.FeetPerNm
        );

        return newNode;
    }

    private record TangentPlacement(
        double Lat,
        double Lon,
        double TangentDistNm,
        double? BearingTowardIntersectionDeg,
        List<GroundEdge> WalkedEdges,
        List<GroundNode> WalkedShapeNodes,
        List<GroundNode> PassthroughNodes,
        GroundNode? WalkFarNode,
        bool LandsInManualArc,
        GroundEdge? SplitEdge
    );

    private record TaxiwayWalkResult(double AvailableLengthFt, GroundNode TerminalNode, List<TaxiwayWalkStep> Steps);

    private record TaxiwayWalkStep(GroundEdge Edge, GroundNode FarNode, double CumulativeDistFt, bool HasOtherTaxiways, bool IsManualArc);

    /// <summary>
    /// Walk along a taxiway chain from an intersection, following same-taxiway edges
    /// through shape-point nodes. Stops at real junctions (multiple same-taxiway
    /// continuations), non-intersection nodes, or dead ends.
    /// </summary>
    private static TaxiwayWalkResult WalkTaxiway(GroundEdge startEdge, GroundNode intersection, HashSet<int> manualArcNodes)
    {
        var otherNode = startEdge.OtherNode(intersection);
        double firstEdgeFt =
            GeoMath.DistanceNm(intersection.Latitude, intersection.Longitude, otherNode.Latitude, otherNode.Longitude) * GeoMath.FeetPerNm;

        bool hasOtherTw = otherNode.Edges.Any(e => (e is GroundEdge ge) && (ge.TaxiwayName != startEdge.TaxiwayName));
        bool isManualArc = manualArcNodes.Contains(otherNode.Id);
        var steps = new List<TaxiwayWalkStep> { new(startEdge, otherNode, firstEdgeFt, hasOtherTw, isManualArc) };

        var currentNode = otherNode;
        var prevEdge = startEdge;
        double cumDist = firstEdgeFt;

        while (true)
        {
            GroundEdge? continuation = null;
            int count = 0;
            foreach (var e in currentNode.Edges)
            {
                if ((e is GroundEdge ge) && (ge != prevEdge) && (ge.TaxiwayName == startEdge.TaxiwayName))
                {
                    continuation = ge;
                    count++;
                }
            }

            if ((count != 1) || (currentNode.Type != GroundNodeType.TaxiwayIntersection))
            {
                break;
            }

            var nextNode = continuation!.OtherNode(currentNode);
            double edgeFt =
                GeoMath.DistanceNm(currentNode.Latitude, currentNode.Longitude, nextNode.Latitude, nextNode.Longitude) * GeoMath.FeetPerNm;
            cumDist += edgeFt;

            bool nextHasOtherTw = nextNode.Edges.Any(e => (e is GroundEdge ge) && (ge.TaxiwayName != startEdge.TaxiwayName));
            bool nextIsManualArc = manualArcNodes.Contains(nextNode.Id);
            steps.Add(new TaxiwayWalkStep(continuation, nextNode, cumDist, nextHasOtherTw, nextIsManualArc));

            prevEdge = continuation;
            currentNode = nextNode;
        }

        return new TaxiwayWalkResult(cumDist, currentNode, steps);
    }

    /// <summary>
    /// Interpolate a position at the given distance along a taxiway walk chain.
    /// Returns the position plus lists of fully consumed edges and pass-through junction nodes.
    /// </summary>
    private static (
        double Lat,
        double Lon,
        double BearingTowardIntersectionDeg,
        List<GroundEdge> ConsumedEdges,
        List<GroundNode> ShapeNodes,
        List<GroundNode> PassthroughNodes,
        GroundNode FarNode,
        bool LandsInManualArc,
        GroundEdge? SplitEdge
    ) InterpolateAlongWalk(TaxiwayWalkResult walk, GroundNode intersection, double targetDistFt)
    {
        var consumed = new List<GroundEdge>();
        var shapeNodes = new List<GroundNode>();
        var passthrough = new List<GroundNode>();

        bool enteredManualArc = false;
        GroundEdge? splitEdge = null;

        for (int i = 0; i < walk.Steps.Count; i++)
        {
            var step = walk.Steps[i];

            if (step.IsManualArc)
            {
                enteredManualArc = true;
            }

            // Step 0's FarNode sits between the starting edge and step 1's edge.
            // When the walk continues past it (step 1+), both its edges get consumed
            // (starting edge + step 1). Classify it so it gets cleaned up properly.
            if ((i == 0) && (walk.Steps.Count > 1) && !step.IsManualArc)
            {
                bool step0Removable =
                    !step.HasOtherTaxiways
                    && (step.FarNode.Type == GroundNodeType.TaxiwayIntersection)
                    && (step.FarNode.SourceIntersectionPosition is null);
                if (step0Removable)
                {
                    shapeNodes.Add(step.FarNode);
                }
                else
                {
                    passthrough.Add(step.FarNode);
                }
            }

            if (targetDistFt <= step.CumulativeDistFt)
            {
                double prevCum = i > 0 ? walk.Steps[i - 1].CumulativeDistFt : 0;
                double edgeLen = step.CumulativeDistFt - prevCum;
                double fraction = edgeLen > 0 ? (targetDistFt - prevCum) / edgeLen : 0;

                var fromNode = i > 0 ? walk.Steps[i - 1].FarNode : intersection;
                var toNode = step.FarNode;

                double lat = fromNode.Latitude + (fraction * (toNode.Latitude - fromNode.Latitude));
                double lon = fromNode.Longitude + (fraction * (toNode.Longitude - fromNode.Longitude));

                double bearingToward = GeoMath.BearingTo(toNode.Latitude, toNode.Longitude, fromNode.Latitude, fromNode.Longitude);

                // If the tangent lands inside a manual arc, record the edge being split
                // so Phase D can split it instead of creating spanning edges.
                if (enteredManualArc)
                {
                    splitEdge = step.Edge;
                }

                return (lat, lon, bearingToward, consumed, shapeNodes, passthrough, toNode, enteredManualArc, splitEdge);
            }

            // Skip the starting edge (index 0) — it's consumed separately in Phase D.
            // Only track edges beyond the first as walked-through extras.
            if (i > 0)
            {
                // Manual arc edges and edges beyond them are left intact —
                // the chain already provides connectivity.
                if (step.IsManualArc || enteredManualArc)
                {
                    continue;
                }

                consumed.Add(step.Edge);
                Log.LogDebug(
                    "  Walk step {I}: consumed edge {Tw}({A}↔{B}), farNode=#{Far} hasOtherTw={Other} isManualArc={Arc}",
                    i,
                    step.Edge.TaxiwayName,
                    step.Edge.Nodes[0].Id,
                    step.Edge.Nodes[1].Id,
                    step.FarNode.Id,
                    step.HasOtherTaxiways,
                    step.IsManualArc
                );
                bool isRemovable =
                    !step.HasOtherTaxiways
                    && (step.FarNode.Type == GroundNodeType.TaxiwayIntersection)
                    && (step.FarNode.SourceIntersectionPosition is null);
                if (isRemovable)
                {
                    shapeNodes.Add(step.FarNode);
                    Log.LogDebug("  Walk step {I}: #{Far} classified as shape (removable)", i, step.FarNode.Id);
                }
                else
                {
                    passthrough.Add(step.FarNode);
                }
            }
        }

        var terminal = walk.Steps[^1].FarNode;
        var prevNode = walk.Steps.Count > 1 ? walk.Steps[^2].FarNode : intersection;
        double termBearing = GeoMath.BearingTo(terminal.Latitude, terminal.Longitude, prevNode.Latitude, prevNode.Longitude);
        return (terminal.Latitude, terminal.Longitude, termBearing, consumed, shapeNodes, passthrough, terminal, enteredManualArc, splitEdge);
    }
}
