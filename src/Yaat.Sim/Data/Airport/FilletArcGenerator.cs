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
    /// one pre-fillet corner (suppress, radius, force and pinned tangents), and reports each corner's
    /// outcome and each override's match and effect (<see cref="FilletStatistics.Report"/>).
    /// </summary>
    public FilletStatistics Apply(AirportGroundLayout layout, CornerFilletOverrides overrides)
    {
        HashSet<int> manualArcNodes = ManualArcDetector.Detect(layout);
        int maxNodeId = layout.Nodes.Keys.DefaultIfEmpty(0).Max();
        var idCounter = new FilletPlanExecutor.NextNodeIdCounter { Next = maxNodeId + 1 };

        layout.RebuildAdjacencyLists();
        IReadOnlyList<ResolvedOverride> resolved = FilletOverrideResolver.ResolveCorners(layout, manualArcNodes, overrides);
        HonouredPass honoured = PlanHonouringEdits(layout, manualArcNodes, FilletCornerEdits.From(resolved), maxNodeId);
        PlannedPass pass = honoured.Pass;

        FilletPlan plan = pass.Plan;
        FilletPlanExecutor.ExecuteResult exec = FilletPlanExecutor.Execute(layout, plan, pass.Junctions, idCounter, pass.Report);
        int structuralCleanups = FilletGraphNormalizer.Normalize(layout);
        ReportDroppedPinnedArcs(layout, exec, pass.Report);

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
            Report = pass.Report.Build(plan.Warnings, [.. resolved.Select(r => WithEffect(r, pass.Report, honoured))]),
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

    /// <summary>Reports each pinned corner's arc the normalizer removed (its sampled radius under the floor), so none goes silently.</summary>
    private static void ReportDroppedPinnedArcs(AirportGroundLayout layout, FilletPlanExecutor.ExecuteResult exec, FilletReportCollector report)
    {
        var kept = layout.Arcs.ToHashSet();
        foreach ((int junctionNodeId, int cornerId, GroundArc arc) in exec.PinnedArcs.Where(p => !kept.Contains(p.Arc)))
        {
            report.PinnedArcDropped(junctionNodeId, cornerId);
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("Pinned corner J{Junction}/{Corner}: the normalizer dropped its arc", junctionNodeId, cornerId);
            }
        }
    }

    /// <summary>One planned pass: the plan, the junctions it fillets and the report of what it decided.</summary>
    private sealed record PlannedPass(FilletPlan Plan, List<JunctionPlan> Junctions, FilletReportCollector Report);

    /// <summary>The pass the generator builds, with the forces and the pins it refused on the way.</summary>
    private sealed record HonouredPass(
        PlannedPass Pass,
        Dictionary<ResolvedCorner, ForceRefusal> RejectedForces,
        Dictionary<ResolvedCorner, PinRefusal> RejectedPins
    );

    /// <summary>
    /// Plans the pass with <paramref name="edits"/>. A pin the plan itself refuses (the planner's bounds and node checks, and
    /// the plan's <see cref="FilletSkipReason.PinOverlapsNeighbour"/> and <see cref="FilletSkipReason.PinConflict"/>) is
    /// rejected first and the pass planned again without it, so forces are judged only on a plan with no such refusal. A
    /// forced corner the plan refuses (<see cref="ForcedCornerCheck.Refusal"/>: a check force never bypasses, or an arc too
    /// tight to steer) is then rejected, with the reason, and the pass is planned again without its force, so a rejected
    /// force leaves the layout as the unforced pass builds it. Once every force left stands, a pinned corner whose built arc
    /// the plan refuses (<see cref="PinnedCornerCheck.Refusal"/>) is rejected the same way and the pass planned again without
    /// its pins. Each round withdraws at least one force or pin, so the loop ends. A refused force or pin is not retried.
    /// </summary>
    private static HonouredPass PlanHonouringEdits(AirportGroundLayout layout, HashSet<int> manualArcNodes, FilletCornerEdits edits, int maxNodeId)
    {
        var rejectedForces = new Dictionary<ResolvedCorner, ForceRefusal>();
        var rejectedPins = new Dictionary<ResolvedCorner, PinRefusal>();
        while (true)
        {
            PlannedPass pass = PlanPass(layout, manualArcNodes, edits, maxNodeId);
            Dictionary<ResolvedCorner, PinRefusal> planned = PlanTimePinRefusals(edits, pass.Report);
            if (planned.Count > 0)
            {
                Record(planned, rejectedPins, "Pinned corner", r => r.Reason);
                edits = edits.WithoutPins(planned.Keys);
                continue;
            }

            Dictionary<ResolvedCorner, ForceRefusal> forces = ForceRefusals(layout, pass, edits);
            if (forces.Count > 0)
            {
                Record(forces, rejectedForces, "Forced corner", r => r.Reason);
                edits = edits.WithoutForce(forces.Keys);
                continue;
            }

            Dictionary<ResolvedCorner, PinRefusal> pins = BuiltPinRefusals(layout, pass, edits);
            if (pins.Count == 0)
            {
                return new HonouredPass(pass, rejectedForces, rejectedPins);
            }

            Record(pins, rejectedPins, "Pinned corner", r => r.Reason);
            edits = edits.WithoutPins(pins.Keys);
        }
    }

    /// <summary>
    /// The pins of <paramref name="edits"/> the plan itself refused: the planner's (<see cref="FilletReportCollector.PinRefusalOf"/>)
    /// and a later stage's (<see cref="FilletReportCollector.PlanPinRefusalOf"/>), by corner.
    /// </summary>
    private static Dictionary<ResolvedCorner, PinRefusal> PlanTimePinRefusals(FilletCornerEdits edits, FilletReportCollector report)
    {
        var refused = new Dictionary<ResolvedCorner, PinRefusal>();
        foreach (ResolvedCorner corner in edits.PinnedCorners)
        {
            if (report.PinRefusalOf(corner) is { } planner)
            {
                refused[corner] = planner;
            }
            else if (report.PlanPinRefusalOf(corner) is { } reason)
            {
                refused[corner] = new PinRefusal(reason, null, null);
            }
        }

        return refused;
    }

    /// <summary>The forces of <paramref name="edits"/> the planned <paramref name="pass"/> refuses, by corner.</summary>
    private static Dictionary<ResolvedCorner, ForceRefusal> ForceRefusals(AirportGroundLayout layout, PlannedPass pass, FilletCornerEdits edits)
    {
        var forces = new Dictionary<ResolvedCorner, ForceRefusal>();
        foreach (ResolvedCorner corner in edits.ForcedCorners)
        {
            if (ForcedCornerCheck.Refusal(layout, pass.Plan, pass.Junctions, pass.Report, corner) is { } refusal)
            {
                forces[corner] = refusal;
            }
        }

        return forces;
    }

    /// <summary>The pins of <paramref name="edits"/> whose arc, built as the planned <paramref name="pass"/> builds it, is refused.</summary>
    private static Dictionary<ResolvedCorner, PinRefusal> BuiltPinRefusals(AirportGroundLayout layout, PlannedPass pass, FilletCornerEdits edits)
    {
        var pins = new Dictionary<ResolvedCorner, PinRefusal>();
        foreach (ResolvedCorner corner in edits.PinnedCorners)
        {
            if (PinnedCornerCheck.Refusal(layout, pass.Plan, pass.Junctions, pass.Report, corner) is { } refusal)
            {
                pins[corner] = refusal;
            }
        }

        return pins;
    }

    private static void Record<T>(
        Dictionary<ResolvedCorner, T> refused,
        Dictionary<ResolvedCorner, T> rejected,
        string what,
        Func<T, FilletSkipReason> reasonOf
    )
    {
        foreach ((ResolvedCorner corner, T refusal) in refused)
        {
            rejected[corner] = refusal;
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug(
                    "{What} J{Junction} arms {A}/{B} rejected: {Reason}",
                    what,
                    corner.JunctionNodeId,
                    corner.ArmIdA,
                    corner.ArmIdB,
                    reasonOf(refusal)
                );
            }
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
    private static FilletOverrideReport WithEffect(ResolvedOverride resolved, FilletReportCollector report, HonouredPass honoured)
    {
        if (resolved.Corner is not { } corner)
        {
            return resolved.Row;
        }

        PinRefusal? pinRefusal = report.PinRefusalOf(corner) ?? honoured.RejectedPins.GetValueOrDefault(corner);
        FilletOverrideReport row = resolved.Row with { Warnings = IgnoredEditWarnings(resolved.Row.Override, pinRefusal) };
        CornerFilletOverride edit = resolved.Row.Override.AsApplied();
        if (IsEditFree(edit))
        {
            return row with { Effect = FilletOverrideEffect.Applied };
        }

        if (honoured.RejectedForces.TryGetValue(corner, out ForceRefusal refusal))
        {
            return Rejected(row, refusal);
        }

        if (!report.Visited(corner))
        {
            return row with { Effect = FilletOverrideEffect.NotReachable };
        }

        return (EditRefusal(edit, report, corner, pinRefusal) is { } edited)
            ? Rejected(row, edited)
            : row with
            {
                Effect = FilletOverrideEffect.Applied,
            };
    }

    /// <summary>
    /// Why the pass refused an edit of a visited corner, or null when it applied them: a force that built no arc, a pin alone
    /// on a pair the pass skips (with the pair's skip reason), or a refused pin.
    /// </summary>
    private static ForceRefusal? EditRefusal(CornerFilletOverride edit, FilletReportCollector report, ResolvedCorner corner, PinRefusal? pinRefusal)
    {
        if ((edit.Force ? UnbuiltForceReason(report, corner) : PinAloneSkip(edit, report, corner)) is { } reason)
        {
            return new ForceRefusal(reason, null);
        }

        return (pinRefusal is null) ? null : new ForceRefusal(pinRefusal.Reason, pinRefusal.RadiusFt);
    }

    /// <summary>The skip reason of the pair when <paramref name="edit"/> only pins tangents and the pass planned no corner for it.</summary>
    private static FilletSkipReason? PinAloneSkip(CornerFilletOverride edit, FilletReportCollector report, ResolvedCorner corner)
    {
        bool pinOnly = ((edit.TangentAFt is not null) || (edit.TangentBFt is not null)) && (edit.RadiusFt is null) && (!edit.Force);
        return (pinOnly && (report.CornerIdOf(corner) is null)) ? report.SkipOf(corner) : null;
    }

    /// <summary>Why the forced corner got no arc, or null when it was built.</summary>
    private static FilletSkipReason? UnbuiltForceReason(FilletReportCollector report, ResolvedCorner corner)
    {
        if (report.OutcomeOf(corner) == FilletCornerOutcome.Built)
        {
            return null;
        }

        return report.SkipOf(corner)
            ?? throw new UnreachableException($"Forced corner at J{corner.JunctionNodeId} was built with no arc and no reason");
    }

    /// <summary>
    /// The edits of <paramref name="edit"/> the pass ignores, and what a refused pin hit: those a suppress wins over; the
    /// detail of <paramref name="pinRefusal"/>; or the radius when the pin is honoured (both tangents pinned, or one pinned
    /// and mirrored onto the other arm).
    /// </summary>
    private static IReadOnlyList<string> IgnoredEditWarnings(CornerFilletOverride edit, PinRefusal? pinRefusal)
    {
        if (edit.SuppressIgnoredEditsWarning() is { } suppressWins)
        {
            return [suppressWins];
        }

        if (pinRefusal is not null)
        {
            return (pinRefusal.Detail is { } detail) ? [detail] : [];
        }

        bool anyPinned = (edit.TangentAFt is not null) || (edit.TangentBFt is not null);
        if ((edit.RadiusFt is null) || (!anyPinned))
        {
            return [];
        }

        return (edit.TangentAFt, edit.TangentBFt) is ({ }, { })
            ? ["both tangents pinned: radiusFt ignored"]
            : ["one tangent pinned and mirrored: radiusFt ignored"];
    }

    private static FilletOverrideReport Rejected(FilletOverrideReport row, ForceRefusal refusal) =>
        row with
        {
            Effect = FilletOverrideEffect.Rejected,
            RejectedReason = refusal.Reason,
            RejectedRadiusFt = refusal.RadiusFt,
        };

    /// <summary>True when <paramref name="edit"/> asks for none of suppress, radius, force or pinned tangents.</summary>
    private static bool IsEditFree(CornerFilletOverride edit) =>
        (!edit.Suppress) && (edit.RadiusFt is null) && (!edit.Force) && (edit.TangentAFt is null) && (edit.TangentBFt is null);
}
