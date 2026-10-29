using Microsoft.Extensions.Logging;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Training;

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
/// "Full safety logic" is ASDE-X Safety Logic raising occupied-runway alerts at the airport now
/// (<see cref="AsdexSafetyLogicSeed.OccupiedRunwayAlertingIsLive"/>): an ASDE-X configuration with
/// runway configurations in the vNAS ARTCC config, and the room's live Safety Logic configuration
/// for that facility on one of them. Airports with a display-only ASDE-X config (or none), and a
/// facility a CRC user has put on LIMITED, still warn. The low-IMC re-imposition (3-9-4.c.2 /
/// 3-10-5.e.2, ceiling below 800 ft or visibility below 2 SM) is not modeled.
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
    /// the aircraft is physically on the cleared runway's pavement. An aircraft frozen on the
    /// pavement by a hold directive without either phase draws the separate
    /// <see cref="WarnIfStoppedTrafficOnRunway"/> warning. Traffic past the runway's hold line
    /// (<see cref="WarnIfTrafficPastHoldLine"/>) warns at every airport, as on the takeoff side: 3-10-3.a has no equipage carve-out.
    /// </summary>
    public static void WarnIfRunwayOccupied(AircraftState aircraft, RunwayInfo runway, DispatchContext ctx)
    {
        if (ctx.ListAircraft is null)
        {
            return;
        }

        WarnIfTrafficPastHoldLine(aircraft, runway, ctx, forTakeoff: false);
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
        WarnIfStoppedTrafficOnRunway(aircraft, runway, ctx, forTakeoff: false);
        WarnIfLiveTrafficOnRunway(aircraft, runway, ctx);
    }

    /// <summary>
    /// Designator-only variant of <see cref="WarnIfRunwayOccupied(AircraftState, RunwayInfo, DispatchContext)"/>
    /// for grant paths that resolve a runway name but no <see cref="RunwayInfo"/> (a clearance
    /// pre-issued against a queued pattern entry, or armed on a following aircraft). The airport is
    /// taken from the cleared aircraft's flight-plan destination; when that is blank the airport
    /// filter is skipped — the occupant's runway identity alone decides. A
    /// <see cref="HoldingInPositionPhase"/> occupant counts when, with the cleared runway resolved, one of its runways
    /// (<see cref="PhaseList.RunsOn"/>) names the cleared runway and the cleared runway's pavement contains its position;
    /// with the cleared runway unresolved, when either of its own runways matches the designator and that runway's
    /// pavement contains its position. The stopped-traffic and live-traffic checks need real geometry, so they run only
    /// when the destination resolves the designator to a runway. The past-the-hold-line check runs at every airport, as in
    /// the other overload.
    /// </summary>
    public static void WarnIfRunwayOccupied(AircraftState aircraft, string runwayDesignator, DispatchContext ctx)
    {
        if (ctx.ListAircraft is null)
        {
            return;
        }

        RunwayInfo? namedRunway = ResolveNamedRunway(aircraft.FlightPlan.Destination, runwayDesignator);
        if (namedRunway is not null)
        {
            WarnIfTrafficPastHoldLine(aircraft, namedRunway, ctx, forTakeoff: false);
        }

        if (ShouldSkip(ctx, aircraft.FlightPlan.Destination))
        {
            return;
        }

        string airport = aircraft.FlightPlan.Destination;
        var occupants = ctx.ListAircraft!()
            .Where(other =>
                (!ReferenceEquals(other, aircraft))
                && (
                    (namedRunway is not null)
                        ? OccupiesResolvedRunway(other, namedRunway)
                        : OccupiesUnresolvedRunway(other, airport, runwayDesignator)
                )
            )
            .Select(other => other.Callsign)
            .ToList();

        EmitOccupiedWarning(aircraft, RunwayIdentifier.ToDisplayDesignator(runwayDesignator), occupants);
        if (namedRunway is not null)
        {
            WarnIfStoppedTrafficOnRunway(aircraft, namedRunway, ctx, forTakeoff: false);
            WarnIfLiveTrafficOnRunway(aircraft, namedRunway, ctx);
        }
    }

    /// <summary>
    /// An aircraft immobilized on the runway pavement by a hold directive (HOLDPOSITION or GIVEWAY
    /// — issue #411): typically a runway crossing frozen mid-pavement, which keeps its
    /// <see cref="CrossingRunwayPhase"/>/<see cref="TaxiingPhase"/> rather than gaining a
    /// <see cref="HoldingInPositionPhase"/>, so the phase-based occupant matches never see it. A
    /// <i>stopped</i> crossing is an occupied runway (3-10-3.a for arrivals, 3-9-6.a for
    /// departures), not anticipated separation; a <i>moving</i> crossing must stay silent (3-1-3
    /// coordinated operations, 3-10-5.b "traffic crossing downfield") and does — its
    /// <see cref="AircraftGroundState.IsImmobile"/> is false. Occupants the phase-based branches
    /// already count (line-up family awaiting takeoff clearance, <see cref="HoldingInPositionPhase"/>)
    /// are excluded so a held LUAW aircraft draws one warning, not two. A <c>GIVEWAY</c> still pending on an arrival's landing
    /// rollout or exit (<see cref="AircraftState.HasGiveWayPendingOnLandingRunway"/>) does not stop it, so that arrival is landed
    /// traffic (<see cref="WarnIfLandedTrafficBlocksTakeoff"/>), not stopped traffic. For a landing clearance an aircraft whose
    /// tail stays inside the runway's holding position marking (<see cref="IsTailInsideOccupant"/>) counts too, wherever its centre is.
    /// </summary>
    private static void WarnIfStoppedTrafficOnRunway(AircraftState aircraft, RunwayInfo runway, DispatchContext ctx, bool forTakeoff)
    {
        var stopped = ctx.ListAircraft!()
            .Where(other =>
                (!ReferenceEquals(other, aircraft))
                && (IsHeldOnPavement(other, runway) || ((!forTakeoff) && IsTailInsideOccupant(other, runway, ctx.GroundLayout)))
            )
            .Select(other => other.Callsign)
            .ToList();
        if (stopped.Count == 0)
        {
            return;
        }

        string display = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
        string warning = forTakeoff
            ? $"traffic stopped on runway {display} ({string.Join(", ", stopped)}) — this departure cannot begin takeoff roll until that traffic is clear of the runway (7110.65 3-9-6); hold this aircraft short until the runway is clear"
            : $"traffic stopped on runway {display} ({string.Join(", ", stopped)}) — the runway is occupied; withhold the landing/option clearance until that traffic is clear (7110.65 3-10-3.a)";
        aircraft.PendingWarnings.Add(warning);
        Log.LogDebug("[RunwaySafety] {Warning}", warning);
    }

    /// <summary>
    /// Immobilized on <paramref name="runway"/>'s pavement by a hold directive, and not one the phase-based occupant matches already
    /// count (<see cref="WarnIfStoppedTrafficOnRunway"/>).
    /// </summary>
    private static bool IsHeldOnPavement(AircraftState other, RunwayInfo runway) =>
        other.Ground.IsImmobile
        && (!other.HasGiveWayPendingOnLandingRunway)
        && RunwayOccupancy.IsOnPavement(other, runway)
        && (!AwaitsTakeoffClearanceOnRunway(other))
        && other.Phases?.CurrentPhase is not HoldingInPositionPhase;

    /// <summary>
    /// An aircraft whose tail stays inside <paramref name="runway"/>'s holding position marking
    /// (<see cref="RunwayOccupancy.IsTailInsideRunwayState"/>, judged on this runway by <see cref="RunwayOccupancy.Classify"/>),
    /// moving or stopped: it has not cleared the runway and will not (AIM 4-3-21.b; 7110.65 3-10-3.a.1), so a landing clearance
    /// draws the stopped-traffic warning. A <see cref="HoldingInPositionPhase"/> on the pavement is left to the phase-based occupant
    /// match, so it draws one warning, not two.
    /// </summary>
    private static bool IsTailInsideOccupant(AircraftState other, RunwayInfo runway, AirportGroundLayout? layout) =>
        RunwayOccupancy.IsTailInsideRunwayState(other)
        && !((other.Phases?.CurrentPhase is HoldingInPositionPhase) && RunwayOccupancy.IsOnPavement(other, runway))
        && (RunwayOccupancy.Classify(other, runway, layout)?.Kind == RunwayUseKind.OnSurface);

    /// <summary>
    /// Simulated traffic past the runway's holding position marking off its pavement
    /// (<see cref="RunwayUseKind.Fouling"/>): a holder whose nose is over the marking, a crossing stopped or held before the
    /// pavement, or a leaving aircraft stopped or restricted with its tail still inside the marking. AIM 2-3-5.a.1 has no part of an aircraft
    /// beyond the marking, so the runway is not clear for a departure's roll (3-9-6) or an arrival's threshold crossing
    /// (3-10-3.a). Every other branch here matches aircraft on the pavement, which a fouler is not, so a fouler draws this
    /// warning only — a held one included.
    /// </summary>
    private static void WarnIfTrafficPastHoldLine(AircraftState aircraft, RunwayInfo runway, DispatchContext ctx, bool forTakeoff)
    {
        var fouling = ctx.ListAircraft!()
            .Where(other =>
                (!ReferenceEquals(other, aircraft)) && RunwayOccupancy.FoulsRunway(RunwayOccupancy.Classify(other, runway, ctx.GroundLayout)?.Kind)
            )
            .Select(other => other.Callsign)
            .ToList();
        if (fouling.Count == 0)
        {
            return;
        }

        string display = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
        string callsigns = string.Join(", ", fouling);
        string warning = forTakeoff
            ? $"traffic past the hold line for runway {display} ({callsigns}) — the runway is not clear; this departure cannot begin takeoff roll until that traffic is clear of the runway (7110.65 3-9-6; AIM 2-3-5.a.1); hold this aircraft short until the runway is clear"
            : $"traffic past the hold line for runway {display} ({callsigns}) — the runway is not clear; withhold the landing/option clearance until that traffic is clear of the runway (7110.65 3-10-3.a; AIM 2-3-5.a.1)";
        aircraft.PendingWarnings.Add(warning);
        Log.LogDebug("[RunwaySafety] {Warning}", warning);
    }

    /// <summary>
    /// Warns the controller when LUAW is authorized for a runway another aircraft already holds a
    /// landing-family clearance for — active (<see cref="PhaseList.LandingClearance"/>) or
    /// pre-issued against a queued pattern entry (<see cref="AircraftPattern.PendingLandingClearance"/>).
    /// A clearance is only counted while the arrival is still airborne: 3-9-4.a contemplates LUAW
    /// behind "another aircraft which has landed on or is taking off the runway", so an arrival in
    /// its rollout no longer restricts it, and a landed aircraft keeps its stale
    /// <see cref="PhaseList.LandingClearance"/> while exiting and taxiing in, which must not
    /// re-trigger this on every later LUAW.
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

        string display = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
        string warning =
            $"{string.Join(", ", cleared)} holds a landing clearance for runway {display} — do not authorize line up and wait while that clearance stands; cancel it or hold short until the arrival is clear (7110.65 3-9-4.c)";
        aircraft.PendingWarnings.Add(warning);
        Log.LogDebug("[RunwaySafety] {Warning}", warning);
    }

    /// <summary>
    /// Warns the controller when a takeoff clearance is issued for a runway another aircraft is
    /// holding in position on without its own takeoff clearance (issue #409). 7110.65 3-9-6.a
    /// requires the succeeding departure not begin takeoff roll until the preceding aircraft has
    /// departed and crossed the runway end or turned to avert a conflict; with a stationary
    /// occupant in position and no clearance in hand there is no 3-9-5 "reasonable assurance" to
    /// anticipate. An occupant that already holds its own takeoff clearance is about to roll —
    /// that IS ordinary anticipated separation (3-9-5, confirmed by the wake-only scoping of the
    /// 3-9-6.e NOTE), so it stays silent, as are crossings in progress (3-1-3 coordinated local
    /// operations; see 3-10-5.b "traffic crossing downfield") — though a crossing <i>stopped</i>
    /// on the pavement by a hold directive warns via
    /// <see cref="WarnIfStoppedTrafficOnRunway"/>. Not gated on the safety-logic
    /// proxy: the full-core-alert relief is scoped to 3-9-4.c.2 / 3-10-5.e.2 — same-runway
    /// separation has no equipage carve-out. The occupant match mirrors
    /// <see cref="WarnIfRunwayOccupied(AircraftState, RunwayInfo, DispatchContext)"/>: runway
    /// identity for line-up-family phases (either end of the pavement), geometric containment for
    /// the generic <see cref="HoldingInPositionPhase"/>. Note this advisory is the ONLY defense
    /// for the intersection-departure geometry — an occupant far down the pavement is outside the
    /// ground conflict detector's proximity range, so nothing physically stops that departure.
    /// A preceding arrival still on the pavement — rolling out, on a touch-and-go or stop-and-go
    /// not yet lifted off, exiting along the centerline, or still airborne over the runway — is the
    /// 3-9-6.b case (<see cref="WarnIfLandedTrafficBlocksTakeoff"/>); it is what covers the
    /// departure once touchdown has ended the arrival's 3-9-4.c hold on LUAW. 7110.65 3-8-2 makes a
    /// touch-and-go a departure from touchdown and a stop-and-go one from its complete stop; YAAT
    /// classifies both at liftoff as a simplification, so once lifted off each is a preceding
    /// departure judged by 3-9-6.a there.
    /// </summary>
    public static void WarnIfRunwayOccupiedForTakeoff(AircraftState aircraft, RunwayInfo runway, DispatchContext ctx)
    {
        if (ctx.ListAircraft is null)
        {
            return;
        }

        var occupants = ctx.ListAircraft()
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

        if (occupants.Count > 0)
        {
            string display = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
            string warning =
                $"traffic holding in position on runway {display} ({string.Join(", ", occupants)}) — this departure cannot begin takeoff roll until that traffic has departed and crossed the runway end or turned to avert a conflict (7110.65 3-9-6.a); with it stopped in position and holding no takeoff clearance there is no reasonable assurance separation will exist when the roll starts (3-9-5) — hold this aircraft short until the runway is clear";
            aircraft.PendingWarnings.Add(warning);
            Log.LogDebug("[RunwaySafety] {Warning}", warning);
        }

        WarnIfStoppedTrafficOnRunway(aircraft, runway, ctx, forTakeoff: true);
        WarnIfTrafficPastHoldLine(aircraft, runway, ctx, forTakeoff: true);
        WarnIfLandedTrafficBlocksTakeoff(aircraft, runway, ctx);
        WarnIfLiveTrafficBlocksTakeoff(aircraft, runway, ctx);
    }

    /// <summary>
    /// A simulated arrival that is not yet clear of the runway when a takeoff clearance is issued —
    /// 7110.65 3-9-6.b: the departure may not begin its roll until the preceding landing aircraft is
    /// clear. Occupancy by <see cref="RunwayOccupancy.Classify"/>: a rollout, a touch-and-go rolling
    /// before liftoff, a stopped or rolling stop-and-go, or an exit still on the centerline
    /// (<see cref="RunwayUseKind.OnSurface"/> on the ground), or an arrival still airborne over the
    /// pavement (<see cref="RunwayUseKind.Landing"/>). 7110.65 3-8-2 makes a touch-and-go a departure
    /// from touchdown and a stop-and-go one from its complete stop; YAAT classifies both at liftoff as a
    /// simplification, so on the ground they warn here as traffic on the runway. Once lifted off, and a
    /// low approach past the landing threshold, they classify <see cref="RunwayUseKind.Departing"/>: a
    /// preceding departure that warns only while <see cref="PrecedingDepartureBlock"/> judges it inside
    /// 3-9-6.a. The same on-surface bucket also catches a rejected
    /// takeoff, which never landed — its condition is 3-9-6.a ("departed and crossed the runway end") —
    /// so the on-surface message cites the parent 3-9-6. An arrival that has begun its turnoff but is
    /// still inside the hold-short bars classifies as a crossing and stays silent: it is leaving, and
    /// that is 3-9-5 anticipated compliance with 3-9-6.b. Occupants the sibling branches already name are
    /// excluded so each draws one advisory: line-up-family occupants (awaiting or holding a takeoff
    /// clearance — the latter is 3-9-5 anticipated separation and stays silent), traffic frozen by a hold
    /// directive (<see cref="WarnIfStoppedTrafficOnRunway"/>) other than a give-way still pending on the landing rollout or exit,
    /// which never stops the arrival, and shadows
    /// (<see cref="WarnIfLiveTrafficBlocksTakeoff"/>). A departure already rolling is 3-9-5 anticipated
    /// compliance with 3-9-6.a and stays silent.
    /// </summary>
    private static void WarnIfLandedTrafficBlocksTakeoff(AircraftState aircraft, RunwayInfo runway, DispatchContext ctx)
    {
        var occupants = ctx.ListAircraft!()
            .Where(other =>
                (!ReferenceEquals(other, aircraft))
                && (!other.IsShadow)
                && ((!other.Ground.IsImmobile) || other.HasGiveWayPendingOnLandingRunway)
                && (!AwaitsTakeoffClearanceOnRunway(other))
                && other.Phases?.CurrentPhase is not (LineUpPhase or LinedUpAndWaitingPhase or HoldingInPositionPhase)
            )
            .Select(other => (Aircraft: other, RunwayOccupancy.Classify(other, runway, ctx.GroundLayout)?.Kind))
            .ToList();
        var onSurface = occupants.Where(a => a.Aircraft.IsOnGround && (a.Kind == RunwayUseKind.OnSurface)).Select(a => a.Aircraft.Callsign).ToList();
        // The phase classifier calls the whole approach "Landing"; only an arrival already over the pavement blocks the roll.
        var landing = occupants
            .Where(a => (!a.Aircraft.IsOnGround) && (a.Kind == RunwayUseKind.Landing) && RunwayOccupancy.IsOverOrOnPavement(a.Aircraft, runway))
            .Select(a => a.Aircraft.Callsign)
            .ToList();
        string display = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
        if (onSurface.Count > 0)
        {
            string warning =
                $"traffic on runway {display} ({string.Join(", ", onSurface)}) — this departure cannot begin takeoff roll until that traffic is clear of the runway (7110.65 3-9-6); hold this aircraft short until the runway is clear";
            aircraft.PendingWarnings.Add(warning);
            Log.LogDebug("[RunwaySafety] {Warning}", warning);
        }

        if (landing.Count > 0)
        {
            string warning =
                $"arrival landing on runway {display} ({string.Join(", ", landing)}) — this departure cannot begin takeoff roll until that traffic is clear of the runway (7110.65 3-9-6.b)";
            aircraft.PendingWarnings.Add(warning);
            Log.LogDebug("[RunwaySafety] {Warning}", warning);
        }

        foreach ((AircraftState leader, RunwayUseKind? kind) in occupants)
        {
            if ((kind == RunwayUseKind.Departing) && (leader.Phases?.CurrentPhase is TouchAndGoPhase or StopAndGoPhase or LowApproachPhase))
            {
                WarnIfDepartingAhead(aircraft, leader, runway, display);
            }
        }
    }

    /// <summary>
    /// A touch-and-go or stop-and-go that has lifted off, or a low approach past the landing threshold, is a departure, so the
    /// takeoff behind it is 3-9-6.a. 7110.65 3-8-2 makes a touch-and-go a departure from touchdown and a stop-and-go one from
    /// its complete stop; YAAT classifies both at liftoff as a simplification. Warns when <paramref name="leader"/>, ahead of
    /// <paramref name="departure"/>'s position, is not yet airborne with the 3-9-6.a distance or past the runway end, as
    /// <see cref="PrecedingDepartureBlock"/> judges a roll about to start. A leader flying the opposite way, toward the
    /// departure, blocks whatever its distance (<see cref="PrecedingDepartureBlock.Blocks"/>): the warning says it is inbound
    /// over the runway, since every 3-9-6.a distance is credit for traffic running away down it.
    /// </summary>
    private static void WarnIfDepartingAhead(AircraftState departure, AircraftState leader, RunwayInfo runway, string display)
    {
        double aheadFt = GeoMath.AlongTrackDistanceNm(leader.Position, departure.Position, runway.TrueHeading) * GeoMath.FeetPerNm;
        if ((aheadFt <= 0) || !PrecedingDepartureBlock.Blocks(departure, leader, runway, aheadFt, out _))
        {
            return;
        }

        string ahead = OccupiedRunwayGoAround.RoundedFeet(aheadFt);
        string warning = PrecedingDepartureBlock.IsOppositeDirection(leader, runway)
            ? $"{leader.Callsign} opposite direction on runway {display}, {ahead} ft ahead — this departure cannot begin takeoff roll while that traffic is inbound over the runway (7110.65 3-9-6.a)"
            : $"{leader.Callsign} departing ahead on runway {display}, {ahead} ft ahead — this departure cannot begin takeoff roll until that traffic is airborne {RequiredDepartureDistance(leader, departure)} ft ahead or past the runway end (7110.65 3-9-6.a)";
        departure.PendingWarnings.Add(warning);
        Log.LogDebug("[RunwaySafety] {Warning}", warning);
    }

    /// <summary>The 3-9-6.a distance, in feet as the warning prints it, for <paramref name="departure"/> behind <paramref name="leader"/>.</summary>
    private static string RequiredDepartureDistance(AircraftState leader, AircraftState departure) =>
        SameRunwaySeparation
            .RequiredDepartureBehindDepartureFt(SameRunwaySeparation.ResolveSrsCategory(leader), SameRunwaySeparation.ResolveSrsCategory(departure))
            .ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A live-traffic shadow occupying the runway surface (3-9-6.a) or landing on it (3-9-6.b)
    /// when a takeoff clearance is issued. A shadow already rolling on its own departure is the
    /// 3-9-5 anticipated case and stays silent.
    /// </summary>
    private static void WarnIfLiveTrafficBlocksTakeoff(AircraftState aircraft, RunwayInfo runway, DispatchContext ctx)
    {
        var shadows = ctx.ListAircraft!()
            .Where(other => (!ReferenceEquals(other, aircraft)) && other.IsShadow)
            .Select(other => (other.Callsign, RunwayOccupancy.Classify(other, runway, ctx.GroundLayout)?.Kind))
            .ToList();
        var onSurface = shadows.Where(s => s.Kind == RunwayUseKind.OnSurface).Select(s => s.Callsign).ToList();
        var landing = shadows.Where(s => s.Kind == RunwayUseKind.Landing).Select(s => s.Callsign).ToList();
        string display = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
        if (onSurface.Count > 0)
        {
            string warning =
                $"live traffic on runway {display} ({string.Join(", ", onSurface)}) — this departure cannot begin takeoff roll until that traffic has departed and crossed the runway end or turned to avert a conflict (7110.65 3-9-6.a)";
            aircraft.PendingWarnings.Add(warning);
            Log.LogDebug("[RunwaySafety] {Warning}", warning);
        }

        if (landing.Count > 0)
        {
            string warning =
                $"live arrival landing on runway {display} ({string.Join(", ", landing)}) — this departure cannot begin takeoff roll until that traffic is clear of the runway (7110.65 3-9-6.b)";
            aircraft.PendingWarnings.Add(warning);
            Log.LogDebug("[RunwaySafety] {Warning}", warning);
        }
    }

    /// <summary>
    /// 7110.65 §3-9-4.h: aircraft may not simultaneously line up and wait on the same runway between sunrise and
    /// sunset unless the local assist/local monitor position is staffed (and §3-9-4.g.4 allows only one at a time at
    /// an intersection at night). The sim has no clock-based daylight or staffing state, so a second LUAW on a pavement
    /// that already has an aircraft holding in position (either end, without a takeoff clearance) always draws the
    /// reminder. Not tied to the safety-logic gate — the rule is about the tower seeing two aircraft on one runway.
    /// </summary>
    public static void WarnIfAnotherHoldingInPosition(AircraftState aircraft, RunwayInfo runway, DispatchContext ctx)
    {
        if (ctx.ListAircraft is null)
        {
            return;
        }

        var holding = ctx.ListAircraft()
            .Where(other =>
                (!ReferenceEquals(other, aircraft))
                && AwaitsTakeoffClearanceOnRunway(other)
                && (OccupiedRunwayOf(other) is { } occupied)
                && SameAirport(occupied.AirportId, runway.AirportId)
                && occupied.Id.Overlaps(runway.Id)
            )
            .Select(other => other.Callsign)
            .ToList();
        if (holding.Count == 0)
        {
            return;
        }

        string display = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
        string warning =
            $"{string.Join(", ", holding)} already holding in position on runway {display} — do not authorize simultaneous line up and wait on the same runway between sunrise and sunset unless the local assist/local monitor position is staffed (7110.65 3-9-4.h)";
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
        IReadOnlyList<RunwayInfo> airportRunways = RunwayOccupancy.AirportRunways(runway.AirportId);
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

        string display = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
        (string Callsign, double DistNm) closest = traffic[0];
        string others = traffic.Count > 1 ? $"; also {string.Join(", ", traffic.Skip(1).Select(t => $"{t.Callsign} {t.DistNm:F1} nm"))}" : "";
        string warning =
            $"live traffic on final runway {display} — issue traffic before line up and wait: \"traffic, {closest.Callsign}, {closest.DistNm:F1} mile final\" (7110.65 3-9-4.d){others}";
        aircraft.PendingWarnings.Add(warning);
        Log.LogDebug("[RunwaySafety] {Warning}", warning);
    }

    /// <summary>
    /// A live-traffic shadow on the runway surface (lined up, holding, or a landed rollout) when a landing-family clearance
    /// is issued for that runway. 3-10-3.a / 3-10-5.e: the runway is not usable until that aircraft is clear; a shadow
    /// carries no clearance state, so only its geometry and the observer's landed latch
    /// (<see cref="AircraftLiveTraffic.LandedOnRunway"/>) can say. Live traffic still airborne on the final is sequencing
    /// (3-10-6.a) and belongs to <see cref="WarnIfTrafficOnFinal"/>, not here.
    /// </summary>
    private static void WarnIfLiveTrafficOnRunway(AircraftState aircraft, RunwayInfo runway, DispatchContext ctx)
    {
        var shadows = ctx.ListAircraft!()
            .Where(other => (!ReferenceEquals(other, aircraft)) && other.IsShadow)
            .Select(other =>
                (
                    other.Callsign,
                    RunwayOccupancy.Classify(other, runway, ctx.GroundLayout)?.Kind,
                    Landed: other.LiveTraffic is { LandedOnRunway: true }
                )
            )
            .ToList();
        // The runway-use observer latches a shadow that landed on this runway, which is the only way to tell a landed
        // rollout (3-10-3.a.1) from a shadow lined up or holding on the surface (3-10-5.e, and 3-10-3.a.2 once it rolls).
        var landed = shadows.Where(s => (s.Kind == RunwayUseKind.OnSurface) && s.Landed).Select(s => s.Callsign).ToList();
        var occupying = shadows.Where(s => (s.Kind == RunwayUseKind.OnSurface) && (!s.Landed)).Select(s => s.Callsign).ToList();
        var landing = shadows.Where(s => s.Kind == RunwayUseKind.Landing).Select(s => s.Callsign).ToList();
        var rolling = shadows.Where(s => s.Kind == RunwayUseKind.Departing).Select(s => s.Callsign).ToList();
        string display = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
        if (occupying.Count > 0)
        {
            string warning =
                $"live traffic on runway {display} ({string.Join(", ", occupying)}) — the runway is not clear; withhold the landing/option clearance until it is (7110.65 3-10-3.a, 3-10-5.e)";
            aircraft.PendingWarnings.Add(warning);
            Log.LogDebug("[RunwaySafety] {Warning}", warning);
        }

        if (landed.Count > 0)
        {
            string warning =
                $"live arrival rolling out on runway {display} ({string.Join(", ", landed)}) — the runway is not clear; withhold the landing/option clearance until it is (7110.65 3-10-3.a.1)";
            aircraft.PendingWarnings.Add(warning);
            Log.LogDebug("[RunwaySafety] {Warning}", warning);
        }

        if (landing.Count > 0)
        {
            string warning =
                $"live arrival landing on runway {display} ({string.Join(", ", landing)}) — the runway is not clear; withhold the landing/option clearance until it is (7110.65 3-10-3.a.1)";
            aircraft.PendingWarnings.Add(warning);
            Log.LogDebug("[RunwaySafety] {Warning}", warning);
        }

        // A departure that has started its roll lifts the holding-in-position prohibition (3-9-4.c.1, 3-10-5.e.1) but
        // 3-10-3.a.2 still governs: the arrival may not cross the threshold until it has crossed the runway end or is
        // airborne past the landmark.
        if (rolling.Count > 0)
        {
            string warning =
                $"live departure rolling on runway {display} ({string.Join(", ", rolling)}) — the arrival may not cross the threshold until it has crossed the runway end or is airborne past the same-runway landmark (7110.65 3-10-3.a.2)";
            aircraft.PendingWarnings.Add(warning);
            Log.LogDebug("[RunwaySafety] {Warning}", warning);
        }
    }

    /// <summary>Final-approach ring for <see cref="WarnIfTrafficOnFinal"/> — roughly the 3-9-4.d "traffic on final" window.</summary>
    public const double OnFinalAdvisoryNm = 6.0;

    private static RunwayInfo? ResolveNamedRunway(string airport, string designator) =>
        RunwayOccupancy.AirportRunways(airport).FirstOrDefault(r => r.Id.Contains(designator))?.ForApproach(designator);

    private static bool ShouldSkip(DispatchContext ctx, string airportId) =>
        (ctx.ListAircraft is null) || AsdexSafetyLogicSeed.OccupiedRunwayAlertingIsLive(ctx.ArtccConfig, ctx.AsdexSafetyLogicConfigs, airportId);

    private static void EmitOccupiedWarning(AircraftState aircraft, string runwayDisplay, List<string> occupants)
    {
        if (occupants.Count == 0)
        {
            return;
        }

        string warning =
            $"traffic holding in position on runway {runwayDisplay} ({string.Join(", ", occupants)}) — withhold the landing/option clearance until that traffic exits the runway or starts takeoff roll; issue \"runway {runwayDisplay}, continue, traffic holding in position\" instead (7110.65 3-10-5.e, 3-9-4.c)";
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
        PhaseList? phases = occupant.Phases;
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
    /// The runway the occupant's current phase runs on (<see cref="PhaseList.CurrentPhaseRunway"/>): for a line-up-family
    /// phase, the departure runway of cross-runway closed traffic, otherwise the assigned runway.
    /// </summary>
    private static RunwayInfo? OccupiedRunwayOf(AircraftState occupant) => occupant.Phases?.CurrentPhaseRunway;

    /// <summary>
    /// The designator path's occupant test once the designator resolves to <paramref name="runway"/>: a line-up-family aircraft
    /// awaiting its takeoff clearance there, or a <see cref="HoldingInPositionPhase"/> aircraft whose phase runs on it
    /// (<see cref="PhaseList.RunsOn"/>, departure or assigned runway) and that is on its pavement.
    /// </summary>
    private static bool OccupiesResolvedRunway(AircraftState other, RunwayInfo runway) =>
        (
            AwaitsTakeoffClearanceOnRunway(other)
            && (OccupiedRunwayOf(other) is { } occupied)
            && SameAirport(occupied.AirportId, runway.AirportId)
            && occupied.Id.Overlaps(runway.Id)
        )
        || (
            (other.Phases is { CurrentPhase: HoldingInPositionPhase } phases) && phases.RunsOn(runway) && RunwayOccupancy.IsOnPavement(other, runway)
        );

    /// <summary>
    /// The designator path's occupant test when no runway geometry resolves: by runway identity alone, with a
    /// <see cref="HoldingInPositionPhase"/> aircraft counting on either its departure or its assigned runway when it is on that
    /// runway's pavement.
    /// </summary>
    private static bool OccupiesUnresolvedRunway(AircraftState other, string airport, string designator)
    {
        if (AwaitsTakeoffClearanceOnRunway(other))
        {
            return (OccupiedRunwayOf(other) is { } occupied) && SameAirportOrUnknown(occupied.AirportId, airport) && occupied.Id.Contains(designator);
        }

        if (other.Phases is not { CurrentPhase: HoldingInPositionPhase } phases)
        {
            return false;
        }

        return new[] { phases.DepartureRunway, phases.AssignedRunway }.Any(own =>
            (own is not null)
            && SameAirportOrUnknown(own.AirportId, airport)
            && own.Id.Contains(designator)
            && RunwayOccupancy.IsOnPavement(other, own)
        );
    }

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
    /// The clearance still governs an approach to the runway only while the aircraft is airborne.
    /// Touchdown ends the 3-9-4.c.1(b) restriction for every landing-family clearance: 3-9-4.a is
    /// clearance-agnostic — it contemplates LUAW behind "another aircraft which has landed on … the
    /// runway" (traffic information may even be omitted when that traffic is clearly visible), and a
    /// rolling touch-and-go or a stopped stop-and-go is such an aircraft. What protects the LUAW
    /// aircraft afterwards is 3-9-6.b at takeoff-clearance time — the preceding landing aircraft must
    /// be clear of the runway before the roll begins (<see cref="WarnIfLandedTrafficBlocksTakeoff"/>).
    /// The phase cannot decide this:
    /// <see cref="LandingPhase"/> spans flare through rollout, so only <see cref="AircraftState.IsOnGround"/>
    /// marks the boundary.
    /// </summary>
    private static bool IsClearanceStillPendingUse(AircraftState other) => !other.IsOnGround;

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
