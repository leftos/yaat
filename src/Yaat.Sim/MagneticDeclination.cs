namespace Yaat.Sim;

/// <summary>
/// Approximate magnetic declination for CONUS. Uses a simple linear model based on
/// longitude that's accurate to ~2-3 degrees — sufficient for ATC training purposes.
/// FD winds are reported in true degrees; YAAT WindLayers use magnetic.
/// </summary>
public static class MagneticDeclination
{
    // Linear model: declination ≈ slope * longitude + intercept
    // Derived from NOAA WMM 2025 data points across CONUS:
    //   lon -125 → decl ~+14° (east), lon -70 → decl ~-14° (west)
    // Slope ≈ (14 - (-14)) / (-125 - (-70)) = 28 / -55 ≈ -0.509
    // Intercept ≈ -49.6°
    private const double Slope = -0.509;
    private const double Intercept = -49.6;

    /// <summary>
    /// Returns approximate magnetic declination in degrees.
    /// Positive = east declination (magnetic north is east of true north).
    /// Negative = west declination (magnetic north is west of true north).
    /// To convert true→magnetic: magnetic = true - declination.
    /// </summary>
    public static double GetDeclination(double lat, double lon)
    {
        // Simple linear model — lat has minor effect across CONUS, ignored
        _ = lat;
        return Slope * lon + Intercept;
    }

    /// <summary>
    /// Converts a wind direction from true degrees to magnetic degrees.
    /// </summary>
    public static double TrueToMagnetic(double trueDeg, double lat, double lon)
    {
        double declination = GetDeclination(lat, lon);
        double magnetic = trueDeg - declination;
        return ((magnetic % 360.0) + 360.0) % 360.0;
    }

    /// <summary>
    /// Converts a magnetic heading to true heading using position-based declination.
    /// </summary>
    public static double MagneticToTrue(double magneticDeg, double lat, double lon)
    {
        double declination = GetDeclination(lat, lon);
        double trueDeg = magneticDeg + declination;
        return ((trueDeg % 360.0) + 360.0) % 360.0;
    }
}
