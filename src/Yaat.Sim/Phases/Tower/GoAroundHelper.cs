using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Pattern;

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

        ctx.Aircraft.Phases.TrafficDirection = ResolvePatternIntent(ctx.Aircraft);

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
    /// Resolves whether the aircraft should re-enter the traffic pattern after a go-around, and on
    /// which side. Shared by the automatic go-around (<see cref="Trigger"/>) and the <c>GA</c> command
    /// (<c>PatternCommandHandler.TryGoAround</c>) so both entry points agree — a controller-issued
    /// go-around must not behave differently from one the simulation triggers itself.
    ///
    /// Per the Pilot/Controller Glossary entry for GO AROUND, a VFR aircraft or one on a visual approach
    /// overflies the runway climbing to pattern altitude and enters the pattern via the crosswind leg
    /// unless ATC advises otherwise. An aircraft already flying a pattern keeps its direction; otherwise
    /// the side comes from the controller's last persistent MLT/MRT intent, then the pattern legs the
    /// aircraft has been flying (an <c>ERD 28R</c> survives a <c>CLAND</c>, which drops both direction
    /// fields to signal full-stop intent), then the runway's L/R suffix, then left traffic (AIM 4-3-3.3).
    /// IFR aircraft not already in a pattern return null — they fly the published missed approach or
    /// runway heading and await instructions.
    /// </summary>
    internal static PatternDirection? ResolvePatternIntent(AircraftState aircraft)
    {
        var phases = aircraft.Phases;
        if (phases is null)
        {
            return null;
        }

        if (phases.TrafficDirection is { } current)
        {
            return current;
        }

        if (!aircraft.FlightPlan.IsVfr)
        {
            return null;
        }

        return aircraft.Pattern.TrafficDirection
            ?? InferDirectionFromPatternLegs(phases)
            ?? InferDefaultPatternDirection(phases.AssignedRunway)
            ?? PatternDirection.Left;
    }

    /// <summary>
    /// Recovers the pattern side from the aircraft's own pattern legs. Completed legs stay in the
    /// phase list with their waypoints intact, so a circuit flown before the direction fields were
    /// cleared still names the side the controller assigned.
    /// </summary>
    private static PatternDirection? InferDirectionFromPatternLegs(PhaseList phases)
    {
        foreach (var phase in phases.Phases)
        {
            var direction = phase switch
            {
                UpwindPhase { Waypoints: { } up } => up.Direction,
                CrosswindPhase { Waypoints: { } cw } => cw.Direction,
                DownwindPhase { Waypoints: { } dw } => dw.Direction,
                BasePhase { Waypoints: { } bp } => bp.Direction,
                _ => (PatternDirection?)null,
            };

            if (direction is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Handoff margin below pattern altitude (AIM 4-3-2); mirrors <c>UpwindPhase</c>'s crosswind-turn gate.</summary>
    internal const double PatternHandoffMarginFt = 300.0;

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
            return (int)(ResolvePatternAltitudeMsl(ctx) - PatternHandoffMarginFt);
        }

        return null;
    }

    /// <summary>
    /// Pattern altitude (feet MSL) the go-around climbs toward. Resolves the airport-authored TPA
    /// and any commanded override (<c>MRT 15</c>) exactly as the auto-cycled circuit does in
    /// <c>PhaseRunner</c>, so the go-around hands off at the same altitude the following
    /// <c>UpwindPhase</c> levels at. Falls back to the context's field elevation when no runway is
    /// assigned — a nullable-arithmetic collapse there would silently revert the aircraft to the
    /// 2000 ft AGL self-clear climb.
    /// </summary>
    private static double ResolvePatternAltitudeMsl(PhaseContext ctx)
    {
        double categoryAglFt = CategoryPerformance.PatternAltitudeAgl(ctx.Category);
        if (ctx.Runway is not { } runway)
        {
            return ctx.FieldElevation + categoryAglFt;
        }

        var layout = ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout;
        var (_, altitudeOverrideFt) = PatternGeometry.ResolveAuthoredOverrides(
            runway,
            layout?.FindRunway(runway.Designator),
            commandSizeNm: null,
            ctx.Aircraft.Pattern.AltitudeOverrideFt
        );

        return altitudeOverrideFt ?? (runway.ElevationFt + categoryAglFt);
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
    /// Infers the VFR pattern direction from a runway's L/R suffix when a parallel sibling exists.
    /// 28R with 28L present → Right; 28L with 28R present → Left. Returns null for single runways,
    /// center parallels (28C), or when no parallel sibling exists, leaving the caller to fall back to
    /// left traffic (AIM 4-3-3.3).
    ///
    /// This is a convention, not a rule: right patterns aren't charted at towered fields (AIM 4-3-3
    /// note 3), so the tower assigns the side and there is nothing to look up. Parallels are lettered
    /// left-to-right as seen from the approach (AIM 4-3-6 note 1), so sending each runway's pattern
    /// outboard is the guess that keeps the two circuits from overlapping. Only reached when neither
    /// the controller's stated intent nor the aircraft's own flown legs name a side.
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
