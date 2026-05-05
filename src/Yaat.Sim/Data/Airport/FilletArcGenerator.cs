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
    private const double MaxTangentDistFt = 150.0;

    // Tangent-node and intersection-node coincidence threshold. Two nodes within
    // this distance are treated as the same point: tangent placements dedupe to
    // one shared node, and post-fillet coincident intersections merge.
    private const double CoincidentNodeThresholdFt = 5.0;
    private const double CoincidentNodeThresholdNm = CoincidentNodeThresholdFt / GeoMath.FeetPerNm;

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

            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug(
                    "[Int#{IntId}] edges ({Count}): [{Edges}]",
                    node.Id,
                    node.Edges.Count,
                    string.Join(
                        ", ",
                        node.Edges.Select(e =>
                            $"{e.TaxiwayName}({e.Nodes[0].Id}↔{e.Nodes[1].Id} {(e is GroundArc ? "arc" : "edge")} {e.DistanceNm * GeoMath.FeetPerNm:F0}ft rwy={e.IsRunwayCenterline}) origin={e.Origin}"
                        )
                    )
                );
            }

            if (node.Edges.Count < 2)
            {
                continue;
            }

            // Snapshot runway edges before this fillet for validation. Skipped when
            // warning level is suppressed — the snapshot + post-iteration scan are
            // O(M) per intersection just to maybe emit a warning, so they're a
            // measurable cost for large airports.
            HashSet<(int, int, string)>? rwyEdgesBefore = null;
            List<(string TaxiwayName, double Brg)>? rwyBearingsBefore = null;
            if (Log.IsEnabled(LogLevel.Warning))
            {
                rwyEdgesBefore = layout.Edges.Where(e => e.IsRunwayCenterline).Select(e => (e.Nodes[0].Id, e.Nodes[1].Id, e.TaxiwayName)).ToHashSet();
                rwyBearingsBefore = layout
                    .Edges.Where(e => e.IsRunwayCenterline)
                    .Select(e => (e.TaxiwayName, Brg: GeoMath.BearingTo(e.Nodes[0].Position, e.Nodes[1].Position)))
                    .ToList();
            }

            var result = FilletNode(layout, node, preserveNode, manualArcNodes, ref nextNodeId);
            if (result.Success)
            {
                filletedCount++;
                arcCount += result.ArcsCreated;
                mergedCount += result.EdgesMerged;
            }

            // Validate: check all runway edges still follow their original bearing.
            if (rwyEdgesBefore is not null && rwyBearingsBefore is not null)
            {
                foreach (var rwyEdge in layout.Edges.Where(e => e.IsRunwayCenterline))
                {
                    double brg = GeoMath.BearingTo(rwyEdge.Nodes[0].Position, rwyEdge.Nodes[1].Position);

                    // Check if this exact edge (by node IDs + taxiway name) existed before
                    bool isNew = !rwyEdgesBefore.Contains((rwyEdge.Nodes[0].Id, rwyEdge.Nodes[1].Id, rwyEdge.TaxiwayName));
                    if (!isNew)
                    {
                        continue;
                    }

                    // New runway edge — check it matches one of the original segment bearings
                    bool bearingOk = rwyBearingsBefore.Any(orig =>
                        (orig.TaxiwayName == rwyEdge.TaxiwayName) && (GeoMath.AbsBearingDifference(orig.Brg, brg) < 1.0)
                    );
                    if (!bearingOk)
                    {
                        Log.LogWarning(
                            "[Int#{IntId}] RUNWAY DISPLACED: edge {Tw}({A}->{B}) bearing={Brg:F1}° doesn't match any original {Tw2} bearing. Origin: {Origin}",
                            node.Id,
                            rwyEdge.TaxiwayName,
                            rwyEdge.Nodes[0].Id,
                            rwyEdge.Nodes[1].Id,
                            brg,
                            rwyEdge.TaxiwayName,
                            rwyEdge.Origin
                        );
                    }
                }
            }
        }

        // --- Global cleanup: merge coincident nodes across all fillet iterations ---
        // When adjacent intersections are both filleted, their tangent-point nodes on the
        // shared edge can end up at the same position. Merge them so the graph has no
        // zero-length edges between coincident nodes.
        int nodesMerged = MergeCoincidentNodes(layout);

        int rescued = RescueOrphanedTangentNodes(layout);
        int redundant = RemoveRedundantPreserveEdges(layout);
        int duplicateCornerArcs = RemoveDuplicateCornerArcs(layout);
        int parallelBypassEdges = RemoveParallelBypassEdges(layout);
        int directShortensAdded = AddDirectShortensFromArcAnchors(layout);

        layout.RebuildAdjacencyLists();

        Log.LogInformation(
            "Fillet arcs: {FilletedNodes} filleted, {Arcs} arcs, {Merged} merged, {NodesMerged} coincident merged, {Rescued} rescued, {Redundant} redundant preserve edges removed, {DupCorners} duplicate corner arcs removed, {Bypasses} parallel bypass edges removed, {DirectShortens} direct shortens added",
            filletedCount,
            arcCount,
            mergedCount,
            nodesMerged,
            rescued,
            redundant,
            duplicateCornerArcs,
            parallelBypassEdges,
            directShortensAdded
        );
    }

    /// <summary>
    /// Post-fillet cleanup: when the per-intersection pair iterator emits multiple
    /// arcs that round the *same physical corner* — same intersection, same pair
    /// of taxiway directions, same approach bearings at the tangent endpoints —
    /// keep only the arc with the largest <see cref="GroundArc.MinRadiusOfCurvatureFt"/>
    /// and discard the rest. The duplicates arise when upstream intersections
    /// inject parallel bypass edges (e.g. SFO @57 leaves a `phase-d-passthrough@57`
    /// edge alongside the original 268↔141 on the same E centerline; @268's pair
    /// iterator then crosses every F-side edge with each parallel E-NE edge,
    /// producing extra arcs at the same corner with smaller radii). The largest
    /// radius corresponds to the tangent placement that had the most edge
    /// available — the "best" geometry for that corner. Smaller-radius arcs
    /// at the same corner come from constrained tangent placements and are not
    /// needed because the larger arc covers the same turn.
    ///
    /// The corner identity is `(intersectionId, sorted-taxiway-pair, sorted
    /// approach-bearings rounded to 5°)`. Different physical corners at the
    /// same intersection always differ in the bearing pair (e.g. a 4-way's
    /// NE+NW corner vs SW+SE corner share the same turn angle but have
    /// opposite bearings at each tangent), so this key is robust against
    /// merging legitimately distinct corners.
    /// </summary>
    private static int RemoveDuplicateCornerArcs(AirportGroundLayout layout)
    {
        // Bearing tolerance for "same physical corner". An angular cluster avoids the
        // boundary fragility of a Math.Round bucket: two arcs at 4.9° and 5.1° were
        // bucketed apart even though they describe the same corner.
        const double cornerBearingToleranceDeg = 5.0;

        // Group by (intersection, taxiway-pair) first; cluster within each group by
        // sorted bearing pair so endpoint order doesn't matter.
        var arcsByGroup = new Dictionary<(int IntId, string Twy), List<(GroundArc Arc, double BrgLo, double BrgHi)>>();

        foreach (var arc in layout.Arcs)
        {
            if (arc.FilletProvenance is not CornerArcProvenance prov)
            {
                continue;
            }

            double b0 = arc.EdgeBearingAtNode0Deg;
            double b1 = arc.EdgeBearingAtNode1Deg;
            (double lo, double hi) = b0 <= b1 ? (b0, b1) : (b1, b0);
            var key = (prov.IntersectionId, prov.NormalizedTaxiwayKey);

            if (!arcsByGroup.TryGetValue(key, out var list))
            {
                list = [];
                arcsByGroup[key] = list;
            }

            list.Add((arc, lo, hi));
        }

        var toRemove = new List<GroundArc>();
        foreach (var (key, group) in arcsByGroup)
        {
            if (group.Count <= 1)
            {
                continue;
            }

            // Cluster arcs by both bearings within tolerance. Single-link clustering:
            // two arcs share a cluster if both their endpoint bearings differ by less
            // than the tolerance. Typical N is small (handful per intersection).
            var clusterIdx = new int[group.Count];
            for (int i = 0; i < clusterIdx.Length; i++)
            {
                clusterIdx[i] = i;
            }

            for (int i = 0; i < group.Count; i++)
            {
                for (int j = i + 1; j < group.Count; j++)
                {
                    if (clusterIdx[j] != j)
                    {
                        continue;
                    }
                    if (
                        GeoMath.AbsBearingDifference(group[i].BrgLo, group[j].BrgLo) < cornerBearingToleranceDeg
                        && GeoMath.AbsBearingDifference(group[i].BrgHi, group[j].BrgHi) < cornerBearingToleranceDeg
                    )
                    {
                        clusterIdx[j] = clusterIdx[i];
                    }
                }
            }

            // Bucket by cluster id, keep the arc with the largest min radius per cluster.
            var clusters = new Dictionary<int, List<(GroundArc Arc, double BrgLo, double BrgHi)>>();
            for (int i = 0; i < group.Count; i++)
            {
                if (!clusters.TryGetValue(clusterIdx[i], out var bucket))
                {
                    bucket = [];
                    clusters[clusterIdx[i]] = bucket;
                }
                bucket.Add(group[i]);
            }

            foreach (var (_, cluster) in clusters)
            {
                if (cluster.Count <= 1)
                {
                    continue;
                }

                cluster.Sort((a, b) => b.Arc.MinRadiusOfCurvatureFt.CompareTo(a.Arc.MinRadiusOfCurvatureFt));
                for (int i = 1; i < cluster.Count; i++)
                {
                    toRemove.Add(cluster[i].Arc);
                }

                if (Log.IsEnabled(LogLevel.Debug))
                {
                    Log.LogDebug(
                        "[CornerDedup@{IntId}] {Twy} bearings(~{Lo:F0}°,~{Hi:F0}°): kept r={KeptR:F1}ft, dropped {DropCount} (radii={DroppedRadii})",
                        key.IntId,
                        key.Twy,
                        cluster[0].BrgLo,
                        cluster[0].BrgHi,
                        cluster[0].Arc.MinRadiusOfCurvatureFt,
                        cluster.Count - 1,
                        string.Join(",", cluster.Skip(1).Select(a => $"{a.Arc.MinRadiusOfCurvatureFt:F1}"))
                    );
                }
            }
        }

        foreach (var arc in toRemove)
        {
            layout.Arcs.Remove(arc);
        }

        return toRemove.Count;
    }

    /// <summary>
    /// Post-fillet cleanup: remove parallel-collinear bypass edges. When two
    /// adjacent intersections each emit a phase-d straight-edge chain on the
    /// same physical taxiway pavement (e.g. SFO @141 and @268 both place
    /// tangent chains on the shared E centerline between them), the resulting
    /// graph contains two parallel collinear chains. The longer "bypass" edge
    /// from a node skips intermediate tangent nodes that the shorter edge
    /// reaches via a chain — and the bypass's endpoint is often a tangent that
    /// only continues into a foreign-taxiway corner arc, so walkers stepping
    /// onto the bypass dead-end at a transition arc instead of staying on the
    /// authorized taxiway.
    ///
    /// <para>
    /// Detection: for each node N with two outgoing same-taxiway phase-d
    /// straight edges in the same bearing bucket (5°), the longer edge is a
    /// bypass iff its endpoint is reachable from the shorter edge's endpoint
    /// by walking forward along the same taxiway with the same general
    /// bearing through straight phase-d edges and SINGLE-named taxiway arcs.
    /// (Multi-named transition arcs are excluded from the walk — they
    /// represent corner transitions to other taxiways, not continuations.)
    /// Iterates until no more bypasses found.
    /// </para>
    ///
    /// <para>
    /// Conservative: only phase-d origin edges are eligible for removal, so
    /// original GeoJSON straight edges are preserved. Corner arcs are never
    /// touched. Tangent nodes are never removed — only the bypass straight
    /// edges that skip them.
    /// </para>
    /// </summary>
    private static int RemoveParallelBypassEdges(AirportGroundLayout layout)
    {
        const int BearingBucketDeg = 5;
        const double WalkBearingToleranceDeg = 30.0;
        const int MaxWalkSteps = 10;

        int totalRemoved = 0;

        bool changed;
        do
        {
            // Refresh adjacency before each pass so node.Edges reflects prior removals.
            layout.RebuildAdjacencyLists();
            changed = false;
            var toRemove = new HashSet<GroundEdge>();

            foreach (var node in layout.Nodes.Values)
            {
                var groups = new Dictionary<(string Twy, int Bucket), List<(GroundEdge Edge, GroundNode Other, double Bearing)>>();

                foreach (var iedge in node.Edges)
                {
                    if (iedge is not GroundEdge edge)
                    {
                        continue;
                    }
                    // Only consider phase-d edges (rescue-orphan and non-fillet edges aren't bypass candidates).
                    if (edge.FilletProvenance is not FilletEdgeProvenance fep || fep.Kind == FilletEdgeKind.RescueOrphan)
                    {
                        continue;
                    }

                    var other = edge.Nodes[0].Id == node.Id ? edge.Nodes[1] : edge.Nodes[0];
                    double bearing = GeoMath.BearingTo(node.Position, other.Position);
                    int bucket = ((int)Math.Round(bearing / BearingBucketDeg) + 72) % 72;
                    var key = (edge.TaxiwayName, bucket);
                    if (!groups.TryGetValue(key, out var list))
                    {
                        list = [];
                        groups[key] = list;
                    }
                    list.Add((edge, other, bearing));
                }

                foreach (var (_, edges) in groups)
                {
                    if (edges.Count < 2)
                    {
                        continue;
                    }
                    edges.Sort((a, b) => a.Edge.DistanceNm.CompareTo(b.Edge.DistanceNm));

                    for (int i = 0; i < edges.Count - 1; i++)
                    {
                        var shorter = edges[i];
                        if (toRemove.Contains(shorter.Edge))
                        {
                            continue;
                        }

                        for (int j = i + 1; j < edges.Count; j++)
                        {
                            var longer = edges[j];
                            if (toRemove.Contains(longer.Edge))
                            {
                                continue;
                            }

                            if (
                                CanReachByWalkingTaxiway(
                                    shorter.Other,
                                    longer.Other,
                                    longer.Edge.TaxiwayName,
                                    longer.Bearing,
                                    WalkBearingToleranceDeg,
                                    toRemove,
                                    MaxWalkSteps
                                )
                            )
                            {
                                Log.LogDebug(
                                    "[BypassDedup] Removing bypass {Twy} #{From}↔#{To} ({Dist:F0}ft, bearing={Brg:F1}°) — endpoint reachable via #{ShortMid} ({ShortDist:F0}ft)",
                                    longer.Edge.TaxiwayName,
                                    node.Id,
                                    longer.Other.Id,
                                    longer.Edge.DistanceNm * GeoMath.FeetPerNm,
                                    longer.Bearing,
                                    shorter.Other.Id,
                                    shorter.Edge.DistanceNm * GeoMath.FeetPerNm
                                );
                                toRemove.Add(longer.Edge);
                                changed = true;
                            }
                        }
                    }
                }
            }

            if (toRemove.Count > 0)
            {
                foreach (var e in toRemove)
                {
                    layout.Edges.Remove(e);
                }
                totalRemoved += toRemove.Count;
            }
        } while (changed);

        return totalRemoved;
    }

    /// <summary>
    /// BFS forward along same-taxiway edges with bearings within
    /// <paramref name="tolDeg"/> of <paramref name="targetBearing"/>. Returns
    /// true if <paramref name="goal"/> is reachable from <paramref name="start"/>.
    /// Excludes edges in <paramref name="excluded"/> (pending-removal bypasses)
    /// and multi-named transition arcs (which represent corner exits to other
    /// taxiways, not continuations of the named taxiway).
    /// </summary>
    private static bool CanReachByWalkingTaxiway(
        GroundNode start,
        GroundNode goal,
        string twy,
        double targetBearing,
        double tolDeg,
        ISet<GroundEdge> excluded,
        int maxSteps
    )
    {
        if (start.Id == goal.Id)
        {
            return true;
        }

        var visited = new HashSet<int> { start.Id };
        var queue = new Queue<GroundNode>();
        queue.Enqueue(start);

        for (int step = 0; step < maxSteps && queue.Count > 0; step++)
        {
            int levelCount = queue.Count;
            for (int q = 0; q < levelCount; q++)
            {
                var current = queue.Dequeue();
                foreach (var edge in current.Edges)
                {
                    if (!edge.MatchesTaxiway(twy))
                    {
                        continue;
                    }
                    if (edge is GroundEdge straight && excluded.Contains(straight))
                    {
                        continue;
                    }
                    // Multi-named arcs (e.g. F-E) are corner transitions — they
                    // exit the current taxiway. Don't treat as continuations.
                    if (edge is GroundArc arc && arc.TaxiwayNames.Length != 1)
                    {
                        continue;
                    }

                    var other = edge.Nodes[0].Id == current.Id ? edge.Nodes[1] : edge.Nodes[0];
                    if (visited.Contains(other.Id))
                    {
                        continue;
                    }

                    double bearing = GeoMath.BearingTo(current.Position, other.Position);
                    if (GeoMath.AbsBearingDifference(bearing, targetBearing) > tolDeg)
                    {
                        continue;
                    }

                    if (other.Id == goal.Id)
                    {
                        return true;
                    }
                    visited.Add(other.Id);
                    queue.Enqueue(other);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Post-fillet cleanup: when fillet generation creates multiple tangent nodes
    /// for the SAME direction at the SAME intersection (one per fillet pair processed —
    /// e.g. T4-T east, T4-T west, and T4-C all create their own tangent on the
    /// T4-toward-#57 line), each chain member has its own corner arc but only ONE end
    /// of the chain hosts the shorten edge to the next T4 segment. Walkers entering
    /// the chain at the "wrong" end traverse the chain to reach the shorten anchor,
    /// producing a U-turn against the direction they exited the corner arc.
    ///
    /// <para>
    /// Fix: for each tangent chain, find every arc-anchored chain node (a chain
    /// member with a <c>Fillet:phase-c-arc@</c> arc edge) and every external
    /// shorten target (a node connected to a chain member via a <c>Fillet:phase-d-shorten@</c>
    /// edge). Add a direct phase-d-shorten edge from every arc-anchored node to every
    /// shorten target if one does not already exist. After this pass, walkers exiting
    /// any corner arc reach the next external segment in a single straight edge with
    /// no detour through the chain.
    /// </para>
    ///
    /// <para>
    /// Conservative: this pass only ADDS edges. Existing edges (tangent-links,
    /// arcs, original shortens) are preserved unchanged so other paths through
    /// the chain are not broken.
    /// </para>
    /// </summary>
    private static int AddDirectShortensFromArcAnchors(AirportGroundLayout layout)
    {
        layout.RebuildAdjacencyLists();

        // Group tangent nodes by (intersectionId, taxiway, destinationNodeId) — read
        // straight from the typed TangentNodeProvenance attached at construction.
        var groups = new Dictionary<(int IntId, string Twy, int DestId), List<GroundNode>>();
        foreach (var node in layout.Nodes.Values)
        {
            if (node.FilletProvenance is not TangentNodeProvenance tprov)
            {
                continue;
            }
            var key = (tprov.IntersectionId, tprov.Taxiway, tprov.DestinationNodeId);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }
            list.Add(node);
        }

        int addedTotal = 0;

        foreach (var (key, members) in groups)
        {
            if (members.Count < 2)
            {
                continue;
            }

            // BFS within members along TangentLink edges from this intersection to find
            // each connected chain. Process each chain independently.
            int intId = key.IntId;
            bool IsKindAt(IGroundEdge e, FilletEdgeKind kind) =>
                e.FilletProvenance is FilletEdgeProvenance fp && fp.IntersectionId == intId && fp.Kind == kind;
            bool IsArcAnchorAt(IGroundEdge e) =>
                e.FilletProvenance is CornerArcProvenance cap && cap.IntersectionId == intId;
            var memberIds = members.Select(n => n.Id).ToHashSet();

            var visited = new HashSet<int>();
            foreach (var seed in members)
            {
                if (visited.Contains(seed.Id))
                {
                    continue;
                }

                var chain = new List<GroundNode>();
                var queue = new Queue<GroundNode>();
                queue.Enqueue(seed);
                visited.Add(seed.Id);

                while (queue.Count > 0)
                {
                    var n = queue.Dequeue();
                    chain.Add(n);
                    foreach (var iedge in n.Edges)
                    {
                        if (iedge is not GroundEdge edge)
                        {
                            continue;
                        }
                        if (!IsKindAt(edge, FilletEdgeKind.TangentLink))
                        {
                            continue;
                        }
                        var other = edge.OtherNode(n);
                        if (!memberIds.Contains(other.Id) || visited.Contains(other.Id))
                        {
                            continue;
                        }
                        visited.Add(other.Id);
                        queue.Enqueue(other);
                    }
                }

                if (chain.Count < 2)
                {
                    continue;
                }

                // Identify arc anchors: chain members with at least one phase-c-arc from this intersection.
                var arcAnchors = chain.Where(n => n.Edges.Any(IsArcAnchorAt)).ToList();
                if (arcAnchors.Count < 2)
                {
                    continue;
                }

                // Identify chain endpoints (members with exactly one tangent-link neighbor
                // inside the chain). For a linear chain there will be two endpoints.
                var chainEndpoints = chain
                    .Where(n =>
                        n.Edges.Count(e => e is GroundEdge ge && IsKindAt(ge, FilletEdgeKind.TangentLink) && memberIds.Contains(ge.OtherNode(n).Id))
                        <= 1
                    )
                    .ToHashSet();

                // Pair each shorten edge with its anchor (chain member) and target (external).
                // Only chains where the existing shorten anchor sits at a chain ENDPOINT
                // qualify for the U-turn fix — that's the FLL pattern: arc lands at one
                // endpoint, shorten anchors at the opposite endpoint, walking the chain
                // crosses its full length opposite to the arc's exit. Symmetric chains
                // (shorten anchor same node as arc anchor, or shorten in chain interior)
                // do not produce U-turns and are skipped.
                var shortenPairs = new List<(GroundNode Anchor, GroundNode Target)>();
                foreach (var n in chain)
                {
                    foreach (var iedge in n.Edges)
                    {
                        if (iedge is not GroundEdge edge)
                        {
                            continue;
                        }
                        if (!IsKindAt(edge, FilletEdgeKind.Shorten))
                        {
                            continue;
                        }
                        if (!edge.MatchesTaxiway(key.Twy))
                        {
                            continue;
                        }
                        var other = edge.OtherNode(n);
                        if (memberIds.Contains(other.Id))
                        {
                            continue;
                        }
                        shortenPairs.Add((n, other));
                    }
                }

                if (shortenPairs.Count == 0)
                {
                    continue;
                }

                foreach (var (existingAnchor, target) in shortenPairs)
                {
                    if (!chainEndpoints.Contains(existingAnchor))
                    {
                        continue;
                    }

                    // Add direct shortens only for arc anchors at the OPPOSITE chain
                    // endpoint from the existing shorten anchor.
                    foreach (var anchor in arcAnchors)
                    {
                        if (anchor.Id == existingAnchor.Id)
                        {
                            continue;
                        }
                        if (!chainEndpoints.Contains(anchor))
                        {
                            continue;
                        }

                        bool exists = anchor.Edges.Any(e =>
                            e is GroundEdge ge && ge.MatchesTaxiway(key.Twy) && (ge.Nodes[0].Id == target.Id || ge.Nodes[1].Id == target.Id)
                        );
                        if (exists)
                        {
                            continue;
                        }

                        double dist = GeoMath.DistanceNm(anchor.Position, target.Position);
                        var prov = new FilletEdgeProvenance(key.IntId, FilletEdgeKind.ShortenDirect, key.Twy, anchor.Id, target.Id);
                        var newEdge = new GroundEdge
                        {
                            Nodes = [anchor, target],
                            TaxiwayName = key.Twy,
                            DistanceNm = dist,
                            Origin = prov.DisplayString,
                            FilletProvenance = prov,
                        };
                        layout.Edges.Add(newEdge);
                        addedTotal++;

                        Log.LogDebug(
                            "[DirectShorten@{IntId}] Added {Twy} #{Anchor}↔#{Target} ({Dist:F0}ft) — bypass tangent chain (chain size {ChainSize})",
                            key.IntId,
                            key.Twy,
                            anchor.Id,
                            target.Id,
                            dist * GeoMath.FeetPerNm,
                            chain.Count
                        );
                    }
                }
            }
        }

        if (addedTotal > 0)
        {
            layout.RebuildAdjacencyLists();
        }

        return addedTotal;
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

        // Centerline projection nodes are synthetic infrastructure — not real intersections
        if (node.Origin?.StartsWith("RunwayCrossing:centerline-projection") == true)
        {
            return false;
        }

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
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug(
                    "[Eligibility] Node #{Id}: preserve=true (rwy={Rwy}, nonRwy={NonRwy}, edges=[{Edges}])",
                    node.Id,
                    runwayEdgeCount,
                    nonRunwayEdgeCount,
                    string.Join(", ", node.Edges.Select(e => $"{e.TaxiwayName}({e.Nodes[0].Id}↔{e.Nodes[1].Id}) rwy={e.IsRunwayCenterline}"))
                );
            }
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

    // Holds all shared state for a single FilletNode invocation, passed between phases.
    private sealed class FilletContext
    {
        public required AirportGroundLayout Layout { get; init; }
        public required GroundNode Intersection { get; init; }
        public required HashSet<int> ManualArcNodes { get; init; }
        public bool PreserveNode { get; set; }

        // Pre-phase: edge collection and bearing computation
        public required List<GroundEdge> Edges { get; init; }
        public required List<(GroundEdge Edge, GroundNode OtherNode, double Bearing)> EdgeBearings { get; init; }

        // Phase A outputs
        public required Dictionary<GroundEdge, TaxiwayWalkResult> EdgeWalks { get; init; }
        public required List<(
            GroundEdge EdgeA,
            GroundEdge EdgeB,
            double RadiusFt,
            double TurnAngleDeg,
            double BearingA,
            double BearingB,
            TangentPlacement PlacementA,
            TangentPlacement PlacementB
        )> PlannedArcs { get; init; }
        public required List<(GroundEdge EdgeA, GroundNode OtherA, GroundEdge EdgeB, GroundNode OtherB)> PlannedMerges { get; init; }

        // Phase B+C outputs
        public int ArcsCreated { get; set; }
        public required Dictionary<GroundEdge, List<(GroundNode Node, TangentPlacement Placement)>> EdgeTangentNodes { get; init; }

        // Phase D working state
        public required HashSet<GroundEdge> ConsumedEdges { get; init; }
        public required List<GroundNode> DeferredShapeNodes { get; init; }
        public int EdgesMerged { get; set; }
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

        var ctx = new FilletContext
        {
            Layout = layout,
            Intersection = intersection,
            ManualArcNodes = manualArcNodes,
            PreserveNode = preserveNode,
            Edges = edges,
            EdgeBearings = edgeBearings,
            EdgeWalks = [],
            PlannedArcs = [],
            PlannedMerges = [],
            EdgeTangentNodes = [],
            ConsumedEdges = [],
            DeferredShapeNodes = [],
        };

        PhaseA_ComputeFillets(ctx);

        if ((ctx.PlannedArcs.Count + ctx.PlannedMerges.Count) == 0)
        {
            return (false, 0, 0);
        }

        PhaseBC_CreateTangentNodesAndArcs(ctx, ref nextNodeId);
        PhaseD1_ShortenEdges(ctx);
        PhaseD2_MergeCollinearPairs(ctx);
        PhaseD3_ReconnectOrphans(ctx);
        PhaseD4_CleanupAndFinalize(ctx);

        return (true, ctx.ArcsCreated, ctx.EdgesMerged);
    }

    // --- Phase A: Compute all fillets without mutating the graph ---
    // Each pair computes its own tangent positions independently. An edge can have
    // multiple tangent nodes when different pairs need different distances (e.g., a
    // genuine 90° turn vs a near-collinear 176° pair). Coincident positions on the
    // same edge are deduplicated in GetOrCreateTangentNode.
    private static void PhaseA_ComputeFillets(FilletContext ctx)
    {
        // Pre-compute taxiway walks per edge: how far along the taxiway chain we can
        // extend if the first edge is too short for the desired tangent distance.
        foreach (var (edge, _, _) in ctx.EdgeBearings)
        {
            ctx.EdgeWalks[edge] = WalkTaxiway(edge, ctx.Intersection, ctx.ManualArcNodes);
        }

        // Per-pair tangent placements: each arc pair computes its own tangent positions
        // independently, so a near-collinear pair's large tangent distance doesn't corrupt
        // other pairs' arcs via max-wins. Pairs that want the same distance on a shared
        // edge will share a tangent node (deduplicated in Phase B).
        for (int i = 0; i < ctx.EdgeBearings.Count; i++)
        {
            for (int j = i + 1; j < ctx.EdgeBearings.Count; j++)
            {
                var (edgeA, otherA, bearingA) = ctx.EdgeBearings[i];
                var (edgeB, otherB, bearingB) = ctx.EdgeBearings[j];

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
                        ctx.Intersection.Id,
                        edgeA.TaxiwayName,
                        otherA.Id,
                        edgeB.TaxiwayName,
                        otherB.Id,
                        turnAngle
                    );
                    ctx.PlannedMerges.Add((edgeA, otherA, edgeB, otherB));
                }
                else if (turnAngle >= MinFilletAngleDeg)
                {
                    double halfAngleRad = (turnAngle / 2.0) * (Math.PI / 180.0);
                    double tanHalf = Math.Tan(halfAngleRad);

                    var walkA = ctx.EdgeWalks[edgeA];
                    var walkB = ctx.EdgeWalks[edgeB];
                    double availableAFt = walkA.AvailableLengthFt;
                    double availableBFt = walkB.AvailableLengthFt;

                    bool capA = IsEligibleForFilleting(walkA.TerminalNode) && (walkA.TerminalNode.SourceIntersectionPosition is null);
                    bool capB = IsEligibleForFilleting(walkB.TerminalNode) && (walkB.TerminalNode.SourceIntersectionPosition is null);
                    double maxTangentAFt = capA ? availableAFt / 2.0 : availableAFt;
                    double maxTangentBFt = capB ? availableBFt / 2.0 : availableBFt;

                    // Cap tangent distance at the next taxiway intersection along each walk.
                    // Without this, steep turns produce huge tangent distances that consume
                    // edges belonging to neighboring intersections.
                    double intersectionCapAFt = DistToFirstIntersectionFt(walkA);
                    double intersectionCapBFt = DistToFirstIntersectionFt(walkB);
                    maxTangentAFt = Math.Min(maxTangentAFt, intersectionCapAFt);
                    maxTangentBFt = Math.Min(maxTangentBFt, intersectionCapBFt);

                    double maxFitRadiusFt = Math.Min(maxTangentAFt, maxTangentBFt) / tanHalf;
                    double maxRadiusFt = SelectMaxRadius(edgeA, edgeB, turnAngle);
                    double radiusFt = Math.Min(maxFitRadiusFt, maxRadiusFt);

                    double tangentDistFt = radiusFt * tanHalf;

                    // Absolute cap on tangent distance — prevents unreasonably long arcs
                    // even when no intervening intersection exists.
                    if (tangentDistFt > MaxTangentDistFt)
                    {
                        tangentDistFt = MaxTangentDistFt;
                        radiusFt = tangentDistFt / tanHalf;
                    }

                    double tangentDistNm = tangentDistFt / GeoMath.FeetPerNm;

                    Log.LogDebug(
                        "[Int#{IntId}] Pair {A}(→{OtherA}, avail={AAvail:F0}ft)/{B}(→{OtherB}, avail={BAvail:F0}ft): "
                            + "turn={Turn:F1}° radius={R:F0}ft(maxFit={MaxFit:F0}, maxType={MaxType:F0}) tangentDist={TD:F0}ft"
                            + " intCapA={IntCapA:F0}ft intCapB={IntCapB:F0}ft",
                        ctx.Intersection.Id,
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
                        tangentDistFt,
                        intersectionCapAFt,
                        intersectionCapBFt
                    );

                    // Skip pairs that produce degenerate geometry — near-U-turns
                    // where tan(halfAngle) → ∞ produce enormous tangent distances
                    // with tiny or zero radii. These can't produce useful arcs.
                    if (radiusFt < 5.0)
                    {
                        Log.LogDebug(
                            "[Int#{IntId}] Skipping degenerate pair {A}/{B}: radius={R:F1}ft < 5ft",
                            ctx.Intersection.Id,
                            edgeA.TaxiwayName,
                            edgeB.TaxiwayName,
                            radiusFt
                        );
                        continue;
                    }

                    var placementA = ComputeTangentPlacement(edgeA, ctx.Intersection, bearingA, tangentDistNm, walkA);
                    var placementB = ComputeTangentPlacement(edgeB, ctx.Intersection, bearingB, tangentDistNm, walkB);

                    ctx.PlannedArcs.Add((edgeA, edgeB, radiusFt, turnAngle, bearingA, bearingB, placementA, placementB));
                }
            }
        }

        // Preserve the intersection node when collinear pairs exist. The straight-through
        // paths need the center node so each side keeps its correct taxiway name
        // (e.g., W3 south of W, U north of W). Stubs from tangent nodes to the center
        // replace the old collinear merge that lost name boundaries.
        if (ctx.PlannedMerges.Count > 0)
        {
            ctx.PreserveNode = true;
        }
    }

    // --- Phase B + C: Create tangent nodes and arcs per pair ---
    // Each pair creates its own tangent nodes. Coincident tangent nodes on the same
    // edge (when multiple pairs want the same distance) are deduplicated by position.
    private static void PhaseBC_CreateTangentNodesAndArcs(FilletContext ctx, ref int nextNodeId)
    {
        foreach (var (edgeA, edgeB, radiusFt, turnAngleDeg, bearingA, bearingB, placementA, placementB) in ctx.PlannedArcs)
        {
            var tanNodeA = GetOrCreateTangentNode(ctx.Layout, ctx.EdgeTangentNodes, edgeA, placementA, ctx.Intersection, ref nextNodeId);
            var tanNodeB = GetOrCreateTangentNode(ctx.Layout, ctx.EdgeTangentNodes, edgeB, placementB, ctx.Intersection, ref nextNodeId);

            if (tanNodeA.Id == tanNodeB.Id)
            {
                Log.LogDebug(
                    "[Int#{IntId}] Phase BC: skipping arc on {TwyA}/{TwyB} (turn={Turn:F1}°, r={R:F0}ft) — tangent points deduped to same node #{NodeId}",
                    ctx.Intersection.Id,
                    edgeA.TaxiwayName,
                    edgeB.TaxiwayName,
                    turnAngleDeg,
                    radiusFt,
                    tanNodeA.Id
                );
                continue;
            }

            double bearingAToIntersection = placementA.BearingTowardIntersectionDeg ?? (bearingA + 180.0) % 360.0;
            double bearingBToIntersection = placementB.BearingTowardIntersectionDeg ?? (bearingB + 180.0) % 360.0;

            double effectiveTurnDeg = 180.0 - GeoMath.AbsBearingDifference(bearingAToIntersection, bearingBToIntersection);

            double sweepRad = effectiveTurnDeg * (Math.PI / 180.0);
            double kappa = (4.0 / 3.0) * Math.Tan(sweepRad / 4.0);

            double radiusNm = radiusFt / GeoMath.FeetPerNm;
            double depthA = kappa * radiusNm;
            double depthB = kappa * radiusNm;

            var (p1Lat, p1Lon) = GeoMath.ProjectPointRaw(tanNodeA.Position.Lat, tanNodeA.Position.Lon, bearingAToIntersection, depthA);
            var (p2Lat, p2Lon) = GeoMath.ProjectPointRaw(tanNodeB.Position.Lat, tanNodeB.Position.Lon, bearingBToIntersection, depthB);

            var bezier = new CubicBezier(
                tanNodeA.Position.Lat,
                tanNodeA.Position.Lon,
                p1Lat,
                p1Lon,
                p2Lat,
                p2Lon,
                tanNodeB.Position.Lat,
                tanNodeB.Position.Lon
            );

            double minRadiusFt = bezier.MinRadiusOfCurvatureFt(tanNodeA.Position.Lat, 10);
            double arcLengthNm = bezier.ArcLengthNm(20);
            bool sameTaxiway = edgeA.SharesTaxiway(edgeB);
            var arcProv = new CornerArcProvenance(ctx.Intersection.Id, edgeA.TaxiwayName, edgeB.TaxiwayName);

            ctx.Layout.Arcs.Add(
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
                    EdgeBearingAtNode0Deg = bearingAToIntersection,
                    EdgeBearingAtNode1Deg = bearingBToIntersection,
                    TurnAngleDeg = effectiveTurnDeg,
                    Origin = arcProv.DisplayString,
                    FilletProvenance = arcProv,
                }
            );
            ctx.ArcsCreated++;
        }
    }

    // --- Phase D1: Shorten edges that have tangent points ---
    // Per-pair tangents mean an edge can have multiple tangent nodes at different distances.
    // Sort by distance (farthest first = nearest to the far end), connect them with edge
    // segments, and shorten the original edge to the farthest tangent.
    private static void PhaseD1_ShortenEdges(FilletContext ctx)
    {
        foreach (var (edge, tangentEntries) in ctx.EdgeTangentNodes)
        {
            var otherNode = edge.OtherNode(ctx.Intersection);

            // Sort tangent nodes on this edge by distance from intersection (farthest first)
            var sorted = tangentEntries.OrderByDescending(t => t.Placement.TangentDistNm).ToList();

            // Consume walked-through edges from the farthest tangent (it walks the most)
            var farthest = sorted[0];

            if (farthest.Placement.LandsInManualArc)
            {
                // Manual-arc handling: chain edges stay intact except where tangent nodes
                // land. Each tangent's SplitEdge gets sliced where the tangent sits;
                // multiple tangents sharing a SplitEdge produce N+1 sub-edges ordered
                // along the original edge. Without this generalization only `farthest`
                // was sliced and nearer chain tangents ended up arc-only, leaving
                // RescueOrphanedTangentNodes to invent chain-side connectivity.
                var chainTangents = sorted.Where(t => t.Placement.LandsInManualArc && t.Placement.SplitEdge is not null).ToList();

                foreach (var grp in chainTangents.GroupBy(t => t.Placement.SplitEdge!))
                {
                    var splitEdge = grp.Key;
                    var splitNodeA = splitEdge.Nodes[0];
                    var splitNodeB = splitEdge.Nodes[1];
                    ctx.ConsumedEdges.Add(splitEdge);

                    // Order tangents by position along the edge starting from Nodes[0]
                    // so the sub-edges form a contiguous chain splitNodeA → t1 → … → tn → splitNodeB.
                    var ordered = grp.OrderBy(t => GeoMath.DistanceNm(splitNodeA.Position, t.Node.Position)).ToList();

                    var prev = splitNodeA;
                    foreach (var t in ordered)
                    {
                        if (t.Node.Id == prev.Id)
                        {
                            continue;
                        }
                        double dist = GeoMath.DistanceNm(prev.Position, t.Node.Position);
                        var splitProv = new FilletEdgeProvenance(ctx.Intersection.Id, FilletEdgeKind.ArcSplit, edge.TaxiwayName, prev.Id, t.Node.Id);
                        ctx.Layout.Edges.Add(
                            new GroundEdge
                            {
                                Nodes = [prev, t.Node],
                                TaxiwayName = edge.TaxiwayName,
                                DistanceNm = dist,
                                Origin = splitProv.DisplayString,
                                FilletProvenance = splitProv,
                            }
                        );
                        prev = t.Node;
                    }
                    if (prev.Id != splitNodeB.Id)
                    {
                        double finalDist = GeoMath.DistanceNm(prev.Position, splitNodeB.Position);
                        var finalProv = new FilletEdgeProvenance(ctx.Intersection.Id, FilletEdgeKind.ArcSplit, edge.TaxiwayName, prev.Id, splitNodeB.Id);
                        ctx.Layout.Edges.Add(
                            new GroundEdge
                            {
                                Nodes = [prev, splitNodeB],
                                TaxiwayName = edge.TaxiwayName,
                                DistanceNm = finalDist,
                                Origin = finalProv.DisplayString,
                                FilletProvenance = finalProv,
                            }
                        );
                    }
                }

                // Also consume any non-manual-arc walked edges before the chain
                foreach (var walkedEdge in farthest.Placement.WalkedEdges)
                {
                    ctx.ConsumedEdges.Add(walkedEdge);
                }
                ctx.DeferredShapeNodes.AddRange(farthest.Placement.WalkedShapeNodes);
                foreach (var ptNode in farthest.Placement.PassthroughNodes)
                {
                    double ptToTanDist = GeoMath.DistanceNm(ptNode.Position, farthest.Node.Position);
                    var ptProv = new FilletEdgeProvenance(ctx.Intersection.Id, FilletEdgeKind.Passthrough, edge.TaxiwayName, ptNode.Id, farthest.Node.Id);
                    ctx.Layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [ptNode, farthest.Node],
                            TaxiwayName = edge.TaxiwayName,
                            DistanceNm = ptToTanDist,
                            Origin = ptProv.DisplayString,
                            FilletProvenance = ptProv,
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
                        double shortenDist = GeoMath.DistanceNm(otherNode.Position, nearest.Node.Position);
                        var shortenProv = new FilletEdgeProvenance(ctx.Intersection.Id, FilletEdgeKind.Shorten, edge.TaxiwayName, otherNode.Id, nearest.Node.Id);
                        ctx.Layout.Edges.Add(
                            new GroundEdge
                            {
                                Nodes = [otherNode, nearest.Node],
                                TaxiwayName = edge.TaxiwayName,
                                DistanceNm = shortenDist,
                                Origin = shortenProv.DisplayString,
                                FilletProvenance = shortenProv,
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
                    ctx.ConsumedEdges.Add(walkedEdge);
                }
                ctx.DeferredShapeNodes.AddRange(farthest.Placement.WalkedShapeNodes);

                var farNode = farthest.Placement.WalkFarNode ?? otherNode;
                foreach (var ptNode in farthest.Placement.PassthroughNodes)
                {
                    double ptToTanDist = GeoMath.DistanceNm(ptNode.Position, farthest.Node.Position);
                    var ptToTanProv = new FilletEdgeProvenance(ctx.Intersection.Id, FilletEdgeKind.Passthrough, edge.TaxiwayName, ptNode.Id, farthest.Node.Id);
                    ctx.Layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [ptNode, farthest.Node],
                            TaxiwayName = edge.TaxiwayName,
                            DistanceNm = ptToTanDist,
                            Origin = ptToTanProv.DisplayString,
                            FilletProvenance = ptToTanProv,
                        }
                    );
                    double ptToFarDist = GeoMath.DistanceNm(ptNode.Position, farNode.Position);
                    var ptToFarProv = new FilletEdgeProvenance(ctx.Intersection.Id, FilletEdgeKind.Passthrough, edge.TaxiwayName, ptNode.Id, farNode.Id);
                    ctx.Layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [ptNode, farNode],
                            TaxiwayName = edge.TaxiwayName,
                            DistanceNm = ptToFarDist,
                            Origin = ptToFarProv.DisplayString,
                            FilletProvenance = ptToFarProv,
                        }
                    );
                }

                // Shortened edge: farNode ↔ farthest tangent
                if (farNode.Id != farthest.Node.Id)
                {
                    double shortenDist = GeoMath.DistanceNm(farNode.Position, farthest.Node.Position);
                    Log.LogDebug(
                        "[Int#{IntId}] Phase D shorten: {Twy} #{Far}↔#{Tan} ({Dist:F0}ft)",
                        ctx.Intersection.Id,
                        edge.TaxiwayName,
                        farNode.Id,
                        farthest.Node.Id,
                        shortenDist * GeoMath.FeetPerNm
                    );
                    var stdShortenProv = new FilletEdgeProvenance(ctx.Intersection.Id, FilletEdgeKind.Shorten, edge.TaxiwayName, farNode.Id, farthest.Node.Id);
                    ctx.Layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [farNode, farthest.Node],
                            TaxiwayName = edge.TaxiwayName,
                            DistanceNm = shortenDist,
                            Origin = stdShortenProv.DisplayString,
                            FilletProvenance = stdShortenProv,
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
                // Skip tangent-links across non-runway manual arc chains (the chain
                // edges provide connectivity). Runway tangent-links are fine — both
                // tangent nodes sit on the straight centerline.
                bool fromProtected = fromTan.Placement.LandsInManualArc && !edge.IsRunwayCenterline;
                bool toProtected = toTan.Placement.LandsInManualArc && !edge.IsRunwayCenterline;
                if (fromProtected || toProtected)
                {
                    continue;
                }
                double segDist = GeoMath.DistanceNm(fromTan.Node.Position, toTan.Node.Position);
                Log.LogDebug(
                    "[Int#{IntId}] Phase D tangent-link: {Twy} #{From}↔#{To} ({Dist:F0}ft)",
                    ctx.Intersection.Id,
                    edge.TaxiwayName,
                    fromTan.Node.Id,
                    toTan.Node.Id,
                    segDist * GeoMath.FeetPerNm
                );
                var linkProv = new FilletEdgeProvenance(ctx.Intersection.Id, FilletEdgeKind.TangentLink, edge.TaxiwayName, fromTan.Node.Id, toTan.Node.Id);
                ctx.Layout.Edges.Add(
                    new GroundEdge
                    {
                        Nodes = [fromTan.Node, toTan.Node],
                        TaxiwayName = edge.TaxiwayName,
                        DistanceNm = segDist,
                        Origin = linkProv.DisplayString,
                        FilletProvenance = linkProv,
                    }
                );
            }

            ctx.ConsumedEdges.Add(edge);
        }
    }

    // --- Phase D2: Merge collinear pairs ---
    // Skip when preserving the intersection node, because the preserve stubs handle
    // straight-through connectivity with correct taxiway names. In preserve mode, still
    // consume the original collinear edges so they don't become orphans.
    private static void PhaseD2_MergeCollinearPairs(FilletContext ctx)
    {
        var mergedEdges = new HashSet<GroundEdge>();
        if (ctx.PreserveNode)
        {
            foreach (var (edgeA, _, edgeB, _) in ctx.PlannedMerges)
            {
                ctx.ConsumedEdges.Add(edgeA);
                ctx.ConsumedEdges.Add(edgeB);
            }
        }
        else
        {
            // Track which edges have been used in a merge to avoid creating duplicate edges
            // when one edge participates in multiple collinear pairs (e.g., W collinear with both W1 and W2).
            foreach (var (edgeA, otherA, edgeB, otherB) in ctx.PlannedMerges)
            {
                if (mergedEdges.Contains(edgeA) || mergedEdges.Contains(edgeB))
                {
                    continue;
                }

                bool aHasTangent = ctx.EdgeTangentNodes.TryGetValue(edgeA, out var tanListA);
                bool bHasTangent = ctx.EdgeTangentNodes.TryGetValue(edgeB, out var tanListB);

                // Determine the effective endpoints after shortening — use the farthest tangent
                GroundNode endA = aHasTangent ? tanListA!.MaxBy(t => t.Placement.TangentDistNm).Node : otherA;
                GroundNode endB = bHasTangent ? tanListB!.MaxBy(t => t.Placement.TangentDistNm).Node : otherB;

                // Create the merged edge between the effective endpoints
                double mergedDist = GeoMath.DistanceNm(endA.Position, endB.Position);

                Log.LogDebug(
                    "[Int#{IntId}] Phase D collinear merge: {Tw} #{EndA}↔#{EndB} ({DistFt:F0}ft) [tangentA={HasA}, tangentB={HasB}]",
                    ctx.Intersection.Id,
                    edgeA.TaxiwayName,
                    endA.Id,
                    endB.Id,
                    mergedDist * GeoMath.FeetPerNm,
                    aHasTangent,
                    bHasTangent
                );

                var mergeProv = new FilletEdgeProvenance(ctx.Intersection.Id, FilletEdgeKind.Merge, edgeA.TaxiwayName, endA.Id, endB.Id);
                ctx.Layout.Edges.Add(
                    new GroundEdge
                    {
                        Nodes = [endA, endB],
                        TaxiwayName = edgeA.TaxiwayName,
                        DistanceNm = mergedDist,
                        Origin = mergeProv.DisplayString,
                        FilletProvenance = mergeProv,
                    }
                );

                ctx.ConsumedEdges.Add(edgeA);
                ctx.ConsumedEdges.Add(edgeB);
                mergedEdges.Add(edgeA);
                mergedEdges.Add(edgeB);
                ctx.EdgesMerged++;
            }
        }
    }

    // --- Phase D3: Reconnect orphaned edges ---
    // Edges not covered by tangent shortening or collinear merging (e.g., parking edges
    // to this intersection) are reconnected to the nearest tangent or merge endpoint.
    private static void PhaseD3_ReconnectOrphans(FilletContext ctx)
    {
        // Collect all candidate reconnection nodes: tangent points + merge endpoints
        var reconnectCandidates = new List<GroundNode>();
        foreach (var entries in ctx.EdgeTangentNodes.Values)
        {
            foreach (var (node, _) in entries)
            {
                reconnectCandidates.Add(node);
            }
        }
        foreach (var (_, otherA, _, otherB) in ctx.PlannedMerges)
        {
            reconnectCandidates.Add(otherA);
            reconnectCandidates.Add(otherB);
        }

        // Reconnect orphaned edges (e.g., parking edges to this intersection)
        foreach (var edge in ctx.Edges)
        {
            if (ctx.ConsumedEdges.Contains(edge))
            {
                continue;
            }

            var otherNode = edge.OtherNode(ctx.Intersection);
            GroundNode? bestTarget = FindNearestNode(otherNode, reconnectCandidates);
            if (bestTarget is not null)
            {
                double newDist = GeoMath.DistanceNm(otherNode.Position, bestTarget.Position);
                var reconnectProv = new FilletEdgeProvenance(ctx.Intersection.Id, FilletEdgeKind.Reconnect, edge.TaxiwayName);
                ctx.Layout.Edges.Add(
                    new GroundEdge
                    {
                        Nodes = [otherNode, bestTarget],
                        TaxiwayName = edge.TaxiwayName,
                        DistanceNm = newDist,
                        Origin = reconnectProv.DisplayString,
                        FilletProvenance = reconnectProv,
                    }
                );
            }
            else
            {
                Log.LogWarning(
                    "Fillet: orphaned edge {Taxiway} from node {NodeId} to filleted node {IntId} — no node to reconnect",
                    edge.TaxiwayName,
                    otherNode.Id,
                    ctx.Intersection.Id
                );
            }
            ctx.ConsumedEdges.Add(edge);
        }
    }

    // --- Phase D4: Remove consumed edges, deferred shape nodes, then preserve or remove intersection ---
    private static void PhaseD4_CleanupAndFinalize(FilletContext ctx)
    {
        // Remove all original edges at this intersection
        foreach (var ce in ctx.ConsumedEdges)
        {
            Log.LogDebug(
                "[Int#{IntId}] Consuming edge {Tw}({A}↔{B}) origin={Origin}",
                ctx.Intersection.Id,
                ce.TaxiwayName,
                ce.Nodes[0].Id,
                ce.Nodes[1].Id,
                ce.Origin
            );
        }
        ctx.Layout.Edges.RemoveAll(e => ctx.ConsumedEdges.Contains(e));

        // Remove walked shape nodes. Deferred until after consumedEdges cleanup so
        // we only remove nodes that truly have no remaining edges. Edges to surviving
        // neighbors (not consumed by the walk) are left intact.
        foreach (var shapeNode in ctx.DeferredShapeNodes)
        {
            var remainingEdges = ctx.Layout.Edges.Where(e => (e.Nodes[0].Id == shapeNode.Id) || (e.Nodes[1].Id == shapeNode.Id)).ToList();
            var remainingArcs = ctx.Layout.Arcs.Where(a => (a.Nodes[0].Id == shapeNode.Id) || (a.Nodes[1].Id == shapeNode.Id)).ToList();
            if ((remainingEdges.Count == 0) && (remainingArcs.Count == 0))
            {
                ctx.Layout.Nodes.Remove(shapeNode.Id);
                Log.LogDebug("[Int#{IntId}] Removed shape node #{NodeId} (no remaining edges)", ctx.Intersection.Id, shapeNode.Id);
            }
            else if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug(
                    "[Int#{IntId}] Kept shape node #{NodeId} ({Edges} edges, {Arcs} arcs surviving: {Detail})",
                    ctx.Intersection.Id,
                    shapeNode.Id,
                    remainingEdges.Count,
                    remainingArcs.Count,
                    string.Join(", ", remainingEdges.Select(e => $"{e.TaxiwayName}({e.Nodes[0].Id}↔{e.Nodes[1].Id})"))
                );
            }
        }

        Log.LogDebug("[Int#{IntId}] preserveNode={Preserve}", ctx.Intersection.Id, ctx.PreserveNode);

        if (ctx.PreserveNode)
        {
            // Preserve: keep the intersection node, connect to the nearest tangent on each edge.
            // When the tangent lands in a manual arc chain, connect to the edge's far node
            // instead — the chain provides connectivity from there to the tangent.
            foreach (var (edge, tangentEntries) in ctx.EdgeTangentNodes)
            {
                var nearest = tangentEntries.MinBy(t => t.Placement.TangentDistNm);
                var otherNode = edge.OtherNode(ctx.Intersection);
                double firstEdgeFt = GeoMath.DistanceNm(ctx.Intersection.Position, otherNode.Position) * GeoMath.FeetPerNm;
                double tangentFt = nearest.Placement.TangentDistNm * GeoMath.FeetPerNm;

                if (tangentFt <= firstEdgeFt)
                {
                    // Tangent fits on the first edge — connect directly to it
                    double stubDist = GeoMath.DistanceNm(ctx.Intersection.Position, nearest.Node.Position);
                    Log.LogDebug(
                        "[Int#{IntId}] Phase D preserve: {Twy} #{Int}→#{Target} (tangent on first edge at {Dist:F0}ft)",
                        ctx.Intersection.Id,
                        edge.TaxiwayName,
                        ctx.Intersection.Id,
                        nearest.Node.Id,
                        tangentFt
                    );
                    var preserveProv = new FilletEdgeProvenance(ctx.Intersection.Id, FilletEdgeKind.Preserve, edge.TaxiwayName);
                    ctx.Layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [ctx.Intersection, nearest.Node],
                            TaxiwayName = edge.TaxiwayName,
                            DistanceNm = stubDist,
                            Origin = preserveProv.DisplayString,
                            FilletProvenance = preserveProv,
                        }
                    );
                }
                else
                {
                    // Tangent is past shape-point nodes — connect to the first neighbor.
                    // The shape-point chain provides connectivity to the tangent node.
                    double neighborDist = GeoMath.DistanceNm(ctx.Intersection.Position, otherNode.Position);
                    Log.LogDebug(
                        "[Int#{IntId}] Phase D preserve: {Twy} #{Int}→#{Neighbor} (neighbor, tangent=#{Tan} at {Dist:F0}ft past firstEdge={EdgeFt:F0}ft)",
                        ctx.Intersection.Id,
                        edge.TaxiwayName,
                        ctx.Intersection.Id,
                        otherNode.Id,
                        nearest.Node.Id,
                        tangentFt,
                        firstEdgeFt
                    );
                    var preserveProv = new FilletEdgeProvenance(ctx.Intersection.Id, FilletEdgeKind.Preserve, edge.TaxiwayName);
                    ctx.Layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [ctx.Intersection, otherNode],
                            TaxiwayName = edge.TaxiwayName,
                            DistanceNm = neighborDist,
                            Origin = preserveProv.DisplayString,
                            FilletProvenance = preserveProv,
                        }
                    );
                }
            }

            // Also add stubs to collinear merge endpoints that don't have tangent points,
            // so the intersection stays connected through merged collinear paths.
            var collinearStubsCreated = new HashSet<int>();
            foreach (var (edgeA, otherA, edgeB, otherB) in ctx.PlannedMerges)
            {
                bool aHasTangent = ctx.EdgeTangentNodes.ContainsKey(edgeA);
                bool bHasTangent = ctx.EdgeTangentNodes.ContainsKey(edgeB);
                Log.LogDebug(
                    "[Int#{IntId}] Phase D collinear preserve: {TwyA}(→#{OtherA}) hasTangent={HasA}, {TwyB}(→#{OtherB}) hasTangent={HasB}",
                    ctx.Intersection.Id,
                    edgeA.TaxiwayName,
                    otherA.Id,
                    aHasTangent,
                    edgeB.TaxiwayName,
                    otherB.Id,
                    bHasTangent
                );
                if (!aHasTangent && collinearStubsCreated.Add(otherA.Id))
                {
                    double dist = GeoMath.DistanceNm(ctx.Intersection.Position, otherA.Position);
                    Log.LogDebug(
                        "[Int#{IntId}] Phase D collinear stub: {Twy} #{Int}→#{Other} ({Dist:F0}ft)",
                        ctx.Intersection.Id,
                        edgeA.TaxiwayName,
                        ctx.Intersection.Id,
                        otherA.Id,
                        dist * GeoMath.FeetPerNm
                    );
                    var stubProvA = new FilletEdgeProvenance(ctx.Intersection.Id, FilletEdgeKind.Preserve, edgeA.TaxiwayName);
                    ctx.Layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [ctx.Intersection, otherA],
                            TaxiwayName = edgeA.TaxiwayName,
                            DistanceNm = dist,
                            Origin = stubProvA.DisplayString,
                            FilletProvenance = stubProvA,
                        }
                    );
                }

                if (!bHasTangent && collinearStubsCreated.Add(otherB.Id))
                {
                    double dist = GeoMath.DistanceNm(ctx.Intersection.Position, otherB.Position);
                    Log.LogDebug(
                        "[Int#{IntId}] Phase D collinear stub: {Twy} #{Int}→#{Other} ({Dist:F0}ft)",
                        ctx.Intersection.Id,
                        edgeB.TaxiwayName,
                        ctx.Intersection.Id,
                        otherB.Id,
                        dist * GeoMath.FeetPerNm
                    );
                    var stubProvB = new FilletEdgeProvenance(ctx.Intersection.Id, FilletEdgeKind.Preserve, edgeB.TaxiwayName);
                    ctx.Layout.Edges.Add(
                        new GroundEdge
                        {
                            Nodes = [ctx.Intersection, otherB],
                            TaxiwayName = edgeB.TaxiwayName,
                            DistanceNm = dist,
                            Origin = stubProvB.DisplayString,
                            FilletProvenance = stubProvB,
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
            int intId = ctx.Intersection.Id;
            var danglingEdges = ctx.Layout.Edges.Where(e => (e.Nodes[0].Id == intId) || (e.Nodes[1].Id == intId)).ToList();
            var danglingArcs = ctx.Layout.Arcs.Where(a => (a.Nodes[0].Id == intId) || (a.Nodes[1].Id == intId)).ToList();
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
            int removedEdges = ctx.Layout.Edges.RemoveAll(e => (e.Nodes[0].Id == intId) || (e.Nodes[1].Id == intId));
            int removedArcs = ctx.Layout.Arcs.RemoveAll(a => (a.Nodes[0].Id == intId) || (a.Nodes[1].Id == intId));
            ctx.Layout.Nodes.Remove(intId);
            Log.LogDebug("[Int#{IntId}] Removed intersection node: {Edges} edges, {Arcs} arcs purged", intId, removedEdges, removedArcs);
        }
    }

    /// <summary>
    /// Global post-fillet pass: iteratively merge coincident TaxiwayIntersection nodes
    /// within 5ft. Adjusts bezier control points so curves stay smooth when endpoints
    /// move. Loops until no more merges occur (handles transitive chains).
    /// </summary>
    private static int MergeCoincidentNodes(AirportGroundLayout layout)
    {
        const int maxPasses = 5;
        int totalMerged = 0;

        int pass;
        for (pass = 0; pass < maxPasses; pass++)
        {
            var mergeMap = BuildMergeMap(layout, CoincidentNodeThresholdNm);
            if (mergeMap.Count == 0)
            {
                break;
            }

            Log.LogDebug("GlobalMerge pass {Pass}: {Count} merges", pass, mergeMap.Count);

            foreach (var (victimId, survivor) in mergeMap)
            {
                double distFt = GeoMath.DistanceNm(layout.Nodes[victimId].Position, survivor.Position) * GeoMath.FeetPerNm;
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
                bool translated = false;
                for (int k = 0; k < arc.Nodes.Length; k++)
                {
                    if (mergeMap.TryGetValue(arc.Nodes[k].Id, out var survivor))
                    {
                        var victim = arc.Nodes[k];
                        double dLat = survivor.Position.Lat - victim.Position.Lat;
                        double dLon = survivor.Position.Lon - victim.Position.Lon;

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
                        translated = true;
                    }
                }

                // Recompute MinRadiusOfCurvatureFt from the now-current geometry so
                // the degenerate-arc filter below uses fresh values rather than the
                // pre-merge radius.
                if (translated)
                {
                    var bezier = arc.ToBezier();
                    arc.MinRadiusOfCurvatureFt = bezier.MinRadiusOfCurvatureFt(arc.Nodes[0].Position.Lat, 10);
                }
            }

            // Remove self-loop edges (both endpoints are the same node after merge)
            layout.Edges.RemoveAll(e => e.Nodes[0].Id == e.Nodes[1].Id);

            // Remove degenerate arcs (both endpoints are the same node after merge,
            // or radius collapsed below usable threshold after control point translation)
            layout.Arcs.RemoveAll(a => (a.Nodes[0].Id == a.Nodes[1].Id) || (a.MinRadiusOfCurvatureFt < 5.0));

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

        if (pass == maxPasses)
        {
            // Cap hit before BuildMergeMap returned empty — graph may still contain
            // coincident nodes. Investigate the airport layout if this fires.
            int remaining = BuildMergeMap(layout, CoincidentNodeThresholdNm).Count;
            if (remaining > 0)
            {
                Log.LogWarning(
                    "MergeCoincidentNodes hit max-pass cap ({MaxPasses}); {Remaining} coincident-node merges still pending",
                    maxPasses,
                    remaining
                );
            }
        }

        // Final pass: recompute cached distances for all edges and arcs so they
        // reflect current node positions after any merges.
        if (totalMerged > 0)
        {
            RecomputeDistances(layout);

            // Clean up nodes orphaned by degenerate arc removal
            var orphanIds = layout
                .Nodes.Keys.Where(id =>
                    !layout.Edges.Any(e => (e.Nodes[0].Id == id) || (e.Nodes[1].Id == id))
                    && !layout.Arcs.Any(a => (a.Nodes[0].Id == id) || (a.Nodes[1].Id == id))
                )
                .ToList();
            foreach (int id in orphanIds)
            {
                var node = layout.Nodes[id];
                // Only remove fillet-created tangent nodes, not original graph nodes
                if (node.FilletProvenance is not null)
                {
                    layout.Nodes.Remove(id);
                }
            }
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
            edge.DistanceNm = GeoMath.DistanceNm(edge.Nodes[0].Position, edge.Nodes[1].Position);
        }

        foreach (var arc in layout.Arcs)
        {
            var bezier = arc.ToBezier();
            arc.MinRadiusOfCurvatureFt = bezier.MinRadiusOfCurvatureFt(arc.Nodes[0].Position.Lat, 10);
            arc.DistanceNm = bezier.ArcLengthNm(20);
        }
    }

    /// <summary>
    /// Remove preserve edges that are redundant because a shorten or tangent-link
    /// edge from another intersection already connects the same node pair with an
    /// intermediate tangent node in between. E.g., preserve 16→17 is redundant when
    /// shorten 16→683 exists and 683 is between 16 and 17 on the same taxiway.
    /// </summary>
    private static int RemoveRedundantPreserveEdges(AirportGroundLayout layout)
    {
        int removed = 0;
        var toRemove = new List<GroundEdge>();

        foreach (
            var preserve in layout
                .Edges.Where(e => e.FilletProvenance is FilletEdgeProvenance fep && fep.Kind == FilletEdgeKind.Preserve)
                .ToList()
        )
        {
            int fromId = preserve.Nodes[0].Id;
            int toId = preserve.Nodes[1].Id;

            // Check if there's another edge from the same node on the same taxiway
            // to a closer node in the same direction
            double preserveDist = preserve.DistanceNm;
            double preserveBearing = GeoMath.BearingTo(preserve.Nodes[0].Position, preserve.Nodes[1].Position);

            bool hasCloserEdge = layout.Edges.Any(e =>
            {
                if (e == preserve)
                {
                    return false;
                }

                if ((e.Nodes[0].Id != fromId) && (e.Nodes[1].Id != fromId))
                {
                    return false;
                }

                if (e.TaxiwayName != preserve.TaxiwayName)
                {
                    return false;
                }

                if (e.DistanceNm >= preserveDist)
                {
                    return false;
                }

                // Check same direction (within 30°)
                var other = e.Nodes[0].Id == fromId ? e.Nodes[1] : e.Nodes[0];
                double otherBearing = GeoMath.BearingTo(preserve.Nodes[0].Position, other.Position);
                return GeoMath.AbsBearingDifference(preserveBearing, otherBearing) < 30.0;
            });

            if (hasCloserEdge)
            {
                Log.LogDebug("Removing redundant preserve edge {Twy}(#{From}↔#{To}) — closer edge exists", preserve.TaxiwayName, fromId, toId);
                toRemove.Add(preserve);
                removed++;
            }
        }

        foreach (var e in toRemove)
        {
            layout.Edges.Remove(e);
        }

        return removed;
    }

    /// <summary>
    /// Post-fillet rescue: find tangent nodes that only have arc edges (no straight
    /// edges connecting them to the graph) and connect them to the nearest node that
    /// has straight-edge connectivity. This handles cases where a later intersection's
    /// Phase D consumed the edge that originally connected the tangent node.
    /// </summary>
    private static int RescueOrphanedTangentNodes(AirportGroundLayout layout)
    {
        int rescued = 0;

        foreach (var node in layout.Nodes.Values.ToList())
        {
            if (node.FilletProvenance is not TangentNodeProvenance)
            {
                continue;
            }

            // Check if this tangent node has any straight edges
            bool hasStraightEdge = layout.Edges.Any(e => (e.Nodes[0].Id == node.Id) || (e.Nodes[1].Id == node.Id));
            if (hasStraightEdge)
            {
                continue;
            }

            // Check it has at least one arc (otherwise it's a fully orphaned node
            // that will be cleaned up by the orphan pass)
            bool hasArc = layout.Arcs.Any(a => (a.Nodes[0].Id == node.Id) || (a.Nodes[1].Id == node.Id));
            if (!hasArc)
            {
                continue;
            }

            // Get the taxiway name from the arc — needed to scope the neighbor search
            string twyName = layout
                .Arcs.Where(a => (a.Nodes[0].Id == node.Id) || (a.Nodes[1].Id == node.Id))
                .Select(a => a.TaxiwayNames.FirstOrDefault(n => !n.StartsWith("RWY")) ?? a.TaxiwayName)
                .First();

            // Find the nearest node that shares the same taxiway. Without this filter,
            // the search could connect to parking spots, helipads, or nodes across a
            // runway, creating phantom cross-runway edges.
            double bestDist = double.MaxValue;
            GroundNode? bestNeighbor = null;
            foreach (var candidate in layout.Nodes.Values)
            {
                if (candidate.Id == node.Id)
                {
                    continue;
                }

                // Exclude parking/helipad — they're never valid fillet neighbors
                if (candidate.Type is GroundNodeType.Parking or GroundNodeType.Helipad)
                {
                    continue;
                }

                // Candidate must be on the same taxiway (connected via an edge or arc with twyName)
                bool onSameTaxiway =
                    layout.Edges.Any(e => ((e.Nodes[0].Id == candidate.Id) || (e.Nodes[1].Id == candidate.Id)) && e.MatchesTaxiway(twyName))
                    || layout.Arcs.Any(a => ((a.Nodes[0].Id == candidate.Id) || (a.Nodes[1].Id == candidate.Id)) && a.MatchesTaxiway(twyName));
                if (!onSameTaxiway)
                {
                    continue;
                }

                double dist = GeoMath.DistanceNm(node.Position, candidate.Position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestNeighbor = candidate;
                }
            }

            if (bestNeighbor is null)
            {
                continue;
            }

            // RescueOrphan has no intersection scope — pass 0 as a placeholder.
            var rescueProv = new FilletEdgeProvenance(0, FilletEdgeKind.RescueOrphan, twyName, node.Id, bestNeighbor.Id);
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [node, bestNeighbor],
                    TaxiwayName = twyName,
                    DistanceNm = bestDist,
                    Origin = rescueProv.DisplayString,
                    FilletProvenance = rescueProv,
                }
            );

            Log.LogDebug(
                "Rescued orphaned tangent node #{Id} → #{Neighbor} ({Dist:F0}ft) via {Twy}",
                node.Id,
                bestNeighbor.Id,
                bestDist * GeoMath.FeetPerNm,
                twyName
            );
            rescued++;
        }

        return rescued;
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

                double dist = GeoMath.DistanceNm(candidates[i].Position, candidates[j].Position);

                if (dist <= thresholdNm)
                {
                    // Don't merge runway-centerline tangent nodes with taxiway tangent
                    // nodes — they're close but intentionally at different positions.
                    // Merging pulls runway edges off the centerline.
                    bool iOnRunway = HasRunwayEdge(layout, candidates[i].Id);
                    bool jOnRunway = HasRunwayEdge(layout, candidates[j].Id);
                    if (iOnRunway != jOnRunway)
                    {
                        continue;
                    }

                    mergeMap[candidates[j].Id] = candidates[i];
                }
            }
        }

        return mergeMap;
    }

    private static bool HasRunwayEdge(AirportGroundLayout layout, int nodeId)
    {
        foreach (var edge in layout.Edges)
        {
            if (edge.IsRunwayCenterline && ((edge.Nodes[0].Id == nodeId) || (edge.Nodes[1].Id == nodeId)))
            {
                return true;
            }
        }
        foreach (var arc in layout.Arcs)
        {
            if (arc.IsRunwayCenterline && ((arc.Nodes[0].Id == nodeId) || (arc.Nodes[1].Id == nodeId)))
            {
                return true;
            }
        }
        return false;
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
            (lat, lon) = GeoMath.ProjectPointRaw(intersection.Position.Lat, intersection.Position.Lon, bearing, tangentDistNm);
            bearingAtTangent = null;
            walkedEdges = [];
            walkedShapeNodes = [];
            passthroughNodes = [];
            walkFarNode = null;
            // Runway centerline edges are protected — tangent splits the edge
            // instead of Phase D creating tangent-links/shorten edges.
            landsInManualArc = edge.IsRunwayCenterline;
            splitEdge = landsInManualArc ? edge : null;
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
            double dist = GeoMath.DistanceNm(target.Position, candidate.Position);
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
                return GeoMath.BearingTo(intersection.Position, new LatLon(pt.Lat, pt.Lon));
            }
            else
            {
                var pt = edge.IntermediatePoints[^1];
                return GeoMath.BearingTo(intersection.Position, new LatLon(pt.Lat, pt.Lon));
            }
        }

        return GeoMath.BearingTo(intersection.Position, otherNode.Position);
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
        if (edgeTangentNodes.TryGetValue(edge, out var existing))
        {
            foreach (var (node, _) in existing)
            {
                double dist = GeoMath.DistanceNm(node.Position, new LatLon(placement.Lat, placement.Lon));
                if (dist <= CoincidentNodeThresholdNm)
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
        var provenance = new TangentNodeProvenance(intersection.Id, edge.TaxiwayName, otherNode.Id);
        var newNode = new GroundNode
        {
            Id = id,
            Position = new LatLon(placement.Lat, placement.Lon),
            Type = GroundNodeType.TaxiwayIntersection,
            SourceIntersectionPosition = (intersection.Position.Lat, intersection.Position.Lon),
            Origin = provenance.DisplayString,
            FilletProvenance = provenance,
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
    /// Find the cumulative distance to the first walk step whose far node has other
    /// taxiways AND is far enough to be a meaningful cap. Returns MaxValue if no such
    /// step exists. Used to prevent tangent points from extending past the next
    /// taxiway intersection along the walk. Steps closer than MaxTangentDistFt are
    /// skipped — capping at very close neighbors changes the edge consumption pattern
    /// and breaks downstream fillet processing at those neighbors.
    /// </summary>
    private static double DistToFirstIntersectionFt(TaxiwayWalkResult walk)
    {
        foreach (var step in walk.Steps)
        {
            if (step.HasOtherTaxiways && (step.CumulativeDistFt >= MaxTangentDistFt))
            {
                return step.CumulativeDistFt;
            }
        }

        return double.MaxValue;
    }

    /// <summary>
    /// Walk along a taxiway chain from an intersection, following same-taxiway edges
    /// through shape-point nodes. Stops at real junctions (multiple same-taxiway
    /// continuations), non-intersection nodes, or dead ends.
    /// </summary>
    private static TaxiwayWalkResult WalkTaxiway(GroundEdge startEdge, GroundNode intersection, HashSet<int> manualArcNodes)
    {
        var otherNode = startEdge.OtherNode(intersection);
        double firstEdgeFt = GeoMath.DistanceNm(intersection.Position, otherNode.Position) * GeoMath.FeetPerNm;

        bool hasOtherTw = otherNode.Edges.Any(e => (e is GroundEdge ge) && (ge.TaxiwayName != startEdge.TaxiwayName));
        bool isProtected = manualArcNodes.Contains(otherNode.Id) || startEdge.IsRunwayCenterline;
        var steps = new List<TaxiwayWalkStep> { new(startEdge, otherNode, firstEdgeFt, hasOtherTw, isProtected) };

        // Cycle guard: a closed taxiway loop (rare but possible at tight ramps) could
        // re-enter a node via a different edge than the one we came in on. Visited-set
        // protection terminates the walk cleanly instead of looping forever.
        var visited = new HashSet<int> { intersection.Id, otherNode.Id };
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
            if (!visited.Add(nextNode.Id))
            {
                Log.LogDebug(
                    "WalkTaxiway: cycle detected on {Twy} at #{Node} (revisit) — terminating walk",
                    startEdge.TaxiwayName,
                    nextNode.Id
                );
                break;
            }
            double edgeFt = GeoMath.DistanceNm(currentNode.Position, nextNode.Position) * GeoMath.FeetPerNm;
            cumDist += edgeFt;

            bool nextHasOtherTw = nextNode.Edges.Any(e => (e is GroundEdge ge) && (ge.TaxiwayName != startEdge.TaxiwayName));
            bool inManualArcSet = manualArcNodes.Contains(nextNode.Id);
            bool isRwyContinuation = continuation.IsRunwayCenterline;
            bool nextIsProtected = inManualArcSet || isRwyContinuation;
            if (nextIsProtected)
            {
                Log.LogDebug(
                    "  Walk step to #{Node}: IsManualArc=true (inManualArcSet={ManualArc}, isRwyCenterline={Rwy}, edge={Twy}({A}↔{B}))",
                    nextNode.Id,
                    inManualArcSet,
                    isRwyContinuation,
                    continuation.TaxiwayName,
                    continuation.Nodes[0].Id,
                    continuation.Nodes[1].Id
                );
            }
            steps.Add(new TaxiwayWalkStep(continuation, nextNode, cumDist, nextHasOtherTw, nextIsProtected));

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
                Log.LogDebug(
                    "  InterpolateAlongWalk step {I}: IsManualArc=true, farNode=#{Far}, edge={Twy}({A}↔{B})",
                    i,
                    step.FarNode.Id,
                    step.Edge.TaxiwayName,
                    step.Edge.Nodes[0].Id,
                    step.Edge.Nodes[1].Id
                );
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

                double lat = fromNode.Position.Lat + (fraction * (toNode.Position.Lat - fromNode.Position.Lat));
                double lon = fromNode.Position.Lon + (fraction * (toNode.Position.Lon - fromNode.Position.Lon));

                double bearingToward = GeoMath.BearingTo(toNode.Position, fromNode.Position);

                // If the tangent lands inside a manual arc, record the edge being split
                // so Phase D can split it instead of creating spanning edges.
                if (enteredManualArc)
                {
                    splitEdge = step.Edge;
                }

                Log.LogDebug(
                    "  InterpolateAlongWalk: landed at ({Lat:F6},{Lon:F6}) step={Step} toNode=#{To} enteredManualArc={Arc} splitEdge={Split}",
                    lat,
                    lon,
                    i,
                    toNode.Id,
                    enteredManualArc,
                    splitEdge is not null ? $"{splitEdge.TaxiwayName}({splitEdge.Nodes[0].Id}↔{splitEdge.Nodes[1].Id})" : "none"
                );
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
        double termBearing = GeoMath.BearingTo(terminal.Position, prevNode.Position);
        return (terminal.Position.Lat, terminal.Position.Lon, termBearing, consumed, shapeNodes, passthrough, terminal, enteredManualArc, splitEdge);
    }
}
