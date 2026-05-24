using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Sim;

public static class FlightPhysics
{
    private static readonly ILogger Log = SimLog.CreateLogger("FlightPhysics");

    private const double HeadingSnapDeg = 0.5;
    private const double AltitudeSnapFt = 10.0;
    private const double SpeedSnapKts = 2.0;
    private const double NavArrivalNm = 0.5;
    private const double FrdArrivalNm = 1.5;
    private const double GroundArrivalNm = 0.05;
    private const double RadialInterceptDeg = 3.0;
    private const double FrdMissThresholdNm = 5.0;
    private const double FrdMissDepartureNm = 0.5;
    private const double DegToRad = Math.PI / 180.0;
    private const double NmPerDegLat = 60.0;

    public static void Update(AircraftState aircraft, double deltaSeconds)
    {
        Update(aircraft, deltaSeconds, aircraftLookup: null, weather: null, soloTrainingMode: false, rpoShowPilotSpeech: false);
    }

    public static void Update(AircraftState aircraft, double deltaSeconds, Func<string, AircraftState?>? aircraftLookup)
    {
        Update(aircraft, deltaSeconds, aircraftLookup, weather: null, soloTrainingMode: false, rpoShowPilotSpeech: false);
    }

    public static void Update(AircraftState aircraft, double deltaSeconds, Func<string, AircraftState?>? aircraftLookup, WeatherProfile? weather)
    {
        Update(aircraft, deltaSeconds, aircraftLookup, weather, soloTrainingMode: false, rpoShowPilotSpeech: false);
    }

    public static void Update(
        AircraftState aircraft,
        double deltaSeconds,
        Func<string, AircraftState?>? aircraftLookup,
        WeatherProfile? weather,
        bool soloTrainingMode,
        bool rpoShowPilotSpeech
    )
    {
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);

        // Recompute magnetic declination only when the aircraft has moved materially.
        // WMM is a degree-12 spherical-harmonic evaluation (~0.06 ms/call) — at 4 Hz per
        // aircraft this dominates per-tick physics. Declination changes ~0.01°/km IRL,
        // so reusing a cached value for sub-nm motion is invisible for heading/wind use.
        // Box threshold in degrees: 0.02° ≈ 1.2 nm of latitude, conservative at all lats.
        const double DeclinationCacheThresholdDeg = 0.02;
        if (
            aircraft.DeclinationCachePosition is not { } cached
            || (Math.Abs(aircraft.Position.Lat - cached.Lat) > DeclinationCacheThresholdDeg)
            || (Math.Abs(aircraft.Position.Lon - cached.Lon) > DeclinationCacheThresholdDeg)
        )
        {
            // Skip declination update if position is non-finite or out of range. Geo.Coordinate's
            // ctor would throw, crashing the tick. Logging the bad state lets the upstream cause
            // be investigated without taking down the sim. Keep the previously cached value.
            if (
                !double.IsFinite(aircraft.Position.Lat)
                || !double.IsFinite(aircraft.Position.Lon)
                || Math.Abs(aircraft.Position.Lat) > 90
                || Math.Abs(aircraft.Position.Lon) > 180
            )
            {
                Log.LogError(
                    "Aircraft position out of range, skipping declination update: callsign={CS} pos=({Lat},{Lon}) phase={Phase}",
                    aircraft.Callsign,
                    aircraft.Position.Lat,
                    aircraft.Position.Lon,
                    aircraft.Phases?.CurrentPhase?.Name ?? "(none)"
                );
            }
            else
            {
                aircraft.Declination = MagneticDeclination.GetDeclination(aircraft.Position);
                aircraft.DeclinationCachePosition = aircraft.Position;
            }
        }

        // Backward compat: airborne aircraft without IAS initialized derive it from GS.
        if (!aircraft.IsOnGround && aircraft.IndicatedAirspeed <= 0 && aircraft.GroundSpeed > 0)
        {
            aircraft.IndicatedAirspeed = aircraft.GroundSpeed;
        }

        UpdateNavigation(aircraft, weather);
        UpdateDescentPlanning(aircraft, cat);
        UpdateClimbPlanning(aircraft, cat);
        UpdateSpeedPlanning(aircraft, cat);
        UpdateHeading(aircraft, cat, deltaSeconds);
        UpdateAltitude(aircraft, cat, deltaSeconds);
        UpdateSpeed(aircraft, cat, deltaSeconds);
        AutoCancelSpeedAtFinal(aircraft);
        UpdatePosition(aircraft, deltaSeconds, weather);
        UpdateCommandQueue(aircraft, deltaSeconds, aircraftLookup);
        UpdateGiveWayResume(aircraft, aircraftLookup);
        PilotObservationUpdater.Update(aircraft, aircraftLookup, weather, soloTrainingMode, rpoShowPilotSpeech);
    }

    private static void UpdateNavigation(AircraftState aircraft, WeatherProfile? weather)
    {
        var route = aircraft.Targets.NavigationRoute;
        if (route.Count == 0)
        {
            if (aircraft.Approach.PendingClearance is { } pendingEarly)
            {
                ApproachCommandHandler.ActivatePendingApproach(aircraft, pendingEarly);
            }

            return;
        }

        var nav = route[0];
        double distNm = GeoMath.DistanceNm(aircraft.Position, nav.Position);

        // Determine sequencing threshold: fly-by waypoints with a following waypoint
        // use turn anticipation; fly-over and terminal waypoints use NavArrivalNm.
        double threshold = NavArrivalNm;
        double anticipationNm = 0;
        bool inAnticipationZone;

        if (route.Count >= 2 && !nav.IsFlyOver)
        {
            double currentLegBearing = GeoMath.BearingTo(aircraft.Position, nav.Position);
            double nextLegBearing = GeoMath.BearingTo(nav.Position, route[1].Position);
            double turnRate =
                aircraft.Targets.TurnRateOverride
                ?? AircraftPerformance.TurnRate(aircraft.AircraftType, AircraftCategorization.Categorize(aircraft.AircraftType));
            anticipationNm = ComputeAnticipationDistanceNm(aircraft.GroundSpeed, turnRate, currentLegBearing, nextLegBearing);
            threshold = Math.Max(anticipationNm, NavArrivalNm);
        }

        inAnticipationZone = anticipationNm > NavArrivalNm && distNm < threshold;

        // Sequence waypoint: for fly-by with anticipation, wait until the aircraft
        // has passed abeam the waypoint (along-track goes negative along the next leg bearing).
        // For fly-over or last waypoint, sequence at NavArrivalNm.
        bool shouldSequence;
        if (inAnticipationZone && route.Count >= 2)
        {
            double nextLegBearing = GeoMath.BearingTo(nav.Position, route[1].Position);
            double alongTrack = GeoMath.AlongTrackDistanceNmRaw(aircraft.Position, nav.Position, nextLegBearing);
            shouldSequence = alongTrack >= 0;
        }
        else
        {
            shouldSequence = distNm < NavArrivalNm;
        }

        if (shouldSequence)
        {
            var sequenced = nav;
            route.RemoveAt(0);

            // Fire any AT fix triggers in the command queue for this fix
            NotifyFixSequenced(aircraft, sequenced.Name);

            // Restore altitude/speed from revert fields when sequencing past a constrained fix
            if (sequenced.RevertAltitude is not null)
            {
                aircraft.Targets.TargetAltitude = sequenced.RevertAltitude;
                aircraft.Targets.AssignedAltitude = sequenced.RevertAssignedAltitude;
                aircraft.Targets.DesiredVerticalRate = null;
            }

            if (sequenced.RevertSpeed is not null)
            {
                aircraft.Targets.TargetSpeed = sequenced.RevertSpeed;
                aircraft.Targets.AssignedSpeed = sequenced.RevertAssignedSpeed;
            }

            if (route.Count == 0)
            {
                if (aircraft.Approach.PendingClearance is { } pending)
                {
                    ApproachCommandHandler.ActivatePendingApproach(aircraft, pending);
                    return;
                }

                // AIM 5-4-1 NOTE 2: after the procedure's terminating fix the pilot
                // maintains the last published speed until ATC intervenes. Publish
                // it as a ceiling so the auto speed schedule cannot accelerate the
                // aircraft above it. An explicit ATC speed (HasExplicitSpeedCommand)
                // wins — leave the procedural memory aside in that case.
                if (!aircraft.Targets.HasExplicitSpeedCommand && aircraft.Procedure.LastProcedureSpeedKts is { } lastProcSpeed)
                {
                    aircraft.Targets.SpeedCeiling = lastProcSpeed;
                }

                aircraft.Targets.TargetTrueHeading = null;
                aircraft.Targets.PreferredTurnDirection = null;
                ClearProcedureState(aircraft);
                return;
            }

            nav = route[0];
            ApplyFixConstraints(aircraft, nav);
        }

        // Compute steering heading
        double bearing;
        if (inAnticipationZone && !shouldSequence && route.Count >= 2)
        {
            // Arc-blended steering: compute tangent heading along inscribed turn circle
            double currentLegBearing = GeoMath.BearingTo(aircraft.Position, nav.Position);
            double nextLegBearing = GeoMath.BearingTo(nav.Position, route[1].Position);
            double turnRate =
                aircraft.Targets.TurnRateOverride
                ?? AircraftPerformance.TurnRate(aircraft.AircraftType, AircraftCategorization.Categorize(aircraft.AircraftType));
            bearing = ComputeArcBlendedHeading(aircraft.Position, aircraft.GroundSpeed, turnRate, nav.Position, currentLegBearing, nextLegBearing);
        }
        else
        {
            bearing = GeoMath.BearingTo(aircraft.Position, nav.Position);
        }

        // Apply wind correction angle so the aircraft flies a straight ground track, not a pursuit curve.
        double wca = 0;
        if (weather is not null && !aircraft.IsOnGround && aircraft.IndicatedAirspeed > 0)
        {
            double tas = WindInterpolator.IasToTas(aircraft.IndicatedAirspeed, aircraft.Altitude);
            var wind = WindInterpolator.GetWindAt(weather, aircraft.Altitude);
            wca = WindInterpolator.ComputeWindCorrectionAngle(bearing, tas, wind.DirectionDeg, wind.SpeedKts);
        }

        aircraft.Targets.TargetTrueHeading = new TrueHeading(bearing + wca);
        // PreferredTurnDirection is NOT cleared here. It is cleared when the heading
        // is reached (UpdateHeading snap) or when the route is exhausted. Clearing it
        // every tick would stomp on direction preferences set by departure phases
        // (e.g., TRDCT/TLDCT) before the initial turn completes.
    }

    /// <summary>
    /// Step-descent planning for STAR via mode. Finds the next altitude constraint
    /// in the route and computes the descent rate required to meet it at the fix.
    /// Adjusts the descent rate (steeper or shallower) rather than using a fixed rate
    /// with a distance-based trigger.
    /// </summary>
    private static void UpdateDescentPlanning(AircraftState aircraft, AircraftCategory cat)
    {
        if (aircraft.IsOnGround || aircraft.GroundSpeed <= 0)
        {
            return;
        }

        // When SidViaMode is active, only climb planning handles constraints
        if (aircraft.Procedure.SidViaMode)
        {
            return;
        }

        var route = aircraft.Targets.NavigationRoute;
        if (route.Count == 0)
        {
            return;
        }

        // Activate when via mode is on OR when the route has any altitude constraint
        bool hasRouteConstraints = false;
        for (int j = 0; j < route.Count; j++)
        {
            if (route[j].AltitudeRestriction is not null)
            {
                hasRouteConstraints = true;
                break;
            }
        }

        if (!aircraft.Procedure.StarViaMode && !hasRouteConstraints)
        {
            return;
        }

        // Find the NEXT constrained fix (step descent: one constraint at a time)
        double cumulativeDistNm = GeoMath.DistanceNm(aircraft.Position, route[0].Position);

        for (int i = 0; i < route.Count; i++)
        {
            if (i > 0)
            {
                cumulativeDistNm += GeoMath.DistanceNm(route[i - 1].Position, route[i].Position);
            }

            if (route[i].AltitudeRestriction is not { } restriction)
            {
                continue;
            }

            // When not in via mode, infer direction from current altitude vs constraint
            bool isDescending = aircraft.Procedure.StarViaMode || aircraft.Altitude > restriction.Altitude1Ft;
            double? resolvedAlt = ResolveAltitudeRestriction(aircraft, restriction, isDescending);
            if (resolvedAlt is not { } targetAlt)
            {
                continue;
            }

            // Only handle descent constraints here; climb is handled by UpdateClimbPlanning
            if (targetAlt >= aircraft.Altitude)
            {
                continue;
            }

            // Apply STAR via floor (only in via mode)
            if (aircraft.Procedure.StarViaMode && aircraft.Procedure.StarViaFloor is { } floor)
            {
                targetAlt = Math.Max(targetAlt, floor);
            }

            double altDelta = aircraft.Altitude - targetAlt;
            if (altDelta <= 0)
            {
                continue;
            }

            // Set target altitude for this step
            aircraft.Targets.TargetAltitude = targetAlt;

            // Compute the descent rate required to reach targetAlt at the fix
            double standardRate = AircraftPerformance.DescentRate(aircraft.AircraftType, cat, aircraft.Altitude);
            double timeMinutes = cumulativeDistNm / (aircraft.GroundSpeed / 60.0);

            if (timeMinutes > 0.1)
            {
                double requiredFpm = altDelta / timeMinutes;
                // Cap at 2× standard rate; no minimum — use a gentle rate if there's plenty of distance
                double maxRate = standardRate * 2.0;
                double rate = Math.Min(requiredFpm, maxRate);
                aircraft.Targets.DesiredVerticalRate = -rate;
            }
            else
            {
                // Almost at the fix — descend at max rate
                aircraft.Targets.DesiredVerticalRate = -(standardRate * 2.0);
            }

            break; // Step descent: only target the next constraint
        }
    }

    /// <summary>
    /// Step-climb planning for SID via mode. Symmetric to descent planning:
    /// finds the next altitude constraint and computes the climb rate required
    /// to meet it at the fix.
    /// </summary>
    private static void UpdateClimbPlanning(AircraftState aircraft, AircraftCategory cat)
    {
        if (aircraft.IsOnGround || aircraft.GroundSpeed <= 0)
        {
            return;
        }

        // When StarViaMode is active, only descent planning handles constraints
        if (aircraft.Procedure.StarViaMode)
        {
            return;
        }

        var route = aircraft.Targets.NavigationRoute;
        if (route.Count == 0)
        {
            return;
        }

        // Activate when via mode is on OR when the route has any altitude constraint
        bool hasRouteConstraints = false;
        for (int j = 0; j < route.Count; j++)
        {
            if (route[j].AltitudeRestriction is not null)
            {
                hasRouteConstraints = true;
                break;
            }
        }

        if (!aircraft.Procedure.SidViaMode && !hasRouteConstraints)
        {
            return;
        }

        double cumulativeDistNm = GeoMath.DistanceNm(aircraft.Position, route[0].Position);

        for (int i = 0; i < route.Count; i++)
        {
            if (i > 0)
            {
                cumulativeDistNm += GeoMath.DistanceNm(route[i - 1].Position, route[i].Position);
            }

            if (route[i].AltitudeRestriction is not { } restriction)
            {
                continue;
            }

            // When not in via mode, infer direction from current altitude vs constraint
            bool isDescending = !aircraft.Procedure.SidViaMode && aircraft.Altitude > restriction.Altitude1Ft;
            double? resolvedAlt = ResolveAltitudeRestriction(aircraft, restriction, isDescending);
            if (resolvedAlt is not { } targetAlt)
            {
                continue;
            }

            // Only handle climb constraints here; descent is handled by UpdateDescentPlanning
            if (targetAlt <= aircraft.Altitude)
            {
                continue;
            }

            // Apply SID via ceiling (only in via mode)
            if (aircraft.Procedure.SidViaMode && aircraft.Procedure.SidViaCeiling is { } ceiling)
            {
                targetAlt = Math.Min(targetAlt, ceiling);
            }

            double altDelta = targetAlt - aircraft.Altitude;
            if (altDelta <= 0)
            {
                continue;
            }

            // Set target altitude for this step
            aircraft.Targets.TargetAltitude = targetAlt;

            // Compute the climb rate required to reach targetAlt at the fix
            double standardRate = AircraftPerformance.ClimbRate(aircraft.AircraftType, cat, aircraft.Altitude);
            double timeMinutes = cumulativeDistNm / (aircraft.GroundSpeed / 60.0);

            if (timeMinutes > 0.1)
            {
                double requiredFpm = altDelta / timeMinutes;
                // Cap at 2× standard rate; no minimum — use a gentle rate if there's plenty of distance
                double maxRate = standardRate * 2.0;
                double rate = Math.Min(requiredFpm, maxRate);
                aircraft.Targets.DesiredVerticalRate = rate;
            }
            else
            {
                // Almost at the fix — climb at max rate
                aircraft.Targets.DesiredVerticalRate = standardRate * 2.0;
            }

            break; // Step climb: only target the next constraint
        }
    }

    /// <summary>
    /// Speed look-ahead planning for procedure fixes. Scans the navigation route for the
    /// first fix with a speed restriction, computes time-to-fix vs acceleration/deceleration
    /// time, and sets TargetSpeed proactively so the aircraft meets the constraint at the fix
    /// rather than reacting after sequencing past it.
    /// </summary>
    private static void UpdateSpeedPlanning(AircraftState aircraft, AircraftCategory cat)
    {
        if (aircraft.IsOnGround || aircraft.GroundSpeed <= 0)
        {
            return;
        }

        // Don't override controller-issued speed commands or Mach hold
        if (aircraft.Targets.HasExplicitSpeedCommand || aircraft.Procedure.SpeedRestrictionsDeleted || (aircraft.Targets.TargetMach is not null))
        {
            return;
        }

        var route = aircraft.Targets.NavigationRoute;
        if (route.Count == 0)
        {
            return;
        }

        bool speedLimitWaived = AircraftPerformance.IsSpeedLimitWaived(aircraft.AircraftType);
        double cumulativeDistNm = GeoMath.DistanceNm(aircraft.Position, route[0].Position);

        for (int i = 0; i < route.Count; i++)
        {
            if (i > 0)
            {
                cumulativeDistNm += GeoMath.DistanceNm(route[i - 1].Position, route[i].Position);
            }

            if (route[i].SpeedRestriction is not { } restriction)
            {
                continue;
            }

            double constraintSpeed = restriction.SpeedKts;

            // 14 CFR 91.117: cap at 250 KIAS below 10,000 ft MSL
            if (aircraft.Altitude < 10_000 && !speedLimitWaived)
            {
                constraintSpeed = Math.Min(constraintSpeed, 250);
            }

            // Clamp to active floor/ceiling
            if (aircraft.Targets.SpeedFloor is { } floor)
            {
                constraintSpeed = Math.Max(constraintSpeed, floor);
            }

            if (aircraft.Targets.SpeedCeiling is { } ceiling)
            {
                constraintSpeed = Math.Min(constraintSpeed, ceiling);
            }

            double speedDelta = Math.Abs(aircraft.IndicatedAirspeed - constraintSpeed);
            if (speedDelta < SpeedSnapKts)
            {
                // Pin TargetSpeed to the constraint so the auto speed schedule
                // doesn't override it while the constrained fix is still ahead.
                aircraft.Targets.TargetSpeed = constraintSpeed;
                break;
            }

            bool needsDecel = aircraft.IndicatedAirspeed > constraintSpeed;
            double rate = needsDecel
                ? AircraftPerformance.DecelRate(aircraft.AircraftType, cat)
                : AircraftPerformance.AccelRate(aircraft.AircraftType, cat);

            double changeTimeSeconds = speedDelta / rate;
            double timeToFixSeconds = cumulativeDistNm / (aircraft.GroundSpeed / 3600.0);

            // Acceleration: set TargetSpeed immediately — there's no benefit to
            // delaying, and starting early ensures the aircraft meets the constraint.
            // Deceleration: wait until time-to-fix is within 10% of change time so the
            // aircraft doesn't slow down prematurely.
            if (!needsDecel || (timeToFixSeconds <= changeTimeSeconds * 1.1))
            {
                aircraft.Targets.TargetSpeed = constraintSpeed;
            }

            break; // Step planning: only target the first speed-constrained fix
        }
    }

    /// <summary>
    /// Computes the turn anticipation distance for a fly-by waypoint.
    /// Based on turn radius (from ground speed and turn rate) and the course change angle.
    /// Returns 0 for negligible turns (&lt;1°), capped at 5nm.
    /// </summary>
    internal static double ComputeAnticipationDistanceNm(
        double groundSpeedKts,
        double turnRateDegPerSec,
        double currentLegBearing,
        double nextLegBearing
    )
    {
        double courseChange = Math.Abs(NormalizeAngle(nextLegBearing - currentLegBearing));
        if (courseChange < 1.0)
        {
            return 0;
        }

        // Turn radius: R = GS / (turnRate × 2π) in nm (GS in nm/hr, turnRate in deg/s → rad/s)
        double turnRateRadPerSec = turnRateDegPerSec * Math.PI / 180.0;
        double gsNmPerSec = groundSpeedKts / 3600.0;
        double radiusNm = gsNmPerSec / turnRateRadPerSec;

        double halfAngleRad = courseChange * Math.PI / 360.0;
        double anticipation = radiusNm * Math.Tan(halfAngleRad);

        return Math.Min(anticipation, 5.0);
    }

    /// <summary>
    /// Computes an arc-blended heading for smooth fly-by turns.
    /// Finds the inscribed turn circle tangent to both legs and returns the tangent heading.
    /// </summary>
    internal static double ComputeArcBlendedHeading(
        LatLon aircraftPos,
        double groundSpeedKts,
        double turnRateDegPerSec,
        LatLon waypointPos,
        double currentLegBearing,
        double nextLegBearing
    )
    {
        double courseChange = NormalizeAngle(nextLegBearing - currentLegBearing);
        if (Math.Abs(courseChange) < 1.0)
        {
            return GeoMath.BearingTo(aircraftPos, waypointPos);
        }

        // Turn radius
        double turnRateRadPerSec = turnRateDegPerSec * Math.PI / 180.0;
        double gsNmPerSec = groundSpeedKts / 3600.0;
        double radiusNm = gsNmPerSec / turnRateRadPerSec;

        // Turn center: offset perpendicular to the bisector of the two legs
        bool turnRight = courseChange > 0;
        double bisector = NormalizeBearing(currentLegBearing + courseChange / 2.0);
        double perpBearing = turnRight ? bisector + 90.0 : bisector - 90.0;
        perpBearing = NormalizeBearing(perpBearing);

        // Offset distance from waypoint to turn center
        double halfAngleRad = Math.Abs(courseChange) * Math.PI / 360.0;
        double cosHalf = Math.Cos(halfAngleRad);
        double offsetNm = cosHalf > 0.01 ? radiusNm / cosHalf : radiusNm;

        var center = GeoMath.ProjectPointRaw(waypointPos, perpBearing, offsetNm);

        // Aircraft's bearing from turn center
        double radialFromCenter = GeoMath.BearingTo(center, aircraftPos);

        // Tangent heading = perpendicular to radial, in turn direction
        double tangent = turnRight ? radialFromCenter + 90.0 : radialFromCenter - 90.0;
        return NormalizeBearing(tangent);
    }

    /// <summary>
    /// Applies altitude and speed constraints from a navigation target when via mode is active.
    /// SID via mode enforces climb restrictions; STAR via mode enforces descent restrictions.
    /// </summary>
    internal static void ApplyFixConstraints(AircraftState aircraft, NavigationTarget target)
    {
        bool sidVia = aircraft.Procedure.SidViaMode;
        bool starVia = aircraft.Procedure.StarViaMode;
        bool hasConstraint = target.AltitudeRestriction is not null || target.SpeedRestriction is not null;

        if (!sidVia && !starVia && !hasConstraint)
        {
            return;
        }

        if (target.AltitudeRestriction is { } alt)
        {
            // When not in via mode, infer direction from current altitude vs constraint
            bool isDescending = starVia || (!sidVia && aircraft.Altitude > alt.Altitude1Ft);
            double? resolvedAlt = ResolveAltitudeRestriction(aircraft, alt, isDescending);
            if (resolvedAlt is { } targetAlt)
            {
                // Apply ceiling/floor limits (only in via mode)
                if (sidVia && aircraft.Procedure.SidViaCeiling is { } ceiling)
                {
                    targetAlt = Math.Min(targetAlt, ceiling);
                }

                if (starVia && aircraft.Procedure.StarViaFloor is { } floor)
                {
                    targetAlt = Math.Max(targetAlt, floor);
                }

                aircraft.Targets.TargetAltitude = targetAlt;
            }
        }

        if (target.SpeedRestriction is { } spd && !aircraft.Procedure.SpeedRestrictionsDeleted)
        {
            double targetSpeed = spd.SpeedKts;

            // 14 CFR 91.117: cap speed restrictions at 250 KIAS below 10,000 ft MSL.
            if (!aircraft.IsOnGround && aircraft.Altitude < 10_000)
            {
                targetSpeed = Math.Min(targetSpeed, 250);
            }

            // Clamp via-mode speed to active floor/ceiling
            if (aircraft.Targets.SpeedFloor is { } floor)
            {
                targetSpeed = Math.Max(targetSpeed, floor);
            }

            if (aircraft.Targets.SpeedCeiling is { } ceiling)
            {
                targetSpeed = Math.Min(targetSpeed, ceiling);
            }

            aircraft.Targets.TargetSpeed = targetSpeed;

            // AIM 5-4-1 NOTE 2: remember the last published speed so that if the
            // procedure terminates without further ATC instruction the aircraft
            // does not accelerate beyond it.
            aircraft.Procedure.LastProcedureSpeedKts = targetSpeed;
        }
    }

    /// <summary>
    /// Resolves an altitude restriction to a concrete target altitude based on the aircraft's
    /// current altitude and the restriction type (At, AtOrAbove, AtOrBelow, Between).
    /// Returns null if the aircraft already satisfies the restriction.
    /// When <paramref name="isDescending"/> is true (STAR via mode), AtOrAbove constraints
    /// resolve to the depicted altitude even when the aircraft is above it — the pilot
    /// descends TO the constraint altitude. When false (SID via mode), AtOrBelow constraints
    /// resolve to the depicted altitude even when below — the pilot climbs TO the ceiling.
    /// </summary>
    private static double? ResolveAltitudeRestriction(AircraftState aircraft, CifpAltitudeRestriction alt, bool isDescending)
    {
        return alt.Type switch
        {
            CifpAltitudeRestrictionType.At or CifpAltitudeRestrictionType.GlideSlopeIntercept => alt.Altitude1Ft,
            CifpAltitudeRestrictionType.AtOrAbove => (isDescending || aircraft.Altitude < alt.Altitude1Ft) ? alt.Altitude1Ft : null,
            CifpAltitudeRestrictionType.AtOrBelow => (!isDescending || aircraft.Altitude > alt.Altitude1Ft) ? alt.Altitude1Ft : null,
            CifpAltitudeRestrictionType.Between => ResolveBetweenRestriction(aircraft, alt, isDescending),
            _ => null,
        };
    }

    private static double? ResolveBetweenRestriction(AircraftState aircraft, CifpAltitudeRestriction alt, bool isDescending)
    {
        if (alt.Altitude2Ft is not { } lower)
        {
            return null;
        }

        // Descent (STAR via): always target the lower bound — the upper bound is permissiveness
        if (isDescending)
        {
            return aircraft.Altitude > lower ? lower : null;
        }

        // Climb (SID via): always target the upper bound — pilots want to get high fast
        return aircraft.Altitude < alt.Altitude1Ft ? alt.Altitude1Ft : null;
    }

    private static void ClearProcedureState(AircraftState aircraft)
    {
        aircraft.Procedure.ActiveSidId = null;
        aircraft.Procedure.ActiveStarId = null;
        aircraft.Procedure.SidViaMode = false;
        aircraft.Procedure.StarViaMode = false;
        aircraft.Procedure.SidViaCeiling = null;
        aircraft.Procedure.StarViaFloor = null;
        aircraft.Procedure.DepartureRunway = null;
        aircraft.Procedure.DestinationRunway = null;
        aircraft.Procedure.LastProcedureSpeedKts = null;
    }

    /// <summary>Constant for bank angle formula: (π/180) × 1.6878 / 32.174 ≈ 0.0009146.</summary>
    private const double BankAngleCoeff = Math.PI / 180.0 * 1.6878 / 32.174;

    /// <summary>
    /// Ground-speed threshold below which a ground aircraft is considered
    /// stationary and cannot rotate. Nosewheel steering requires forward
    /// motion; differential-brake or tiller turns need some thrust overcoming
    /// rolling friction. Helicopters on the ground must be rolling as well —
    /// hover turns only happen in the air. A tight threshold (0.1 kt) avoids
    /// forbidding rotation in the last sliver of a taxi deceleration while
    /// still catching the fully-stopped case that causes stale
    /// <see cref="ControlTargets.TargetTrueHeading"/> values to drift a parked
    /// aircraft's heading.
    /// </summary>
    private const double StationaryGroundSpeedKts = 0.1;

    private static void UpdateHeading(AircraftState aircraft, AircraftCategory cat, double deltaSeconds)
    {
        var target = aircraft.Targets.TargetTrueHeading;
        if (target is null)
        {
            aircraft.BankAngle = 0;
            return;
        }

        // Aircraft on the ground at zero ground speed cannot physically
        // pivot in place. Reject rotation even if an upstream phase left a
        // stale target heading — the physics contract is the last line of
        // defence against "aircraft pirouettes while parked" artifacts.
        // Airborne helicopters at zero ground speed (hover) are unaffected
        // because IsOnGround is false.
        if (aircraft.IsOnGround && aircraft.GroundSpeed < StationaryGroundSpeedKts)
        {
            aircraft.BankAngle = 0;
            return;
        }

        double current = aircraft.TrueHeading.Degrees;
        double goal = target.Value.Degrees;

        double diff = NormalizeAngle(goal - current);
        if (Math.Abs(diff) < HeadingSnapDeg)
        {
            aircraft.TrueHeading = new TrueHeading(goal);

            // Heading reached — clear turn direction bias but keep the
            // assigned heading so it persists in the UI and autopilot
            // until the controller issues a new instruction.
            aircraft.Targets.PreferredTurnDirection = null;
            aircraft.BankAngle = 0;
            return;
        }

        double turnRate = aircraft.Targets.TurnRateOverride ?? AircraftPerformance.TurnRate(aircraft.AircraftType, cat);
        double maxTurn = turnRate * deltaSeconds;

        double direction = ResolveDirection(diff, aircraft.Targets.PreferredTurnDirection);

        double turnAmount = Math.Min(Math.Abs(diff), maxTurn) * direction;

        aircraft.TrueHeading = new TrueHeading(current + turnAmount);

        // Compute bank angle: atan(TAS_kts × turnRate_deg/s × coeff)
        double tasKts = WindInterpolator.IasToTas(aircraft.IndicatedAirspeed, aircraft.Altitude);
        double bankMag = Math.Atan(tasKts * turnRate * BankAngleCoeff) * (180.0 / Math.PI);
        aircraft.BankAngle = bankMag * direction;
    }

    private static void UpdateAltitude(AircraftState aircraft, AircraftCategory cat, double deltaSeconds)
    {
        if (aircraft.IsOnGround)
        {
            aircraft.VerticalSpeed = 0;
            return;
        }

        double? target = ResolveAltitudeGoal(aircraft);
        if (target is null)
        {
            aircraft.VerticalSpeed = 0;
            return;
        }

        double current = aircraft.Altitude;
        double goal = target.Value;
        double diff = goal - current;

        if (Math.Abs(diff) < AltitudeSnapFt)
        {
            aircraft.Altitude = goal;
            aircraft.VerticalSpeed = 0;
            aircraft.Targets.TargetAltitude = null;
            aircraft.Targets.DesiredVerticalRate = null;
            aircraft.Procedure.IsExpediting = false;
            return;
        }

        bool climbing = diff > 0;

        double rate;
        if (aircraft.Targets.DesiredVerticalRate is { } desired)
        {
            rate = Math.Abs(desired);
        }
        else
        {
            rate = climbing
                ? AircraftPerformance.ClimbRate(aircraft.AircraftType, cat, current)
                : AircraftPerformance.DescentRate(aircraft.AircraftType, cat, current);
        }

        if (aircraft.Procedure.IsExpediting)
        {
            rate *= 1.5;
        }

        double feetPerSec = rate / 60.0;
        double maxChange = feetPerSec * deltaSeconds;
        double change = Math.Min(Math.Abs(diff), maxChange);

        if (climbing)
        {
            aircraft.Altitude += change;
            aircraft.VerticalSpeed = rate;
        }
        else
        {
            aircraft.Altitude -= change;
            aircraft.VerticalSpeed = -rate;
        }
    }

    private static double? ResolveAltitudeGoal(AircraftState aircraft)
    {
        double? goal = aircraft.Targets.TargetAltitude;
        if (aircraft.Targets.AltitudeFloor is { } floor)
        {
            goal = goal is null ? (aircraft.Altitude < floor - AltitudeSnapFt ? floor : null) : Math.Max(goal.Value, floor);
        }

        if (aircraft.Targets.AltitudeCeiling is { } ceiling)
        {
            goal = goal is null ? (aircraft.Altitude > ceiling + AltitudeSnapFt ? ceiling : null) : Math.Min(goal.Value, ceiling);
        }

        return goal;
    }

    private static void UpdateSpeed(AircraftState aircraft, AircraftCategory cat, double deltaSeconds)
    {
        bool below10k = !aircraft.IsOnGround && aircraft.Altitude < 10_000;
        bool speedLimitWaived = AircraftPerformance.IsSpeedLimitWaived(aircraft.AircraftType);

        // Mach hold: recompute equivalent IAS each tick so the aircraft maintains constant Mach.
        if (aircraft.Targets.TargetMach is { } targetMach && !aircraft.IsOnGround)
        {
            double machIas = WindInterpolator.MachToIas(targetMach, aircraft.Altitude);
            if (below10k && !speedLimitWaived)
            {
                machIas = Math.Min(machIas, 250);
            }

            aircraft.Targets.TargetSpeed = machIas;
        }

        // Floor/ceiling enforcement: if IAS violates a floor or ceiling, create a target to correct it.
        if (aircraft.Targets.TargetSpeed is null)
        {
            double effectiveFloor = aircraft.Targets.SpeedFloor ?? 0;
            double effectiveCeiling = aircraft.Targets.SpeedCeiling ?? double.MaxValue;

            // 14 CFR 91.117: cap effective floor at 250 below 10,000 ft.
            if (below10k && !speedLimitWaived)
            {
                effectiveFloor = Math.Min(effectiveFloor, 250);
                effectiveCeiling = Math.Min(effectiveCeiling, 250);
            }

            if (aircraft.Targets.SpeedFloor is not null && aircraft.IndicatedAirspeed < effectiveFloor)
            {
                aircraft.Targets.TargetSpeed = effectiveFloor;
            }
            else if (aircraft.Targets.SpeedCeiling is not null && aircraft.IndicatedAirspeed > effectiveCeiling)
            {
                aircraft.Targets.TargetSpeed = effectiveCeiling;
            }
        }

        // Auto speed schedule: when no explicit speed target exists and aircraft is
        // climbing or descending without an explicit SPD command, set speed based on
        // current altitude band. This gives natural speed transitions through climb/descent.
        // Skip during approach or pattern phases — they manage their own speed.
        if (
            aircraft.Targets.TargetSpeed is null
            && !aircraft.IsOnGround
            && !aircraft.Targets.HasExplicitSpeedCommand
            && aircraft.Phases?.ActiveApproach is null
            && aircraft.Phases?.CurrentPhase?.ManagesSpeed != true
            && aircraft.Targets.TargetAltitude is not null
            && Math.Abs(aircraft.Altitude - aircraft.Targets.TargetAltitude.Value) > AltitudeSnapFt
        )
        {
            double defaultSpeed = AircraftPerformance.DefaultSpeed(aircraft.AircraftType, cat, aircraft.Altitude, aircraft.Targets.TargetAltitude);

            // Honor an active speed ceiling so the auto schedule cannot accelerate
            // past a procedural last-published-speed memory or other capped speed.
            if (aircraft.Targets.SpeedCeiling is { } scheduleCeiling)
            {
                defaultSpeed = Math.Min(defaultSpeed, scheduleCeiling);
            }

            if (Math.Abs(aircraft.IndicatedAirspeed - defaultSpeed) > SpeedSnapKts)
            {
                aircraft.Targets.TargetSpeed = defaultSpeed;
            }
        }

        var target = aircraft.Targets.TargetSpeed;
        if (target is null)
        {
            return;
        }

        // IAS is the primary airspeed; TargetSpeed is an IAS command.
        double current = aircraft.IndicatedAirspeed;
        double goal = target.Value;

        // Clamp goal to ground conflict limit (ground ops only).
        if (aircraft.IsOnGround && aircraft.Ground.SpeedLimit is { } limit)
        {
            goal = Math.Min(goal, limit);
        }

        // 14 CFR 91.117: max 250 KIAS below 10,000 ft MSL when airborne.
        if (below10k && !speedLimitWaived)
        {
            goal = Math.Min(goal, 250);
        }

        // Honor SpeedCeiling continuously, including when a non-procedural source
        // (e.g. auto speed schedule, controller-assigned TargetSpeed before a ceiling
        // was published) has set TargetSpeed above the cap.
        if (aircraft.Targets.SpeedCeiling is { } activeCeiling)
        {
            goal = Math.Min(goal, activeCeiling);
        }

        double diff = goal - current;

        if (Math.Abs(diff) < SpeedSnapKts)
        {
            aircraft.IndicatedAirspeed = goal;
            aircraft.Targets.TargetSpeed = null;
            return;
        }

        bool accelerating = diff > 0;
        double rate;
        if (accelerating)
        {
            rate = AircraftPerformance.AccelRate(aircraft.AircraftType, cat);
        }
        else
        {
            // Phases (LandingPhase, RunwayExitPhase) override the default
            // category decel rate during ground rollout when kinematic
            // firm-braking is required. Null falls back to category default.
            rate = aircraft.Targets.DesiredDecelRate ?? AircraftPerformance.DecelRate(aircraft.AircraftType, cat);
        }

        double maxChange = rate * deltaSeconds;
        double change = Math.Min(Math.Abs(diff), maxChange);

        aircraft.IndicatedAirspeed += accelerating ? change : -change;
    }

    private static void UpdatePosition(AircraftState aircraft, double deltaSeconds, WeatherProfile? weather)
    {
        var pos = aircraft.Position;
        double latRad = pos.Lat * DegToRad;

        if (aircraft.IsOnGround)
        {
            // Enforce ground conflict speed limit before computing displacement.
            if (aircraft.Ground.SpeedLimit is { } limit && aircraft.IndicatedAirspeed > limit)
            {
                aircraft.IndicatedAirspeed = limit;
            }

            double speedNmPerSec = aircraft.IndicatedAirspeed / 3600.0;
            double moveDir = aircraft.Ground.PushbackTrueHeading?.Degrees ?? aircraft.TrueHeading.Degrees;
            double headingRad = moveDir * DegToRad;

            double dLat = speedNmPerSec * deltaSeconds * Math.Cos(headingRad) / NmPerDegLat;
            double dLon = speedNmPerSec * deltaSeconds * Math.Sin(headingRad) / (NmPerDegLat * Math.Cos(latRad));
            aircraft.Position = new LatLon(pos.Lat + dLat, pos.Lon + dLon);

            // On the ground: Track follows Heading directly (GS is derived from IAS).
            aircraft.TrueTrack = aircraft.TrueHeading;
        }
        else
        {
            // Airborne: derive ground speed vector from TAS + wind.
            double tasKts = WindInterpolator.IasToTas(aircraft.IndicatedAirspeed, aircraft.Altitude);
            double headingRad = aircraft.TrueHeading.Degrees * DegToRad;
            var (windNKts, windEKts) = WindInterpolator.GetWindComponents(weather, aircraft.Altitude);

            // Cache wind so AircraftState.GroundSpeed can derive the correct value without weather context.
            aircraft.WindComponents = (windNKts, windEKts);

            // Ground speed vector (knots, N/E components).
            double gsNKts = tasKts * Math.Cos(headingRad) + windNKts;
            double gsEKts = tasKts * Math.Sin(headingRad) + windEKts;

            double trackDeg = Math.Atan2(gsEKts, gsNKts) * (180.0 / Math.PI);
            if (trackDeg < 0)
            {
                trackDeg += 360.0;
            }

            aircraft.TrueTrack = new TrueHeading(trackDeg);

            // Displace using the full ground speed vector.
            double dLat = (gsNKts / 3600.0) * deltaSeconds / NmPerDegLat;
            double dLon = (gsEKts / 3600.0) * deltaSeconds / (NmPerDegLat * Math.Cos(latRad));
            aircraft.Position = new LatLon(pos.Lat + dLat, pos.Lon + dLon);
        }
    }

    private static void UpdateCommandQueue(AircraftState aircraft, double deltaSeconds, Func<string, AircraftState?>? aircraftLookup)
    {
        var queue = aircraft.Queue;
        if (queue.IsComplete)
        {
            return;
        }

        // During active phases, the phase system owns control targets. The full
        // command queue logic (block advancement, untriggered block application) is
        // skipped, but triggered blocks still need to be watched. This lets commands
        // such as "SPD 210 UNTIL 10" fire their ATFN/RNS block during approach
        // phases, while fix and ground triggers can still fire via their event
        // callbacks.
        if (aircraft.Phases?.CurrentPhase is not null)
        {
            int triggerStart = queue.CurrentBlock is { IsApplied: true } ? queue.CurrentBlockIndex + 1 : queue.CurrentBlockIndex;
            ApplyReadyConditionalBlocks(aircraft, queue, triggerStart, aircraftLookup);
            return;
        }

        var block = queue.CurrentBlock;
        if (block is null)
        {
            return;
        }

        // Check completion of commands in the current applied block
        if (block.IsApplied)
        {
            UpdateBlockCompletion(aircraft, block, deltaSeconds);

            if (block.ReadyToAdvance)
            {
                queue.CurrentBlockIndex++;
                block = queue.CurrentBlock;
                if (block is null)
                {
                    return;
                }
            }
            else
            {
                // Lookahead: check following conditional blocks while the current
                // block is still running. This allows "DCT A B C; AT B CM 014",
                // "CM 100; LV 050 FH 270", and "SPD 210; ATFN 10 RNS" to fire
                // on their trigger without waiting for the current lateral,
                // altitude, or speed target to complete.
                ApplyReadyConditionalBlocks(aircraft, queue, queue.CurrentBlockIndex + 1, aircraftLookup);
                return;
            }
        }

        // For unapplied blocks, check if trigger is met
        if (!block.IsApplied)
        {
            if (block.Trigger is not null && !block.TriggerMet)
            {
                block.TriggerMet = IsTriggerMet(aircraft, block.Trigger, aircraftLookup);
                if (!block.TriggerMet)
                {
                    TrackFrdMiss(aircraft, block);
                    return;
                }

                if (block.Trigger.Type is BlockTriggerType.OnHandoff)
                {
                    aircraft.Track.HandoffAccepted = false;
                }
            }

            // Apply the block's commands
            ApplyBlock(aircraft, block);
        }
    }

    /// <summary>
    /// Applies ready conditional blocks in the contiguous triggered run starting at
    /// <paramref name="startIndex"/>. Stops at the first unapplied untriggered block
    /// so ordinary semicolon sequencing is preserved.
    /// </summary>
    private static void ApplyReadyConditionalBlocks(
        AircraftState aircraft,
        CommandQueue queue,
        int startIndex,
        Func<string, AircraftState?>? aircraftLookup
    )
    {
        for (int i = startIndex; i < queue.Blocks.Count; i++)
        {
            var block = queue.Blocks[i];
            if (block.IsApplied)
            {
                continue;
            }

            if (block.Trigger is null)
            {
                break;
            }

            block.TriggerMet = IsTriggerMet(aircraft, block.Trigger, aircraftLookup);
            if (!block.TriggerMet)
            {
                TrackFrdMiss(aircraft, block);
                continue;
            }

            if (block.Trigger.Type is BlockTriggerType.OnHandoff)
            {
                aircraft.Track.HandoffAccepted = false;
            }

            ApplyBlock(aircraft, block);
        }
    }

    private static void UpdateGiveWayResume(AircraftState aircraft, Func<string, AircraftState?>? aircraftLookup)
    {
        if (aircraft.Ground.Hold is not { Kind: HoldKind.GiveWay, YieldTarget: { } yieldTarget } || !aircraft.IsOnGround)
        {
            return;
        }

        if (aircraftLookup is null)
        {
            return;
        }

        var target = aircraftLookup(yieldTarget);
        if (target is null || !target.IsOnGround)
        {
            // Target is gone or airborne — resume
            aircraft.Ground.Hold = null;
            return;
        }

        // Check if give-way condition is met (target has passed)
        var trigger = new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = yieldTarget };

        if (IsGiveWayMet(aircraft, trigger, aircraftLookup))
        {
            aircraft.Ground.Hold = null;
        }
    }

    private static void UpdateBlockCompletion(AircraftState aircraft, CommandBlock block, double deltaSeconds)
    {
        foreach (var cmd in block.Commands)
        {
            if (cmd.IsComplete)
            {
                continue;
            }

            cmd.IsComplete = cmd.Type switch
            {
                TrackedCommandType.Heading => IsHeadingReached(aircraft),
                TrackedCommandType.Altitude => aircraft.Targets.TargetAltitude is null,
                TrackedCommandType.Speed => aircraft.Targets.TargetSpeed is null,
                TrackedCommandType.Navigation => aircraft.Targets.NavigationRoute.Count == 0,
                TrackedCommandType.Immediate => true,
                TrackedCommandType.Wait => CheckWaitComplete(block, aircraft, deltaSeconds),
                _ => true,
            };
        }
    }

    private static bool IsHeadingReached(AircraftState aircraft)
    {
        if (aircraft.Targets.NavigationRoute.Count > 0)
        {
            return false;
        }

        if (aircraft.Targets.TargetTrueHeading is not { } target)
        {
            return true;
        }

        double diff = NormalizeAngle(aircraft.TrueHeading.Degrees - target.Degrees);
        return Math.Abs(diff) < HeadingSnapDeg;
    }

    private static bool CheckWaitComplete(CommandBlock block, AircraftState aircraft, double deltaSeconds)
    {
        if (block.WaitRemainingSeconds > 0)
        {
            block.WaitRemainingSeconds -= deltaSeconds;
            return block.WaitRemainingSeconds <= 0;
        }

        if (block.WaitRemainingDistanceNm > 0)
        {
            double distanceTraveled = (aircraft.GroundSpeed / 3600.0) * deltaSeconds;
            block.WaitRemainingDistanceNm -= distanceTraveled;
            return block.WaitRemainingDistanceNm <= 0;
        }

        return true;
    }

    private static bool IsTriggerMet(AircraftState aircraft, BlockTrigger trigger, Func<string, AircraftState?>? aircraftLookup)
    {
        return trigger.Type switch
        {
            BlockTriggerType.ReachAltitude => trigger.Altitude.HasValue && Math.Abs(aircraft.Altitude - trigger.Altitude.Value) < AltitudeSnapFt,
            BlockTriggerType.ReachFix => trigger.FixLat.HasValue
                && trigger.FixLon.HasValue
                && GeoMath.DistanceNm(aircraft.Position, new LatLon(trigger.FixLat.Value, trigger.FixLon.Value)) < NavArrivalNm,
            BlockTriggerType.InterceptRadial => trigger.FixLat.HasValue
                && trigger.FixLon.HasValue
                && trigger.Radial.HasValue
                && IsRadialIntercepted(aircraft, trigger),
            BlockTriggerType.ReachFrdPoint => trigger.TargetLat.HasValue
                && trigger.TargetLon.HasValue
                && GeoMath.DistanceNm(aircraft.Position, new LatLon(trigger.TargetLat.Value, trigger.TargetLon.Value)) < FrdArrivalNm,
            BlockTriggerType.GiveWay => IsGiveWayMet(aircraft, trigger, aircraftLookup),
            BlockTriggerType.DistanceFinal => IsDistanceFinalMet(aircraft, trigger),
            BlockTriggerType.OnHandoff => aircraft.Track.HandoffAccepted,
            BlockTriggerType.AtGroundEntity => IsGroundEntityReached(aircraft, trigger),
            _ => true,
        };
    }

    private static bool IsGroundEntityReached(AircraftState aircraft, BlockTrigger trigger)
    {
        // Taxiway-only triggers fire from TaxiingPhase via NotifyGroundEntityReached;
        // there's no per-tick "am I on taxiway X" check here. The route cursor is the
        // source of truth and only the phase callback knows when it transitions.
        if (trigger.GroundKind == GroundEntityKind.Taxiway)
        {
            return false;
        }

        if (!trigger.FixLat.HasValue || !trigger.FixLon.HasValue)
        {
            return false;
        }

        return GeoMath.DistanceNm(aircraft.Position, new LatLon(trigger.FixLat.Value, trigger.FixLon.Value)) < GroundArrivalNm;
    }

    internal static bool IsGiveWayMet(AircraftState aircraft, BlockTrigger trigger, Func<string, AircraftState?>? aircraftLookup)
    {
        if (trigger.TargetCallsign is null || aircraftLookup is null)
        {
            return true;
        }

        var target = aircraftLookup(trigger.TargetCallsign);
        if (target is null || !target.IsOnGround)
        {
            // Target is gone or airborne — no conflict
            return true;
        }

        // The held aircraft proceeds only when the target is no longer in the way:
        // either behind us (opposite-direction case) or ahead and moving away
        // (same-direction case). Distance alone is not a release condition — for
        // ground BEHIND, the target is virtually always >0.1 nm away when the
        // command is issued (that's the point of issuing it).
        double headingDiff = Math.Abs(aircraft.TrueHeading.Degrees - target.TrueHeading.Degrees);
        if (headingDiff > 180)
        {
            headingDiff = 360 - headingDiff;
        }

        double bearingToTarget = GeoMath.BearingTo(aircraft.Position, target.Position);
        double diffToTarget = Math.Abs(aircraft.TrueHeading.Degrees - bearingToTarget);
        if (diffToTarget > 180)
        {
            diffToTarget = 360 - diffToTarget;
        }

        // Opposite direction: conflict resolved when no longer head-on
        if (headingDiff > 120)
        {
            return diffToTarget > 90;
        }

        // Same direction: conflict resolved when target is ahead of us
        if (headingDiff < 60)
        {
            return diffToTarget < 90;
        }

        // Neither same nor opposite — no conflict
        return true;
    }

    private static bool IsDistanceFinalMet(AircraftState aircraft, BlockTrigger trigger)
    {
        if (trigger.DistanceFinalNm is not { } distNm)
        {
            return false;
        }

        var runway = aircraft.Phases?.AssignedRunway;
        if (runway is null)
        {
            return false;
        }

        double dist = GeoMath.DistanceNm(aircraft.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude));
        return dist <= distNm;
    }

    /// <summary>
    /// Auto-cancel ATC speed restrictions at 5nm final per 7110.65 §5-7-1.a.2.d.
    /// Only clears speeds set by explicit ATC commands (S180, etc.), not phase-managed
    /// speeds like FAS set by FinalApproachPhase.
    /// Called from Update() after UpdateSpeed().
    /// </summary>
    private static void AutoCancelSpeedAtFinal(AircraftState aircraft)
    {
        if (aircraft.IsOnGround)
        {
            return;
        }

        // Only cancel explicit ATC speed restrictions, not phase-managed approach speeds
        if (!aircraft.Targets.HasExplicitSpeedCommand)
        {
            return;
        }

        var runway = aircraft.Phases?.AssignedRunway;
        if (runway is null)
        {
            return;
        }

        double dist = GeoMath.DistanceNm(aircraft.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude));
        if (dist <= 5.0)
        {
            aircraft.Targets.TargetSpeed = null;
            aircraft.Targets.HasExplicitSpeedCommand = false;
            aircraft.Targets.SpeedFloor = null;
            aircraft.Targets.SpeedCeiling = null;
        }
    }

    private static bool IsRadialIntercepted(AircraftState aircraft, BlockTrigger trigger)
    {
        double bearing = GeoMath.BearingTo(new LatLon(trigger.FixLat!.Value, trigger.FixLon!.Value), aircraft.Position);
        double diff = Math.Abs(NormalizeAngle(bearing - trigger.Radial!.Value));
        return diff < RadialInterceptDeg;
    }

    private static void TrackFrdMiss(AircraftState aircraft, CommandBlock block)
    {
        if (
            block.TriggerMissed
            || block.Trigger?.Type is not BlockTriggerType.ReachFrdPoint
            || !block.Trigger.TargetLat.HasValue
            || !block.Trigger.TargetLon.HasValue
        )
        {
            return;
        }

        double dist = GeoMath.DistanceNm(aircraft.Position, new LatLon(block.Trigger.TargetLat.Value, block.Trigger.TargetLon.Value));

        if (dist < block.TriggerClosestApproach)
        {
            block.TriggerClosestApproach = dist;
        }
        else if (block.TriggerClosestApproach < FrdMissThresholdNm && dist > block.TriggerClosestApproach + FrdMissDepartureNm)
        {
            block.TriggerMissed = true;
            var fixName = block.Trigger.FixName ?? "?";
            var radial = block.Trigger.Radial?.ToString("D3") ?? "???";
            var distNm = block.Trigger.DistanceNm?.ToString("D3") ?? "???";
            aircraft.PendingWarnings.Add($"Missed condition at {fixName} R{radial} D{distNm} " + $"(closest: {block.TriggerClosestApproach:F1} NM)");
        }
    }

    private static void ApplyBlock(AircraftState aircraft, CommandBlock block)
    {
        block.IsApplied = true;
        var result = block.ApplyAction?.Invoke(aircraft);

        if (result is not null && !result.Success)
        {
            Log.LogWarning("Triggered block failed during apply: {Message}", result.Message);

            // CommandDispatcher.DryRunValidate only validates the first
            // immediately-applied block — deferred blocks reach their handler
            // here at trigger fire time. Surface failures so the RPO sees them
            // in the terminal log rather than having them swallowed.
            var src = !string.IsNullOrEmpty(block.SourceCommandText)
                ? block.SourceCommandText
                : (!string.IsNullOrEmpty(block.Description) ? block.Description : block.NaturalDescription);
            var reason = !string.IsNullOrEmpty(result.Message) ? result.Message : "command failed";
            aircraft.PendingWarnings.Add($"{aircraft.Callsign} {src}: {reason}");
        }

        if (result is { Success: true, Message: not null })
        {
            block.NaturalDescription = result.Message;
        }

        foreach (var cmd in block.Commands)
        {
            if (cmd.Type == TrackedCommandType.Immediate)
            {
                cmd.IsComplete = true;
            }
        }

        if (block.Trigger is not null)
        {
            var desc = block.NaturalDescription.Length > 0 ? block.NaturalDescription : block.Description;
            aircraft.PendingNotifications.Add($"[Executing] {desc}");
        }
    }

    /// <summary>
    /// Called when a fix is sequenced from the navigation route or an approach phase.
    /// Scans the command queue for any pending block with a ReachFix trigger matching
    /// the given fix name and fires it immediately. This ensures AT fix triggers work
    /// even when phases are active (where UpdateCommandQueue is normally skipped) and
    /// when turn anticipation sequences the fix at a distance greater than NavArrivalNm.
    /// </summary>
    public static void NotifyFixSequenced(AircraftState aircraft, string fixName)
    {
        var queue = aircraft.Queue;
        if (queue.IsComplete)
        {
            return;
        }

        for (int i = 0; i < queue.Blocks.Count; i++)
        {
            var block = queue.Blocks[i];
            if (block.IsApplied)
            {
                continue;
            }

            if (block.Trigger is not { Type: BlockTriggerType.ReachFix } trigger)
            {
                continue;
            }

            if (!string.Equals(trigger.FixName, fixName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Log.LogDebug("[AtFix] {Callsign}: fix {Fix} sequenced from route, firing block {Idx}", aircraft.Callsign, fixName, i);

            block.TriggerMet = true;
            ApplyBlock(aircraft, block);
        }
    }

    /// <summary>
    /// Called by <see cref="Phases.Ground.TaxiingPhase"/> on segment transitions and node
    /// arrivals. Scans the command queue for any pending AT ground-entity block whose
    /// node id matches <paramref name="arrivedNodeId"/> or whose taxiway name matches
    /// <paramref name="newTaxiwayName"/> and fires it immediately. Mirrors
    /// <see cref="NotifyFixSequenced"/> — required because <see cref="UpdateCommandQueue"/>
    /// is skipped while a phase is active, and ground aircraft are essentially always in
    /// a phase. Idempotent: already-applied blocks are skipped, so double-calls are safe.
    /// </summary>
    public static void NotifyGroundEntityReached(AircraftState aircraft, int? arrivedNodeId, string? newTaxiwayName)
    {
        if (arrivedNodeId is null && string.IsNullOrEmpty(newTaxiwayName))
        {
            return;
        }

        var queue = aircraft.Queue;
        if (queue.IsComplete)
        {
            return;
        }

        for (int i = 0; i < queue.Blocks.Count; i++)
        {
            var block = queue.Blocks[i];
            if (block.IsApplied)
            {
                continue;
            }

            if (block.Trigger is not { Type: BlockTriggerType.AtGroundEntity } trigger)
            {
                continue;
            }

            bool nodeMatch = arrivedNodeId.HasValue && trigger.GroundNodeId == arrivedNodeId.Value;
            bool taxiwayMatch =
                !string.IsNullOrEmpty(newTaxiwayName)
                && !string.IsNullOrEmpty(trigger.GroundTaxiwayName)
                && string.Equals(trigger.GroundTaxiwayName, newTaxiwayName, StringComparison.OrdinalIgnoreCase);

            if (!nodeMatch && !taxiwayMatch)
            {
                continue;
            }

            Log.LogDebug(
                "[AtGround] {Callsign}: ground entity reached (kind={Kind}, node={Node}, taxiway={Taxi}), firing block {Idx}",
                aircraft.Callsign,
                trigger.GroundKind,
                arrivedNodeId,
                newTaxiwayName,
                i
            );

            block.TriggerMet = true;
            ApplyBlock(aircraft, block);
        }
    }

    /// <summary>
    /// Called by <see cref="Phases.PhaseRunner"/> after a phase advances to a new
    /// current phase. If the new phase is a "wait" phase (has at least one
    /// unsatisfied clearance requirement, e.g. HoldingShortPhase waiting on
    /// RunwayCrossing) and the head of the command queue is an untriggered,
    /// unapplied block whose commands the new phase accepts, fire it now. This
    /// lets sequential compounds like <c>TAXI ... ; CTO MRT</c> auto-fire the
    /// second block when the aircraft reaches the hold-short —
    /// <see cref="UpdateCommandQueue"/> early-returns while a phase is active,
    /// and <see cref="ApplyReadyConditionalBlocks"/> stops at the first
    /// untriggered block, so without this hook the queued block would sit
    /// untouched until the next user dispatch.
    ///
    /// We gate on unsatisfied requirements so transient phases (InitialClimb,
    /// pattern legs) don't prematurely fire a queued block intended for after
    /// the phase chain settles into its natural endpoint. Example: in
    /// <c>CTO MR270 014; DCT OAK30NUM</c>, InitialClimbPhase accepts DCT (as
    /// ClearsPhase), but the user means "DCT after the MR270 pattern" — not
    /// "DCT now." InitialClimbPhase has no clearance requirement, so it skips.
    /// HoldingShortPhase has a RunwayCrossing requirement, so TAXI;CTO does fire.
    ///
    /// Idempotent: applied blocks are skipped, so double-calls are safe.
    /// </summary>
    public static void NotifyPhaseAdvanced(AircraftState aircraft)
    {
        var queue = aircraft.Queue;
        if (queue.IsComplete)
        {
            return;
        }

        if (aircraft.Phases?.CurrentPhase is not { } currentPhase)
        {
            return;
        }

        // Only fire on phases that actively wait for input (have unsatisfied
        // clearance requirements). Transient phases that auto-advance must let
        // the queue keep its untriggered head block.
        bool isWaitPhase = false;
        foreach (var req in currentPhase.Requirements)
        {
            if (!req.IsSatisfied)
            {
                isWaitPhase = true;
                break;
            }
        }
        if (!isWaitPhase)
        {
            return;
        }

        // Locate the head unapplied block (matches ApplyReadyConditionalBlocks's
        // start-index logic: skip the current applied block, then look forward).
        int startIndex = queue.CurrentBlock is { IsApplied: true } ? queue.CurrentBlockIndex + 1 : queue.CurrentBlockIndex;
        for (int i = startIndex; i < queue.Blocks.Count; i++)
        {
            var block = queue.Blocks[i];
            if (block.IsApplied)
            {
                continue;
            }

            // Triggered blocks are handled by their own trigger pathway.
            if (block.Trigger is not null)
            {
                return;
            }

            // Untriggered head block: ask the new current phase whether it accepts
            // every command in the block. Any Rejected leaves the block queued.
            if (block.ParsedCommands is not { Count: > 0 } parsed)
            {
                return;
            }

            foreach (var cmd in parsed)
            {
                var canonical = CommandDescriber.ToCanonicalType(cmd);
                var acceptance = currentPhase.CanAcceptCommand(canonical);
                if (acceptance.IsRejected)
                {
                    return;
                }
            }

            Log.LogDebug(
                "[PhaseAdvanced] {Callsign}: new wait phase {Phase} accepts queued block {Idx} ({Desc}); firing",
                aircraft.Callsign,
                currentPhase.GetType().Name,
                i,
                block.Description
            );

            ApplyBlock(aircraft, block);
            return;
        }
    }

    // --- Math helpers ---

    private static double ResolveDirection(double diff, TurnDirection? preferred)
    {
        if (preferred == TurnDirection.Left)
        {
            return -1.0;
        }

        if (preferred == TurnDirection.Right)
        {
            return 1.0;
        }

        return diff > 0 ? 1.0 : -1.0;
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= 360.0;
        if (angle > 180.0)
        {
            angle -= 360.0;
        }

        if (angle < -180.0)
        {
            angle += 360.0;
        }

        return angle;
    }

    /// <summary>Normalize a raw angle to [0,360). For headings, prefer constructing TrueHeading/MagneticHeading directly.</summary>
    private static double NormalizeBearing(double bearing)
    {
        bearing = ((bearing % 360.0) + 360.0) % 360.0;
        return bearing;
    }

    /// <summary>Display-format a raw bearing angle as 001..360. For headings, use TrueHeading/MagneticHeading.ToDisplayInt().</summary>
    internal static int BearingToDisplayInt(double bearing)
    {
        var normalized = ((bearing % 360.0) + 360.0) % 360.0;
        return normalized < 0.5 ? 360 : (int)Math.Round(normalized);
    }
}
