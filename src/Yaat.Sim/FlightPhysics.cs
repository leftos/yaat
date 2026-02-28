namespace Yaat.Sim;

public static class FlightPhysics
{
    private const double HeadingSnapDeg = 0.5;
    private const double AltitudeSnapFt = 10.0;
    private const double SpeedSnapKts = 2.0;

    public static void Update(AircraftState aircraft, double deltaSeconds)
    {
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);

        UpdateHeading(aircraft, cat, deltaSeconds);
        UpdateAltitude(aircraft, cat, deltaSeconds);
        UpdateSpeed(aircraft, cat, deltaSeconds);
        UpdatePosition(aircraft, deltaSeconds);
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

            aircraft.Targets.TargetHeading = null;
            aircraft.Targets.PreferredTurnDirection = null;
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
        double headingRad = aircraft.Heading * Math.PI / 180.0;
        double latRad = aircraft.Latitude * Math.PI / 180.0;

        aircraft.Latitude += speedNmPerSec * deltaSeconds * Math.Cos(headingRad) / 60.0;

        aircraft.Longitude += speedNmPerSec * deltaSeconds * Math.Sin(headingRad) / (60.0 * Math.Cos(latRad));
    }

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
