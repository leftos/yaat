using System.Diagnostics.CodeAnalysis;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Situation;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Single source of truth for whether a tower / ground / landing / pattern command
/// is contextually valid for an aircraft's current state. The three right-click menu
/// surfaces (aircraft list, ground view, radar view) all consult these predicates so
/// they show only commands that make sense for the aircraft, and stay consistent.
///
/// The aircraft phase strings and IFR/VFR rules encoded here were validated against
/// FAA 7110.65 §3-9/§3-10 and AIM 4-3-23, cross-checked with the command handlers
/// (PatternCommandHandler / DepartureClearanceHandler / GroundCommandHandler).
///
/// <para>The phase-based predicates only suppress UI clutter — the command handlers still
/// reject a maneuver that makes no sense from the aircraft's current state. The flight-rules
/// predicates are different: the simulation does not gate on IFR-vs-VFR at all, so the
/// <see cref="VfrCommandsForIfr"/> checks here are the enforcement for menu-issued commands
/// (typed commands go through <c>VfrCommandGate</c>). Dropping a mode check here means
/// the command goes through.</para>
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
    private static bool IsTransientArrivalManeuver(string phase) =>
        phase is "S-Turns" or "ProcedureTurn" or "TeardropReentry" || phase.StartsWith("Turn", StringComparison.Ordinal);

    /// <summary>True when a landing phase is still pending anywhere in the phase sequence.</summary>
    private static bool HasPendingLandingPhase(IMenuAircraft ac)
    {
        if (string.IsNullOrEmpty(ac.PhaseSequence))
        {
            return false;
        }

        foreach (string name in ac.PhaseSequence.Split(" > "))
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
    private static bool IsOnArrival(IMenuAircraft ac)
    {
        string phase = ac.CurrentPhase;
        return IsPendingLandingPhase(phase) || (IsTransientArrivalManeuver(phase) && HasPendingLandingPhase(ac));
    }

    internal static bool IsGroundPhase(string phase)
    {
        return phase
                is "At Parking"
                    or "Pushback"
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

    internal static bool IsPatternPhase(string phase) =>
        phase is "Pattern Entry" or "Upwind" or "Crosswind" or "Downwind" or "Base" or "MidfieldCrossing";

    internal static bool IsHoldingPhase(string phase) => phase is "HoldingPattern" or "HPP-L" or "HPP-R" or "HPP" or "HoldingAtFix" or "ProceedToFix";

    internal static bool IsTurnPhase(string phase) => phase is "S-Turns" || phase.StartsWith("Turn", StringComparison.Ordinal);

    // --- Live traffic ---

    /// <summary>
    /// Whether the sim will accept flight / ground commands for this aircraft at all. A live-traffic shadow
    /// (<see cref="IMenuAircraft.IsLiveTraffic"/>) is read-only until assumed — the server rejects every such
    /// command with "ASSUME first" — but a command sent to one the sim would auto-assume
    /// (<see cref="CanAssume"/>) counts as controllable: the dispatcher assumes the shadow and applies the
    /// command in the same call, exactly as it does for a typed command. A surface shadow is never assumable
    /// and stays uncontrollable, so no maneuver predicate below offers anything for it.
    /// </summary>
    public static bool IsControllable([NotNullWhen(true)] IMenuAircraft? ac) => ac is not null && (!ac.IsLiveTraffic || CanAssume(ac));

    /// <summary>
    /// A controllable aircraft in the air, which a point menu's heading, direct-to and hold items command toward the
    /// right-clicked point.
    /// </summary>
    public static bool IsAirborneControllable([NotNullWhen(true)] IMenuAircraft? ac) => IsControllable(ac) && !ac.IsOnGround;

    /// <summary>
    /// Assume control of a live-traffic shadow (<c>ASSUME</c>): airborne shadows only. Surface shadows come from
    /// ASDE-X with no flight plan or air vector to seed a simulated aircraft from, so they are never assumable.
    /// </summary>
    public static bool CanAssume(IMenuAircraft? ac) => ac is { IsLiveTraffic: true, IsOnGround: false };

    /// <summary>
    /// A live-traffic shadow that cannot be assumed (<see cref="CanAssume"/>): its menu is read-only, so nothing on it may
    /// command the aircraft.
    /// </summary>
    public static bool IsSurfaceShadow([NotNullWhen(true)] IMenuAircraft? ac) => (ac is { IsLiveTraffic: true }) && !CanAssume(ac);

    /// <summary>
    /// Ask-pilot queries ("say altitude", "say heading") — never for a live-traffic shadow, assumable or not:
    /// they are read-only queries and the auto-assume gate skips those (<c>IsReadOnlyQuery</c>), so the server
    /// answers every one of them with "ASSUME first".
    /// </summary>
    public static bool CanAskPilot(IMenuAircraft? ac) => ac is { IsLiveTraffic: false };

    /// <summary>
    /// Edit an aircraft's flight plan — never for a live-traffic shadow, assumable or not: the flight-plan editor
    /// is not a command, so the edit never reaches the dispatcher's auto-assume gate and the server refuses it.
    /// </summary>
    public static bool CanEditFlightPlan(IMenuAircraft? ac) => ac is { IsLiveTraffic: false };

    /// <summary>
    /// Release an assumed aircraft back to the live feed (<c>UNASSUME</c>): only an aircraft the sim is flying
    /// that came from the feed in the first place — the sim refuses it for a scenario aircraft and for a shadow.
    /// </summary>
    public static bool CanUnassume(IMenuAircraft? ac) => ac is { AssumedFromLiveTraffic: true, IsLiveTraffic: false };

    /// <summary>True when the aircraft is operating under VFR.</summary>
    public static bool IsVfr(IMenuAircraft? ac) => ac is not null && string.Equals(ac.FlightRules, "VFR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a VFR-only command may be offered for this aircraft: always for a VFR aircraft,
    /// and for an IFR aircraft only when the controller opted into the full set.
    /// </summary>
    private static bool AllowsVfrOnly(IMenuAircraft? ac, VfrCommandsForIfr mode) =>
        IsControllable(ac) && (IsVfr(ac) || mode == VfrCommandsForIfr.All);

    // --- Departures ---

    /// <summary>
    /// Line up and wait — a departure holding short of a runway it can name (the held runway, else the assigned one,
    /// <see cref="HoldShortMenuHelper.HeldRunway(IMenuAircraft?)"/>), or taxiing with a runway assigned.
    /// </summary>
    public static bool CanLineUpAndWait(IMenuAircraft? ac)
    {
        if (!IsControllable(ac) || !ac.IsOnGround)
        {
            return false;
        }

        return IsHoldingShortOfANamedRunway(ac) || IsTaxiingWithAnAssignedRunway(ac);
    }

    /// <summary>
    /// Cleared for takeoff — a departure holding short of a runway it can name
    /// (<see cref="HoldShortMenuHelper.HeldRunway(IMenuAircraft?)"/>), taxiing with a runway assigned (stored as a
    /// deferred clearance applied when the aircraft reaches the runway), lined up, or lining up (the sim upgrades a
    /// line-up in progress). Never once rolling.
    /// </summary>
    public static bool CanClearForTakeoff(IMenuAircraft? ac)
    {
        if (!IsControllable(ac) || !ac.IsOnGround)
        {
            return false;
        }

        return (ac.CurrentPhase is "LinedUpAndWaiting" or "LiningUp") || IsHoldingShortOfANamedRunway(ac) || IsTaxiingWithAnAssignedRunway(ac);
    }

    private static bool IsHoldingShortOfANamedRunway(IMenuAircraft ac) =>
        ac.CurrentPhase.StartsWith("Holding Short", StringComparison.Ordinal) && (HoldShortMenuHelper.HeldRunway(ac) is { Length: > 0 });

    private static bool IsTaxiingWithAnAssignedRunway(IMenuAircraft ac) => (ac.CurrentPhase == "Taxiing") && !string.IsNullOrEmpty(ac.AssignedRunway);

    /// <summary>
    /// Whether to show the VFR-only takeoff modifiers (closed-traffic / pattern entries
    /// off the departure). An IFR departure normally gets a bare CTO, an assigned heading,
    /// or runway heading only; the pattern-relative modifiers appear for it only when the
    /// controller opted into the full VFR command set.
    /// </summary>
    public static bool ShowVfrTakeoffModifiers(IMenuAircraft? ac, VfrCommandsForIfr mode) => AllowsVfrOnly(ac, mode);

    /// <summary>Cancel takeoff clearance — a departure lining up, lined up and waiting, or rolling.</summary>
    public static bool CanCancelTakeoff(IMenuAircraft? ac)
    {
        if (!IsControllable(ac) || !ac.IsOnGround)
        {
            return false;
        }

        string phase = ac.CurrentPhase;
        return phase is "LinedUpAndWaiting" or "LiningUp" or "Takeoff";
    }

    // --- Arrivals / pattern landing ---

    /// <summary>
    /// Cleared to land — any phase with a pending landing, plus go-around (re-clear), plus the window
    /// where a pattern entry is queued but has not fired: there is no arrival phase yet, but the
    /// clearance is pre-issued against that entry and applies when it builds its circuit.
    /// </summary>
    public static bool CanClearToLand(IMenuAircraft? ac)
    {
        if (!IsControllable(ac))
        {
            return false;
        }

        return IsOnArrival(ac) || ac.HasQueuedPatternEntry || (ac.CurrentPhase == "GoAround");
    }

    /// <summary>
    /// Cleared for the option / touch-and-go / stop-and-go / low approach. Same window as
    /// "cleared to land", but these model VFR operations, so an IFR aircraft is only offered
    /// them when the controller opted into the full VFR command set.
    /// </summary>
    public static bool CanIssueVfrOption(IMenuAircraft? ac, VfrCommandsForIfr mode) => CanClearToLand(ac) && AllowsVfrOnly(ac, mode);

    /// <summary>
    /// Go around / missed approach — pending-landing phases and the climb-out of an
    /// option maneuver. Deliberately NOT offered during "Landing" rollout (a go-around
    /// once committed/decelerating on the runway is a rejected-landing edge case).
    /// </summary>
    public static bool CanGoAround(IMenuAircraft? ac)
    {
        if (!IsControllable(ac))
        {
            return false;
        }

        string phase = ac.CurrentPhase;
        return IsOnArrival(ac) || phase is "TouchAndGo" or "StopAndGo" or "LowApproach";
    }

    /// <summary>
    /// Cancel landing clearance — only when a clearance is currently set, either on the circuit the
    /// aircraft is flying or pre-issued against a pattern entry that is still queued.
    /// </summary>
    public static bool CanCancelLandingClearance(IMenuAircraft? ac) =>
        IsControllable(ac) && (!string.IsNullOrEmpty(ac.LandingClearance) || !string.IsNullOrEmpty(ac.PendingLandingClearance));

    /// <summary>
    /// Exit left / right in the landing and runway exit phases, and on final while the server lists the exits ahead
    /// (<see cref="IMenuAircraft.ExitsAhead"/> not null). Fixed-wing only — helicopters ("Landing-H") land to a spot, not a
    /// runway exit.
    /// </summary>
    public static bool CanExitRunway(IMenuAircraft? ac)
    {
        if (!IsControllable(ac))
        {
            return false;
        }

        string phase = ac.CurrentPhase;
        return (phase is "Landing" or "Runway Exit") || (ac.ExitsAhead is not null);
    }

    /// <summary>
    /// Exit left / right on <paramref name="side"/>: <see cref="CanExitRunway(IMenuAircraft?)"/>, and while the server lists the
    /// exits ahead, only with a listed exit on that side. The exit search falls back to the other side when nothing is makeable
    /// on the one asked for, so an exit offered on a side with none would turn the aircraft the opposite way. With no list (no
    /// layout or hold-short data) the side is not judged.
    /// </summary>
    public static bool CanExitRunway(IMenuAircraft? ac, ExitSide side) =>
        CanExitRunway(ac) && ((ac!.ExitsAhead is not { } exits) || exits.Any(row => row.Side == side));

    // --- Ground movement ---

    /// <summary>
    /// Push back (<c>PUSH</c>, including the heading and spot-destination variants). GroundCommandHandler.TryPushback
    /// accepts an aircraft at a stand and one resting on a ramp spot after a completed pushback ("Holding After
    /// Pushback"), so a pushed aircraft can be repositioned without a TAXI first. It refuses the other two holds
    /// ("Holding After Exit", "Holding In Position"), which therefore get no item — offering one that can only
    /// produce a refusal is worse than offering nothing. The <see cref="IsControllable"/> guard keeps surface
    /// live-traffic shadows, which are never assumable, out; an assumable (airborne) shadow reaches the phase test
    /// below and is refused there, having no ground phase. An aircraft parked on a taxi-out stand
    /// (<see cref="IMenuAircraft.StandDeparture"/>) gets no item: its stand is left under its own power, so every push
    /// entry is hidden, though the sim still accepts a typed <c>PUSH</c> there. A push-back stand and an either stand (pushed
    /// off or taxied out of alike) get every item. After a completed push the aircraft is off its stand, so the stand's
    /// departure no longer gates it.
    /// </summary>
    public static bool CanPushBack(IMenuAircraft? ac)
    {
        if (!IsControllable(ac))
        {
            return false;
        }

        return (ac.CurrentPhase is "Holding After Pushback") || ((ac.CurrentPhase is "At Parking") && (ac.StandDeparture != StandDeparture.TaxiOut));
    }

    /// <summary>
    /// Hold position (<c>HOLD</c>) — an aircraft that is moving under its own or a tug's power: a pushback (plain
    /// or to a spot), a taxi, or a taxi-follow. FollowingPhase is named "Following &lt;callsign&gt;", so the prefix
    /// test matches the ground follow; the airborne pattern-follow phase is named "VFR Follow" and is not matched.
    /// The <see cref="IsControllable"/> guard keeps surface live-traffic shadows, which are never assumable, out,
    /// as in <see cref="CanPushBack"/>.
    /// </summary>
    public static bool CanHoldPosition(IMenuAircraft? ac)
    {
        if (!IsControllable(ac))
        {
            return false;
        }

        string phase = ac.CurrentPhase;
        return phase is "Pushback" or "Taxiing" || phase.StartsWith("Following", StringComparison.Ordinal);
    }

    /// <summary>
    /// Resume taxi (<c>RES</c>) out of a stationary hold. GroundCommandHandler.TryResumeTaxi only clears an active
    /// hold directive and refuses ("Aircraft is not held") without one, and each of these three phases is also
    /// reachable unheld — Holding In Position after a WARPG, a completed taxi to a spot, or a rejected/cancelled
    /// takeoff — so the phase alone cannot gate the item. The hold-short holds are a different RES path (satisfying
    /// a crossing clearance, no hold directive needed) and are deliberately not covered here. The
    /// <see cref="IsControllable"/> guard keeps surface live-traffic shadows, which are never assumable, out, as
    /// in <see cref="CanPushBack"/>.
    /// </summary>
    public static bool CanResumeTaxi(IMenuAircraft? ac)
    {
        if (!IsControllable(ac))
        {
            return false;
        }

        return (ac.CurrentPhase is "Holding After Exit" or "Holding After Pushback" or "Holding In Position") && ac.IsHeld;
    }

    /// <summary>
    /// Resume taxi (<c>RES</c>) from a hold-short, the RES path that needs no hold directive: offered while the
    /// aircraft has an active taxi route to continue on. The sim takes RES at a runway-crossing or explicit hold-short
    /// bar and refuses it at a departure-runway bar or the bar ending an incomplete route; both of those end the route,
    /// so an aircraft held there has no active route. The stationary holds are <see cref="CanResumeTaxi"/>'s. The
    /// <see cref="IsControllable"/> guard keeps surface live-traffic shadows out, as in <see cref="CanPushBack"/>.
    /// </summary>
    public static bool CanResumeFromHoldShort(IMenuAircraft? ac) =>
        IsControllable(ac) && (ac.CurrentPhase.StartsWith("Holding Short", StringComparison.Ordinal)) && ac.HasActiveTaxiRoute;

    /// <summary>
    /// Cross the runway (<c>CROSS</c>) an aircraft is holding short of: the runway comes from the hold-short phase,
    /// else the assigned runway (<see cref="HoldShortMenuHelper.HeldRunway(string, IMenuAircraft?)"/>), and with neither there is no runway
    /// to name. Never at a taxiway or spot bar (<see cref="HoldShortMenuHelper.IsNonRunwayBar"/>), which protects no
    /// runway to cross. The <see cref="IsControllable"/> guard keeps surface live-traffic shadows out, as in
    /// <see cref="CanPushBack"/>.
    /// </summary>
    public static bool CanCrossRunway(IMenuAircraft? ac)
    {
        if (!IsControllable(ac))
        {
            return false;
        }

        return IsHoldingShortOfANamedRunway(ac) && !HoldShortMenuHelper.IsNonRunwayBar(ac.CurrentPhase);
    }

    /// <summary>
    /// Check the release window (<c>CFR CHECK</c>) — an aircraft on the ground that has a call-for-release window to
    /// report on. The <see cref="IsControllable"/> guard keeps surface live-traffic shadows out, as in <see cref="CanPushBack"/>.
    /// </summary>
    public static bool CanCheckReleaseWindow(IMenuAircraft? ac) => IsControllable(ac) && ac.IsOnGround && ac.HasCfrWindow;

    /// <summary>
    /// Release an aircraft held for release (<c>REL</c>). No on-ground term: a departure can be held for release off the
    /// ground. The <see cref="IsControllable"/> guard keeps surface live-traffic shadows out, as in <see cref="CanCheckReleaseWindow"/>.
    /// </summary>
    public static bool CanReleaseHeld(IMenuAircraft? ac) => IsControllable(ac) && ac.IsHeldForRelease;

    /// <summary>
    /// Break a ground conflict (<c>BREAK</c>), which overrides the ground-conflict speed limit for 15 seconds so one
    /// of two mutually stopped aircraft can push through — offered while taxiing. The <see cref="IsControllable"/>
    /// guard keeps surface live-traffic shadows out, as in <see cref="CanPushBack"/>.
    /// </summary>
    public static bool CanBreakConflict(IMenuAircraft? ac) => IsControllable(ac) && (ac.CurrentPhase == "Taxiing");

    /// <summary>
    /// Hold short of a point on the taxi route (<c>HS</c>) — offered while taxiing, the phase whose route names the
    /// targets. Its own predicate although <see cref="CanBreakConflict"/> has the same body: the two gates answer
    /// different questions and may part. The <see cref="IsControllable"/> guard keeps surface live-traffic shadows
    /// out, as in <see cref="CanPushBack"/>.
    /// </summary>
    public static bool CanHoldShort(IMenuAircraft? ac) => IsControllable(ac) && (ac.CurrentPhase == "Taxiing");

    /// <summary>
    /// Follow another ground aircraft (<c>FOLLOWG</c>) — offered at parking (start up and trail), while taxiing and in
    /// the three stationary holds, unless another on-ground aircraft is selected, when the relative items
    /// (<see cref="RelativeTraffic.OffersGroundRelative"/>) replace it. The <see cref="IsControllable"/> guard keeps
    /// surface live-traffic shadows out, as in <see cref="CanPushBack"/>.
    /// </summary>
    public static bool CanFollowBehind(IMenuAircraft? ac, MenuContext context)
    {
        if (!IsControllable(ac) || RelativeTraffic.OffersGroundRelative(ac, context))
        {
            return false;
        }

        return ac.CurrentPhase is "At Parking" or "Taxiing" or "Holding In Position" or "Holding After Exit" or "Holding After Pushback";
    }

    /// <summary>
    /// Give way to another ground aircraft (<c>GW</c>), which needs an assigned taxi route: offered while taxiing and
    /// holding in position, and in the after-exit and after-pushback holds only while the aircraft has an active taxi
    /// route, since an aircraft resting after a runway exit or a push may have none. Never at parking, which has no
    /// route; and, as with <see cref="CanFollowBehind"/>, not while the relative items replace it.
    /// </summary>
    public static bool CanGiveWayTo(IMenuAircraft? ac, MenuContext context)
    {
        if (!IsControllable(ac) || RelativeTraffic.OffersGroundRelative(ac, context))
        {
            return false;
        }

        string phase = ac.CurrentPhase;
        return (phase is "Taxiing" or "Holding In Position")
            || ((phase is "Holding After Exit" or "Holding After Pushback") && ac.HasActiveTaxiRoute);
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
    /// Only the quick list widens it, to the line-up and rollout phases (<see cref="WidensDrawTaxiRoute"/>).
    /// </summary>
    public static bool CanDrawTaxiRoute(IMenuAircraft? ac)
    {
        if (!IsControllable(ac) || !ac.IsOnGround)
        {
            return false;
        }

        string phase = ac.CurrentPhase;
        return phase is "At Parking" or "Pushback" or "Taxiing" or "Holding After Exit" or "Holding After Pushback" or "Holding In Position"
            || phase.StartsWith("Holding Short", StringComparison.Ordinal)
            || phase.StartsWith("Following", StringComparison.Ordinal);
    }

    // --- Pattern ---

    /// <summary>
    /// Whether the aircraft is in a state where it could be sequenced into the pattern at all:
    /// airborne and either inbound (free-flight), holding, or in a pending-landing phase.
    /// Says nothing about flight rules.
    /// </summary>
    private static bool IsPatternEntryEligible(IMenuAircraft? ac)
    {
        if (!IsControllable(ac) || ac.IsOnGround)
        {
            return false;
        }

        string phase = ac.CurrentPhase;
        return string.IsNullOrEmpty(phase) || IsPendingLandingPhase(phase) || IsHoldingPhase(phase);
    }

    /// <summary>
    /// Enter the traffic pattern on a circuit leg (left/right downwind, base). These put the
    /// aircraft on a full VFR circuit, so an IFR aircraft is only offered them when the
    /// controller opted into the full VFR command set. Straight-in final is separate —
    /// see <see cref="CanEnterFinal"/>.
    /// </summary>
    public static bool CanEnterPattern(IMenuAircraft? ac, VfrCommandsForIfr mode) => IsPatternEntryEligible(ac) && AllowsVfrOnly(ac, mode);

    /// <summary>
    /// Enter straight-in final (EF). This is the one pattern entry an IFR arrival flying a
    /// visual routinely needs — moving it to the parallel runway — so it is offered under the
    /// default setting as well as the full one (issue #317).
    /// </summary>
    public static bool CanEnterFinal(IMenuAircraft? ac, VfrCommandsForIfr mode) =>
        IsPatternEntryEligible(ac) && (IsVfr(ac) || mode != VfrCommandsForIfr.None);

    /// <summary>
    /// In-pattern maneuvers — leg turns, spacing adjustments, orbits, S-turns, offsets. They only
    /// make sense once the aircraft is flying the circuit, so an IFR aircraft is offered them
    /// only when the controller opted into the full VFR command set.
    /// </summary>
    public static bool CanIssuePatternManeuvers(IMenuAircraft? ac, VfrCommandsForIfr mode) => AllowsVfrOnly(ac, mode);

    // --- Quick-list visibility ---
    //
    // These read the server's situation flags and decide only whether a quick command shows in a situation's quick list;
    // All Commands keeps every entry whatever the flags say. Each answers true outside the situation its rule is about.

    /// <summary>
    /// Cleared for takeoff shows while taxiing only within reach of the departure runway's hold line
    /// (<see cref="SituationFlags.NearingDepartureHoldLine"/>); 7110.65 §3-9-10 allows it while taxiing, but further out
    /// it is clutter.
    /// </summary>
    public static bool ShowsTakeoffWhileTaxiing(IMenuAircraft? ac) =>
        ac is not null && ((ac.Situation != AircraftSituation.Taxiing) || ac.SituationFlags.HasFlag(SituationFlags.NearingDepartureHoldLine));

    /// <summary>
    /// Cleared for takeoff and Line up and wait show at a hold-short only when it is the departure runway's
    /// (<see cref="SituationFlags.HoldShortIsDepartureRunway"/>): a runway the aircraft only crosses offers neither (§3-7-2).
    /// </summary>
    public static bool ShowsDepartureClearanceAtHoldShort(IMenuAircraft? ac) =>
        ac is not null && ((ac.Situation != AircraftSituation.HoldingShort) || ac.SituationFlags.HasFlag(SituationFlags.HoldShortIsDepartureRunway));

    /// <summary>
    /// Cross runway shows at a hold-short only when it is not the departure runway's, which the aircraft departs from
    /// rather than crosses.
    /// </summary>
    public static bool ShowsCrossAtHoldShort(IMenuAircraft? ac) =>
        ac is not null && ((ac.Situation != AircraftSituation.HoldingShort) || !ac.SituationFlags.HasFlag(SituationFlags.HoldShortIsDepartureRunway));

    /// <summary>
    /// Resume taxi shows at a hold-short only when it is not the departure runway's, where the sim refuses <c>RES</c>
    /// (<c>HoldingShortPhase.CanAcceptCommand</c>). Its own predicate although <see cref="ShowsCrossAtHoldShort"/> has the
    /// same body: the two rules answer different questions and may part.
    /// </summary>
    public static bool ShowsResumeTaxiAtHoldShort(IMenuAircraft? ac) =>
        ac is not null && ((ac.Situation != AircraftSituation.HoldingShort) || !ac.SituationFlags.HasFlag(SituationFlags.HoldShortIsDepartureRunway));

    /// <summary>
    /// Cleared visual shows only for an IFR aircraft that has reported the field or the preceding traffic in sight
    /// (§7-4-3.a, §7-4-3.c.2), the reports the sim checks before it accepts the clearance.
    /// </summary>
    public static bool ShowsClearedVisual(IMenuAircraft? ac) =>
        ac is not null
        && string.Equals(ac.FlightRules, "IFR", StringComparison.OrdinalIgnoreCase)
        && (ac.SituationFlags.HasFlag(SituationFlags.HasReportedFieldInSight) || ac.SituationFlags.HasFlag(SituationFlags.HasReportedTrafficInSight));

    /// <summary>
    /// A speed adjustment (Speed, Reduce to final approach speed) hides inside the final approach fix
    /// (<see cref="SituationFlags.InsideFinalApproachFix"/>, §5-7-1); Resume normal speed is a termination and is not
    /// gated by this rule.
    /// </summary>
    public static bool ShowsSpeedAdjustment(IMenuAircraft? ac) => ac is not null && !ac.SituationFlags.HasFlag(SituationFlags.InsideFinalApproachFix);

    /// <summary>
    /// Exit left / right shows on the rollout only once the aircraft is decelerating
    /// (<see cref="SituationFlags.RolloutDecelerating"/>): no exit instruction immediately after touchdown (§3-10-9 note).
    /// </summary>
    public static bool ShowsRunwayExit(IMenuAircraft? ac) =>
        ac is not null && ((ac.Situation != AircraftSituation.RolloutExit) || ac.SituationFlags.HasFlag(SituationFlags.RolloutDecelerating));

    /// <summary>
    /// Cleared for takeoff and Line up and wait hide while a hold-for-release keeps the departure from departing: the sim
    /// refuses CTO, CTOPP and LUAW for a held aircraft until it is released.
    /// </summary>
    public static bool ShowsDepartureClearanceWhileHeld(IMenuAircraft? ac) => ac is { IsHeldForRelease: false };

    /// <summary>
    /// Cancel takeoff clearance shows only once the aircraft is cleared for takeoff (<see cref="SituationFlags.HasTakeoffClearance"/>,
    /// §3-9-10) and not past V1 (<see cref="SituationFlags.PastV1"/>), where the sim answers "unable" (§3-9-11).
    /// </summary>
    public static bool ShowsCancelTakeoff(IMenuAircraft? ac) =>
        ac is not null && ac.SituationFlags.HasFlag(SituationFlags.HasTakeoffClearance) && !ac.SituationFlags.HasFlag(SituationFlags.PastV1);

    /// <summary>
    /// Cleared approach hides once the aircraft holds an approach clearance with descent on it
    /// (<see cref="SituationFlags.ApproachClearedForDescent"/>, §5-9-4); a lateral intercept only (JFAC/JLOC) still offers it.
    /// </summary>
    public static bool ShowsApproachClearanceUntilCleared(IMenuAircraft? ac) =>
        ac is not null && !ac.SituationFlags.HasFlag(SituationFlags.ApproachClearedForDescent);

    /// <summary>Give way shows only with a taxi route, which the sim needs before it accepts <c>GW</c>.</summary>
    public static bool ShowsGiveWay(IMenuAircraft? ac) => ac is { HasActiveTaxiRoute: true };

    /// <summary>Cleared to land hides on final once a landing clearance is on the aircraft.</summary>
    public static bool ShowsClearedToLandOnFinal(IMenuAircraft? ac) =>
        ac is not null && ((ac.Situation != AircraftSituation.Final) || string.IsNullOrEmpty(ac.LandingClearance));

    /// <summary>
    /// Cleared approach shows after a go-around only once an altitude is assigned: an approach clearance carries the altitude
    /// to maintain until established (§4-8-1).
    /// </summary>
    public static bool ShowsApproachClearanceAfterGoAround(IMenuAircraft? ac) =>
        ac is not null && ((ac.Situation != AircraftSituation.GoAround) || ac.AssignedAltitude is not null);

    /// <summary>
    /// Climb via SID shows only with a SID to climb via: an active one, or one in the filed route the sim would activate
    /// (<c>NavigationCommandHandler.DispatchClimbVia</c>, which refuses <c>CVIA</c> with neither).
    /// </summary>
    public static bool ShowsClimbViaSid(IMenuAircraft? ac) => ac is not null && (!string.IsNullOrEmpty(ac.ActiveSidId) || HasFiledSid(ac));

    /// <summary>
    /// Descend via STAR shows only with a STAR to descend via: an active one, or one in the filed route the sim would
    /// activate (<c>NavigationCommandHandler.DispatchDescendVia</c>, which refuses <c>DVIA</c> with neither).
    /// </summary>
    public static bool ShowsDescendViaStar(IMenuAircraft? ac) => ac is not null && (!string.IsNullOrEmpty(ac.ActiveStarId) || HasFiledStar(ac));

    /// <summary>
    /// Whether the filed route names a SID the departure airport publishes: the sim's own lookup
    /// (<see cref="FiledProcedureLookup.FindSid"/>), which a VFR aircraft never flies.
    /// </summary>
    private static bool HasFiledSid(IMenuAircraft ac) =>
        (!IsVfr(ac)) && (NavigationDatabase.InstanceOrNull is { } navDb) && (FiledProcedureLookup.FindSid(navDb, ac.Departure, ac.Route) is not null);

    /// <summary>
    /// Whether the filed route names a STAR the destination airport publishes: the sim's own lookup
    /// (<see cref="FiledProcedureLookup.FindStar"/>).
    /// </summary>
    private static bool HasFiledStar(IMenuAircraft ac) =>
        (NavigationDatabase.InstanceOrNull is { } navDb) && (FiledProcedureLookup.FindStar(navDb, ac.Destination, ac.Route) is not null);

    // --- Quick-list widening ---
    //
    // A quick entry these admit shows in its situation's list although its catalog predicate (and so All Commands) leaves
    // it out. Each phase here is one QuickCommandSimAcceptanceTests proves the sim accepts the command in.

    /// <summary>Push route (<c>PUSHM</c>) while the push is under way: the sim takes a tug move as a redirect of the running one.</summary>
    public static bool WidensPushRoute(IMenuAircraft? ac) => IsControllable(ac) && (ac.CurrentPhase == "Pushback");

    /// <summary>
    /// Draw taxi route (a <c>TAXI</c>) while lining up, lined up and waiting, on the landing rollout and in the runway exit,
    /// which the sim accepts as a new route from where the aircraft is.
    /// </summary>
    public static bool WidensDrawTaxiRoute(IMenuAircraft? ac) =>
        IsControllable(ac) && ac.IsOnGround && (ac.CurrentPhase is "LiningUp" or "LinedUpAndWaiting" or "Landing" or "Runway Exit");

    /// <summary>
    /// A downwind or left-base pattern entry over the runway at the start of a go-around or low approach, or any leg entry
    /// in an airspace-boundary hold or an AR anchor; VFR-only, as <see cref="CanEnterPattern"/>. A right base and a
    /// straight-in final are refused over the runway by geometry, so <paramref name="takenInClimbOut"/> is false for them.
    /// </summary>
    public static bool WidensPatternLegEntry(IMenuAircraft? ac, VfrCommandsForIfr mode, bool takenInClimbOut) =>
        IsAirborneControllable(ac)
        && (IsVfr(ac) || (mode == VfrCommandsForIfr.All))
        && (IsBoundaryOrAnchorHold(ac.CurrentPhase) || (takenInClimbOut && (ac.CurrentPhase is "GoAround" or "LowApproach")));

    /// <summary>Straight-in final in an airspace-boundary hold or an AR anchor, under the same rules as <see cref="CanEnterFinal"/>.</summary>
    public static bool WidensEnterFinal(IMenuAircraft? ac, VfrCommandsForIfr mode) =>
        IsAirborneControllable(ac) && (IsVfr(ac) || (mode != VfrCommandsForIfr.None)) && IsBoundaryOrAnchorHold(ac.CurrentPhase);

    /// <summary>
    /// The rejected-takeoff "slowed to taxi speed" threshold (kt ground speed) below which Cross runway shows in a rejected
    /// takeoff: <c>CategoryPerformance.TaxiSpeed</c>'s jet figure, the highest category taxi speed.
    /// </summary>
    public const double RejectedTakeoffCrossMaxGroundSpeedKts = 30.0;

    /// <summary>
    /// Cross runway on the ground while taxiing, holding on the ground (in position, after an exit, after a push) or in a
    /// rollout/exit phase (the landing rollout, the runway exit, clearing the runway, a rejected takeoff), when the next
    /// uncleared bar on the taxi route is a runway to cross (<see cref="IMenuAircraft.NextCrossingRunway"/>, §3-7-2): the
    /// sim takes <c>CROSS</c> for it in every one of those phases, before the aircraft reaches the bar. The menu holds it
    /// back on the landing rollout until the aircraft is decelerating (<see cref="SituationFlags.RolloutDecelerating"/>, no
    /// taxi instruction immediately after touchdown, §3-10-9 note) and in a rejected takeoff until it has slowed to
    /// <see cref="RejectedTakeoffCrossMaxGroundSpeedKts"/>.
    /// </summary>
    public static bool WidensCrossRunway(IMenuAircraft? ac) =>
        IsControllable(ac)
        && ac.IsOnGround
        && (ac.Situation is AircraftSituation.Taxiing or AircraftSituation.HoldingOnGround or AircraftSituation.RolloutExit)
        && !string.IsNullOrEmpty(ac.NextCrossingRunway)
        && IsSettledForCrossing(ac);

    /// <summary>
    /// Not on the landing rollout before it decelerates, and not in a rejected takeoff above taxi speed; every other phase
    /// is settled.
    /// </summary>
    private static bool IsSettledForCrossing(IMenuAircraft ac) =>
        ac.CurrentPhase switch
        {
            "Landing" => ac.SituationFlags.HasFlag(SituationFlags.RolloutDecelerating),
            "Rejected Takeoff" => ac.GroundSpeedKnots <= RejectedTakeoffCrossMaxGroundSpeedKts,
            _ => true,
        };

    /// <summary>
    /// Cancel takeoff clearance while taxiing with a takeoff clearance stored for the runway
    /// (<see cref="SituationFlags.HasTakeoffClearance"/>, §3-9-10): the sim drops the stored clearance and the aircraft
    /// holds short.
    /// </summary>
    public static bool WidensCancelTakeoff(IMenuAircraft? ac) =>
        IsControllable(ac) && ac.IsOnGround && (ac.CurrentPhase == "Taxiing") && ac.SituationFlags.HasFlag(SituationFlags.HasTakeoffClearance);

    /// <summary>
    /// Follow another ground aircraft while already following one: the sim replaces the follow. Give way while following
    /// is admitted by this too; its taxi-route requirement is <see cref="ShowsGiveWay"/>'s.
    /// </summary>
    public static bool WidensFollowWhileFollowing(IMenuAircraft? ac, MenuContext context) =>
        IsControllable(ac) && !RelativeTraffic.OffersGroundRelative(ac, context) && ac.CurrentPhase.StartsWith("Following", StringComparison.Ordinal);

    /// <summary>The airspace-boundary holds (LevelBelow…, HoldOutside…) and an AR anchor orbit (AR anchor …).</summary>
    private static bool IsBoundaryOrAnchorHold(string phase) =>
        phase.StartsWith("LevelBelow", StringComparison.Ordinal)
        || phase.StartsWith("HoldOutside", StringComparison.Ordinal)
        || phase.StartsWith("AR anchor ", StringComparison.Ordinal);

    // --- Track state ---

    /// <summary>
    /// True while a position owns the aircraft's track, whether it is named by its callsign or ID
    /// (<see cref="IMenuAircraft.Owner"/>) or by a sector code it resolves to (<see cref="IMenuAircraft.OwnerSectorCode"/>);
    /// either one alone is enough. It offers the handoff, point out and drop items either way, as it does for a track
    /// mirrored from a live feed.
    /// </summary>
    public static bool IsOwned(IMenuAircraft? ac) => (!string.IsNullOrEmpty(ac?.Owner)) || (!string.IsNullOrEmpty(ac?.OwnerSectorCode));

    /// <summary>
    /// True while a track handoff is in progress, whether it names its peer (<see cref="IMenuAircraft.HandoffPeer"/>) or
    /// only the peer's sector (<see cref="IMenuAircraft.HandoffPeerSectorCode"/>), as a handoff to an unstaffed sector does.
    /// </summary>
    public static bool HasHandoffInProgress(IMenuAircraft? ac) =>
        (!string.IsNullOrEmpty(ac?.HandoffPeer)) || (!string.IsNullOrEmpty(ac?.HandoffPeerSectorCode));

    /// <summary>
    /// True while a pointout awaits an answer (<see cref="IMenuAircraft.PointoutStatus"/>,
    /// <see cref="StarsPointoutStatus.Pending"/>); an accepted or rejected pointout counts as none.
    /// </summary>
    public static bool HasPendingPointout(IMenuAircraft? ac) =>
        string.Equals(ac?.PointoutStatus, nameof(StarsPointoutStatus.Pending), StringComparison.Ordinal);

    /// <summary>
    /// True when no track state applies — no owner, no handoff in progress and no pending pointout — which offers the
    /// single Initiate Track item. A missing aircraft carries no state and counts as untracked, so the submenu always
    /// holds at least one item.
    /// </summary>
    public static bool IsUntracked(IMenuAircraft? ac) => !IsOwned(ac) && !HasHandoffInProgress(ac) && !HasPendingPointout(ac);
}
