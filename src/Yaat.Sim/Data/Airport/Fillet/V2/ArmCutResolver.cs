namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal static class ArmCutResolver
{
    public sealed record JunctionCutResult(
        IReadOnlyDictionary<int, ResolvedArmCut> Cuts,
        IReadOnlyList<TangentMergeOp> TangentMerges,
        IReadOnlyList<CornerArcOp> CornerArcs,
        IReadOnlyList<StraightConnectorOp> StraightConnectors,
        IReadOnlyList<PlanWarning> Warnings,
        IReadOnlyList<CornerSpec> SurvivingCorners
    );

    public static JunctionCutResult Resolve(JunctionPlan junction, ref int nextCutId)
    {
        var warnings = new List<PlanWarning>();
        if (junction.Corners.Count == 0)
        {
            return new JunctionCutResult(new Dictionary<int, ResolvedArmCut>(), [], [], [], warnings, []);
        }

        var armsById = junction.Arms.ToDictionary(a => a.Id);
        var activeCorners = junction.Corners.ToList();
        var distortedArms = new HashSet<int>();
        var candidateFt = new Dictionary<int, double>();

        foreach (var arm in junction.Arms)
        {
            var ideals = activeCorners.Where(c => (c.ArmIdA == arm.Id) || (c.ArmIdB == arm.Id)).Select(c => c.IdealTangentFt).ToList();

            if (ideals.Count == 0)
            {
                continue;
            }

            double minIdeal = ideals.Min();
            double maxIdeal = ideals.Max();
            double candidate =
                (maxIdeal - minIdeal) <= FilletConstants.CoincidentNodeThresholdFt ? ideals.Average() : Math.Min(maxIdeal, arm.IntersectionCapFt);
            candidate = Math.Min(candidate, FilletConstants.MaxTangentDistFt);
            candidateFt[arm.Id] = candidate;
        }

        foreach (var corner in activeCorners.ToList())
        {
            var armA = armsById[corner.ArmIdA];
            var armB = armsById[corner.ArmIdB];
            double ta = candidateFt[corner.ArmIdA];
            double tb = candidateFt[corner.ArmIdB];

            var (posA, _) = TaxiwayWalk.InterpolateAtDistanceFt(armA.Walk, junction.JunctionNode, ta);
            var (posB, _) = TaxiwayWalk.InterpolateAtDistanceFt(armB.Walk, junction.JunctionNode, tb);

            double r = FilletGeometry.EffectiveMinRadiusFt(ta, tb, corner.BearingAToJunctionDeg, corner.BearingBToJunctionDeg, posA, posB);

            bool reject =
                (r < FilletConstants.RadiusFloorFt)
                || (r > corner.RequestedRadiusFt * FilletConstants.DistortionThreshold)
                || ((Math.Max(ta, tb) / Math.Max(Math.Min(ta, tb), 1e-6)) > FilletConstants.AsymmetryThreshold);

            if (reject)
            {
                distortedArms.Add(corner.ArmIdA);
                distortedArms.Add(corner.ArmIdB);
            }
        }

        var cuts = new Dictionary<int, ResolvedArmCut>();
        var cornerToCutA = new Dictionary<int, int>();
        var cornerToCutB = new Dictionary<int, int>();

        foreach (var arm in junction.Arms)
        {
            var involved = activeCorners.Where(c => (c.ArmIdA == arm.Id) || (c.ArmIdB == arm.Id)).ToList();
            if (involved.Count == 0)
            {
                continue;
            }

            if (!distortedArms.Contains(arm.Id))
            {
                double dist = candidateFt[arm.Id];
                if (dist <= FilletConstants.CoincidentNodeThresholdFt)
                {
                    dist = FilletConstants.CoincidentNodeThresholdFt + 1.0;
                    warnings.Add(
                        new PlanWarning(
                            junction.JunctionNodeId,
                            null,
                            PlanWarning.SubThresholdCutSkipped,
                            $"Arm {arm.Id} ({arm.TaxiwayName}) cut clamped to {dist:F1}ft (was below coincident threshold)"
                        )
                    );
                }

                int cutId = nextCutId++;
                var (pos, brg) = TaxiwayWalk.InterpolateAtDistanceFt(arm.Walk, junction.JunctionNode, dist);
                var cut = new ResolvedArmCut(cutId, junction.JunctionNodeId, arm.Id, dist, pos, brg, involved.Select(c => c.CornerId).ToList());
                cuts[cutId] = cut;
                foreach (var c in involved)
                {
                    if (c.ArmIdA == arm.Id)
                    {
                        cornerToCutA[c.CornerId] = cutId;
                    }

                    if (c.ArmIdB == arm.Id)
                    {
                        cornerToCutB[c.CornerId] = cutId;
                    }
                }

                continue;
            }

            warnings.Add(
                new PlanWarning(
                    junction.JunctionNodeId,
                    null,
                    PlanWarning.SingleCutRejected,
                    $"Arm {arm.Id} ({arm.TaxiwayName}) requires ordered multi-cut"
                )
            );

            var positions = involved.Select(c => c.IdealTangentFt).Distinct().OrderBy(d => d).ToList();

            positions = CoalescePositions(positions);
            positions = EnforceGap(positions, involved, warnings, junction.JunctionNodeId);

            foreach (double dist in positions)
            {
                double capped = Math.Min(dist, Math.Min(arm.IntersectionCapFt, FilletConstants.MaxTangentDistFt));
                if (capped <= FilletConstants.CoincidentNodeThresholdFt)
                {
                    continue;
                }

                int cutId = nextCutId++;
                var (pos, brg) = TaxiwayWalk.InterpolateAtDistanceFt(arm.Walk, junction.JunctionNode, capped);
                var owners = involved
                    .Where(c => Math.Abs(c.IdealTangentFt - dist) <= FilletConstants.CoincidentNodeThresholdFt)
                    .Select(c => c.CornerId)
                    .ToList();

                var cut = new ResolvedArmCut(cutId, junction.JunctionNodeId, arm.Id, capped, pos, brg, owners);
                cuts[cutId] = cut;

                foreach (var c in involved.Where(c => owners.Contains(c.CornerId)))
                {
                    if (c.ArmIdA == arm.Id)
                    {
                        cornerToCutA[c.CornerId] = cutId;
                    }

                    if (c.ArmIdB == arm.Id)
                    {
                        cornerToCutB[c.CornerId] = cutId;
                    }
                }
            }
        }

        var surviving = new List<CornerSpec>();
        var cornerArcs = new List<CornerArcOp>();
        foreach (var corner in activeCorners)
        {
            if (!cornerToCutA.TryGetValue(corner.CornerId, out int cutA) || !cornerToCutB.TryGetValue(corner.CornerId, out int cutB))
            {
                warnings.Add(
                    new PlanWarning(junction.JunctionNodeId, corner.CornerId, PlanWarning.NoOwningCut, "Corner has no owning cut after resolve")
                );
                continue;
            }

            var cA = cuts[cutA];
            var cB = cuts[cutB];
            double r = FilletGeometry.EffectiveMinRadiusFt(
                cA.DistanceAlongArmFt,
                cB.DistanceAlongArmFt,
                corner.BearingAToJunctionDeg,
                corner.BearingBToJunctionDeg,
                cA.Position,
                cB.Position
            );

            if (r < FilletConstants.RadiusFloorFt)
            {
                warnings.Add(
                    new PlanWarning(junction.JunctionNodeId, corner.CornerId, PlanWarning.DegenerateRadius, $"Effective radius {r:F1}ft below floor")
                );
                continue;
            }

            if (cutA == cutB)
            {
                continue;
            }

            surviving.Add(corner);
            cornerArcs.Add(new CornerArcOp(junction.JunctionNodeId, corner.CornerId, cutA, cutB));
        }

        var merges = SharedArmTangentPass.ApplyIntraArmCoalesce(junction, cuts, warnings);

        var arcCornerIds = cornerArcs.Select(a => a.CornerId).ToHashSet();
        var straightConnectors = new List<StraightConnectorOp>();
        foreach (var corner in activeCorners)
        {
            if (arcCornerIds.Contains(corner.CornerId))
            {
                continue;
            }

            if (!cornerToCutA.TryGetValue(corner.CornerId, out int cutA) || !cornerToCutB.TryGetValue(corner.CornerId, out int cutB))
            {
                continue;
            }

            if (cutA == cutB)
            {
                continue;
            }

            string twy = corner.EdgeA.SharesTaxiway(corner.EdgeB) ? corner.EdgeA.TaxiwayName : corner.EdgeA.TaxiwayName;
            straightConnectors.Add(new StraightConnectorOp(junction.JunctionNodeId, corner.CornerId, cutA, cutB, twy));
        }

        return new JunctionCutResult(cuts, merges, cornerArcs, straightConnectors, warnings, surviving);
    }

    private static List<double> CoalescePositions(List<double> sorted)
    {
        if (sorted.Count == 0)
        {
            return sorted;
        }

        var result = new List<double> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - result[^1] <= FilletConstants.IdealCoalesceThresholdFt)
            {
                result[^1] = (result[^1] + sorted[i]) / 2.0;
            }
            else
            {
                result.Add(sorted[i]);
            }
        }

        return result;
    }

    private static List<double> EnforceGap(List<double> positions, List<CornerSpec> involved, List<PlanWarning> warnings, int junctionId)
    {
        if (positions.Count < 2)
        {
            return positions;
        }

        var kept = new List<double> { positions[0] };
        for (int i = 1; i < positions.Count; i++)
        {
            if (positions[i] - kept[^1] >= FilletConstants.MinArmSegmentGapFt)
            {
                kept.Add(positions[i]);
            }
            else
            {
                warnings.Add(
                    new PlanWarning(
                        junctionId,
                        involved[0].CornerId,
                        PlanWarning.CornerDemoted,
                        $"Cut gap {positions[i] - kept[^1]:F1}ft < {FilletConstants.MinArmSegmentGapFt}ft"
                    )
                );
            }
        }

        return kept;
    }
}
