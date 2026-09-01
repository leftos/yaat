using System.Collections.Concurrent;
using Geo;
using Geo.Geomagnetism;

namespace Yaat.Sim;

/// <summary>
/// Magnetic declination from the NOAA World Magnetic Model (WMM), via the <c>Geo</c> library.
/// Globally accurate; no CONUS-only approximation. Declination is positive east of true north,
/// negative west — matching the geodetic convention used throughout Yaat.Sim.
///
/// The model is evaluated at a whole UTC day. Anything that feeds simulation state passes the scenario's
/// <see cref="Simulation.SimScenarioState.MagneticModelDateUtc"/> (recorded with the session, so a replay a
/// year later computes the same declinations); display-only readouts use <see cref="EvaluationDateUtc"/>,
/// the day the process started.
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

    /// <summary>
    /// The instant display-only readouts evaluate the WMM at: the UTC day the process started, never a time of
    /// day, so two processes started seconds apart agree. Simulation state uses the scenario's recorded date.
    /// </summary>
    public static DateTime EvaluationDateUtc => EpochDate;

    private static DateTime ResolveEpochDate() => ClampToModelRange(DateTime.UtcNow.Date);

    /// <summary>
    /// Clamps an evaluation date into the range the bundled WMM epochs cover. A date past the newest bundled
    /// epoch (a stale package, or a recording made after it) evaluates at that epoch's last valid day so
    /// <c>TryCalculate</c> still returns a best-effort result instead of nothing.
    /// </summary>
    private static DateTime ClampToModelRange(DateTime dateUtc)
    {
        if (Calculator.Models.Any(m => m.ValidFrom <= dateUtc && m.ValidTo >= dateUtc))
        {
            return dateUtc;
        }

        IGeomagneticModel newest = Calculator.Models.OrderBy(m => m.ValidTo).Last();
        return newest.ValidTo.AddDays(-1.0);
    }

    /// <summary>
    /// Returns magnetic declination in degrees at the given location, evaluated at the process day.
    /// Positive = east declination (magnetic north is east of true north).
    /// Negative = west declination (magnetic north is west of true north).
    /// To convert true→magnetic: magnetic = true - declination.
    /// </summary>
    public static double GetDeclination(double lat, double lon) => GetDeclination(lat, lon, EpochDate);

    /// <summary>Declination at the given location evaluated at <paramref name="modelDateUtc"/> (a whole UTC day).</summary>
    public static double GetDeclination(double lat, double lon, DateTime modelDateUtc)
    {
        if (!double.IsFinite(lat) || !double.IsFinite(lon))
        {
            return Evaluate(lat, lon, modelDateUtc);
        }

        var cell = ((int)Math.Floor(lat / GridCellDeg), (int)Math.Floor(lon / GridCellDeg), modelDateUtc.Ticks);
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
        double declination = Evaluate((cell.Item1 + 0.5) * GridCellDeg, (cell.Item2 + 0.5) * GridCellDeg, modelDateUtc);
        GridCache[cell] = declination;
        return declination;
    }

    // Each WMM evaluation allocates ~100 KB of working arrays inside the Geo library, and every
    // aircraft re-evaluates each time it moves ~1 nm (FlightPhysics' per-aircraft gate). Aircraft in
    // a pattern or on the ground keep crossing the same few cells, so share results on a grid of
    // the same 0.02° (~1.2 nm) size the per-aircraft gate already treats as "no visible change"
    // (declination varies ~0.01°/km, so a cell-centre value is within ~0.01° of any point in it).
    // Keyed by evaluation date as well: a room replaying an old recording and a live room evaluate
    // at different days in the same process.
    private const double GridCellDeg = 0.02;
    private const int GridCacheMaxEntries = 200_000;
    private static readonly ConcurrentDictionary<(int Lat, int Lon, long DateTicks), double> GridCache = new();

    private static double Evaluate(double lat, double lon, DateTime modelDateUtc)
    {
        GeomagnetismResult? result = Calculator.TryCalculate(new Coordinate(lat, lon), ClampToModelRange(modelDateUtc));
        return result?.Declination ?? 0.0;
    }

    /// <summary>Converts a true direction to magnetic degrees at the process day (display use).</summary>
    public static double TrueToMagnetic(double trueDeg, double lat, double lon) => TrueToMagnetic(trueDeg, lat, lon, EpochDate);

    /// <summary>Converts a true direction to magnetic degrees, evaluated at <paramref name="modelDateUtc"/>.</summary>
    public static double TrueToMagnetic(double trueDeg, double lat, double lon, DateTime modelDateUtc)
    {
        double declination = GetDeclination(lat, lon, modelDateUtc);
        double magnetic = trueDeg - declination;
        return ((magnetic % 360.0) + 360.0) % 360.0;
    }

    /// <summary>Converts a magnetic heading to true using position-based declination at the process day (display use).</summary>
    public static double MagneticToTrue(double magneticDeg, double lat, double lon) => MagneticToTrue(magneticDeg, lat, lon, EpochDate);

    /// <summary>Converts a magnetic heading to true, evaluated at <paramref name="modelDateUtc"/>.</summary>
    public static double MagneticToTrue(double magneticDeg, double lat, double lon, DateTime modelDateUtc)
    {
        double declination = GetDeclination(lat, lon, modelDateUtc);
        double trueDeg = magneticDeg + declination;
        return ((trueDeg % 360.0) + 360.0) % 360.0;
    }

    /// <summary>Declination at the given position, evaluated at the process day (display use).</summary>
    public static double GetDeclination(LatLon position) => GetDeclination(position.Lat, position.Lon);

    /// <summary>Declination at the given position, evaluated at <paramref name="modelDateUtc"/>.</summary>
    public static double GetDeclination(LatLon position, DateTime modelDateUtc) => GetDeclination(position.Lat, position.Lon, modelDateUtc);

    /// <summary>Convert a true direction to magnetic at the given position, evaluated at the process day (display use).</summary>
    public static double TrueToMagnetic(double trueDeg, LatLon position) => TrueToMagnetic(trueDeg, position.Lat, position.Lon);

    /// <summary>Convert a true direction to magnetic at the given position, evaluated at <paramref name="modelDateUtc"/>.</summary>
    public static double TrueToMagnetic(double trueDeg, LatLon position, DateTime modelDateUtc) =>
        TrueToMagnetic(trueDeg, position.Lat, position.Lon, modelDateUtc);

    /// <summary>Convert a magnetic direction to true at the given position, evaluated at the process day (display use).</summary>
    public static double MagneticToTrue(double magneticDeg, LatLon position) => MagneticToTrue(magneticDeg, position.Lat, position.Lon);

    /// <summary>Convert a magnetic direction to true at the given position, evaluated at <paramref name="modelDateUtc"/>.</summary>
    public static double MagneticToTrue(double magneticDeg, LatLon position, DateTime modelDateUtc) =>
        MagneticToTrue(magneticDeg, position.Lat, position.Lon, modelDateUtc);
}
