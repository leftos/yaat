using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

internal sealed record DepartureRouteResult(
    List<NavigationTarget> Targets,
    string? SidId,
    double? DepartureHeadingMagnetic = null,
    bool RvSidDeferHeadingUntilMinAlt = false,
    bool RvSidHoldRunwayHeading = false,
    List<ProcedureLeg>? ProcedureLegs = null,
    ProcedureSource? Source = null
);

internal static class DepartureClearanceHandler
{
    private static readonly ILogger Log = SimLog.CreateLogger("DepartureClearanceHandler");

    /// <summary>
    /// When CTO bundles an explicit climb-to altitude, mirror it onto
    /// <see cref="ControlTargets.AssignedAltitude"/> so the datablock and SALT
    /// observe a single source of truth from issuance —
    /// matching the CM/DM/FA pattern. Bare LUAW (without takeoff) does not
    /// authorize the climb yet, so leave the field alone in that case. The altitude is the last clearance, so it cancels
    /// an earlier climb via's "except maintain", its top altitude and the via mode itself (7110.65 4-5-7.h.11 NOTE 1).
    /// </summary>
    private static void SyncControllerAssignedAltitude(AircraftState aircraft, ClearanceType type, int? assignedAltitude)
    {
        if ((type == ClearanceType.ClearedForTakeoff) && (assignedAltitude is { } v))
        {
            aircraft.Targets.AssignedAltitude = v;
            aircraft.Procedure.SidViaMode = false;
            aircraft.Procedure.SidViaCeiling = null;
            aircraft.Procedure.ClimbViaTargetFt = null;
            aircraft.Procedure.ClimbViaTransmissionId = null;
        }
    }

    /// <summary>
    /// Resolves what a takeoff or line-up clearance gives an IFR departure from its SID transition's TDLS defaults, as vTDLS
    /// fills the clearance (<see cref="ArtccConfigResolver.GetSidDepartureAltitude"/>), so <see cref="InitialClimbPhase"/>
    /// flies it when no altitude is commanded. Runs only once the clearance has been given effect. A climb-via default puts
    /// the climb via read in the departure clearance in force from takeoff
    /// (<see cref="NavigationCommandHandler.TryPutTdlsClimbViaInForce"/>), its top "EXC MAINT X" or the SID's published
    /// top, with <paramref name="ctx"/>'s transmission and without cancelling anything assigned on the ground; an initial
    /// altitude is stored as <see cref="AircraftProcedure.SidInitialAltitudeFt"/>, a maintain; with neither, or when no SID
    /// can be activated for the climb via, nothing is stored, and a takeoff clearance (<paramref name="isTakeoffClearance"/>)
    /// on a filed SID the departure airport publishes warns the instructor that the departure has no initial altitude, with
    /// or without a TDLS config; a departure filing no SID is not warned. A climb via already in force (a <c>CVIA</c> given on
    /// the ground, or one an earlier line-up clearance put in force) and an altitude already assigned win: nothing is
    /// resolved. A clearance carrying its own altitude (<paramref name="clearanceAltitude"/>) resolves nothing and drops the
    /// stored maintain. VFR clears the maintain.
    /// </summary>
    internal static void StoreSidInitialAltitude(AircraftState aircraft, DispatchContext ctx, int? clearanceAltitude, bool isTakeoffClearance)
    {
        if (aircraft.FlightPlan.IsVfr || (clearanceAltitude is not null))
        {
            aircraft.Procedure.SidInitialAltitudeFt = null;
            return;
        }

        if (aircraft.Procedure.SidViaMode || (aircraft.Targets.AssignedAltitude is not null))
        {
            return;
        }

        TdlsDepartureAltitude resolved = ctx.ArtccConfig.GetSidDepartureAltitude(aircraft.FlightPlan.Departure ?? "", aircraft.FlightPlan.Route);
        aircraft.Procedure.SidInitialAltitudeFt = null;
        if (resolved.Kind == TdlsDepartureAltitudeKind.Maintain)
        {
            aircraft.Procedure.SidInitialAltitudeFt = resolved.AltitudeFt;
            return;
        }

        if (resolved.Kind == TdlsDepartureAltitudeKind.ClimbVia)
        {
            if (NavigationCommandHandler.TryPutTdlsClimbViaInForce(aircraft, ctx.TransmissionId, resolved.AltitudeFt))
            {
                return;
            }

            Log.LogWarning(
                "{Callsign}: TDLS climb via for {Route} at {Departure}, but no filed SID activates; no initial altitude",
                aircraft.Callsign,
                aircraft.FlightPlan.Route,
                aircraft.FlightPlan.Departure
            );
        }

        if (isTakeoffClearance)
        {
            WarnNoInitialAltitude(aircraft);
        }
    }

    /// <summary>
    /// Warns the instructor that a takeoff-cleared departure on a filed SID the departure airport publishes has no initial
    /// altitude to climb to. A departure filing no SID is left alone.
    /// </summary>
    private static void WarnNoInitialAltitude(AircraftState aircraft)
    {
        if (FiledProcedureLookup.FindSid(NavigationDatabase.Instance, aircraft.FlightPlan.Departure, aircraft.FlightPlan.Route) is not null)
        {
            aircraft.PendingWarnings.Add($"no initial altitude for {aircraft.Callsign}; assign one");
        }
    }

    /// <summary>
    /// A takeoff or line-up-and-wait clearance that may name a runway (<see cref="ClearedForTakeoffCommand.Runway"/>,
    /// <see cref="LineUpAndWaitCommand.Runway"/>), cleared by <paramref name="clear"/> as a bare one once the named runway is judged.
    /// At a taxiway hold short the implicit taxi there judges it (<see cref="TakeoffFromTaxiwayHold"/>), so the clearance goes ahead.
    /// At the hold short of a runway crossed on the way to the assigned one, <see cref="JudgeAtCrossingHold"/> judges it. Anywhere
    /// else (lined up, at a runway hold short, taxiing) it is matched end for end against the reference runway: the assigned runway,
    /// else the runway held short of, else the runway the taxi route departs from. A different end draws the pilot's query
    /// ("confirm runway 28L? we're lined up on runway 28R") and nothing changes; with no reference runway the clearance goes ahead
    /// and the named runway is assigned once it succeeds.
    /// </summary>
    internal static CommandResult ClearNamingRunway(
        string? namedRunway,
        AircraftState aircraft,
        Phase currentPhase,
        ActiveRunways activeRunways,
        Func<CommandResult> clear
    )
    {
        if ((namedRunway is null) || IsAtTaxiwayHold(aircraft, currentPhase))
        {
            return clear();
        }

        if ((currentPhase is HoldingShortPhase holding) && (CrossingHold(aircraft, holding) is { } crossing))
        {
            return JudgeAtCrossingHold(aircraft, holding, crossing, namedRunway, activeRunways) ?? clear();
        }

        if (ReferenceTakeoffRunway(aircraft, currentPhase, activeRunways) is { } reference)
        {
            return (TakeoffSituation(aircraft, currentPhase) is { } situation) && !IsSameRunwayEnd(namedRunway, reference)
                ? ConfirmRunway(aircraft, namedRunway, situation, reference, forRunway: null)
                : clear();
        }

        return ClearAndAssignNamedRunway(namedRunway, aircraft, clear);
    }

    private static bool IsAtTaxiwayHold(AircraftState aircraft, Phase currentPhase) =>
        (currentPhase is HoldingShortPhase) && (GroundCommandHandler.HeldShortTaxiway(aircraft) is not null);

    /// <summary>
    /// A takeoff or line-up-and-wait clearance naming <paramref name="namedRunway"/> with no reference runway to match it against:
    /// cleared by <paramref name="clear"/>, and the named runway becomes the assigned and departure runway once the clearance succeeds.
    /// </summary>
    private static CommandResult ClearAndAssignNamedRunway(string namedRunway, AircraftState aircraft, Func<CommandResult> clear)
    {
        if (CommandDispatcher.ResolveRunway(aircraft, namedRunway) is not { } runway)
        {
            return UnknownRunway(namedRunway);
        }

        CommandResult result = clear();
        if (result.Success && (aircraft.Phases is { } phases))
        {
            phases.AssignedRunway = runway;
            aircraft.Procedure.DepartureRunway = runway.Designator;
        }

        return result;
    }

    private static CommandResult UnknownRunway(string runway) => new(false, $"Unknown runway {RunwayIdentifier.ToDisplayDesignator(runway)}");

    private static bool IsSameRunwayEnd(string a, string b) =>
        string.Equals(RunwayIdentifier.NormalizeDesignator(a), RunwayIdentifier.NormalizeDesignator(b), StringComparison.OrdinalIgnoreCase);

    private static CommandResult ConfirmRunway(AircraftState aircraft, string runway, string situation, string reference, string? forRunway)
    {
        PilotSpeechText query = PilotResponder.BuildConfirmRunway(aircraft, runway, situation, reference, forRunway);
        return new CommandResult(false, query.Terminal) { PilotUnable = query };
    }

    /// <param name="Crossed">The runway held short of, crossed on the way to <paramref name="Assigned"/>.</param>
    /// <param name="Assigned">The assigned runway beyond it.</param>
    private sealed record CrossingHoldRunways(RunwayInfo Crossed, RunwayInfo Assigned);

    /// <summary>
    /// The runway held short of and the assigned runway when the hold is a <see cref="HoldShortReason.RunwayCrossing"/> hold, or a
    /// crossing the controller re-armed with <c>HS {runway}</c> (<see cref="HoldShortReason.ExplicitHoldShort"/> on a runway), whose
    /// runway does not overlap the assigned one, else null.
    /// </summary>
    private static CrossingHoldRunways? CrossingHold(AircraftState aircraft, HoldingShortPhase holding) =>
        (holding.HoldShort.Reason is HoldShortReason.RunwayCrossing or HoldShortReason.ExplicitHoldShort)
        && (aircraft.Phases?.AssignedRunway is { } assigned)
        && (HeldShortRunway(aircraft, holding) is { } held)
        && !held.Id.Overlaps(assigned.Id)
            ? new CrossingHoldRunways(held, assigned)
            : null;

    /// <summary>
    /// A takeoff or line-up-and-wait clearance naming <paramref name="namedRunway"/> at the hold short of a runway crossed on the way
    /// to the assigned runway (7110.65 3-9-10.d/.e): the crossed runway is the intersection departure, and the clearance goes ahead
    /// (null); the assigned runway beyond needs the crossing the clearance does not include, so the pilot says unable; any other runway
    /// draws the pilot's query. Nothing changes on a refusal.
    /// </summary>
    private static CommandResult? JudgeAtCrossingHold(
        AircraftState aircraft,
        HoldingShortPhase holding,
        CrossingHoldRunways crossing,
        string namedRunway,
        ActiveRunways activeRunways
    )
    {
        var named = RunwayIdentifier.Parse(namedRunway);
        if (named.Overlaps(crossing.Crossed.Id))
        {
            return null;
        }

        string heldTarget = holding.HoldShort.TargetName ?? crossing.Crossed.Designator;
        RunwayInfo assigned = crossing.Assigned;
        string heldEnd = CrossingRunwayEnd(aircraft, heldTarget, assigned, activeRunways);
        if (named.Overlaps(assigned.Id))
        {
            PilotSpeechText unable = PilotResponder.BuildUnableNeedToCrossRunway(aircraft, heldEnd, taxiway: null);
            return new CommandResult(false, unable.Terminal) { PilotUnable = unable };
        }

        return ConfirmRunway(aircraft, namedRunway, "holding short of", heldEnd, assigned.Designator);
    }

    /// <summary>
    /// Where the aircraft stands relative to its takeoff runway, as the pilot says it, or null where no takeoff clearance applies.
    /// </summary>
    private static string? TakeoffSituation(AircraftState aircraft, Phase currentPhase) =>
        currentPhase switch
        {
            LinedUpAndWaitingPhase or LineUpPhase or HoldingInPositionPhase => "lined up on",
            HoldingShortPhase holding when HeldShortRunway(aircraft, holding) is not null => "holding short of",
            HoldingShortPhase or TaxiingPhase or CrossingRunwayPhase => "taxiing to",
            _ => null,
        };

    /// <summary>
    /// The runway a named takeoff runway is matched against: the assigned runway (also the runway a lined-up aircraft is on, which
    /// the line-up reads from it), else the runway held short of by its active end, else the runway the taxi route departs from.
    /// </summary>
    private static string? ReferenceTakeoffRunway(AircraftState aircraft, Phase currentPhase, ActiveRunways activeRunways)
    {
        if (aircraft.Phases?.AssignedRunway is { } assigned)
        {
            return assigned.Designator;
        }

        AirportGroundLayout? layout = aircraft.Ground.Layout;
        if ((currentPhase is HoldingShortPhase holding) && (HeldShortRunway(aircraft, holding) is { } held))
        {
            return RunwayCrossingEnd.ActiveEndOf(aircraft, holding.HoldShort.TargetName, layout, activeRunways) ?? held.Designator;
        }

        string? destination = aircraft
            .Ground.AssignedTaxiRoute?.HoldShortPoints.Find(hs => hs.Reason == HoldShortReason.DestinationRunway)
            ?.TargetName;
        return destination is null
            ? null
            : RunwayCrossingEnd.ActiveEndOf(aircraft, destination, layout, activeRunways)
                ?? CommandDispatcher.FindRunway(aircraft, destination)?.Designator;
    }

    private static RunwayInfo? HeldShortRunway(AircraftState aircraft, HoldingShortPhase holding) =>
        (holding.HoldShort.TargetName is { } target) && (GroundCommandHandler.HeldShortTaxiway(aircraft) is null)
            ? CommandDispatcher.FindRunway(aircraft, target)
            : null;

    internal static CommandResult TryClearedForTakeoff(
        ClearedForTakeoffCommand cto,
        AircraftState aircraft,
        LinedUpAndWaitingPhase luaw,
        DispatchContext ctx
    )
    {
        if (aircraft.Phases?.AssignedRunway is not { } assignedRunway)
        {
            return new CommandResult(false, "No runway assigned — cannot clear for takeoff");
        }

        // 3-9-6: another aircraft holding in position on the same runway (dual LUAW) means this
        // departure cannot begin its roll — advisory only, the clearance stands (issue #409).
        RunwaySafetyAdvisor.WarnIfRunwayOccupiedForTakeoff(aircraft, assignedRunway, ctx);

        luaw.Departure = cto.Departure;
        luaw.AssignedAltitude = cto.AssignedAltitude;
        luaw.SatisfyClearance(ClearanceType.ClearedForTakeoff);
        SyncControllerAssignedAltitude(aircraft, ClearanceType.ClearedForTakeoff, cto.AssignedAltitude);

        string? takeoffDesignator = null;
        DepartureRouteResult? routeResult = null;

        // Propagate departure to TakeoffPhase and InitialClimbPhase
        if (aircraft.Phases is not null)
        {
            routeResult = ResolveDepartureRoute(cto.Departure, aircraft);

            for (int i = 0; i < aircraft.Phases.Phases.Count; i++)
            {
                Phase p = aircraft.Phases.Phases[i];
                if (p is TakeoffPhase tkoff)
                {
                    tkoff.SetAssignedDeparture(cto.Departure);
                }
                else if (p is HelicopterTakeoffPhase htkoff)
                {
                    htkoff.SetAssignedDeparture(cto.Departure);
                }
                else if (p is InitialClimbPhase climb && p.Status == PhaseStatus.Pending)
                {
                    SetInitialClimbProperties(climb, cto.Departure, cto.AssignedAltitude, routeResult, aircraft);
                }
            }

            // Circuit departures (closed traffic / pattern exit): establish pattern mode and append
            // the circuit. Capture the takeoff runway before the circuit apply overwrites AssignedRunway.
            if (IsCircuitDeparture(cto.Departure) && aircraft.Phases.AssignedRunway is { } ctRunway)
            {
                takeoffDesignator = ctRunway.Designator;
                ApplyCircuitDeparture(
                    cto.Departure,
                    aircraft,
                    aircraft.Phases,
                    ctRunway,
                    cto.AssignedAltitude,
                    removeInitialClimb: true,
                    activeRunways: ctx.ActiveRunways
                );
            }
        }

        return BuildDepartureMessage(
            ClearanceType.ClearedForTakeoff,
            takeoffDesignator ?? aircraft.Phases?.AssignedRunway?.Designator ?? "unknown",
            cto.Departure,
            cto.AssignedAltitude
        ) with
        {
            Advisory = ProcedureSourceSidAdvisory(ClearanceType.ClearedForTakeoff, routeResult, aircraft),
        };
    }

    /// <param name="groundLayout">
    /// The room's layout for the aircraft's airport, falling back to <see cref="AircraftGroundOps.Layout"/> when
    /// null. Positional rather than defaulted so every call site has to answer for it:
    /// <see cref="LineUpFromPosition"/> starts <see cref="LineUpPhase"/> itself rather than leaving it to the tick
    /// loop, and <see cref="LineUpPhase.OnStart"/> faults outright when neither source yields a layout.
    /// </param>
    /// <param name="taxiDispatch">
    /// How the clearance reached the handler, for the implicit taxi a clearance at a taxiway hold short makes
    /// (<see cref="TakeoffFromTaxiwayHold"/>): the world's aircraft and the room's active runways.
    /// </param>
    /// <param name="requestedRunway">
    /// The runway a takeoff or line-up-and-wait clearance names (<see cref="ClearedForTakeoffCommand.Runway"/>,
    /// <see cref="LineUpAndWaitCommand.Runway"/>), or null. Only a clearance at a hold short reads it (<see cref="TakeoffFromTaxiwayHold"/>,
    /// <see cref="LineUpFromHoldShort"/>); elsewhere <see cref="ClearNamingRunway"/> has judged it.
    /// </param>
    internal static CommandResult TryDepartureClearance(
        AircraftState aircraft,
        Phase currentPhase,
        ClearanceType clearanceType,
        DepartureInstruction departure,
        int? assignedAltitude,
        string? requestedRunway,
        DispatchContext ctx,
        GroundCommandHandler.TaxiDispatch taxiDispatch,
        ILogger logger
    )
    {
        CommandResult result = TryDepartureClearanceCore(
            aircraft,
            currentPhase,
            new DepartureRequest(clearanceType, departure, assignedAltitude, requestedRunway, ctx.CrossingsClearedInTransmission ?? []),
            ctx.GroundLayout,
            taxiDispatch,
            logger,
            ctx.ActiveRunways
        );

        // 7110.65 3-9-4: do not authorize LUAW when an aircraft holds a landing-family clearance
        // for the same runway, or is already holding in position on it (advisory only — the
        // clearance stands). CTO stays outside the arrival-clearance check — takeoff with an
        // arrival cleared to land is ordinary anticipated separation — but a CTO over an
        // aircraft holding in position on the runway violates 3-9-6 same-runway separation and
        // draws its own advisory (issue #409).
        if (result.Success && (clearanceType == ClearanceType.LineUpAndWait) && aircraft.Phases?.AssignedRunway is { } runway)
        {
            RunwaySafetyAdvisor.WarnIfArrivalCleared(aircraft, runway, ctx);
            RunwaySafetyAdvisor.WarnIfAnotherHoldingInPosition(aircraft, runway, ctx);
            RunwaySafetyAdvisor.WarnIfTrafficOnFinal(aircraft, runway, ctx);
        }

        // Only when the aircraft is entering the runway now: at the runway's hold-short, in position,
        // mid-line-up, or taxiing onto the taxiway it held short of to the runway's hold line there
        // (TakeoffFromTaxiwayHold), which puts it on the runway within seconds. A CTO stored during
        // taxi, or at a taxiway hold-short where the aircraft stays held, is applied when the
        // aircraft reaches its departure hold-short; warning against occupancy then would be noise.
        bool enteringRunwayNow =
            currentPhase is HoldingInPositionPhase or LineUpPhase
            || (result.TaxiwayHoldTakeoff is { Outcome: not TaxiwayHoldTakeoffOutcome.StaysHeld })
            || (
                currentPhase is HoldingShortPhase enteringHold
                && enteringHold.HoldShort.TargetName is { } holdTarget
                && CommandDispatcher.ResolveRunway(aircraft, holdTarget) is not null
            );
        if (
            result.Success
            && (clearanceType == ClearanceType.ClearedForTakeoff)
            && enteringRunwayNow
            && aircraft.Phases?.AssignedRunway is { } takeoffRunway
        )
        {
            RunwaySafetyAdvisor.WarnIfRunwayOccupiedForTakeoff(aircraft, takeoffRunway, ctx);
        }

        return result;
    }

    /// <summary>
    /// A departure clearance as issued: its type, departure instruction, altitude and named runway, and the runways a CROSS earlier
    /// in its transmission cleared (<see cref="DispatchContext.CrossingsClearedInTransmission"/>), which its implicit taxi from a
    /// taxiway hold short crosses.
    /// </summary>
    private sealed record DepartureRequest(
        ClearanceType ClearanceType,
        DepartureInstruction Departure,
        int? AssignedAltitude,
        string? RequestedRunway,
        IReadOnlyList<string> CrossingsCleared
    );

    private static CommandResult TryDepartureClearanceCore(
        AircraftState aircraft,
        Phase currentPhase,
        DepartureRequest request,
        AirportGroundLayout? groundLayout,
        GroundCommandHandler.TaxiDispatch taxiDispatch,
        ILogger logger,
        ActiveRunways activeRunways
    )
    {
        (ClearanceType clearanceType, DepartureInstruction departure, int? assignedAltitude, _, _) = request;
        if (currentPhase is HoldingShortPhase holding)
        {
            return LineUpFromHoldShort(aircraft, holding, request, taxiDispatch, logger, activeRunways);
        }

        // A crossing on the way to the route's destination runway is the same rolling situation as
        // plain taxiing: store the clearance now and let the taxi machinery apply it at route end.
        // StoreDepartureClearanceDuringTaxi validates against the assigned route, so a crossing with
        // no departure hold-short downstream still fails with a specific message.
        if (currentPhase is TaxiingPhase or CrossingRunwayPhase)
        {
            return StoreDepartureClearanceDuringTaxi(aircraft, clearanceType, departure, assignedAltitude);
        }

        // Aircraft is lining up — CTO can pre-satisfy the upcoming LUAW phase
        if (currentPhase is LineUpPhase && clearanceType == ClearanceType.ClearedForTakeoff)
        {
            return SatisfyUpcomingTakeoffClearance(aircraft, departure, assignedAltitude, logger, activeRunways);
        }

        // Re-issuing LUAW to an aircraft held mid-line-up (via HOLD) lifts the hold so the
        // line-up resumes; it is still a line-up-and-wait, so it stops at the centerline.
        if (currentPhase is LineUpPhase heldLineup && clearanceType == ClearanceType.LineUpAndWait && heldLineup.HoldPosition)
        {
            heldLineup.HoldPosition = false;
            return BuildDepartureMessage(clearanceType, aircraft.Phases?.AssignedRunway?.Designator ?? "unknown", departure, assignedAltitude);
        }

        // Aircraft holding in position (e.g. after a WARPG onto the pavement) — allow LUAW/CTO with assigned runway.
        // A WARPG onto a gate or helipad parks the aircraft instead, and AtParkingPhase refuses both.
        if (currentPhase is HoldingInPositionPhase)
        {
            return LineUpFromPosition(aircraft, clearanceType, departure, assignedAltitude, groundLayout, logger, activeRunways);
        }

        if (clearanceType == ClearanceType.ClearedForTakeoff)
        {
            return new CommandResult(false, "Aircraft is not lined up and waiting");
        }

        return new CommandResult(false, "Line up and wait requires aircraft to be taxiing or holding short");
    }

    private static CommandResult LineUpFromHoldShort(
        AircraftState aircraft,
        HoldingShortPhase holding,
        DepartureRequest request,
        GroundCommandHandler.TaxiDispatch taxiDispatch,
        ILogger logger,
        ActiveRunways activeRunways
    )
    {
        (ClearanceType clearanceType, DepartureInstruction departure, int? assignedAltitude, string? requestedRunway, _) = request;
        string? runwayId = holding.HoldShort.TargetName;
        if (runwayId is null)
        {
            return new CommandResult(false, "Hold short point has no runway assigned");
        }

        RunwayInfo? runway = CommandDispatcher.ResolveRunway(aircraft, runwayId);
        if (runway is null)
        {
            // Target is a taxiway (e.g., "HS E"), not a runway. A CTO there, or a LUAW with a runway assigned or named, taxis onto it.
            if (
                (GroundCommandHandler.HeldShortTaxiway(aircraft) is { } heldTaxiway)
                && (
                    (clearanceType == ClearanceType.ClearedForTakeoff)
                    || (aircraft.Phases?.AssignedRunway is not null)
                    || (requestedRunway is not null)
                )
            )
            {
                return TakeoffFromTaxiwayHold(aircraft, heldTaxiway, request, taxiDispatch, logger);
            }

            // Clear the hold-short and store the departure clearance so the
            // aircraft resumes taxiing to its destination runway.
            holding.SatisfyClearance(ClearanceType.RunwayCrossing);
            return StoreDepartureClearanceDuringTaxi(aircraft, clearanceType, departure, assignedAltitude);
        }

        // If the controller previously assigned a specific runway end (via RWY command) and it refers
        // to the same physical runway as the hold-short, respect that assignment. This preserves the
        // controller's explicit direction (e.g., RWY 01R at SFO when hold-short TargetName is "1R").
        // A takeoff or line-up clearance naming this runway (judged by ClearNamingRunway: at a crossing hold, the crossed runway as an
        // intersection departure) departs the end it names.
        if (
            (requestedRunway is not null)
            && RunwayIdentifier.Parse(requestedRunway).Overlaps(runway.Id)
            && (CommandDispatcher.ResolveRunway(aircraft, requestedRunway) is { } requested)
        )
        {
            runway = requested;
        }
        else if (aircraft.Phases?.AssignedRunway is { } existingRunway && runway.Id.Overlaps(existingRunway.Id))
        {
            runway = existingRunway;
        }

        // Satisfy the runway crossing clearance so HoldingShortPhase completes
        holding.SatisfyClearance(ClearanceType.RunwayCrossing);

        // Set the assigned runway and insert tower phases
        aircraft.Phases!.AssignedRunway = runway;
        aircraft.Procedure.DepartureRunway = runway.Designator;
        DepartureRouteResult? routeResult = InsertTowerPhasesAfterCurrent(
            aircraft,
            clearanceType,
            departure,
            assignedAltitude,
            runway,
            logger,
            activeRunways
        );
        SyncControllerAssignedAltitude(aircraft, clearanceType, assignedAltitude);

        return BuildDepartureMessage(clearanceType, runway.Designator, departure, assignedAltitude) with
        {
            Advisory = ProcedureSourceSidAdvisory(clearanceType, routeResult, aircraft),
        };
    }

    /// <summary>
    /// A takeoff or line-up-and-wait clearance given while holding short of taxiway <paramref name="taxiway"/> is an implicit taxi
    /// onto it for an intersection departure (7110.65 3-9-4.m, 3-9-10.b): the aircraft is re-cleared
    /// <c>TAXI {taxiway} RWY {assigned runway}</c>, which ends at the runway's hold line on that taxiway, and the clearance is stored
    /// for the taxi machinery to line up (and depart) there. Traffic standing on that way to the runway's hold line refuses the
    /// clearance at once: the pilot says unable and stays held short. When the taxiway does not reach the assigned runway, or no
    /// runway is assigned, the aircraft stays held and the clearance is stored on its existing route; it moves on a <c>RES</c> or a
    /// new <c>TAXI</c>. A clearance naming a runway other than the assigned one is judged by
    /// <see cref="TakeoffOnNamedRunwayFromTaxiwayHold"/>.
    /// </summary>
    private static CommandResult TakeoffFromTaxiwayHold(
        AircraftState aircraft,
        string taxiway,
        DepartureRequest request,
        GroundCommandHandler.TaxiDispatch taxiDispatch,
        ILogger logger
    )
    {
        RunwayInfo? assigned = aircraft.Phases?.AssignedRunway;
        if ((request.RequestedRunway is { } named) && ((assigned is null) || !IsSameRunwayEnd(named, assigned.Designator)))
        {
            return TakeoffOnNamedRunwayFromTaxiwayHold(aircraft, taxiway, request, named, taxiDispatch, logger);
        }

        if ((assigned is not null) && (TaxiOntoTaxiwayForDeparture(aircraft, taxiway, assigned, request, taxiDispatch, logger) is { } taken))
        {
            return taken;
        }

        return StoreStayingHeld(aircraft, taxiway, request);
    }

    /// <summary>
    /// The clearance stored on the aircraft's existing route while it stays held short of <paramref name="taxiway"/>, read back with
    /// where it holds.
    /// </summary>
    private static CommandResult StoreStayingHeld(AircraftState aircraft, string taxiway, DepartureRequest request)
    {
        CommandResult held = StoreDepartureClearanceDuringTaxi(aircraft, request.ClearanceType, request.Departure, request.AssignedAltitude);
        return held.Success ? held with { TaxiwayHoldTakeoff = new TaxiwayHoldTakeoff(taxiway, TaxiwayHoldTakeoffOutcome.StaysHeld) } : held;
    }

    /// <summary>
    /// The implicit taxi onto <paramref name="taxiway"/> to <paramref name="runway"/>'s hold line there, with the clearance stored on
    /// it: the pilot's unable when traffic stands on the way, the stored clearance (or its failure) once the taxi is installed, or
    /// null when the taxi does not resolve and the aircraft stays held.
    /// </summary>
    private static CommandResult? TaxiOntoTaxiwayForDeparture(
        AircraftState aircraft,
        string taxiway,
        RunwayInfo runway,
        DepartureRequest request,
        GroundCommandHandler.TaxiDispatch taxiDispatch,
        ILogger logger
    )
    {
        // The implicit taxi keeps the crew's standing taxi instructions: a running expedite and a commanded taxi speed,
        // which a fresh TAXI would otherwise reset. It pre-clears only the crossings its transmission cleared, whatever the room's
        // auto-cross mode.
        double? commandedTaxiSpeedKts = aircraft.Ground.CommandedTaxiSpeedKts;
        TaxiCommand taxi = ImplicitTaxi(aircraft, taxiway, runway, request.CrossingsCleared);
        GroundCommandHandler.TaxiDispatch implicitTaxi = ImplicitTaxiDispatch(taxiDispatch);
        if (TrafficOnImplicitTaxi(aircraft, taxi, implicitTaxi) is { } blocked)
        {
            string blockedTaxiway = blocked.Taxiway ?? taxiway;
            logger.LogDebug(
                "{Callsign}: unable at the hold short of {Taxiway} — {Traffic} stands on {BlockedTaxiway} on the way to the {Runway} hold short",
                aircraft.Callsign,
                taxiway,
                blocked.Traffic.Callsign,
                blockedTaxiway,
                runway.Designator
            );
            PilotSpeechText unable = PilotResponder.BuildUnableTrafficOnTaxiway(aircraft, blockedTaxiway);
            return new CommandResult(false, unable.Terminal) { PilotUnable = unable };
        }

        CommandResult taxiResult = GroundCommandHandler.TryTaxi(aircraft, taxi, aircraft.Ground.Layout, implicitTaxi);
        if (taxiResult.Success)
        {
            aircraft.Ground.CommandedTaxiSpeedKts = commandedTaxiSpeedKts;
            return StoreImplicitTaxiTakeoff(aircraft, taxiway, request.ClearanceType, request.Departure, request.AssignedAltitude, logger);
        }

        logger.LogDebug(
            "{Callsign}: {Clearance} at the hold short of {Taxiway} keeps the hold — taxi to {Runway} failed: {Reason}",
            aircraft.Callsign,
            request.ClearanceType,
            taxiway,
            runway.Designator,
            taxiResult.Message
        );
        return null;
    }

    /// <summary>
    /// <c>TAXI {taxiway} RWY {runway}</c>, keeping a running expedite, with the crossings <paramref name="crossingsCleared"/> the
    /// clearance's transmission cleared (<see cref="DepartureRequest.CrossingsCleared"/>) as its <c>CROSS</c> runways.
    /// </summary>
    private static TaxiCommand ImplicitTaxi(AircraftState aircraft, string taxiway, RunwayInfo runway, IReadOnlyList<string> crossingsCleared) =>
        new(
            [taxiway],
            [],
            Expedite: aircraft.Ground.IsExpeditingTaxi,
            DestinationRunway: runway.Designator,
            CrossRunways: crossingsCleared.Count > 0 ? [.. crossingsCleared] : null
        );

    /// <summary>
    /// The refusal, before anything applies, of a transmission whose CROSS runways (<paramref name="carried"/>) a takeoff or
    /// line-up clearance at the hold short of <paramref name="taxiway"/> carries into its implicit taxi: a carried runway that is
    /// the clearance's own runway (the one it names, else the assigned one) is no crossing, and one the implicit route never
    /// crosses is the route's own "not applied" warning. With no runway to the clearance, or no implicit taxi resolving to it, nothing
    /// carries the crossings and the transmission is refused too. Null when nothing is refused.
    /// </summary>
    /// <param name="aircraft">The aircraft held short of <paramref name="taxiway"/>.</param>
    /// <param name="taxiway">The taxiway held short of.</param>
    /// <param name="namedRunway">The runway the clearance names, or null.</param>
    /// <param name="clearanceType">Takeoff or line-up and wait.</param>
    /// <param name="carried">The runways the transmission's CROSS commands named before the clearance.</param>
    /// <param name="taxiDispatch">How the transmission was dispatched.</param>
    /// <returns>The refusal, or null.</returns>
    internal static CommandResult? RefuseCarriedCrossings(
        AircraftState aircraft,
        string taxiway,
        string? namedRunway,
        ClearanceType clearanceType,
        IReadOnlyList<string> carried,
        GroundCommandHandler.TaxiDispatch taxiDispatch
    )
    {
        RunwayInfo? runway = namedRunway is null ? aircraft.Phases?.AssignedRunway : CommandDispatcher.ResolveRunway(aircraft, namedRunway);
        if ((runway is null) || (aircraft.Ground.Layout is not { } layout))
        {
            return NoRouteToCarry(carried);
        }

        string clearance = clearanceType == ClearanceType.LineUpAndWait ? "line-up" : "takeoff";
        foreach (string crossed in carried)
        {
            if (RunwayIdentifier.Parse(crossed).Overlaps(runway.Id))
            {
                string display = RunwayIdentifier.ToDisplayDesignator(crossed);
                return new CommandResult(false, $"CROSS {display}: {display} is the runway of the {clearance} clearance");
            }
        }

        if (PreviewImplicitTaxi(aircraft, ImplicitTaxi(aircraft, taxiway, runway, carried), layout, taxiDispatch).Route is not { } route)
        {
            return NoRouteToCarry(carried);
        }

        string? notApplied = route.Warnings.Find(w =>
            w.StartsWith("CROSS ", StringComparison.Ordinal) && w.Contains(" not applied", StringComparison.Ordinal)
        );
        return notApplied is null ? null : new CommandResult(false, notApplied);
    }

    /// <summary>
    /// The refusal of carried crossings no implicit taxi can carry: the clearance has no runway, or its implicit taxi does not resolve.
    /// Names the first carried runway.
    /// </summary>
    private static CommandResult NoRouteToCarry(IReadOnlyList<string> carried) =>
        new(false, $"CROSS {RunwayIdentifier.ToDisplayDesignator(carried[0])}: no departure route to carry the crossing");

    /// <summary>
    /// The implicit taxi <paramref name="taxi"/> resolved with nothing installed and no traffic in view, its <c>CROSS</c> runways
    /// cleared on the route as the install clears them (<see cref="GroundCommandHandler.ApplyCrossKeywords"/>).
    /// </summary>
    private static GroundCommandHandler.TaxiRouteResolution PreviewImplicitTaxi(
        AircraftState aircraft,
        TaxiCommand taxi,
        AirportGroundLayout layout,
        GroundCommandHandler.TaxiDispatch taxiDispatch
    )
    {
        GroundCommandHandler.TaxiRouteResolution resolution = GroundCommandHandler.ResolveTaxiRouteWithFailure(
            aircraft,
            taxi,
            layout,
            ImplicitTaxiDispatch(taxiDispatch) with
            {
                ListAircraft = null,
            }
        );
        if (resolution.Route is { } route)
        {
            GroundCommandHandler.ApplyCrossKeywords(route, taxi);
        }

        return resolution;
    }

    private static GroundCommandHandler.TaxiDispatch ImplicitTaxiDispatch(GroundCommandHandler.TaxiDispatch taxiDispatch) =>
        taxiDispatch with
        {
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };

    /// <summary>
    /// A takeoff or line-up-and-wait clearance naming runway <paramref name="named"/>, not the assigned one, given while held short of
    /// <paramref name="taxiway"/> (7110.65 3-9-4, 3-9-10.d/.e). The implicit taxi onto the taxiway to that runway decides it: a taxiway
    /// that never reaches the runway's hold line draws the pilot's query; one that reaches it only across another runway, a crossing the
    /// clearance does not include, draws the pilot's unable; one that reaches it is the intersection departure or line-up on that
    /// runway, which becomes the assigned runway once the clearance is stored. A taxi that cannot be judged (no layout, or a refusal of another
    /// kind) or does not resolve also draws the query. A refusal or a query changes nothing.
    /// </summary>
    private static CommandResult TakeoffOnNamedRunwayFromTaxiwayHold(
        AircraftState aircraft,
        string taxiway,
        DepartureRequest request,
        string named,
        GroundCommandHandler.TaxiDispatch taxiDispatch,
        ILogger logger
    )
    {
        if (CommandDispatcher.ResolveRunway(aircraft, named) is not { } runway)
        {
            return UnknownRunway(named);
        }

        RunwayInfo? assigned = aircraft.Phases?.AssignedRunway;
        NamedRunwayReach reach = ClassifyNamedRunwayReach(aircraft, taxiway, runway, request.CrossingsCleared, taxiDispatch);
        logger.LogDebug(
            "{Callsign}: {Clearance} naming {Runway} at the hold short of {Taxiway} (assigned {Assigned}) — {Reach}",
            aircraft.Callsign,
            request.ClearanceType,
            runway.Designator,
            taxiway,
            assigned?.Designator ?? "none",
            reach.Kind
        );
        if ((reach.Kind == NamedRunwayReachKind.NeedsCrossing) && (reach.CrossingTarget is { } crossingTarget))
        {
            string crossing = SpokenCrossingRunway(aircraft, crossingTarget, assigned, runway, taxiDispatch.ActiveRunways);
            PilotSpeechText unable = PilotResponder.BuildUnableNeedToCrossRunway(aircraft, crossing, taxiway);
            return new CommandResult(false, unable.Terminal) { PilotUnable = unable };
        }

        CommandResult? departing =
            reach.Kind == NamedRunwayReachKind.Reaches
                ? DepartFromTaxiwayOnNamedRunway(aircraft, taxiway, runway, request, taxiDispatch, logger)
                : null;
        return departing ?? ConfirmRunwayAtTaxiwayHold(aircraft, runway, taxiway, assigned);
    }

    private static CommandResult ConfirmRunwayAtTaxiwayHold(AircraftState aircraft, RunwayInfo runway, string taxiway, RunwayInfo? assigned)
    {
        PilotSpeechText query = PilotResponder.BuildConfirmRunwayAtTaxiwayHold(aircraft, runway.Designator, taxiway, assigned?.Designator);
        return new CommandResult(false, query.Terminal) { PilotUnable = query };
    }

    /// <summary>
    /// The intersection departure on <paramref name="runway"/> from <paramref name="taxiway"/>: the runway is assigned for the implicit
    /// taxi and the stored clearance, and the departure runway is left to the taxi, which sets it once installed and warns of the
    /// runway change. Once the taxi is installed the runway stays assigned, the route and runway agreeing, even if the clearance fails
    /// to store; before that (traffic on the way) the previous assignment is restored. Null when the taxi does not resolve, the
    /// previous assignment restored.
    /// </summary>
    private static CommandResult? DepartFromTaxiwayOnNamedRunway(
        AircraftState aircraft,
        string taxiway,
        RunwayInfo runway,
        DepartureRequest request,
        GroundCommandHandler.TaxiDispatch taxiDispatch,
        ILogger logger
    )
    {
        if (aircraft.Phases is not { } phases)
        {
            return null;
        }

        RunwayInfo? previousRunway = phases.AssignedRunway;
        TaxiRoute? routeBefore = aircraft.Ground.AssignedTaxiRoute;
        phases.AssignedRunway = runway;

        CommandResult? taken = TaxiOntoTaxiwayForDeparture(aircraft, taxiway, runway, request, taxiDispatch, logger);
        if ((taken is { Success: true }) || !ReferenceEquals(routeBefore, aircraft.Ground.AssignedTaxiRoute))
        {
            return taken;
        }

        phases.AssignedRunway = previousRunway;
        return taken;
    }

    private enum NamedRunwayReachKind
    {
        /// <summary>The taxiway reaches the runway's hold line without crossing another runway.</summary>
        Reaches,

        /// <summary>The taxiway never reaches the runway's hold line.</summary>
        DoesNotReach,

        /// <summary>The taxiway reaches the runway's hold line only across another runway the clearance does not include.</summary>
        NeedsCrossing,

        /// <summary>The implicit taxi cannot be judged: no layout, or a refusal that says nothing about reaching the runway.</summary>
        Unjudged,
    }

    /// <param name="Kind">What the implicit taxi to the named runway says.</param>
    /// <param name="CrossingTarget">
    /// The first uncleared crossing's runway pair in route order, for <see cref="NamedRunwayReachKind.NeedsCrossing"/>.
    /// </param>
    private sealed record NamedRunwayReach(NamedRunwayReachKind Kind, string? CrossingTarget);

    /// <summary>
    /// Resolves the implicit taxi onto <paramref name="taxiway"/> to <paramref name="runway"/> as <see cref="TrafficOnImplicitTaxi"/>
    /// does, with nothing installed, and reads what it says about reaching the runway. "Does not reach" is the pathfinder's
    /// <see cref="FailureKind.DestinationUnreachable"/> on the held taxiway itself, or a route that resolves with no hold at the
    /// runway's bar.
    /// </summary>
    private static NamedRunwayReach ClassifyNamedRunwayReach(
        AircraftState aircraft,
        string taxiway,
        RunwayInfo runway,
        IReadOnlyList<string> crossingsCleared,
        GroundCommandHandler.TaxiDispatch taxiDispatch
    )
    {
        if (aircraft.Ground.Layout is not { } layout)
        {
            return new NamedRunwayReach(NamedRunwayReachKind.Unjudged, null);
        }

        GroundCommandHandler.TaxiRouteResolution resolution = PreviewImplicitTaxi(
            aircraft,
            ImplicitTaxi(aircraft, taxiway, runway, crossingsCleared),
            layout,
            taxiDispatch
        );
        if (resolution.Route is not { } route)
        {
            bool neverReaches =
                resolution.Failure is { Kind: FailureKind.DestinationUnreachable, InfeasibleTaxiway: { } infeasible }
                && string.Equals(infeasible, taxiway, StringComparison.OrdinalIgnoreCase);
            return new NamedRunwayReach(neverReaches ? NamedRunwayReachKind.DoesNotReach : NamedRunwayReachKind.Unjudged, null);
        }

        if (!route.HoldShortPoints.Exists(hs => hs.Reason == HoldShortReason.DestinationRunway))
        {
            return new NamedRunwayReach(NamedRunwayReachKind.DoesNotReach, null);
        }

        HoldShortPoint? crossing = route.HoldShortPoints.Find(hs =>
            (hs.Reason == HoldShortReason.RunwayCrossing)
            && !hs.IsCleared
            && (hs.TargetName is { } target)
            && !RunwayIdentifier.Parse(target).Overlaps(runway.Id)
        );
        return crossing is null
            ? new NamedRunwayReach(NamedRunwayReachKind.Reaches, null)
            : new NamedRunwayReach(NamedRunwayReachKind.NeedsCrossing, crossing.TargetName);
    }

    /// <summary>
    /// The end of the crossing's runway pair (<c>28R/10L</c>) the pilot names: the assigned runway's end when the assigned runway is
    /// that runway, else by <see cref="CrossingRunwayEnd"/> against the assigned runway, or the named one when none is assigned.
    /// </summary>
    private static string SpokenCrossingRunway(
        AircraftState aircraft,
        string crossingTarget,
        RunwayInfo? assigned,
        RunwayInfo named,
        ActiveRunways activeRunways
    ) =>
        (assigned is not null) && RunwayIdentifier.Parse(crossingTarget).Contains(assigned.Designator)
            ? assigned.Designator
            : CrossingRunwayEnd(aircraft, crossingTarget, assigned ?? named, activeRunways);

    /// <summary>
    /// The end of the runway <paramref name="crossingTarget"/> the pilot names on the way to <paramref name="reference"/>: its active
    /// end, else the end parallel to the reference runway, else the end nearer the aircraft
    /// (<see cref="RunwayTailClearance.HeldShortRunwayEnd(AirportGroundLayout, string, RunwayInfo?, AircraftState, ActiveRunways)"/>).
    /// With no layout, <see cref="RunwayCrossingEnd.Spoken"/>'s first end.
    /// </summary>
    private static string CrossingRunwayEnd(AircraftState aircraft, string crossingTarget, RunwayInfo reference, ActiveRunways activeRunways) =>
        aircraft.Ground.Layout is { } layout
            ? RunwayTailClearance.HeldShortRunwayEnd(layout, crossingTarget, reference, aircraft, activeRunways)
            : RunwayCrossingEnd.Spoken(aircraft, crossingTarget, null, activeRunways);

    /// <summary>
    /// The nearest aircraft at rest on the ground standing on the implicit taxi's route, from where <paramref name="aircraft"/> stands
    /// up to the destination runway's hold-short bar on that route (<see cref="RunwayCrossingFarSideGate.StandingOnPathAhead"/>), and
    /// the taxiway of the route segment it stands on. The route is resolved as the taxi would resolve it, with nothing installed; its
    /// path joins the segments' end nodes, a fillet's chord standing in for its arc. An aircraft on the runway itself is beyond the bar
    /// and not counted. Null when nothing stands there, or when there is no layout, no world or no
    /// route to judge.
    /// </summary>
    private static (AircraftState Traffic, string? Taxiway)? TrafficOnImplicitTaxi(
        AircraftState aircraft,
        TaxiCommand taxi,
        GroundCommandHandler.TaxiDispatch dispatch
    )
    {
        if (
            (aircraft.Ground.Layout is not { } layout)
            || (dispatch.ListAircraft is not { } listAircraft)
            // Resolved with no traffic in view: a taxi start picked clear of traffic would begin past the very aircraft judged here.
            || (
                GroundCommandHandler.ResolveTaxiRoute(aircraft, taxi, layout, dispatch with { ListAircraft = null })
                is not { Segments.Count: > 0 } route
            )
            || (route.HoldShortPoints.Find(hs => hs.Reason == HoldShortReason.DestinationRunway) is not { } bar)
        )
        {
            return null;
        }

        // Each leg of the path is named for the route segment it runs along: leg i joins path[i] and path[i + 1].
        List<LatLon> path = [aircraft.Position];
        List<string?> legTaxiways = [];
        foreach (TaxiRouteSegment segment in route.Segments)
        {
            if (!layout.Nodes.TryGetValue(segment.ToNodeId, out GroundNode? node))
            {
                break;
            }

            path.Add(node.Position);
            legTaxiways.Add(segment.TaxiwayName);
            if (segment.ToNodeId == bar.NodeId)
            {
                break;
            }
        }

        if (RunwayCrossingFarSideGate.StandingOnPathAhead(aircraft, listAircraft(), path) is not [var nearest, ..])
        {
            return null;
        }

        return (nearest.Aircraft, TaxiwayAlong(path, legTaxiways, nearest.AlongFt));
    }

    /// <summary>The taxiway of the leg of <paramref name="path"/> that <paramref name="alongFt"/> (ft from its start) falls on.</summary>
    private static string? TaxiwayAlong(List<LatLon> path, List<string?> legTaxiways, double alongFt)
    {
        double legStartFt = 0.0;
        for (int i = 1; i < path.Count; i++)
        {
            legStartFt += GeoMath.DistanceNm(path[i - 1], path[i]) * GeoMath.FeetPerNm;
            if (alongFt <= legStartFt)
            {
                return legTaxiways[i - 1];
            }
        }

        return legTaxiways.Count > 0 ? legTaxiways[^1] : null;
    }

    /// <summary>
    /// Stores the takeoff or line-up clearance on the route the implicit taxi onto <paramref name="taxiway"/> just installed. The
    /// departure is an intersection departure from <paramref name="taxiway"/> only when the route's destination hold is the runway's
    /// entry on that taxiway (<see cref="RunwayEntryPoint.Resolve"/>); a route that reaches the runway elsewhere departs from there. A
    /// line-up at the intersection is remembered (<see cref="PhaseList.LineUpIntersectionTaxiway"/>) for the takeoff clearance's
    /// readback.
    /// </summary>
    private static CommandResult StoreImplicitTaxiTakeoff(
        AircraftState aircraft,
        string taxiway,
        ClearanceType clearanceType,
        DepartureInstruction departure,
        int? assignedAltitude,
        ILogger logger
    )
    {
        CommandResult departing = StoreDepartureClearanceDuringTaxi(aircraft, clearanceType, departure, assignedAltitude);
        string runwayDisplay = RunwayIdentifier.ToDisplayDesignator(aircraft.Phases?.AssignedRunway?.Designator ?? "");
        if (!departing.Success)
        {
            logger.LogWarning(
                "{Callsign}: {Clearance} at the hold short of {Taxiway} taxied it toward {Runway} but the clearance was not stored: {Reason}",
                aircraft.Callsign,
                clearanceType,
                taxiway,
                runwayDisplay,
                departing.Message
            );
            return new CommandResult(
                false,
                $"{aircraft.Callsign} is taxiing to the {runwayDisplay} hold short on {taxiway} without a departure clearance: {departing.Message}"
            );
        }

        bool fromTaxiway = DepartsFromTaxiway(aircraft, taxiway);
        if (fromTaxiway && (clearanceType == ClearanceType.LineUpAndWait) && (aircraft.Phases is { } phases))
        {
            phases.LineUpIntersectionTaxiway = taxiway;
        }

        TaxiwayHoldTakeoffOutcome outcome = fromTaxiway ? TaxiwayHoldTakeoffOutcome.IntersectionDeparture : TaxiwayHoldTakeoffOutcome.OtherEntry;
        return departing with { TaxiwayHoldTakeoff = new TaxiwayHoldTakeoff(taxiway, outcome) };
    }

    private static bool DepartsFromTaxiway(AircraftState aircraft, string taxiway)
    {
        if (
            aircraft.Ground.Layout is not { } layout
            || aircraft.Phases?.AssignedRunway is not { } runway
            || aircraft.Ground.AssignedTaxiRoute?.HoldShortPoints.Find(hs => hs.Reason == HoldShortReason.DestinationRunway) is not { } bar
        )
        {
            return false;
        }

        string? entry = RunwayEntryPoint.Resolve(layout, bar.NodeId, runway.Designator, null);
        return string.Equals(entry, taxiway, StringComparison.OrdinalIgnoreCase);
    }

    internal static CommandResult LineUpFromPosition(
        AircraftState aircraft,
        ClearanceType clearanceType,
        DepartureInstruction departure,
        int? assignedAltitude,
        AirportGroundLayout? groundLayout,
        ILogger logger,
        ActiveRunways activeRunways
    )
    {
        if (aircraft.Phases?.AssignedRunway is not { } runway)
        {
            return new CommandResult(false, "No runway assigned — use RWY first");
        }

        // Unlike the hold-short path, this one starts the rebuilt phase list itself instead of leaving
        // it to the tick loop, so the layout has to be threaded in: LineUpPhase.OnStart faults on a null
        // ctx.GroundLayout and the aircraft accepts the clearance without ever moving (issue #315).
        AirportGroundLayout? layout = groundLayout ?? aircraft.Ground.Layout;

        // Clear the holding phase and rebuild with tower departure phases
        // No route to the room's active runways: this handler is not given the dispatch context.
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout);
        aircraft.Phases.Clear(ctx);

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Procedure.DepartureRunway = runway.Designator;
        DepartureRouteResult? routeResult = InsertTowerPhasesAfterCurrent(
            aircraft,
            clearanceType,
            departure,
            assignedAltitude,
            runway,
            logger,
            activeRunways
        );
        // No route to the room's active runways: this handler is not given the dispatch context.
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        SyncControllerAssignedAltitude(aircraft, clearanceType, assignedAltitude);

        return BuildDepartureMessage(clearanceType, runway.Designator, departure, assignedAltitude) with
        {
            Advisory = ProcedureSourceSidAdvisory(clearanceType, routeResult, aircraft),
        };
    }

    internal static CommandResult StoreDepartureClearanceDuringTaxi(
        AircraftState aircraft,
        ClearanceType clearanceType,
        DepartureInstruction departure,
        int? assignedAltitude
    )
    {
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        // Find the destination runway hold-short. Prefer DestinationRunway (authoritative);
        // fall back to the last ExplicitHoldShort only when no destination exists
        // (handles "TAXI C E" + "HS 28R" + "CTO" without a RWY directive).
        HoldShortPoint? depHoldShort = null;
        HoldShortPoint? explicitFallback = null;
        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            if (hs.Reason is HoldShortReason.DestinationRunway)
            {
                depHoldShort = hs;
            }
            else if (hs.Reason is HoldShortReason.ExplicitHoldShort && !HoldShortTarget.IsSpotTargetName(hs.TargetName))
            {
                explicitFallback = hs;
            }
        }

        depHoldShort ??= explicitFallback;

        if (depHoldShort?.TargetName is null)
        {
            return new CommandResult(false, "No departure runway hold-short in taxi route");
        }

        RunwayInfo? runway = CommandDispatcher.ResolveRunway(aircraft, depHoldShort.TargetName);
        if (runway is null)
        {
            return new CommandResult(false, $"Cannot resolve runway {RunwayIdentifier.ToDisplayDesignator(depHoldShort.TargetName ?? "")}");
        }

        // Consistency: if RWY assigned a runway that doesn't share a physical runway
        // with the route's destination, refuse to silently overwrite. The controller
        // gave conflicting instructions; one of them has to be revoked.
        if (aircraft.Phases?.AssignedRunway is { } assigned && !runway.Id.Overlaps(assigned.Id))
        {
            return new CommandResult(
                false,
                $"Taxi route ends at {RunwayIdentifier.ToDisplayDesignator(runway.Designator)} but {RunwayIdentifier.ToDisplayDesignator(assigned.Designator)} is the assigned runway — re-taxi or re-assign with RWY"
            );
        }

        // Same physical runway with a different end (e.g. assigned 28R, hold-short
        // named "10L/28R"): preserve the controller's explicit end rather than
        // flipping back to the joint name.
        if (aircraft.Phases?.AssignedRunway is { } existing && runway.Id.Overlaps(existing.Id))
        {
            runway = existing;
        }

        // Pre-clear the destination hold-short and any runway crossings for the same runway.
        // The route may cross the departure runway before reaching the destination hold-short
        // (e.g., taxiway B at OAK crosses 28L/10R before reaching the 28L threshold).
        // Track which IDs we flipped so CTOC can revert exactly those, leaving crossings
        // independently cleared by LV/RC/CROSS untouched.
        var preClearedIds = new List<int>();
        if (!depHoldShort.IsCleared)
        {
            depHoldShort.IsCleared = true;
            preClearedIds.Add(depHoldShort.NodeId);
        }
        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            if (
                hs.Reason == HoldShortReason.RunwayCrossing
                && hs.TargetName is not null
                && RunwayIdentifier.Parse(hs.TargetName).Contains(runway.Designator)
                && !hs.IsCleared
            )
            {
                hs.IsCleared = true;
                preClearedIds.Add(hs.NodeId);
            }
        }

        // Assign runway before SID resolution — TryResolveSidFromCifp selects the
        // runway transition (e.g. RW28B) and reads the VM heading from that leg.
        aircraft.Phases!.AssignedRunway = runway;
        aircraft.Procedure.DepartureRunway = runway.Designator;

        // Pre-resolve navigation targets for route-based departures
        DepartureRouteResult? routeResult = ResolveDepartureRoute(departure, aircraft);

        // Pre-resolve pattern runway for cross-runway closed traffic
        RunwayInfo? patternRunway = null;
        if (departure is ClosedTrafficDeparture ctDep)
        {
            patternRunway = ResolvePatternRunway(ctDep, aircraft);
        }

        // Store departure clearance for TaxiingPhase to consume
        aircraft.Phases.DepartureClearance = new DepartureClearanceInfo
        {
            Type = clearanceType,
            Departure = departure,
            AssignedAltitude = assignedAltitude,
            DepartureRoute = routeResult?.Targets,
            DepartureProcedureLegs = routeResult?.ProcedureLegs,
            DepartureSidId = routeResult?.SidId,
            SidDepartureHeadingMagnetic = routeResult?.DepartureHeadingMagnetic,
            RvSidDeferHeadingUntilMinAlt = routeResult?.RvSidDeferHeadingUntilMinAlt ?? false,
            RvSidHoldRunwayHeading = routeResult?.RvSidHoldRunwayHeading ?? false,
            PatternRunway = patternRunway,
            PreClearedHoldShortNodeIds = preClearedIds.Count > 0 ? preClearedIds : null,
        };
        SyncControllerAssignedAltitude(aircraft, clearanceType, assignedAltitude);

        return BuildDepartureMessage(clearanceType, runway.Designator, departure, assignedAltitude) with
        {
            Advisory = ProcedureSourceSidAdvisory(clearanceType, routeResult, aircraft),
        };
    }

    internal static DepartureRouteResult? InsertTowerPhasesAfterCurrent(
        AircraftState aircraft,
        ClearanceType clearanceType,
        DepartureInstruction departure,
        int? assignedAltitude,
        RunwayInfo runway,
        ILogger logger,
        ActiveRunways activeRunways
    )
    {
        DepartureRouteResult? routeResult = ResolveDepartureRoute(departure, aircraft);

        var lineup = new LineUpPhase();
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        bool isHeli = cat == AircraftCategory.Helicopter;
        Phase takeoffPhase = isHeli ? new HelicopterTakeoffPhase() : new TakeoffPhase();

        // Rolling takeoff: if CTO is already in hand at insertion time, omit
        // LinedUpAndWaitingPhase. LineUpPhase detects this from the phase
        // list shape (next phase is TakeoffPhase, not LUAW) and skips its
        // brake-to-stop so the aircraft rolls straight into the takeoff roll.
        //
        // Super and Heavy aircraft are prohibited from rolling takeoffs per
        // 7110.65 §3-9-5.3 (wake-turbulence separation timers key off the
        // standing-start takeoff power application). Fall back to the
        // traditional stop-then-go sequence with a pre-satisfied LUAW.
        bool rolling = clearanceType == ClearanceType.ClearedForTakeoff && LineUpPhase.IsAircraftEligibleForRollingTakeoff(aircraft.AircraftType);

        // For circuit departures (closed traffic / pattern exit), skip InitialClimbPhase — the
        // UpwindPhase handles the climb-out. The crosswind turn is position-based (departure end),
        // not altitude-based.
        bool isCircuit = IsCircuitDeparture(departure);
        Phase[] towerPhases;
        if (isCircuit)
        {
            towerPhases = rolling ? [lineup, takeoffPhase] : [lineup, new LinedUpAndWaitingPhase(), takeoffPhase];
        }
        else
        {
            var climb = new InitialClimbPhase
            {
                Departure = departure,
                AssignedAltitude = assignedAltitude,
                DepartureRoute = routeResult?.Targets ?? [],
                DepartureSidId = routeResult?.SidId,
                SidDepartureHeadingMagnetic = routeResult?.DepartureHeadingMagnetic,
                RvSidDeferHeadingUntilMinAlt = routeResult?.RvSidDeferHeadingUntilMinAlt ?? false,
                RvSidHoldRunwayHeading = routeResult?.RvSidHoldRunwayHeading ?? false,
                DepartureProcedureLegs = routeResult?.ProcedureLegs,
                IsVfr = aircraft.FlightPlan.IsVfr,
                CruiseAltitude = aircraft.FlightPlan.Altitude.CruiseFeet ?? 0,
            };
            towerPhases = rolling ? [lineup, takeoffPhase, climb] : [lineup, new LinedUpAndWaitingPhase(), takeoffPhase, climb];
        }

        // Replace (not insert) — remaining taxi phases (CrossingRunwayPhase, etc.)
        // must not execute after the aircraft is airborne.
        aircraft.Phases!.ReplaceUpcoming(towerPhases);

        if (clearanceType == ClearanceType.ClearedForTakeoff)
        {
            // Rolling mode omits LUAW entirely. For the non-rolling CTO
            // path (heavy/super aircraft per 7110.65 §3-9-5.3), LUAW is
            // still in the list but must be pre-satisfied so the aircraft
            // doesn't hang waiting for a clearance that was already given.
            if (!rolling)
            {
                LinedUpAndWaitingPhase? luawPhase = towerPhases.OfType<LinedUpAndWaitingPhase>().FirstOrDefault();
                if (luawPhase is not null)
                {
                    luawPhase.SatisfyClearance(ClearanceType.ClearedForTakeoff);
                    luawPhase.Departure = departure;
                    luawPhase.AssignedAltitude = assignedAltitude;
                }
            }

            if (takeoffPhase is TakeoffPhase fw)
            {
                fw.SetAssignedDeparture(departure);
            }
            else if (takeoffPhase is HelicopterTakeoffPhase hp)
            {
                hp.SetAssignedDeparture(departure);
            }

            if (IsCircuitDeparture(departure))
            {
                ApplyCircuitDeparture(
                    departure,
                    aircraft,
                    aircraft.Phases!,
                    runway,
                    assignedAltitude,
                    removeInitialClimb: false,
                    activeRunways: activeRunways
                );
            }
        }

        logger.LogDebug(
            "[Departure] {Callsign}: tower phases inserted ({Clearance}, rolling={Rolling}), runway {Rwy}",
            aircraft.Callsign,
            clearanceType,
            rolling,
            runway.Designator
        );

        return routeResult;
    }

    internal static CommandResult SatisfyUpcomingTakeoffClearance(
        AircraftState aircraft,
        DepartureInstruction departure,
        int? assignedAltitude,
        ILogger logger,
        ActiveRunways activeRunways
    )
    {
        PhaseList? phases = aircraft.Phases;
        if (phases is null)
        {
            return new CommandResult(false, "No active phase sequence");
        }

        // Find the pending LinedUpAndWaitingPhase, TakeoffPhase/HelicopterTakeoffPhase, InitialClimbPhase
        LinedUpAndWaitingPhase? luaw = null;
        Phase? takeoff = null;
        InitialClimbPhase? climb = null;
        foreach (Phase p in phases.Phases)
        {
            if (p.Status != PhaseStatus.Pending)
            {
                continue;
            }

            if (luaw is null && p is LinedUpAndWaitingPhase l)
            {
                luaw = l;
            }
            else if (takeoff is null && p is TakeoffPhase or HelicopterTakeoffPhase)
            {
                takeoff = p;
            }
            else if (climb is null && p is InitialClimbPhase c)
            {
                climb = c;
            }
        }

        if (luaw is null)
        {
            return new CommandResult(false, "Aircraft is not lined up and waiting");
        }

        DepartureRouteResult? routeResult = ResolveDepartureRoute(departure, aircraft);

        luaw.SatisfyClearance(ClearanceType.ClearedForTakeoff);
        luaw.Departure = departure;
        luaw.AssignedAltitude = assignedAltitude;
        if (takeoff is TakeoffPhase fwTakeoff)
        {
            fwTakeoff.SetAssignedDeparture(departure);
        }
        else if (takeoff is HelicopterTakeoffPhase heliTakeoff)
        {
            heliTakeoff.SetAssignedDeparture(departure);
        }

        if (climb is not null)
        {
            SetInitialClimbProperties(climb, departure, assignedAltitude, routeResult, aircraft);
        }

        // Capture takeoff runway before the circuit apply overwrites AssignedRunway.
        string takeoffDesignator = phases.AssignedRunway?.Designator ?? "unknown";
        if (IsCircuitDeparture(departure) && phases.AssignedRunway is { } rwy)
        {
            ApplyCircuitDeparture(departure, aircraft, phases, rwy, assignedAltitude, removeInitialClimb: true, activeRunways: activeRunways);
        }

        // Rolling-takeoff upgrade: if an active LineUpPhase exists and the
        // aircraft is still above the upgrade speed threshold, flip it into
        // rolling mode so the aircraft doesn't bother finishing the brake
        // curve. The LUAW pre-satisfy above stays as the fallback — if the
        // upgrade is rejected (state == Setup/Stop/Faulted, IAS too low, or
        // Super/Heavy) the original stop-then-go path runs via the
        // pre-satisfied LUAW.
        foreach (Phase p in phases.Phases)
        {
            if (p is LineUpPhase active && p.Status == PhaseStatus.Active)
            {
                // Re-clearing for takeoff lifts a prior CTOC hold-position so the
                // aircraft resumes the line-up and departs.
                active.HoldPosition = false;
                // No route to the room's active runways: this handler is not given the dispatch context.
                PhaseContext upgradeCtx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);
                active.TryUpgradeToRolling(upgradeCtx);
                break;
            }
        }

        logger.LogDebug("[Departure] {Callsign}: CTO satisfied on upcoming LUAW phase", aircraft.Callsign);

        SyncControllerAssignedAltitude(aircraft, ClearanceType.ClearedForTakeoff, assignedAltitude);

        return BuildDepartureMessage(ClearanceType.ClearedForTakeoff, takeoffDesignator, departure, assignedAltitude) with
        {
            Advisory = ProcedureSourceSidAdvisory(ClearanceType.ClearedForTakeoff, routeResult, aircraft),
        };
    }

    /// <summary>
    /// Instructor-facing advisory when the filed SID's coded legs did not come from the current FAA CIFP —
    /// recovered from a cached prior AIRAC cycle, or supplied by an ARTCC CIFP fragment (the procedure may be
    /// retired, or still charted but missing from the CIFP dataset). Null when the SID came from the current
    /// cycle or the clearance is not a takeoff clearance.
    /// </summary>
    internal static string? ProcedureSourceSidAdvisory(ClearanceType clearanceType, DepartureRouteResult? routeResult, AircraftState aircraft)
    {
        if (clearanceType != ClearanceType.ClearedForTakeoff || routeResult?.Source is not { } source)
        {
            return null;
        }

        string sidName = (aircraft.FlightPlan.Route ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "SID";
        return CommandDispatcher.ProcedureSourceAdvisory("SID", sidName, source);
    }

    internal static CommandResult BuildDepartureMessage(
        ClearanceType clearanceType,
        string runwayId,
        DepartureInstruction departure,
        int? assignedAltitude
    )
    {
        string displayRunway = RunwayIdentifier.ToDisplayDesignator(runwayId);
        if (clearanceType == ClearanceType.ClearedForTakeoff)
        {
            string msg = $"Cleared for takeoff runway {displayRunway}";
            msg += FormatDepartureInstructionSuffix(departure);
            if (assignedAltitude is not null)
            {
                msg += $", climb and maintain {assignedAltitude:N0}";
            }
            return CommandDispatcher.Ok(msg);
        }

        return CommandDispatcher.Ok($"Line up and wait runway {displayRunway}");
    }

    internal static string FormatDepartureInstructionSuffix(DepartureInstruction departure)
    {
        return departure switch
        {
            DefaultDeparture => "",
            PresentPositionHoverDeparture hover => $", hover and hold at {hover.HoverAltitudeAglFt:N0} feet",
            RunwayHeadingDeparture => ", fly runway heading",
            PatternExitDeparture { ExitLeg: PatternEntryLeg.Crosswind, Direction: PatternDirection.Right } => ", right crosswind departure",
            PatternExitDeparture { ExitLeg: PatternEntryLeg.Crosswind, Direction: PatternDirection.Left } => ", left crosswind departure",
            PatternExitDeparture { ExitLeg: PatternEntryLeg.Downwind, Direction: PatternDirection.Right } => ", right downwind departure",
            PatternExitDeparture { ExitLeg: PatternEntryLeg.Downwind, Direction: PatternDirection.Left } => ", left downwind departure",
            RelativeTurnDeparture rel => $", turn {(rel.Direction == TurnDirection.Right ? "right" : "left")} {rel.Degrees} degrees",
            FlyHeadingDeparture fh when fh.Direction is TurnDirection.Right => $", turn right heading {fh.MagneticHeading.ToDisplayInt():000}",
            FlyHeadingDeparture fh when fh.Direction is TurnDirection.Left => $", turn left heading {fh.MagneticHeading.ToDisplayInt():000}",
            FlyHeadingDeparture fh => $", fly heading {fh.MagneticHeading.ToDisplayInt():000}",
            OnCourseDeparture => ", on course",
            DirectFixDeparture { Direction: TurnDirection.Left } dfd => $", turn left direct {dfd.FixName}",
            DirectFixDeparture { Direction: TurnDirection.Right } dfd => $", turn right direct {dfd.FixName}",
            DirectFixDeparture dfd => $", direct {dfd.FixName}",
            ClosedTrafficDeparture ct when ct.RunwayId is not null =>
                $", make {(ct.Direction == PatternDirection.Left ? "left" : "right")} traffic runway {RunwayIdentifier.ToDisplayDesignator(ct.RunwayId)}",
            ClosedTrafficDeparture ct => $", make {(ct.Direction == PatternDirection.Left ? "left" : "right")} traffic",
            _ => "",
        };
    }

    /// <summary>
    /// Resolves the pattern runway for cross-runway closed traffic departures.
    /// Returns null if no cross-runway is specified or it can't be resolved.
    /// </summary>
    internal static RunwayInfo? ResolvePatternRunway(ClosedTrafficDeparture ct, AircraftState aircraft)
    {
        if (ct.RunwayId is null)
        {
            return null;
        }

        return CommandDispatcher.ResolveRunway(aircraft, ct.RunwayId);
    }

    /// <summary>
    /// Applies closed traffic departure to an aircraft: sets traffic direction,
    /// pattern altitude override, resolves cross-runway, builds and appends
    /// the pattern circuit, and optionally removes InitialClimbPhase.
    /// </summary>
    internal static void ApplyClosedTraffic(
        ClosedTrafficDeparture ct,
        AircraftState aircraft,
        PhaseList phases,
        RunwayInfo fallbackRunway,
        bool removeInitialClimb,
        ActiveRunways activeRunways
    )
    {
        phases.TrafficDirection = ct.Direction;
        aircraft.Pattern.TrafficDirection = ct.Direction;
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);

        if (ct.PatternAltitude is not null)
        {
            aircraft.Pattern.SetAltitudeOverride(ct.PatternAltitude, phases, cat);
        }

        if (removeInitialClimb)
        {
            phases.Phases.RemoveAll(p => p is InitialClimbPhase { Status: PhaseStatus.Pending });
        }

        RunwayInfo patternRunway = ResolvePatternRunway(ct, aircraft) ?? fallbackRunway;
        IReadOnlyList<RunwayInfo> airportRunways = Data.NavigationDatabase.Instance.GetRunways(patternRunway.AirportId);
        GroundRunway? authoredPatternRunway = aircraft.Ground.Layout?.FindRunway(patternRunway.Designator);
        (double? sizeOv, double? altOv) = PatternGeometry.ResolveAuthoredOverrides(
            patternRunway,
            authoredPatternRunway,
            cat,
            aircraft.Pattern.SizeOverrideNm,
            aircraft.Pattern.AltitudeOverrideFt
        );
        // Cross-runway closed traffic (e.g. takeoff 33, make right traffic 28R): the
        // first circuit climbs out on the DEPARTURE runway then transitions to the PATTERN
        // runway's circuit — crossing midfield for a crossing pair, continuing the upwind
        // past both departure ends for a close parallel. Same-runway closed traffic flies a
        // normal upwind-entry circuit on the one runway.
        bool crossRunway = !string.Equals(patternRunway.Designator, fallbackRunway.Designator, StringComparison.OrdinalIgnoreCase);
        List<Phase> circuit = crossRunway
            ? PatternBuilder.BuildRunwayTransitionCircuit(
                fallbackRunway,
                patternRunway,
                cat,
                aircraft.AircraftType,
                aircraft.WindSpeedKts,
                ct.Direction,
                true,
                sizeOv,
                altOv,
                airportRunways,
                aircraft.Ground.Layout?.FindRunway(fallbackRunway.Designator),
                authoredPatternRunway,
                activeRunways,
                PatternCommanded.Of(aircraft)
            )
            : PatternBuilder.BuildCircuit(
                patternRunway,
                cat,
                aircraft.AircraftType,
                aircraft.WindSpeedKts,
                ct.Direction,
                PatternEntryLeg.Upwind,
                true,
                null,
                sizeOv,
                altOv,
                airportRunways,
                authoredPatternRunway,
                activeRunways,
                PatternCommanded.Of(aircraft)
            );

        phases.Phases.AddRange(circuit);
        aircraft.Pattern.NoteRunwayCrossing(circuit);
        phases.PatternRunway = patternRunway;
        // The pattern runway is re-armed here, so a transition an earlier option clearance armed, and the altitude it
        // deferred, are replaced.
        phases.TransitionPatternAltitude = null;
        // AssignedRunway carries the pattern runway (circuit/final/landing read it).
        // DepartureRunway carries the takeoff runway for lineup/takeoff when they differ.
        phases.AssignedRunway = patternRunway;
        phases.DepartureRunway = crossRunway ? fallbackRunway : null;
    }

    /// <summary>
    /// True for departures that build a pattern circuit (and so skip <see cref="InitialClimbPhase"/>):
    /// closed traffic (re-enters to land) and pattern-exit departures (flies the pattern then leaves).
    /// </summary>
    internal static bool IsCircuitDeparture(DepartureInstruction departure) => departure is ClosedTrafficDeparture or PatternExitDeparture;

    /// <summary>
    /// Applies a circuit-building departure: dispatches to <see cref="ApplyClosedTraffic"/> or
    /// <see cref="ApplyPatternExitDeparture"/>. No-op for any other departure type.
    /// </summary>
    internal static void ApplyCircuitDeparture(
        DepartureInstruction departure,
        AircraftState aircraft,
        PhaseList phases,
        RunwayInfo fallbackRunway,
        int? assignedAltitude,
        bool removeInitialClimb,
        ActiveRunways activeRunways
    )
    {
        switch (departure)
        {
            case ClosedTrafficDeparture ct:
                ApplyClosedTraffic(ct, aircraft, phases, fallbackRunway, removeInitialClimb, activeRunways: activeRunways);
                break;
            case PatternExitDeparture ped:
                ApplyPatternExitDeparture(ped, aircraft, phases, fallbackRunway, assignedAltitude, removeInitialClimb, activeRunways: activeRunways);
                break;
        }
    }

    /// <summary>
    /// Applies a pattern-exit departure (CTO MRC/MRD/MLC/MLD): sets the pattern traffic direction,
    /// optionally removes the pending <see cref="InitialClimbPhase"/>, and appends the truncated
    /// circuit (upwind [→ crosswind] → <see cref="PatternExitPhase"/>) that flies the pattern to the
    /// exit leg and then departs the area. Unlike closed traffic there is no base/final/landing and no
    /// cycle terminator, so the pattern auto-cycle never re-appends a circuit.
    /// </summary>
    internal static void ApplyPatternExitDeparture(
        PatternExitDeparture ped,
        AircraftState aircraft,
        PhaseList phases,
        RunwayInfo runway,
        int? assignedAltitude,
        bool removeInitialClimb,
        ActiveRunways activeRunways
    )
    {
        phases.TrafficDirection = ped.Direction;
        aircraft.Pattern.TrafficDirection = ped.Direction;

        if (removeInitialClimb)
        {
            phases.Phases.RemoveAll(p => p is InitialClimbPhase { Status: PhaseStatus.Pending });
        }

        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        IReadOnlyList<RunwayInfo> airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
        GroundRunway? authoredRunway = aircraft.Ground.Layout?.FindRunway(runway.Designator);
        (double? sizeOv, double? altOv) = PatternGeometry.ResolveAuthoredOverrides(
            runway,
            authoredRunway,
            cat,
            aircraft.Pattern.SizeOverrideNm,
            aircraft.Pattern.AltitudeOverrideFt
        );

        List<Phase> circuit = PatternBuilder.BuildPatternExitCircuit(
            runway,
            cat,
            aircraft.AircraftType,
            aircraft.WindSpeedKts,
            ped.Direction,
            ped.ExitLeg,
            assignedAltitude,
            aircraft.FlightPlan.Altitude.CruiseFeet ?? 0,
            sizeOv,
            altOv,
            airportRunways,
            authoredRunway,
            activeRunways,
            // A departure leaving the pattern flies normal speeds: only closed traffic compresses.
            PatternCommanded.Of(aircraft) with
            {
                NeverCompresses = true,
            }
        );

        phases.Phases.AddRange(circuit);
        phases.AssignedRunway = runway;
    }

    /// <summary>
    /// Pre-resolves navigation targets for route-based departure instructions.
    /// Keeps NavigationDatabase out of the phase layer.
    /// </summary>
    internal static DepartureRouteResult? ResolveDepartureRoute(DepartureInstruction departure, AircraftState aircraft)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;

        switch (departure)
        {
            case DirectFixDeparture dfd:
                return new DepartureRouteResult([new NavigationTarget { Name = dfd.FixName, Position = new LatLon(dfd.Lat, dfd.Lon) }], null);

            case OnCourseDeparture when aircraft.FlightPlan.Destination is not null:
            {
                (double Lat, double Lon)? pos = navDb.GetAirportPosition(aircraft.FlightPlan.Destination);
                if (pos is null)
                {
                    return null;
                }
                return new DepartureRouteResult(
                    [new NavigationTarget { Name = aircraft.FlightPlan.Destination, Position = new LatLon(pos.Value.Lat, pos.Value.Lon) }],
                    null
                );
            }

            case DefaultDeparture when !aircraft.FlightPlan.IsVfr && aircraft.FlightPlan.Route is not null:
            {
                // Try CIFP SID first for constrained navigation targets
                DepartureRouteResult? cifpResult = TryResolveSidFromCifp(aircraft);
                if (cifpResult is not null)
                {
                    return cifpResult;
                }

                // Fallback to NavData body-fix expansion (lateral path only, no constraints)
                List<NavigationTarget> targets = BuildFallbackNavTargets(aircraft, navDb);

                // CIFP couldn't resolve the SID (e.g. the procedure was retired from the current FAA
                // cycle, so the published vectors heading is unavailable). If the vNAS nav data still
                // recognizes the first route token as a radar-vectors SID — one with no published lateral
                // path — degrade to a radar-vectors departure: hold runway heading and await vectors
                // instead of turning direct to the first enroute fix. The expanded fixes are retained as
                // the post-vectors route, loaded after comms handoff like any other RV-SID departure.
                string? firstToken = aircraft.FlightPlan.Route.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (firstToken is not null && navDb.IsRadarVectorsSidWithoutLateralPath(firstToken, aircraft.FlightPlan.Departure))
                {
                    return new DepartureRouteResult(targets, SidId: null, DepartureHeadingMagnetic: null, RvSidHoldRunwayHeading: true);
                }

                return targets.Count > 0 ? new DepartureRouteResult(targets, null) : null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Expands the filed route via NavData body-fix expansion (lateral path only, no constraints) and
    /// resolves each fix to a navigation target, stripping leading fixes within 1 nm of the departure
    /// airport. Used as the fallback when CIFP can't supply constrained SID legs.
    /// </summary>
    private static List<NavigationTarget> BuildFallbackNavTargets(AircraftState aircraft, NavigationDatabase navDb)
    {
        IReadOnlyList<string> expanded = navDb.ExpandRouteForNavigation(aircraft.FlightPlan.Route, aircraft.FlightPlan.Departure);
        var targets = new List<NavigationTarget>();

        (double Lat, double Lon)? airportPos = aircraft.FlightPlan.Departure is not null
            ? navDb.GetAirportPosition(aircraft.FlightPlan.Departure)
            : null;

        foreach (string name in expanded)
        {
            (double Lat, double Lon)? pos = navDb.ResolveFixOrFrd(name);
            if (pos is null)
            {
                continue;
            }

            targets.Add(new NavigationTarget { Name = name, Position = new LatLon(pos.Value.Lat, pos.Value.Lon) });
        }

        // Safety net: strip leading targets within 1nm of departure
        if (airportPos is not null)
        {
            while (targets.Count > 0)
            {
                double dist = GeoMath.DistanceNm(new LatLon(airportPos.Value.Lat, airportPos.Value.Lon), targets[0].Position);
                if (dist > 1.0)
                {
                    break;
                }

                targets.RemoveAt(0);
            }
        }

        return targets;
    }

    /// <summary>
    /// Attempts to resolve a SID from CIFP data. Extracts SID name from the first
    /// route token, selects the runway transition matching the assigned runway,
    /// and builds an ordered leg sequence with altitude/speed constraints.
    /// Appends remaining enroute fixes from the filed route after the SID.
    /// Returns null if CIFP data is unavailable or SID cannot be resolved.
    /// </summary>
    internal static DepartureRouteResult? TryResolveSidFromCifp(AircraftState aircraft)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        if (aircraft.FlightPlan.Route is null || aircraft.FlightPlan.Departure is null)
        {
            return null;
        }

        string[] routeTokens = aircraft.FlightPlan.Route.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (routeTokens.Length == 0)
        {
            return null;
        }

        // First route token is the SID name
        string sidName = routeTokens[0];
        CifpSidProcedure? sid = navDb.GetSid(aircraft.FlightPlan.Departure, sidName, out ProcedureSource? procedureSource);
        if (sid is null)
        {
            return null;
        }

        // Build ordered leg sequence: runway transition → common → enroute transition
        var orderedLegs = new List<CifpLeg>();

        // Select runway transition matching assigned runway ("RW" + designator)
        if (aircraft.Phases?.AssignedRunway is { } rwy)
        {
            string rwKey = "RW" + rwy.Designator;
            if (!sid.RunwayTransitions.TryGetValue(rwKey, out CifpTransition? rwTransition))
            {
                // CIFP "B" suffix means both L/R share the same transition (e.g. "RW01B")
                string bothKey = "RW" + rwy.Designator.TrimEnd('L', 'R', 'C') + "B";
                sid.RunwayTransitions.TryGetValue(bothKey, out rwTransition);
            }

            if (rwTransition is not null)
            {
                orderedLegs.AddRange(rwTransition.Legs);
            }
        }

        orderedLegs.AddRange(sid.CommonLegs);

        // Locate the enroute-transition token. Filed routes commonly carry the departure's co-located
        // navaid as a redundant reference between the SID and its transition (e.g. "HUSSH2 OAK SYRAH" —
        // OAK is KOAK's on-field VOR, SYRAH names the transition). Skip such co-located tokens so the real
        // transition still matches instead of the navaid being appended literally as a turn-back over the
        // field. The skip is used ONLY to find a transition: when none matches, the tokens are left
        // untouched so a filed departure navaid can still anchor a following airway (e.g. "NIMI5 OAK V6 SAC"
        // needs OAK as V6's entry fix).
        int transitionTokenIdx = -1;
        for (int i = 1; i < routeTokens.Length; i++)
        {
            string token = routeTokens[i].ToUpperInvariant();
            if (sid.EnrouteTransitions.ContainsKey(token))
            {
                transitionTokenIdx = i;
                break;
            }

            if (!IsFixColocatedWithDeparture(token, aircraft.FlightPlan.Departure, navDb))
            {
                break;
            }
        }

        bool hasEnrouteTransition = transitionTokenIdx >= 0;
        if (hasEnrouteTransition)
        {
            orderedLegs.AddRange(sid.EnrouteTransitions[routeTokens[transitionTokenIdx].ToUpperInvariant()].Legs);
        }

        // Post-SID enroute append begins after the transition token (skipping the redundant co-located
        // navaid and the transition name), or at the first post-SID token when no transition matched.
        int enrouteStartIdx = hasEnrouteTransition ? transitionTokenIdx + 1 : 1;

        if (orderedLegs.Count == 0)
        {
            return null;
        }

        // Detect radar vectors SIDs (core procedure ends with VM/VA/VI).
        // These have no published lateral path — controller vectors the aircraft.
        IReadOnlyList<CifpLeg> rwLegs = aircraft.Phases?.AssignedRunway is { } rwyInfo
            ? GetRunwayTransitionLegs(sid, rwyInfo.Designator)
            : (IReadOnlyList<CifpLeg>)[];
        if (IsRadarVectorsSid(rwLegs, sid.CommonLegs))
        {
            double? heading = ExtractRadarVectorsHeading(orderedLegs);
            bool deferHeading = RvSidTransitionDefersHeadingAssignment(rwLegs);
            var rvTargets = new List<NavigationTarget>();

            if (hasEnrouteTransition)
            {
                // Enroute transition provides a published path from the vectors segment.
                // Resolve only the enroute transition legs (skip the core RV legs).
                IReadOnlyList<CifpLeg> enLegs = sid.EnrouteTransitions[routeTokens[transitionTokenIdx].ToUpperInvariant()].Legs;
                rvTargets = ResolveLegsToTargets(enLegs);
            }

            AppendPostSidEnrouteFixes(
                rvTargets,
                routeTokens,
                enrouteStartIdx,
                rvTargets.Count > 0 ? rvTargets[^1].Name : null,
                aircraft.FlightPlan.Departure
            );
            StripNearDepartureTargets(rvTargets, aircraft.FlightPlan.Departure);
            return new DepartureRouteResult(rvTargets, null, heading, deferHeading, Source: procedureSource);
        }

        // Convert SID legs to NavigationTargets with constraints
        List<NavigationTarget> targets = ResolveLegsToTargets(orderedLegs);
        if (targets.Count == 0)
        {
            return null;
        }

        AppendPostSidEnrouteFixes(targets, routeTokens, enrouteStartIdx, targets[^1].Name, aircraft.FlightPlan.Departure);
        StripNearDepartureTargets(targets, aircraft.FlightPlan.Departure);

        // Typed legs preserve the VA/VI/VM/CA heading legs and course-tracked CF that the flat
        // resolver drops. Non-null only when the SID actually has coded heading/intercept legs;
        // plain TF/CF SIDs keep the flat direct-to-fix path unchanged.
        List<ProcedureLeg>? procedureLegs = ProcedureLegResolver.ExtractActiveDepartureLegs(ProcedureLegResolver.Resolve(orderedLegs));

        return targets.Count > 0 ? new DepartureRouteResult(targets, sid.ProcedureId, ProcedureLegs: procedureLegs, Source: procedureSource) : null;
    }

    /// <summary>
    /// Returns true when the core SID procedure (common legs, or runway transition if no common)
    /// terminates with a radar vectors leg (VM, VA, or VI). These SIDs have no published lateral
    /// path — the controller provides heading vectors after departure.
    /// </summary>
    internal static bool IsRadarVectorsSid(IReadOnlyList<CifpLeg> runwayTransitionLegs, IReadOnlyList<CifpLeg> commonLegs)
    {
        // Check the last leg of the core procedure (common legs take precedence)
        CifpLeg? lastCoreLeg =
            commonLegs.Count > 0 ? commonLegs[^1]
            : runwayTransitionLegs.Count > 0 ? runwayTransitionLegs[^1]
            : null;

        if (lastCoreLeg is null)
        {
            return false;
        }

        return lastCoreLeg.PathTerminator is CifpPathTerminator.VM or CifpPathTerminator.VA or CifpPathTerminator.VI;
    }

    /// <summary>
    /// True when the runway transition climbs on the runway course (CA / track-to-altitude)
    /// before the terminating VM/VA/VI vectors leg — IFR pilots fly runway heading until
    /// ≥400 ft AGL, then the published vectors heading.
    /// </summary>
    internal static bool RvSidTransitionDefersHeadingAssignment(IReadOnlyList<CifpLeg> runwayTransitionLegs)
    {
        if (runwayTransitionLegs.Count < 2)
        {
            return false;
        }

        int vectorsLegIndex = -1;
        for (int i = runwayTransitionLegs.Count - 1; i >= 0; i--)
        {
            if (runwayTransitionLegs[i].PathTerminator is CifpPathTerminator.VM or CifpPathTerminator.VA or CifpPathTerminator.VI)
            {
                vectorsLegIndex = i;
                break;
            }
        }

        if (vectorsLegIndex <= 0)
        {
            return false;
        }

        for (int i = 0; i < vectorsLegIndex; i++)
        {
            CifpLeg leg = runwayTransitionLegs[i];
            if (
                leg.PathTerminator
                is CifpPathTerminator.CA
                    or CifpPathTerminator.FA
                    or CifpPathTerminator.VA
                    or CifpPathTerminator.TF
                    or CifpPathTerminator.CF
                    or CifpPathTerminator.CD
                    or CifpPathTerminator.VD
                    or CifpPathTerminator.FD
                    or CifpPathTerminator.FC
                    or CifpPathTerminator.CR
                    or CifpPathTerminator.VR
                    or CifpPathTerminator.Other
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts the magnetic heading from the last VM/VA/VI leg in the sequence.
    /// Returns the OutboundCourse of that leg (the assigned heading for radar vectors).
    /// </summary>
    private static double? ExtractRadarVectorsHeading(IReadOnlyList<CifpLeg> legs)
    {
        for (int i = legs.Count - 1; i >= 0; i--)
        {
            if (legs[i].PathTerminator is CifpPathTerminator.VM or CifpPathTerminator.VA or CifpPathTerminator.VI)
            {
                return legs[i].OutboundCourse;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets runway transition legs for the given designator, handling "B" suffix fallback.
    /// </summary>
    private static IReadOnlyList<CifpLeg> GetRunwayTransitionLegs(CifpSidProcedure sid, string designator)
    {
        string rwKey = "RW" + designator;
        if (sid.RunwayTransitions.TryGetValue(rwKey, out CifpTransition? rwTransition))
        {
            return rwTransition.Legs;
        }

        string bothKey = "RW" + designator.TrimEnd('L', 'R', 'C') + "B";
        if (sid.RunwayTransitions.TryGetValue(bothKey, out rwTransition))
        {
            return rwTransition.Legs;
        }

        return [];
    }

    /// <summary>
    /// Appends remaining enroute fixes from the filed route (tokens from <paramref name="startIdx"/> on)
    /// without constraints. Delegates to <see cref="RouteExpander.Expand"/> to handle airway expansion,
    /// dot-notation, and deduplication. Prepends the last SID fix as an anchor so airways have a from-fix.
    /// The caller computes <paramref name="startIdx"/> so it points past the SID token, any redundant
    /// co-located departure navaid, and the enroute-transition token (all already resolved into targets).
    /// A leading fix co-located with <paramref name="departure"/> is still dropped from the appended output
    /// (it anchors airway expansion but is never flown as a waypoint — routing to the departure field's own
    /// navaid right after the SID body is a turn-back).
    /// </summary>
    private static void AppendPostSidEnrouteFixes(
        List<NavigationTarget> targets,
        string[] routeTokens,
        int startIdx,
        string? lastSidFix,
        string? departure
    )
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;

        if (startIdx >= routeTokens.Length)
        {
            return;
        }

        // Prepend lastSidFix (or last target) as anchor so RouteExpander has a from-fix for airways
        string? anchor = lastSidFix ?? (targets.Count > 0 ? targets[^1].Name : null);
        string[] postSidTokens = routeTokens[startIdx..];
        string postSidRoute = anchor is not null ? anchor + " " + string.Join(' ', postSidTokens) : string.Join(' ', postSidTokens);

        // Flight-plan context: suppress the "emit all transitions on mismatch" fallback so an RV-SID's
        // adapted-route hints can't fabricate a turn-back through every synthesized transition fix.
        List<string> expandedFixes = RouteExpander.Expand(postSidRoute, navDb, includeAllTransitionsOnMismatch: false);

        // Skip expanded fixes up to and including the anchor (already covered by SID targets)
        int fixStart = 0;
        if (anchor is not null)
        {
            for (int i = 0; i < expandedFixes.Count; i++)
            {
                if (string.Equals(expandedFixes[i], anchor, StringComparison.OrdinalIgnoreCase))
                {
                    fixStart = i + 1;
                    break;
                }
            }
        }

        // Drop a leading co-located departure navaid (the field's own VOR, listed as a routing hint or used
        // to anchor a following airway). It served its purpose during RouteExpander airway expansion above,
        // but flying to it right after the SID body reverses the aircraft back over the departure field.
        bool atLeadingBoundary = true;
        for (int i = fixStart; i < expandedFixes.Count; i++)
        {
            (double Lat, double Lon)? pos = navDb.ResolveFixOrFrd(expandedFixes[i]);
            if (pos is null)
            {
                continue;
            }

            if (atLeadingBoundary && IsFixColocatedWithDeparture(expandedFixes[i], departure, navDb))
            {
                continue;
            }

            atLeadingBoundary = false;
            targets.Add(new NavigationTarget { Name = expandedFixes[i].ToUpperInvariant(), Position = new LatLon(pos.Value.Lat, pos.Value.Lon) });
        }
    }

    /// <summary>
    /// Strips leading targets within 1nm of the departure airport.
    /// </summary>
    private static void StripNearDepartureTargets(List<NavigationTarget> targets, string? departure)
    {
        if (departure is null)
        {
            return;
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        (double Lat, double Lon)? airportPos = navDb.GetAirportPosition(departure);
        if (airportPos is not null)
        {
            while (targets.Count > 0)
            {
                double dist = GeoMath.DistanceNm(new LatLon(airportPos.Value.Lat, airportPos.Value.Lon), targets[0].Position);
                if (dist > 1.0)
                {
                    break;
                }
                targets.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// True when the named fix resolves to a position within 1 nm of the departure airport — i.e. the
    /// airport's co-located reference navaid (e.g. OAK VOR on KOAK). Filed routes often list this navaid
    /// redundantly between the SID and its transition; it must never be flown as a routing point.
    /// </summary>
    private static bool IsFixColocatedWithDeparture(string fixName, string? departure, NavigationDatabase navDb)
    {
        if (departure is null)
        {
            return false;
        }

        (double Lat, double Lon)? airportPos = navDb.GetAirportPosition(departure);
        (double Lat, double Lon)? fixPos = navDb.GetFixPosition(fixName);
        if (airportPos is null || fixPos is null)
        {
            return false;
        }

        return GeoMath.DistanceNm(new LatLon(airportPos.Value.Lat, airportPos.Value.Lon), new LatLon(fixPos.Value.Lat, fixPos.Value.Lon)) <= 1.0;
    }

    /// <summary>
    /// Converts CIFP legs to NavigationTargets with altitude/speed constraints.
    /// Resolves fix positions, skips unknown fixes, carries restrictions.
    /// RF/AF legs are expanded into intermediate arc waypoints.
    /// PI (procedure turn) legs are skipped in SID/STAR context.
    /// </summary>
    internal static List<NavigationTarget> ResolveLegsToTargets(IReadOnlyList<CifpLeg> legs)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        var targets = new List<NavigationTarget>();
        (double Lat, double Lon)? previousFixPos = null;

        foreach (CifpLeg leg in legs)
        {
            // Skip procedure turn legs in SID/STAR context (approach-only, handled by hold-in-lieu)
            if (leg.PathTerminator == CifpPathTerminator.PI)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(leg.FixIdentifier))
            {
                continue;
            }

            (double Lat, double Lon)? pos = leg.ResolveFixPosition(navDb);
            if (pos is null)
            {
                continue;
            }

            // Deduplicate adjacent identical fix names. A common pattern is a TF/CF to a fix
            // followed by an FM "fly course from that fix, expect vectors" leg on the same fix
            // (ends most US STARs) — carry the FM outbound course onto the existing target so the
            // collapse doesn't discard it.
            if (targets.Count > 0 && string.Equals(targets[^1].Name, leg.FixIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                if (leg.PathTerminator == CifpPathTerminator.FM && leg.OutboundCourse is { } fmCourse)
                {
                    targets[^1].TerminalCourseMagnetic = fmCourse;
                }
                previousFixPos = (pos.Value.Lat, pos.Value.Lon);
                continue;
            }

            // RF leg: expand arc from previous fix to terminator fix
            if (
                leg.PathTerminator == CifpPathTerminator.RF
                && leg.ArcCenterLat is not null
                && leg.ArcCenterLon is not null
                && leg.ArcRadiusNm is not null
                && previousFixPos is not null
            )
            {
                ExpandArcWaypoints(
                    targets,
                    leg.ArcCenterLat.Value,
                    leg.ArcCenterLon.Value,
                    leg.ArcRadiusNm.Value,
                    previousFixPos.Value,
                    pos.Value,
                    leg.TurnDirection
                );
            }

            // AF leg: expand DME arc from previous fix to terminator fix
            if (
                leg.PathTerminator == CifpPathTerminator.AF
                && leg.RecommendedNavaidId is not null
                && leg.Rho is not null
                && previousFixPos is not null
            )
            {
                (double Lat, double Lon)? navaidPos = navDb.GetFixPosition(leg.RecommendedNavaidId);
                if (navaidPos is not null)
                {
                    ExpandArcWaypoints(
                        targets,
                        navaidPos.Value.Lat,
                        navaidPos.Value.Lon,
                        leg.Rho.Value,
                        previousFixPos.Value,
                        pos.Value,
                        leg.TurnDirection
                    );
                }
            }

            // Add the terminator fix with constraints
            targets.Add(
                new NavigationTarget
                {
                    Name = leg.FixIdentifier,
                    Position = new LatLon(pos.Value.Lat, pos.Value.Lon),
                    AltitudeRestriction = leg.Altitude,
                    SpeedRestriction = leg.Speed,
                    IsFlyOver =
                        leg.IsFlyOver
                        || leg.FixRole is CifpFixRole.FAF or CifpFixRole.MAP
                        || leg.PathTerminator is CifpPathTerminator.HA or CifpPathTerminator.HF or CifpPathTerminator.HM,
                    TerminalCourseMagnetic = leg.PathTerminator == CifpPathTerminator.FM ? leg.OutboundCourse : null,
                }
            );

            previousFixPos = (pos.Value.Lat, pos.Value.Lon);
        }
        return targets;
    }

    /// <summary>
    /// Expands an arc between two fixes into intermediate waypoints.
    /// Computes start/end bearings from the arc center and generates points along the arc.
    /// </summary>
    private static void ExpandArcWaypoints(
        List<NavigationTarget> targets,
        double centerLat,
        double centerLon,
        double radiusNm,
        (double Lat, double Lon) previousFix,
        (double Lat, double Lon) terminatorFix,
        char? turnDirection
    )
    {
        double startBearing = GeoMath.BearingTo(centerLat, centerLon, previousFix.Lat, previousFix.Lon);
        double endBearing = GeoMath.BearingTo(centerLat, centerLon, terminatorFix.Lat, terminatorFix.Lon);
        bool turnRight = GeoMath.ResolveArcTurnRight(turnDirection, startBearing, endBearing);

        List<(double Lat, double Lon)> arcPoints = GeoMath.GenerateArcPoints(centerLat, centerLon, radiusNm, startBearing, endBearing, turnRight);

        // Insert intermediate points (skip the last one — that's the terminator fix itself)
        for (int i = 0; i < arcPoints.Count - 1; i++)
        {
            targets.Add(new NavigationTarget { Name = $"ARC{i + 1:D2}", Position = new LatLon(arcPoints[i].Lat, arcPoints[i].Lon) });
        }
    }

    /// <summary>
    /// Re-resolves SID route/heading on a stored departure clearance after a flight-plan
    /// route amendment (e.g. NIMI5 → NIMI6) while the aircraft is still taxiing.
    /// Also refreshes any pending <see cref="InitialClimbPhase"/> in the tower chain.
    /// </summary>
    internal static void RefreshStoredDepartureClearance(AircraftState aircraft)
    {
        if (aircraft.Phases?.DepartureClearance is not { } stored)
        {
            return;
        }

        DepartureRouteResult? routeResult = ResolveDepartureRoute(stored.Departure, aircraft);
        aircraft.Phases.DepartureClearance = new DepartureClearanceInfo
        {
            Type = stored.Type,
            Departure = stored.Departure,
            AssignedAltitude = stored.AssignedAltitude,
            DepartureRoute = routeResult?.Targets,
            DepartureProcedureLegs = routeResult?.ProcedureLegs,
            DepartureSidId = routeResult?.SidId,
            SidDepartureHeadingMagnetic = routeResult?.DepartureHeadingMagnetic,
            RvSidDeferHeadingUntilMinAlt = routeResult?.RvSidDeferHeadingUntilMinAlt ?? stored.RvSidDeferHeadingUntilMinAlt,
            RvSidHoldRunwayHeading = routeResult?.RvSidHoldRunwayHeading ?? stored.RvSidHoldRunwayHeading,
            PatternRunway = stored.PatternRunway,
            PreClearedHoldShortNodeIds = stored.PreClearedHoldShortNodeIds,
        };

        if (routeResult is not null)
        {
            RefreshPendingInitialClimbPhases(aircraft, stored.Departure, stored.AssignedAltitude, routeResult);
        }
    }

    /// <summary>
    /// Re-resolves RV SID heading / enroute targets on pending <see cref="InitialClimbPhase"/> instances
    /// after a route amendment (e.g. empty route at first CTO, then NIMI6 filed seconds later).
    /// </summary>
    internal static void RefreshPendingInitialClimbPhases(AircraftState aircraft)
    {
        if (aircraft.Phases is null)
        {
            return;
        }

        DepartureRouteResult? routeResult = ResolveDepartureRoute(new DefaultDeparture(), aircraft);
        if (routeResult is null)
        {
            return;
        }

        var pending = aircraft.Phases.Phases.OfType<InitialClimbPhase>().Where(p => p.Status == PhaseStatus.Pending).ToList();
        foreach (InitialClimbPhase? climb in pending)
        {
            DepartureInstruction departure = climb.Departure ?? new DefaultDeparture();
            SetInitialClimbProperties(climb, departure, climb.AssignedAltitude, routeResult, aircraft);
        }
    }

    private static void RefreshPendingInitialClimbPhases(
        AircraftState aircraft,
        DepartureInstruction departure,
        int? assignedAltitude,
        DepartureRouteResult routeResult
    )
    {
        var pending = aircraft.Phases!.Phases.OfType<InitialClimbPhase>().Where(p => p.Status == PhaseStatus.Pending).ToList();
        foreach (InitialClimbPhase? climb in pending)
        {
            SetInitialClimbProperties(climb, departure, assignedAltitude, routeResult, aircraft);
        }
    }

    /// <summary>
    /// Sets properties on a pending InitialClimbPhase. Since init properties
    /// are set via object initializer, we use reflection-free approach by
    /// replacing the phase in the list.
    /// </summary>
    internal static void SetInitialClimbProperties(
        InitialClimbPhase existing,
        DepartureInstruction departure,
        int? assignedAltitude,
        DepartureRouteResult? routeResult,
        AircraftState aircraft
    )
    {
        // InitialClimbPhase uses init properties, so we can't set them after
        // construction. However, since the phase hasn't started yet (Pending),
        // we find it in the phase list and replace it with a new instance.
        if (aircraft.Phases is null)
        {
            return;
        }

        var newClimb = new InitialClimbPhase
        {
            Departure = departure,
            AssignedAltitude = assignedAltitude,
            DepartureRoute = routeResult?.Targets ?? [],
            DepartureSidId = routeResult?.SidId,
            SidDepartureHeadingMagnetic = routeResult?.DepartureHeadingMagnetic,
            RvSidDeferHeadingUntilMinAlt = routeResult?.RvSidDeferHeadingUntilMinAlt ?? false,
            RvSidHoldRunwayHeading = routeResult?.RvSidHoldRunwayHeading ?? false,
            DepartureProcedureLegs = routeResult?.ProcedureLegs,
            IsVfr = aircraft.FlightPlan.IsVfr,
            CruiseAltitude = aircraft.FlightPlan.Altitude.CruiseFeet ?? 0,
        };

        for (int i = 0; i < aircraft.Phases.Phases.Count; i++)
        {
            if (ReferenceEquals(aircraft.Phases.Phases[i], existing))
            {
                aircraft.Phases.Phases[i] = newClimb;
                break;
            }
        }
    }

    internal static CommandResult TryCancelTakeoff(AircraftState aircraft, Phase currentPhase, DispatchContext ctx)
    {
        if (currentPhase is LinedUpAndWaitingPhase luawCancel)
        {
            foreach (ClearanceRequirement req in luawCancel.Requirements)
            {
                if (req.Type == ClearanceType.ClearedForTakeoff)
                {
                    req.IsSatisfied = false;
                }
            }
            luawCancel.Departure = null;
            luawCancel.AssignedAltitude = null;
            aircraft.Targets.AssignedAltitude = null;
            return CommandDispatcher.Ok($"Takeoff clearance cancelled, hold in position{CommandDispatcher.RunwayLabel(aircraft)}");
        }
        if (currentPhase is LineUpPhase lineup)
        {
            return CancelTakeoffDuringLineUp(aircraft, lineup);
        }
        if (currentPhase is TaxiingPhase && aircraft.Phases?.DepartureClearance is { Type: ClearanceType.ClearedForTakeoff } stored)
        {
            // CTO was issued mid-taxi and stored for the taxi phase to consume
            // at the runway. Revert the hold-shorts that storage pre-cleared
            // (only the ones this clearance flipped — leave LV/RC/CROSS state
            // alone) and drop the stored clearance. The aircraft will reach
            // the runway and hold short until a fresh clearance is issued.
            TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
            if (route is not null && stored.PreClearedHoldShortNodeIds is { } ids)
            {
                var idSet = ids.ToHashSet();
                foreach (HoldShortPoint hs in route.HoldShortPoints)
                {
                    if (idSet.Contains(hs.NodeId))
                    {
                        hs.IsCleared = false;
                    }
                }
            }
            aircraft.Phases.DepartureClearance = null;
            aircraft.Phases.LineUpIntersectionTaxiway = null;
            aircraft.Targets.AssignedAltitude = null;
            return CommandDispatcher.Ok($"Takeoff clearance cancelled, hold short{CommandDispatcher.RunwayLabel(aircraft)}");
        }
        if (currentPhase is TakeoffPhase rollingTakeoff && aircraft.IsOnGround)
        {
            // Abort takeoff during ground roll (7110.65 §3-9-11 — mid-roll only for safety).
            // Past V1 (≈ Vr − 5 kts, compared in INDICATED — the on-ground IAS field carries
            // groundspeed, so a headwind moves the gate) the aircraft is committed and the
            // pilot answers "unable" — unless a blocking occupant ahead cannot be overflown, in
            // which case continuing means a certain collision and the pilot rejects anyway
            // (AIM 4-4-1.a; 14 CFR 91.3(a) — the pilot in command is the final authority).
            AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
            AircraftState? blocked = null;
            double blockedDistanceFt = double.MaxValue;
            if (
                RejectedTakeoff.IsAtOrPastV1(aircraft)
                && !RejectedTakeoff.TryFindBlockedBeyondOverfly(aircraft, cat, ctx, out blocked, out blockedDistanceFt)
            )
            {
                double v1 = AircraftPerformance.DecisionSpeed(aircraft.AircraftType, cat);
                return new CommandResult(false, $"Unable — past V1 ({v1:F0} kts), continuing the takeoff");
            }

            RejectedTakeoffPhase? reject = RejectedTakeoff.Install(
                CommandDispatcher.BuildMinimalContext(aircraft, ctx.ActiveRunways, ctx.GroundLayout),
                rollingTakeoff.RollElapsedSeconds
            );
            if ((reject is not null) && (blocked is not null))
            {
                RejectedTakeoff.WarnIfCannotStopShort(aircraft, cat, blocked, blockedDistanceFt, reject);
            }

            return CommandDispatcher.Ok($"Abort takeoff, hold position{CommandDispatcher.RunwayLabel(aircraft)}");
        }
        if (currentPhase is TakeoffPhase)
        {
            return new CommandResult(false, "Aircraft is airborne — the takeoff clearance is complete");
        }
        if (currentPhase is HelicopterTakeoffPhase heloTakeoff)
        {
            return CancelHelicopterTakeoff(aircraft, heloTakeoff);
        }
        return new CommandResult(false, "No takeoff clearance to cancel");
    }

    /// <summary>
    /// CTOC during a helicopter's vertical liftoff. No V1 gate — §3-9-11's mid-roll restriction
    /// is written on "started takeoff roll", which a rotorcraft never does (Vr = 0), so it can
    /// stop its departure anywhere: barely off the ground it sets back down and holds in
    /// position; established in the climb it stops where it is and hovers (the <c>CTOPP</c>
    /// present-position-hover shape) awaiting instructions. Either way the takeoff clearance is
    /// spent. Stated simplification: a stationary OGE hover between ~50 and 400 ft sits in the
    /// height-velocity avoid region a real single would not park in — a real crew would return
    /// to the pad or hold with forward airspeed; the hover reuses the existing CTOPP machinery.
    /// </summary>
    private static CommandResult CancelHelicopterTakeoff(AircraftState aircraft, HelicopterTakeoffPhase heloTakeoff)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "No takeoff clearance to cancel");
        }

        heloTakeoff.SetAssignedDeparture(null);
        aircraft.Targets.AssignedAltitude = null;
        // No route to the room's active runways: this handler is not given the dispatch context.
        PhaseContext heloCtx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);

        double fieldElevation =
            aircraft.Phases.AssignedRunway?.ElevationFt ?? CommandDispatcher.ResolveFieldElevation(aircraft, aircraft.Ground.Layout);
        double agl = aircraft.Altitude - fieldElevation;
        const double SetDownAglFt = 50.0;
        if (agl < SetDownAglFt)
        {
            // Barely lifted: set back down on the spot and hold.
            aircraft.IsOnGround = true;
            aircraft.Altitude = fieldElevation;
            aircraft.IndicatedAirspeed = 0;
            aircraft.Phases.ReplaceUpcoming([new HoldingInPositionPhase()]);
            aircraft.Phases.AdvanceToNext(heloCtx);
            FlightPhysics.NotifyPhaseAdvanced(aircraft);
            return CommandDispatcher.Ok("Takeoff clearance cancelled, setting down and holding position");
        }

        // Established in the vertical climb: stop here and hover until the next instruction.
        heloTakeoff.CompletionAgl = agl;
        aircraft.Phases.ReplaceUpcoming([new VfrHoldPhase { OrbitDirection = null }]);
        return CommandDispatcher.Ok("Takeoff clearance cancelled, holding hover");
    }

    /// <summary>
    /// Cancel a takeoff clearance while the aircraft is in <see cref="LineUpPhase"/>.
    /// Two phase-list shapes possible:
    /// <list type="bullet">
    ///   <item><b>Non-rolling</b> (heavy/super or non-rolling-eligible): the upcoming
    ///         pending <see cref="LinedUpAndWaitingPhase"/> has its CTO requirement
    ///         pre-satisfied. Cancel by un-satisfying it; the aircraft completes
    ///         line-up and then holds.</item>
    ///   <item><b>Rolling</b>: no LUAW phase exists between line-up and takeoff.
    ///         Flip <see cref="LineUpPhase.RollingMode"/> to false (so the rollout
    ///         brakes to a stop instead of cruising into TakeoffPhase) and insert
    ///         a fresh unsatisfied <see cref="LinedUpAndWaitingPhase"/> ahead of
    ///         the takeoff phase.</item>
    /// </list>
    /// </summary>
    private static CommandResult CancelTakeoffDuringLineUp(AircraftState aircraft, LineUpPhase lineup)
    {
        List<Phase>? phases = aircraft.Phases?.Phases;
        if (phases is null)
        {
            return new CommandResult(false, "No takeoff clearance to cancel");
        }

        int selfIdx = phases.IndexOf(lineup);
        if (selfIdx < 0)
        {
            return new CommandResult(false, "No takeoff clearance to cancel");
        }

        // Look for an upcoming pending LUAW (non-rolling shape) before any takeoff phase.
        for (int i = selfIdx + 1; i < phases.Count; i++)
        {
            Phase p = phases[i];
            if (p is LinedUpAndWaitingPhase upcomingLuaw && p.Status == PhaseStatus.Pending)
            {
                foreach (ClearanceRequirement req in upcomingLuaw.Requirements)
                {
                    if (req.Type == ClearanceType.ClearedForTakeoff)
                    {
                        req.IsSatisfied = false;
                    }
                }
                upcomingLuaw.Departure = null;
                upcomingLuaw.AssignedAltitude = null;
                aircraft.Targets.AssignedAltitude = null;
                // 7110.65 §3-9-11 lets the controller cancel a takeoff clearance at
                // any time. The aircraft stops where it is — it does not continue
                // rolling onto the runway — and the upcoming LUAW (now unsatisfied)
                // makes the aircraft hold if a fresh CTO later resumes the line-up.
                // When a mid-line-up CTO upgraded the line-up to rolling, revert
                // that upgrade: left in rolling mode with the navigator's floor up,
                // a line-up later released by LUAW instead of a fresh CTO flows the
                // graph route at the floor to the end of its rollout and hard-snaps
                // to zero there instead of braking onto the centerline.
                lineup.RollingMode = false;
                lineup.ClearRollingSpeedFloor();
                lineup.HoldPosition = true;
                return CommandDispatcher.Ok($"Takeoff clearance cancelled, hold in position{CommandDispatcher.RunwayLabel(aircraft)}");
            }
            if (p is TakeoffPhase or HelicopterTakeoffPhase)
            {
                // Rolling shape: no LUAW between line-up and takeoff. Demote
                // line-up out of rolling mode and insert a fresh unsatisfied
                // LUAW so the aircraft holds when the line-up brake completes.
                lineup.RollingMode = false;
                lineup.ClearRollingSpeedFloor();
                phases.Insert(i, new LinedUpAndWaitingPhase());
                aircraft.Targets.AssignedAltitude = null;
                // Hold position immediately — stop where we are rather than
                // completing the line-up onto the centerline (7110.65 §3-9-11).
                lineup.HoldPosition = true;
                return CommandDispatcher.Ok($"Takeoff clearance cancelled, hold in position{CommandDispatcher.RunwayLabel(aircraft)}");
            }
        }

        return new CommandResult(false, "No takeoff clearance to cancel");
    }

    internal static CommandResult TryClearedTakeoffPresent(
        ClearedTakeoffPresentCommand ctopp,
        AircraftState aircraft,
        AirportGroundLayout? groundLayout
    )
    {
        AircraftCategory ctoppCat = AircraftCategorization.Categorize(aircraft.AircraftType);
        if (ctoppCat != AircraftCategory.Helicopter)
        {
            return new CommandResult(false, "CTOPP is only valid for helicopters");
        }

        if (!aircraft.IsOnGround)
        {
            return new CommandResult(false, "CTOPP requires the aircraft to be on the ground");
        }

        // Clear existing phases and set up vertical takeoff
        // No route to the room's active runways: this handler is not given the dispatch context.
        PhaseContext ctoppCtx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, groundLayout);
        aircraft.Phases?.Clear(ctoppCtx);

        aircraft.Ground.Hold = null;
        aircraft.Phases = new PhaseList();

        var helo = new HelicopterTakeoffPhase();
        helo.SetAssignedDeparture(ctopp.Departure);
        aircraft.Phases.Add(helo);

        DepartureRouteResult? routeResult = null;
        if (ctopp.Departure is PresentPositionHoverDeparture hover)
        {
            // Hold in place: vertical liftoff to the hover altitude, then hover (zero forward
            // speed, hold present heading) until the controller issues the next command.
            helo.CompletionAgl = hover.HoverAltitudeAglFt;
            aircraft.Phases.Add(new VfrHoldPhase { OrbitDirection = null });
        }
        else
        {
            // Depart: vertical liftoff first, then InitialClimbPhase applies the departure turn
            // and forward acceleration once airborne.
            var climb = new InitialClimbPhase { IsVfr = aircraft.FlightPlan.IsVfr, CruiseAltitude = aircraft.FlightPlan.Altitude.CruiseFeet ?? 0 };
            aircraft.Phases.Add(climb);

            routeResult = ResolveDepartureRoute(ctopp.Departure, aircraft);
            SetInitialClimbProperties(climb, ctopp.Departure, ctopp.AssignedAltitude, routeResult, aircraft);
        }

        // Field elevation = current altitude (on ground)
        ctoppCtx = new PhaseContext
        {
            ActiveRunways = ctoppCtx.ActiveRunways,
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = ctoppCat,
            DeltaSeconds = 0,
            Runway = null,
            FieldElevation = aircraft.Altitude,
            GroundLayout = groundLayout,
            Logger = Log,
        };
        aircraft.Phases.Start(ctoppCtx);
        SyncControllerAssignedAltitude(aircraft, ClearanceType.ClearedForTakeoff, ctopp.AssignedAltitude);

        string msg = "Cleared for takeoff, present position";
        msg += FormatDepartureInstructionSuffix(ctopp.Departure);
        if (ctopp.AssignedAltitude is not null)
        {
            msg += $", climb and maintain {ctopp.AssignedAltitude:N0}";
        }
        return CommandDispatcher.Ok(msg) with { Advisory = ProcedureSourceSidAdvisory(ClearanceType.ClearedForTakeoff, routeResult, aircraft) };
    }
}
