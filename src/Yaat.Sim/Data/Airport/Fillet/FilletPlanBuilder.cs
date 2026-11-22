using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Fillet;

internal static class FilletPlanBuilder
{
    private static readonly ILogger Log = SimLog.CreateLogger("FilletPlanBuilder");

    public static FilletPlan Build(
        AirportGroundLayout layout,
        IReadOnlyList<JunctionPlan> junctions,
        IReadOnlyList<ArmCutResolver.JunctionCutResult> results,
        FilletReportCollector report
    )
    {
        var cuts = new Dictionary<CutId, ResolvedArmCut>();
        var merges = new List<TangentMergeOp>();
        var cornerArcs = new List<CornerArcOp>();
        var straightConnectors = new List<StraightConnectorOp>();
        var warnings = new List<PlanWarning>();
        var nodesToRemove = new List<int>();

        for (int i = 0; i < junctions.Count; i++)
        {
            JunctionPlan jp = junctions[i];
            ArmCutResolver.JunctionCutResult r = results[i];

            foreach ((CutId id, ResolvedArmCut? cut) in r.Cuts)
            {
                cuts[id] = cut;
            }

            merges.AddRange(r.TangentMerges);
            cornerArcs.AddRange(r.CornerArcs);
            straightConnectors.AddRange(r.StraightConnectors);
            warnings.AddRange(r.Warnings);

            if ((!jp.PreserveNode) && (jp.CollinearPairs.Count == 0) && ((r.CornerArcs.Count > 0) || (r.Cuts.Count > 0)))
            {
                nodesToRemove.Add(jp.JunctionNodeId);
            }
        }

        var refusals = new SharedArmTangentPass.CutRefusals();
        var sizingOnly = results.SelectMany(r => r.SizingOnlyCuts).ToDictionary(kv => kv.Key, kv => kv.Value);
        merges.AddRange(SharedArmTangentPass.ApplyCrossJunction(junctions, cuts, sizingOnly, warnings, refusals));
        List<CornerArcOp> beforeRefusal = cornerArcs;
        cornerArcs = [.. beforeRefusal.Where(op => !UsesAnyCut(op, refusals.Forced))];
        report.CornerOpsDropped(beforeRefusal, cornerArcs, FilletSkipReason.SharedArmClearGap);
        foreach (CutId overlapping in refusals.PinnedOverlapping)
        {
            RefusePin(cuts[overlapping], FilletSkipReason.PinOverlapsNeighbour, report);
        }

        // Global coincident-cut coalesce runs last so it sees the scaled positions from
        // ApplyCrossJunction. It absorbs every remaining cut-vs-cut coincidence — most importantly
        // adjacent junctions' tangent cuts on a shared taxiway — so the executor materializes no
        // coincident tangent nodes and the post-execute normalizer has none to merge.
        merges.AddRange(SharedArmTangentPass.ApplyGlobalCoincidentCutCoalesce(cuts, warnings));
        RefusePinnedMerges(merges, cuts, report);

        HashSet<CutId> pinnedCuts = [.. cuts.Values.Where(c => c.Pinned).Select(c => c.CutId)];
        Dictionary<CutId, FilletEndpoint> redirect = FilletPlanCutRedirect.BuildSurvivorMap(merges, pinnedCuts);
        var preFilletStableNodes = layout
            .Nodes.Where(kv => FilletPlanCutRedirect.IsStableAnchorTarget(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        HashSet<int> stableAnchorNodeIds = FilletPlanCutRedirect.ExtendWithStableAnchors(
            redirect,
            cuts,
            preFilletStableNodes,
            FilletConstants.CoincidentNodeThresholdFt
        );
        Dictionary<CutId, ResolvedArmCut> prunedCuts = FilletPlanCutRedirect.PruneCuts(cuts, redirect);
        var redirectedCornerArcs = FilletPlanCutRedirect.RedirectCornerArcs(cornerArcs, redirect).ToList();
        report.CornerOpsDropped(cornerArcs, redirectedCornerArcs, FilletSkipReason.CoincidentCuts);
        var redirectedStraightConnectors = FilletPlanCutRedirect.RedirectStraightConnectors(straightConnectors, redirect).ToList();

        // The cross-arm tangent merge collapses coincident cross-arm cuts onto one survivor, so
        // several corners (e.g. A/A and A/A8, or A/RAMP twice) now resolve to the SAME endpoint
        // pair. Keep one op per pair — preferring the single-name corner (requirement ①) — so the
        // executor emits exactly one arc per node pair and the post-execute normalizer has no
        // coincident nodes to merge.
        redirectedCornerArcs = DedupCornerArcs(redirectedCornerArcs, junctions, report);
        redirectedStraightConnectors = DedupByEndpointPair(redirectedStraightConnectors, op => (op.EndpointAtArmA, op.EndpointAtArmB), _ => 0);

        var nodesToRemoveSet = nodesToRemove.ToHashSet();
        FilletEdgeSplitPlanner.Result split = FilletEdgeSplitPlanner.Plan(junctions, prunedCuts, redirect, nodesToRemoveSet);
        warnings.AddRange(split.Warnings);

        // Use the anchor node IDs returned directly by ExtendWithStableAnchors rather than
        // inferring them from arc ops post-hoc. The inferred approach was broken: when an
        // anchor node ID coincides with a surviving cut ID, the inference incorrectly removed
        // the anchor from the set, causing the executor to resolve via cutNode instead of
        // layout.Nodes and pick up the wrong tangent-cut from a different junction.
        HashSet<int> stableAnchoredEndpoints = stableAnchorNodeIds;

        var built = new FilletPlan(
            prunedCuts,
            merges,
            redirectedCornerArcs,
            redirectedStraightConnectors,
            split.SurvivingEdges,
            nodesToRemove,
            split.ConsumedEdges,
            warnings,
            stableAnchoredEndpoints
        );
        FilletPlanConsistency.ValidateCutReferences(built);
        FilletPlanConsistency.ValidateNodeReferences(built);
        return built;
    }

    /// <summary>
    /// Keeps one corner arc per endpoint pair, the one of the corner that holds the pair most strongly (<see cref="DedupRank"/>),
    /// and records each other arc of the pair as dropped (<see cref="FilletSkipReason.Deduplicated"/>). A forced corner never
    /// wins a pair from an unforced one, so forcing leaves the other corner's arc alone.
    /// </summary>
    internal static List<CornerArcOp> DedupCornerArcs(List<CornerArcOp> ops, IReadOnlyList<JunctionPlan> junctions, FilletReportCollector report)
    {
        List<CornerArcOp> kept = DedupByEndpointPair(ops, op => (op.EndpointAtArmA, op.EndpointAtArmB), op => DedupRank(CornerOf(op, junctions)));
        report.CornerOpsDropped(ops, kept, FilletSkipReason.Deduplicated);
        return kept;
    }

    /// <summary>
    /// Keep one op per unordered resolved-endpoint pair, preserving original order. When two ops
    /// share a pair, the one of higher <paramref name="rank"/> (e.g. a single-name corner) wins;
    /// otherwise the first-seen op is kept.
    /// </summary>
    private static List<T> DedupByEndpointPair<T>(List<T> ops, Func<T, (FilletEndpoint A, FilletEndpoint B)> endpoints, Func<T, int> rank)
    {
        var chosenIndex = new Dictionary<((int, int) A, (int, int) B), int>();
        for (int i = 0; i < ops.Count; i++)
        {
            ((int, int) A, (int, int) B) key = PairKey(endpoints(ops[i]));
            if (!chosenIndex.TryGetValue(key, out int existing))
            {
                chosenIndex[key] = i;
            }
            else if (rank(ops[i]) > rank(ops[existing]))
            {
                chosenIndex[key] = i;
            }
        }

        var keep = chosenIndex.Values.ToHashSet();
        var result = new List<T>(keep.Count);
        for (int i = 0; i < ops.Count; i++)
        {
            if (keep.Contains(i))
            {
                result.Add(ops[i]);
            }
        }

        return result;
    }

    private static ((int, int) A, (int, int) B) PairKey((FilletEndpoint A, FilletEndpoint B) ep)
    {
        (int Kind, int Id) a = Token(ep.A);
        (int Kind, int Id) b = Token(ep.B);
        return a.CompareTo(b) <= 0 ? (a, b) : (b, a);
    }

    private static (int Kind, int Id) Token(FilletEndpoint ep) =>
        ep switch
        {
            FilletEndpoint.Cut cut => (0, cut.Id.Value),
            FilletEndpoint.Node node => (1, node.NodeId),
            _ => throw new InvalidOperationException($"Unknown FilletEndpoint subtype: {ep.GetType().Name}"),
        };

    /// <summary>
    /// How strongly a corner holds an endpoint pair against another: a pinned corner over an unpinned one, then an unforced
    /// corner over a forced one, then a single-name corner over a two-name one.
    /// </summary>
    internal static int DedupRank(CornerSpec? corner)
    {
        if (corner is null)
        {
            return 0;
        }

        return (corner.IsPinned ? 4 : 0) + (corner.IsForced ? 0 : 2) + (corner.EdgeA.SharesTaxiway(corner.EdgeB) ? 1 : 0);
    }

    private static CornerSpec? CornerOf(CornerArcOp op, IReadOnlyList<JunctionPlan> junctions) =>
        junctions.Where(jp => jp.JunctionNodeId == op.JunctionNodeId).SelectMany(jp => jp.Corners).FirstOrDefault(c => c.CornerId == op.CornerId);

    /// <summary>
    /// Judges the merge groups of every planned merge (from any pass: intra-arm, cross-arm, cross-junction or global): every
    /// pinned cut whose group holds another pinned cut, directly or through unpinned cuts, has its pins refused
    /// (<see cref="FilletSkipReason.PinConflict"/>), and the merges that would move it are dropped: two pins are never merged
    /// into one. A pinned cut alone in its group still absorbs the group's unpinned cuts.
    /// </summary>
    private static void RefusePinnedMerges(List<TangentMergeOp> merges, Dictionary<CutId, ResolvedArmCut> cuts, FilletReportCollector report)
    {
        HashSet<CutId> conflicted = PinnedCutsSharingAGroup(merges, id => cuts.TryGetValue(id, out ResolvedArmCut? cut) && cut.Pinned);
        foreach (CutId id in conflicted.OrderBy(c => c.Value))
        {
            RefusePin(cuts[id], FilletSkipReason.PinConflict, report);
        }

        merges.RemoveAll(m => conflicted.Contains(m.CutIdA) || conflicted.Contains(m.CutIdB));
    }

    /// <summary>The pinned cuts whose merge group (a plain union-find over <paramref name="merges"/>) holds two or more pinned cuts.</summary>
    internal static HashSet<CutId> PinnedCutsSharingAGroup(IReadOnlyList<TangentMergeOp> merges, Func<CutId, bool> isPinned)
    {
        Dictionary<CutId, FilletEndpoint> groups = FilletPlanCutRedirect.BuildSurvivorMap(merges, new HashSet<CutId>());
        return
        [
            .. groups
                .Where(kv => isPinned(kv.Key))
                .GroupBy(kv => ((FilletEndpoint.Cut)kv.Value).Id)
                .Where(g => g.Count() >= 2)
                .SelectMany(g => g.Select(kv => kv.Key)),
        ];
    }

    /// <summary>Records the pins of every corner owning the pinned <paramref name="cut"/> as refused, for <paramref name="reason"/>.</summary>
    private static void RefusePin(ResolvedArmCut cut, FilletSkipReason reason, FilletReportCollector report)
    {
        foreach (int cornerId in cut.OwningCornerIds)
        {
            report.PlanPinRefused(cut.JunctionNodeId, cornerId, reason);
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("Pinned cut {Cut} of J{Junction}/{Corner} refused: {Reason}", cut.CutId.Value, cut.JunctionNodeId, cornerId, reason);
            }
        }
    }

    private static bool UsesAnyCut(CornerArcOp op, HashSet<CutId> cutIds) =>
        ((op.EndpointAtArmA is FilletEndpoint.Cut a) && cutIds.Contains(a.Id))
        || ((op.EndpointAtArmB is FilletEndpoint.Cut b) && cutIds.Contains(b.Id));
}
