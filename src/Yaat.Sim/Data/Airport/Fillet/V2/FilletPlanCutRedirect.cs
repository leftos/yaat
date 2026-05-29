namespace Yaat.Sim.Data.Airport.Fillet.V2;

/// <summary>Union-find survivor map for <see cref="TangentMergeOp"/>; rewrites cut-id references across the plan.</summary>
internal static class FilletPlanCutRedirect
{
    /// <summary>
    /// Pre-fillet nodes that may absorb a coincident V2 cut. Includes TaxiwayIntersection,
    /// Spot, Parking, Helipad, and RunwayHoldShort — all types that survive the fillet pass
    /// unchanged and are therefore valid redirect targets. Centerline-projection nodes are
    /// excluded because they are synthetically inserted and not part of the source topology.
    /// </summary>
    public static bool IsStableAnchorTarget(GroundNode node) =>
        (
            node.Type
            is GroundNodeType.TaxiwayIntersection
                or GroundNodeType.Spot
                or GroundNodeType.Parking
                or GroundNodeType.Helipad
                or GroundNodeType.RunwayHoldShort
        ) && (node.Origin?.StartsWith("RunwayCrossing:centerline-projection", StringComparison.Ordinal) != true);

    /// <summary>
    /// After tangent merges, redirect each surviving cut that lands on a pre-fillet stable intersection to that node id
    /// so the executor does not materialize a duplicate tangent.
    /// Returns the set of pre-existing node IDs that were actually used as redirect targets.
    /// The caller must pass this set to the executor so it can resolve these IDs via layout.Nodes
    /// rather than via the cut-node map.
    /// </summary>
    public static HashSet<int> ExtendWithStableAnchors(
        Dictionary<CutId, FilletEndpoint> redirect,
        IReadOnlyDictionary<CutId, ResolvedArmCut> cuts,
        IReadOnlyDictionary<int, GroundNode> preFilletStableNodes,
        double thresholdFt
    )
    {
        var usedAnchorIds = new HashSet<int>();

        foreach (var (cutId, cut) in cuts)
        {
            // Skip cuts that are already redirected away (they're merged into another cut).
            if (redirect.TryGetValue(cutId, out var existing) && (existing is FilletEndpoint.Cut c) && (c.Id != cutId))
            {
                continue;
            }

            int? bestAnchorId = null;
            double bestDistFt = double.MaxValue;
            foreach (var (candidateId, candidateNode) in preFilletStableNodes)
            {
                double distFt = GeoMath.DistanceNm(cut.Position, candidateNode.Position) * GeoMath.FeetPerNm;
                if ((distFt > thresholdFt) || (distFt >= bestDistFt))
                {
                    continue;
                }

                bestDistFt = distFt;
                bestAnchorId = candidateId;
            }

            if (bestAnchorId is not int anchorId)
            {
                continue;
            }

            var anchorEndpoint = new FilletEndpoint.Node(anchorId);

            // Repoint every cut that was pointing at this cutId to the anchor node instead.
            foreach (var key in redirect.Keys.ToList())
            {
                if ((redirect[key] is FilletEndpoint.Cut fc) && (fc.Id == cutId))
                {
                    redirect[key] = anchorEndpoint;
                }
            }

            redirect[cutId] = anchorEndpoint;
            // Ensure the anchor itself is in the redirect map so Resolve can find it.
            if (!redirect.ContainsKey(new CutId(anchorId)))
            {
                redirect[new CutId(anchorId)] = anchorEndpoint;
            }

            usedAnchorIds.Add(anchorId);
        }

        return usedAnchorIds;
    }

    /// <summary>
    /// Builds the initial survivor map from tangent merges (union-find over CutIds only).
    /// Each entry maps a child CutId to its surviving <see cref="FilletEndpoint.Cut"/> root.
    /// <see cref="ExtendWithStableAnchors"/> may later overwrite entries with <see cref="FilletEndpoint.Node"/>.
    /// </summary>
    public static Dictionary<CutId, FilletEndpoint> BuildSurvivorMap(IReadOnlyList<TangentMergeOp> merges)
    {
        var parent = new Dictionary<CutId, CutId>();

        CutId Find(CutId x)
        {
            if (!parent.TryGetValue(x, out var p))
            {
                parent[x] = x;
                return x;
            }

            if (p != x)
            {
                parent[x] = Find(p);
            }

            return parent[x];
        }

        void Union(CutId a, CutId b)
        {
            var ra = Find(a);
            var rb = Find(b);
            // Pick the lower integer value as survivor to match original Math.Min(ra, rb) semantics.
            var survivor = ra.Value <= rb.Value ? ra : rb;
            var child = ra.Value <= rb.Value ? rb : ra;
            parent[child] = survivor;
        }

        foreach (var m in merges)
        {
            Union(m.CutIdA, m.CutIdB);
        }

        var redirect = new Dictionary<CutId, FilletEndpoint>();
        foreach (var id in parent.Keys)
        {
            redirect[id] = new FilletEndpoint.Cut(Find(id));
        }

        return redirect;
    }

    /// <summary>
    /// Resolves a CutId through the redirect map to a <see cref="FilletEndpoint"/>.
    /// If not in the map the CutId survives as-is (no merge, no anchor).
    /// </summary>
    public static FilletEndpoint Resolve(CutId cutId, IReadOnlyDictionary<CutId, FilletEndpoint> redirect) =>
        redirect.TryGetValue(cutId, out var ep) ? ep : new FilletEndpoint.Cut(cutId);

    public static Dictionary<CutId, ResolvedArmCut> PruneCuts(
        IReadOnlyDictionary<CutId, ResolvedArmCut> cuts,
        IReadOnlyDictionary<CutId, FilletEndpoint> redirect
    ) =>
        cuts.Where(kv => Resolve(kv.Key, redirect) is FilletEndpoint.Cut survivor && survivor.Id == kv.Key)
            .ToDictionary(kv => kv.Key, kv => kv.Value with { CutId = kv.Key });

    public static IReadOnlyList<CornerArcOp> RedirectCornerArcs(IReadOnlyList<CornerArcOp> ops, IReadOnlyDictionary<CutId, FilletEndpoint> redirect)
    {
        var result = new List<CornerArcOp>();
        foreach (var o in ops)
        {
            FilletEndpoint epA = ResolveEndpoint(o.EndpointAtArmA, redirect);
            FilletEndpoint epB = ResolveEndpoint(o.EndpointAtArmB, redirect);

            // Drop arcs whose two endpoints resolved to the same node/cut.
            if (EndpointsEqual(epA, epB))
            {
                continue;
            }

            result.Add(new CornerArcOp(o.JunctionNodeId, o.CornerId, epA, epB));
        }

        return result;
    }

    public static IReadOnlyList<StraightConnectorOp> RedirectStraightConnectors(
        IReadOnlyList<StraightConnectorOp> ops,
        IReadOnlyDictionary<CutId, FilletEndpoint> redirect
    )
    {
        var result = new List<StraightConnectorOp>();
        foreach (var o in ops)
        {
            FilletEndpoint epA = ResolveEndpoint(o.EndpointAtArmA, redirect);
            FilletEndpoint epB = ResolveEndpoint(o.EndpointAtArmB, redirect);

            // Drop connectors whose two endpoints resolved to the same node/cut.
            if (EndpointsEqual(epA, epB))
            {
                continue;
            }

            result.Add(new StraightConnectorOp(o.JunctionNodeId, o.CornerId, epA, epB, o.TaxiwayName));
        }

        return result;
    }

    /// <summary>
    /// Resolves a <see cref="FilletEndpoint"/> through the redirect map.
    /// A <see cref="FilletEndpoint.Cut"/> is looked up; a <see cref="FilletEndpoint.Node"/> is stable and passes through.
    /// </summary>
    private static FilletEndpoint ResolveEndpoint(FilletEndpoint ep, IReadOnlyDictionary<CutId, FilletEndpoint> redirect) =>
        ep switch
        {
            FilletEndpoint.Cut cut => Resolve(cut.Id, redirect),
            FilletEndpoint.Node node => node,
            _ => throw new InvalidOperationException($"Unknown FilletEndpoint subtype: {ep.GetType().Name}"),
        };

    private static bool EndpointsEqual(FilletEndpoint a, FilletEndpoint b) =>
        (a, b) switch
        {
            (FilletEndpoint.Cut ca, FilletEndpoint.Cut cb) => ca.Id == cb.Id,
            (FilletEndpoint.Node na, FilletEndpoint.Node nb) => na.NodeId == nb.NodeId,
            _ => false,
        };
}
