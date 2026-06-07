using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Phases;

/// <summary>
/// Builds a short human-readable label for the phase chain that is about to be
/// cleared by a dispatcher-side phase reset. Used by the phase-cancellation
/// warning surfaced through <c>AircraftState.PendingWarnings</c>.
///
/// Recognises pattern chains (returns <c>"pattern to RWY 28R"</c>) and the
/// approach families (returns <c>"approach to RWY 28R"</c>); falls back to the
/// active phase's <see cref="Phase.Name"/> for everything else.
/// </summary>
internal static class PhaseClearSummary
{
    /// <summary>
    /// Returns a label describing what will be cancelled, or <c>null</c> when
    /// there is no active phase to describe.
    /// </summary>
    public static string? Build(PhaseList phases)
    {
        var current = phases.CurrentPhase;
        if (current is null)
        {
            return null;
        }

        // Walk pending phases from the current index forward. A pattern chain is
        // recognised when at least two pattern-family phases remain (e.g. Pattern
        // Entry → Downwind → Base → Final → Landing). The runway tag comes from
        // PatternRunway when set (cross-runway closed traffic) or AssignedRunway.
        int patternFamilyCount = 0;
        int totalRemaining = 0;
        for (int i = phases.CurrentIndex; i < phases.Phases.Count; i++)
        {
            var p = phases.Phases[i];
            if (p.Status is not (PhaseStatus.Active or PhaseStatus.Pending))
            {
                continue;
            }

            totalRemaining++;
            if (IsPatternFamily(p))
            {
                patternFamilyCount++;
            }
        }

        if (patternFamilyCount >= 2 && patternFamilyCount == totalRemaining)
        {
            var runwayId = phases.PatternRunway?.Designator ?? phases.AssignedRunway?.Designator;
            return runwayId is not null ? $"pattern to RWY {RunwayIdentifier.ToDisplayDesignator(runwayId)}" : "pattern";
        }

        if (current is FinalApproachPhase or InterceptCoursePhase or ApproachNavigationPhase or ProcedureTurnPhase or HoldingPatternPhase)
        {
            var runwayId = phases.AssignedRunway?.Designator ?? phases.ActiveApproach?.RunwayId;
            return runwayId is not null ? $"approach to RWY {RunwayIdentifier.ToDisplayDesignator(runwayId)}" : "approach";
        }

        return current.Name;
    }

    private static bool IsPatternFamily(Phase p) =>
        p
            is PatternEntryPhase
                or UpwindPhase
                or CrosswindPhase
                or DownwindPhase
                or BasePhase
                or MidfieldCrossingPhase
                or TeardropReentryPhase
                or VfrFollowPhase
                or FinalApproachPhase
                or LandingPhase;
}
