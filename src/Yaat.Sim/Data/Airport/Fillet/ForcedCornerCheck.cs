using System.Diagnostics;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>Why a planned pass refuses a forced corner, and the refused arc's tightest radius when that is why.</summary>
/// <param name="Reason">The check the forced corner fails.</param>
/// <param name="RadiusFt">The arc's tightest radius, feet, set only for <see cref="FilletSkipReason.Unsteerable"/>.</param>
internal readonly record struct ForceRefusal(FilletSkipReason Reason, double? RadiusFt);

/// <summary>Decides, from a plan and before the executor changes the layout, whether a forced corner may be built.</summary>
internal static class ForcedCornerCheck
{
    /// <summary>
    /// Why the plan refuses the forced corner <paramref name="corner"/>, or null when it may be built (or the pass never
    /// visited it): a skip or drop the plan recorded for it, an endpoint that resolves to nothing or to the other endpoint, or
    /// an arc whose tightest radius, built as <see cref="FilletPlanExecutor.CornerBezier"/> builds it, is under
    /// <see cref="GeometricAdmissibility.MinSteerableArcRadiusFt"/>.
    /// </summary>
    public static ForceRefusal? Refusal(
        AirportGroundLayout layout,
        FilletPlan plan,
        IReadOnlyList<JunctionPlan> junctions,
        FilletReportCollector report,
        ResolvedCorner corner
    )
    {
        if (report.SkipOf(corner) is { } skip)
        {
            return new ForceRefusal(skip, null);
        }

        if (report.CornerIdOf(corner) is not { } cornerId)
        {
            return null;
        }

        (JunctionPlan junction, CornerSpec spec, CornerArcOp op) = PlannedArc(plan, junctions, corner.JunctionNodeId, cornerId);
        LatLon? tanA = FilletPlanExecutor.PlannedPosition(layout, plan, op.EndpointAtArmA);
        LatLon? tanB = FilletPlanExecutor.PlannedPosition(layout, plan, op.EndpointAtArmB);
        if ((tanA is not { } a) || (tanB is not { } b))
        {
            return new ForceRefusal(FilletSkipReason.UnresolvedEndpoint, null);
        }

        if (op.EndpointAtArmA == op.EndpointAtArmB)
        {
            return new ForceRefusal(FilletSkipReason.CoincidentCuts, null);
        }

        double radiusFt = FilletPlanExecutor.CornerBezier(spec, junction.JunctionNode.Position, a, b).MinRadiusFt;
        return (radiusFt >= GeometricAdmissibility.MinSteerableArcRadiusFt) ? null : new ForceRefusal(FilletSkipReason.Unsteerable, radiusFt);
    }

    /// <summary>
    /// The junction plan, corner and planned arc of a forced corner the plan neither skipped nor dropped; the plan always holds
    /// all three for such a corner.
    /// </summary>
    private static (JunctionPlan Junction, CornerSpec Spec, CornerArcOp Op) PlannedArc(
        FilletPlan plan,
        IReadOnlyList<JunctionPlan> junctions,
        int junctionNodeId,
        int cornerId
    )
    {
        JunctionPlan junction =
            junctions.FirstOrDefault(j => j.JunctionNodeId == junctionNodeId)
            ?? throw new UnreachableException($"Forced corner J{junctionNodeId}/{cornerId} has no junction plan and no skip");
        CornerSpec spec = junction.Corners.First(c => c.CornerId == cornerId);
        CornerArcOp op =
            plan.CornerArcs.FirstOrDefault(o => (o.JunctionNodeId == junctionNodeId) && (o.CornerId == cornerId))
            ?? throw new UnreachableException($"Forced corner J{junctionNodeId}/{cornerId} has no arc and no skip");
        return (junction, spec, op);
    }
}
