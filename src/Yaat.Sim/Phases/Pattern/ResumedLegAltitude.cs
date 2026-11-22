namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// A controller altitude (CM/DM) issued on a pattern leg that a boundary hold suspended: the leg's restart copy keeps it
/// (AIM 4-4-10.a) instead of the leg's own altitude target.
/// </summary>
internal static class ResumedLegAltitude
{
    /// <summary>
    /// The altitude the controller assigned while the leg ran: the assigned altitude when it differs from the one recorded as
    /// the leg started. Null when there is none, or when the leg recorded no baseline (restored from a snapshot written before
    /// it did), so an assignment already in force at the leg's start is never mistaken for one.
    /// </summary>
    internal static double? IssuedOnLeg(ControlTargets targets, bool baselineRecorded, double? baselineFt) =>
        baselineRecorded && (targets.AssignedAltitude is { } assignedFt) && (assignedFt != baselineFt) ? assignedFt : null;

    /// <summary>Targets <paramref name="controllerAltitudeFt"/> when there is one, at the default rate when it is a descent.</summary>
    internal static void Apply(PhaseContext ctx, double? controllerAltitudeFt)
    {
        if (controllerAltitudeFt is not { } altitudeFt)
        {
            return;
        }

        ctx.Targets.TargetAltitude = altitudeFt;
        if (altitudeFt < ctx.Aircraft.Altitude)
        {
            ctx.Targets.DesiredVerticalRate = null;
        }
    }
}
