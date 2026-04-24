using Geo;
using Geo.Geomagnetism;

namespace Yaat.Sim;

/// <summary>
/// Magnetic declination from the NOAA World Magnetic Model (WMM), via the <c>Geo</c> library.
/// Globally accurate; no CONUS-only approximation. Declination is positive east of true north,
/// negative west — matching the geodetic convention used throughout Yaat.Sim.
/// </summary>
public static class MagneticDeclination
{
    // WmmGeomagnetismCalculator performs stateless spherical-harmonic evaluation over its
    // embedded coefficient tables; safe to share across threads.
    private static readonly WmmGeomagnetismCalculator Calculator = new();

    // WMM epochs last 5 years, so the model covering "now" is stable for the lifetime of the
    // process. Resolve once at startup, then reuse — avoids a per-call LINQ scan of the 9
    // embedded models on every tick, per aircraft.
    private static readonly DateTime EpochDate = ResolveEpochDate();

    private static DateTime ResolveEpochDate()
    {
        DateTime now = DateTime.UtcNow;
        if (Calculator.Models.Any(m => m.ValidFrom <= now && m.ValidTo >= now))
        {
            return now;
        }
        // No embedded epoch covers the current date — clamp to the most recent epoch's last
        // valid day so TryCalculate still returns a best-effort result. Triggers only if YAAT
        // runs past the newest bundled WMM epoch (i.e. a stale package).
        IGeomagneticModel newest = Calculator.Models.OrderBy(m => m.ValidTo).Last();
        return newest.ValidTo.AddDays(-1.0);
    }

    /// <summary>
    /// Returns magnetic declination in degrees at the given location.
    /// Positive = east declination (magnetic north is east of true north).
    /// Negative = west declination (magnetic north is west of true north).
    /// To convert true→magnetic: magnetic = true - declination.
    /// </summary>
    public static double GetDeclination(double lat, double lon)
    {
        GeomagnetismResult? result = Calculator.TryCalculate(new Coordinate(lat, lon), EpochDate);
        return result?.Declination ?? 0.0;
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

    /// <summary>Declination at the given position.</summary>
    public static double GetDeclination(LatLon position) => GetDeclination(position.Lat, position.Lon);

    /// <summary>Convert a true direction to magnetic at the given position.</summary>
    public static double TrueToMagnetic(double trueDeg, LatLon position) => TrueToMagnetic(trueDeg, position.Lat, position.Lon);

    /// <summary>Convert a magnetic direction to true at the given position.</summary>
    public static double MagneticToTrue(double magneticDeg, LatLon position) => MagneticToTrue(magneticDeg, position.Lat, position.Lon);
}
