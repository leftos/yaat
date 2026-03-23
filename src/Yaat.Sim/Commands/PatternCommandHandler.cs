using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

internal static class PatternCommandHandler
{
    internal static CommandResult TryEnterPattern(
        AircraftState aircraft,
        PatternDirection direction,
        PatternEntryLeg entryLeg,
        string? runwayId,
        double? finalDistanceNm
    )
    {
        // Resolve runway from argument if provided
        if (runwayId is not null)
        {
            var airportId = aircraft.Phases?.AssignedRunway?.AirportId ?? aircraft.Destination;
            if (airportId is null)
            {
                return new CommandResult(false, "No airport context to resolve runway");
            }

            var resolved = NavigationDatabase.Instance.GetRunway(airportId, runwayId);
            if (resolved is null)
            {
                return new CommandResult(false, $"Runway {runwayId} not found at {airportId}");
            }

            aircraft.Phases ??= new PhaseList();
            aircraft.Phases.AssignedRunway = resolved;
            aircraft.DestinationRunway = resolved.Designator;
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
            TrueHeading crosswindHdg = direction == PatternDirection.Right ? runway.TrueHeading + 90.0 : runway.TrueHeading - 90.0;

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
                var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
                waypoints = PatternGeometry.Compute(
                    runway,
                    category,
                    direction,
                    aircraft.PatternSizeOverrideNm,
                    aircraft.PatternAltitudeOverrideFt,
                    airportRunways
                );
            }
        }

        // Clear current phases and build new sequence from entry leg
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Clear(ctx);

        // For wrong-side entry: midfield crossing then downwind entry
        PatternEntryLeg effectiveEntryLeg = isOnWrongSide ? PatternEntryLeg.Downwind : entryLeg;
        double? effectiveFinalDistanceNm = isOnWrongSide ? null : finalDistanceNm;

        // If the aircraft is airborne, heading roughly aligned with the runway,
        // near the runway (within 3nm of departure end), and NOT already close to
        // the downwind entry, override to upwind entry. This handles the go-around
        // case where the pilot should fly upwind→crosswind→downwind instead of
        // navigating directly to the downwind abeam point at the wrong heading.
        if (!aircraft.IsOnGround && !isOnWrongSide && effectiveEntryLeg == PatternEntryLeg.Downwind && waypoints is not null)
        {
            double hdgDiff = aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading);
            double distToDepEnd = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, runway.EndLatitude, runway.EndLongitude);
            double distToDownwindEntry = GeoMath.DistanceNm(
                aircraft.Latitude,
                aircraft.Longitude,
                waypoints.DownwindAbeamLat,
                waypoints.DownwindAbeamLon
            );
            if (hdgDiff < 30 && distToDepEnd < 3.0 && distToDownwindEntry > 1.0)
            {
                effectiveEntryLeg = PatternEntryLeg.Upwind;
            }
        }

        // Compute waypoints for the entry point check
        {
            var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
            waypoints ??= PatternGeometry.Compute(
                runway,
                category,
                direction,
                aircraft.PatternSizeOverrideNm,
                aircraft.PatternAltitudeOverrideFt,
                airportRunways
            );
        }

        var circuitPhases = PatternBuilder.BuildCircuit(
            runway,
            category,
            direction,
            effectiveEntryLeg,
            touchAndGo,
            effectiveFinalDistanceNm,
            aircraft.PatternSizeOverrideNm,
            aircraft.PatternAltitudeOverrideFt,
            NavigationDatabase.Instance.GetRunways(runway.AirportId)
        );

        var phases = new PhaseList { AssignedRunway = runway };
        aircraft.DestinationRunway = runway.Designator;
        phases.LandingClearance = aircraft.Phases.LandingClearance;
        phases.ClearedRunwayId = aircraft.Phases.ClearedRunwayId;
        phases.TrafficDirection = aircraft.Phases.TrafficDirection;

        // If the aircraft is airborne and far from the pattern, insert a PatternEntryPhase
        // to navigate to the entry point with descent/climb to pattern altitude.
        // For downwind entry, add a lead-in waypoint so the aircraft aligns with the
        // downwind heading before reaching the abeam point.
        if (!aircraft.IsOnGround && !isOnWrongSide)
        {
            var (entryLat, entryLon) = GetEntryPoint(waypoints, effectiveEntryLeg, effectiveFinalDistanceNm, category);
            double distToEntry = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, entryLat, entryLon);

            if (distToEntry > 1.0)
            {
                double? leadInLat = null;
                double? leadInLon = null;

                // For downwind entry: add a lead-in waypoint ~1.5nm before the abeam point
                // along the reverse downwind track, so the aircraft arrives aligned.
                if (effectiveEntryLeg == PatternEntryLeg.Downwind)
                {
                    TrueHeading reverseDownwind = waypoints.DownwindHeading.ToReciprocal();
                    var leadIn = GeoMath.ProjectPoint(entryLat, entryLon, reverseDownwind, 1.5);
                    leadInLat = leadIn.Lat;
                    leadInLon = leadIn.Lon;
                }

                // For final entry, target the glideslope altitude at the entry point
                // (not TPA). FinalApproachPhase handles glideslope tracking from there.
                double entryAltitude = waypoints.PatternAltitude;
                if (effectiveEntryLeg == PatternEntryLeg.Final)
                {
                    double entryDist = GeoMath.DistanceNm(entryLat, entryLon, waypoints.ThresholdLat, waypoints.ThresholdLon);
                    double gsAngle = GlideSlopeGeometry.AngleForCategory(category);
                    entryAltitude = GlideSlopeGeometry.AltitudeAtDistance(entryDist, runway.ElevationFt, gsAngle);
                }

                phases.Add(
                    new PatternEntryPhase
                    {
                        EntryLat = entryLat,
                        EntryLon = entryLon,
                        PatternAltitude = entryAltitude,
                        LeadInLat = leadInLat,
                        LeadInLon = leadInLon,
                    }
                );
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

        var legDesc =
            entryLeg == PatternEntryLeg.Final
                ? "final"
                : $"{(direction == PatternDirection.Left ? "left" : "right")} {entryLeg.ToString().ToLowerInvariant()}";
        var distStr = finalDistanceNm is not null ? $", {finalDistanceNm:G}nm final" : "";
        var sideStr = isOnWrongSide ? " (crossing midfield)" : "";
        return CommandDispatcher.Ok($"Enter {legDesc}{CommandDispatcher.RunwayLabel(aircraft)}{distStr}{sideStr}");
    }

    internal static CommandResult TryChangePatternDirection(
        AircraftState aircraft,
        PatternDirection newDirection,
        string? runwayId,
        int? altitudeOverride
    )
    {
        // Resolve runway from argument if provided
        if (runwayId is not null)
        {
            var airportId = aircraft.Phases?.AssignedRunway?.AirportId ?? aircraft.Destination;
            if (airportId is null)
            {
                return new CommandResult(false, "No airport context to resolve runway");
            }

            var resolved = NavigationDatabase.Instance.GetRunway(airportId, runwayId);
            if (resolved is null)
            {
                return new CommandResult(false, $"Runway {runwayId} not found at {airportId}");
            }

            aircraft.Phases ??= new PhaseList();
            aircraft.Phases.AssignedRunway = resolved;
            aircraft.DestinationRunway = resolved.Designator;
            // Changing runway clears altitude override — different runway, different TPA
            aircraft.PatternAltitudeOverrideFt = null;
        }

        // Set explicit altitude override if provided
        if (altitudeOverride is not null)
        {
            aircraft.PatternAltitudeOverrideFt = altitudeOverride;
        }

        if (aircraft.Phases?.AssignedRunway is null)
        {
            return new CommandResult(false, "No assigned runway");
        }

        var runway = aircraft.Phases.AssignedRunway;
        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
        var waypoints = PatternGeometry.Compute(
            runway,
            category,
            newDirection,
            aircraft.PatternSizeOverrideNm,
            aircraft.PatternAltitudeOverrideFt,
            airportRunways
        );

        // Set traffic direction — aircraft is now in pattern mode
        aircraft.Phases.TrafficDirection = newDirection;

        // Update waypoints on existing pattern phases
        bool hasPatternPhases = PatternBuilder.UpdateWaypoints(aircraft.Phases, waypoints);

        // If no pattern phases exist yet (e.g., after takeoff or go-around),
        // append a full pattern circuit after the current phase.
        if (!hasPatternPhases)
        {
            var circuit = PatternBuilder.BuildCircuit(
                runway,
                category,
                newDirection,
                PatternEntryLeg.Upwind,
                true,
                null,
                aircraft.PatternSizeOverrideNm,
                aircraft.PatternAltitudeOverrideFt,
                airportRunways
            );
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
    internal static CommandResult TryPatternTurnTo<T>(AircraftState aircraft, string legName)
        where T : Phase
    {
        if (aircraft.Phases?.CurrentPhase is T)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.AdvanceToNext(ctx);
            return CommandDispatcher.Ok($"Turn {legName}");
        }

        return new CommandResult(false, $"Not on the leg before {legName}");
    }

    internal static CommandResult TryPatternTurnBase(AircraftState aircraft)
    {
        if (aircraft.Phases?.CurrentPhase is DownwindPhase)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
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

    internal static CommandResult TryMakeShortApproach(AircraftState aircraft)
    {
        if (aircraft.Phases?.CurrentPhase is DownwindPhase)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
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

    internal static CommandResult TryMakeNormalApproach(AircraftState aircraft)
    {
        if (aircraft.Phases?.CurrentPhase is BasePhase bp)
        {
            bp.FinalDistanceNm = null;
            return CommandDispatcher.Ok("Make normal approach");
        }

        if (aircraft.Phases?.CurrentPhase is DownwindPhase)
        {
            // On downwind, MNA is a no-op since MSA from downwind skips to base.
            // If the aircraft is still on downwind, there's nothing to undo.
            return CommandDispatcher.Ok("Make normal approach");
        }

        return new CommandResult(false, "Make normal approach requires downwind or base leg");
    }

    internal static CommandResult TryCancel270(AircraftState aircraft)
    {
        // NO270 cancels a planned (pending) 270 from P270. An in-progress 270
        // is cancelled by issuing any other command (FH, FPH, TB, etc.) since
        // MakeTurnPhase.CanAcceptCommand returns ClearsPhase for all commands.
        if (aircraft.Phases is not null)
        {
            int nextIdx = aircraft.Phases.CurrentIndex + 1;
            if (
                nextIdx < aircraft.Phases.Phases.Count
                && aircraft.Phases.Phases[nextIdx] is MakeTurnPhase { TargetDegrees: >= 269 and <= 271, Status: PhaseStatus.Pending }
            )
            {
                aircraft.Phases.Phases.RemoveAt(nextIdx);
                return CommandDispatcher.Ok("Cancel planned 270");
            }
        }

        return new CommandResult(false, "No planned 270 to cancel");
    }

    internal static CommandResult TryPlan270(AircraftState aircraft)
    {
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            return new CommandResult(false, "No active pattern phase");
        }

        var current = aircraft.Phases.CurrentPhase;
        if (current is not (DownwindPhase or BasePhase or CrosswindPhase or UpwindPhase))
        {
            return new CommandResult(false, "Plan 270 requires an active pattern leg");
        }

        var patternDir = aircraft.Phases.TrafficDirection;
        if (patternDir is null)
        {
            return new CommandResult(false, "Plan 270 requires an active traffic pattern");
        }

        // Check if there's already a planned 270 in the next phase
        int nextIdx = aircraft.Phases.CurrentIndex + 1;
        if (nextIdx < aircraft.Phases.Phases.Count && aircraft.Phases.Phases[nextIdx] is MakeTurnPhase { TargetDegrees: >= 269 and <= 271 })
        {
            return new CommandResult(false, "270 already planned for next turn");
        }

        var turnDir = patternDir == PatternDirection.Left ? TurnDirection.Left : TurnDirection.Right;
        var turnPhase = new MakeTurnPhase { Direction = turnDir, TargetDegrees = 270 };
        aircraft.Phases.InsertAfterCurrent(turnPhase);

        var dirStr = turnDir == TurnDirection.Left ? "left" : "right";
        return CommandDispatcher.Ok($"Plan {dirStr} 270 at next turn");
    }

    internal static CommandResult TrySetPatternSize(AircraftState aircraft, double sizeNm)
    {
        if (sizeNm is < 0.25 or > 10.0)
        {
            return new CommandResult(false, "Pattern size must be between 0.25 and 10.0 NM");
        }

        aircraft.PatternSizeOverrideNm = sizeNm;

        // Update waypoints on active pattern phases if in a pattern
        if (aircraft.Phases?.AssignedRunway is { } runway)
        {
            var category = AircraftCategorization.Categorize(aircraft.AircraftType);
            var direction = aircraft.Phases.TrafficDirection ?? PatternDirection.Left;
            var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
            var waypoints = PatternGeometry.Compute(runway, category, direction, sizeNm, aircraft.PatternAltitudeOverrideFt, airportRunways);
            PatternBuilder.UpdateWaypoints(aircraft.Phases, waypoints);
        }

        return CommandDispatcher.Ok($"Pattern size {sizeNm:G} NM");
    }

    internal static CommandResult TryMakeSTurns(AircraftState aircraft, TurnDirection initialDirection, int count)
    {
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            return new CommandResult(false, "No active phase for S-turns");
        }

        // S-turns are inserted before FinalApproachPhase or during downwind/base
        var sturnPhase = new STurnPhase { InitialDirection = initialDirection, Count = count };
        aircraft.Phases.InsertAfterCurrent(sturnPhase);

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.AdvanceToNext(ctx);

        var dirStr = initialDirection == TurnDirection.Left ? "left" : "right";
        return CommandDispatcher.Ok($"S-turns, initial {dirStr}, {count} turns");
    }

    internal static CommandResult TryMakeTurn(AircraftState aircraft, TurnDirection direction, double degrees)
    {
        var dirStr = direction == TurnDirection.Left ? "left" : "right";

        // Standalone turn: no active phases — create a minimal phase list with
        // just the turn so R360/L360/R270/L270 work on airborne aircraft that
        // haven't been given a pattern entry (e.g. VFR traffic handed off to tower).
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            var standalone = new MakeTurnPhase { Direction = direction, TargetDegrees = degrees };
            aircraft.Phases = new PhaseList();
            aircraft.Phases.Add(standalone);
            var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Start(startCtx);
            return CommandDispatcher.Ok($"Make {dirStr} {degrees:F0}");
        }

        var turnPhase = new MakeTurnPhase { Direction = direction, TargetDegrees = degrees };

        // For 360s on pattern legs: re-insert a fresh copy of the current phase
        // after the turn so the aircraft resumes the same leg instead of
        // incorrectly skipping to the next one.
        bool is360 = Math.Abs(degrees - 360) < 1;
        Phase? resumePhase = is360 ? ClonePatternPhase(aircraft.Phases.CurrentPhase) : null;

        if (resumePhase is not null)
        {
            aircraft.Phases.InsertAfterCurrent(new List<Phase> { turnPhase, resumePhase });
        }
        else
        {
            aircraft.Phases.InsertAfterCurrent(turnPhase);
        }

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.AdvanceToNext(ctx);

        return CommandDispatcher.Ok($"Make {dirStr} {degrees:F0}");
    }

    /// <summary>
    /// Create a fresh copy of a pattern phase with the same configuration,
    /// so the aircraft can resume the same leg after a 360° turn.
    /// Returns null if the current phase is not a pattern leg.
    /// </summary>
    private static Phase? ClonePatternPhase(Phase? phase)
    {
        return phase switch
        {
            DownwindPhase dw => new DownwindPhase { Waypoints = dw.Waypoints, IsExtended = dw.IsExtended },
            BasePhase bp => new BasePhase { Waypoints = bp.Waypoints, FinalDistanceNm = bp.FinalDistanceNm },
            CrosswindPhase cw => new CrosswindPhase { Waypoints = cw.Waypoints },
            UpwindPhase up => new UpwindPhase { Waypoints = up.Waypoints },
            _ => null,
        };
    }

    internal static CommandResult TrySetupTouchAndGo(AircraftState aircraft, PatternDirection? trafficPattern)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedTouchAndGo;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.Designator;
        if (trafficPattern is { } dir)
        {
            aircraft.Phases.TrafficDirection = dir;
        }
        EnsurePatternMode(aircraft.Phases);
        CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, new TouchAndGoPhase());

        return CommandDispatcher.Ok($"Cleared touch-and-go{CommandDispatcher.RunwayLabel(aircraft)}{TrafficLabel(trafficPattern)}");
    }

    internal static CommandResult TrySetupStopAndGo(AircraftState aircraft, PatternDirection? trafficPattern)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedStopAndGo;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.Designator;
        if (trafficPattern is { } dir)
        {
            aircraft.Phases.TrafficDirection = dir;
        }
        EnsurePatternMode(aircraft.Phases);
        CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, new StopAndGoPhase());

        return CommandDispatcher.Ok($"Cleared stop-and-go{CommandDispatcher.RunwayLabel(aircraft)}{TrafficLabel(trafficPattern)}");
    }

    internal static CommandResult TrySetupLowApproach(AircraftState aircraft, PatternDirection? trafficPattern)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedLowApproach;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.Designator;
        if (trafficPattern is { } dir)
        {
            aircraft.Phases.TrafficDirection = dir;
        }
        EnsurePatternMode(aircraft.Phases);
        CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, new LowApproachPhase());

        return CommandDispatcher.Ok($"Cleared low approach{CommandDispatcher.RunwayLabel(aircraft)}{TrafficLabel(trafficPattern)}");
    }

    internal static CommandResult TrySetupClearedForOption(AircraftState aircraft, PatternDirection? trafficPattern)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedForOption;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.Designator;
        if (trafficPattern is { } dir)
        {
            aircraft.Phases.TrafficDirection = dir;
        }
        EnsurePatternMode(aircraft.Phases);
        CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, new TouchAndGoPhase());

        return CommandDispatcher.Ok($"Cleared for the option{CommandDispatcher.RunwayLabel(aircraft)}{TrafficLabel(trafficPattern)}");
    }

    private static string TrafficLabel(PatternDirection? dir)
    {
        return dir switch
        {
            PatternDirection.Left => ", make left traffic",
            PatternDirection.Right => ", make right traffic",
            _ => "",
        };
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

    internal static CommandResult TryHoldPresentPosition(AircraftState aircraft, TurnDirection? orbitDirection)
    {
        var phase = new VfrHoldPhase { OrbitDirection = orbitDirection };

        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
            aircraft.Phases = new PhaseList { AssignedRunway = aircraft.Phases.AssignedRunway };
        }
        else
        {
            aircraft.Phases = new PhaseList();
        }

        aircraft.Phases.Add(phase);
        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        var dirStr = orbitDirection switch
        {
            TurnDirection.Left => "left 360s",
            TurnDirection.Right => "right 360s",
            _ => "hover",
        };
        return CommandDispatcher.Ok($"Hold present position, {dirStr}");
    }

    internal static CommandResult TryHoldAtFix(AircraftState aircraft, string fixName, double lat, double lon, TurnDirection? orbitDirection)
    {
        var phase = new VfrHoldPhase
        {
            FixName = fixName,
            FixLat = lat,
            FixLon = lon,
            OrbitDirection = orbitDirection,
        };

        RunwayInfo? runway = aircraft.Phases?.AssignedRunway;
        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
        }

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        if (runway is not null)
        {
            aircraft.DestinationRunway = runway.Designator;
        }

        aircraft.Phases.Add(phase);
        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
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
    /// For final entry, computes the glideslope-TPA intercept point on
    /// the extended centerline (or uses the custom final distance).
    /// </summary>
    private static (double Lat, double Lon) GetEntryPoint(
        PatternWaypoints wp,
        PatternEntryLeg leg,
        double? finalDistanceNm,
        AircraftCategory category
    )
    {
        if (leg == PatternEntryLeg.Base && finalDistanceNm is not null)
        {
            // Compute a base entry point at the custom final distance
            TrueHeading reciprocal = wp.FinalHeading.ToReciprocal();
            var finalPoint = GeoMath.ProjectPoint(wp.ThresholdLat, wp.ThresholdLon, reciprocal, finalDistanceNm.Value);
            // Offset laterally by the pattern width (same direction as BaseTurn from threshold)
            double baseOffset = GeoMath.DistanceNm(wp.ThresholdLat, wp.ThresholdLon, wp.BaseTurnLat, wp.BaseTurnLon);
            double lateralBearing = GeoMath.BearingTo(wp.ThresholdLat, wp.ThresholdLon, wp.BaseTurnLat, wp.BaseTurnLon);
            var entryPoint = GeoMath.ProjectPointRaw(finalPoint.Lat, finalPoint.Lon, lateralBearing, baseOffset);
            return (entryPoint.Lat, entryPoint.Lon);
        }

        if (leg == PatternEntryLeg.Final)
        {
            // Entry point on extended centerline at the glideslope-TPA intercept distance.
            // If finalDistanceNm is specified, use that distance instead.
            double gsAngle = GlideSlopeGeometry.AngleForCategory(category);
            double entryDist = finalDistanceNm ?? (CategoryPerformance.PatternAltitudeAgl(category) / GlideSlopeGeometry.FeetPerNm(gsAngle));
            TrueHeading reciprocal = wp.FinalHeading.ToReciprocal();
            var point = GeoMath.ProjectPoint(wp.ThresholdLat, wp.ThresholdLon, reciprocal, entryDist);
            return (point.Lat, point.Lon);
        }

        return leg switch
        {
            // AIM 4-3-3: enter downwind at midfield (abeam the threshold), not at the departure end.
            // The standard 45° entry joins the downwind leg abeam the runway midpoint.
            PatternEntryLeg.Downwind => (wp.DownwindAbeamLat, wp.DownwindAbeamLon),
            PatternEntryLeg.Crosswind => (wp.CrosswindTurnLat, wp.CrosswindTurnLon),
            PatternEntryLeg.Base => (wp.BaseTurnLat, wp.BaseTurnLon),
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

    internal static CommandResult TryGoAround(GoAroundCommand ga, AircraftState aircraft)
    {
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            return new CommandResult(false, "Go around not applicable");
        }

        if (ga.TrafficPattern is { } patDir)
        {
            // Traffic pattern direction only applies to VFR, visual approach, or already-in-pattern aircraft
            bool canSetPattern =
                aircraft.IsVfr
                || (aircraft.Phases.TrafficDirection is not null)
                || (aircraft.Phases.ActiveApproach?.ApproachId.StartsWith("VIS", StringComparison.Ordinal) == true);
            if (!canSetPattern)
            {
                return new CommandResult(false, "Traffic pattern direction not applicable for IFR aircraft");
            }

            aircraft.Phases.TrafficDirection = patDir;

            // Set pattern altitude override if provided (e.g., GA MLT 15)
            if (ga.TargetAltitude is not null)
            {
                aircraft.PatternAltitudeOverrideFt = ga.TargetAltitude;
            }
        }

        bool isGaPattern = aircraft.Phases.TrafficDirection is not null;
        var gaCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        int? gaTargetAlt = ga.TargetAltitude;
        bool hasAtcOverride = ga.AssignedMagneticHeading is not null || ga.TargetAltitude is not null;

        // Build MAP phases for instrument approaches without ATC override
        var mapPhases = (!isGaPattern && !hasAtcOverride) ? ApproachCommandHandler.BuildMissedApproachPhases(aircraft) : [];

        if (gaTargetAlt is null && mapPhases.Count > 0)
        {
            var mapFixes = aircraft.Phases.ActiveApproach!.MissedApproachFixes;
            gaTargetAlt = ApproachCommandHandler.GetMissedApproachAltitude(mapFixes);
        }
        else if (gaTargetAlt is null && isGaPattern)
        {
            double fieldElev = gaCtx.Runway?.ElevationFt ?? 0;
            double patAgl = CategoryPerformance.PatternAltitudeAgl(gaCtx.Category);
            gaTargetAlt = (int)(fieldElev + patAgl);
        }

        var goAround = new GoAroundPhase
        {
            AssignedMagneticHeading = ga.AssignedMagneticHeading,
            TargetAltitude = gaTargetAlt,
            ReenterPattern = isGaPattern,
        };

        var phases = new List<Phase> { goAround };
        phases.AddRange(mapPhases);

        aircraft.Phases.ReplaceUpcoming(phases);
        aircraft.Phases.AdvanceToNext(gaCtx);

        var gaMsg = "Go around";
        if (ga.TrafficPattern is PatternDirection.Left)
        {
            gaMsg += ", make left traffic";
        }
        else if (ga.TrafficPattern is PatternDirection.Right)
        {
            gaMsg += ", make right traffic";
        }
        if (ga.AssignedMagneticHeading is not null)
        {
            gaMsg += $", fly heading {ga.AssignedMagneticHeading.Value.ToDisplayInt():000}";
        }
        if (ga.TargetAltitude is not null)
        {
            gaMsg += $", climb to {ga.TargetAltitude:N0}";
        }
        return CommandDispatcher.Ok(gaMsg);
    }

    internal static CommandResult TryClearedToLand(ClearedToLandCommand ctl, AircraftState aircraft)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedToLand;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.Designator;
        aircraft.Phases.TrafficDirection = null;
        var isHeliCtl = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        Phase landingCtl = isHeliCtl ? new HelicopterLandingPhase() : new LandingPhase();
        CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, landingCtl);
        if (ctl.NoDelete)
        {
            aircraft.AutoDeleteExempt = true;
        }
        return CommandDispatcher.Ok($"Cleared to land{CommandDispatcher.RunwayLabel(aircraft)}");
    }

    internal static CommandResult TryLandAndHoldShort(LandAndHoldShortCommand lahso, AircraftState aircraft, AirportGroundLayout? groundLayout)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        if (aircraft.Phases.AssignedRunway is null)
        {
            return new CommandResult(false, "No assigned runway");
        }

        if (groundLayout is null)
        {
            return new CommandResult(false, "No ground layout available for LAHSO intersection calculation");
        }

        var runway = aircraft.Phases.AssignedRunway;

        // Find the landing runway in the ground layout
        var landingGround = groundLayout.FindGroundRunway(runway.Designator);
        if (landingGround is null)
        {
            return new CommandResult(false, $"Landing runway {runway.Designator} not found in ground layout");
        }

        // Find the crossing runway in the ground layout
        var crossingGround = groundLayout.FindGroundRunway(lahso.CrossingRunwayId);
        if (crossingGround is null)
        {
            return new CommandResult(false, $"Crossing runway {lahso.CrossingRunwayId} not found in ground layout");
        }

        // Compute the intersection point
        var intersection = RunwayIntersectionCalculator.FindIntersection(landingGround, crossingGround);
        if (intersection is null)
        {
            return new CommandResult(false, $"Runway {runway.Designator} does not intersect runway {lahso.CrossingRunwayId}");
        }

        // Compute hold-short distance from threshold
        double holdShortDistNm = RunwayIntersectionCalculator.ComputeHoldShortDistanceNm(
            intersection.Value.DistFromStartNm,
            runway.Designator,
            landingGround,
            crossingGround.WidthFt
        );

        if (holdShortDistNm < 0.1)
        {
            return new CommandResult(false, $"Hold-short point too close to threshold for runway {lahso.CrossingRunwayId}");
        }

        // Compute the hold-short lat/lon on the landing runway centerline
        var holdShortPoint = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading, holdShortDistNm);

        // Set LAHSO target
        aircraft.Phases.LahsoHoldShort = new LahsoTarget
        {
            Lat = holdShortPoint.Lat,
            Lon = holdShortPoint.Lon,
            DistFromThresholdNm = holdShortDistNm,
            CrossingRunwayId = lahso.CrossingRunwayId,
        };

        // LAHSO includes landing clearance per 7110.65 §3-10-5.b
        aircraft.Phases.LandingClearance = ClearanceType.ClearedToLand;
        aircraft.Phases.ClearedRunwayId = runway.Designator;
        aircraft.Phases.TrafficDirection = null;
        CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, new LandingPhase());

        return CommandDispatcher.Ok($"Cleared to land{CommandDispatcher.RunwayLabel(aircraft)}, hold short runway {lahso.CrossingRunwayId}");
    }

    internal static CommandResult TryCancelLandingClearance(AircraftState aircraft)
    {
        if (aircraft.Phases is null || aircraft.Phases.LandingClearance is null)
        {
            return new CommandResult(false, "No landing clearance to cancel");
        }

        aircraft.Phases.LandingClearance = null;
        aircraft.Phases.ClearedRunwayId = null;
        return CommandDispatcher.Ok($"Landing clearance cancelled{CommandDispatcher.RunwayLabel(aircraft)}");
    }
}
