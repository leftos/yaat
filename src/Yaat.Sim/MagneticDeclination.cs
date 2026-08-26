using System.Collections.Concurrent;
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
        if (!double.IsFinite(lat) || !double.IsFinite(lon))
        {
            return Evaluate(lat, lon);
        }

        var cell = ((int)Math.Floor(lat / GridCellDeg), (int)Math.Floor(lon / GridCellDeg));
        if (GridCache.TryGetValue(cell, out double cached))
        {
            return cached;
        }

        if (GridCache.Count >= GridCacheMaxEntries)
        {
            GridCache.Clear();
        }

        // Evaluate at the cell centre so a cell's value is a pure function of the cell, not of which
        // aircraft happened to enter it first — replays stay reproducible across runs and threads.
        double declination = Evaluate((cell.Item1 + 0.5) * GridCellDeg, (cell.Item2 + 0.5) * GridCellDeg);
        GridCache[cell] = declination;
        return declination;
    }

    // Each WMM evaluation allocates ~100 KB of working arrays inside the Geo library, and every
    // aircraft re-evaluates each time it moves ~1 nm (FlightPhysics' per-aircraft gate). Aircraft in
    // a pattern or on the ground keep crossing the same few cells, so share results on a grid of
    // the same 0.02° (~1.2 nm) size the per-aircraft gate already treats as "no visible change"
    // (declination varies ~0.01°/km, so a cell-centre value is within ~0.01° of any point in it).
    private const double GridCellDeg = 0.02;
    private const int GridCacheMaxEntries = 200_000;
    private static readonly ConcurrentDictionary<(int Lat, int Lon), double> GridCache = new();

    private static double Evaluate(double lat, double lon)
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
