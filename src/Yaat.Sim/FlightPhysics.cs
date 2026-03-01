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
        Update(aircraft, deltaSeconds, aircraftLookup: null);
    }

    public static void Update(
        AircraftState aircraft, double deltaSeconds,
        Func<string, AircraftState?>? aircraftLookup)
    {
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);

        UpdateNavigation(aircraft);
        UpdateHeading(aircraft, cat, deltaSeconds);
        UpdateAltitude(aircraft, cat, deltaSeconds);
        UpdateSpeed(aircraft, cat, deltaSeconds);
        UpdatePosition(aircraft, deltaSeconds);
        UpdateCommandQueue(aircraft, deltaSeconds, aircraftLookup);
    }

    private static void UpdateNavigation(AircraftState aircraft)
    {
        var route = aircraft.Targets.NavigationRoute;
        if (route.Count == 0)
        {
            return;
        }

        var nav = route[0];
        double distNm = DistanceNm(aircraft.Latitude, aircraft.Longitude, nav.Latitude, nav.Longitude);
        if (distNm < NavArrivalNm)
        {
            route.RemoveAt(0);
            if (route.Count == 0)
            {
                aircraft.Targets.TargetHeading = null;
                aircraft.Targets.PreferredTurnDirection = null;
                return;
            }

            nav = route[0];
        }

        double bearing = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, nav.Latitude, nav.Longitude);
        aircraft.Targets.TargetHeading = bearing;
        aircraft.Targets.PreferredTurnDirection = null;
    }

    private static void UpdateHeading(AircraftState aircraft, AircraftCategory cat, double deltaSeconds)
    {
        var target = aircraft.Targets.TargetHeading;
        if (target is null)
        {
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

            // Don't clear heading if nav route is actively steering
            if (aircraft.Targets.NavigationRoute.Count == 0)
            {
                aircraft.Targets.TargetHeading = null;
                aircraft.Targets.PreferredTurnDirection = null;
            }
            return;
        }

        double turnRate = CategoryPerformance.TurnRate(cat);
        double maxTurn = turnRate * deltaSeconds;

        double direction = ResolveDirection(diff, aircraft.Targets.PreferredTurnDirection);

        double turnAmount = Math.Min(Math.Abs(diff), maxTurn) * direction;

        aircraft.Heading = NormalizeHeading(current + turnAmount);
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
        var target = aircraft.Targets.TargetSpeed;
        if (target is null)
        {
            return;
        }

        double current = aircraft.GroundSpeed;
        double goal = target.Value;
        double diff = goal - current;

        if (Math.Abs(diff) < SpeedSnapKts)
        {
            aircraft.GroundSpeed = goal;
            aircraft.Targets.TargetSpeed = null;
            return;
        }

        bool accelerating = diff > 0;
        double rate = accelerating ? CategoryPerformance.AccelRate(cat) : CategoryPerformance.DecelRate(cat);

        double maxChange = rate * deltaSeconds;
        double change = Math.Min(Math.Abs(diff), maxChange);

        aircraft.GroundSpeed += accelerating ? change : -change;
    }

    private static void UpdatePosition(AircraftState aircraft, double deltaSeconds)
    {
        double speedNmPerSec = aircraft.GroundSpeed / 3600.0;
        double headingRad = aircraft.Heading * DegToRad;
        double latRad = aircraft.Latitude * DegToRad;

        aircraft.Latitude += speedNmPerSec * deltaSeconds * Math.Cos(headingRad) / NmPerDegLat;

        aircraft.Longitude += speedNmPerSec * deltaSeconds * Math.Sin(headingRad) / (NmPerDegLat * Math.Cos(latRad));
    }

    private static void UpdateCommandQueue(
        AircraftState aircraft, double deltaSeconds,
        Func<string, AircraftState?>? aircraftLookup = null)
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

    private static void UpdateBlockCompletion(
        AircraftState aircraft, CommandBlock block, double deltaSeconds)
    {
        foreach (var cmd in block.Commands)
        {
            if (cmd.IsComplete)
            {
                continue;
            }

            cmd.IsComplete = cmd.Type switch
            {
                TrackedCommandType.Heading => aircraft.Targets.TargetHeading is null && aircraft.Targets.NavigationRoute.Count == 0,
                TrackedCommandType.Altitude => aircraft.Targets.TargetAltitude is null,
                TrackedCommandType.Speed => aircraft.Targets.TargetSpeed is null,
                TrackedCommandType.Navigation => aircraft.Targets.NavigationRoute.Count == 0,
                TrackedCommandType.Immediate => true,
                TrackedCommandType.Wait => CheckWaitComplete(block, aircraft, deltaSeconds),
                _ => true,
            };
        }
    }

    private static bool CheckWaitComplete(
        CommandBlock block, AircraftState aircraft, double deltaSeconds)
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

    private static bool IsTriggerMet(
        AircraftState aircraft, BlockTrigger trigger,
        Func<string, AircraftState?>? aircraftLookup)
    {
        return trigger.Type switch
        {
            BlockTriggerType.ReachAltitude =>
                trigger.Altitude.HasValue
                && Math.Abs(aircraft.Altitude - trigger.Altitude.Value) < AltitudeSnapFt,
            BlockTriggerType.ReachFix =>
                trigger.FixLat.HasValue
                && trigger.FixLon.HasValue
                && DistanceNm(aircraft.Latitude, aircraft.Longitude,
                    trigger.FixLat.Value, trigger.FixLon.Value) < NavArrivalNm,
            BlockTriggerType.InterceptRadial =>
                trigger.FixLat.HasValue
                && trigger.FixLon.HasValue
                && trigger.Radial.HasValue
                && IsRadialIntercepted(aircraft, trigger),
            BlockTriggerType.ReachFrdPoint =>
                trigger.TargetLat.HasValue
                && trigger.TargetLon.HasValue
                && DistanceNm(aircraft.Latitude, aircraft.Longitude,
                    trigger.TargetLat.Value, trigger.TargetLon.Value) < FrdArrivalNm,
            BlockTriggerType.GiveWay =>
                IsGiveWayMet(aircraft, trigger, aircraftLookup),
            _ => true,
        };
    }

    private static bool IsGiveWayMet(
        AircraftState aircraft, BlockTrigger trigger,
        Func<string, AircraftState?>? aircraftLookup)
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

        double distNm = GeoMath.DistanceNm(
            aircraft.Latitude, aircraft.Longitude,
            target.Latitude, target.Longitude);

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

        double bearingToTarget = GeoMath.BearingTo(
            aircraft.Latitude, aircraft.Longitude,
            target.Latitude, target.Longitude);
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

    private static bool IsRadialIntercepted(
        AircraftState aircraft, BlockTrigger trigger)
    {
        double bearing = GeoMath.BearingTo(
            trigger.FixLat!.Value, trigger.FixLon!.Value,
            aircraft.Latitude, aircraft.Longitude);
        double diff = Math.Abs(NormalizeAngle(bearing - trigger.Radial!.Value));
        return diff < RadialInterceptDeg;
    }

    private static void TrackFrdMiss(AircraftState aircraft, CommandBlock block)
    {
        if (block.TriggerMissed
            || block.Trigger?.Type is not BlockTriggerType.ReachFrdPoint
            || !block.Trigger.TargetLat.HasValue
            || !block.Trigger.TargetLon.HasValue)
        {
            return;
        }

        double dist = DistanceNm(
            aircraft.Latitude, aircraft.Longitude,
            block.Trigger.TargetLat.Value, block.Trigger.TargetLon.Value);

        if (dist < block.TriggerClosestApproach)
        {
            block.TriggerClosestApproach = dist;
        }
        else if (block.TriggerClosestApproach < FrdMissThresholdNm
                 && dist > block.TriggerClosestApproach + FrdMissDepartureNm)
        {
            block.TriggerMissed = true;
            var fixName = block.Trigger.FixName ?? "?";
            var radial = block.Trigger.Radial?.ToString("D3") ?? "???";
            var distNm = block.Trigger.DistanceNm?.ToString("D3") ?? "???";
            aircraft.PendingWarnings.Add(
                $"Missed condition at {fixName} R{radial} D{distNm} " +
                $"(closest: {block.TriggerClosestApproach:F1} NM)");
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
    }

    // --- Geo helpers ---

    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * DegToRad;
        double dLon = (lon2 - lon1) * DegToRad;
        double lat1Rad = lat1 * DegToRad;
        double lat2Rad = lat2 * DegToRad;

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1Rad) * Math.Cos(lat2Rad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return c * 3440.065; // Earth radius in nm
    }

    /// <summary>
    /// Projects a point from a given lat/lon along a heading for a given distance.
    /// Returns (latitude, longitude) of the projected point.
    /// </summary>
    public static (double Lat, double Lon) ProjectPoint(
        double lat, double lon, double headingDeg, double distanceNm)
    {
        double headingRad = headingDeg * DegToRad;
        double latRad = lat * DegToRad;

        double newLat = lat + (distanceNm * Math.Cos(headingRad) / NmPerDegLat);
        double newLon = lon + (distanceNm * Math.Sin(headingRad) / (NmPerDegLat * Math.Cos(latRad)));

        return (newLat, newLon);
    }

    /// <summary>
    /// Signed perpendicular distance from a point to a line defined by
    /// a reference point and heading. Positive = right of heading, negative = left.
    /// </summary>
    public static double SignedCrossTrackDistanceNm(
        double pointLat, double pointLon,
        double refLat, double refLon,
        double headingDeg)
    {
        double bearing = GeoMath.BearingTo(refLat, refLon, pointLat, pointLon);
        double dist = DistanceNm(refLat, refLon, pointLat, pointLon);
        double angleDiff = (bearing - headingDeg) * DegToRad;
        return dist * Math.Sin(angleDiff);
    }

    /// <summary>
    /// Signed distance along a heading from a reference point to a target point.
    /// Positive = ahead (in heading direction), negative = behind.
    /// </summary>
    public static double AlongTrackDistanceNm(
        double pointLat, double pointLon,
        double refLat, double refLon,
        double headingDeg)
    {
        double bearing = GeoMath.BearingTo(refLat, refLon, pointLat, pointLon);
        double dist = DistanceNm(refLat, refLon, pointLat, pointLon);
        double angleDiff = (bearing - headingDeg) * DegToRad;
        return dist * Math.Cos(angleDiff);
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

    private static double NormalizeHeading(double heading)
    {
        heading = ((heading % 360.0) + 360.0) % 360.0;
        return heading;
    }
}
