namespace Yaat.Sim.Data.Airport.Fillet.V2;

/// <summary>Union-find survivor map for <see cref="TangentMergeOp"/>; rewrites cut-id references across the plan.</summary>
internal static class FilletPlanCutRedirect
{
    /// <summary>Pre-fillet intersection nodes that may absorb a coincident V2 cut (never hold-shorts).</summary>
    public static bool IsStableAnchorTarget(GroundNode node) =>
        (node.Type == GroundNodeType.TaxiwayIntersection)
        && (node.Origin?.StartsWith("RunwayCrossing:centerline-projection", StringComparison.Ordinal) != true);

    /// <summary>
    /// After tangent merges, redirect each surviving cut that lands on a pre-fillet stable intersection to that node id
    /// so the executor does not materialize a duplicate tangent.
    /// </summary>
    public static void ExtendWithStableAnchors(
        Dictionary<int, int> redirect,
        IReadOnlyDictionary<int, ResolvedArmCut> cuts,
        IReadOnlyDictionary<int, GroundNode> preFilletStableNodes,
        double thresholdFt
    )
    {
        foreach (var (cutId, cut) in cuts)
        {
            if (redirect.TryGetValue(cutId, out int survivor) && (survivor != cutId))
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

            foreach (var key in redirect.Keys.ToList())
            {
                if (redirect[key] == cutId)
                {
                    redirect[key] = anchorId;
                }
            }

            redirect[cutId] = anchorId;
            if (!redirect.ContainsKey(anchorId))
            {
                redirect[anchorId] = anchorId;
            }
        }
    }

    public static HashSet<int> CollectReferencedCutIds(FilletPlan plan)
    {
        var ids = new HashSet<int>();
        foreach (var op in plan.ArmChainEdges)
        {
            if (op.FromCutId is int from)
            {
                ids.Add(from);
            }

            if (op.ToCutId is int to)
            {
                ids.Add(to);
            }
        }

        foreach (var op in plan.CornerArcs)
        {
            ids.Add(op.CutIdAtArmA);
            ids.Add(op.CutIdAtArmB);
        }

        foreach (var op in plan.StraightConnectors)
        {
            ids.Add(op.CutIdAtArmA);
            ids.Add(op.CutIdAtArmB);
        }

        foreach (var op in plan.ReconnectEdges)
        {
            if (op.TargetCutId is int target)
            {
                ids.Add(target);
            }
        }

        foreach (var op in plan.PreserveStubs)
        {
            ids.Add(op.CutId);
        }

        return ids;
    }

    public static Dictionary<int, int> BuildSurvivorMap(IReadOnlyList<TangentMergeOp> merges)
    {
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            if (!parent.TryGetValue(x, out int p))
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

        void Union(int a, int b)
        {
            int ra = Find(a);
            int rb = Find(b);
            int survivor = Math.Min(ra, rb);
            int child = Math.Max(ra, rb);
            parent[child] = survivor;
        }

        foreach (var m in merges)
        {
            Union(m.CutIdA, m.CutIdB);
        }

        var redirect = new Dictionary<int, int>();
        foreach (var id in parent.Keys)
        {
            redirect[id] = Find(id);
        }

        return redirect;
    }

    public static int Resolve(int cutId, IReadOnlyDictionary<int, int> redirect) => redirect.TryGetValue(cutId, out int survivor) ? survivor : cutId;

    public static Dictionary<int, ResolvedArmCut> PruneCuts(IReadOnlyDictionary<int, ResolvedArmCut> cuts, IReadOnlyDictionary<int, int> redirect) =>
        cuts.Where(kv => Resolve(kv.Key, redirect) == kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value with { CutId = kv.Key });

    public static IReadOnlyList<CornerArcOp> RedirectCornerArcs(IReadOnlyList<CornerArcOp> ops, IReadOnlyDictionary<int, int> redirect) =>
        ops.Select(o => new CornerArcOp(o.JunctionNodeId, o.CornerId, Resolve(o.CutIdAtArmA, redirect), Resolve(o.CutIdAtArmB, redirect)))
            .Where(o => o.CutIdAtArmA != o.CutIdAtArmB)
            .ToList();

    public static IReadOnlyList<StraightConnectorOp> RedirectStraightConnectors(
        IReadOnlyList<StraightConnectorOp> ops,
        IReadOnlyDictionary<int, int> redirect
    ) =>
        ops.Select(o => new StraightConnectorOp(
                o.JunctionNodeId,
                o.CornerId,
                Resolve(o.CutIdAtArmA, redirect),
                Resolve(o.CutIdAtArmB, redirect),
                o.TaxiwayName
            ))
            .Where(o => o.CutIdAtArmA != o.CutIdAtArmB)
            .ToList();

    public static IReadOnlyList<ArmChainEdgeOp> RedirectArmChainEdges(IReadOnlyList<ArmChainEdgeOp> ops, IReadOnlyDictionary<int, int> redirect) =>
        ops.Select(o => new ArmChainEdgeOp(
                o.JunctionNodeId,
                o.ArmId,
                o.FromCutId is int fc ? Resolve(fc, redirect) : null,
                o.ToCutId is int tc ? Resolve(tc, redirect) : null,
                o.TerminalNodeId,
                o.FromStableNodeId,
                o.TaxiwayName,
                o.IsRunwayCenterline
            ))
            .Where(o => o.FromCutId is null || o.ToCutId is null || o.FromCutId != o.ToCutId)
            .ToList();

    public static IReadOnlyList<ReconnectEdgeOp> RedirectReconnectEdges(IReadOnlyList<ReconnectEdgeOp> ops, IReadOnlyDictionary<int, int> redirect) =>
        ops.Select(o =>
            {
                int? target = o.TargetCutId is int id ? Resolve(id, redirect) : null;
                return o with { TargetCutId = target };
            })
            .ToList();
}
