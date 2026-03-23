using Yaat.Sim.Simulation.Snapshots;

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
    public TrueHeading UpwindHeading { get; init; }
    public TrueHeading CrosswindHeading { get; init; }
    public TrueHeading DownwindHeading { get; init; }
    public TrueHeading BaseHeading { get; init; }
    public TrueHeading FinalHeading { get; init; }

    /// <summary>Pattern altitude MSL.</summary>
    public double PatternAltitude { get; init; }

    /// <summary>Pattern direction (left or right turns).</summary>
    public PatternDirection Direction { get; init; }

    public PatternWaypointsDto ToSnapshot() =>
        new()
        {
            DepartureEndLat = DepartureEndLat,
            DepartureEndLon = DepartureEndLon,
            CrosswindTurnLat = CrosswindTurnLat,
            CrosswindTurnLon = CrosswindTurnLon,
            DownwindStartLat = DownwindStartLat,
            DownwindStartLon = DownwindStartLon,
            DownwindAbeamLat = DownwindAbeamLat,
            DownwindAbeamLon = DownwindAbeamLon,
            BaseTurnLat = BaseTurnLat,
            BaseTurnLon = BaseTurnLon,
            ThresholdLat = ThresholdLat,
            ThresholdLon = ThresholdLon,
            UpwindHeadingDeg = UpwindHeading.Degrees,
            CrosswindHeadingDeg = CrosswindHeading.Degrees,
            DownwindHeadingDeg = DownwindHeading.Degrees,
            BaseHeadingDeg = BaseHeading.Degrees,
            FinalHeadingDeg = FinalHeading.Degrees,
        };

    public static PatternWaypoints FromSnapshot(PatternWaypointsDto dto) =>
        new()
        {
            DepartureEndLat = dto.DepartureEndLat,
            DepartureEndLon = dto.DepartureEndLon,
            CrosswindTurnLat = dto.CrosswindTurnLat,
            CrosswindTurnLon = dto.CrosswindTurnLon,
            DownwindStartLat = dto.DownwindStartLat,
            DownwindStartLon = dto.DownwindStartLon,
            DownwindAbeamLat = dto.DownwindAbeamLat,
            DownwindAbeamLon = dto.DownwindAbeamLon,
            BaseTurnLat = dto.BaseTurnLat,
            BaseTurnLon = dto.BaseTurnLon,
            ThresholdLat = dto.ThresholdLat,
            ThresholdLon = dto.ThresholdLon,
            UpwindHeading = new TrueHeading(dto.UpwindHeadingDeg),
            CrosswindHeading = new TrueHeading(dto.CrosswindHeadingDeg),
            DownwindHeading = new TrueHeading(dto.DownwindHeadingDeg),
            BaseHeading = new TrueHeading(dto.BaseHeadingDeg),
            FinalHeading = new TrueHeading(dto.FinalHeadingDeg),
        };
}

/// <summary>
/// Computes traffic pattern waypoints from runway geometry and aircraft category.
/// </summary>
public static class PatternGeometry
{
    /// <summary>Minimum pattern size (NM) — below this, deconfliction is skipped.</summary>
    public const double MinPatternSizeNm = 0.4;

    /// <summary>Buffer distance (NM) from downwind track to neighboring runway centerline.</summary>
    public const double RunwayBufferNm = 0.15;

    public static PatternWaypoints Compute(
        RunwayInfo runway,
        AircraftCategory category,
        PatternDirection direction,
        double? sizeOverrideNm,
        double? altitudeOverrideFt,
        IReadOnlyList<RunwayInfo>? airportRunways
    )
    {
        TrueHeading rwyHdg = runway.TrueHeading;

        // Turn offset: +90 for left pattern, -90 for right pattern
        double turnOffset = direction == PatternDirection.Left ? -90.0 : 90.0;

        TrueHeading upwindHdg = rwyHdg;
        TrueHeading crosswindHdg = new TrueHeading(rwyHdg.Degrees + turnOffset);
        TrueHeading downwindHdg = rwyHdg.ToReciprocal();
        TrueHeading baseHdg = new TrueHeading(downwindHdg.Degrees + turnOffset);
        TrueHeading finalHdg = rwyHdg;

        double defaultSize = CategoryPerformance.PatternSizeNm(category);
        double patternSize = sizeOverrideNm ?? defaultSize;

        // Deconfliction: shrink pattern if downwind would encroach on another runway
        patternSize = ApplyRunwayDeconfliction(runway, direction, crosswindHdg, patternSize, airportRunways);

        // Scale crosswind extension and base extension proportionally when size is overridden
        double sizeRatio = patternSize / defaultSize;
        double crosswindExt = CategoryPerformance.CrosswindExtensionNm(category) * sizeRatio;
        double baseExt = CategoryPerformance.BaseExtensionNm(category) * sizeRatio;
        double patternAltAgl = CategoryPerformance.PatternAltitudeAgl(category);
        double patternAlt = altitudeOverrideFt ?? (runway.ElevationFt + patternAltAgl);

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

    /// <summary>
    /// Shrink pattern size if the downwind leg would encroach on another runway.
    /// Returns the (possibly reduced) pattern size. Skips deconfliction when:
    /// same physical runway, runways that physically cross (centerlines intersect
    /// within their surfaces), other runway on wrong side, or too close to adjust
    /// (below minimum floor).
    /// </summary>
    private static double ApplyRunwayDeconfliction(
        RunwayInfo runway,
        PatternDirection direction,
        TrueHeading crosswindHdg,
        double patternSize,
        IReadOnlyList<RunwayInfo>? airportRunways
    )
    {
        if (airportRunways is null || airportRunways.Count <= 1)
        {
            return patternSize;
        }

        double result = patternSize;

        foreach (var other in airportRunways)
        {
            // Skip the same physical runway
            if (other.Id == runway.Id)
            {
                continue;
            }

            // Skip runways that physically cross — their centerlines intersect
            // within the actual runway surfaces, making avoidance impossible
            if (RunwaysCross(runway, other))
            {
                continue;
            }

            // Compute perpendicular distance from the pattern runway centerline to
            // the other runway's midpoint. SignedCrossTrackDistanceNm along the runway
            // heading gives the signed offset: positive = right of centerline, negative = left.
            double otherMidLat = (other.Lat1 + other.Lat2) / 2.0;
            double otherMidLon = (other.Lon1 + other.Lon2) / 2.0;

            double signedPerp = GeoMath.SignedCrossTrackDistanceNm(
                otherMidLat,
                otherMidLon,
                runway.ThresholdLatitude,
                runway.ThresholdLongitude,
                runway.TrueHeading
            );

            // For left pattern, the pattern side is LEFT (negative cross-track).
            // For right pattern, the pattern side is RIGHT (positive cross-track).
            // Flip sign so positive always means "on the pattern side".
            double crossTrackDist = direction == PatternDirection.Left ? -signedPerp : signedPerp;

            // Positive = on the pattern side, negative = opposite side — no conflict
            if (crossTrackDist <= 0)
            {
                continue;
            }

            // Check if the downwind track would encroach on this runway
            if (crossTrackDist < result + RunwayBufferNm)
            {
                double newSize = crossTrackDist - RunwayBufferNm;

                // If we can't fit a viable pattern, skip deconfliction entirely
                if (newSize < MinPatternSizeNm)
                {
                    continue;
                }

                result = Math.Min(result, newSize);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns true if two runways physically cross — their centerline segments
    /// intersect within both runway surfaces. Converging runways that meet beyond
    /// their endpoints do NOT count as crossing.
    /// </summary>
    public static bool RunwaysCross(RunwayInfo a, RunwayInfo b)
    {
        // Use line segment intersection test on the two centerlines.
        // Each runway is a segment from (Lat1,Lon1) to (Lat2,Lon2).
        return SegmentsIntersect(a.Lat1, a.Lon1, a.Lat2, a.Lon2, b.Lat1, b.Lon1, b.Lat2, b.Lon2);
    }

    /// <summary>
    /// Tests whether two line segments (p1→p2 and p3→p4) intersect.
    /// Uses the cross-product orientation method.
    /// </summary>
    private static bool SegmentsIntersect(double p1x, double p1y, double p2x, double p2y, double p3x, double p3y, double p4x, double p4y)
    {
        double d1 = CrossProduct(p3x, p3y, p4x, p4y, p1x, p1y);
        double d2 = CrossProduct(p3x, p3y, p4x, p4y, p2x, p2y);
        double d3 = CrossProduct(p1x, p1y, p2x, p2y, p3x, p3y);
        double d4 = CrossProduct(p1x, p1y, p2x, p2y, p4x, p4y);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
        {
            return true;
        }

        // Collinear overlap cases — treat as non-crossing for pattern purposes
        return false;
    }

    /// <summary>
    /// 2D cross product of vectors (b-a) and (c-a).
    /// Positive = c is left of a→b, negative = right, zero = collinear.
    /// </summary>
    private static double CrossProduct(double ax, double ay, double bx, double by, double cx, double cy)
    {
        return ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
    }
}
