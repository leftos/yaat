namespace Yaat.Sim.Phases;

/// <summary>
/// Direction of the traffic pattern relative to the runway.
/// Left pattern: all turns are left. Right pattern: all turns are right.
/// </summary>
public enum PatternDirection
{
    Left,
    Right,
}

/// <summary>
/// Computed waypoints for a standard rectangular traffic pattern.
/// All positions are lat/lon. Pattern is computed from runway info,
/// aircraft category, and pattern direction.
/// </summary>
public sealed class PatternWaypoints
{
    /// <summary>Departure end of runway (start of upwind/crosswind turn point).</summary>
    public double DepartureEndLat { get; init; }
    public double DepartureEndLon { get; init; }

    /// <summary>Point where crosswind turn begins (departure end + extension).</summary>
    public double CrosswindTurnLat { get; init; }
    public double CrosswindTurnLon { get; init; }

    /// <summary>Start of downwind leg (offset from crosswind turn point).</summary>
    public double DownwindStartLat { get; init; }
    public double DownwindStartLon { get; init; }

    /// <summary>Abeam the threshold on downwind.</summary>
    public double DownwindAbeamLat { get; init; }
    public double DownwindAbeamLon { get; init; }

    /// <summary>Point where base turn begins (past abeam by base extension).</summary>
    public double BaseTurnLat { get; init; }
    public double BaseTurnLon { get; init; }

    /// <summary>Runway threshold (end of final approach).</summary>
    public double ThresholdLat { get; init; }
    public double ThresholdLon { get; init; }

    /// <summary>Headings for each leg.</summary>
    public double UpwindHeading { get; init; }
    public double CrosswindHeading { get; init; }
    public double DownwindHeading { get; init; }
    public double BaseHeading { get; init; }
    public double FinalHeading { get; init; }

    /// <summary>Pattern altitude MSL.</summary>
    public double PatternAltitude { get; init; }

    /// <summary>Pattern direction (left or right turns).</summary>
    public PatternDirection Direction { get; init; }
}

/// <summary>
/// Computes traffic pattern waypoints from runway geometry and aircraft category.
/// </summary>
public static class PatternGeometry
{
    public static PatternWaypoints Compute(RunwayInfo runway, AircraftCategory category, PatternDirection direction, double? sizeOverrideNm = null)
    {
        double rwyHdg = runway.TrueHeading;
        double reciprocal = NormalizeHeading(rwyHdg + 180.0);

        // Turn offset: +90 for left pattern, -90 for right pattern
        double turnOffset = direction == PatternDirection.Left ? -90.0 : 90.0;

        double upwindHdg = rwyHdg;
        double crosswindHdg = NormalizeHeading(rwyHdg + turnOffset);
        double downwindHdg = reciprocal;
        double baseHdg = NormalizeHeading(reciprocal + turnOffset);
        double finalHdg = rwyHdg;

        double defaultSize = CategoryPerformance.PatternSizeNm(category);
        double patternSize = sizeOverrideNm ?? defaultSize;
        // Scale crosswind extension and base extension proportionally when size is overridden
        double sizeRatio = patternSize / defaultSize;
        double crosswindExt = CategoryPerformance.CrosswindExtensionNm(category) * sizeRatio;
        double baseExt = CategoryPerformance.BaseExtensionNm(category) * sizeRatio;
        double patternAltAgl = CategoryPerformance.PatternAltitudeAgl(category);
        double patternAlt = runway.ElevationFt + patternAltAgl;

        // Departure end of runway
        double depEndLat = runway.EndLatitude;
        double depEndLon = runway.EndLongitude;

        // Crosswind turn point: departure end + extension along upwind
        var crosswindTurn = GeoMath.ProjectPoint(depEndLat, depEndLon, upwindHdg, crosswindExt);

        // Downwind start: crosswind turn + offset perpendicular to runway
        var downwindStart = GeoMath.ProjectPoint(crosswindTurn.Lat, crosswindTurn.Lon, crosswindHdg, patternSize);

        // Downwind abeam: threshold offset perpendicular
        var downwindAbeam = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, crosswindHdg, patternSize);

        // Base turn point: downwind abeam + extension along downwind heading
        var baseTurn = GeoMath.ProjectPoint(downwindAbeam.Lat, downwindAbeam.Lon, downwindHdg, baseExt);

        return new PatternWaypoints
        {
            DepartureEndLat = depEndLat,
            DepartureEndLon = depEndLon,
            CrosswindTurnLat = crosswindTurn.Lat,
            CrosswindTurnLon = crosswindTurn.Lon,
            DownwindStartLat = downwindStart.Lat,
            DownwindStartLon = downwindStart.Lon,
            DownwindAbeamLat = downwindAbeam.Lat,
            DownwindAbeamLon = downwindAbeam.Lon,
            BaseTurnLat = baseTurn.Lat,
            BaseTurnLon = baseTurn.Lon,
            ThresholdLat = runway.ThresholdLatitude,
            ThresholdLon = runway.ThresholdLongitude,
            UpwindHeading = upwindHdg,
            CrosswindHeading = crosswindHdg,
            DownwindHeading = downwindHdg,
            BaseHeading = baseHdg,
            FinalHeading = finalHdg,
            PatternAltitude = patternAlt,
            Direction = direction,
        };
    }

    private static double NormalizeHeading(double heading)
    {
        return ((heading % 360.0) + 360.0) % 360.0;
    }
}
