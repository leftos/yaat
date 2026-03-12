using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

internal sealed record DepartureRouteResult(List<NavigationTarget> Targets, string? SidId);

internal static class DepartureClearanceHandler
{
    internal static CommandResult TryClearedForTakeoff(
        ClearedForTakeoffCommand cto,
        AircraftState aircraft,
        LinedUpAndWaitingPhase luaw,
        IFixLookup? fixes,
        IProcedureLookup? procedures = null,
        IRunwayLookup? runways = null
    )
    {
        if (aircraft.Phases?.AssignedRunway is null)
        {
            return new CommandResult(false, "No runway assigned — cannot clear for takeoff");
        }

        luaw.Departure = cto.Departure;
        luaw.AssignedAltitude = cto.AssignedAltitude;
        luaw.SatisfyClearance(ClearanceType.ClearedForTakeoff);

        // Propagate departure to TakeoffPhase and InitialClimbPhase
        if (aircraft.Phases is not null)
        {
            var routeResult = ResolveDepartureRoute(cto.Departure, aircraft, fixes, procedures);

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
            // Remove InitialClimbPhase — UpwindPhase handles climb to pattern altitude.
            if (cto.Departure is ClosedTrafficDeparture ct)
            {
                aircraft.Phases.TrafficDirection = ct.Direction;
                aircraft.Phases.Phases.RemoveAll(p => p is InitialClimbPhase { Status: PhaseStatus.Pending });

                var patternRunway = ResolvePatternRunway(ct, aircraft, runways) ?? aircraft.Phases.AssignedRunway;
                if (patternRunway is not null)
                {
                    var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
                    var circuit = PatternBuilder.BuildCircuit(patternRunway, cat, ct.Direction, PatternEntryLeg.Upwind, true);
                    aircraft.Phases.Phases.AddRange(circuit);
                    aircraft.Phases.PatternRunway = patternRunway;
                }
            }
        }

        return BuildDepartureMessage(
            ClearanceType.ClearedForTakeoff,
            aircraft.Phases?.AssignedRunway?.Designator ?? "unknown",
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
        IRunwayLookup? runways,
        IFixLookup? fixes,
        ILogger logger,
        IProcedureLookup? procedureLookup
    )
    {
        if (currentPhase is HoldingShortPhase holding)
        {
            return LineUpFromHoldShort(aircraft, holding, clearanceType, departure, assignedAltitude, runways, fixes, logger, procedureLookup);
        }

        if (currentPhase is TaxiingPhase)
        {
            return StoreDepartureClearanceDuringTaxi(aircraft, clearanceType, departure, assignedAltitude, runways, fixes, procedureLookup);
        }

        // Aircraft is lining up — CTO can pre-satisfy the upcoming LUAW phase
        if (currentPhase is LineUpPhase && clearanceType == ClearanceType.ClearedForTakeoff)
        {
            return SatisfyUpcomingTakeoffClearance(aircraft, departure, assignedAltitude, fixes, logger, runways, procedureLookup);
        }

        // Aircraft holding in position (e.g. after WARPG) — allow LUAW/CTO with assigned runway
        if (currentPhase is HoldingInPositionPhase)
        {
            return LineUpFromPosition(aircraft, clearanceType, departure, assignedAltitude, runways, fixes, logger, procedureLookup);
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
        IRunwayLookup? runways,
        IFixLookup? fixes,
        ILogger logger,
        IProcedureLookup? procedureLookup
    )
    {
        var runwayId = holding.HoldShort.TargetName;
        if (runwayId is null)
        {
            return new CommandResult(false, "Hold short point has no runway assigned");
        }

        var runway = CommandDispatcher.ResolveRunway(aircraft, runwayId, runways);
        if (runway is null)
        {
            return new CommandResult(false, $"Cannot resolve runway {runwayId}");
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
        aircraft.DepartureRunway = runway.Designator;
        InsertTowerPhasesAfterCurrent(
            aircraft,
            clearanceType,
            departure,
            assignedAltitude,
            runway,
            fixes,
            holding.HoldShort.NodeId,
            logger,
            runways,
            procedureLookup
        );

        return BuildDepartureMessage(clearanceType, runway.Designator, departure, assignedAltitude);
    }

    internal static CommandResult LineUpFromPosition(
        AircraftState aircraft,
        ClearanceType clearanceType,
        DepartureInstruction departure,
        int? assignedAltitude,
        IRunwayLookup? runways,
        IFixLookup? fixes,
        ILogger logger,
        IProcedureLookup? procedureLookup
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
        aircraft.DepartureRunway = runway.Designator;
        InsertTowerPhasesAfterCurrent(aircraft, clearanceType, departure, assignedAltitude, runway, fixes, null, logger, runways, procedureLookup);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft));

        return BuildDepartureMessage(clearanceType, runway.Designator, departure, assignedAltitude);
    }

    internal static CommandResult StoreDepartureClearanceDuringTaxi(
        AircraftState aircraft,
        ClearanceType clearanceType,
        DepartureInstruction departure,
        int? assignedAltitude,
        IRunwayLookup? runways,
        IFixLookup? fixes,
        IProcedureLookup? procedureLookup
    )
    {
        var route = aircraft.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        // Find the destination runway hold-short (last one in the route)
        HoldShortPoint? depHoldShort = null;
        foreach (var hs in route.HoldShortPoints)
        {
            if (hs.Reason is HoldShortReason.DestinationRunway or HoldShortReason.ExplicitHoldShort)
            {
                depHoldShort = hs;
            }
        }

        if (depHoldShort?.TargetName is null)
        {
            return new CommandResult(false, "No departure runway hold-short in taxi route");
        }

        var runway = CommandDispatcher.ResolveRunway(aircraft, depHoldShort.TargetName, runways);
        if (runway is null)
        {
            return new CommandResult(false, $"Cannot resolve runway {depHoldShort.TargetName}");
        }

        // Pre-clear the destination hold-short and any runway crossings for the same runway.
        // The route may cross the departure runway before reaching the destination hold-short
        // (e.g., taxiway B at OAK crosses 28L/10R before reaching the 28L threshold).
        depHoldShort.IsCleared = true;
        foreach (var hs in route.HoldShortPoints)
        {
            if (
                hs.Reason == HoldShortReason.RunwayCrossing
                && hs.TargetName is not null
                && hs.TargetName.Contains(runway.Designator, StringComparison.OrdinalIgnoreCase)
            )
            {
                hs.IsCleared = true;
            }
        }

        // Pre-resolve navigation targets for route-based departures
        var routeResult = ResolveDepartureRoute(departure, aircraft, fixes, procedureLookup);

        // Pre-resolve pattern runway for cross-runway closed traffic
        RunwayInfo? patternRunway = null;
        if (departure is ClosedTrafficDeparture ctDep)
        {
            patternRunway = ResolvePatternRunway(ctDep, aircraft, runways);
        }

        // Set runway and store departure clearance for TaxiingPhase to consume
        aircraft.Phases!.AssignedRunway = runway;
        aircraft.DepartureRunway = runway.Designator;
        aircraft.Phases.DepartureClearance = new DepartureClearanceInfo
        {
            Type = clearanceType,
            Departure = departure,
            AssignedAltitude = assignedAltitude,
            DepartureRoute = routeResult?.Targets,
            DepartureSidId = routeResult?.SidId,
            PatternRunway = patternRunway,
        };

        return BuildDepartureMessage(clearanceType, runway.Designator, departure, assignedAltitude);
    }

    internal static void InsertTowerPhasesAfterCurrent(
        AircraftState aircraft,
        ClearanceType clearanceType,
        DepartureInstruction departure,
        int? assignedAltitude,
        RunwayInfo runway,
        IFixLookup? fixes,
        int? holdShortNodeId,
        ILogger logger,
        IRunwayLookup? runways,
        IProcedureLookup? procedureLookup
    )
    {
        var routeResult = ResolveDepartureRoute(departure, aircraft, fixes, procedureLookup);

        var lineup = new LineUpPhase(holdShortNodeId);
        var luawPhase = new LinedUpAndWaitingPhase();
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        bool isHeli = cat == AircraftCategory.Helicopter;
        Phase takeoffPhase = isHeli ? new HelicopterTakeoffPhase() : new TakeoffPhase();
        // For closed traffic, skip InitialClimbPhase — UpwindPhase handles the
        // climb to pattern altitude. The crosswind turn is position-based (departure end),
        // not altitude-based.
        bool isClosedTraffic = departure is ClosedTrafficDeparture;
        Phase[] towerPhases;
        if (isClosedTraffic)
        {
            towerPhases = [lineup, luawPhase, takeoffPhase];
        }
        else
        {
            var climb = new InitialClimbPhase
            {
                Departure = departure,
                AssignedAltitude = assignedAltitude,
                DepartureRoute = routeResult?.Targets,
                DepartureSidId = routeResult?.SidId,
                IsVfr = aircraft.IsVfr,
                CruiseAltitude = aircraft.CruiseAltitude,
            };
            towerPhases = [lineup, luawPhase, takeoffPhase, climb];
        }

        // Replace (not insert) — remaining taxi phases (CrossingRunwayPhase, etc.)
        // must not execute after the aircraft is airborne.
        aircraft.Phases!.ReplaceUpcoming(towerPhases);

        if (clearanceType == ClearanceType.ClearedForTakeoff)
        {
            luawPhase.SatisfyClearance(ClearanceType.ClearedForTakeoff);
            luawPhase.Departure = departure;
            luawPhase.AssignedAltitude = assignedAltitude;
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
                aircraft.Phases.TrafficDirection = ct.Direction;
                var patternRunway = ResolvePatternRunway(ct, aircraft, runways) ?? runway;
                var circuit = PatternBuilder.BuildCircuit(patternRunway, cat, ct.Direction, PatternEntryLeg.Upwind, true);
                aircraft.Phases.Phases.AddRange(circuit);
                aircraft.Phases.PatternRunway = patternRunway;
            }
        }

        logger.LogDebug(
            "[Departure] {Callsign}: tower phases inserted ({Clearance}), runway {Rwy}",
            aircraft.Callsign,
            clearanceType,
            runway.Designator
        );
    }

    internal static CommandResult SatisfyUpcomingTakeoffClearance(
        AircraftState aircraft,
        DepartureInstruction departure,
        int? assignedAltitude,
        IFixLookup? fixes,
        ILogger logger,
        IRunwayLookup? runways,
        IProcedureLookup? procedureLookup
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

        var routeResult = ResolveDepartureRoute(departure, aircraft, fixes, procedureLookup);

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

        if (departure is ClosedTrafficDeparture ct && phases.AssignedRunway is { } rwy)
        {
            phases.TrafficDirection = ct.Direction;
            // Remove InitialClimbPhase — UpwindPhase handles climb to pattern altitude
            phases.Phases.RemoveAll(p => p is InitialClimbPhase { Status: PhaseStatus.Pending });
            var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
            var patternRunway = ResolvePatternRunway(ct, aircraft, runways) ?? rwy;
            var circuit = PatternBuilder.BuildCircuit(patternRunway, cat, ct.Direction, PatternEntryLeg.Upwind, true);
            phases.Phases.AddRange(circuit);
            phases.PatternRunway = patternRunway;
        }

        logger.LogDebug("[Departure] {Callsign}: CTO satisfied on upcoming LUAW phase", aircraft.Callsign);

        return BuildDepartureMessage(ClearanceType.ClearedForTakeoff, phases.AssignedRunway?.Designator ?? "unknown", departure, assignedAltitude);
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
            RunwayHeadingDeparture => ", fly runway heading",
            RelativeTurnDeparture { Degrees: 90, Direction: TurnDirection.Right } => ", right crosswind departure",
            RelativeTurnDeparture { Degrees: 90, Direction: TurnDirection.Left } => ", left crosswind departure",
            RelativeTurnDeparture { Degrees: 180, Direction: TurnDirection.Right } => ", right downwind departure",
            RelativeTurnDeparture { Degrees: 180, Direction: TurnDirection.Left } => ", left downwind departure",
            RelativeTurnDeparture rel => $", turn {(rel.Direction == TurnDirection.Right ? "right" : "left")} {rel.Degrees} degrees",
            FlyHeadingDeparture fh when fh.Direction is TurnDirection.Right => $", turn right heading {fh.Heading:000}",
            FlyHeadingDeparture fh when fh.Direction is TurnDirection.Left => $", turn left heading {fh.Heading:000}",
            FlyHeadingDeparture fh => $", fly heading {fh.Heading:000}",
            OnCourseDeparture => ", on course",
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
    internal static RunwayInfo? ResolvePatternRunway(ClosedTrafficDeparture ct, AircraftState aircraft, IRunwayLookup? runways)
    {
        if (ct.RunwayId is null || runways is null)
        {
            return null;
        }

        return CommandDispatcher.ResolveRunway(aircraft, ct.RunwayId, runways);
    }

    /// <summary>
    /// Pre-resolves navigation targets for route-based departure instructions.
    /// Keeps IFixLookup out of the phase layer.
    /// </summary>
    internal static DepartureRouteResult? ResolveDepartureRoute(
        DepartureInstruction departure,
        AircraftState aircraft,
        IFixLookup? fixes,
        IProcedureLookup? procedures
    )
    {
        if (fixes is null)
        {
            return null;
        }

        switch (departure)
        {
            case DirectFixDeparture dfd:
                return new DepartureRouteResult(
                    [
                        new NavigationTarget
                        {
                            Name = dfd.FixName,
                            Latitude = dfd.Lat,
                            Longitude = dfd.Lon,
                        },
                    ],
                    null
                );

            case OnCourseDeparture when aircraft.Destination is not null:
            {
                var pos = fixes.GetFixPosition(aircraft.Destination);
                if (pos is null)
                {
                    return null;
                }
                return new DepartureRouteResult(
                    [
                        new NavigationTarget
                        {
                            Name = aircraft.Destination,
                            Latitude = pos.Value.Lat,
                            Longitude = pos.Value.Lon,
                        },
                    ],
                    null
                );
            }

            case DefaultDeparture when !aircraft.IsVfr && aircraft.Route is not null:
            {
                // Try CIFP SID first for constrained navigation targets
                var cifpResult = TryResolveSidFromCifp(aircraft, fixes, procedures);
                if (cifpResult is not null)
                {
                    return cifpResult;
                }

                // Fallback to NavData body-fix expansion (lateral path only, no constraints)
                var expanded = fixes.ExpandRouteForNavigation(aircraft.Route, aircraft.Departure);
                var targets = new List<NavigationTarget>();

                // Resolve fix positions, skipping unknown fixes
                var airportPos = aircraft.Departure is not null ? fixes.GetFixPosition(aircraft.Departure) : null;

                foreach (var name in expanded)
                {
                    var pos = fixes.GetFixPosition(name);
                    if (pos is null)
                    {
                        continue;
                    }

                    targets.Add(
                        new NavigationTarget
                        {
                            Name = name,
                            Latitude = pos.Value.Lat,
                            Longitude = pos.Value.Lon,
                        }
                    );
                }

                // Safety net: strip leading targets within 1nm of departure
                if (airportPos is not null)
                {
                    while (targets.Count > 0)
                    {
                        double dist = GeoMath.DistanceNm(airportPos.Value.Lat, airportPos.Value.Lon, targets[0].Latitude, targets[0].Longitude);
                        if (dist > 1.0)
                        {
                            break;
                        }

                        targets.RemoveAt(0);
                    }
                }

                return targets.Count > 0 ? new DepartureRouteResult(targets, null) : null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Attempts to resolve a SID from CIFP data. Extracts SID name from the first
    /// route token, selects the runway transition matching the assigned runway,
    /// and builds an ordered leg sequence with altitude/speed constraints.
    /// Appends remaining enroute fixes from the filed route after the SID.
    /// Returns null if CIFP data is unavailable or SID cannot be resolved.
    /// </summary>
    internal static DepartureRouteResult? TryResolveSidFromCifp(AircraftState aircraft, IFixLookup fixes, IProcedureLookup? procedures)
    {
        if (procedures is null || aircraft.Route is null || aircraft.Departure is null)
        {
            return null;
        }

        var routeTokens = aircraft.Route.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (routeTokens.Length == 0)
        {
            return null;
        }

        // First route token is the SID name
        var sidName = routeTokens[0];
        var sid = procedures.GetSid(aircraft.Departure, sidName);
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

        // If the route specifies an enroute transition (second token matches a transition name)
        if (routeTokens.Length > 1)
        {
            var enrouteKey = routeTokens[1].ToUpperInvariant();
            if (sid.EnrouteTransitions.TryGetValue(enrouteKey, out var enTransition))
            {
                orderedLegs.AddRange(enTransition.Legs);
            }
        }

        if (orderedLegs.Count == 0)
        {
            return null;
        }

        // Convert SID legs to NavigationTargets with constraints
        var targets = ResolveLegsToTargets(orderedLegs, fixes);
        if (targets.Count == 0)
        {
            return null;
        }

        // Append remaining enroute fixes from filed route (post-SID tokens) without constraints.
        // Skip the SID token and any enroute transition token that was consumed.
        int startIdx = 1;
        if (routeTokens.Length > 1)
        {
            var secondToken = routeTokens[1].ToUpperInvariant();
            if (sid.EnrouteTransitions.ContainsKey(secondToken))
            {
                startIdx = 2;
            }
        }

        var lastSidFix = targets[^1].Name;
        bool pastSidFix = false;
        for (int i = startIdx; i < routeTokens.Length; i++)
        {
            var token = routeTokens[i];
            if (double.TryParse(token, out _))
            {
                continue;
            }

            // Skip until we're past the last SID fix to avoid duplicates
            if (!pastSidFix)
            {
                if (string.Equals(token, lastSidFix, StringComparison.OrdinalIgnoreCase))
                {
                    pastSidFix = true;
                }
                continue;
            }

            var pos = fixes.GetFixPosition(token);
            if (pos is not null)
            {
                targets.Add(
                    new NavigationTarget
                    {
                        Name = token.ToUpperInvariant(),
                        Latitude = pos.Value.Lat,
                        Longitude = pos.Value.Lon,
                    }
                );
            }
        }

        // Safety net: strip leading targets within 1nm of departure
        var airportPos = fixes.GetFixPosition(aircraft.Departure);
        if (airportPos is not null)
        {
            while (targets.Count > 0)
            {
                double dist = GeoMath.DistanceNm(airportPos.Value.Lat, airportPos.Value.Lon, targets[0].Latitude, targets[0].Longitude);
                if (dist > 1.0)
                {
                    break;
                }
                targets.RemoveAt(0);
            }
        }

        return targets.Count > 0 ? new DepartureRouteResult(targets, sid.ProcedureId) : null;
    }

    /// <summary>
    /// Converts CIFP legs to NavigationTargets with altitude/speed constraints.
    /// Resolves fix positions, skips unknown fixes, carries restrictions.
    /// RF/AF legs are expanded into intermediate arc waypoints.
    /// PI (procedure turn) legs are skipped in SID/STAR context.
    /// </summary>
    internal static List<NavigationTarget> ResolveLegsToTargets(IReadOnlyList<CifpLeg> legs, IFixLookup fixes)
    {
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

            var pos = fixes.GetFixPosition(leg.FixIdentifier);
            if (pos is null)
            {
                continue;
            }

            // Deduplicate adjacent identical fix names
            if (targets.Count > 0 && string.Equals(targets[^1].Name, leg.FixIdentifier, StringComparison.OrdinalIgnoreCase))
            {
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
                var navaidPos = fixes.GetFixPosition(leg.RecommendedNavaidId);
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
                    Latitude = pos.Value.Lat,
                    Longitude = pos.Value.Lon,
                    AltitudeRestriction = leg.Altitude,
                    SpeedRestriction = leg.Speed,
                    IsFlyOver =
                        leg.IsFlyOver
                        || leg.FixRole is CifpFixRole.FAF or CifpFixRole.MAHP
                        || leg.PathTerminator is CifpPathTerminator.HA or CifpPathTerminator.HF or CifpPathTerminator.HM,
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
            targets.Add(
                new NavigationTarget
                {
                    Name = $"ARC{i + 1:D2}",
                    Latitude = arcPoints[i].Lat,
                    Longitude = arcPoints[i].Lon,
                }
            );
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
            IsVfr = aircraft.IsVfr,
            CruiseAltitude = aircraft.CruiseAltitude,
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
            return CommandDispatcher.Ok($"Takeoff clearance cancelled, hold position{CommandDispatcher.RunwayLabel(aircraft)}");
        }
        if (currentPhase is TakeoffPhase && aircraft.IsOnGround)
        {
            // Abort takeoff during ground roll
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases?.Clear(ctx);
            aircraft.Phases = null;
            aircraft.Targets.TargetSpeed = 0;
            return CommandDispatcher.Ok("Abort takeoff, hold position");
        }
        return new CommandResult(false, "No takeoff clearance to cancel");
    }

    internal static CommandResult TryClearedTakeoffPresent(AircraftState aircraft, AirportGroundLayout? groundLayout)
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

        aircraft.IsHeld = false;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HelicopterTakeoffPhase());
        aircraft.Phases.Add(new InitialClimbPhase { IsVfr = aircraft.IsVfr, CruiseAltitude = aircraft.CruiseAltitude });

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

        return CommandDispatcher.Ok("Cleared for takeoff, present position");
    }
}
