using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>
/// Plan-time intra-arm coalesce and cross-junction shared-arm scaling/merge. A pinned cut (<see cref="ResolvedArmCut.Pinned"/>)
/// is a fixed point of every pass: it is never scaled, stays out of the shared-arm sizing, and survives any unpinned cut it
/// coalesces with.
/// </summary>
internal static class SharedArmTangentPass
{
    private static readonly ILogger Log = SimLog.CreateLogger("SharedArmTangentPass");

    /// <summary>The cuts <see cref="ApplyCrossJunction"/> refuses: forced-only cuts, and pinned cuts that overlap a neighbour's.</summary>
    internal sealed class CutRefusals
    {
        /// <summary>The forced-only cuts that would leave too short a straight to a neighbour; the forces owning them are refused.</summary>
        public HashSet<CutId> Forced { get; } = [];

        /// <summary>
        /// The pinned cuts that reach more than <see cref="FilletConstants.CoincidentNodeThresholdFt"/> past the neighbouring
        /// junction's cut on their shared arm; the pins owning them are refused (<see cref="FilletSkipReason.PinOverlapsNeighbour"/>).
        /// </summary>
        public HashSet<CutId> PinnedOverlapping { get; } = [];
    }

    /// <summary>
    /// The merge of two coincident cuts: a pinned cut survives an unpinned one; otherwise the lower <see cref="CutId.Value"/>
    /// survives (mirrors the old Math.Min behavior). The survivor is first. A merge of two pinned cuts is planned like any
    /// other; <see cref="FilletPlanBuilder"/> then refuses both pins (<see cref="FilletSkipReason.PinConflict"/>) and drops it.
    /// </summary>
    private static TangentMergeOp MergeOf(ResolvedArmCut a, ResolvedArmCut b)
    {
        bool aSurvives = (a.Pinned != b.Pinned) ? a.Pinned : (a.CutId.Value <= b.CutId.Value);
        (ResolvedArmCut survivor, ResolvedArmCut child) = aSurvives ? (a, b) : (b, a);
        if ((survivor.Pinned) && Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "Pinned cut {Survivor} at J{Junction} coalesces cut {Child} of J{ChildJunction} ({ChildPinned})",
                survivor.CutId.Value,
                survivor.JunctionNodeId,
                child.CutId.Value,
                child.JunctionNodeId,
                child.Pinned ? "pinned" : "unpinned"
            );
        }

        return new TangentMergeOp(survivor.CutId, child.CutId);
    }

    /// <summary>Merge ordered cuts on one arm that land within <see cref="FilletConstants.CoincidentNodeThresholdFt"/>.</summary>
    public static IReadOnlyList<TangentMergeOp> ApplyIntraArmCoalesce(
        JunctionPlan junction,
        Dictionary<CutId, ResolvedArmCut> cuts,
        List<PlanWarning> warnings
    )
    {
        var merges = new List<TangentMergeOp>();

        foreach (TaxiwayArm arm in junction.Arms)
        {
            var armCuts = cuts
                .Values.Where(c => (c.JunctionNodeId == junction.JunctionNodeId) && (c.ArmId == arm.Id))
                .OrderBy(c => c.DistanceAlongArmFt)
                .ToList();
            if (armCuts.Count < 2)
            {
                continue;
            }

            for (int i = 1; i < armCuts.Count; i++)
            {
                ResolvedArmCut prev = armCuts[i - 1];
                ResolvedArmCut curr = armCuts[i];
                double gapFt = GeoMath.DistanceNm(prev.Position, curr.Position) * GeoMath.FeetPerNm;
                if (gapFt <= FilletConstants.CoincidentNodeThresholdFt)
                {
                    merges.Add(MergeOf(prev, curr));
                }
            }
        }

        if (merges.Count > 0)
        {
            warnings.Add(
                new PlanWarning(junction.JunctionNodeId, null, PlanWarning.SharedArmScaled, $"Planned {merges.Count} intra-arm tangent merge(s)")
            );
        }

        return merges;
    }

    /// <summary>
    /// Merge coincident cuts on DIFFERENT arms of one junction that land within
    /// <see cref="FilletConstants.CoincidentNodeThresholdFt"/> — the same physical tangent point
    /// reached from two arms (e.g. an A tangent and a collinear A8/RAMP tangent). Without this the
    /// executor materializes two coincident nodes that the post-execute normalizer then has to
    /// merge, repointing both arms' corner arcs onto the survivor and leaving duplicate corner
    /// arcs. Planning the merge here keeps the produced graph clean. Mirrors
    /// <see cref="ApplyIntraArmCoalesce"/> but across arm pairs.
    /// </summary>
    public static IReadOnlyList<TangentMergeOp> ApplyCrossArmCoalesce(
        JunctionPlan junction,
        Dictionary<CutId, ResolvedArmCut> cuts,
        List<PlanWarning> warnings
    )
    {
        var merges = new List<TangentMergeOp>();
        var junctionCuts = cuts.Values.Where(c => c.JunctionNodeId == junction.JunctionNodeId).OrderBy(c => c.CutId.Value).ToList();

        for (int i = 0; i < junctionCuts.Count; i++)
        {
            for (int j = i + 1; j < junctionCuts.Count; j++)
            {
                ResolvedArmCut a = junctionCuts[i];
                ResolvedArmCut b = junctionCuts[j];
                if (a.ArmId == b.ArmId)
                {
                    continue;
                }

                double gapFt = GeoMath.DistanceNm(a.Position, b.Position) * GeoMath.FeetPerNm;
                if (gapFt <= FilletConstants.CoincidentNodeThresholdFt)
                {
                    merges.Add(MergeOf(a, b));
                }
            }
        }

        if (merges.Count > 0)
        {
            warnings.Add(
                new PlanWarning(junction.JunctionNodeId, null, PlanWarning.SharedArmScaled, $"Planned {merges.Count} cross-arm tangent merge(s)")
            );
        }

        return merges;
    }

    /// <summary>
    /// Merge ANY two cuts across the whole plan whose resolved positions land within
    /// <see cref="FilletConstants.CoincidentNodeThresholdFt"/>. This is the plan-time equivalent of
    /// the node-coincidence test the post-execute normalizer used to apply before it was deleted,
    /// moved earlier and applied to cuts. It catches cross-junction coincidences that
    /// <see cref="ApplyCrossJunction"/>'s farthest-pair-only merge does not reach — e.g. adjacent
    /// junctions whose tangent cuts on a shared taxiway land 1-4 ft apart. Run AFTER
    /// <see cref="ApplyCrossJunction"/> so cut positions reflect any shared-arm scaling. Survivor
    /// (<see cref="MergeOf"/>): a pinned cut over an unpinned one, otherwise the lower <see cref="CutId.Value"/>; a merge of
    /// two pinned cuts has both pins refused by <see cref="FilletPlanBuilder"/>. The union-find in <c>BuildSurvivorMap</c>
    /// absorbs the overlap with the intra-arm/cross-arm/cross-junction passes.
    /// </summary>
    public static IReadOnlyList<TangentMergeOp> ApplyGlobalCoincidentCutCoalesce(
        IReadOnlyDictionary<CutId, ResolvedArmCut> cuts,
        List<PlanWarning> warnings
    )
    {
        var merges = new List<TangentMergeOp>();
        var ordered = cuts.Values.OrderBy(c => c.CutId.Value).ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            for (int j = i + 1; j < ordered.Count; j++)
            {
                ResolvedArmCut a = ordered[i];
                ResolvedArmCut b = ordered[j];
                double gapFt = GeoMath.DistanceNm(a.Position, b.Position) * GeoMath.FeetPerNm;
                if (gapFt <= FilletConstants.CoincidentNodeThresholdFt)
                {
                    merges.Add(MergeOf(a, b));
                }
            }
        }

        if (merges.Count > 0)
        {
            warnings.Add(new PlanWarning(null, null, PlanWarning.CoincidentCutMerged, $"Planned {merges.Count} global coincident-cut merge(s)"));
        }

        return merges;
    }

    /// <summary>
    /// Reconcile the tangent-cut sets of adjacent junctions on a shared physical arm so the straight
    /// left between them is navigable. Two pairing sources feed <see cref="ReconcileArmPair"/>: arms
    /// that terminate at the neighbor junction (a taxiway that ends there), and arms whose walks pass
    /// <em>through</em> the neighbor — a shared through-chain such as a runway centerline crossing
    /// several taxiway junctions, paired via cuts landing on the same original edge. Without the
    /// second source, through-chain junctions cut toward each other blindly and can leave an
    /// orbit-trap sliver between their tangent nodes.
    /// <para>
    /// A forced-only cut (<see cref="ResolvedArmCut.ForcedOnly"/>) takes no part in the sizing, the pairing or the scaling,
    /// so its neighbours' cuts are as without the force. A forced-only cut that reaches past the other cuts of its arm and
    /// leaves less than <see cref="FilletConstants.MinSharedArmClearGapFt"/> of straight to the neighbour's cuts is added to
    /// <paramref name="refusals"/> instead: the force that owns it is refused. A pinned cut is never moved; one that reaches
    /// past the neighbour's cut is added to <paramref name="refusals"/>, so the pin that owns it is refused.
    /// </para>
    /// <para>
    /// <paramref name="sizingOnlyCuts"/>, the sized cuts pins left to no corner, take part in the sizing, the pairing and the
    /// scaling for this pass only, so a pinned corner's neighbours are sized as without the pin; no merge names them and they
    /// are gone from <paramref name="allCuts"/> when it returns.
    /// </para>
    /// </summary>
    public static IReadOnlyList<TangentMergeOp> ApplyCrossJunction(
        IReadOnlyList<JunctionPlan> junctionPlans,
        Dictionary<CutId, ResolvedArmCut> allCuts,
        IReadOnlyDictionary<CutId, ResolvedArmCut> sizingOnlyCuts,
        List<PlanWarning> warnings,
        CutRefusals refusals
    )
    {
        foreach ((CutId id, ResolvedArmCut cut) in sizingOnlyCuts)
        {
            allCuts[id] = cut;
        }

        HashSet<CutId> sizingOnly = [.. sizingOnlyCuts.Keys];
        var context = new ReconcileContext([], allCuts, sizingOnly, warnings, [], refusals, CheckForcedOnly: false);
        var planById = junctionPlans.ToDictionary(p => p.JunctionNodeId);

        foreach (JunctionPlan jp1 in junctionPlans)
        {
            foreach (TaxiwayArm? arm1 in jp1.Arms.Where(a => a.Terminus == TaxiwayArmTerminus.OtherIntersection))
            {
                if (!planById.TryGetValue(arm1.TerminalNode.Id, out JunctionPlan? jp2))
                {
                    continue;
                }

                TaxiwayArm? arm2 = jp2.Arms.FirstOrDefault(a => a.TerminalNode.Id == jp1.JunctionNodeId && a.TaxiwayName == arm1.TaxiwayName);
                if (arm2 is null)
                {
                    continue;
                }

                ReconcileArmPair(jp1, arm1, jp2, arm2, context);
            }
        }

        ReconcileEdgeGroups(planById, includeForcedOnly: false, context);
        ReconcileEdgeGroups(planById, includeForcedOnly: true, context with { CheckForcedOnly = true });
        foreach (CutId id in sizingOnly)
        {
            allCuts.Remove(id);
        }

        return context.Merges;
    }

    /// <summary>The shared state of one <see cref="ApplyCrossJunction"/> run.</summary>
    /// <param name="Processed">The junction pairs and taxiway already reconciled.</param>
    /// <param name="AllCuts">Every cut of the plan, and the sizing-only cuts, updated as cut sets are scaled.</param>
    /// <param name="SizingOnly">The ids of the sizing-only cuts in <paramref name="AllCuts"/>, which no corner uses.</param>
    /// <param name="Warnings">The plan's warnings.</param>
    /// <param name="Merges">The tangent merges planned.</param>
    /// <param name="Refusals">The forced-only and pinned cuts refused.</param>
    /// <param name="CheckForcedOnly">True to check forced-only cuts alone, for arm pairs only they bring together.</param>
    private sealed record ReconcileContext(
        HashSet<(int, int, string)> Processed,
        Dictionary<CutId, ResolvedArmCut> AllCuts,
        HashSet<CutId> SizingOnly,
        List<PlanWarning> Warnings,
        List<TangentMergeOp> Merges,
        CutRefusals Refusals,
        bool CheckForcedOnly
    );

    private static void ReconcileEdgeGroups(Dictionary<int, JunctionPlan> planById, bool includeForcedOnly, ReconcileContext context)
    {
        // Pinned cuts pair arms only in the second, checking pass, so they never bring a pair into the sizing.
        IEnumerable<ResolvedArmCut> cuts = context.AllCuts.Values.Where(c => includeForcedOnly || ((!c.ForcedOnly) && (!c.Pinned)));
        foreach (List<(JunctionPlan Plan, TaxiwayArm Arm)> edgeArms in GroupArmsByLocatedCutEdge(planById, [.. cuts]))
        {
            for (int i = 0; i < edgeArms.Count; i++)
            {
                for (int j = i + 1; j < edgeArms.Count; j++)
                {
                    (JunctionPlan? jpA, TaxiwayArm? armA) = edgeArms[i];
                    (JunctionPlan? jpB, TaxiwayArm? armB) = edgeArms[j];
                    if (jpA.JunctionNodeId == jpB.JunctionNodeId)
                    {
                        continue;
                    }

                    ReconcileArmPair(jpA, armA, jpB, armB, context);
                }
            }
        }
    }

    /// <summary>
    /// Group the (junction, arm) pairs whose cuts land on each original runway-centerline edge. Two
    /// different junctions appearing in one group share that edge's corridor — their cut sets face
    /// each other even though neither arm terminates at the other junction (the walk passes through
    /// it). This PAIRING source is restricted to runway centerlines: only the high-speed-exit
    /// widening pushes cuts far enough to collide on a through-chain, and pairing every
    /// taxiway-to-taxiway through-chain would perturb geometry airport-wide for no observed defect.
    /// (The <see cref="FilletConstants.MinSharedArmClearGapFt"/> reconcile rule itself is NOT
    /// runway-specific — terminating-arm pairs from the first pairing source, taxiway junctions
    /// included, get the same navigable-gap treatment.)
    /// </summary>
    private static IEnumerable<List<(JunctionPlan Plan, TaxiwayArm Arm)>> GroupArmsByLocatedCutEdge(
        Dictionary<int, JunctionPlan> planById,
        List<ResolvedArmCut> cuts
    )
    {
        var byEdge = new Dictionary<GroundEdge, List<(JunctionPlan Plan, TaxiwayArm Arm)>>();
        foreach (ResolvedArmCut cut in cuts)
        {
            if (!planById.TryGetValue(cut.JunctionNodeId, out JunctionPlan? jp))
            {
                continue;
            }

            TaxiwayArm? arm = jp.Arms.FirstOrDefault(a => a.Id == cut.ArmId);
            if (arm is null)
            {
                continue;
            }

            TaxiwayWalk.EdgeLocation loc = TaxiwayWalk.LocateDistanceFt(arm.Walk, jp.JunctionNode, cut.DistanceAlongArmFt);
            if (!loc.Edge.IsRunwayCenterline)
            {
                continue;
            }
            if (!byEdge.TryGetValue(loc.Edge, out List<(JunctionPlan Plan, TaxiwayArm Arm)>? list))
            {
                list = [];
                byEdge[loc.Edge] = list;
            }

            if (!list.Any(e => (e.Plan.JunctionNodeId == jp.JunctionNodeId) && (e.Arm.Id == arm.Id)))
            {
                list.Add((jp, arm));
            }
        }

        return byEdge.Values.Where(l => l.Count >= 2);
    }

    /// <summary>
    /// Reconcile one junction pair's opposing cut sets on their shared arm (the unforced cuts, unless the context checks
    /// forced-only cuts alone), then refuse any forced-only cut that leaves too short a straight between them.
    /// </summary>
    private static void ReconcileArmPair(JunctionPlan jp1, TaxiwayArm arm1, JunctionPlan jp2, TaxiwayArm arm2, ReconcileContext context)
    {
        int lo = Math.Min(jp1.JunctionNodeId, jp2.JunctionNodeId);
        int hi = Math.Max(jp1.JunctionNodeId, jp2.JunctionNodeId);
        if (!context.Processed.Add((lo, hi, arm1.TaxiwayName)))
        {
            return;
        }

        if (!context.CheckForcedOnly)
        {
            ReconcileUnforcedCuts((jp1, arm1), (jp2, arm2), context);
        }

        RefuseForcedCutsInGap((jp1, arm1), (jp2, arm2), context);
        CheckPinnedCutsInGap((jp1, arm1), (jp2, arm2), context);
    }

    /// <summary>
    /// Refuses every pinned cut of the shared arm whose clear gap to the other side's farthest cut is below
    /// −<see cref="FilletConstants.CoincidentNodeThresholdFt"/> (the cut order would fold back); when none is, warns on every
    /// corner owning a pinned cut there when, with every cut counted, less than
    /// <see cref="FilletConstants.MinSharedArmClearGapFt"/> of straight is left between the pair: the pins are honoured.
    /// </summary>
    private static void CheckPinnedCutsInGap(
        (JunctionPlan Plan, TaxiwayArm Arm) side1,
        (JunctionPlan Plan, TaxiwayArm Arm) side2,
        ReconcileContext context
    )
    {
        List<ResolvedArmCut> all1 = CutsOn(side1.Plan, side1.Arm, context);
        List<ResolvedArmCut> all2 = CutsOn(side2.Plan, side2.Arm, context);
        if (!all1.Concat(all2).Any(c => c.Pinned))
        {
            return;
        }

        double reach1 = all1.Select(c => c.DistanceAlongArmFt).DefaultIfEmpty(0).Max();
        double reach2 = all2.Select(c => c.DistanceAlongArmFt).DefaultIfEmpty(0).Max();
        double sharedLengthFt = SharedLengthFt(side1, side2);
        List<ResolvedArmCut> overlapping =
        [
            .. all1.Where(c => (c.Pinned) && ((sharedLengthFt - (c.DistanceAlongArmFt + reach2)) < -FilletConstants.CoincidentNodeThresholdFt)),
            .. all2.Where(c => (c.Pinned) && ((sharedLengthFt - (c.DistanceAlongArmFt + reach1)) < -FilletConstants.CoincidentNodeThresholdFt)),
        ];
        foreach (ResolvedArmCut cut in overlapping)
        {
            context.Refusals.PinnedOverlapping.Add(cut.CutId);
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("Pinned cut {Cut} at J{Junction} overlaps the neighbour's cut: refused", cut.CutId.Value, cut.JunctionNodeId);
            }
        }

        double clearGapFt = sharedLengthFt - (reach1 + reach2);
        if ((overlapping.Count == 0) && (clearGapFt < FilletConstants.MinSharedArmClearGapFt))
        {
            WarnPinnedShortStraight(side1, side2, [.. all1.Concat(all2).Where(c => c.Pinned)], (clearGapFt, context.Warnings));
        }
    }

    /// <summary>Warns on every corner owning one of <paramref name="pinned"/> that it leaves a straight under the clear gap.</summary>
    private static void WarnPinnedShortStraight(
        (JunctionPlan Plan, TaxiwayArm Arm) side1,
        (JunctionPlan Plan, TaxiwayArm Arm) side2,
        List<ResolvedArmCut> pinned,
        (double ClearGapFt, List<PlanWarning> Warnings) into
    )
    {
        double clearGapFt = into.ClearGapFt;

        string between = $"J{side1.Plan.JunctionNodeId}/J{side2.Plan.JunctionNodeId}";
        foreach (ResolvedArmCut cut in pinned)
        {
            string message =
                $"Pinned cut {cut.DistanceAlongArmFt:F1} ft along {side1.Arm.TaxiwayName} leaves a {clearGapFt:F1} ft straight between {between}, "
                + $"under {FilletConstants.MinSharedArmClearGapFt:F0} ft";
            foreach (int cornerId in cut.OwningCornerIds)
            {
                into.Warnings.Add(new PlanWarning(cut.JunctionNodeId, cornerId, PlanWarning.PinnedShortStraight, message));
            }
        }
    }

    /// <summary>
    /// Reconcile the unforced cut sets of one junction pair's shared arm. Cut lists are fetched
    /// fresh from the context (an arm may already have been scaled against another
    /// neighbor). Outcomes: a clear gap of at least
    /// <see cref="FilletConstants.MinSharedArmClearGapFt"/> is left alone; a dead-zone sliver gap is
    /// widened by scaling both sets down; overlapping or near-abutting sets are scaled to abut and
    /// their far cuts merged into one shared tangent node.
    /// </summary>
    private static void ReconcileUnforcedCuts(
        (JunctionPlan Plan, TaxiwayArm Arm) side1,
        (JunctionPlan Plan, TaxiwayArm Arm) side2,
        ReconcileContext context
    )
    {
        (JunctionPlan jp1, TaxiwayArm arm1) = side1;
        (JunctionPlan jp2, TaxiwayArm arm2) = side2;
        List<ResolvedArmCut> cuts1 = UnforcedCutsOn(jp1, arm1, context.AllCuts);
        List<ResolvedArmCut> cuts2 = UnforcedCutsOn(jp2, arm2, context.AllCuts);
        if ((cuts1.Count == 0) && (cuts2.Count == 0))
        {
            return;
        }

        double d1 = cuts1.Count > 0 ? cuts1.Max(c => c.DistanceAlongArmFt) : 0;
        double d2 = cuts2.Count > 0 ? cuts2.Max(c => c.DistanceAlongArmFt) : 0;
        double sharedLengthFt = SharedLengthFt(side1, side2);
        double clearGapFt = sharedLengthFt - (d1 + d2);
        if (clearGapFt >= FilletConstants.MinSharedArmClearGapFt)
        {
            return;
        }

        if ((clearGapFt > FilletConstants.CoincidentNodeThresholdFt) && (sharedLengthFt > FilletConstants.MinSharedArmClearGapFt))
        {
            // Dead zone: the cut sets don't collide, but the straight left between them is shorter
            // than the navigator's look-ahead cap — an orbit-trap sliver the pure-pursuit follower
            // cannot converge on. Scale both sets down so a navigable straight remains. (Sets within
            // the coincident threshold fall through to the abut-and-merge path below, which fuses
            // them into one shared tangent node.)
            double shrink = (sharedLengthFt - FilletConstants.MinSharedArmClearGapFt) / (d1 + d2);
            ScaleCutSet(jp1, arm1, cuts1, shrink, context.AllCuts);
            ScaleCutSet(jp2, arm2, cuts2, shrink, context.AllCuts);
            context.Warnings.Add(
                new PlanWarning(
                    jp1.JunctionNodeId,
                    null,
                    PlanWarning.SharedArmScaled,
                    $"Scaled shared arm {arm1.TaxiwayName} between J{jp1.JunctionNodeId}/J{jp2.JunctionNodeId} by {shrink:F3} "
                        + $"to keep a {FilletConstants.MinSharedArmClearGapFt:F0} ft navigable straight"
                )
            );
            return;
        }

        double scale = (sharedLengthFt - 1.0) / (d1 + d2);
        ScaleCutSet(jp1, arm1, cuts1, scale, context.AllCuts);
        ScaleCutSet(jp2, arm2, cuts2, scale, context.AllCuts);

        context.Warnings.Add(
            new PlanWarning(
                jp1.JunctionNodeId,
                null,
                PlanWarning.SharedArmScaled,
                $"Scaled shared arm {arm1.TaxiwayName} between J{jp1.JunctionNodeId}/J{jp2.JunctionNodeId} by {scale:F3}"
            )
        );

        ResolvedArmCut? far1 = UsedUnforcedCutsOn(jp1, arm1, context).OrderByDescending(c => c.DistanceAlongArmFt).FirstOrDefault();
        ResolvedArmCut? far2 = UsedUnforcedCutsOn(jp2, arm2, context).OrderByDescending(c => c.DistanceAlongArmFt).FirstOrDefault();
        if ((far1 is not null) && (far2 is not null))
        {
            double gapFt = GeoMath.DistanceNm(far1.Position, far2.Position) * GeoMath.FeetPerNm;
            if (gapFt <= FilletConstants.CoincidentNodeThresholdFt)
            {
                context.Merges.Add(MergeOf(far1, far2));
            }
        }
    }

    /// <summary>
    /// Refuses each forced-only cut of the shared arm that reaches past its arm's unforced cuts when, with the forced-only
    /// cuts counted, less than <see cref="FilletConstants.MinSharedArmClearGapFt"/> of straight is left between the pair.
    /// </summary>
    private static void RefuseForcedCutsInGap(
        (JunctionPlan Plan, TaxiwayArm Arm) side1,
        (JunctionPlan Plan, TaxiwayArm Arm) side2,
        ReconcileContext context
    )
    {
        List<ResolvedArmCut> all1 = CutsOn(side1.Plan, side1.Arm, context);
        List<ResolvedArmCut> all2 = CutsOn(side2.Plan, side2.Arm, context);
        if (!all1.Concat(all2).Any(c => c.ForcedOnly))
        {
            return;
        }

        double reach1 = all1.Select(c => c.DistanceAlongArmFt).DefaultIfEmpty(0).Max();
        double reach2 = all2.Select(c => c.DistanceAlongArmFt).DefaultIfEmpty(0).Max();
        if ((SharedLengthFt(side1, side2) - (reach1 + reach2)) >= FilletConstants.MinSharedArmClearGapFt)
        {
            return;
        }

        foreach (List<ResolvedArmCut> side in new[] { all1, all2 })
        {
            double unforcedReach = side.Where(c => !c.ForcedOnly).Select(c => c.DistanceAlongArmFt).DefaultIfEmpty(0).Max();
            foreach (ResolvedArmCut cut in side.Where(c => (c.ForcedOnly) && (c.DistanceAlongArmFt > unforcedReach)))
            {
                context.Refusals.Forced.Add(cut.CutId);
            }
        }
    }

    /// <summary>The straight length between the pair's junctions, feet, or the shorter arm when they coincide.</summary>
    private static double SharedLengthFt((JunctionPlan Plan, TaxiwayArm Arm) side1, (JunctionPlan Plan, TaxiwayArm Arm) side2)
    {
        double sharedLengthFt = GeoMath.DistanceNm(side1.Plan.JunctionNode.Position, side2.Plan.JunctionNode.Position) * GeoMath.FeetPerNm;
        return (sharedLengthFt < 1.0) ? Math.Min(side1.Arm.LengthFt, side2.Arm.LengthFt) : sharedLengthFt;
    }

    /// <summary>Every cut of the arm, the sizing-only cuts included.</summary>
    private static IEnumerable<ResolvedArmCut> ArmCuts(JunctionPlan junction, TaxiwayArm arm, Dictionary<CutId, ResolvedArmCut> allCuts) =>
        allCuts.Values.Where(c => (c.JunctionNodeId == junction.JunctionNodeId) && (c.ArmId == arm.Id));

    /// <summary>The cuts of the arm some corner uses: every cut but the sizing-only ones.</summary>
    private static List<ResolvedArmCut> CutsOn(JunctionPlan junction, TaxiwayArm arm, ReconcileContext context) =>
        [.. ArmCuts(junction, arm, context.AllCuts).Where(c => !context.SizingOnly.Contains(c.CutId))];

    /// <summary>
    /// The cuts of the arm the shared-arm sizing counts and may scale: neither forced-only nor pinned (a sizing-only cut
    /// included, so the neighbours are sized as without the pin that dropped it).
    /// </summary>
    private static List<ResolvedArmCut> UnforcedCutsOn(JunctionPlan junction, TaxiwayArm arm, Dictionary<CutId, ResolvedArmCut> allCuts) =>
        [.. ArmCuts(junction, arm, allCuts).Where(c => (!c.ForcedOnly) && (!c.Pinned))];

    /// <summary>The unforced cuts of the arm a merge may name: the sizing-only ones left out.</summary>
    private static List<ResolvedArmCut> UsedUnforcedCutsOn(JunctionPlan junction, TaxiwayArm arm, ReconcileContext context) =>
        [.. UnforcedCutsOn(junction, arm, context.AllCuts).Where(c => !context.SizingOnly.Contains(c.CutId))];

    private static void ScaleCutSet(
        JunctionPlan junction,
        TaxiwayArm arm,
        List<ResolvedArmCut> cutsOnArm,
        double scale,
        Dictionary<CutId, ResolvedArmCut> allCuts
    )
    {
        foreach (ResolvedArmCut cut in cutsOnArm.Where(c => !c.Pinned))
        {
            double newDist = cut.DistanceAlongArmFt * scale;
            (LatLon pos, double brg) = TaxiwayWalk.InterpolateAtDistanceFt(arm.Walk, junction.JunctionNode, newDist);
            ResolvedArmCut updated = cut with { DistanceAlongArmFt = newDist, Position = pos, BearingTowardJunctionDeg = brg };
            allCuts[cut.CutId] = updated;
        }
    }
}
