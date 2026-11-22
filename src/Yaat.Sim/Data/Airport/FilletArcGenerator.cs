using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport.Fillet;

namespace Yaat.Sim.Data.Airport;

/// <summary>Plan-then-execute fillet generator.</summary>
public sealed class FilletArcGenerator : IFilletArcGenerator
{
    private static readonly ILogger Log = SimLog.CreateLogger("FilletArcGenerator");

    public string Id => "standard";

    public string DisplayName => "Standard (plan-then-execute)";

    /// <summary>
    /// Fillets every eligible corner of <paramref name="layout"/>, applying each of <paramref name="overrides"/> that matches
    /// one pre-fillet corner (suppress, radius and force; pinned tangents are not applied), and reports each corner's
    /// outcome and each override's match and effect (<see cref="FilletStatistics.Report"/>).
    /// </summary>
    public FilletStatistics Apply(AirportGroundLayout layout, CornerFilletOverrides overrides)
    {
        HashSet<int> manualArcNodes = ManualArcDetector.Detect(layout);
        int maxNodeId = layout.Nodes.Keys.DefaultIfEmpty(0).Max();
        var idCounter = new FilletPlanExecutor.NextNodeIdCounter { Next = maxNodeId + 1 };

        layout.RebuildAdjacencyLists();
        IReadOnlyList<ResolvedOverride> resolved = FilletOverrideResolver.ResolveCorners(layout, manualArcNodes, overrides);
        (PlannedPass pass, Dictionary<ResolvedCorner, ForceRefusal> rejected) = PlanHonouringForces(
            layout,
            manualArcNodes,
            FilletCornerEdits.From(resolved),
            maxNodeId
        );

        FilletPlan plan = pass.Plan;
        FilletPlanExecutor.ExecuteResult exec = FilletPlanExecutor.Execute(layout, plan, pass.Junctions, idCounter, pass.Report);
        int structuralCleanups = FilletGraphNormalizer.Normalize(layout);

        var stats = new FilletStatistics(
            FilletedNodes: exec.FilletedNodes,
            ArcsCreated: exec.ArcsCreated,
            CollinearMerges: exec.CollinearMerges,
            CoincidentNodesMerged: 0,
            OrphansRescued: 0,
            RedundantPreserveEdgesRemoved: 0,
            DuplicateCornerArcsRemoved: 0,
            ParallelBypassEdgesRemoved: 0,
            DirectShortensAdded: 0
        )
        {
            Warnings = plan.Warnings,
            Report = pass.Report.Build(plan.Warnings, [.. resolved.Select(r => WithEffect(r, pass.Report, rejected))]),
        };

        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation(
                "Fillet: {Nodes} filleted, {Arcs} arcs, {Merged} collinear, {Cleanups} structural cleanups, {Warnings} warnings",
                stats.FilletedNodes,
                stats.ArcsCreated,
                stats.CollinearMerges,
                structuralCleanups,
                stats.Warnings.Count
            );
        }

        return stats;
    }

    /// <summary>One planned pass: the plan, the junctions it fillets and the report of what it decided.</summary>
    private sealed record PlannedPass(FilletPlan Plan, List<JunctionPlan> Junctions, FilletReportCollector Report);

    /// <summary>
    /// Plans the pass with <paramref name="edits"/>. A forced corner the plan refuses (<see cref="ForcedCornerCheck.Refusal"/>:
    /// a check force never bypasses, or an arc too tight to steer) is rejected, with the reason, and the pass is planned
    /// again without its force, so a rejected force leaves the layout as the unforced pass builds it. Each round withdraws at
    /// least one force, so the loop ends. A refused force is not retried: one refused only because another forced corner
    /// was planned stays refused after that other force is withdrawn too.
    /// </summary>
    private static (PlannedPass Pass, Dictionary<ResolvedCorner, ForceRefusal> Rejected) PlanHonouringForces(
        AirportGroundLayout layout,
        HashSet<int> manualArcNodes,
        FilletCornerEdits edits,
        int maxNodeId
    )
    {
        var rejected = new Dictionary<ResolvedCorner, ForceRefusal>();
        while (true)
        {
            PlannedPass pass = PlanPass(layout, manualArcNodes, edits, maxNodeId);
            List<(ResolvedCorner Corner, ForceRefusal Refusal)> refused = [];
            foreach (ResolvedCorner corner in edits.ForcedCorners)
            {
                if (ForcedCornerCheck.Refusal(layout, pass.Plan, pass.Junctions, pass.Report, corner) is { } refusal)
                {
                    refused.Add((corner, refusal));
                }
            }

            if (refused.Count == 0)
            {
                return (pass, rejected);
            }

            foreach ((ResolvedCorner corner, ForceRefusal refusal) in refused)
            {
                rejected[corner] = refusal;
                if (Log.IsEnabled(LogLevel.Debug))
                {
                    Log.LogDebug(
                        "Forced corner J{Junction} arms {A}/{B} rejected: {Reason}",
                        corner.JunctionNodeId,
                        corner.ArmIdA,
                        corner.ArmIdB,
                        refusal.Reason
                    );
                }
            }

            edits = edits.WithoutForce([.. refused.Select(r => r.Corner)]);
        }
    }

    /// <summary>Plans every junction of <paramref name="layout"/> in node id order, without changing the layout.</summary>
    private static PlannedPass PlanPass(AirportGroundLayout layout, HashSet<int> manualArcNodes, FilletCornerEdits edits, int maxNodeId)
    {
        var report = new FilletReportCollector();
        var junctionPlans = new List<JunctionPlan>();
        var cutResults = new List<ArmCutResolver.JunctionCutResult>();

        // Start cut IDs well above the maximum possible node ID (both pre-existing and new
        // tangent-cut nodes) so CutId values never collide with node IDs. Pre-existing nodes are
        // 0..maxNodeId; new tangent-cut nodes start at maxNodeId+1 and there are at most one
        // per cut, so the largest new-node ID is ~maxNodeId + maxCuts. Using a 1_000_000
        // offset guarantees no overlap for any airport that fits in int32. The CutId type wrapper
        // enforces this namespace separation at compile time. Forced-only cuts are numbered from
        // a second 1_000_000 above, so every other cut is numbered below them (ArmCutResolver.CutIds).
        var cutIds = new ArmCutResolver.CutIds(new CutId(maxNodeId + 1_000_000), new CutId(maxNodeId + 2_000_000));

        foreach (GroundNode node in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            if (PlanJunction(node, manualArcNodes, edits, report) is not { } junction)
            {
                continue;
            }

            ArmCutResolver.JunctionCutResult cutResult = ArmCutResolver.Resolve(junction, cutIds, report);
            if ((cutResult.CornerArcs.Count == 0) && (junction.CollinearPairs.Count == 0) && (junction.SuppressedPairs.Count == 0))
            {
                report.JunctionSkipped(node, FilletSkipReason.NoCornerPairs);
                continue;
            }

            junctionPlans.Add(junction);
            cutResults.Add(cutResult);
        }

        FilletPlan plan = FilletPlanBuilder.Build(layout, junctionPlans, cutResults, report);
        return new PlannedPass(plan, junctionPlans, report);
    }

    /// <summary>The junction plan of <paramref name="node"/>, or null when the pass skips the node whole.</summary>
    private static JunctionPlan? PlanJunction(GroundNode node, HashSet<int> manualArcNodes, FilletCornerEdits edits, FilletReportCollector report)
    {
        if (manualArcNodes.Contains(node.Id))
        {
            report.JunctionSkipped(node, FilletSkipReason.ShapePoint);
            return null;
        }

        bool eligible = FilletEligibility.IsEligible(node, out bool preserve);
        if ((!eligible) && !(edits.ForcesAt(node.Id) && FilletEligibility.IsForceable(node)))
        {
            report.JunctionSkipped(node, FilletSkipReason.Ineligible);
            return null;
        }

        if (node.Edges.Count < 2)
        {
            return null;
        }

        JunctionPlan junction = JunctionClassifier.Classify(node, preserve, manualArcNodes, edits, report);
        if (junction.Kind == JunctionKind.Skip)
        {
            report.JunctionSkipped(node, FilletSkipReason.JunctionSkipped);
            return null;
        }

        return junction;
    }

    /// <summary><paramref name="resolved"/>'s report row with what applying its override did; unmatched rows unchanged.</summary>
    private static FilletOverrideReport WithEffect(
        ResolvedOverride resolved,
        FilletReportCollector report,
        Dictionary<ResolvedCorner, ForceRefusal> rejected
    )
    {
        if (resolved.Corner is not { } corner)
        {
            return resolved.Row;
        }

        CornerFilletOverride edit = resolved.Row.Override;
        if (IsEditFree(edit))
        {
            return resolved.Row with { Effect = FilletOverrideEffect.Applied };
        }

        if (rejected.TryGetValue(corner, out ForceRefusal refusal))
        {
            return Rejected(resolved.Row, refusal);
        }

        if (!report.Visited(corner))
        {
            return resolved.Row with { Effect = FilletOverrideEffect.NotReachable };
        }

        if ((edit.Force) && (report.OutcomeOf(corner) != FilletCornerOutcome.Built))
        {
            FilletSkipReason reason =
                report.SkipOf(corner)
                ?? throw new UnreachableException($"Forced corner at J{corner.JunctionNodeId} was built with no arc and no reason");
            return Rejected(resolved.Row, new ForceRefusal(reason, null));
        }

        return resolved.Row with
        {
            Effect = FilletOverrideEffect.Applied,
        };
    }

    private static FilletOverrideReport Rejected(FilletOverrideReport row, ForceRefusal refusal) =>
        row with
        {
            Effect = FilletOverrideEffect.Rejected,
            RejectedReason = refusal.Reason,
            RejectedRadiusFt = refusal.RadiusFt,
        };

    /// <summary>True when <paramref name="edit"/> asks for none of suppress, radius or force (pinned tangents are not applied).</summary>
    private static bool IsEditFree(CornerFilletOverride edit) => (!edit.Suppress) && (edit.RadiusFt is null) && (!edit.Force);
}
