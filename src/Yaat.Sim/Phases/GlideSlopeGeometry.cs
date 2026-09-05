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
    /// Returns the appropriate glideslope angle for the category. Helicopters fly a steeper 6° path —
    /// the sim's helicopter glidepath, in the band Helicopter TERPS (FAA Order 8260.42B, not in the
    /// local reference set) uses for copter procedures; the publications carried here specify no VFR
    /// rotorcraft approach angle (AIM §10-1-2 covers copter IAP minima and speeds only).
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
    /// Target altitude (MSL) at a given distance from the landing threshold, on a glidepath that crosses
    /// the threshold at <paramref name="crossingHeightFt"/> above it. The path therefore reaches the
    /// surface <c>crossingHeightFt / tan(angle)</c> beyond the threshold — that intercept point is the
    /// approach's aiming point, and it emerges from the geometry rather than being tuned per category.
    ///
    /// Pass the *wheel* crossing height, not a published TCH: the modelled point becomes the wheels at
    /// touchdown, and AIM 1-1-9.d.6 is explicit that a published TCH is the height of the glide slope
    /// *antenna*. <see cref="CategoryPerformance.WheelCrossingHeightFt"/> is the source.
    /// </summary>
    public static double AltitudeAtDistance(double distanceNm, double thresholdElevation, double crossingHeightFt, double angleDeg)
    {
        double angleFt = Math.Tan(angleDeg * DegToRad) * distanceNm * 6076.12;
        return thresholdElevation + crossingHeightFt + angleFt;
    }

    /// <summary>
    /// Target altitude (MSL) at a given distance from the landing threshold, on the glidepath
    /// <paramref name="category"/> flies: its angle (<see cref="AngleForCategory"/>) and its wheel
    /// crossing height (<see cref="CategoryPerformance.WheelCrossingHeightFt"/>).
    /// </summary>
    public static double AltitudeAtDistance(double distanceNm, double thresholdElevation, AircraftCategory category) =>
        AltitudeAtDistance(distanceNm, thresholdElevation, CategoryPerformance.WheelCrossingHeightFt(category), AngleForCategory(category));

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
