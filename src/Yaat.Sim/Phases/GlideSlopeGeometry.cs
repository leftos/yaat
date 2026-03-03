namespace Yaat.Sim.Phases;

/// <summary>
/// Computes glideslope altitude targets from distance to threshold.
/// Standard 3° glideslope: approximately 300 ft per nautical mile.
/// </summary>
public static class GlideSlopeGeometry
{
    public const double StandardAngleDeg = 3.0;
    public const double HelicopterAngleDeg = 6.0;
    private const double DegToRad = Math.PI / 180.0;

    /// <summary>
    /// Returns the appropriate glideslope angle for the category.
    /// Helicopters use a steeper 6° glideslope per AIM §10-1-2.
    /// </summary>
    public static double AngleForCategory(AircraftCategory category)
    {
        return category == AircraftCategory.Helicopter ? HelicopterAngleDeg : StandardAngleDeg;
    }

    /// <summary>
    /// Feet of altitude per nautical mile for the given glideslope angle.
    /// Standard 3°: ~318 ft/nm (rule of thumb: 300 ft/nm).
    /// </summary>
    public static double FeetPerNm(double angleDeg = StandardAngleDeg)
    {
        return Math.Tan(angleDeg * DegToRad) * 6076.12;
    }

    /// <summary>
    /// Target altitude (MSL) at a given distance from the threshold
    /// on the specified glideslope angle.
    /// </summary>
    public static double AltitudeAtDistance(double distanceNm, double thresholdElevation, double angleDeg = StandardAngleDeg)
    {
        double angleFt = Math.Tan(angleDeg * DegToRad) * distanceNm * 6076.12;
        return thresholdElevation + angleFt;
    }

    /// <summary>
    /// Required descent rate (fpm) to maintain glideslope at a given groundspeed.
    /// Rule of thumb: fpm = groundspeed * 5.3.
    /// </summary>
    public static double RequiredDescentRate(double groundSpeedKts, double angleDeg = StandardAngleDeg)
    {
        double angleRad = angleDeg * DegToRad;
        return groundSpeedKts * Math.Tan(angleRad) * 101.269;
    }
}
