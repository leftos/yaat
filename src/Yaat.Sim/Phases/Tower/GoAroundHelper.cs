using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Shared go-around trigger used by <see cref="FinalApproachPhase"/> and
/// <see cref="LandingPhase"/>. Inserts a <see cref="GoAroundPhase"/> (plus any
/// instrument-approach missed-approach phases) and advances the phase list.
///
/// Both phases delegate here so the go-around wiring — VFR pattern-direction
/// default, MAP phase selection, pattern-altitude target, and phase-list
/// replacement — lives in one place.
/// </summary>
internal static class GoAroundHelper
{
    /// <summary>
    /// Trigger a go-around from any predecessor phase. Adds a warning, picks
    /// the appropriate missed-approach phase sequence, and advances the
    /// phase list to the new <see cref="GoAroundPhase"/>.
    /// </summary>
    public static void Trigger(PhaseContext ctx, string reason)
    {
        if (ctx.Aircraft.Phases is null)
        {
            return;
        }

        ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} is going around ({reason})");

        // VFR aircraft without a pattern direction default to left traffic
        if (ctx.Aircraft.IsVfr && ctx.Aircraft.Phases.TrafficDirection is null)
        {
            ctx.Aircraft.Phases.TrafficDirection = PatternDirection.Left;
        }

        bool isPattern = ctx.Aircraft.Phases.TrafficDirection is not null;

        // For instrument approaches with MAP data, use MAP altitude and queue MAP phases
        var mapPhases = isPattern ? [] : ApproachCommandHandler.BuildMissedApproachPhases(ctx.Aircraft);
        int? targetAlt;
        if (mapPhases.Count > 0)
        {
            var mapFixes = ctx.Aircraft.Phases.ActiveApproach!.MissedApproachFixes;
            targetAlt = ApproachCommandHandler.GetMissedApproachAltitude(mapFixes);
        }
        else if (isPattern)
        {
            // AIM 4-3-2: hand off to UpwindPhase 300ft below pattern altitude so the
            // crosswind turn becomes available at the same threshold as a VFR departure.
            targetAlt = (int?)(ctx.Runway?.ElevationFt + CategoryPerformance.PatternAltitudeAgl(ctx.Category) - 300.0);
        }
        else
        {
            targetAlt = null;
        }

        var goAround = new GoAroundPhase { TargetAltitude = targetAlt, ReenterPattern = isPattern };

        var phases = new List<Phase> { goAround };
        phases.AddRange(mapPhases);

        ctx.Aircraft.Phases.ReplaceUpcoming(phases);
        ctx.Aircraft.Phases.AdvanceToNext(ctx);
    }
}
