using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

internal static class DepartureClearanceHandler
{
    internal static CommandResult TryClearedForTakeoff(
        ClearedForTakeoffCommand cto,
        AircraftState aircraft,
        LinedUpAndWaitingPhase luaw,
        IFixLookup? fixes
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
            var departureRoute = ResolveDepartureRoute(cto.Departure, aircraft, fixes);

            foreach (var p in aircraft.Phases.Phases)
            {
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
                    SetInitialClimbProperties(climb, cto.Departure, cto.AssignedAltitude, departureRoute, aircraft);
                }
            }

            // ClosedTrafficDeparture: establish pattern mode and append circuit
            if (cto.Departure is ClosedTrafficDeparture ct)
            {
                aircraft.Phases.TrafficDirection = ct.Direction;
                var runway = aircraft.Phases.AssignedRunway;
                if (runway is not null)
                {
                    var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
                    var circuit = PatternBuilder.BuildCircuit(runway, cat, ct.Direction, PatternEntryLeg.Upwind, true);
                    aircraft.Phases.Phases.AddRange(circuit);
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
        ILogger logger
    )
    {
        if (currentPhase is HoldingShortPhase holding)
        {
            return LineUpFromHoldShort(aircraft, holding, clearanceType, departure, assignedAltitude, runways, fixes, logger);
        }

        if (currentPhase is TaxiingPhase)
        {
            return StoreDepartureClearanceDuringTaxi(aircraft, clearanceType, departure, assignedAltitude, runways, fixes);
        }

        // Aircraft is lining up — CTO can pre-satisfy the upcoming LUAW phase
        if (currentPhase is LineUpPhase && clearanceType == ClearanceType.ClearedForTakeoff)
        {
            return SatisfyUpcomingTakeoffClearance(aircraft, departure, assignedAltitude, fixes, logger);
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
        ILogger logger
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

        // Satisfy the runway crossing clearance so HoldingShortPhase completes
        holding.SatisfyClearance(ClearanceType.RunwayCrossing);

        // Set the assigned runway and insert tower phases
        aircraft.Phases!.AssignedRunway = runway;
        InsertTowerPhasesAfterCurrent(aircraft, clearanceType, departure, assignedAltitude, runway, fixes, holding.HoldShort.NodeId, logger);

        return BuildDepartureMessage(clearanceType, runway.Designator, departure, assignedAltitude);
    }

    internal static CommandResult StoreDepartureClearanceDuringTaxi(
        AircraftState aircraft,
        ClearanceType clearanceType,
        DepartureInstruction departure,
        int? assignedAltitude,
        IRunwayLookup? runways,
        IFixLookup? fixes
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

        // Pre-clear so aircraft doesn't stop at the hold-short
        depHoldShort.IsCleared = true;

        // Pre-resolve navigation targets for route-based departures
        var departureRoute = ResolveDepartureRoute(departure, aircraft, fixes);

        // Set runway and store departure clearance for TaxiingPhase to consume
        aircraft.Phases!.AssignedRunway = runway;
        aircraft.Phases.DepartureClearance = new DepartureClearanceInfo
        {
            Type = clearanceType,
            Departure = departure,
            AssignedAltitude = assignedAltitude,
            DepartureRoute = departureRoute,
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
        ILogger logger
    )
    {
        var departureRoute = ResolveDepartureRoute(departure, aircraft, fixes);

        var lineup = new LineUpPhase(holdShortNodeId);
        var luawPhase = new LinedUpAndWaitingPhase();
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        bool isHeli = cat == AircraftCategory.Helicopter;
        Phase takeoffPhase = isHeli ? new HelicopterTakeoffPhase() : new TakeoffPhase();
        var climb = new InitialClimbPhase
        {
            Departure = departure,
            AssignedAltitude = assignedAltitude,
            DepartureRoute = departureRoute,
            IsVfr = aircraft.IsVfr,
            CruiseAltitude = aircraft.CruiseAltitude,
        };
        aircraft.Phases!.InsertAfterCurrent(new Phase[] { lineup, luawPhase, takeoffPhase, climb });

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
                var circuit = PatternBuilder.BuildCircuit(runway, cat, ct.Direction, PatternEntryLeg.Upwind, true);
                aircraft.Phases.Phases.AddRange(circuit);
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

        var departureRoute = ResolveDepartureRoute(departure, aircraft, fixes);

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
            SetInitialClimbProperties(climb, departure, assignedAltitude, departureRoute, aircraft);
        }

        if (departure is ClosedTrafficDeparture ct && phases.AssignedRunway is { } rwy)
        {
            phases.TrafficDirection = ct.Direction;
            var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
            var circuit = PatternBuilder.BuildCircuit(rwy, cat, ct.Direction, PatternEntryLeg.Upwind, true);
            phases.Phases.AddRange(circuit);
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
            ClosedTrafficDeparture ct => $", make {(ct.Direction == PatternDirection.Left ? "left" : "right")} traffic",
            _ => "",
        };
    }

    /// <summary>
    /// Pre-resolves navigation targets for route-based departure instructions.
    /// Keeps IFixLookup out of the phase layer.
    /// </summary>
    internal static List<NavigationTarget>? ResolveDepartureRoute(DepartureInstruction departure, AircraftState aircraft, IFixLookup? fixes)
    {
        if (fixes is null)
        {
            return null;
        }

        switch (departure)
        {
            case DirectFixDeparture dfd:
                return
                [
                    new NavigationTarget
                    {
                        Name = dfd.FixName,
                        Latitude = dfd.Lat,
                        Longitude = dfd.Lon,
                    },
                ];

            case OnCourseDeparture when aircraft.Destination is not null:
            {
                var pos = fixes.GetFixPosition(aircraft.Destination);
                if (pos is null)
                {
                    return null;
                }
                return
                [
                    new NavigationTarget
                    {
                        Name = aircraft.Destination,
                        Latitude = pos.Value.Lat,
                        Longitude = pos.Value.Lon,
                    },
                ];
            }

            case DefaultDeparture when !aircraft.IsVfr && aircraft.Route is not null:
            {
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

                return targets.Count > 0 ? targets : null;
            }

            default:
                return null;
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
        List<NavigationTarget>? departureRoute,
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
            DepartureRoute = departureRoute,
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
}
