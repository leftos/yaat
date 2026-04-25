using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

internal static class PatternCommandHandler
{
    private static readonly ILogger Log = SimLog.CreateLogger("PatternCommandHandler");

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
            var airportId = aircraft.Phases?.AssignedRunway?.AirportId ?? aircraft.FlightPlan.Destination;
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
            aircraft.Procedure.DestinationRunway = resolved.Designator;
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
                aircraft.Position,
                new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
                crosswindHdg
            );

            isOnWrongSide = patternSideOffset < 0;

            if (isOnWrongSide)
            {
                var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
                var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
                    runway,
                    aircraft.Ground.Layout?.FindRunway(runway.Designator),
                    aircraft.Pattern.SizeOverrideNm,
                    aircraft.Pattern.AltitudeOverrideFt
                );
                waypoints = PatternGeometry.Compute(runway, category, direction, sizeOv, altOv, airportRunways);
            }
        }

        // For Final entry: reject if the maneuver would create a 360° loop.
        // Compute the two heading changes: (1) turn from current heading to the
        // bearing toward the entry point, (2) turn from arrival bearing at the
        // entry point to the runway (approach) heading. If BOTH exceed 90°, the
        // total turn approaches 360° — the aircraft must reverse to the entry
        // point and then reverse again to align with final. That's a loop.
        if (!aircraft.IsOnGround && entryLeg == PatternEntryLeg.Final)
        {
            var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
            var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
                runway,
                aircraft.Ground.Layout?.FindRunway(runway.Designator),
                aircraft.Pattern.SizeOverrideNm,
                aircraft.Pattern.AltitudeOverrideFt
            );
            waypoints ??= PatternGeometry.Compute(runway, category, direction, sizeOv, altOv, airportRunways);
            var (eLat, eLon) = GetEntryPoint(waypoints, PatternEntryLeg.Final, finalDistanceNm, category);

            double bearingToEntry = GeoMath.BearingTo(aircraft.Position, new LatLon(eLat, eLon));
            double turnToEntry = aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearingToEntry));
            double turnAtEntry = new TrueHeading(bearingToEntry).AbsAngleTo(runway.TrueHeading);

            // The aircraft can only execute high-angle turns if it has enough
            // room. Compute the minimum distance needed for the combined turn
            // using standard-rate geometry: arc length = turn_angle × turn_radius.
            // With standard 3°/s turns at the aircraft's current speed, the
            // turn radius is V²/(g·tan(bank)) ≈ V/ω where ω = 3°/s.
            // Simplification: use the distance to the entry point vs the arc
            // distance required. If the straight-line distance is less than
            // the arc needed, the maneuver is infeasible (loop).
            double distToEntry = GeoMath.DistanceNm(aircraft.Position, new LatLon(eLat, eLon));

            // Turn radius model depends on flight rules. IFR keeps the
            // conservative standard-rate envelope (3°/s on IAS); VFR uses a
            // pattern-rate envelope on groundspeed so light/slow VFR traffic
            // can accept the tighter turns it actually flies in the pattern.
            // Precedent for IsVfr branching: FinalApproachPhase.cs:367.
            double turnRateDegs;
            double speedKts;
            if (aircraft.FlightPlan.IsVfr)
            {
                // 12°/s ≈ 25° bank at 90 kt — AIM medium-bank ceiling for
                // traffic-pattern maneuvering (AIM 4-3-2/3 + FAA Airplane
                // Flying Handbook pattern-turn guidance). Groundspeed is
                // wind-corrected, floored at 60 kt so a light single on a
                // strong headwind isn't granted an unrealistically tight
                // radius.
                turnRateDegs = 12.0;
                speedKts = Math.Max(aircraft.GroundSpeed, 60);
            }
            else
            {
                turnRateDegs = 3.0;
                speedKts = Math.Max(aircraft.IndicatedAirspeed, 80);
            }
            double radiusNm = (speedKts / 3600.0) / (turnRateDegs * Math.PI / 180.0);

            // Arc distance for both turns combined
            double totalTurnDeg = turnToEntry + turnAtEntry;
            double arcNm = (totalTurnDeg * Math.PI / 180.0) * radiusNm;

            Log.LogDebug(
                "[EF-LoopCheck] {Callsign}: vfr={IsVfr}, hdg={Hdg:F0}, brg→entry={Brg:F0}, turnToEntry={T1:F0}°, turnAtEntry={T2:F0}°, spd={Spd:F0}kt, rate={Rate:F0}°/s, r={R:F2}nm, dist={Dist:F1}nm, arc={Arc:F1}nm",
                aircraft.Callsign,
                aircraft.FlightPlan.IsVfr,
                aircraft.TrueHeading.Degrees,
                bearingToEntry,
                turnToEntry,
                turnAtEntry,
                speedKts,
                turnRateDegs,
                radiusNm,
                distToEntry,
                arcNm
            );

            // Reject if the required arc exceeds what the aircraft can fly in
            // the available straight-line distance. The aircraft physically
            // cannot complete the turns before reaching the entry point.
            if ((totalTurnDeg > 180) && (arcNm > distToEntry))
            {
                return new CommandResult(false, "Unable, short final");
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
            double distToDepEnd = GeoMath.DistanceNm(aircraft.Position, new LatLon(runway.EndLatitude, runway.EndLongitude));
            double distToDownwindEntry = GeoMath.DistanceNm(aircraft.Position, new LatLon(waypoints.DownwindAbeamLat, waypoints.DownwindAbeamLon));
            if (hdgDiff < 30 && distToDepEnd < 3.0 && distToDownwindEntry > 1.0)
            {
                effectiveEntryLeg = PatternEntryLeg.Upwind;
            }
        }

        // Compute waypoints for the entry point check
        {
            var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
            var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
                runway,
                aircraft.Ground.Layout?.FindRunway(runway.Designator),
                aircraft.Pattern.SizeOverrideNm,
                aircraft.Pattern.AltitudeOverrideFt
            );
            waypoints ??= PatternGeometry.Compute(runway, category, direction, sizeOv, altOv, airportRunways);
        }

        // ERB/ELB without a distance argument: derive FinalDistanceNm from the
        // aircraft's current along-track projection onto the extended centerline.
        // This makes BasePhase turn onto final at the aircraft's present position
        // along centerline, yielding a perpendicular base leg whose length equals
        // the current cross-track. "Enter base from present distance" — rather
        // than aiming for the standard pattern base turn point (which would force
        // a diagonal leg). Setting useAircraftPositionAsEntry skips PatternEntryPhase
        // below so BasePhase starts immediately from the aircraft's current position.
        bool useAircraftPositionAsEntry = false;
        if (!aircraft.IsOnGround && !isOnWrongSide && effectiveEntryLeg == PatternEntryLeg.Base && effectiveFinalDistanceNm is null)
        {
            TrueHeading reciprocal = waypoints.FinalHeading.ToReciprocal();
            double alongTrackOutbound = GeoMath.AlongTrackDistanceNm(
                aircraft.Position,
                new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon),
                reciprocal
            );
            if (alongTrackOutbound < MinimumPerpendicularBaseFinalDistanceNm(category))
            {
                return new CommandResult(false, "Unable, too close for base");
            }

            // Altitude feasibility: can the aircraft descend from its current
            // altitude to runway elevation within the base + final path at
            // category pattern descent rate and base speed? Controllers issuing
            // "enter base" to an aircraft well above TPA should descend it first
            // (AIM 4-3-3: pattern entry at TPA). Rejecting here prompts a DM.
            double crossTrackAbsNm = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(
                    aircraft.Position,
                    new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon),
                    waypoints.FinalHeading
                )
            );
            double totalPathNm = crossTrackAbsNm + alongTrackOutbound;
            double baseSpeedKt = CategoryPerformance.BaseSpeed(category);
            double pathMinutes = totalPathNm / (baseSpeedKt / 60.0);
            double maxDescentFt = CategoryPerformance.PatternDescentRate(category) * pathMinutes;
            double altitudeToLoseFt = aircraft.Altitude - runway.ElevationFt;
            if (altitudeToLoseFt > maxDescentFt)
            {
                return new CommandResult(false, "Unable, too high for base");
            }

            effectiveFinalDistanceNm = alongTrackOutbound;
            useAircraftPositionAsEntry = true;
        }

        var (circuitSizeOv, circuitAltOv) = PatternGeometry.ResolveAuthoredOverrides(
            runway,
            aircraft.Ground.Layout?.FindRunway(runway.Designator),
            aircraft.Pattern.SizeOverrideNm,
            aircraft.Pattern.AltitudeOverrideFt
        );
        var circuitPhases = PatternBuilder.BuildCircuit(
            runway,
            category,
            direction,
            effectiveEntryLeg,
            touchAndGo,
            effectiveFinalDistanceNm,
            circuitSizeOv,
            circuitAltOv,
            NavigationDatabase.Instance.GetRunways(runway.AirportId)
        );

        var phases = new PhaseList { AssignedRunway = runway };
        aircraft.Procedure.DestinationRunway = runway.Designator;
        phases.LandingClearance = aircraft.Phases.LandingClearance;
        phases.ClearedRunwayId = aircraft.Phases.ClearedRunwayId;
        phases.TrafficDirection = aircraft.Phases.TrafficDirection;

        // If the aircraft is airborne and far from the pattern, insert a PatternEntryPhase
        // to navigate to the entry point with descent/climb to pattern altitude.
        // For downwind entry, add a lead-in waypoint so the aircraft aligns with the
        // downwind heading before reaching the abeam point.
        if (!aircraft.IsOnGround && !isOnWrongSide)
        {
            var (entryLat, entryLon) = useAircraftPositionAsEntry
                ? (aircraft.Position.Lat, aircraft.Position.Lon)
                : GetEntryPoint(waypoints, effectiveEntryLeg, effectiveFinalDistanceNm, category);
            double distToEntry = GeoMath.DistanceNm(aircraft.Position, new LatLon(entryLat, entryLon));

            if (distToEntry > 1.0)
            {
                double? leadInLat = null;
                double? leadInLon = null;

                // For downwind entry: choose between a 45° midfield intercept (AIM 4-3-3)
                // and a straight-in join to the extended downwind leg. Score each by the
                // total heading change required along aircraft → lead-in → abeam → downwind,
                // with a penalty on any single turn >120° (discourages U-turns / looping past
                // the field).
                if (effectiveEntryLeg == PatternEntryLeg.Downwind)
                {
                    var leadIn = ChooseDownwindLeadIn(aircraft, runway, direction, category, waypoints, entryLat, entryLon);
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

                var kind = ClassifyEntryKind(aircraft, runway, direction, effectiveEntryLeg);
                phases.Add(
                    new PatternEntryPhase
                    {
                        EntryLat = entryLat,
                        EntryLon = entryLon,
                        PatternAltitude = entryAltitude,
                        Kind = kind,
                        LeadInLat = leadInLat,
                        LeadInLon = leadInLon,
                    }
                );
            }
        }

        if (isOnWrongSide)
        {
            phases.Add(new MidfieldCrossingPhase { Waypoints = waypoints });

            // Large/turbine cross at TPA+500 per AIM 4-3-3.1.b; they still need to
            // descend to TPA before joining downwind. TeardropReentryPhase does that
            // via an outbound leg + 45° intercept to abeam. Pistons/helicopters already
            // cross at TPA (see MidfieldCrossingPhase) and can drop straight into downwind.
            if (category is AircraftCategory.Jet or AircraftCategory.Turboprop)
            {
                phases.Add(new TeardropReentryPhase { Waypoints = waypoints });
            }
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
            var airportId = aircraft.Phases?.AssignedRunway?.AirportId ?? aircraft.FlightPlan.Destination;
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
            aircraft.Procedure.DestinationRunway = resolved.Designator;
            // Changing runway clears altitude override — different runway, different TPA
            aircraft.Pattern.AltitudeOverrideFt = null;
        }

        // Set explicit altitude override if provided
        if (altitudeOverride is not null)
        {
            aircraft.Pattern.AltitudeOverrideFt = altitudeOverride;
        }

        if (aircraft.Phases?.AssignedRunway is null)
        {
            return new CommandResult(false, "No assigned runway");
        }

        var runway = aircraft.Phases.AssignedRunway;
        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
        var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
            runway,
            aircraft.Ground.Layout?.FindRunway(runway.Designator),
            aircraft.Pattern.SizeOverrideNm,
            aircraft.Pattern.AltitudeOverrideFt
        );
        var waypoints = PatternGeometry.Compute(runway, category, newDirection, sizeOv, altOv, airportRunways);

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
                sizeOv,
                altOv,
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
        switch (aircraft.Phases?.CurrentPhase)
        {
            case DownwindPhase dw:
                dw.IsExtended = true;
                return CommandDispatcher.Ok("Extend downwind");
            case UpwindPhase uw:
                uw.IsExtended = true;
                return CommandDispatcher.Ok("Extend upwind");
            case CrosswindPhase cw:
                cw.IsExtended = true;
                return CommandDispatcher.Ok("Extend crosswind");
            case BasePhase:
                return new CommandResult(false, "Extend not allowed on base leg");
            default:
                return new CommandResult(false, "Extend applies on upwind, crosswind, or downwind");
        }
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

        aircraft.Pattern.SizeOverrideNm = sizeNm;

        // Update waypoints on active pattern phases if in a pattern
        if (aircraft.Phases?.AssignedRunway is { } runway)
        {
            var category = AircraftCategorization.Categorize(aircraft.AircraftType);
            var direction = aircraft.Phases.TrafficDirection ?? PatternDirection.Left;
            var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
            var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
                runway,
                aircraft.Ground.Layout?.FindRunway(runway.Designator),
                sizeNm,
                aircraft.Pattern.AltitudeOverrideFt
            );
            var waypoints = PatternGeometry.Compute(runway, category, direction, sizeOv, altOv, airportRunways);
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
            aircraft.Procedure.DestinationRunway = runway.Designator;
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
    /// Choose the lead-in waypoint for a downwind entry. Evaluates two candidates
    /// — a 45° midfield intercept (AIM 4-3-3) and a straight-in join to the
    /// extended downwind leg — and picks whichever requires less total maneuvering
    /// from the aircraft's current position and heading. A penalty on any single
    /// turn exceeding 120° discourages near-U-turn (loop) paths.
    ///
    /// 45° entry heading: downwind ± 45° (+ for right pattern, − for left).
    /// 45° lead-in distance: 50% of runway length, with per-category floors so
    /// turboprops and jets get enough room to stabilize on the entry leg:
    ///   Piston/Helicopter: 0.5 × runway length (no floor — tight patterns)
    ///   Turboprop:         max(0.5 × runway length, 1.5 nm)
    ///   Jet:               max(0.5 × runway length, 2.0 nm)
    /// Extended-downwind lead-in distance: 1.5 nm (legacy default).
    /// </summary>
    private static (double Lat, double Lon) ChooseDownwindLeadIn(
        AircraftState aircraft,
        RunwayInfo runway,
        PatternDirection direction,
        AircraftCategory category,
        PatternWaypoints waypoints,
        double abeamLat,
        double abeamLon
    )
    {
        const double UTurnPenaltyThresholdDeg = 120.0;
        const double ExtendedDownwindLeadInNm = 1.5;

        double downwindDeg = waypoints.DownwindHeading.Degrees;
        double entry45Deg = direction == PatternDirection.Right ? downwindDeg + 45.0 : downwindDeg - 45.0;
        var entry45Hdg = new TrueHeading(entry45Deg);
        TrueHeading reverseEntry45 = entry45Hdg.ToReciprocal();
        TrueHeading reverseDownwind = waypoints.DownwindHeading.ToReciprocal();

        double runwayHalfNm = runway.LengthFt * 0.5 / 6076.12;
        double categoryFloorNm = category switch
        {
            AircraftCategory.Jet => 2.0,
            AircraftCategory.Turboprop => 1.5,
            _ => 0.0,
        };
        double leadIn45Nm = Math.Max(runwayHalfNm, categoryFloorNm);
        var lead45 = GeoMath.ProjectPoint(abeamLat, abeamLon, reverseEntry45, leadIn45Nm);
        var leadXtdDownwind = GeoMath.ProjectPoint(abeamLat, abeamLon, reverseDownwind, ExtendedDownwindLeadInNm);

        double score45 = ScoreLeadInPath(aircraft, lead45.Lat, lead45.Lon, entry45Deg, downwindDeg, UTurnPenaltyThresholdDeg);
        double scoreXtdDownwind = ScoreLeadInPath(
            aircraft,
            leadXtdDownwind.Lat,
            leadXtdDownwind.Lon,
            downwindDeg,
            downwindDeg,
            UTurnPenaltyThresholdDeg
        );

        Log.LogDebug(
            "[PatternEntry.LeadIn] {Callsign}: 45° score={Score45:F1} XDW score={ScoreXDW:F1} → {Chosen}",
            aircraft.Callsign,
            score45,
            scoreXtdDownwind,
            score45 <= scoreXtdDownwind ? "45°" : "XDW"
        );

        return score45 <= scoreXtdDownwind ? (lead45.Lat, lead45.Lon) : (leadXtdDownwind.Lat, leadXtdDownwind.Lon);
    }

    /// <summary>
    /// Sum of absolute heading changes along the path aircraft → lead-in → abeam → downwind,
    /// plus a penalty for any single turn exceeding <paramref name="uTurnThresholdDeg"/>.
    /// The penalty is linear past the threshold so a 160° turn scores far worse than a 119°
    /// one, which is what distinguishes a clean 45° intercept from a loop-around.
    /// </summary>
    private static double ScoreLeadInPath(
        AircraftState aircraft,
        double leadInLat,
        double leadInLon,
        double entryHeadingDeg,
        double downwindHeadingDeg,
        double uTurnThresholdDeg
    )
    {
        double bearingToLeadIn = GeoMath.BearingTo(aircraft.Position, new LatLon(leadInLat, leadInLon));
        double turnInit = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, bearingToLeadIn);
        double turnMid = GeoMath.AbsBearingDifference(bearingToLeadIn, entryHeadingDeg);
        double turnAbeam = GeoMath.AbsBearingDifference(entryHeadingDeg, downwindHeadingDeg);

        double total = turnInit + turnMid + turnAbeam;
        total += Math.Max(0.0, turnInit - uTurnThresholdDeg);
        total += Math.Max(0.0, turnMid - uTurnThresholdDeg);
        total += Math.Max(0.0, turnAbeam - uTurnThresholdDeg);
        return total;
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
    /// Minimum along-track distance (nm) for an ERB/ELB-no-distance turn to base —
    /// below this, the aircraft cannot establish a stable final approach segment
    /// after the base-to-final turn. Tuned per category so the turn radius still
    /// leaves useful final: jets need ~2 nm, pistons ~1 nm, helicopters ~0.5 nm.
    /// </summary>
    private static double MinimumPerpendicularBaseFinalDistanceNm(AircraftCategory category) =>
        category switch
        {
            AircraftCategory.Jet => 2.0,
            AircraftCategory.Turboprop => 2.0,
            AircraftCategory.Piston => 1.0,
            AircraftCategory.Helicopter => 0.5,
            _ => 2.0,
        };

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
                aircraft.FlightPlan.IsVfr
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
                aircraft.Pattern.AltitudeOverrideFt = ga.TargetAltitude;
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
            // AIM 4-3-2: hand off to UpwindPhase 300ft below pattern altitude so the
            // crosswind turn becomes available at the same threshold as a VFR departure.
            double fieldElev = gaCtx.Runway?.ElevationFt ?? 0;
            double patAgl = CategoryPerformance.PatternAltitudeAgl(gaCtx.Category);
            gaTargetAlt = (int)(fieldElev + patAgl - 300.0);
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
            aircraft.Ground.AutoDeleteExempt = true;
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

    /// <summary>
    /// Determines the entry kind for a PatternEntryPhase. Non-downwind legs map
    /// directly; a downwind entry is classified by the angular delta between the
    /// aircraft's current track and the downwind course.
    /// </summary>
    internal static PatternEntryKind ClassifyEntryKind(
        AircraftState aircraft,
        RunwayInfo runway,
        PatternDirection direction,
        PatternEntryLeg entryLeg
    )
    {
        _ = direction;
        return entryLeg switch
        {
            PatternEntryLeg.Upwind => PatternEntryKind.Upwind,
            PatternEntryLeg.Base => PatternEntryKind.Base,
            PatternEntryLeg.Final => PatternEntryKind.Final,
            _ => PatternEntryPhase.ClassifyDownwindEntry(aircraft.TrueTrack, runway.TrueHeading.ToReciprocal()),
        };
    }
}
