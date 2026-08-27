using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

/// <summary>
/// Non-blocking 7110.65 occupied-runway advisories for the RPO (issue #346).
///
/// At facilities without a full safety logic system, 3-9-4.c.1 / 3-10-5.e.1 prohibit (a) clearing
/// an aircraft for a full-stop, touch-and-go, stop-and-go, low approach, or option on a runway
/// with an aircraft holding in position or taxiing to LUAW — until that aircraft exits the runway
/// or starts takeoff roll — and (b) authorizing LUAW while an aircraft holds one of those
/// clearances for the same runway. YAAT enforces neither; these advisories warn the controller
/// instead, via <see cref="AircraftState.PendingWarnings"/> (drained each tick into amber terminal
/// entries). The command itself always succeeds.
///
/// An occupant that already holds its takeoff clearance is excluded from the forward direction —
/// clearing an arrival behind a departure that is about to roll is ordinary anticipated separation
/// (3-9-5 / 3-10-6), the same reasoning that keeps CTO out of the reverse direction.
///
/// "Full safety logic" is a property of the airport in the vNAS ARTCC config
/// (<see cref="ArtccConfigResolver.AirportHasFullSafetyLogic"/>): an ASDE-X configuration with
/// runway configurations. Airports with a display-only ASDE-X config (or none) still warn. The
/// proxy models a system running in full core alert mode; the reg's "inoperative or in the
/// limited configuration" states, and the low-IMC re-imposition (3-9-4.c.2 / 3-10-5.e.2, ceiling
/// below 800 ft or visibility below 2 SM), are not expressible from a static config and are not
/// modeled.
/// </summary>
public static class RunwaySafetyAdvisor
{
    private static readonly ILogger Log = SimLog.CreateLogger("RunwaySafetyAdvisor");

    /// <summary>
    /// Warns the controller when a landing-family clearance (CLAND/COPT/TG/SG/LA/CLANDF/LAHSO) is
    /// issued for a runway another aircraft is holding in position on or taxiing to line up on.
    /// The occupant match is by runway identity (<see cref="RunwayIdentifier.Overlaps"/>), so an
    /// aircraft lined up on the opposite end of the same pavement still triggers the advisory —
    /// except <see cref="HoldingInPositionPhase"/>, which YAAT also uses as a generic ground-idle
    /// hold (post-WARPG anywhere, far side of a crossing, CLRWY) and therefore only counts when
    /// the aircraft is physically on the cleared runway's pavement.
    /// </summary>
    public static void WarnIfRunwayOccupied(AircraftState aircraft, RunwayInfo runway, DispatchContext ctx)
    {
        if (ShouldSkip(ctx, runway.AirportId))
        {
            return;
        }

        var occupants = ctx.ListAircraft!()
            .Where(other =>
                (!ReferenceEquals(other, aircraft))
                && (
                    (
                        AwaitsTakeoffClearanceOnRunway(other)
                        && (OccupiedRunwayOf(other) is { } occupied)
                        && SameAirport(occupied.AirportId, runway.AirportId)
                        && occupied.Id.Overlaps(runway.Id)
                    ) || (other.Phases?.CurrentPhase is HoldingInPositionPhase && RunwayOccupancy.IsOnPavement(other, runway))
                )
            )
            .Select(other => other.Callsign)
            .ToList();

        EmitOccupiedWarning(aircraft, RunwayIdentifier.ToDisplayDesignator(runway.Designator), occupants);
        WarnIfLiveTrafficOnRunway(aircraft, runway, ctx);
    }

    /// <summary>
    /// Designator-only variant of <see cref="WarnIfRunwayOccupied(AircraftState, RunwayInfo, DispatchContext)"/>
    /// for grant paths that resolve a runway name but no <see cref="RunwayInfo"/> (a clearance
    /// pre-issued against a queued pattern entry, or armed on a following aircraft). The airport is
    /// taken from the cleared aircraft's flight-plan destination; when that is blank the airport
    /// filter is skipped — the occupant's runway identity alone decides. A
    /// <see cref="HoldingInPositionPhase"/> occupant counts only when its own assigned runway both
    /// names the cleared runway and geometrically contains its position.
    /// </summary>
    public static void WarnIfRunwayOccupied(AircraftState aircraft, string runwayDesignator, DispatchContext ctx)
    {
        if (ShouldSkip(ctx, aircraft.FlightPlan.Destination))
        {
            return;
        }

        var occupants = ctx.ListAircraft!()
            .Where(other =>
                (!ReferenceEquals(other, aircraft))
                && (OccupiedRunwayOf(other) is { } occupied)
                && SameAirportOrUnknown(occupied.AirportId, aircraft.FlightPlan.Destination)
                && occupied.Id.Contains(runwayDesignator)
                && (
                    AwaitsTakeoffClearanceOnRunway(other)
                    || (other.Phases?.CurrentPhase is HoldingInPositionPhase && RunwayOccupancy.IsOnPavement(other, occupied))
                )
            )
            .Select(other => other.Callsign)
            .ToList();

        EmitOccupiedWarning(aircraft, RunwayIdentifier.ToDisplayDesignator(runwayDesignator), occupants);
        if (ResolveNamedRunway(aircraft.FlightPlan.Destination, runwayDesignator) is { } namedRunway)
        {
            WarnIfLiveTrafficOnRunway(aircraft, namedRunway, ctx);
        }
    }

    /// <summary>
    /// Warns the controller when LUAW is authorized for a runway another aircraft already holds a
    /// landing-family clearance for — active (<see cref="PhaseList.LandingClearance"/>) or
    /// pre-issued against a queued pattern entry (<see cref="AircraftPattern.PendingLandingClearance"/>).
    /// A clearance is only counted while it is still pending use: an arrival that has landed and
    /// is exiting or taxiing in keeps its stale <see cref="PhaseList.LandingClearance"/>, and must
    /// not re-trigger this on every later LUAW.
    /// </summary>
    public static void WarnIfArrivalCleared(AircraftState aircraft, RunwayInfo runway, DispatchContext ctx)
    {
        if (ShouldSkip(ctx, runway.AirportId))
        {
            return;
        }

        var cleared = ctx.ListAircraft!()
            .Where(other => (!ReferenceEquals(other, aircraft)) && HoldsLandingClearanceFor(other, runway))
            .Select(other => other.Callsign)
            .ToList();

        if (cleared.Count == 0)
        {
            return;
        }

        var display = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
        var warning =
            $"{aircraft.Callsign}: {string.Join(", ", cleared)} holds a landing clearance for runway {display} — do not authorize line up and wait while that clearance stands; cancel it or hold short until the arrival is clear (7110.65 3-9-4.c)";
        aircraft.PendingWarnings.Add(warning);
        Log.LogDebug("[RunwaySafety] {Warning}", warning);
    }

    /// <summary>
    /// 7110.65 §3-9-4.d: when LUAW is authorized with real traffic on the final for that runway, the
    /// controller must exchange traffic — advise which live aircraft are inside <see cref="OnFinalAdvisoryNm"/>.
    /// Simulated arrivals are covered by their clearance state (<see cref="WarnIfArrivalCleared"/>); this
    /// is for shadows, which carry no clearance the sim can see.
    /// </summary>
    public static void WarnIfTrafficOnFinal(AircraftState aircraft, RunwayInfo runway, DispatchContext ctx)
    {
        if (ShouldSkip(ctx, runway.AirportId))
        {
            return;
        }

        // Geometry sees only the final itself; the rule's "requesting a full-stop / option" downwind traffic has no
        // observable intent for a shadow. Between parallel finals an arrival is reported for the closest runway only.
        var airportRunways = RunwayOccupancy.AirportRunways(runway.AirportId);
        var traffic = ctx.ListAircraft!()
            .Where(other =>
                (!ReferenceEquals(other, aircraft))
                && other.IsShadow
                && (RunwayOccupancy.ClosestFinal(other, airportRunways, ctx.GroundLayout, OnFinalAdvisoryNm)?.Id.Overlaps(runway.Id) ?? false)
            )
            .Select(other => (other.Callsign, DistNm: RunwayOccupancy.DistanceToLandingThresholdNm(other, runway, ctx.GroundLayout)))
            .OrderBy(t => t.DistNm)
            .ToList();
        if (traffic.Count == 0)
        {
            return;
        }

        var display = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
        var closest = traffic[0];
        var others = traffic.Count > 1 ? $"; also {string.Join(", ", traffic.Skip(1).Select(t => $"{t.Callsign} {t.DistNm:F1} nm"))}" : "";
        var warning =
            $"{aircraft.Callsign}: live traffic on final runway {display} — issue traffic before line up and wait: \"traffic, {closest.Callsign}, {closest.DistNm:F1} mile final\" (7110.65 3-9-4.d){others}";
        aircraft.PendingWarnings.Add(warning);
        Log.LogDebug("[RunwaySafety] {Warning}", warning);
    }

    /// <summary>
    /// A live-traffic shadow on the runway surface (lined up, holding, or a landed rollout) when a landing-family clearance
    /// is issued for that runway. 3-10-3.a.1 / 3-9-6.b: the runway is not usable until that aircraft is clear; a shadow
    /// carries no clearance state, so only its geometry can say. Live traffic still airborne on the final is sequencing
    /// (3-10-6.a) and belongs to <see cref="WarnIfTrafficOnFinal"/>, not here.
    /// </summary>
    private static void WarnIfLiveTrafficOnRunway(AircraftState aircraft, RunwayInfo runway, DispatchContext ctx)
    {
        var live = ctx.ListAircraft!()
            .Where(other => (!ReferenceEquals(other, aircraft)) && ShadowOccupies(other, runway, ctx))
            .Select(other => other.Callsign)
            .ToList();
        if (live.Count == 0)
        {
            return;
        }

        var display = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
        var warning =
            $"{aircraft.Callsign}: live traffic on runway {display} ({string.Join(", ", live)}) — the runway is not clear; withhold the landing/option clearance until it is (7110.65 3-10-3.a.1, 3-10-5.e)";
        aircraft.PendingWarnings.Add(warning);
        Log.LogDebug("[RunwaySafety] {Warning}", warning);
    }

    /// <summary>Final-approach ring for <see cref="WarnIfTrafficOnFinal"/> — roughly the 3-9-4.d "traffic on final" window.</summary>
    public const double OnFinalAdvisoryNm = 6.0;

    /// <summary>
    /// A live-traffic shadow on the runway (a landed rollout reads OnSurface via the observer's latch, never Departing) or
    /// still in the air over it below threshold-crossing height — a flare, or a rotorcraft descending onto the runway
    /// (P/CG CLEAR OF THE RUNWAY: an aircraft that has not touched down is not clear).
    /// </summary>
    private static bool ShadowOccupies(AircraftState other, RunwayInfo runway, DispatchContext ctx) =>
        other.IsShadow && RunwayOccupancy.Classify(other, runway, ctx.GroundLayout) is { Kind: RunwayUseKind.OnSurface or RunwayUseKind.Landing };

    private static RunwayInfo? ResolveNamedRunway(string airport, string designator) =>
        RunwayOccupancy.AirportRunways(airport).FirstOrDefault(r => r.Id.Contains(designator))?.ForApproach(designator);

    private static bool ShouldSkip(DispatchContext ctx, string airportId) =>
        (ctx.ListAircraft is null) || (ctx.ArtccConfig is { } config && config.AirportHasFullSafetyLogic(airportId));

    private static void EmitOccupiedWarning(AircraftState aircraft, string runwayDisplay, List<string> occupants)
    {
        if (occupants.Count == 0)
        {
            return;
        }

        var warning =
            $"{aircraft.Callsign}: traffic holding in position on runway {runwayDisplay} ({string.Join(", ", occupants)}) — withhold the landing/option clearance until that traffic exits the runway or starts takeoff roll; issue \"runway {runwayDisplay}, continue, traffic holding in position\" instead (7110.65 3-10-5.e, 3-9-4.c)";
        aircraft.PendingWarnings.Add(warning);
        Log.LogDebug("[RunwaySafety] {Warning}", warning);
    }

    /// <summary>
    /// True when the aircraft is in the 3-9-4 restricted state: holding in position or taxiing to
    /// LUAW <b>without</b> a takeoff clearance in hand. A lined-up aircraft whose clearance is
    /// already satisfied, or one taxiing into position for a rolling takeoff (next phase is the
    /// takeoff itself), is about to roll — anticipated separation, not a conflict.
    /// </summary>
    private static bool AwaitsTakeoffClearanceOnRunway(AircraftState occupant)
    {
        var phases = occupant.Phases;
        return phases?.CurrentPhase switch
        {
            LinedUpAndWaitingPhase luaw => !luaw.HasTakeoffClearance,
            LineUpPhase => NextPhaseOf(phases) is LinedUpAndWaitingPhase next && !next.HasTakeoffClearance,
            _ => false,
        };
    }

    private static Phase? NextPhaseOf(PhaseList phases) =>
        (phases.CurrentIndex + 1) < phases.Phases.Count ? phases.Phases[phases.CurrentIndex + 1] : null;

    /// <summary>
    /// The runway a ground aircraft in a line-up-family phase is claiming: the departure runway for
    /// cross-runway closed traffic (where <see cref="PhaseList.AssignedRunway"/> holds the pattern
    /// runway instead), otherwise the assigned runway.
    /// </summary>
    private static RunwayInfo? OccupiedRunwayOf(AircraftState occupant) => occupant.Phases?.DepartureRunway ?? occupant.Phases?.AssignedRunway;

    private static bool HoldsLandingClearanceFor(AircraftState other, RunwayInfo runway)
    {
        if (other.Phases is { LandingClearance: { } active } phases && IsLandingFamily(active) && IsClearanceStillPendingUse(other))
        {
            if (phases.AssignedRunway is { } assigned)
            {
                if (SameAirport(assigned.AirportId, runway.AirportId) && assigned.Id.Overlaps(runway.Id))
                {
                    return true;
                }
            }
            else if ((phases.ClearedRunwayId is { } clearedId) && runway.Id.Contains(clearedId))
            {
                // Armed on a follower: no RunwayInfo yet, only the cleared designator.
                return true;
            }
        }

        return other.Pattern.PendingLandingClearance is { } pending && IsLandingFamily(pending.Clearance) && runway.Id.Contains(pending.RunwayId);
    }

    /// <summary>
    /// The clearance still governs an approach to the runway: the aircraft is airborne, or it is
    /// on the runway mid-landing (rollout, stop-and-go hold, touch-and-go roll). Once it is
    /// exiting or taxiing in, the stale <see cref="PhaseList.LandingClearance"/> no longer
    /// restricts LUAW.
    /// </summary>
    private static bool IsClearanceStillPendingUse(AircraftState other) =>
        (!other.IsOnGround) || other.Phases?.CurrentPhase is LandingPhase or HelicopterLandingPhase or StopAndGoPhase or TouchAndGoPhase;

    private static bool IsLandingFamily(ClearanceType clearance) =>
        clearance
            is ClearanceType.ClearedToLand
                or ClearanceType.ClearedForOption
                or ClearanceType.ClearedTouchAndGo
                or ClearanceType.ClearedStopAndGo
                or ClearanceType.ClearedLowApproach;

    /// <summary>FAA-vs-ICAO tolerant airport comparison: "OAK" and "KOAK" name the same field.</summary>
    private static bool SameAirport(string a, string b) => string.Equals(StripIcaoPrefix(a), StripIcaoPrefix(b), StringComparison.OrdinalIgnoreCase);

    private static bool SameAirportOrUnknown(string occupantAirport, string commandedAirport) =>
        string.IsNullOrWhiteSpace(commandedAirport) || SameAirport(occupantAirport, commandedAirport);

    private static string StripIcaoPrefix(string airportId) =>
        (airportId.Length == 4) && (char.ToUpperInvariant(airportId[0]) == 'K') ? airportId[1..] : airportId;
}
