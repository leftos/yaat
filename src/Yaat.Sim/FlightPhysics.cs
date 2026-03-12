using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim;

public static class FlightPhysics
{
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

        // Backward compat: airborne aircraft without IAS initialized derive it from GS.
        if (!aircraft.IsOnGround && aircraft.IndicatedAirspeed <= 0 && aircraft.GroundSpeed > 0)
        {
            aircraft.IndicatedAirspeed = aircraft.GroundSpeed;
        }

        UpdateNavigation(aircraft, weather);
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
            return;
        }

        var nav = route[0];
        double distNm = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, nav.Latitude, nav.Longitude);

        // Determine sequencing threshold: fly-by waypoints with a following waypoint
        // use turn anticipation; fly-over and terminal waypoints use NavArrivalNm.
        double threshold = NavArrivalNm;
        double anticipationNm = 0;
        bool inAnticipationZone = false;

        if (route.Count >= 2 && !nav.IsFlyOver)
        {
            double currentLegBearing = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, nav.Latitude, nav.Longitude);
            double nextLegBearing = GeoMath.BearingTo(nav.Latitude, nav.Longitude, route[1].Latitude, route[1].Longitude);
            double turnRate =
                aircraft.Targets.TurnRateOverride ?? CategoryPerformance.TurnRate(AircraftCategorization.Categorize(aircraft.AircraftType));
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
            double alongTrack = GeoMath.AlongTrackDistanceNm(aircraft.Latitude, aircraft.Longitude, nav.Latitude, nav.Longitude, nextLegBearing);
            shouldSequence = alongTrack >= 0;
        }
        else
        {
            shouldSequence = distNm < NavArrivalNm;
        }

        if (shouldSequence)
        {
            route.RemoveAt(0);
            if (route.Count == 0)
            {
                aircraft.Targets.TargetHeading = null;
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
                aircraft.Targets.TurnRateOverride ?? CategoryPerformance.TurnRate(AircraftCategorization.Categorize(aircraft.AircraftType));
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

        aircraft.Targets.TargetHeading = NormalizeHeading(bearing + wca);
        aircraft.Targets.PreferredTurnDirection = null;
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
        double bisector = NormalizeHeading(currentLegBearing + courseChange / 2.0);
        double perpBearing = turnRight ? bisector + 90.0 : bisector - 90.0;
        perpBearing = NormalizeHeading(perpBearing);

        // Offset distance from waypoint to turn center
        double halfAngleRad = Math.Abs(courseChange) * Math.PI / 360.0;
        double cosHalf = Math.Cos(halfAngleRad);
        double offsetNm = cosHalf > 0.01 ? radiusNm / cosHalf : radiusNm;

        var (centerLat, centerLon) = GeoMath.ProjectPoint(waypointLat, waypointLon, perpBearing, offsetNm);

        // Aircraft's bearing from turn center
        double radialFromCenter = GeoMath.BearingTo(centerLat, centerLon, aircraftLat, aircraftLon);

        // Tangent heading = perpendicular to radial, in turn direction
        double tangent = turnRight ? radialFromCenter + 90.0 : radialFromCenter - 90.0;
        return NormalizeHeading(tangent);
    }

    /// <summary>
    /// Applies altitude and speed constraints from a navigation target when via mode is active.
    /// SID via mode enforces climb restrictions; STAR via mode enforces descent restrictions.
    /// </summary>
    internal static void ApplyFixConstraints(AircraftState aircraft, NavigationTarget target)
    {
        bool sidVia = aircraft.SidViaMode;
        bool starVia = aircraft.StarViaMode;
        if (!sidVia && !starVia)
        {
            return;
        }

        if (target.AltitudeRestriction is { } alt)
        {
            double? resolvedAlt = ResolveAltitudeRestriction(aircraft, alt);
            if (resolvedAlt is { } targetAlt)
            {
                // Apply ceiling/floor limits
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
    /// </summary>
    private static double? ResolveAltitudeRestriction(AircraftState aircraft, CifpAltitudeRestriction alt)
    {
        return alt.Type switch
        {
            CifpAltitudeRestrictionType.At or CifpAltitudeRestrictionType.GlideSlopeIntercept => alt.Altitude1Ft,
            CifpAltitudeRestrictionType.AtOrAbove => aircraft.Altitude < alt.Altitude1Ft ? alt.Altitude1Ft : null,
            CifpAltitudeRestrictionType.AtOrBelow => aircraft.Altitude > alt.Altitude1Ft ? alt.Altitude1Ft : null,
            CifpAltitudeRestrictionType.Between => ResolveBetweenRestriction(aircraft, alt),
            _ => null,
        };
    }

    private static double? ResolveBetweenRestriction(AircraftState aircraft, CifpAltitudeRestriction alt)
    {
        if (alt.Altitude2Ft is not { } lower)
        {
            return null;
        }

        if (aircraft.Altitude > alt.Altitude1Ft)
        {
            return alt.Altitude1Ft;
        }

        if (aircraft.Altitude < lower)
        {
            return lower;
        }

        return null;
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
        var target = aircraft.Targets.TargetHeading;
        if (target is null)
        {
            aircraft.BankAngle = 0;
            return;
        }

        double current = aircraft.Heading;
        double goal = target.Value;

        double diff = NormalizeAngle(goal - current);
        if (Math.Abs(diff) < HeadingSnapDeg)
        {
            aircraft.Heading = goal % 360.0;
            if (aircraft.Heading < 0)
            {
                aircraft.Heading += 360.0;
            }

            // Heading reached — clear turn direction bias but keep the
            // assigned heading so it persists in the UI and autopilot
            // until the controller issues a new instruction.
            aircraft.Targets.PreferredTurnDirection = null;
            aircraft.BankAngle = 0;
            return;
        }

        double turnRate = aircraft.Targets.TurnRateOverride ?? CategoryPerformance.TurnRate(cat);
        double maxTurn = turnRate * deltaSeconds;

        double direction = ResolveDirection(diff, aircraft.Targets.PreferredTurnDirection);

        double turnAmount = Math.Min(Math.Abs(diff), maxTurn) * direction;

        aircraft.Heading = NormalizeHeading(current + turnAmount);

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
            rate = climbing ? CategoryPerformance.ClimbRate(cat, current) : CategoryPerformance.DescentRate(cat);
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

        // Mach hold: recompute equivalent IAS each tick so the aircraft maintains constant Mach.
        if (aircraft.Targets.TargetMach is { } targetMach && !aircraft.IsOnGround)
        {
            double machIas = WindInterpolator.MachToIas(targetMach, aircraft.Altitude);
            if (below10k)
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
            if (below10k)
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
        if (
            aircraft.Targets.TargetSpeed is null
            && !aircraft.IsOnGround
            && !aircraft.Targets.HasExplicitSpeedCommand
            && aircraft.Targets.TargetAltitude is not null
            && Math.Abs(aircraft.Altitude - aircraft.Targets.TargetAltitude.Value) > AltitudeSnapFt
        )
        {
            double defaultSpeed = CategoryPerformance.DefaultSpeed(cat, aircraft.Altitude, aircraft.AircraftType);
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
        if (below10k)
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
        double rate = accelerating ? CategoryPerformance.AccelRate(cat) : CategoryPerformance.DecelRate(cat);

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
            double moveHeading = aircraft.PushbackHeading ?? aircraft.Heading;
            double headingRad = moveHeading * DegToRad;

            aircraft.Latitude += speedNmPerSec * deltaSeconds * Math.Cos(headingRad) / NmPerDegLat;
            aircraft.Longitude += speedNmPerSec * deltaSeconds * Math.Sin(headingRad) / (NmPerDegLat * Math.Cos(latRad));

            // On the ground: Track follows Heading directly (GS is derived from IAS).
            aircraft.Track = aircraft.Heading;
        }
        else
        {
            // Airborne: derive ground speed vector from TAS + wind.
            double tasKts = WindInterpolator.IasToTas(aircraft.IndicatedAirspeed, aircraft.Altitude);
            double headingRad = aircraft.Heading * DegToRad;
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

            aircraft.Track = trackDeg;

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
            }

            // Apply the block's commands
            ApplyBlock(aircraft, block);
        }
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

        if (aircraft.Targets.TargetHeading is not { } target)
        {
            return true;
        }

        double diff = NormalizeAngle(aircraft.Heading - target);
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
        double headingDiff = Math.Abs(aircraft.Heading - target.Heading);
        if (headingDiff > 180)
        {
            headingDiff = 360 - headingDiff;
        }

        double bearingToTarget = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, target.Latitude, target.Longitude);
        double diffToTarget = Math.Abs(aircraft.Heading - bearingToTarget);
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
    /// Called from Update() after UpdateSpeed().
    /// </summary>
    private static void AutoCancelSpeedAtFinal(AircraftState aircraft)
    {
        if (aircraft.IsOnGround)
        {
            return;
        }

        var runway = aircraft.Phases?.AssignedRunway;
        if (runway is null)
        {
            return;
        }

        // Only clear if there's something to clear
        if (aircraft.Targets.TargetSpeed is null && aircraft.Targets.SpeedFloor is null && aircraft.Targets.SpeedCeiling is null)
        {
            return;
        }

        double dist = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, runway.ThresholdLatitude, runway.ThresholdLongitude);
        if (dist <= 5.0)
        {
            aircraft.Targets.TargetSpeed = null;
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
        block.ApplyAction?.Invoke(aircraft);

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

    internal static double NormalizeAngle(double angle)
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

    internal static double NormalizeHeading(double heading)
    {
        heading = ((heading % 360.0) + 360.0) % 360.0;
        return heading;
    }

    internal static int NormalizeHeadingInt(double heading)
    {
        var normalized = ((heading % 360.0) + 360.0) % 360.0;
        return normalized < 0.5 ? 360 : (int)Math.Round(normalized);
    }
}
