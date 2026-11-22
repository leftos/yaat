namespace Yaat.Sim.Data.Airport.Fillet;

internal static class ArmCutResolver
{
    public sealed record JunctionCutResult(
        IReadOnlyDictionary<CutId, ResolvedArmCut> Cuts,
        IReadOnlyList<TangentMergeOp> TangentMerges,
        IReadOnlyList<CornerArcOp> CornerArcs,
        IReadOnlyList<StraightConnectorOp> StraightConnectors,
        IReadOnlyList<PlanWarning> Warnings,
        IReadOnlyList<CornerSpec> SurvivingCorners
    );

    /// <summary>The next cut ids: <see cref="Next"/> for every cut, <see cref="NextForcedOnly"/> for a cut only forced corners own.</summary>
    /// <remarks>
    /// Forced-only cuts are numbered above every other cut, so every lower-id-survives merge
    /// (<see cref="SharedArmTangentPass"/>, <see cref="FilletPlanCutRedirect.BuildSurvivorMap"/>) moves a forced-only cut onto
    /// the other cut and never the other way, and the other cuts keep the ids they have without the force.
    /// </remarks>
    public sealed class CutIds(CutId next, CutId nextForcedOnly)
    {
        public CutId Next { get; private set; } = next;

        public CutId NextForcedOnly { get; private set; } = nextForcedOnly;

        public CutId Take(bool forcedOnly)
        {
            CutId id = forcedOnly ? NextForcedOnly : Next;
            if (forcedOnly)
            {
                NextForcedOnly = new CutId(id.Value + 1);
            }
            else
            {
                Next = new CutId(id.Value + 1);
            }

            return id;
        }
    }

    /// <summary>Where the corners of one junction take their tangent cuts on each arm.</summary>
    private sealed class CutAssignment
    {
        public Dictionary<CutId, ResolvedArmCut> Cuts { get; } = [];

        public Dictionary<int, CutId> CornerToCutA { get; } = [];

        public Dictionary<int, CutId> CornerToCutB { get; } = [];

        public void Assign(CornerSpec corner, int armId, CutId cutId)
        {
            if (corner.ArmIdA == armId)
            {
                CornerToCutA[corner.CornerId] = cutId;
            }

            if (corner.ArmIdB == armId)
            {
                CornerToCutB[corner.CornerId] = cutId;
            }
        }
    }

    public static JunctionCutResult Resolve(JunctionPlan junction, CutIds cutIds, FilletReportCollector report)
    {
        var warnings = new List<PlanWarning>();
        if (junction.Corners.Count == 0)
        {
            return new JunctionCutResult(new Dictionary<CutId, ResolvedArmCut>(), [], [], [], warnings, []);
        }

        WarnWhenForceUnpreserves(junction, warnings);
        var activeCorners = junction.Corners.ToList();
        Dictionary<int, double> candidateFt = CandidateCutsFt(junction, activeCorners);
        HashSet<int> distortedArms = DistortedArms(junction, activeCorners, candidateFt);

        var assignment = new CutAssignment();
        foreach (TaxiwayArm arm in junction.Arms)
        {
            var involved = activeCorners.Where(c => (c.ArmIdA == arm.Id) || (c.ArmIdB == arm.Id)).ToList();
            if (involved.Count == 0)
            {
                continue;
            }

            if (!distortedArms.Contains(arm.Id))
            {
                PlaceSingleCut(junction, arm, involved, candidateFt[arm.Id], (cutIds, assignment, warnings));
                continue;
            }

            PlaceMultiCut(junction, arm, involved, (cutIds, assignment, warnings));
        }

        var surviving = new List<CornerSpec>();
        var cornerArcs = new List<CornerArcOp>();
        Dictionary<CutId, ResolvedArmCut> cuts = assignment.Cuts;
        foreach (CornerSpec? corner in activeCorners)
        {
            if (
                !assignment.CornerToCutA.TryGetValue(corner.CornerId, out CutId cutA)
                || !assignment.CornerToCutB.TryGetValue(corner.CornerId, out CutId cutB)
            )
            {
                warnings.Add(
                    new PlanWarning(junction.JunctionNodeId, corner.CornerId, PlanWarning.NoOwningCut, "Corner has no owning cut after resolve")
                );
                report.CornerDropped(junction.JunctionNodeId, corner.CornerId, FilletSkipReason.NoOwningCut);
                continue;
            }

            ResolvedArmCut cA = cuts[cutA];
            ResolvedArmCut cB = cuts[cutB];
            double r = FilletGeometry.EffectiveMinRadiusFt(
                cA.DistanceAlongArmFt,
                cB.DistanceAlongArmFt,
                corner.BearingAToJunctionDeg,
                corner.BearingBToJunctionDeg,
                cA.Position,
                cB.Position
            );

            if (IsBelowFloor(corner, r))
            {
                warnings.Add(
                    new PlanWarning(junction.JunctionNodeId, corner.CornerId, PlanWarning.DegenerateRadius, $"Effective radius {r:F1}ft below floor")
                );
                if (cutA == cutB)
                {
                    report.CornerDropped(junction.JunctionNodeId, corner.CornerId, FilletSkipReason.CoincidentCuts);
                }
                else
                {
                    report.CornerStraightened(junction.JunctionNodeId, corner.CornerId, FilletSkipReason.DegenerateRadius);
                }

                continue;
            }

            if (cutA == cutB)
            {
                report.CornerDropped(junction.JunctionNodeId, corner.CornerId, FilletSkipReason.CoincidentCuts);
                continue;
            }

            surviving.Add(corner);
            // Emit FilletEndpoint.Cut for both arms; FilletPlanCutRedirect will rewrite to
            // FilletEndpoint.Node when a cut redirects to a stable anchor.
            cornerArcs.Add(new CornerArcOp(junction.JunctionNodeId, corner.CornerId, new FilletEndpoint.Cut(cutA), new FilletEndpoint.Cut(cutB)));
        }

        var merges = SharedArmTangentPass.ApplyIntraArmCoalesce(junction, cuts, warnings).ToList();
        merges.AddRange(SharedArmTangentPass.ApplyCrossArmCoalesce(junction, cuts, warnings));

        var arcCornerIds = cornerArcs.Select(a => a.CornerId).ToHashSet();
        var straightConnectors = new List<StraightConnectorOp>();
        foreach (CornerSpec? corner in activeCorners)
        {
            if (arcCornerIds.Contains(corner.CornerId))
            {
                continue;
            }

            if (
                !assignment.CornerToCutA.TryGetValue(corner.CornerId, out CutId cutA)
                || !assignment.CornerToCutB.TryGetValue(corner.CornerId, out CutId cutB)
            )
            {
                continue;
            }

            if (cutA == cutB)
            {
                continue;
            }

            string twy = corner.EdgeA.SharesTaxiway(corner.EdgeB) ? corner.EdgeA.TaxiwayName : corner.EdgeA.TaxiwayName;
            straightConnectors.Add(
                new StraightConnectorOp(junction.JunctionNodeId, corner.CornerId, new FilletEndpoint.Cut(cutA), new FilletEndpoint.Cut(cutB), twy)
            );
        }

        return new JunctionCutResult(cuts, merges, cornerArcs, straightConnectors, warnings, surviving);
    }

    /// <summary>
    /// The corners whose ideal tangents size an arm's cut: every corner on it but those planned only because an override
    /// forces them (<see cref="CornerForce.Added"/>), so a forced corner leaves its neighbours' cuts as they are without the
    /// force; the forced corners alone when they are all the arm has.
    /// </summary>
    private static List<CornerSpec> SizingCorners(List<CornerSpec> onArm)
    {
        List<CornerSpec> unforced = [.. onArm.Where(c => c.Force != CornerForce.Added)];
        return (unforced.Count > 0) ? unforced : onArm;
    }

    private static Dictionary<int, double> CandidateCutsFt(JunctionPlan junction, List<CornerSpec> activeCorners)
    {
        var candidateFt = new Dictionary<int, double>();
        foreach (TaxiwayArm arm in junction.Arms)
        {
            List<CornerSpec> onArm = [.. activeCorners.Where(c => (c.ArmIdA == arm.Id) || (c.ArmIdB == arm.Id))];
            if (onArm.Count == 0)
            {
                continue;
            }

            var ideals = SizingCorners(onArm).Select(c => c.IdealTangentFt).ToList();
            double minIdeal = ideals.Min();
            double maxIdeal = ideals.Max();
            double candidate =
                (maxIdeal - minIdeal) <= FilletConstants.CoincidentNodeThresholdFt ? ideals.Average() : Math.Min(maxIdeal, arm.IntersectionCapFt);
            candidateFt[arm.Id] = Math.Min(candidate, FilletConstants.MaxTangentDistFt);
        }

        return candidateFt;
    }

    /// <summary>
    /// The arms a single cut cannot serve: an arm of a corner whose single-cut radius or tangent asymmetry is out of range.
    /// A corner planned only by a force never marks an arm, so it leaves its arms' cut layout as it is without the force.
    /// </summary>
    private static HashSet<int> DistortedArms(JunctionPlan junction, List<CornerSpec> activeCorners, Dictionary<int, double> candidateFt)
    {
        var armsById = junction.Arms.ToDictionary(a => a.Id);
        var distortedArms = new HashSet<int>();
        foreach (CornerSpec corner in activeCorners.Where(c => c.Force != CornerForce.Added))
        {
            double ta = candidateFt[corner.ArmIdA];
            double tb = candidateFt[corner.ArmIdB];
            (LatLon posA, double _) = TaxiwayWalk.InterpolateAtDistanceFt(armsById[corner.ArmIdA].Walk, junction.JunctionNode, ta);
            (LatLon posB, double _) = TaxiwayWalk.InterpolateAtDistanceFt(armsById[corner.ArmIdB].Walk, junction.JunctionNode, tb);
            double r = FilletGeometry.EffectiveMinRadiusFt(ta, tb, corner.BearingAToJunctionDeg, corner.BearingBToJunctionDeg, posA, posB);
            if (IsDistorted(corner, r, ta, tb))
            {
                distortedArms.Add(corner.ArmIdA);
                distortedArms.Add(corner.ArmIdB);
            }
        }

        return distortedArms;
    }

    private static void PlaceSingleCut(
        JunctionPlan junction,
        TaxiwayArm arm,
        List<CornerSpec> involved,
        double dist,
        (CutIds Ids, CutAssignment Assignment, List<PlanWarning> Warnings) into
    )
    {
        if (dist <= FilletConstants.CoincidentNodeThresholdFt)
        {
            dist = FilletConstants.CoincidentNodeThresholdFt + 1.0;
            into.Warnings.Add(
                new PlanWarning(
                    junction.JunctionNodeId,
                    null,
                    PlanWarning.SubThresholdCutSkipped,
                    $"Arm {arm.Id} ({arm.TaxiwayName}) cut clamped to {dist:F1}ft (was below coincident threshold)"
                )
            );
        }

        bool forcedOnly = involved.All(c => c.Force == CornerForce.Added);
        CutId cutId = into.Ids.Take(forcedOnly);
        (LatLon pos, double brg) = TaxiwayWalk.InterpolateAtDistanceFt(arm.Walk, junction.JunctionNode, dist);
        List<int> owners = [.. involved.Select(c => c.CornerId)];
        into.Assignment.Cuts[cutId] = new ResolvedArmCut(cutId, junction.JunctionNodeId, arm.Id, dist, pos, brg, owners, forcedOnly);
        foreach (CornerSpec c in involved)
        {
            into.Assignment.Assign(c, arm.Id, cutId);
        }
    }

    /// <summary>
    /// Places an ordered set of cuts on a distorted arm, one per distinct ideal tangent of the corners that size it; a
    /// corner planned only by a force takes the cut nearest its ideal tangent.
    /// </summary>
    private static void PlaceMultiCut(
        JunctionPlan junction,
        TaxiwayArm arm,
        List<CornerSpec> involved,
        (CutIds Ids, CutAssignment Assignment, List<PlanWarning> Warnings) into
    )
    {
        into.Warnings.Add(
            new PlanWarning(
                junction.JunctionNodeId,
                null,
                PlanWarning.SingleCutRejected,
                $"Arm {arm.Id} ({arm.TaxiwayName}) requires ordered multi-cut"
            )
        );

        List<CornerSpec> sizing = SizingCorners(involved);
        var positions = sizing.Select(c => c.IdealTangentFt).Distinct().OrderBy(d => d).ToList();
        positions = CoalescePositions(positions);
        positions = EnforceGap(positions, sizing, into.Warnings, junction.JunctionNodeId);

        var placed = new List<(CutId Id, double DistFt)>();
        foreach (double dist in positions)
        {
            double capped = Math.Min(dist, Math.Min(arm.IntersectionCapFt, FilletConstants.MaxTangentDistFt));
            if (capped <= FilletConstants.CoincidentNodeThresholdFt)
            {
                continue;
            }

            CutId cutId = into.Ids.Take(forcedOnly: false);
            (LatLon pos, double brg) = TaxiwayWalk.InterpolateAtDistanceFt(arm.Walk, junction.JunctionNode, capped);
            List<CornerSpec> owners = [.. sizing.Where(c => Math.Abs(c.IdealTangentFt - dist) <= FilletConstants.CoincidentNodeThresholdFt)];
            List<int> ownerIds = [.. owners.Select(c => c.CornerId)];
            into.Assignment.Cuts[cutId] = new ResolvedArmCut(cutId, junction.JunctionNodeId, arm.Id, capped, pos, brg, ownerIds, false);
            placed.Add((cutId, capped));
            foreach (CornerSpec c in owners)
            {
                into.Assignment.Assign(c, arm.Id, cutId);
            }
        }

        if (placed.Count == 0)
        {
            return;
        }

        foreach (CornerSpec forced in involved.Where(c => !sizing.Contains(c)))
        {
            into.Assignment.Assign(forced, arm.Id, placed.MinBy(p => Math.Abs(p.DistFt - forced.IdealTangentFt)).Id);
        }
    }

    /// <summary>
    /// Warns on the junction when a force makes it lose the preserved node it has without the force: a forced through
    /// (collinear) pair is now a corner and the junction has no other through or suppressed pair.
    /// </summary>
    private static void WarnWhenForceUnpreserves(JunctionPlan junction, List<PlanWarning> warnings)
    {
        if (junction.PreserveNode)
        {
            return;
        }

        foreach (CornerSpec corner in junction.Corners.Where(c => (c.IsForced) && (c.TurnAngleDeg < FilletConstants.CollinearThresholdDeg)))
        {
            string a = junction.Arms.First(x => x.Id == corner.ArmIdA).TaxiwayName;
            string b = junction.Arms.First(x => x.Id == corner.ArmIdB).TaxiwayName;
            warnings.Add(
                new PlanWarning(
                    junction.JunctionNodeId,
                    null,
                    PlanWarning.ForcedCornerUnpreserves,
                    $"Forced pair {a}/{b} is filleted, so the junction node is no longer preserved"
                )
            );
        }
    }

    /// <summary>True when the corner's single-cut radius <paramref name="r"/> or its tangent asymmetry rules a single cut out.</summary>
    private static bool IsDistorted(CornerSpec corner, double r, double ta, double tb) =>
        (r < FilletConstants.RadiusFloorFt)
        || (r > corner.RequestedRadiusFt * FilletConstants.DistortionThreshold)
        || ((Math.Max(ta, tb) / Math.Max(Math.Min(ta, tb), 1e-6)) > FilletConstants.AsymmetryThreshold);

    /// <summary>True when <paramref name="r"/> is below the radius floor and the corner is not forced.</summary>
    private static bool IsBelowFloor(CornerSpec corner, double r) => (!corner.IsForced) && (r < FilletConstants.RadiusFloorFt);

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
