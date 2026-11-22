using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>Why the pass refuses a corner fillet override's pinned tangents.</summary>
/// <param name="Reason">The check the pin fails.</param>
/// <param name="RadiusFt">
/// The tightest radius of the arc built with the pinned tangents, feet (0 for a chord), set only for
/// <see cref="FilletSkipReason.Unsteerable"/>.
/// </param>
/// <param name="Detail">A sentence for the override report naming what the pin hit (a node, a mirrored arm), or null.</param>
internal sealed record PinRefusal(FilletSkipReason Reason, double? RadiusFt, string? Detail)
{
    /// <summary>What the refusal names for a log line: its <see cref="Detail"/>, or its <see cref="Reason"/> when it has none.</summary>
    public string DetailOrReason => Detail ?? Reason.ToString();
}

/// <summary>
/// Decides, from a plan and before the executor changes the layout, whether a pinned corner keeps its pins: the one check of
/// the arc built with the pinned tangents, run for every pinned corner of every planned pass (a plain pin, and a pin whose
/// corner is planned again after its force was refused).
/// </summary>
internal static class PinnedCornerCheck
{
    private static readonly ILogger Log = SimLog.CreateLogger("PinnedCornerCheck");

    /// <summary>
    /// Why the plan refuses the pins of <paramref name="corner"/>, or null when they stand (or the planner already refused
    /// them, or planned no corner): an arc, built as <see cref="FilletPlanExecutor.CornerBezier"/> builds it, that degrades
    /// to a chord or whose tightest radius is under <see cref="GeometricAdmissibility.MinSteerableArcRadiusFt"/>
    /// (<see cref="FilletSkipReason.Unsteerable"/>). A pin a later stage of the plan refused
    /// (<see cref="FilletSkipReason.PinOverlapsNeighbour"/>, <see cref="FilletSkipReason.PinConflict"/>) is withdrawn and the
    /// pass planned again before this check runs.
    /// </summary>
    public static PinRefusal? Refusal(
        AirportGroundLayout layout,
        FilletPlan plan,
        IReadOnlyList<JunctionPlan> junctions,
        FilletReportCollector report,
        ResolvedCorner corner
    )
    {
        if ((report.CornerIdOf(corner) is not { } cornerId) || (report.PinRefusalOf(corner) is not null))
        {
            return null;
        }

        if (report.OutcomeOf(corner) == FilletCornerOutcome.ChordDegraded)
        {
            return Logged(corner, Unsteerable(0.0));
        }

        return (report.SkipOf(corner) is null) ? Logged(corner, BuiltArcRefusal(layout, plan, junctions, corner.JunctionNodeId, cornerId)) : null;
    }

    /// <summary>The refusal of a planned pinned arc that would be a chord or too tight to steer, or null.</summary>
    private static PinRefusal? BuiltArcRefusal(
        AirportGroundLayout layout,
        FilletPlan plan,
        IReadOnlyList<JunctionPlan> junctions,
        int junctionNodeId,
        int cornerId
    )
    {
        (JunctionPlan junction, CornerSpec spec, CornerArcOp op) = ForcedCornerCheck.PlannedArc(plan, junctions, junctionNodeId, cornerId);
        LatLon? tanA = FilletPlanExecutor.PlannedPosition(layout, plan, op.EndpointAtArmA);
        LatLon? tanB = FilletPlanExecutor.PlannedPosition(layout, plan, op.EndpointAtArmB);
        if ((!spec.IsPinned) || (tanA is not { } a) || (tanB is not { } b) || (op.EndpointAtArmA == op.EndpointAtArmB))
        {
            return null;
        }

        double radiusFt = FilletPlanExecutor.CornerBezier(spec, junction.JunctionNode.Position, a, b).MinRadiusFt;
        if ((!spec.IsForced) && (radiusFt < FilletConstants.RadiusFloorFt))
        {
            return Unsteerable(0.0);
        }

        return (radiusFt >= GeometricAdmissibility.MinSteerableArcRadiusFt) ? null : Unsteerable(radiusFt);
    }

    private static PinRefusal Unsteerable(double radiusFt) => new(FilletSkipReason.Unsteerable, radiusFt, null);

    /// <summary><paramref name="refusal"/>, logged at Debug for <paramref name="corner"/> when there is one.</summary>
    private static PinRefusal? Logged(ResolvedCorner corner, PinRefusal? refusal)
    {
        if ((refusal is not null) && Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "Pinned corner J{Junction} arms {A}/{B}: built arc refused ({Reason}, tightest radius {Radius:F2} ft)",
                corner.JunctionNodeId,
                corner.ArmIdA,
                corner.ArmIdB,
                refusal.Reason,
                refusal.RadiusFt
            );
        }

        return refusal;
    }
}
