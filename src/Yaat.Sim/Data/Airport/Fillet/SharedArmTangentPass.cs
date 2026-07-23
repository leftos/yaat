namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>Plan-time intra-arm coalesce and cross-junction shared-arm scaling/merge.</summary>
internal static class SharedArmTangentPass
{
    /// <summary>Merge ordered cuts on one arm that land within <see cref="FilletConstants.CoincidentNodeThresholdFt"/>.</summary>
    public static IReadOnlyList<TangentMergeOp> ApplyIntraArmCoalesce(
        JunctionPlan junction,
        Dictionary<CutId, ResolvedArmCut> cuts,
        List<PlanWarning> warnings
    )
    {
        var merges = new List<TangentMergeOp>();

        foreach (var arm in junction.Arms)
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
                var prev = armCuts[i - 1];
                var curr = armCuts[i];
                double gapFt = GeoMath.DistanceNm(prev.Position, curr.Position) * GeoMath.FeetPerNm;
                if (gapFt <= FilletConstants.CoincidentNodeThresholdFt)
                {
                    // Pick the lower integer value as the survivor (mirrors the old Math.Min behavior).
                    var survivor = prev.CutId.Value <= curr.CutId.Value ? prev.CutId : curr.CutId;
                    var child = prev.CutId.Value <= curr.CutId.Value ? curr.CutId : prev.CutId;
                    merges.Add(new TangentMergeOp(survivor, child));
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
                var a = junctionCuts[i];
                var b = junctionCuts[j];
                if (a.ArmId == b.ArmId)
                {
                    continue;
                }

                double gapFt = GeoMath.DistanceNm(a.Position, b.Position) * GeoMath.FeetPerNm;
                if (gapFt <= FilletConstants.CoincidentNodeThresholdFt)
                {
                    // Lower integer value survives (mirrors the intra-arm / cross-junction convention).
                    var survivor = a.CutId.Value <= b.CutId.Value ? a.CutId : b.CutId;
                    var child = a.CutId.Value <= b.CutId.Value ? b.CutId : a.CutId;
                    merges.Add(new TangentMergeOp(survivor, child));
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
    /// <see cref="ApplyCrossJunction"/> so cut positions reflect any shared-arm scaling. Survivor =
    /// lower <see cref="CutId.Value"/>; the union-find in <c>BuildSurvivorMap</c> absorbs the overlap
    /// with the intra-arm/cross-arm/cross-junction passes.
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
                var a = ordered[i];
                var b = ordered[j];
                double gapFt = GeoMath.DistanceNm(a.Position, b.Position) * GeoMath.FeetPerNm;
                if (gapFt <= FilletConstants.CoincidentNodeThresholdFt)
                {
                    var survivor = a.CutId.Value <= b.CutId.Value ? a.CutId : b.CutId;
                    var child = a.CutId.Value <= b.CutId.Value ? b.CutId : a.CutId;
                    merges.Add(new TangentMergeOp(survivor, child));
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
    /// </summary>
    public static IReadOnlyList<TangentMergeOp> ApplyCrossJunction(
        IReadOnlyList<JunctionPlan> junctionPlans,
        Dictionary<CutId, ResolvedArmCut> allCuts,
        List<PlanWarning> warnings
    )
    {
        var merges = new List<TangentMergeOp>();
        var planById = junctionPlans.ToDictionary(p => p.JunctionNodeId);
        var processed = new HashSet<(int, int, string)>();

        foreach (var jp1 in junctionPlans)
        {
            foreach (var arm1 in jp1.Arms.Where(a => a.Terminus == TaxiwayArmTerminus.OtherIntersection))
            {
                if (!planById.TryGetValue(arm1.TerminalNode.Id, out var jp2))
                {
                    continue;
                }

                var arm2 = jp2.Arms.FirstOrDefault(a => a.TerminalNode.Id == jp1.JunctionNodeId && a.TaxiwayName == arm1.TaxiwayName);
                if (arm2 is null)
                {
                    continue;
                }

                ReconcileArmPair(jp1, arm1, jp2, arm2, processed, allCuts, warnings, merges);
            }
        }

        foreach (var edgeArms in GroupArmsByLocatedCutEdge(planById, allCuts))
        {
            for (int i = 0; i < edgeArms.Count; i++)
            {
                for (int j = i + 1; j < edgeArms.Count; j++)
                {
                    var (jpA, armA) = edgeArms[i];
                    var (jpB, armB) = edgeArms[j];
                    if (jpA.JunctionNodeId == jpB.JunctionNodeId)
                    {
                        continue;
                    }

                    ReconcileArmPair(jpA, armA, jpB, armB, processed, allCuts, warnings, merges);
                }
            }
        }

        return merges;
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
        Dictionary<CutId, ResolvedArmCut> allCuts
    )
    {
        var byEdge = new Dictionary<GroundEdge, List<(JunctionPlan Plan, TaxiwayArm Arm)>>();
        foreach (var cut in allCuts.Values)
        {
            if (!planById.TryGetValue(cut.JunctionNodeId, out var jp))
            {
                continue;
            }

            var arm = jp.Arms.FirstOrDefault(a => a.Id == cut.ArmId);
            if (arm is null)
            {
                continue;
            }

            var loc = TaxiwayWalk.LocateDistanceFt(arm.Walk, jp.JunctionNode, cut.DistanceAlongArmFt);
            if (!loc.Edge.IsRunwayCenterline)
            {
                continue;
            }
            if (!byEdge.TryGetValue(loc.Edge, out var list))
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
    /// Reconcile one junction pair's opposing cut sets on their shared arm. Cut lists are fetched
    /// fresh from <paramref name="allCuts"/> (an arm may already have been scaled against another
    /// neighbor). Outcomes: a clear gap of at least
    /// <see cref="FilletConstants.MinSharedArmClearGapFt"/> is left alone; a dead-zone sliver gap is
    /// widened by scaling both sets down; overlapping or near-abutting sets are scaled to abut and
    /// their far cuts merged into one shared tangent node.
    /// </summary>
    private static void ReconcileArmPair(
        JunctionPlan jp1,
        TaxiwayArm arm1,
        JunctionPlan jp2,
        TaxiwayArm arm2,
        HashSet<(int, int, string)> processed,
        Dictionary<CutId, ResolvedArmCut> allCuts,
        List<PlanWarning> warnings,
        List<TangentMergeOp> merges
    )
    {
        int lo = Math.Min(jp1.JunctionNodeId, jp2.JunctionNodeId);
        int hi = Math.Max(jp1.JunctionNodeId, jp2.JunctionNodeId);
        var key = (lo, hi, arm1.TaxiwayName);
        if (!processed.Add(key))
        {
            return;
        }

        var cuts1 = allCuts.Values.Where(c => (c.JunctionNodeId == jp1.JunctionNodeId) && (c.ArmId == arm1.Id)).ToList();
        var cuts2 = allCuts.Values.Where(c => (c.JunctionNodeId == jp2.JunctionNodeId) && (c.ArmId == arm2.Id)).ToList();
        if ((cuts1.Count == 0) && (cuts2.Count == 0))
        {
            return;
        }

        double d1 = cuts1.Count > 0 ? cuts1.Max(c => c.DistanceAlongArmFt) : 0;
        double d2 = cuts2.Count > 0 ? cuts2.Max(c => c.DistanceAlongArmFt) : 0;
        double sharedLengthFt = GeoMath.DistanceNm(jp1.JunctionNode.Position, jp2.JunctionNode.Position) * GeoMath.FeetPerNm;
        if (sharedLengthFt < 1.0)
        {
            sharedLengthFt = Math.Min(arm1.LengthFt, arm2.LengthFt);
        }

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
            ScaleCutSet(jp1, arm1, cuts1, shrink, allCuts);
            ScaleCutSet(jp2, arm2, cuts2, shrink, allCuts);
            warnings.Add(
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
        ScaleCutSet(jp1, arm1, cuts1, scale, allCuts);
        ScaleCutSet(jp2, arm2, cuts2, scale, allCuts);

        warnings.Add(
            new PlanWarning(
                jp1.JunctionNodeId,
                null,
                PlanWarning.SharedArmScaled,
                $"Scaled shared arm {arm1.TaxiwayName} between J{jp1.JunctionNodeId}/J{jp2.JunctionNodeId} by {scale:F3}"
            )
        );

        var far1 = allCuts
            .Values.Where(c => (c.JunctionNodeId == jp1.JunctionNodeId) && (c.ArmId == arm1.Id))
            .OrderByDescending(c => c.DistanceAlongArmFt)
            .FirstOrDefault();
        var far2 = allCuts
            .Values.Where(c => (c.JunctionNodeId == jp2.JunctionNodeId) && (c.ArmId == arm2.Id))
            .OrderByDescending(c => c.DistanceAlongArmFt)
            .FirstOrDefault();
        if ((far1 is not null) && (far2 is not null))
        {
            double gapFt = GeoMath.DistanceNm(far1.Position, far2.Position) * GeoMath.FeetPerNm;
            if (gapFt <= FilletConstants.CoincidentNodeThresholdFt)
            {
                // Pick the lower integer value as the survivor (mirrors the old Math.Min behavior).
                var survivor = far1.CutId.Value <= far2.CutId.Value ? far1.CutId : far2.CutId;
                var child = far1.CutId.Value <= far2.CutId.Value ? far2.CutId : far1.CutId;
                merges.Add(new TangentMergeOp(survivor, child));
            }
        }
    }

    private static void ScaleCutSet(
        JunctionPlan junction,
        TaxiwayArm arm,
        List<ResolvedArmCut> cutsOnArm,
        double scale,
        Dictionary<CutId, ResolvedArmCut> allCuts
    )
    {
        foreach (var cut in cutsOnArm)
        {
            double newDist = cut.DistanceAlongArmFt * scale;
            var (pos, brg) = TaxiwayWalk.InterpolateAtDistanceFt(arm.Walk, junction.JunctionNode, newDist);
            var updated = cut with { DistanceAlongArmFt = newDist, Position = pos, BearingTowardJunctionDeg = brg };
            allCuts[cut.CutId] = updated;
        }
    }
}
