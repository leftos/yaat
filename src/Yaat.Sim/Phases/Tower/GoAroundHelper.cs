using Yaat.Sim.Commands;
using Yaat.Sim.Data;

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

        // VFR aircraft without a pattern direction get a sensible default. For
        // parallel runways with L/R suffixes, the runway side determines the
        // pattern (28L → left, 28R → right) per AIM 4-3-3 convention. Otherwise
        // (single runway, or center parallel like 28C) fall back to left traffic.
        if (ctx.Aircraft.FlightPlan.IsVfr && ctx.Aircraft.Phases.TrafficDirection is null)
        {
            ctx.Aircraft.Phases.TrafficDirection = InferDefaultPatternDirection(ctx.Aircraft.Phases.AssignedRunway) ?? PatternDirection.Left;
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

    /// <summary>
    /// Infers the conventional VFR pattern direction from a runway's L/R suffix
    /// when a parallel sibling exists. 28R with 28L present → Right; 28L with 28R
    /// present → Left. Returns null for single runways, center parallels (28C),
    /// or when no parallel sibling exists, leaving the caller to fall back to a
    /// generic default.
    /// </summary>
    internal static PatternDirection? InferDefaultPatternDirection(RunwayInfo? runway)
    {
        if (runway is null)
        {
            return null;
        }

        var (number, suffix) = SplitDesignator(runway.Designator);
        if (suffix is not ('L' or 'R') || number is null)
        {
            return null;
        }

        // GetRunways returns one entry per physical runway; the active Designator
        // can be either End1 or End2 (whichever was loaded as the default). Check
        // both ends to find the sibling.
        var siblings = NavigationDatabase.Instance.GetRunways(runway.AirportId);
        char siblingSuffix = suffix == 'L' ? 'R' : 'L';
        bool hasSibling = false;
        foreach (var rwy in siblings)
        {
            var (end1Num, end1Sfx) = SplitDesignator(rwy.Id.End1);
            var (end2Num, end2Sfx) = SplitDesignator(rwy.Id.End2);
            if ((end1Num == number && end1Sfx == siblingSuffix) || (end2Num == number && end2Sfx == siblingSuffix))
            {
                hasSibling = true;
                break;
            }
        }

        if (!hasSibling)
        {
            return null;
        }

        return suffix == 'R' ? PatternDirection.Right : PatternDirection.Left;
    }

    private static (string? Number, char? Suffix) SplitDesignator(string designator)
    {
        if (string.IsNullOrEmpty(designator))
        {
            return (null, null);
        }

        char last = designator[^1];
        if (last is 'L' or 'R' or 'C')
        {
            return (designator[..^1], last);
        }

        return (designator, null);
    }
}
