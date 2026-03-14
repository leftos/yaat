using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

public static class ApproachCommandHandler
{
    private static readonly ILogger Log = SimLog.CreateLogger("ApproachCommandHandler");

    public static CommandResult TryClearedApproach(
        ClearedApproachCommand cmd,
        AircraftState aircraft,
        IApproachLookup? approachLookup,
        IRunwayLookup? runways,
        IFixLookup? fixes
    )
    {
        var resolved = ResolveApproach(cmd.ApproachId, cmd.AirportCode, aircraft, approachLookup, runways);
        if (!resolved.Success)
        {
            return new CommandResult(false, resolved.Error);
        }

        var (procedure, approachRunway, airport) = resolved;

        // Validate intercept angle unless force
        if (!cmd.Force)
        {
            var rejection = ValidateInterceptAngle(aircraft, approachRunway);
            if (rejection is not null)
            {
                return rejection;
            }
        }

        // Cancel existing speed restrictions per 7110.65 §5-7-4
        aircraft.Targets.TargetSpeed = null;

        double finalCourse = approachRunway.TrueHeading;

        // Build approach fix sequence from common legs
        var approachFixes = BuildApproachFixes(procedure, fixes);

        // Clear existing phases
        ClearExistingPhases(aircraft);

        var clearance = new ApproachClearance
        {
            ApproachId = procedure.ApproachId,
            AirportCode = airport,
            RunwayId = procedure.Runway!,
            FinalApproachCourse = finalCourse,
            Procedure = procedure,
            MissedApproachFixes = BuildMissedApproachFixes(procedure, fixes),
            MapHold = ExtractMissedApproachHold(procedure, fixes),
        };

        aircraft.Phases = new PhaseList { AssignedRunway = approachRunway, ActiveApproach = clearance };
        aircraft.DestinationRunway = approachRunway.Designator;

        // Handle rich CAPP forms: AT fix, DCT fix — prepend to approach fixes
        if (cmd.AtFix is not null && cmd.AtFixLat is not null && cmd.AtFixLon is not null)
        {
            approachFixes.Insert(0, new ApproachFix(cmd.AtFix, cmd.AtFixLat.Value, cmd.AtFixLon.Value));
        }
        else if (cmd.DctFix is not null && cmd.DctFixLat is not null && cmd.DctFixLon is not null)
        {
            approachFixes.Insert(0, new ApproachFix(cmd.DctFix, cmd.DctFixLat.Value, cmd.DctFixLon.Value));
        }

        // Apply crossing fix altitude if specified
        if (cmd.CrossFixAltitude is { } cxAlt && cxAlt > 0)
        {
            aircraft.Targets.TargetAltitude = cxAlt;
        }

        // Build phase sequence
        if (approachFixes.Count > 0)
        {
            aircraft.Phases.Add(new ApproachNavigationPhase { Fixes = approachFixes });
        }

        aircraft.Phases.Add(new FinalApproachPhase());
        var isHeliApch = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        aircraft.Phases.Add(isHeliApch ? new HelicopterLandingPhase() : new LandingPhase());

        StartPhases(aircraft);

        return new CommandResult(true, $"Cleared {procedure.ApproachId} approach, runway {procedure.Runway}");
    }

    public static CommandResult TryJoinApproach(
        string approachId,
        string? airportCode,
        bool force,
        bool straightIn,
        AircraftState aircraft,
        IApproachLookup? approachLookup,
        IRunwayLookup? runways,
        IFixLookup? fixes
    )
    {
        var resolved = ResolveApproach(approachId, airportCode, aircraft, approachLookup, runways);
        if (!resolved.Success)
        {
            return new CommandResult(false, resolved.Error);
        }

        var (procedure, approachRunway, airport) = resolved;

        // Validate intercept angle unless force
        if (!force)
        {
            var rejection = ValidateInterceptAngle(aircraft, approachRunway);
            if (rejection is not null)
            {
                return rejection;
            }
        }

        // Cancel existing speed restrictions per 7110.65 §5-7-4
        aircraft.Targets.TargetSpeed = null;

        double finalCourse = approachRunway.TrueHeading;

        // Build approach fix sequence from procedure
        var approachFixes = BuildApproachFixes(procedure, fixes);

        // For JAPP: find nearest IAF/IF ahead of aircraft
        var trimmedFixes = TrimToNearestEntry(approachFixes, aircraft);

        // Hold-in-lieu: if procedure has one and NOT straight-in, insert hold
        bool needsHold = procedure.HasHoldInLieu && !straightIn;

        // Clear existing phases
        ClearExistingPhases(aircraft);

        var clearance = new ApproachClearance
        {
            ApproachId = procedure.ApproachId,
            AirportCode = airport,
            RunwayId = procedure.Runway!,
            FinalApproachCourse = finalCourse,
            StraightIn = straightIn,
            Procedure = procedure,
            MissedApproachFixes = BuildMissedApproachFixes(procedure, fixes),
            MapHold = ExtractMissedApproachHold(procedure, fixes),
        };

        aircraft.Phases = new PhaseList { AssignedRunway = approachRunway, ActiveApproach = clearance };
        aircraft.DestinationRunway = approachRunway.Designator;

        // Insert hold-in-lieu if needed
        if (needsHold && procedure.HoldInLieuLeg is { } holdLeg)
        {
            var holdFix = trimmedFixes.FirstOrDefault(f => f.Name.Equals(holdLeg.FixIdentifier, StringComparison.OrdinalIgnoreCase));
            if (holdFix is not null)
            {
                int inboundCourse = holdLeg.OutboundCourse.HasValue ? (int)((holdLeg.OutboundCourse.Value + 180) % 360) : (int)finalCourse;

                aircraft.Phases.Add(
                    new HoldingPatternPhase
                    {
                        FixName = holdFix.Name,
                        FixLat = holdFix.Latitude,
                        FixLon = holdFix.Longitude,
                        InboundCourse = inboundCourse,
                        LegLength = holdLeg.LegDistanceNm ?? 1.0,
                        IsMinuteBased = holdLeg.LegDistanceNm is null,
                        Direction = holdLeg.TurnDirection == 'L' ? TurnDirection.Left : TurnDirection.Right,
                        MaxCircuits = 1,
                    }
                );
            }
        }

        if (trimmedFixes.Count > 0)
        {
            aircraft.Phases.Add(new ApproachNavigationPhase { Fixes = trimmedFixes });
        }

        aircraft.Phases.Add(new FinalApproachPhase());
        var isHeliApch = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        aircraft.Phases.Add(isHeliApch ? new HelicopterLandingPhase() : new LandingPhase());

        StartPhases(aircraft);

        string prefix = straightIn ? "Cleared straight-in" : "Join";
        return new CommandResult(true, $"{prefix} {procedure.ApproachId} approach, runway {procedure.Runway}");
    }

    public static CommandResult TryPtac(
        PositionTurnAltitudeClearanceCommand cmd,
        AircraftState aircraft,
        IApproachLookup? approachLookup,
        IRunwayLookup? runways,
        IFixLookup? fixes
    )
    {
        var resolved = ResolveApproach(cmd.ApproachId, null, aircraft, approachLookup, runways);
        if (!resolved.Success)
        {
            return new CommandResult(false, resolved.Error);
        }

        var (procedure, approachRunway, airport) = resolved;

        double finalCourse = approachRunway.TrueHeading;

        // Cancel existing speed restrictions per 7110.65 §5-7-4
        aircraft.Targets.TargetSpeed = null;

        // Set heading and altitude immediately
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetHeading = cmd.Heading;
        aircraft.Targets.PreferredTurnDirection = null;
        aircraft.Targets.TargetAltitude = cmd.Altitude;

        // Clear existing phases
        ClearExistingPhases(aircraft);

        var clearance = new ApproachClearance
        {
            ApproachId = procedure.ApproachId,
            AirportCode = airport,
            RunwayId = procedure.Runway!,
            FinalApproachCourse = finalCourse,
            Procedure = procedure,
            MissedApproachFixes = BuildMissedApproachFixes(procedure, fixes),
            MapHold = ExtractMissedApproachHold(procedure, fixes),
        };

        aircraft.Phases = new PhaseList { AssignedRunway = approachRunway, ActiveApproach = clearance };
        aircraft.DestinationRunway = approachRunway.Designator;

        aircraft.Phases.Add(
            new InterceptCoursePhase
            {
                FinalApproachCourse = finalCourse,
                ThresholdLat = approachRunway.ThresholdLatitude,
                ThresholdLon = approachRunway.ThresholdLongitude,
            }
        );
        aircraft.Phases.Add(new FinalApproachPhase());
        var isHeliApch = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        aircraft.Phases.Add(isHeliApch ? new HelicopterLandingPhase() : new LandingPhase());

        StartPhases(aircraft);

        return new CommandResult(
            true,
            $"Turn heading {cmd.Heading:000}, maintain {cmd.Altitude}, cleared {procedure.ApproachId} approach, runway {procedure.Runway}"
        );
    }

    public static CommandResult TryClearedVisualApproach(ClearedVisualApproachCommand cmd, AircraftState aircraft, IRunwayLookup? runways)
    {
        if (runways is null)
        {
            return new CommandResult(false, "Runway data not available");
        }

        string airport = cmd.AirportCode ?? CommandDispatcher.ResolveAirport(aircraft);
        if (string.IsNullOrEmpty(airport))
        {
            return new CommandResult(false, "Cannot determine airport for visual approach");
        }

        var runway = runways.GetRunway(airport, cmd.RunwayId);
        if (runway is null)
        {
            return new CommandResult(false, $"Unknown runway {cmd.RunwayId} at {airport}");
        }

        var approachRunway = runway.Designator.Equals(cmd.RunwayId, StringComparison.OrdinalIgnoreCase) ? runway : runway.ForApproach(cmd.RunwayId);

        // Cancel speed restrictions per 7110.65 §5-7-4
        aircraft.Targets.TargetSpeed = null;

        // Clear existing phases
        ClearExistingPhases(aircraft);

        var clearance = new ApproachClearance
        {
            ApproachId = $"VIS{cmd.RunwayId}",
            AirportCode = airport,
            RunwayId = cmd.RunwayId,
            FinalApproachCourse = approachRunway.TrueHeading,
            Procedure = null,
        };

        aircraft.Phases = new PhaseList { AssignedRunway = approachRunway, ActiveApproach = clearance };
        aircraft.DestinationRunway = approachRunway.Designator;

        // Determine execution path based on aircraft position
        double finalCourse = approachRunway.TrueHeading;
        double bearingToThreshold = GeoMath.BearingTo(
            aircraft.Latitude,
            aircraft.Longitude,
            approachRunway.ThresholdLatitude,
            approachRunway.ThresholdLongitude
        );
        double angleOff = Math.Abs(FlightPhysics.NormalizeAngle(aircraft.Heading - finalCourse));

        if (cmd.FollowCallsign is not null)
        {
            aircraft.FollowingCallsign = cmd.FollowCallsign;
        }

        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        bool isHeli = category == AircraftCategory.Helicopter;

        if (angleOff <= 30.0)
        {
            // Straight-in: direct to final approach + landing
            aircraft.Phases.Add(new FinalApproachPhase());
            aircraft.Phases.Add(isHeli ? new HelicopterLandingPhase() : new LandingPhase());
        }
        else if (angleOff <= 90.0)
        {
            // Angled join: navigate to intercept point, then final
            double interceptDistNm = category is AircraftCategory.Jet ? 5.0 : 3.0;
            var interceptPoint = ComputeInterceptPoint(approachRunway, interceptDistNm);

            aircraft.Phases.Add(new ApproachNavigationPhase { Fixes = [new ApproachFix("INTCP", interceptPoint.Lat, interceptPoint.Lon)] });
            aircraft.Phases.Add(new FinalApproachPhase());
            aircraft.Phases.Add(isHeli ? new HelicopterLandingPhase() : new LandingPhase());
        }
        else
        {
            // Pattern entry: downwind → base → final → landing
            var direction = cmd.TrafficDirection ?? DeterminePatternDirection(aircraft, approachRunway);

            var phases = PatternBuilder.BuildCircuit(approachRunway, category, direction, PatternEntryLeg.Downwind, false);
            foreach (var phase in phases)
            {
                aircraft.Phases.Add(phase);
            }
        }

        StartPhases(aircraft);

        var msg = $"Cleared visual approach runway {cmd.RunwayId}";
        if (cmd.FollowCallsign is not null)
        {
            msg += $", follow {cmd.FollowCallsign}";
        }
        return new CommandResult(true, msg);
    }

    private static (double Lat, double Lon) ComputeInterceptPoint(RunwayInfo runway, double distanceNm)
    {
        // Project back from threshold along the reciprocal of the runway heading
        double reciprocal = (runway.TrueHeading + 180.0) % 360.0;
        return GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, distanceNm);
    }

    private static PatternDirection DeterminePatternDirection(AircraftState aircraft, RunwayInfo runway)
    {
        // Determine which side of the runway the aircraft is on
        double crossTrack = GeoMath.SignedCrossTrackDistanceNm(
            aircraft.Latitude,
            aircraft.Longitude,
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            runway.TrueHeading
        );

        // Positive = right of runway heading → right downwind; negative → left downwind
        return crossTrack >= 0 ? PatternDirection.Left : PatternDirection.Right;
    }

    // --- Shared helpers ---

    internal static ResolvedApproach ResolveApproach(
        string? approachId,
        string? airportCode,
        AircraftState aircraft,
        IApproachLookup? approachLookup,
        IRunwayLookup? runways
    )
    {
        if (approachLookup is null)
        {
            return ResolvedApproach.Fail("Approach data not available");
        }

        string airport = airportCode ?? CommandDispatcher.ResolveAirport(aircraft);
        if (string.IsNullOrEmpty(airport))
        {
            return ResolvedApproach.Fail("Cannot determine airport for approach");
        }

        // Auto-resolve: when no approach ID given, try ExpectedApproach first, then assigned runway
        if (approachId is null)
        {
            approachId = aircraft.ExpectedApproach ?? aircraft.DestinationRunway ?? aircraft.DepartureRunway;
            if (approachId is null)
            {
                return ResolvedApproach.Fail("No approach ID and no runway assigned — cannot auto-resolve");
            }
        }

        string? resolvedId = approachLookup.ResolveApproachId(airport, approachId);
        if (resolvedId is null)
        {
            return ResolvedApproach.Fail($"Unknown approach: {approachId} at {airport}");
        }

        var procedure = approachLookup.GetApproach(airport, resolvedId);
        if (procedure?.Runway is null)
        {
            return ResolvedApproach.Fail($"No runway for approach {resolvedId}");
        }

        if (runways is null)
        {
            return ResolvedApproach.Fail("Runway data not available");
        }

        var runway = runways.GetRunway(airport, procedure.Runway);
        if (runway is null)
        {
            return ResolvedApproach.Fail($"Unknown runway {procedure.Runway} at {airport}");
        }

        var approachRunway = runway.Designator.Equals(procedure.Runway, StringComparison.OrdinalIgnoreCase)
            ? runway
            : runway.ForApproach(procedure.Runway);

        return new ResolvedApproach(procedure, approachRunway, airport);
    }

    internal static CommandResult? ValidateInterceptAngle(AircraftState aircraft, RunwayInfo runway)
    {
        double finalCourse = runway.TrueHeading;
        double interceptAngle = Math.Abs(FlightPhysics.NormalizeAngle(aircraft.Heading - finalCourse));

        // Helicopters: 45° max intercept angle per §5-9-2
        bool isHelicopter = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        if (isHelicopter)
        {
            if (interceptAngle > 45.0)
            {
                return new CommandResult(false, $"Intercept angle {interceptAngle:F0}° exceeds 45° helicopter limit [7110.65 §5-9-2]");
            }

            return null;
        }

        // Fixed-wing: distance-based angle limits per TBL 5-9-1
        // Compute where aircraft's track intersects the final approach course.
        // The FAC extends from the threshold along the reciprocal of the runway heading.
        double facReciprocalDeg = (finalCourse + 180) % 360;

        // Along-track distance of aircraft position relative to threshold on the FAC.
        // Positive = on the approach side (away from airport along reciprocal).
        double aircraftAlongTrack = GeoMath.AlongTrackDistanceNm(
            aircraft.Latitude,
            aircraft.Longitude,
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            facReciprocalDeg
        );

        double crossTrack = GeoMath.SignedCrossTrackDistanceNm(
            aircraft.Latitude,
            aircraft.Longitude,
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            facReciprocalDeg
        );

        // Compute along-track distance of the interception point from the threshold.
        // Using trig: the aircraft is at (crossTrack, alongTrack) in the FAC frame.
        // Its heading relative to the FAC determines where it crosses.
        double relativeAngleRad = FlightPhysics.NormalizeAngle(aircraft.Heading - finalCourse) * Math.PI / 180.0;
        double tanAngle = Math.Tan(relativeAngleRad);

        double interceptAlongTrack;
        if (Math.Abs(tanAngle) < 0.001)
        {
            // Nearly parallel — use aircraft's along-track as approximation
            interceptAlongTrack = aircraftAlongTrack;
        }
        else
        {
            // Distance along FAC from aircraft's along-track to where it crosses the course
            interceptAlongTrack = aircraftAlongTrack - (crossTrack / tanAngle);
        }

        // The approach gate is on the FAC at (minIntercept - 2nm) from the threshold.
        double minIntercept = ApproachGateDatabase.GetMinInterceptDistanceNm(runway.AirportId, runway.Designator);
        double approachGateAlongTrack = minIntercept - 2.0;

        // Distance from interception point to approach gate (along the FAC)
        double distToGate = interceptAlongTrack - approachGateAlongTrack;

        // TBL 5-9-1: distance from interception point to approach gate
        double maxAngle = distToGate < 2.0 ? 20.0 : 30.0;

        if (interceptAngle > maxAngle)
        {
            return new CommandResult(
                false,
                $"Intercept angle {interceptAngle:F0}° exceeds {maxAngle:F0}° limit ({distToGate:F1}nm from approach gate) [7110.65 §5-9-2]"
            );
        }

        return null;
    }

    public static IReadOnlyList<string> GetApproachFixNames(CifpApproachProcedure procedure)
    {
        var names = new List<string>();
        foreach (var leg in procedure.CommonLegs)
        {
            if (string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }
            if (leg.FixRole == CifpFixRole.MAHP)
            {
                break;
            }
            names.Add(leg.FixIdentifier);
        }
        return names;
    }

    internal static List<ApproachFix> BuildMissedApproachFixes(CifpApproachProcedure procedure, IFixLookup? fixes)
    {
        var result = new List<ApproachFix>();
        (double Lat, double Lon)? previousFixPos = null;

        foreach (var leg in procedure.MissedApproachLegs)
        {
            if (string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }

            if (leg.PathTerminator == CifpPathTerminator.PI)
            {
                continue;
            }

            var pos = fixes?.GetFixPosition(leg.FixIdentifier);
            if (pos is null)
            {
                continue;
            }

            if (
                leg.PathTerminator == CifpPathTerminator.RF
                && leg.ArcCenterLat is not null
                && leg.ArcCenterLon is not null
                && leg.ArcRadiusNm is not null
                && previousFixPos is not null
            )
            {
                ExpandApproachArcFixes(
                    result,
                    leg.ArcCenterLat.Value,
                    leg.ArcCenterLon.Value,
                    leg.ArcRadiusNm.Value,
                    previousFixPos.Value,
                    pos.Value,
                    leg.TurnDirection == 'R'
                );
            }

            if (
                leg.PathTerminator == CifpPathTerminator.AF
                && leg.RecommendedNavaidId is not null
                && leg.Rho is not null
                && previousFixPos is not null
            )
            {
                var navaidPos = fixes?.GetFixPosition(leg.RecommendedNavaidId);
                if (navaidPos is not null)
                {
                    ExpandApproachArcFixes(
                        result,
                        navaidPos.Value.Lat,
                        navaidPos.Value.Lon,
                        leg.Rho.Value,
                        previousFixPos.Value,
                        pos.Value,
                        leg.TurnDirection != 'L'
                    );
                }
            }

            result.Add(
                new ApproachFix(leg.FixIdentifier, pos.Value.Lat, pos.Value.Lon, leg.Altitude, leg.Speed?.SpeedKts, leg.FixRole, leg.IsFlyOver)
            );
            previousFixPos = (pos.Value.Lat, pos.Value.Lon);
        }

        return result;
    }

    /// <summary>
    /// Build MAP navigation phases from pre-built missed approach fixes on the active approach clearance.
    /// Returns empty if no instrument approach, no procedure, or no MAP fixes.
    /// </summary>
    internal static List<Phase> BuildMissedApproachPhases(AircraftState aircraft)
    {
        var clearance = aircraft.Phases?.ActiveApproach;
        if (clearance?.Procedure is null || clearance.MissedApproachFixes.Count == 0)
        {
            return [];
        }

        // Visual approaches have no MAP
        if (clearance.ApproachId.StartsWith("VIS", StringComparison.Ordinal))
        {
            return [];
        }

        var phases = new List<Phase> { new ApproachNavigationPhase { Fixes = clearance.MissedApproachFixes } };

        if (clearance.MapHold is { } hold)
        {
            phases.Add(
                new HoldingPatternPhase
                {
                    FixName = hold.FixName,
                    FixLat = hold.FixLat,
                    FixLon = hold.FixLon,
                    InboundCourse = hold.InboundCourse,
                    LegLength = hold.LegLength,
                    IsMinuteBased = hold.IsMinuteBased,
                    Direction = hold.Direction,
                    MaxCircuits = null,
                }
            );
        }

        return phases;
    }

    /// <summary>
    /// Extract the target altitude for GoAroundPhase from the first MAP fix altitude restriction.
    /// Returns null if no altitude restriction found (caller falls back to default).
    /// </summary>
    internal static int? GetMissedApproachAltitude(IReadOnlyList<ApproachFix> mapFixes)
    {
        foreach (var fix in mapFixes)
        {
            if (fix.Altitude is { } alt)
            {
                return alt.Altitude1Ft;
            }
        }

        return null;
    }

    /// <summary>
    /// Extract holding pattern data from the MAP legs (HA/HF/HM terminator on the last fix).
    /// Returns null if no hold leg exists or the fix position can't be resolved.
    /// </summary>
    internal static MissedApproachHold? ExtractMissedApproachHold(CifpApproachProcedure procedure, IFixLookup? fixes)
    {
        for (int i = procedure.MissedApproachLegs.Count - 1; i >= 0; i--)
        {
            var leg = procedure.MissedApproachLegs[i];
            if (leg.PathTerminator is not (CifpPathTerminator.HA or CifpPathTerminator.HF or CifpPathTerminator.HM))
            {
                continue;
            }

            if (string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }

            var pos = fixes?.GetFixPosition(leg.FixIdentifier);
            if (pos is null)
            {
                continue;
            }

            int inboundCourse = leg.OutboundCourse.HasValue ? (int)((leg.OutboundCourse.Value + 180) % 360) : 0;
            double legLength = leg.LegDistanceNm ?? 1.0;
            bool isMinuteBased = leg.LegDistanceNm is null;
            var direction = leg.TurnDirection == 'L' ? TurnDirection.Left : TurnDirection.Right;

            return new MissedApproachHold(leg.FixIdentifier, pos.Value.Lat, pos.Value.Lon, inboundCourse, legLength, isMinuteBased, direction);
        }

        return null;
    }

    private static List<ApproachFix> BuildApproachFixes(CifpApproachProcedure procedure, IFixLookup? fixes)
    {
        var result = new List<ApproachFix>();
        (double Lat, double Lon)? previousFixPos = null;

        foreach (var leg in procedure.CommonLegs)
        {
            if (string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }

            // Skip past MAHP (missed approach) — those are in MissedApproachLegs already
            if (leg.FixRole == CifpFixRole.MAHP)
            {
                break;
            }

            // Skip procedure turn legs (handled by hold-in-lieu)
            if (leg.PathTerminator == CifpPathTerminator.PI)
            {
                continue;
            }

            var pos = fixes?.GetFixPosition(leg.FixIdentifier);
            if (pos is null)
            {
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
                ExpandApproachArcFixes(
                    result,
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
                var navaidPos = fixes?.GetFixPosition(leg.RecommendedNavaidId);
                if (navaidPos is not null)
                {
                    ExpandApproachArcFixes(
                        result,
                        navaidPos.Value.Lat,
                        navaidPos.Value.Lon,
                        leg.Rho.Value,
                        previousFixPos.Value,
                        pos.Value,
                        leg.TurnDirection != 'L'
                    );
                }
            }

            result.Add(
                new ApproachFix(
                    leg.FixIdentifier,
                    pos.Value.Lat,
                    pos.Value.Lon,
                    leg.Altitude,
                    leg.Speed?.SpeedKts,
                    leg.FixRole,
                    leg.IsFlyOver || leg.FixRole is CifpFixRole.FAF or CifpFixRole.MAHP
                )
            );
            previousFixPos = (pos.Value.Lat, pos.Value.Lon);
        }

        return result;
    }

    private static void ExpandApproachArcFixes(
        List<ApproachFix> result,
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

        // Insert intermediate points (skip the last one — that's the terminator fix)
        for (int i = 0; i < arcPoints.Count - 1; i++)
        {
            result.Add(new ApproachFix($"ARC{i + 1:D2}", arcPoints[i].Lat, arcPoints[i].Lon));
        }
    }

    private static List<ApproachFix> TrimToNearestEntry(List<ApproachFix> fixes, AircraftState aircraft)
    {
        if (fixes.Count == 0)
        {
            return fixes;
        }

        // Find nearest IAF or IF ahead of aircraft
        int bestIndex = -1;
        double bestDist = double.MaxValue;

        for (int i = 0; i < fixes.Count; i++)
        {
            var fix = fixes[i];
            if (fix.Role is not (CifpFixRole.IAF or CifpFixRole.IF))
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, fix.Latitude, fix.Longitude);
            double angleDiff = Math.Abs(FlightPhysics.NormalizeAngle(bearing - aircraft.Heading));

            // Only consider fixes within ±90° of current heading
            if (angleDiff > 90)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, fix.Latitude, fix.Longitude);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = i;
            }
        }

        if (bestIndex >= 0)
        {
            return fixes.GetRange(bestIndex, fixes.Count - bestIndex);
        }

        // Fallback: use all fixes
        return fixes;
    }

    private static void ClearExistingPhases(AircraftState aircraft)
    {
        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
        }

        // Clear visual approach state
        aircraft.HasReportedFieldInSight = false;
        aircraft.HasReportedTrafficInSight = false;
        aircraft.FollowingCallsign = null;
    }

    private static void StartPhases(AircraftState aircraft)
    {
        if (aircraft.Phases is not null)
        {
            var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Start(startCtx);
        }
    }

    internal readonly record struct ResolvedApproach
    {
        public bool Success { get; }
        public string? Error { get; }
        public CifpApproachProcedure? Procedure { get; }
        public RunwayInfo? Runway { get; }
        public string? Airport { get; }

        public ResolvedApproach(CifpApproachProcedure procedure, RunwayInfo runway, string airport)
        {
            Success = true;
            Procedure = procedure;
            Runway = runway;
            Airport = airport;
        }

        private ResolvedApproach(string error)
        {
            Success = false;
            Error = error;
        }

        public static ResolvedApproach Fail(string error) => new(error);

        public void Deconstruct(out CifpApproachProcedure procedure, out RunwayInfo runway, out string airport)
        {
            procedure = Procedure!;
            runway = Runway!;
            airport = Airport!;
        }
    }
}
