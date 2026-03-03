namespace Yaat.Sim;

/// <summary>Wind at a specific altitude after interpolation.</summary>
public readonly record struct WindAtAltitude(double DirectionDeg, double SpeedKts);

public static class WindInterpolator
{
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    // Standard atmosphere TAS/CAS factors at altitude checkpoints.
    // Accurate within 1-2% of ISA. At FL350 IAS 280 → TAS ~503 kts.
    private static readonly (double Altitude, double Factor)[] TasFactors =
    [
        (0, 1.000),
        (5_000, 1.077),
        (10_000, 1.165),
        (15_000, 1.261),
        (20_000, 1.370),
        (25_000, 1.494),
        (30_000, 1.634),
        (35_000, 1.796),
        (40_000, 2.014),
    ];

    /// <summary>
    /// Returns interpolated wind at the given altitude.
    /// Null profile or empty layers returns zero wind.
    /// Altitudes outside the layer range are clamped to the nearest layer.
    /// Interpolation uses N/E vector components to handle the 0/360 boundary correctly.
    /// </summary>
    public static WindAtAltitude GetWindAt(WeatherProfile? profile, double altitudeFt)
    {
        if (profile is null || profile.WindLayers.Count == 0)
        {
            return new WindAtAltitude(0, 0);
        }

        var layers = profile.WindLayers;

        if (altitudeFt <= layers[0].Altitude)
        {
            return new WindAtAltitude(layers[0].Direction, layers[0].Speed);
        }

        if (altitudeFt >= layers[^1].Altitude)
        {
            return new WindAtAltitude(layers[^1].Direction, layers[^1].Speed);
        }

        int upper = 1;
        while (upper < layers.Count && layers[upper].Altitude < altitudeFt)
        {
            upper++;
        }

        var low = layers[upper - 1];
        var high = layers[upper];
        double t = (altitudeFt - low.Altitude) / (high.Altitude - low.Altitude);

        // Decompose wind FROM direction into unit N/E components, then lerp.
        // This correctly handles the 0/360 wraparound (e.g., 350° and 010° → 000°).
        double lowRad = low.Direction * DegToRad;
        double highRad = high.Direction * DegToRad;

        double interpN = Math.Cos(lowRad) + t * (Math.Cos(highRad) - Math.Cos(lowRad));
        double interpE = Math.Sin(lowRad) + t * (Math.Sin(highRad) - Math.Sin(lowRad));
        double interpSpeed = low.Speed + t * (high.Speed - low.Speed);

        double direction = Math.Atan2(interpE, interpN) * RadToDeg;
        if (direction < 0)
        {
            direction += 360.0;
        }

        return new WindAtAltitude(direction, interpSpeed);
    }

    /// <summary>
    /// Returns the wind effect vector in knots: the direction the wind blows TOWARD
    /// (reversed from the "wind FROM" convention) as (northKts, eastKts) components.
    /// Example: wind FROM 270 at 10 kts → (northKts: 0, eastKts: +10).
    /// </summary>
    public static (double NorthKts, double EastKts) GetWindComponents(WeatherProfile? profile, double altitudeFt)
    {
        var wind = GetWindAt(profile, altitudeFt);
        if (wind.SpeedKts <= 0)
        {
            return (0, 0);
        }

        // Wind FROM direction + 180° gives the direction wind blows toward.
        double toRad = ((wind.DirectionDeg + 180.0) % 360.0) * DegToRad;
        double northKts = wind.SpeedKts * Math.Cos(toRad);
        double eastKts = wind.SpeedKts * Math.Sin(toRad);
        return (northKts, eastKts);
    }

    /// <summary>
    /// Converts indicated airspeed (IAS/CAS) to true airspeed (TAS) using a standard
    /// atmosphere lookup table with linear interpolation. Accurate within 1-2% of ISA.
    /// </summary>
    public static double IasToTas(double ias, double altitudeFt)
    {
        if (altitudeFt <= TasFactors[0].Altitude)
        {
            return ias * TasFactors[0].Factor;
        }

        if (altitudeFt >= TasFactors[^1].Altitude)
        {
            return ias * TasFactors[^1].Factor;
        }

        int upper = 1;
        while (upper < TasFactors.Length && TasFactors[upper].Altitude < altitudeFt)
        {
            upper++;
        }

        var (lowAlt, lowFactor) = TasFactors[upper - 1];
        var (highAlt, highFactor) = TasFactors[upper];
        double t = (altitudeFt - lowAlt) / (highAlt - lowAlt);

        return ias * (lowFactor + t * (highFactor - lowFactor));
    }

    /// <summary>
    /// Computes the Wind Correction Angle (WCA) in degrees.
    /// Positive WCA means correction is to the right of the desired track.
    /// Apply as: heading = desiredTrack + WCA.
    /// Returns 0 if TAS or wind speed is zero.
    /// </summary>
    public static double ComputeWindCorrectionAngle(double desiredTrackDeg, double tasKts, double windFromDeg, double windSpeedKts)
    {
        if (tasKts <= 0 || windSpeedKts <= 0)
        {
            return 0;
        }

        // Wind blows toward: FROM + 180°
        double windToRad = ((windFromDeg + 180.0) % 360.0) * DegToRad;
        double windN = windSpeedKts * Math.Cos(windToRad);
        double windE = windSpeedKts * Math.Sin(windToRad);

        double trackRad = desiredTrackDeg * DegToRad;

        // Cross-track wind component (positive = wind pushes left of track → crab right).
        // Derived from: sin(WCA) = (windN·sin(track) - windE·cos(track)) / TAS
        double sinWca = (windN * Math.Sin(trackRad) - windE * Math.Cos(trackRad)) / tasKts;
        sinWca = Math.Clamp(sinWca, -1.0, 1.0);

        return Math.Asin(sinWca) * RadToDeg;
    }
}
