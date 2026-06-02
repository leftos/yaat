namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Shared speed control for tight spacing/delay maneuvers (360s, 270s, VFR holds, S-turns):
/// decelerate to holding speed for the duration of the maneuver, then resume normal speed when
/// it ends. Owned by the maneuver phase, which persists its fields in its snapshot DTO.
/// </summary>
internal sealed class ManeuverSpeedController
{
    /// <summary>Pre-maneuver speed target, restored on resume when one was explicitly assigned.</summary>
    public double? PriorTargetSpeed { get; set; }

    /// <summary>Whether an explicit speed was assigned before the maneuver started.</summary>
    public bool PriorHasExplicitSpeed { get; set; }

    /// <summary>True once <see cref="Reduce"/> has actually slowed the aircraft, gating <see cref="Resume"/>.</summary>
    public bool SpeedReduced { get; set; }

    /// <summary>Records the pre-maneuver speed state. Call once in the phase's OnStart.</summary>
    public void Capture(PhaseContext ctx)
    {
        PriorTargetSpeed = ctx.Targets.TargetSpeed;
        PriorHasExplicitSpeed = ctx.Targets.HasExplicitSpeedCommand;
    }

    /// <summary>
    /// Slows the aircraft to its holding speed for the maneuver. Never speeds an aircraft up —
    /// only reduces when the current speed exceeds holding speed (so already-slow aircraft and
    /// aircraft on final at approach speed are left untouched).
    /// </summary>
    public void Reduce(PhaseContext ctx)
    {
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
    /// if one existed (7110.65 §5-7-4 retains assignments until deleted); otherwise releases to the
    /// aircraft's natural schedule. No-op when the maneuver never reduced speed.
    /// </summary>
    public void Resume(PhaseContext ctx)
    {
        if (!SpeedReduced)
        {
            return;
        }

        if (PriorHasExplicitSpeed)
        {
            ctx.Targets.TargetSpeed = PriorTargetSpeed;
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
    /// Cancels the pending auto-resume when the controller takes over speed mid-maneuver, so
    /// completing the maneuver does not clobber the newly assigned speed.
    /// </summary>
    public void CancelAutoResume()
    {
        SpeedReduced = false;
    }
}
