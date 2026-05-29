namespace Yaat.Sim.Data.Airport.Fillet.V2;

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

    /// <summary>Scale endpoint cut sets on the same physical arm between adjacent junctions.</summary>
    public static IReadOnlyList<TangentMergeOp> ApplyCrossJunction(
        IReadOnlyList<JunctionPlan> junctionPlans,
        IReadOnlyList<ArmCutResolver.JunctionCutResult> results,
        Dictionary<CutId, ResolvedArmCut> allCuts,
        List<PlanWarning> warnings
    )
    {
        var merges = new List<TangentMergeOp>();
        var planById = junctionPlans.ToDictionary(p => p.JunctionNodeId);
        var processed = new HashSet<(int, int, string)>();

        for (int i = 0; i < junctionPlans.Count; i++)
        {
            var jp1 = junctionPlans[i];
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

                int lo = Math.Min(jp1.JunctionNodeId, jp2.JunctionNodeId);
                int hi = Math.Max(jp1.JunctionNodeId, jp2.JunctionNodeId);
                var key = (lo, hi, arm1.TaxiwayName);
                if (!processed.Add(key))
                {
                    continue;
                }

                var cuts1 = allCuts.Values.Where(c => (c.JunctionNodeId == jp1.JunctionNodeId) && (c.ArmId == arm1.Id)).ToList();
                var cuts2 = allCuts.Values.Where(c => (c.JunctionNodeId == jp2.JunctionNodeId) && (c.ArmId == arm2.Id)).ToList();
                if ((cuts1.Count == 0) && (cuts2.Count == 0))
                {
                    continue;
                }

                double d1 = cuts1.Count > 0 ? cuts1.Max(c => c.DistanceAlongArmFt) : 0;
                double d2 = cuts2.Count > 0 ? cuts2.Max(c => c.DistanceAlongArmFt) : 0;
                double sharedLengthFt = GeoMath.DistanceNm(jp1.JunctionNode.Position, jp2.JunctionNode.Position) * GeoMath.FeetPerNm;
                if (sharedLengthFt < 1.0)
                {
                    sharedLengthFt = Math.Min(arm1.LengthFt, arm2.LengthFt);
                }

                if ((d1 + d2) <= (sharedLengthFt - 1.0))
                {
                    continue;
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
        }

        return merges;
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
