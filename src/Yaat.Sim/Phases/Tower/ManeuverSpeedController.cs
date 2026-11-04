namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Shared speed control for tight spacing/delay maneuvers (360s, 270s, VFR holds, S-turns):
/// decelerate to holding speed for the duration of the maneuver, then resume normal speed when
/// it ends. Owned by the maneuver phase, which persists its fields in its snapshot DTO.
/// A speed the controller assigns during the maneuver takes speed over for good: from that point
/// neither the maneuver's slow-down nor its resume touches the aircraft's speed.
/// </summary>
internal sealed class ManeuverSpeedController
{
    /// <summary>Pre-maneuver speed target, restored on resume when one was explicitly assigned.</summary>
    public double? PriorTargetSpeed { get; set; }

    /// <summary>
    /// Pre-maneuver <see cref="ControlTargets.AssignedSpeed"/>, restored in place of a null prior target when the
    /// captured state was an explicit assignment.
    /// </summary>
    public double? PriorAssignedSpeed { get; set; }

    /// <summary>Whether an explicit speed was assigned before the maneuver started.</summary>
    public bool PriorHasExplicitSpeed { get; set; }

    /// <summary>True once <see cref="Reduce"/> has actually slowed the aircraft, gating <see cref="Resume"/>.</summary>
    public bool SpeedReduced { get; set; }

    /// <summary>
    /// True once the controller assigned a speed during the maneuver. After that both
    /// <see cref="Reduce"/> and <see cref="Resume"/> do nothing: the assignment stands until the
    /// controller changes it, including through a VFR hold's arrival at its fix.
    /// </summary>
    public bool SpeedTakenOver { get; set; }

    /// <summary>Records the pre-maneuver speed state. Call once in the phase's OnStart.</summary>
    public void Capture(PhaseContext ctx)
    {
        PriorTargetSpeed = ctx.Targets.TargetSpeed;
        PriorAssignedSpeed = ctx.Targets.AssignedSpeed;
        PriorHasExplicitSpeed = ctx.Targets.HasExplicitSpeedCommand;
    }

    /// <summary>
    /// Slows the aircraft to its holding speed for the maneuver. Never speeds an aircraft up —
    /// only reduces when the current speed exceeds holding speed (so already-slow aircraft and
    /// aircraft on final at approach speed are left untouched). No-op once the controller has taken
    /// speed over, so a later slow-down cannot undo an assignment made during the maneuver.
    /// </summary>
    public void Reduce(PhaseContext ctx)
    {
        if (SpeedTakenOver)
        {
            return;
        }

        double maxHold = AircraftPerformance.HoldingSpeed(ctx.AircraftType, ctx.Aircraft.Altitude);
        double current = ctx.Targets.TargetSpeed ?? ctx.Aircraft.IndicatedAirspeed;
        if (current > maxHold)
        {
            ctx.Targets.TargetSpeed = maxHold;
            SpeedReduced = true;
        }
    }

    /// <summary>
    /// Resumes normal speed when the maneuver ends. Restores the prior explicit speed assignment
    /// if one existed (7110.65 §5-7-4 retains assignments until deleted), the prior target or, when
    /// that was null, the captured assigned speed; otherwise releases to the aircraft's natural
    /// schedule. No-op when the maneuver never reduced speed or the controller took speed over
    /// during it.
    /// </summary>
    public void Resume(PhaseContext ctx)
    {
        if ((SpeedTakenOver) || (!SpeedReduced))
        {
            return;
        }

        if (PriorHasExplicitSpeed)
        {
            ctx.Targets.TargetSpeed = PriorTargetSpeed ?? PriorAssignedSpeed;
            ctx.Targets.HasExplicitSpeedCommand = true;
        }
        else
        {
            ctx.Targets.TargetSpeed = AircraftPerformance.DefaultSpeed(
                ctx.AircraftType,
                ctx.Category,
                ctx.Aircraft.Altitude,
                ctx.Targets.TargetAltitude
            );
            ctx.Targets.HasExplicitSpeedCommand = false;
        }

        SpeedReduced = false;
    }

    /// <summary>
    /// Hands speed over to the controller mid-maneuver. Both <see cref="Reduce"/> and
    /// <see cref="Resume"/> become no-ops, so neither a later slow-down (a VFR hold reaching its
    /// fix) nor the maneuver's end overwrites the newly assigned speed.
    /// </summary>
    public void CancelAutoResume()
    {
        SpeedReduced = false;
        SpeedTakenOver = true;
    }
}
