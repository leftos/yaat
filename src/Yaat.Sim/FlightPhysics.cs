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
    private const double RadialInterceptDeg = 3.0;
    private const double FrdMissThresholdNm = 5.0;
    private const double FrdMissDepartureNm = 0.5;
    private const double DegToRad = Math.PI / 180.0;
    private const double NmPerDegLat = 60.0;

    public static void Update(AircraftState aircraft, double deltaSeconds)
    {
        Update(aircraft, deltaSeconds, aircraftLookup: null, weather: null);
    }

    public static void Update(AircraftState aircraft, double deltaSeconds, Func<string, AircraftState?>? aircraftLookup)
    {
        Update(aircraft, deltaSeconds, aircraftLookup, weather: null);
    }

    public static void Update(AircraftState aircraft, double deltaSeconds, Func<string, AircraftState?>? aircraftLookup, WeatherProfile? weather)
    {
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);

        // Cache magnetic declination at current position for this tick.
        aircraft.Declination = MagneticDeclination.GetDeclination(aircraft.Latitude, aircraft.Longitude);

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
    }

    private static void UpdateNavigation(AircraftState aircraft, WeatherProfile? weather)
    {
        var route = aircraft.Targets.NavigationRoute;
        if (route.Count == 0)
        {
            if (aircraft.PendingApproachClearance is { } pendingEarly)
            {
                ApproachCommandHandler.ActivatePendingApproach(aircraft, pendingEarly);
            }

            return;
        }

        var nav = route[0];
        double distNm = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, nav.Latitude, nav.Longitude);

        // Determine sequencing threshold: fly-by waypoints with a following waypoint
        // use turn anticipation; fly-over and terminal waypoints use NavArrivalNm.
        double threshold = NavArrivalNm;
        double anticipationNm = 0;
        bool inAnticipationZone;

        if (route.Count >= 2 && !nav.IsFlyOver)
        {
            double currentLegBearing = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, nav.Latitude, nav.Longitude);
            double nextLegBearing = GeoMath.BearingTo(nav.Latitude, nav.Longitude, route[1].Latitude, route[1].Longitude);
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
            double nextLegBearing = GeoMath.BearingTo(nav.Latitude, nav.Longitude, route[1].Latitude, route[1].Longitude);
            double alongTrack = GeoMath.AlongTrackDistanceNmRaw(aircraft.Latitude, aircraft.Longitude, nav.Latitude, nav.Longitude, nextLegBearing);
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
                if (aircraft.PendingApproachClearance is { } pending)
                {
                    ApproachCommandHandler.ActivatePendingApproach(aircraft, pending);
                    return;
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
            double currentLegBearing = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, nav.Latitude, nav.Longitude);
            double nextLegBearing = GeoMath.BearingTo(nav.Latitude, nav.Longitude, route[1].Latitude, route[1].Longitude);
            double turnRate =
                aircraft.Targets.TurnRateOverride
                ?? AircraftPerformance.TurnRate(aircraft.AircraftType, AircraftCategorization.Categorize(aircraft.AircraftType));
            bearing = ComputeArcBlendedHeading(
                aircraft.Latitude,
                aircraft.Longitude,
                aircraft.GroundSpeed,
                turnRate,
                nav.Latitude,
                nav.Longitude,
                currentLegBearing,
                nextLegBearing
            );
        }
        else
        {
            bearing = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, nav.Latitude, nav.Longitude);
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
        if (aircraft.SidViaMode)
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

        if (!aircraft.StarViaMode && !hasRouteConstraints)
        {
            return;
        }

        // Find the NEXT constrained fix (step descent: one constraint at a time)
        double cumulativeDistNm = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, route[0].Latitude, route[0].Longitude);

        for (int i = 0; i < route.Count; i++)
        {
            if (i > 0)
            {
                cumulativeDistNm += GeoMath.DistanceNm(route[i - 1].Latitude, route[i - 1].Longitude, route[i].Latitude, route[i].Longitude);
            }

            if (route[i].AltitudeRestriction is not { } restriction)
            {
                continue;
            }

            // When not in via mode, infer direction from current altitude vs constraint
            bool isDescending = aircraft.StarViaMode || aircraft.Altitude > restriction.Altitude1Ft;
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
            if (aircraft.StarViaMode && aircraft.StarViaFloor is { } floor)
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
        if (aircraft.StarViaMode)
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

        if (!aircraft.SidViaMode && !hasRouteConstraints)
        {
            return;
        }

        double cumulativeDistNm = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, route[0].Latitude, route[0].Longitude);

        for (int i = 0; i < route.Count; i++)
        {
            if (i > 0)
            {
                cumulativeDistNm += GeoMath.DistanceNm(route[i - 1].Latitude, route[i - 1].Longitude, route[i].Latitude, route[i].Longitude);
            }

            if (route[i].AltitudeRestriction is not { } restriction)
            {
                continue;
            }

            // When not in via mode, infer direction from current altitude vs constraint
            bool isDescending = !aircraft.SidViaMode && aircraft.Altitude > restriction.Altitude1Ft;
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
            if (aircraft.SidViaMode && aircraft.SidViaCeiling is { } ceiling)
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
        if (aircraft.Targets.HasExplicitSpeedCommand || aircraft.SpeedRestrictionsDeleted || (aircraft.Targets.TargetMach is not null))
        {
            return;
        }

        var route = aircraft.Targets.NavigationRoute;
        if (route.Count == 0)
        {
            return;
        }

        bool speedLimitWaived = AircraftPerformance.IsSpeedLimitWaived(aircraft.AircraftType);
        double cumulativeDistNm = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, route[0].Latitude, route[0].Longitude);

        for (int i = 0; i < route.Count; i++)
        {
            if (i > 0)
            {
                cumulativeDistNm += GeoMath.DistanceNm(route[i - 1].Latitude, route[i - 1].Longitude, route[i].Latitude, route[i].Longitude);
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
                break; // Already at constraint speed
            }

            bool needsDecel = aircraft.IndicatedAirspeed > constraintSpeed;
            double rate = needsDecel
                ? AircraftPerformance.DecelRate(aircraft.AircraftType, cat)
                : AircraftPerformance.AccelRate(aircraft.AircraftType, cat);

            double changeTimeSeconds = speedDelta / rate;
            double timeToFixSeconds = cumulativeDistNm / (aircraft.GroundSpeed / 3600.0);

            // Start speed change with 10% margin to ensure constraint is met at the fix
            if (timeToFixSeconds <= changeTimeSeconds * 1.1)
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
        double aircraftLat,
        double aircraftLon,
        double groundSpeedKts,
        double turnRateDegPerSec,
        double waypointLat,
        double waypointLon,
        double currentLegBearing,
        double nextLegBearing
    )
    {
        double courseChange = NormalizeAngle(nextLegBearing - currentLegBearing);
        if (Math.Abs(courseChange) < 1.0)
        {
            return GeoMath.BearingTo(aircraftLat, aircraftLon, waypointLat, waypointLon);
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

        var (centerLat, centerLon) = GeoMath.ProjectPointRaw(waypointLat, waypointLon, perpBearing, offsetNm);

        // Aircraft's bearing from turn center
        double radialFromCenter = GeoMath.BearingTo(centerLat, centerLon, aircraftLat, aircraftLon);

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
        bool sidVia = aircraft.SidViaMode;
        bool starVia = aircraft.StarViaMode;
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
                if (sidVia && aircraft.SidViaCeiling is { } ceiling)
                {
                    targetAlt = Math.Min(targetAlt, ceiling);
                }

                if (starVia && aircraft.StarViaFloor is { } floor)
                {
                    targetAlt = Math.Max(targetAlt, floor);
                }

                aircraft.Targets.TargetAltitude = targetAlt;
            }
        }

        if (target.SpeedRestriction is { } spd && !aircraft.SpeedRestrictionsDeleted)
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
        aircraft.ActiveSidId = null;
        aircraft.ActiveStarId = null;
        aircraft.SidViaMode = false;
        aircraft.StarViaMode = false;
        aircraft.SidViaCeiling = null;
        aircraft.StarViaFloor = null;
        aircraft.DepartureRunway = null;
        aircraft.DestinationRunway = null;
    }

    /// <summary>Constant for bank angle formula: (π/180) × 1.6878 / 32.174 ≈ 0.0009146.</summary>
    private const double BankAngleCoeff = Math.PI / 180.0 * 1.6878 / 32.174;

    private static void UpdateHeading(AircraftState aircraft, AircraftCategory cat, double deltaSeconds)
    {
        var target = aircraft.Targets.TargetTrueHeading;
        if (target is null)
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

        var target = aircraft.Targets.TargetAltitude;
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
            aircraft.IsExpediting = false;
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

        if (aircraft.IsExpediting)
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
        // Skip during approach phases — approach/intercept phases manage their own speed.
        if (
            aircraft.Targets.TargetSpeed is null
            && !aircraft.IsOnGround
            && !aircraft.Targets.HasExplicitSpeedCommand
            && aircraft.Phases?.ActiveApproach is null
            && aircraft.Targets.TargetAltitude is not null
            && Math.Abs(aircraft.Altitude - aircraft.Targets.TargetAltitude.Value) > AltitudeSnapFt
        )
        {
            double defaultSpeed = AircraftPerformance.DefaultSpeed(aircraft.AircraftType, cat, aircraft.Altitude, aircraft.Targets.TargetAltitude);
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
        if (aircraft.IsOnGround && aircraft.GroundSpeedLimit is { } limit)
        {
            goal = Math.Min(goal, limit);
        }

        // 14 CFR 91.117: max 250 KIAS below 10,000 ft MSL when airborne.
        if (below10k && !speedLimitWaived)
        {
            goal = Math.Min(goal, 250);
        }

        double diff = goal - current;

        if (Math.Abs(diff) < SpeedSnapKts)
        {
            aircraft.IndicatedAirspeed = goal;
            aircraft.Targets.TargetSpeed = null;
            return;
        }

        bool accelerating = diff > 0;
        double rate = accelerating
            ? AircraftPerformance.AccelRate(aircraft.AircraftType, cat)
            : AircraftPerformance.DecelRate(aircraft.AircraftType, cat);

        double maxChange = rate * deltaSeconds;
        double change = Math.Min(Math.Abs(diff), maxChange);

        aircraft.IndicatedAirspeed += accelerating ? change : -change;
    }

    private static void UpdatePosition(AircraftState aircraft, double deltaSeconds, WeatherProfile? weather)
    {
        double latRad = aircraft.Latitude * DegToRad;

        if (aircraft.IsOnGround)
        {
            // Enforce ground conflict speed limit before computing displacement.
            if (aircraft.GroundSpeedLimit is { } limit && aircraft.IndicatedAirspeed > limit)
            {
                aircraft.IndicatedAirspeed = limit;
            }

            double speedNmPerSec = aircraft.IndicatedAirspeed / 3600.0;
            double moveDir = aircraft.PushbackTrueHeading?.Degrees ?? aircraft.TrueHeading.Degrees;
            double headingRad = moveDir * DegToRad;

            aircraft.Latitude += speedNmPerSec * deltaSeconds * Math.Cos(headingRad) / NmPerDegLat;
            aircraft.Longitude += speedNmPerSec * deltaSeconds * Math.Sin(headingRad) / (NmPerDegLat * Math.Cos(latRad));

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
            aircraft.Latitude += (gsNKts / 3600.0) * deltaSeconds / NmPerDegLat;
            aircraft.Longitude += (gsEKts / 3600.0) * deltaSeconds / (NmPerDegLat * Math.Cos(latRad));
        }
    }

    private static void UpdateCommandQueue(AircraftState aircraft, double deltaSeconds, Func<string, AircraftState?>? aircraftLookup)
    {
        if (aircraft.Phases?.CurrentPhase is not null)
        {
            return;
        }

        var queue = aircraft.Queue;
        if (queue.IsComplete)
        {
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

            if (block.AllComplete)
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
                // Lookahead: check next block's AT-fix trigger while current block is running.
                // This allows "DCT A B C; AT B CM 014" to fire the altitude command at B
                // without waiting for the DCT to reach C first.
                LookaheadAtFixTrigger(aircraft, queue, aircraftLookup);
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
                    aircraft.HandoffAccepted = false;
                }
            }

            // Apply the block's commands
            ApplyBlock(aircraft, block);
        }
    }

    /// <summary>
    /// Peeks at the next block in the queue. If it has an AT fix-name trigger and the
    /// aircraft is currently at that fix, apply the block immediately alongside the
    /// still-running current block. This lets "DCT A B C; AT B CM 014" fire the
    /// altitude command when the aircraft crosses B, without waiting for the DCT to
    /// reach C first.
    /// </summary>
    private static void LookaheadAtFixTrigger(AircraftState aircraft, CommandQueue queue, Func<string, AircraftState?>? aircraftLookup)
    {
        int nextIdx = queue.CurrentBlockIndex + 1;
        if (nextIdx >= queue.Blocks.Count)
        {
            return;
        }

        var nextBlock = queue.Blocks[nextIdx];
        if (nextBlock.IsApplied || nextBlock.Trigger is null || nextBlock.Trigger.Type is not BlockTriggerType.ReachFix)
        {
            return;
        }

        if (!IsTriggerMet(aircraft, nextBlock.Trigger, aircraftLookup))
        {
            return;
        }

        nextBlock.TriggerMet = true;
        ApplyBlock(aircraft, nextBlock);
    }

    private static void UpdateGiveWayResume(AircraftState aircraft, Func<string, AircraftState?>? aircraftLookup)
    {
        if (aircraft.GiveWayTarget is null || !aircraft.IsHeld || !aircraft.IsOnGround)
        {
            return;
        }

        if (aircraftLookup is null)
        {
            return;
        }

        var target = aircraftLookup(aircraft.GiveWayTarget);
        if (target is null || !target.IsOnGround)
        {
            // Target is gone or airborne — resume
            aircraft.IsHeld = false;
            aircraft.GiveWayTarget = null;
            return;
        }

        // Check if give-way condition is met (target has passed)
        var trigger = new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = aircraft.GiveWayTarget };

        if (IsGiveWayMet(aircraft, trigger, aircraftLookup))
        {
            aircraft.IsHeld = false;
            aircraft.GiveWayTarget = null;
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
                && GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, trigger.FixLat.Value, trigger.FixLon.Value) < NavArrivalNm,
            BlockTriggerType.InterceptRadial => trigger.FixLat.HasValue
                && trigger.FixLon.HasValue
                && trigger.Radial.HasValue
                && IsRadialIntercepted(aircraft, trigger),
            BlockTriggerType.ReachFrdPoint => trigger.TargetLat.HasValue
                && trigger.TargetLon.HasValue
                && GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, trigger.TargetLat.Value, trigger.TargetLon.Value) < FrdArrivalNm,
            BlockTriggerType.GiveWay => IsGiveWayMet(aircraft, trigger, aircraftLookup),
            BlockTriggerType.DistanceFinal => IsDistanceFinalMet(aircraft, trigger),
            BlockTriggerType.OnHandoff => aircraft.HandoffAccepted,
            _ => true,
        };
    }

    private static bool IsGiveWayMet(AircraftState aircraft, BlockTrigger trigger, Func<string, AircraftState?>? aircraftLookup)
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

        double distNm = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, target.Latitude, target.Longitude);

        // If the target is far enough away, the conflict is resolved
        if (distNm > 0.1)
        {
            return true;
        }

        // Check if they're still conflicting based on heading
        double headingDiff = Math.Abs(aircraft.TrueHeading.Degrees - target.TrueHeading.Degrees);
        if (headingDiff > 180)
        {
            headingDiff = 360 - headingDiff;
        }

        double bearingToTarget = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, target.Latitude, target.Longitude);
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

        double dist = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, runway.ThresholdLatitude, runway.ThresholdLongitude);
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

        double dist = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, runway.ThresholdLatitude, runway.ThresholdLongitude);
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
        double bearing = GeoMath.BearingTo(trigger.FixLat!.Value, trigger.FixLon!.Value, aircraft.Latitude, aircraft.Longitude);
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

        double dist = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, block.Trigger.TargetLat.Value, block.Trigger.TargetLon.Value);

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
