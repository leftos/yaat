using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

internal static class PatternCommandHandler
{
    internal static CommandResult TryEnterPattern(
        AircraftState aircraft,
        PatternDirection direction,
        PatternEntryLeg entryLeg,
        ILogger logger,
        string? runwayId = null,
        double? finalDistanceNm = null,
        IRunwayLookup? runways = null
    )
    {
        // Resolve runway from argument if provided
        if (runwayId is not null && runways is not null)
        {
            var airportId = aircraft.Phases?.AssignedRunway?.AirportId ?? aircraft.Destination ?? aircraft.Departure;
            if (airportId is null)
            {
                return new CommandResult(false, "No airport context to resolve runway");
            }

            var resolved = runways.GetRunway(airportId, runwayId);
            if (resolved is null)
            {
                return new CommandResult(false, $"Runway {runwayId} not found at {airportId}");
            }

            aircraft.Phases ??= new PhaseList();
            aircraft.Phases.AssignedRunway = resolved;
        }

        if (aircraft.Phases?.AssignedRunway is null)
        {
            return new CommandResult(false, "No assigned runway for pattern entry");
        }

        var runway = aircraft.Phases.AssignedRunway;
        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        bool touchAndGo = aircraft.Phases.TrafficDirection is not null;

        // Detect if the aircraft is on the wrong side of the runway
        bool isOnWrongSide = false;
        PatternWaypoints? waypoints = null;

        if (entryLeg is PatternEntryLeg.Downwind or PatternEntryLeg.Base)
        {
            // Crosswind heading points toward the pattern side
            double crosswindHdg =
                direction == PatternDirection.Right
                    ? FlightPhysics.NormalizeHeading(runway.TrueHeading + 90.0)
                    : FlightPhysics.NormalizeHeading(runway.TrueHeading - 90.0);

            // Positive = pattern side, negative = wrong side
            double patternSideOffset = GeoMath.AlongTrackDistanceNm(
                aircraft.Latitude,
                aircraft.Longitude,
                runway.ThresholdLatitude,
                runway.ThresholdLongitude,
                crosswindHdg
            );

            isOnWrongSide = patternSideOffset < 0;

            if (isOnWrongSide)
            {
                waypoints = PatternGeometry.Compute(runway, category, direction);
            }
        }

        // Clear current phases and build new sequence from entry leg
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, logger);
        aircraft.Phases.Clear(ctx);

        // For wrong-side entry: midfield crossing then downwind entry
        PatternEntryLeg effectiveEntryLeg = isOnWrongSide ? PatternEntryLeg.Downwind : entryLeg;
        double? effectiveFinalDistanceNm = isOnWrongSide ? null : finalDistanceNm;

        // Compute waypoints for the entry point check
        waypoints ??= PatternGeometry.Compute(runway, category, direction);

        var circuitPhases = PatternBuilder.BuildCircuit(runway, category, direction, effectiveEntryLeg, touchAndGo, effectiveFinalDistanceNm);

        var phases = new PhaseList { AssignedRunway = runway };
        phases.LandingClearance = aircraft.Phases.LandingClearance;
        phases.ClearedRunwayId = aircraft.Phases.ClearedRunwayId;
        phases.TrafficDirection = aircraft.Phases.TrafficDirection;

        // If the aircraft is airborne and far from the pattern, insert a PatternEntryPhase
        // to navigate to the entry point with descent/climb to pattern altitude.
        if (!aircraft.IsOnGround && !isOnWrongSide)
        {
            var (entryLat, entryLon) = GetEntryPoint(waypoints, effectiveEntryLeg, effectiveFinalDistanceNm);
            double distToEntry = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, entryLat, entryLon);

            if (distToEntry > 1.0)
            {
                phases.Add(new PatternEntryPhase
                {
                    EntryLat = entryLat,
                    EntryLon = entryLon,
                    PatternAltitude = waypoints.PatternAltitude,
                });
            }
        }

        if (isOnWrongSide)
        {
            phases.Add(new MidfieldCrossingPhase { Waypoints = waypoints });
        }

        foreach (var phase in circuitPhases)
        {
            phases.Add(phase);
        }

        aircraft.Phases = phases;

        var dirStr = direction == PatternDirection.Left ? "left" : "right";
        var legStr = entryLeg.ToString().ToLowerInvariant();
        var distStr = finalDistanceNm is not null ? $", {finalDistanceNm:G}nm final" : "";
        var sideStr = isOnWrongSide ? " (crossing midfield)" : "";
        return CommandDispatcher.Ok($"Enter {dirStr} {legStr}{CommandDispatcher.RunwayLabel(aircraft)}{distStr}{sideStr}");
    }

    internal static CommandResult TryChangePatternDirection(
        AircraftState aircraft,
        PatternDirection newDirection,
        string? runwayId = null,
        IRunwayLookup? runways = null
    )
    {
        // Resolve runway from argument if provided
        if (runwayId is not null && runways is not null)
        {
            var airportId = aircraft.Phases?.AssignedRunway?.AirportId ?? aircraft.Destination ?? aircraft.Departure;
            if (airportId is null)
            {
                return new CommandResult(false, "No airport context to resolve runway");
            }

            var resolved = runways.GetRunway(airportId, runwayId);
            if (resolved is null)
            {
                return new CommandResult(false, $"Runway {runwayId} not found at {airportId}");
            }

            aircraft.Phases ??= new PhaseList();
            aircraft.Phases.AssignedRunway = resolved;
        }

        if (aircraft.Phases?.AssignedRunway is null)
        {
            return new CommandResult(false, "No assigned runway");
        }

        var runway = aircraft.Phases.AssignedRunway;
        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        var waypoints = PatternGeometry.Compute(runway, category, newDirection);

        // Set traffic direction — aircraft is now in pattern mode
        aircraft.Phases.TrafficDirection = newDirection;

        // Update waypoints on existing pattern phases
        bool hasPatternPhases = PatternBuilder.UpdateWaypoints(aircraft.Phases, waypoints);

        // If no pattern phases exist yet (e.g., after takeoff or go-around),
        // append a full pattern circuit after the current phase.
        if (!hasPatternPhases)
        {
            var circuit = PatternBuilder.BuildCircuit(runway, category, newDirection, PatternEntryLeg.Upwind, true);
            aircraft.Phases.InsertAfterCurrent(circuit);
        }
        else
        {
            // Replace any pending LandingPhase with TouchAndGoPhase
            // since the aircraft is now in pattern mode.
            // Don't override specific approach instructions (SG/LA).
            for (int i = 0; i < aircraft.Phases.Phases.Count; i++)
            {
                if (aircraft.Phases.Phases[i] is LandingPhase { Status: PhaseStatus.Pending })
                {
                    aircraft.Phases.Phases[i] = new TouchAndGoPhase();
                    break;
                }
            }
        }

        var dirStr = newDirection == PatternDirection.Left ? "left" : "right";
        return CommandDispatcher.Ok($"Make {dirStr} traffic{CommandDispatcher.RunwayLabel(aircraft)}");
    }

    /// <summary>
    /// Advance to the next phase when the current phase is of type T.
    /// Used for TC (skip upwind to crosswind) and TD (skip crosswind to downwind).
    /// </summary>
    internal static CommandResult TryPatternTurnTo<T>(AircraftState aircraft, string legName, ILogger logger)
        where T : Phase
    {
        if (aircraft.Phases?.CurrentPhase is T)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft, logger);
            aircraft.Phases.AdvanceToNext(ctx);
            return CommandDispatcher.Ok($"Turn {legName}");
        }

        return new CommandResult(false, $"Not on the leg before {legName}");
    }

    internal static CommandResult TryPatternTurnBase(AircraftState aircraft, ILogger logger)
    {
        if (aircraft.Phases?.CurrentPhase is DownwindPhase)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft, logger);
            aircraft.Phases.AdvanceToNext(ctx);
            return CommandDispatcher.Ok("Turn base");
        }

        return new CommandResult(false, "Not on downwind");
    }

    internal static CommandResult TryExtendPattern(AircraftState aircraft)
    {
        if (aircraft.Phases?.CurrentPhase is not DownwindPhase dw)
        {
            return new CommandResult(false, "Extend applies on downwind only");
        }

        dw.IsExtended = true;
        return CommandDispatcher.Ok("Extend downwind");
    }

    internal static CommandResult TryMakeShortApproach(AircraftState aircraft, ILogger logger)
    {
        if (aircraft.Phases?.CurrentPhase is DownwindPhase)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft, logger);
            aircraft.Phases.SkipTo<BasePhase>(ctx);
            return CommandDispatcher.Ok("Make short approach");
        }

        if (aircraft.Phases?.CurrentPhase is BasePhase bp)
        {
            bp.FinalDistanceNm = 0.5;
            return CommandDispatcher.Ok("Make short approach");
        }

        return new CommandResult(false, "Make short approach requires downwind or base leg");
    }

    internal static CommandResult TryMakeTurn(AircraftState aircraft, TurnDirection direction, double degrees, ILogger logger)
    {
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            return new CommandResult(false, "No active phase for turn");
        }

        var turnPhase = new MakeTurnPhase { Direction = direction, TargetDegrees = degrees };
        aircraft.Phases.InsertAfterCurrent(turnPhase);

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, logger);
        aircraft.Phases.AdvanceToNext(ctx);

        var dirStr = direction == TurnDirection.Left ? "left" : "right";
        return CommandDispatcher.Ok($"Make {dirStr} {degrees:F0}");
    }

    internal static CommandResult TrySetupTouchAndGo(AircraftState aircraft)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedTouchAndGo;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.Designator;
        EnsurePatternMode(aircraft.Phases);
        CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, new TouchAndGoPhase());

        return CommandDispatcher.Ok($"Cleared touch-and-go{CommandDispatcher.RunwayLabel(aircraft)}");
    }

    internal static CommandResult TrySetupStopAndGo(AircraftState aircraft)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedStopAndGo;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.Designator;
        EnsurePatternMode(aircraft.Phases);
        CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, new StopAndGoPhase());

        return CommandDispatcher.Ok($"Cleared stop-and-go{CommandDispatcher.RunwayLabel(aircraft)}");
    }

    internal static CommandResult TrySetupLowApproach(AircraftState aircraft)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedLowApproach;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.Designator;
        EnsurePatternMode(aircraft.Phases);
        CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, new LowApproachPhase());

        return CommandDispatcher.Ok($"Cleared low approach{CommandDispatcher.RunwayLabel(aircraft)}");
    }

    internal static CommandResult TrySetLandingClearance(AircraftState aircraft, ClearanceType clearanceType, string message)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        aircraft.Phases.LandingClearance = clearanceType;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.Designator;
        return CommandDispatcher.Ok(message);
    }

    internal static CommandResult TryHoldPresentPosition(AircraftState aircraft, TurnDirection? orbitDirection, ILogger logger)
    {
        var phase = new HoldPresentPositionPhase { OrbitDirection = orbitDirection };

        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft, logger);
            aircraft.Phases.Clear(ctx);
            aircraft.Phases = new PhaseList { AssignedRunway = aircraft.Phases.AssignedRunway };
        }
        else
        {
            aircraft.Phases = new PhaseList();
        }

        aircraft.Phases.Add(phase);
        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft, logger);
        aircraft.Phases.Start(startCtx);

        var dirStr = orbitDirection switch
        {
            TurnDirection.Left => "left 360s",
            TurnDirection.Right => "right 360s",
            _ => "hover",
        };
        return CommandDispatcher.Ok($"Hold present position, {dirStr}");
    }

    internal static CommandResult TryHoldAtFix(
        AircraftState aircraft,
        string fixName,
        double lat,
        double lon,
        TurnDirection? orbitDirection,
        ILogger logger
    )
    {
        var phase = new HoldAtFixPhase
        {
            FixName = fixName,
            FixLat = lat,
            FixLon = lon,
            OrbitDirection = orbitDirection,
        };

        RunwayInfo? runway = aircraft.Phases?.AssignedRunway;
        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft, logger);
            aircraft.Phases.Clear(ctx);
        }

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(phase);
        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft, logger);
        aircraft.Phases.Start(startCtx);

        var dirStr = orbitDirection switch
        {
            TurnDirection.Left => "left orbits",
            TurnDirection.Right => "right orbits",
            _ => "hover",
        };
        return CommandDispatcher.Ok($"Hold at {fixName}, {dirStr}");
    }

    /// <summary>
    /// Get the entry point coordinates for a given pattern entry leg.
    /// For base with a custom final distance, computes the point on the
    /// extended centerline offset laterally by pattern size.
    /// </summary>
    private static (double Lat, double Lon) GetEntryPoint(
        PatternWaypoints wp, PatternEntryLeg leg, double? finalDistanceNm)
    {
        if (leg == PatternEntryLeg.Base && finalDistanceNm is not null)
        {
            // Compute a base entry point at the custom final distance
            double reciprocal = ((wp.FinalHeading + 180.0) % 360.0 + 360.0) % 360.0;
            var finalPoint = GeoMath.ProjectPoint(wp.ThresholdLat, wp.ThresholdLon, reciprocal, finalDistanceNm.Value);
            // Offset laterally by the pattern width (same direction as BaseTurn from threshold)
            double baseOffset = GeoMath.DistanceNm(wp.ThresholdLat, wp.ThresholdLon, wp.BaseTurnLat, wp.BaseTurnLon);
            double lateralBearing = GeoMath.BearingTo(wp.ThresholdLat, wp.ThresholdLon, wp.BaseTurnLat, wp.BaseTurnLon);
            var entryPoint = GeoMath.ProjectPoint(finalPoint.Lat, finalPoint.Lon, lateralBearing, baseOffset);
            return (entryPoint.Lat, entryPoint.Lon);
        }

        return leg switch
        {
            // AIM 4-3-3: enter downwind at midfield (abeam the threshold), not at the departure end.
            // The standard 45° entry joins the downwind leg abeam the runway midpoint.
            PatternEntryLeg.Downwind => (wp.DownwindAbeamLat, wp.DownwindAbeamLon),
            PatternEntryLeg.Base => (wp.BaseTurnLat, wp.BaseTurnLon),
            PatternEntryLeg.Final => (wp.ThresholdLat, wp.ThresholdLon),
            PatternEntryLeg.Upwind => (wp.DepartureEndLat, wp.DepartureEndLon),
            _ => (wp.DownwindAbeamLat, wp.DownwindAbeamLon),
        };
    }

    /// <summary>
    /// Ensure the aircraft is in pattern mode. If TrafficDirection is not set,
    /// infer direction from existing pattern phases or default to Left.
    /// </summary>
    private static void EnsurePatternMode(PhaseList phases)
    {
        if (phases.TrafficDirection is not null)
        {
            return;
        }

        // Infer from existing pattern phases
        foreach (var phase in phases.Phases)
        {
            if (phase is DownwindPhase { Waypoints: not null } dw)
            {
                phases.TrafficDirection = dw.Waypoints.Direction;
                return;
            }
            if (phase is BasePhase { Waypoints: not null } bp)
            {
                phases.TrafficDirection = bp.Waypoints.Direction;
                return;
            }
        }

        phases.TrafficDirection = PatternDirection.Left;
    }
}
