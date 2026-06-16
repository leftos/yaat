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

        Pilot.PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            Pilot.PilotResponder.BuildGoingAround(ctx.Aircraft, reason),
            Pilot.PilotResponder.SoloPositionsTower
        );

        // VFR aircraft without a pattern direction get a sensible default. The
        // controller's last persistent MLT/MRT intent wins (preserves direction
        // across landings/vectors). For parallel runways with L/R suffixes, the
        // runway side determines the pattern (28L → left, 28R → right) per AIM
        // 4-3-3 convention. Otherwise fall back to left traffic.
        if (ctx.Aircraft.FlightPlan.IsVfr && ctx.Aircraft.Phases.TrafficDirection is null)
        {
            ctx.Aircraft.Phases.TrafficDirection =
                ctx.Aircraft.Pattern.TrafficDirection ?? InferDefaultPatternDirection(ctx.Aircraft.Phases.AssignedRunway) ?? PatternDirection.Left;
        }

        bool isPattern = ctx.Aircraft.Phases.TrafficDirection is not null;

        // For instrument approaches with MAP data, queue the published missed-approach phases.
        var missedApproachPhases = isPattern ? [] : ApproachCommandHandler.BuildMissedApproachPhases(ctx.Aircraft);

        var goAround = new GoAroundPhase
        {
            TargetAltitude = ResolveClimbOutAltitude(ctx, isPattern, missedApproachPhases),
            ReenterPattern = isPattern,
            NextLandingFullStop = CaptureLandingFullStopIntent(ctx.Aircraft.Phases),
        };

        InstallGoAroundPhases(ctx, goAround, missedApproachPhases);
    }

    /// <summary>
    /// Resolve the climb-out target altitude for a go-around when the controller did not
    /// specify one: the published missed-approach altitude when missed-approach phases were
    /// built, otherwise pattern altitude (300 ft below TPA, AIM 4-3-2) for a pattern
    /// go-around, otherwise null (the phase self-clears at 2000 ft AGL).
    /// </summary>
    internal static int? ResolveClimbOutAltitude(PhaseContext ctx, bool isPattern, IReadOnlyList<Phase> missedApproachPhases)
    {
        if (missedApproachPhases.Count > 0)
        {
            var mapFixes = ctx.Aircraft.Phases!.ActiveApproach!.MissedApproachFixes;
            return ApproachCommandHandler.GetMissedApproachAltitude(mapFixes);
        }

        // AIM 4-3-2: hand off to UpwindPhase 300ft below pattern altitude so the crosswind
        // turn becomes available at the same threshold as a VFR departure.
        if (isPattern)
        {
            return (int?)(ctx.Runway?.ElevationFt + CategoryPerformance.PatternAltitudeAgl(ctx.Category) - 300.0);
        }

        return null;
    }

    /// <summary>
    /// Replace the upcoming phases with the go-around (plus any missed-approach phases),
    /// advance the phase list to it, and fire the phase-advanced hook. Shared by the
    /// automatic (<see cref="Trigger"/>) and manual (GA command) go-around paths.
    /// </summary>
    internal static void InstallGoAroundPhases(PhaseContext ctx, GoAroundPhase goAround, IReadOnlyList<Phase> missedApproachPhases)
    {
        var phaseList = ctx.Aircraft.Phases!;

        var phases = new List<Phase> { goAround };
        phases.AddRange(missedApproachPhases);

        phaseList.ReplaceUpcoming(phases);
        phaseList.AdvanceToNext(ctx);
        FlightPhysics.NotifyPhaseAdvanced(ctx.Aircraft);
    }

    /// <summary>
    /// Captures the aircraft's pre-go-around landing intent from the last pending
    /// approach-ending phase. Drives the next auto-cycled circuit's terminator: a
    /// TG aircraft keeps cycling TG (returns false), a landing aircraft keeps trying
    /// to land (returns true). Without this, every VFR aircraft would be forced into
    /// TG cycling after a go-around regardless of what the pilot was originally doing.
    /// Default true (full-stop) when no terminator is queued — a visual aircraft
    /// stays in landing intent instead of being silently switched to TG.
    /// </summary>
    internal static bool CaptureLandingFullStopIntent(PhaseList list)
    {
        for (int i = list.Phases.Count - 1; i >= 0; i--)
        {
            var phase = list.Phases[i];
            if (phase.Status != PhaseStatus.Pending)
            {
                continue;
            }

            switch (phase)
            {
                case LandingPhase:
                case HelicopterLandingPhase:
                    return true;
                case TouchAndGoPhase:
                case StopAndGoPhase:
                case LowApproachPhase:
                    return false;
            }
        }

        return true;
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
