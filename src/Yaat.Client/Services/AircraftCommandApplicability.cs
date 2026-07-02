using Yaat.Client.Models;

namespace Yaat.Client.Services;

/// <summary>
/// Single source of truth for whether a tower / ground / landing / pattern command
/// is contextually valid for an aircraft's current state. The three right-click menu
/// surfaces (aircraft list, ground view, radar view) all consult these predicates so
/// they show only commands that make sense for the aircraft, and stay consistent.
///
/// The aircraft phase strings and IFR/VFR rules encoded here were validated against
/// FAA 7110.65 §3-9/§3-10 and AIM 4-3-23, cross-checked with the server-side command
/// handlers (PatternCommandHandler / DepartureClearanceHandler / GroundCommandHandler).
/// The server remains authoritative — these predicates only suppress UI clutter.
/// </summary>
public static class AircraftCommandApplicability
{
    /// <summary>
    /// Airborne phases where the aircraft has a pending landing (instrument approach or
    /// VFR pattern circuit). Landing/option/go-around clearances are issued throughout
    /// this window, not just on final (7110.65 §3-10-5, AIM 4-3-23).
    /// </summary>
    private static bool IsPendingLandingPhase(string phase)
    {
        return phase
            is "FinalApproach"
                or "ApproachNav"
                or "InterceptCourse"
                or "Pattern Entry"
                or "Upwind"
                or "Crosswind"
                or "Downwind"
                or "Base"
                or "MidfieldCrossing";
    }

    /// <summary>
    /// Transient maneuvers that interrupt an approach or pattern — controller-commanded
    /// 360/270 orbits ("TurnL360"/"TurnR270"), S-turns, an instrument procedure turn, or a
    /// teardrop holding-pattern entry — drop the approach/leg name from CurrentPhase. They
    /// only count as "on an arrival" when a landing is still pending (see <see cref="IsOnArrival"/>),
    /// which excludes an enroute aircraft given a 360 for spacing.
    /// </summary>
    private static bool IsTransientArrivalManeuver(string phase)
    {
        return phase is "S-Turns" or "ProcedureTurn" or "TeardropReentry" || phase.StartsWith("Turn", StringComparison.Ordinal);
    }

    /// <summary>True when a landing phase is still pending anywhere in the phase sequence.</summary>
    private static bool HasPendingLandingPhase(AircraftModel ac)
    {
        if (string.IsNullOrEmpty(ac.PhaseSequence))
        {
            return false;
        }

        foreach (var name in ac.PhaseSequence.Split(" > "))
        {
            if (name is "FinalApproach" or "Landing" or "Landing-H")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the aircraft is on an approach or in the pattern with a landing pending —
    /// either by its current leg/approach phase, or while in a transient maneuver that still
    /// has a landing pending in the sequence.
    /// </summary>
    private static bool IsOnArrival(AircraftModel ac)
    {
        var phase = ac.CurrentPhase ?? "";
        return IsPendingLandingPhase(phase) || (IsTransientArrivalManeuver(phase) && HasPendingLandingPhase(ac));
    }

    internal static bool IsGroundPhase(string phase)
    {
        return phase
                is "At Parking"
                    or "Pushback"
                    or "Pushback to Spot"
                    or "Holding After Pushback"
                    or "Taxiing"
                    or "Holding In Position"
                    or "Crossing Runway"
                    or "Runway Exit"
                    or "Holding After Exit"
                    or "AirTaxi"
                    or "LiningUp"
                    or "LinedUpAndWaiting"
            || phase.StartsWith("Holding Short", StringComparison.Ordinal)
            || phase.StartsWith("Following", StringComparison.Ordinal);
    }

    internal static bool IsPatternPhase(string phase)
    {
        return phase is "Pattern Entry" or "Upwind" or "Crosswind" or "Downwind" or "Base" or "MidfieldCrossing";
    }

    internal static bool IsHoldingPhase(string phase)
    {
        return phase is "HoldingPattern" or "HPP-L" or "HPP-R" or "HPP" or "HoldingAtFix" or "ProceedToFix";
    }

    internal static bool IsTurnPhase(string phase)
    {
        return phase is "S-Turns" || phase.StartsWith("Turn", StringComparison.Ordinal);
    }

    /// <summary>True when the aircraft is operating under VFR.</summary>
    public static bool IsVfr(AircraftModel? ac)
    {
        return ac is not null && string.Equals(ac.FlightRules, "VFR", StringComparison.OrdinalIgnoreCase);
    }

    // --- Departures ---

    /// <summary>Line up and wait — a departure that has reached, or is taxiing to, its runway.</summary>
    public static bool CanLineUpAndWait(AircraftModel? ac)
    {
        if (ac is null || !ac.IsOnGround)
        {
            return false;
        }

        var phase = ac.CurrentPhase ?? "";
        return phase.StartsWith("Holding Short", StringComparison.Ordinal) || (phase == "Taxiing" && !string.IsNullOrEmpty(ac.AssignedRunway));
    }

    /// <summary>
    /// Cleared for takeoff — a ground departure at/approaching the runway. CTO during
    /// "Taxiing" is stored as a deferred clearance applied when the aircraft reaches
    /// the runway, so only offer it when a runway is already assigned.
    /// </summary>
    public static bool CanClearForTakeoff(AircraftModel? ac)
    {
        if (ac is null || !ac.IsOnGround)
        {
            return false;
        }

        var phase = ac.CurrentPhase ?? "";
        return phase is "LinedUpAndWaiting" or "LiningUp" or "Takeoff"
            || phase.StartsWith("Holding Short", StringComparison.Ordinal)
            || (phase == "Taxiing" && !string.IsNullOrEmpty(ac.AssignedRunway));
    }

    /// <summary>
    /// Whether to show the VFR-only takeoff modifiers (closed-traffic / pattern entries
    /// off the departure). IFR departures get runway-heading / on-course only — the
    /// server rejects the pattern modifiers for IFR (CheckIfrDepartureCompatibility).
    /// </summary>
    public static bool ShowVfrTakeoffModifiers(AircraftModel? ac)
    {
        return IsVfr(ac);
    }

    /// <summary>Cancel takeoff clearance — a departure that has been cleared/lined up.</summary>
    public static bool CanCancelTakeoff(AircraftModel? ac)
    {
        if (ac is null || !ac.IsOnGround)
        {
            return false;
        }

        var phase = ac.CurrentPhase ?? "";
        return phase is "LinedUpAndWaiting" or "LiningUp" or "Takeoff";
    }

    // --- Arrivals / pattern landing ---

    /// <summary>Cleared to land — any phase with a pending landing, plus go-around (re-clear).</summary>
    public static bool CanClearToLand(AircraftModel? ac)
    {
        if (ac is null)
        {
            return false;
        }

        return IsOnArrival(ac) || (ac.CurrentPhase ?? "") == "GoAround";
    }

    /// <summary>
    /// Cleared for the option / touch-and-go / stop-and-go / low approach. Same window as
    /// "cleared to land" but VFR-only — the server gates these behind RequiresVfr.
    /// </summary>
    public static bool CanIssueVfrOption(AircraftModel? ac)
    {
        return CanClearToLand(ac) && IsVfr(ac);
    }

    /// <summary>
    /// Go around / missed approach — pending-landing phases and the climb-out of an
    /// option maneuver. Deliberately NOT offered during "Landing" rollout (a go-around
    /// once committed/decelerating on the runway is a rejected-landing edge case).
    /// </summary>
    public static bool CanGoAround(AircraftModel? ac)
    {
        if (ac is null)
        {
            return false;
        }

        var phase = ac.CurrentPhase ?? "";
        return IsOnArrival(ac) || phase is "TouchAndGo" or "StopAndGo" or "LowApproach";
    }

    /// <summary>Cancel landing clearance — only when a landing clearance is currently set.</summary>
    public static bool CanCancelLandingClearance(AircraftModel? ac)
    {
        return ac is not null && !string.IsNullOrEmpty(ac.LandingClearance);
    }

    /// <summary>
    /// Exit left / right after touchdown. Fixed-wing only — helicopters ("Landing-H")
    /// land to a spot, not a runway exit.
    /// </summary>
    public static bool CanExitRunway(AircraftModel? ac)
    {
        if (ac is null)
        {
            return false;
        }

        var phase = ac.CurrentPhase ?? "";
        return phase is "Landing" or "Runway Exit";
    }

    // --- Ground routing ---

    /// <summary>
    /// Draw / preset a taxi route — offered for on-ground aircraft that are parked, taxiing,
    /// or stopped/held/following mid-ground, so a controller can (re)route them from their
    /// current position. Excludes transient runway phases (crossing / exit / line-up) and
    /// airborne states. Drawing sends a fresh TAXI that clears the active ground phase and
    /// re-plans, so the target phases must accept a new TAXI (HoldingInPositionPhase /
    /// FollowingPhase treat it as ClearsPhase). The airborne pattern-follow phase is named
    /// "VFR Follow", so the "Following" prefix here matches only the ground taxi-follow.
    /// </summary>
    public static bool CanDrawTaxiRoute(AircraftModel? ac)
    {
        if (ac is null || !ac.IsOnGround)
        {
            return false;
        }

        var phase = ac.CurrentPhase ?? "";
        return phase
                is "At Parking"
                    or "Pushback"
                    or "Pushback to Spot"
                    or "Taxiing"
                    or "Holding After Exit"
                    or "Holding After Pushback"
                    or "Holding In Position"
            || phase.StartsWith("Holding Short", StringComparison.Ordinal)
            || phase.StartsWith("Following", StringComparison.Ordinal);
    }

    // --- Pattern ---

    /// <summary>
    /// Enter the traffic pattern (downwind / base / straight-in). Airborne VFR aircraft
    /// that are inbound (free-flight), holding, or already in a pending-landing phase can
    /// be sequenced into the pattern. Pattern operations are VFR-only.
    /// </summary>
    public static bool CanEnterPattern(AircraftModel? ac)
    {
        if (ac is null || ac.IsOnGround || !IsVfr(ac))
        {
            return false;
        }

        var phase = ac.CurrentPhase ?? "";
        return string.IsNullOrEmpty(phase) || IsPendingLandingPhase(phase) || IsHoldingPhase(phase);
    }
}
