using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

internal sealed record DepartureRouteResult(
    List<NavigationTarget> Targets,
    string? SidId,
    double? DepartureHeadingMagnetic = null,
    bool RvSidDeferHeadingUntilMinAlt = false,
    bool RvSidHoldRunwayHeading = false,
    List<ProcedureLeg>? ProcedureLegs = null
);

internal static class DepartureClearanceHandler
{
    /// <summary>
    /// When CTO bundles an explicit climb-to altitude, mirror it onto
    /// <see cref="ControlTargets.AssignedAltitude"/> so the datablock, SALT,
    /// and SnapshotDiff observe a single source of truth from issuance —
    /// matching the CM/DM/FA pattern. Bare LUAW (without takeoff) does not
    /// authorize the climb yet, so leave the field alone in that case.
    /// </summary>
    private static void SyncControllerAssignedAltitude(AircraftState aircraft, ClearanceType type, int? assignedAltitude)
    {
        if (type == ClearanceType.ClearedForTakeoff && assignedAltitude is { } v)
        {
            aircraft.Targets.AssignedAltitude = v;
        }
    }

    /// <summary>
    /// Resolves and stores the departure's published SID initial-altitude cap (the TDLS "maintain"
    /// altitude, e.g. KIAH 4000 / KHOU 5000) on the aircraft so <see cref="InitialClimbPhase"/> holds
    /// it when no altitude is commanded. Only IFR departures on a SID get a value; VFR clears it. The
    /// SID and enroute transition come from the filed route's first two tokens (e.g. "BLTWY7 CRIED").
    /// </summary>
    internal static void StoreSidInitialAltitude(AircraftState aircraft, ArtccConfigRoot? artccConfig)
    {
        if (aircraft.FlightPlan.IsVfr)
        {
            aircraft.Procedure.SidInitialAltitudeFt = null;
            return;
        }

        var tokens = (aircraft.FlightPlan.Route ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? sidId = tokens.Length > 0 ? tokens[0] : null;
        string? transitionId = tokens.Length > 1 ? tokens[1] : null;

        aircraft.Procedure.SidInitialAltitudeFt = artccConfig.GetSidInitialAltitudeFt(aircraft.FlightPlan.Departure ?? "", sidId, transitionId);
    }

    internal static CommandResult TryClearedForTakeoff(ClearedForTakeoffCommand cto, AircraftState aircraft, LinedUpAndWaitingPhase luaw)
    {
        if (aircraft.Phases?.AssignedRunway is null)
        {
            return new CommandResult(false, "No runway assigned — cannot clear for takeoff");
        }

        luaw.Departure = cto.Departure;
        luaw.AssignedAltitude = cto.AssignedAltitude;
        luaw.SatisfyClearance(ClearanceType.ClearedForTakeoff);
        SyncControllerAssignedAltitude(aircraft, ClearanceType.ClearedForTakeoff, cto.AssignedAltitude);

        string? takeoffDesignator = null;

        // Propagate departure to TakeoffPhase and InitialClimbPhase
        if (aircraft.Phases is not null)
        {
            var routeResult = ResolveDepartureRoute(cto.Departure, aircraft);

            for (int i = 0; i < aircraft.Phases.Phases.Count; i++)
            {
                var p = aircraft.Phases.Phases[i];
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

            // ClosedTrafficDeparture: establish pattern mode and append circuit.
            // Capture takeoff runway before ApplyClosedTraffic overwrites AssignedRunway.
            if (cto.Departure is ClosedTrafficDeparture ct && aircraft.Phases.AssignedRunway is { } ctRunway)
            {
                takeoffDesignator = ctRunway.Designator;
                ApplyClosedTraffic(ct, aircraft, aircraft.Phases, ctRunway, removeInitialClimb: true);
            }
        }

        return BuildDepartureMessage(
            ClearanceType.ClearedForTakeoff,
            takeoffDesignator ?? aircraft.Phases?.AssignedRunway?.Designator ?? "unknown",
            cto.Departure,
            cto.AssignedAltitude
        );
    }

    internal static CommandResult TryDepartureClearance(
        AircraftState aircraft,
        Phase currentPhase,
        ClearanceType clearanceType,
        DepartureInstruction departure,
        int? assignedAltitude,
        ILogger logger
    )
    {
        if (currentPhase is HoldingShortPhase holding)
        {
            return LineUpFromHoldShort(aircraft, holding, clearanceType, departure, assignedAltitude, logger);
        }

        if (currentPhase is TaxiingPhase)
        {
            return StoreDepartureClearanceDuringTaxi(aircraft, clearanceType, departure, assignedAltitude);
        }

        // Aircraft is lining up — CTO can pre-satisfy the upcoming LUAW phase
        if (currentPhase is LineUpPhase && clearanceType == ClearanceType.ClearedForTakeoff)
        {
            return SatisfyUpcomingTakeoffClearance(aircraft, departure, assignedAltitude, logger);
        }

        // Aircraft holding in position (e.g. after WARPG) — allow LUAW/CTO with assigned runway
        if (currentPhase is HoldingInPositionPhase)
        {
            return LineUpFromPosition(aircraft, clearanceType, departure, assignedAltitude, logger);
        }

        if (clearanceType == ClearanceType.ClearedForTakeoff)
        {
            return new CommandResult(false, "Aircraft is not lined up and waiting");
        }

        return new CommandResult(false, "Line up and wait requires aircraft to be taxiing or holding short");
    }

    internal static CommandResult LineUpFromHoldShort(
        AircraftState aircraft,
        HoldingShortPhase holding,
        ClearanceType clearanceType,
        DepartureInstruction departure,
        int? assignedAltitude,
        ILogger logger
    )
    {
        var runwayId = holding.HoldShort.TargetName;
        if (runwayId is null)
        {
            return new CommandResult(false, "Hold short point has no runway assigned");
        }

        var runway = CommandDispatcher.ResolveRunway(aircraft, runwayId);
        if (runway is null)
        {
            // Target is a taxiway (e.g., "HS E"), not a runway.
            // Clear the hold-short and store the departure clearance so the
            // aircraft resumes taxiing to its destination runway.
            holding.SatisfyClearance(ClearanceType.RunwayCrossing);
            return StoreDepartureClearanceDuringTaxi(aircraft, clearanceType, departure, assignedAltitude);
        }

        // If the controller previously assigned a specific runway end (via RWY command) and it refers
        // to the same physical runway as the hold-short, respect that assignment. This preserves the
        // controller's explicit direction (e.g., RWY 01R at SFO when hold-short TargetName is "1R").
        if (aircraft.Phases?.AssignedRunway is { } existingRunway && runway.Id.Overlaps(existingRunway.Id))
        {
            runway = existingRunway;
        }

        // Satisfy the runway crossing clearance so HoldingShortPhase completes
        holding.SatisfyClearance(ClearanceType.RunwayCrossing);

        // Set the assigned runway and insert tower phases
        aircraft.Phases!.AssignedRunway = runway;
        aircraft.Procedure.DepartureRunway = runway.Designator;
        InsertTowerPhasesAfterCurrent(aircraft, clearanceType, departure, assignedAltitude, runway, holding.HoldShort.NodeId, logger);
        SyncControllerAssignedAltitude(aircraft, clearanceType, assignedAltitude);

        return BuildDepartureMessage(clearanceType, runway.Designator, departure, assignedAltitude);
    }

    internal static CommandResult LineUpFromPosition(
        AircraftState aircraft,
        ClearanceType clearanceType,
        DepartureInstruction departure,
        int? assignedAltitude,
        ILogger logger
    )
    {
        if (aircraft.Phases?.AssignedRunway is not { } runway)
        {
            return new CommandResult(false, "No runway assigned — use RWY first");
        }

        // Clear the holding phase and rebuild with tower departure phases
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Clear(ctx);

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Procedure.DepartureRunway = runway.Designator;
        InsertTowerPhasesAfterCurrent(aircraft, clearanceType, departure, assignedAltitude, runway, null, logger);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft));
        SyncControllerAssignedAltitude(aircraft, clearanceType, assignedAltitude);

        return BuildDepartureMessage(clearanceType, runway.Designator, departure, assignedAltitude);
    }

    internal static CommandResult StoreDepartureClearanceDuringTaxi(
        AircraftState aircraft,
        ClearanceType clearanceType,
        DepartureInstruction departure,
        int? assignedAltitude
    )
    {
        var route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        // Find the destination runway hold-short. Prefer DestinationRunway (authoritative);
        // fall back to the last ExplicitHoldShort only when no destination exists
        // (handles "TAXI C E" + "HS 28R" + "CTO" without a RWY directive).
        HoldShortPoint? depHoldShort = null;
        HoldShortPoint? explicitFallback = null;
        foreach (var hs in route.HoldShortPoints)
        {
            if (hs.Reason is HoldShortReason.DestinationRunway)
            {
                depHoldShort = hs;
            }
            else if (hs.Reason is HoldShortReason.ExplicitHoldShort)
            {
                explicitFallback = hs;
            }
        }

        depHoldShort ??= explicitFallback;

        if (depHoldShort?.TargetName is null)
        {
            return new CommandResult(false, "No departure runway hold-short in taxi route");
        }

        var runway = CommandDispatcher.ResolveRunway(aircraft, depHoldShort.TargetName);
        if (runway is null)
        {
            return new CommandResult(false, $"Cannot resolve runway {depHoldShort.TargetName}");
        }

        // Consistency: if RWY assigned a runway that doesn't share a physical runway
        // with the route's destination, refuse to silently overwrite. The controller
        // gave conflicting instructions; one of them has to be revoked.
        if (aircraft.Phases?.AssignedRunway is { } assigned && !runway.Id.Overlaps(assigned.Id))
        {
            return new CommandResult(
                false,
                $"Taxi route ends at {runway.Designator} but {assigned.Designator} is the assigned runway — re-taxi or re-assign with RWY"
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
        foreach (var hs in route.HoldShortPoints)
        {
            if (
                hs.Reason == HoldShortReason.RunwayCrossing
                && hs.TargetName is not null
                && hs.TargetName.Contains(runway.Designator, StringComparison.OrdinalIgnoreCase)
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
        var routeResult = ResolveDepartureRoute(departure, aircraft);

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
            DepartureSidId = routeResult?.SidId,
            SidDepartureHeadingMagnetic = routeResult?.DepartureHeadingMagnetic,
            RvSidDeferHeadingUntilMinAlt = routeResult?.RvSidDeferHeadingUntilMinAlt ?? false,
            RvSidHoldRunwayHeading = routeResult?.RvSidHoldRunwayHeading ?? false,
            PatternRunway = patternRunway,
            PreClearedHoldShortNodeIds = preClearedIds.Count > 0 ? preClearedIds : null,
        };
        SyncControllerAssignedAltitude(aircraft, clearanceType, assignedAltitude);

        return BuildDepartureMessage(clearanceType, runway.Designator, departure, assignedAltitude);
    }

    internal static void InsertTowerPhasesAfterCurrent(
        AircraftState aircraft,
        ClearanceType clearanceType,
        DepartureInstruction departure,
        int? assignedAltitude,
        RunwayInfo runway,
        int? holdShortNodeId,
        ILogger logger
    )
    {
        var routeResult = ResolveDepartureRoute(departure, aircraft);

        var lineup = new LineUpPhase();
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
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

        // For closed traffic, skip InitialClimbPhase — UpwindPhase handles the
        // climb to pattern altitude. The crosswind turn is position-based (departure end),
        // not altitude-based.
        bool isClosedTraffic = departure is ClosedTrafficDeparture;
        Phase[] towerPhases;
        if (isClosedTraffic)
        {
            towerPhases = rolling ? [lineup, takeoffPhase] : [lineup, new LinedUpAndWaitingPhase(), takeoffPhase];
        }
        else
        {
            var climb = new InitialClimbPhase
            {
                Departure = departure,
                AssignedAltitude = assignedAltitude,
                DepartureRoute = routeResult?.Targets,
                DepartureSidId = routeResult?.SidId,
                SidDepartureHeadingMagnetic = routeResult?.DepartureHeadingMagnetic,
                RvSidDeferHeadingUntilMinAlt = routeResult?.RvSidDeferHeadingUntilMinAlt ?? false,
                RvSidHoldRunwayHeading = routeResult?.RvSidHoldRunwayHeading ?? false,
                DepartureProcedureLegs = routeResult?.ProcedureLegs,
                IsVfr = aircraft.FlightPlan.IsVfr,
                CruiseAltitude = aircraft.FlightPlan.CruiseAltitude,
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
                var luawPhase = towerPhases.OfType<LinedUpAndWaitingPhase>().FirstOrDefault();
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

            if (departure is ClosedTrafficDeparture ct)
            {
                ApplyClosedTraffic(ct, aircraft, aircraft.Phases!, runway, removeInitialClimb: false);
            }
        }

        logger.LogDebug(
            "[Departure] {Callsign}: tower phases inserted ({Clearance}, rolling={Rolling}), runway {Rwy}",
            aircraft.Callsign,
            clearanceType,
            rolling,
            runway.Designator
        );
    }

    internal static CommandResult SatisfyUpcomingTakeoffClearance(
        AircraftState aircraft,
        DepartureInstruction departure,
        int? assignedAltitude,
        ILogger logger
    )
    {
        var phases = aircraft.Phases;
        if (phases is null)
        {
            return new CommandResult(false, "No active phase sequence");
        }

        // Find the pending LinedUpAndWaitingPhase, TakeoffPhase/HelicopterTakeoffPhase, InitialClimbPhase
        LinedUpAndWaitingPhase? luaw = null;
        Phase? takeoff = null;
        InitialClimbPhase? climb = null;
        foreach (var p in phases.Phases)
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

        var routeResult = ResolveDepartureRoute(departure, aircraft);

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

        // Capture takeoff runway before ApplyClosedTraffic overwrites AssignedRunway.
        var takeoffDesignator = phases.AssignedRunway?.Designator ?? "unknown";
        if (departure is ClosedTrafficDeparture ct && phases.AssignedRunway is { } rwy)
        {
            ApplyClosedTraffic(ct, aircraft, phases, rwy, removeInitialClimb: true);
        }

        // Rolling-takeoff upgrade: if an active LineUpPhase exists and the
        // aircraft is still above the upgrade speed threshold, flip it into
        // rolling mode so the aircraft doesn't bother finishing the brake
        // curve. The LUAW pre-satisfy above stays as the fallback — if the
        // upgrade is rejected (state == Setup/Stop/Faulted, IAS too low, or
        // Super/Heavy) the original stop-then-go path runs via the
        // pre-satisfied LUAW.
        foreach (var p in phases.Phases)
        {
            if (p is LineUpPhase active && p.Status == PhaseStatus.Active)
            {
                // Re-clearing for takeoff lifts a prior CTOC hold-position so the
                // aircraft resumes the line-up and departs.
                active.HoldPosition = false;
                var upgradeCtx = CommandDispatcher.BuildMinimalContext(aircraft);
                active.TryUpgradeToRolling(upgradeCtx);
                break;
            }
        }

        logger.LogDebug("[Departure] {Callsign}: CTO satisfied on upcoming LUAW phase", aircraft.Callsign);

        SyncControllerAssignedAltitude(aircraft, ClearanceType.ClearedForTakeoff, assignedAltitude);

        return BuildDepartureMessage(ClearanceType.ClearedForTakeoff, takeoffDesignator, departure, assignedAltitude);
    }

    internal static CommandResult BuildDepartureMessage(
        ClearanceType clearanceType,
        string runwayId,
        DepartureInstruction departure,
        int? assignedAltitude
    )
    {
        if (clearanceType == ClearanceType.ClearedForTakeoff)
        {
            var msg = $"Cleared for takeoff runway {runwayId}";
            msg += FormatDepartureInstructionSuffix(departure);
            if (assignedAltitude is not null)
            {
                msg += $", climb and maintain {assignedAltitude:N0}";
            }
            return CommandDispatcher.Ok(msg);
        }

        return CommandDispatcher.Ok($"Line up and wait runway {runwayId}");
    }

    internal static string FormatDepartureInstructionSuffix(DepartureInstruction departure)
    {
        return departure switch
        {
            DefaultDeparture => "",
            PresentPositionHoverDeparture hover => $", hover and hold at {hover.HoverAltitudeAglFt:N0} feet",
            RunwayHeadingDeparture => ", fly runway heading",
            RelativeTurnDeparture { Degrees: 90, Direction: TurnDirection.Right } => ", right crosswind departure",
            RelativeTurnDeparture { Degrees: 90, Direction: TurnDirection.Left } => ", left crosswind departure",
            RelativeTurnDeparture { Degrees: 180, Direction: TurnDirection.Right } => ", right downwind departure",
            RelativeTurnDeparture { Degrees: 180, Direction: TurnDirection.Left } => ", left downwind departure",
            RelativeTurnDeparture rel => $", turn {(rel.Direction == TurnDirection.Right ? "right" : "left")} {rel.Degrees} degrees",
            FlyHeadingDeparture fh when fh.Direction is TurnDirection.Right => $", turn right heading {fh.MagneticHeading.ToDisplayInt():000}",
            FlyHeadingDeparture fh when fh.Direction is TurnDirection.Left => $", turn left heading {fh.MagneticHeading.ToDisplayInt():000}",
            FlyHeadingDeparture fh => $", fly heading {fh.MagneticHeading.ToDisplayInt():000}",
            OnCourseDeparture => ", on course",
            DirectFixDeparture { Direction: TurnDirection.Left } dfd => $", turn left direct {dfd.FixName}",
            DirectFixDeparture { Direction: TurnDirection.Right } dfd => $", turn right direct {dfd.FixName}",
            DirectFixDeparture dfd => $", direct {dfd.FixName}",
            ClosedTrafficDeparture ct when ct.RunwayId is not null =>
                $", make {(ct.Direction == PatternDirection.Left ? "left" : "right")} traffic runway {ct.RunwayId}",
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
        bool removeInitialClimb
    )
    {
        phases.TrafficDirection = ct.Direction;
        aircraft.Pattern.TrafficDirection = ct.Direction;

        if (ct.PatternAltitude is not null)
        {
            aircraft.Pattern.AltitudeOverrideFt = ct.PatternAltitude;
        }

        if (removeInitialClimb)
        {
            phases.Phases.RemoveAll(p => p is InitialClimbPhase { Status: PhaseStatus.Pending });
        }

        var patternRunway = ResolvePatternRunway(ct, aircraft) ?? fallbackRunway;
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        var airportRunways = Data.NavigationDatabase.Instance.GetRunways(patternRunway.AirportId);
        var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
            patternRunway,
            aircraft.Ground.Layout?.FindRunway(patternRunway.Designator),
            aircraft.Pattern.SizeOverrideNm,
            aircraft.Pattern.AltitudeOverrideFt
        );
        // Cross-runway closed traffic (e.g. takeoff 33, make right traffic 28R): the
        // first circuit climbs out on the DEPARTURE runway then joins the PATTERN
        // runway's downwind. Same-runway closed traffic flies a normal upwind-entry
        // circuit on the one runway.
        bool crossRunway = !string.Equals(patternRunway.Designator, fallbackRunway.Designator, StringComparison.OrdinalIgnoreCase);
        var circuit = crossRunway
            ? PatternBuilder.BuildCrossRunwayDepartureCircuit(fallbackRunway, patternRunway, cat, ct.Direction, true, sizeOv, altOv, airportRunways)
            : PatternBuilder.BuildCircuit(patternRunway, cat, ct.Direction, PatternEntryLeg.Upwind, true, null, sizeOv, altOv, airportRunways);

        phases.Phases.AddRange(circuit);
        phases.PatternRunway = patternRunway;
        // AssignedRunway carries the pattern runway (circuit/final/landing read it).
        // DepartureRunway carries the takeoff runway for lineup/takeoff when they differ.
        phases.AssignedRunway = patternRunway;
        phases.DepartureRunway = crossRunway ? fallbackRunway : null;
    }

    /// <summary>
    /// Pre-resolves navigation targets for route-based departure instructions.
    /// Keeps NavigationDatabase out of the phase layer.
    /// </summary>
    internal static DepartureRouteResult? ResolveDepartureRoute(DepartureInstruction departure, AircraftState aircraft)
    {
        var navDb = NavigationDatabase.Instance;

        switch (departure)
        {
            case DirectFixDeparture dfd:
                return new DepartureRouteResult([new NavigationTarget { Name = dfd.FixName, Position = new LatLon(dfd.Lat, dfd.Lon) }], null);

            case OnCourseDeparture when aircraft.FlightPlan.Destination is not null:
            {
                var pos = navDb.GetFixPosition(aircraft.FlightPlan.Destination);
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
                var cifpResult = TryResolveSidFromCifp(aircraft);
                if (cifpResult is not null)
                {
                    return cifpResult;
                }

                // Fallback to NavData body-fix expansion (lateral path only, no constraints)
                var targets = BuildFallbackNavTargets(aircraft, navDb);

                // CIFP couldn't resolve the SID (e.g. the procedure was retired from the current FAA
                // cycle, so the published vectors heading is unavailable). If the vNAS nav data still
                // recognizes the first route token as a radar-vectors SID — one with no published lateral
                // path — degrade to a radar-vectors departure: hold runway heading and await vectors
                // instead of turning direct to the first enroute fix. The expanded fixes are retained as
                // the post-vectors route, loaded after comms handoff like any other RV-SID departure.
                var firstToken = aircraft.FlightPlan.Route.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
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
        var expanded = navDb.ExpandRouteForNavigation(aircraft.FlightPlan.Route, aircraft.FlightPlan.Departure);
        var targets = new List<NavigationTarget>();

        var airportPos = aircraft.FlightPlan.Departure is not null ? navDb.GetFixPosition(aircraft.FlightPlan.Departure) : null;

        foreach (var name in expanded)
        {
            var pos = navDb.GetFixPosition(name);
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
        var navDb = NavigationDatabase.Instance;
        if (aircraft.FlightPlan.Route is null || aircraft.FlightPlan.Departure is null)
        {
            return null;
        }

        var routeTokens = aircraft.FlightPlan.Route.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (routeTokens.Length == 0)
        {
            return null;
        }

        // First route token is the SID name
        var sidName = routeTokens[0];
        var sid = navDb.GetSid(aircraft.FlightPlan.Departure, sidName);
        if (sid is null)
        {
            return null;
        }

        // Build ordered leg sequence: runway transition → common → enroute transition
        var orderedLegs = new List<CifpLeg>();

        // Select runway transition matching assigned runway ("RW" + designator)
        if (aircraft.Phases?.AssignedRunway is { } rwy)
        {
            var rwKey = "RW" + rwy.Designator;
            if (!sid.RunwayTransitions.TryGetValue(rwKey, out var rwTransition))
            {
                // CIFP "B" suffix means both L/R share the same transition (e.g. "RW01B")
                var bothKey = "RW" + rwy.Designator.TrimEnd('L', 'R', 'C') + "B";
                sid.RunwayTransitions.TryGetValue(bothKey, out rwTransition);
            }

            if (rwTransition is not null)
            {
                orderedLegs.AddRange(rwTransition.Legs);
            }
        }

        orderedLegs.AddRange(sid.CommonLegs);

        // Check for enroute transition (second route token matches a transition name)
        bool hasEnrouteTransition = false;
        if (routeTokens.Length > 1)
        {
            var enrouteKey = routeTokens[1].ToUpperInvariant();
            if (sid.EnrouteTransitions.TryGetValue(enrouteKey, out var enTransition))
            {
                orderedLegs.AddRange(enTransition.Legs);
                hasEnrouteTransition = true;
            }
        }

        if (orderedLegs.Count == 0)
        {
            return null;
        }

        // Detect radar vectors SIDs (core procedure ends with VM/VA/VI).
        // These have no published lateral path — controller vectors the aircraft.
        var rwLegs = aircraft.Phases?.AssignedRunway is { } rwyInfo ? GetRunwayTransitionLegs(sid, rwyInfo.Designator) : (IReadOnlyList<CifpLeg>)[];
        if (IsRadarVectorsSid(rwLegs, sid.CommonLegs))
        {
            double? heading = ExtractRadarVectorsHeading(orderedLegs);
            bool deferHeading = RvSidTransitionDefersHeadingAssignment(rwLegs);
            var rvTargets = new List<NavigationTarget>();

            if (hasEnrouteTransition)
            {
                // Enroute transition provides a published path from the vectors segment.
                // Resolve only the enroute transition legs (skip the core RV legs).
                var enrouteKey = routeTokens[1].ToUpperInvariant();
                var enLegs = sid.EnrouteTransitions[enrouteKey].Legs;
                rvTargets = ResolveLegsToTargets(enLegs);
            }

            AppendPostSidEnrouteFixes(rvTargets, routeTokens, sid, rvTargets.Count > 0 ? rvTargets[^1].Name : null);
            StripNearDepartureTargets(rvTargets, aircraft.FlightPlan.Departure);
            return new DepartureRouteResult(rvTargets, null, heading, deferHeading);
        }

        // Convert SID legs to NavigationTargets with constraints
        var targets = ResolveLegsToTargets(orderedLegs);
        if (targets.Count == 0)
        {
            return null;
        }

        AppendPostSidEnrouteFixes(targets, routeTokens, sid, targets[^1].Name);
        StripNearDepartureTargets(targets, aircraft.FlightPlan.Departure);

        // Typed legs preserve the VA/VI/VM/CA heading legs and course-tracked CF that the flat
        // resolver drops. Non-null only when the SID actually has coded heading/intercept legs;
        // plain TF/CF SIDs keep the flat direct-to-fix path unchanged.
        var procedureLegs = ProcedureLegResolver.ExtractActiveDepartureLegs(ProcedureLegResolver.Resolve(orderedLegs));

        return targets.Count > 0 ? new DepartureRouteResult(targets, sid.ProcedureId, ProcedureLegs: procedureLegs) : null;
    }

    /// <summary>
    /// Returns true when the core SID procedure (common legs, or runway transition if no common)
    /// terminates with a radar vectors leg (VM, VA, or VI). These SIDs have no published lateral
    /// path — the controller provides heading vectors after departure.
    /// </summary>
    internal static bool IsRadarVectorsSid(IReadOnlyList<CifpLeg> runwayTransitionLegs, IReadOnlyList<CifpLeg> commonLegs)
    {
        // Check the last leg of the core procedure (common legs take precedence)
        var lastCoreLeg =
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
            var leg = runwayTransitionLegs[i];
            if (
                leg.PathTerminator
                is CifpPathTerminator.CA
                    or CifpPathTerminator.FA
                    or CifpPathTerminator.VA
                    or CifpPathTerminator.TF
                    or CifpPathTerminator.CF
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
        var rwKey = "RW" + designator;
        if (sid.RunwayTransitions.TryGetValue(rwKey, out var rwTransition))
        {
            return rwTransition.Legs;
        }

        var bothKey = "RW" + designator.TrimEnd('L', 'R', 'C') + "B";
        if (sid.RunwayTransitions.TryGetValue(bothKey, out rwTransition))
        {
            return rwTransition.Legs;
        }

        return [];
    }

    /// <summary>
    /// Appends remaining enroute fixes from the filed route (post-SID tokens) without constraints.
    /// Delegates to <see cref="RouteExpander.Expand"/> to handle airway expansion, dot-notation,
    /// and deduplication. Prepends the last SID fix as an anchor so airways have a from-fix.
    /// </summary>
    private static void AppendPostSidEnrouteFixes(List<NavigationTarget> targets, string[] routeTokens, CifpSidProcedure sid, string? lastSidFix)
    {
        var navDb = NavigationDatabase.Instance;

        // Skip the SID token and any enroute transition token
        int startIdx = 1;
        if (routeTokens.Length > 1)
        {
            var secondToken = routeTokens[1].ToUpperInvariant();
            if (sid.EnrouteTransitions.ContainsKey(secondToken))
            {
                startIdx = 2;
            }
        }

        if (startIdx >= routeTokens.Length)
        {
            return;
        }

        // Prepend lastSidFix (or last target) as anchor so RouteExpander has a from-fix for airways
        string? anchor = lastSidFix ?? (targets.Count > 0 ? targets[^1].Name : null);
        var postSidTokens = routeTokens[startIdx..];
        string postSidRoute = anchor is not null ? anchor + " " + string.Join(' ', postSidTokens) : string.Join(' ', postSidTokens);

        // The SID token has already been stripped from postSidRoute, so the mismatch fallback won't
        // fire here today — but keep the flight-plan flag explicit so future routes that contain a
        // second SID token (theoretical) don't slip past.
        var expandedFixes = RouteExpander.Expand(postSidRoute, navDb, includeAllTransitionsOnMismatch: false);

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

        for (int i = fixStart; i < expandedFixes.Count; i++)
        {
            var pos = navDb.GetFixPosition(expandedFixes[i]);
            if (pos is not null)
            {
                targets.Add(new NavigationTarget { Name = expandedFixes[i].ToUpperInvariant(), Position = new LatLon(pos.Value.Lat, pos.Value.Lon) });
            }
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

        var navDb = NavigationDatabase.Instance;
        var airportPos = navDb.GetFixPosition(departure);
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
    /// Converts CIFP legs to NavigationTargets with altitude/speed constraints.
    /// Resolves fix positions, skips unknown fixes, carries restrictions.
    /// RF/AF legs are expanded into intermediate arc waypoints.
    /// PI (procedure turn) legs are skipped in SID/STAR context.
    /// </summary>
    internal static List<NavigationTarget> ResolveLegsToTargets(IReadOnlyList<CifpLeg> legs)
    {
        var navDb = NavigationDatabase.Instance;
        var targets = new List<NavigationTarget>();
        (double Lat, double Lon)? previousFixPos = null;

        foreach (var leg in legs)
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

            var pos = navDb.GetFixPosition(leg.FixIdentifier);
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
                    leg.TurnDirection == 'R'
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
                var navaidPos = navDb.GetFixPosition(leg.RecommendedNavaidId);
                if (navaidPos is not null)
                {
                    ExpandArcWaypoints(
                        targets,
                        navaidPos.Value.Lat,
                        navaidPos.Value.Lon,
                        leg.Rho.Value,
                        previousFixPos.Value,
                        pos.Value,
                        leg.TurnDirection != 'L'
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
        bool turnRight
    )
    {
        double startBearing = GeoMath.BearingTo(centerLat, centerLon, previousFix.Lat, previousFix.Lon);
        double endBearing = GeoMath.BearingTo(centerLat, centerLon, terminatorFix.Lat, terminatorFix.Lon);

        var arcPoints = GeoMath.GenerateArcPoints(centerLat, centerLon, radiusNm, startBearing, endBearing, turnRight);

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

        var routeResult = ResolveDepartureRoute(stored.Departure, aircraft);
        aircraft.Phases.DepartureClearance = new DepartureClearanceInfo
        {
            Type = stored.Type,
            Departure = stored.Departure,
            AssignedAltitude = stored.AssignedAltitude,
            DepartureRoute = routeResult?.Targets,
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

        var routeResult = ResolveDepartureRoute(new DefaultDeparture(), aircraft);
        if (routeResult is null)
        {
            return;
        }

        var pending = aircraft.Phases.Phases.OfType<InitialClimbPhase>().Where(p => p.Status == PhaseStatus.Pending).ToList();
        foreach (var climb in pending)
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
        foreach (var climb in pending)
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
            DepartureRoute = routeResult?.Targets,
            DepartureSidId = routeResult?.SidId,
            SidDepartureHeadingMagnetic = routeResult?.DepartureHeadingMagnetic,
            RvSidDeferHeadingUntilMinAlt = routeResult?.RvSidDeferHeadingUntilMinAlt ?? false,
            RvSidHoldRunwayHeading = routeResult?.RvSidHoldRunwayHeading ?? false,
            DepartureProcedureLegs = routeResult?.ProcedureLegs,
            IsVfr = aircraft.FlightPlan.IsVfr,
            CruiseAltitude = aircraft.FlightPlan.CruiseAltitude,
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

    internal static CommandResult TryCancelTakeoff(AircraftState aircraft, Phase currentPhase)
    {
        if (currentPhase is LinedUpAndWaitingPhase luawCancel)
        {
            foreach (var req in luawCancel.Requirements)
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
            var route = aircraft.Ground.AssignedTaxiRoute;
            if (route is not null && stored.PreClearedHoldShortNodeIds is { } ids)
            {
                var idSet = ids.ToHashSet();
                foreach (var hs in route.HoldShortPoints)
                {
                    if (idSet.Contains(hs.NodeId))
                    {
                        hs.IsCleared = false;
                    }
                }
            }
            aircraft.Phases.DepartureClearance = null;
            aircraft.Targets.AssignedAltitude = null;
            return CommandDispatcher.Ok($"Takeoff clearance cancelled, hold short{CommandDispatcher.RunwayLabel(aircraft)}");
        }
        if (currentPhase is TakeoffPhase && aircraft.IsOnGround)
        {
            // Abort takeoff during ground roll. Past V1 (≈ Vr - 5 kts) the
            // aircraft is committed — stopping on remaining runway is no
            // longer guaranteed, so reject the abort and let the takeoff
            // continue. The controller has to issue a different instruction
            // (heading/altitude override post-rotation) instead.
            var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
            double v1 = AircraftPerformance.DecisionSpeed(aircraft.AircraftType, cat);
            if (aircraft.IndicatedAirspeed >= v1)
            {
                return new CommandResult(false, $"Past V1 ({v1:F0} kts) — committed to takeoff, cannot abort");
            }

            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases?.Clear(ctx);
            aircraft.Phases = null;
            aircraft.Targets.TargetSpeed = 0;
            aircraft.Targets.AssignedAltitude = null;
            return CommandDispatcher.Ok("Abort takeoff, hold position");
        }
        return new CommandResult(false, "No takeoff clearance to cancel");
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
        var phases = aircraft.Phases?.Phases;
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
            var p = phases[i];
            if (p is LinedUpAndWaitingPhase upcomingLuaw && p.Status == PhaseStatus.Pending)
            {
                foreach (var req in upcomingLuaw.Requirements)
                {
                    if (req.Type == ClearanceType.ClearedForTakeoff)
                    {
                        req.IsSatisfied = false;
                    }
                }
                upcomingLuaw.Departure = null;
                upcomingLuaw.AssignedAltitude = null;
                aircraft.Targets.AssignedAltitude = null;
                // Hold position immediately — do not continue rolling onto the
                // runway (7110.65 §3-9-11). The upcoming LUAW (now unsatisfied)
                // makes the aircraft hold if a fresh CTO later resumes the line-up.
                lineup.HoldPosition = true;
                return CommandDispatcher.Ok($"Takeoff clearance cancelled, hold in position{CommandDispatcher.RunwayLabel(aircraft)}");
            }
            if (p is TakeoffPhase or HelicopterTakeoffPhase)
            {
                // Rolling shape: no LUAW between line-up and takeoff. Demote
                // line-up out of rolling mode and insert a fresh unsatisfied
                // LUAW so the aircraft holds when the line-up brake completes.
                lineup.RollingMode = false;
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
        var ctoppCat = AircraftCategorization.Categorize(aircraft.AircraftType);
        if (ctoppCat != AircraftCategory.Helicopter)
        {
            return new CommandResult(false, "CTOPP is only valid for helicopters");
        }

        if (!aircraft.IsOnGround)
        {
            return new CommandResult(false, "CTOPP requires the aircraft to be on the ground");
        }

        // Clear existing phases and set up vertical takeoff
        var ctoppCtx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        if (aircraft.Phases is not null)
        {
            aircraft.Phases.Clear(ctoppCtx);
        }

        aircraft.Ground.Hold = null;
        aircraft.Phases = new PhaseList();

        var helo = new HelicopterTakeoffPhase();
        helo.SetAssignedDeparture(ctopp.Departure);
        aircraft.Phases.Add(helo);

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
            var climb = new InitialClimbPhase { IsVfr = aircraft.FlightPlan.IsVfr, CruiseAltitude = aircraft.FlightPlan.CruiseAltitude };
            aircraft.Phases.Add(climb);

            var routeResult = ResolveDepartureRoute(ctopp.Departure, aircraft);
            SetInitialClimbProperties(climb, ctopp.Departure, ctopp.AssignedAltitude, routeResult, aircraft);
        }

        // Field elevation = current altitude (on ground)
        ctoppCtx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = ctoppCat,
            DeltaSeconds = 0,
            Runway = null,
            FieldElevation = aircraft.Altitude,
            GroundLayout = groundLayout,
            Logger = SimLog.CreateLogger("DepartureClearanceHandler"),
        };
        aircraft.Phases.Start(ctoppCtx);
        SyncControllerAssignedAltitude(aircraft, ClearanceType.ClearedForTakeoff, ctopp.AssignedAltitude);

        var msg = "Cleared for takeoff, present position";
        msg += FormatDepartureInstructionSuffix(ctopp.Departure);
        if (ctopp.AssignedAltitude is not null)
        {
            msg += $", climb and maintain {ctopp.AssignedAltitude:N0}";
        }
        return CommandDispatcher.Ok(msg);
    }
}
