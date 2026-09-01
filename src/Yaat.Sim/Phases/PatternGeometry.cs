using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Pattern;
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

    /// <summary>
    /// Resolved downwind offset (nm) for this pattern — the authored/command size after deconfliction,
    /// or the per-category default. Descent-profile math (downwind/base) must use this, not the bare
    /// category default, so a resized pattern descends on the correct geometry.
    /// </summary>
    public double PatternSizeNm { get; init; }

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
            PatternAltitudeFt = PatternAltitude,
            PatternSizeNm = PatternSizeNm,
            Direction = (int)Direction,
        };

    public static PatternWaypoints FromSnapshot(PatternWaypointsDto dto)
    {
        // Infer Direction from geometry when the snapshot predates the
        // Direction field: signed cross-track of the downwind abeam from the
        // threshold along the landing heading is positive for a right pattern
        // and negative for a left pattern.
        PatternDirection direction;
        if (dto.Direction is { } explicitDir)
        {
            direction = (PatternDirection)explicitDir;
        }
        else
        {
            double cross = GeoMath.SignedCrossTrackDistanceNmRaw(
                dto.DownwindAbeamLat,
                dto.DownwindAbeamLon,
                dto.ThresholdLat,
                dto.ThresholdLon,
                dto.FinalHeadingDeg
            );
            direction = cross >= 0 ? PatternDirection.Right : PatternDirection.Left;
        }

        return new PatternWaypoints
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
            PatternAltitude = dto.PatternAltitudeFt ?? 0,
            // Older snapshots predate the explicit size: derive it from geometry — the perpendicular
            // offset of the downwind abeam point from the threshold along the landing heading.
            PatternSizeNm =
                dto.PatternSizeNm
                ?? Math.Abs(
                    GeoMath.SignedCrossTrackDistanceNmRaw(
                        dto.DownwindAbeamLat,
                        dto.DownwindAbeamLon,
                        dto.ThresholdLat,
                        dto.ThresholdLon,
                        dto.FinalHeadingDeg
                    )
                ),
            Direction = direction,
        };
    }
}

/// <summary>
/// Computes traffic pattern waypoints from runway geometry and aircraft category.
/// </summary>
public static class PatternGeometry
{
    private static readonly ILogger Log = SimLog.CreateLogger("PatternGeometry");

    /// <summary>Minimum pattern size (NM) — below this, deconfliction is skipped.</summary>
    public const double MinPatternSizeNm = 0.4;

    /// <summary>Buffer distance (NM) from downwind track to neighboring runway centerline.</summary>
    public const double RunwayBufferNm = 0.15;

    /// <summary>
    /// Compose pattern size and altitude overrides from a command-issued override (e.g. TPA/PSIZE)
    /// and the airport-authored runway data. Command override wins; data fills in when no command
    /// override is set; otherwise the caller passes nulls and PatternGeometry.Compute falls back
    /// to the per-category default.
    ///
    /// Authored <see cref="GroundRunway.PatternAltitudeAglFt"/> is the airport's *established*
    /// pattern altitude (feet AGL above field elevation) — the value the AIM 4-3-3.a category
    /// rule is applied to, not a verbatim replacement: turbine aircraft fly 500 ft above it
    /// (AIM 4-3-3.a.2 — of the clause's two figures, "established + 500" is read as the
    /// general rule and "1,500 AGL" as its value at a standard 1,000 ft field; where a field
    /// authors a low pattern the +500 keeps the turbine spread over the light traffic rather
    /// than pushed into the airspace the low pattern exists to avoid), helicopters stay at or
    /// below their absolute 500 AGL (AIM 4-3-3.a.3 — when a field authors below 500 the
    /// helicopter ends up co-altitude with the aeroplane pattern, the accepted degenerate
    /// case; a fixed 500 would put it above them, which is worse), and props fly it as
    /// published. A command override is a controller instruction and wins verbatim for every
    /// category — unlike the pattern-size flyability floor, every altitude is flyable, so
    /// there is no clamp here.
    /// </summary>
    public static (double? SizeNm, double? AltitudeMslFt) ResolveAuthoredOverrides(
        RunwayInfo runway,
        GroundRunway? authoredRunway,
        AircraftCategory category,
        double? commandSizeNm,
        double? commandAltitudeMslFt
    )
    {
        double? size = commandSizeNm ?? authoredRunway?.PatternSizeNm;
        double? alt = commandAltitudeMslFt;
        if ((alt is null) && (authoredRunway?.PatternAltitudeAglFt is double agl))
        {
            double effectiveAgl = category switch
            {
                AircraftCategory.Jet or AircraftCategory.Turboprop => agl + 500,
                AircraftCategory.Helicopter => Math.Min(CategoryPerformance.PatternAltitudeAgl(AircraftCategory.Helicopter), agl),
                AircraftCategory.Piston => agl,
                // Any future category defaults turbine-side, matching CategoryPerformance.PatternAltitudeAgl's default.
                _ => agl + 500,
            };
            alt = runway.AirportElevationFt + effectiveAgl;
        }
        return (size, alt);
    }

    /// <summary>
    /// Minimum flyable pattern width (nm) for the given aircraft: the downwind→base and
    /// base→final turns each consume one turn radius of lateral room, so a pattern narrower
    /// than r(downwind speed) + r(base speed) cannot roll out on the final approach course —
    /// the aircraft is geometrically forced through it (and onto a close parallel's final;
    /// AIM FIG 4-3-3 key 7 prohibits exactly that track). Speeds are the per-type leg speeds
    /// inflated by the local wind — a partial allowance (not a worst-case bound) for the
    /// ground-speed-anticipated turns (see <see cref="BasePhase.TurnRadiusNm"/>) widening on
    /// the tailwind-quartering side.
    ///
    /// The calm-wind floor exceeds <see cref="CategoryPerformance.PatternSizeNm"/> for Jet
    /// (~1.96 vs 1.5) and Turboprop (~1.11 vs 1.0) at category speeds, so those defaults are
    /// effectively sizeRatio anchors for the base-extension scaling — the floor is the width
    /// actually flown.
    /// </summary>
    public static double MinFlyablePatternSizeNm(string aircraftType, AircraftCategory category, double windSpeedKt)
    {
        double downwindKt = AircraftPerformance.DownwindSpeed(aircraftType, category) + windSpeedKt;
        double baseKt = AircraftPerformance.BaseSpeed(aircraftType, category) + windSpeedKt;
        return BasePhase.TurnRadiusNm(downwindKt, category) + BasePhase.TurnRadiusNm(baseKt, category);
    }

    /// <param name="aircraftType">
    /// ICAO type designator of the aircraft the pattern is built for — sizes the flyability
    /// floor from the type's actual leg speeds (falls back to category speeds when unknown).
    /// </param>
    /// <param name="windSpeedKt">
    /// Wind speed (kt) at the aircraft, used to inflate the floor's planning speeds; pass
    /// <see cref="AircraftState.WindSpeedKts"/> (0 when no weather is loaded).
    /// </param>
    /// <param name="authoredRunway">
    /// The same ground runway <see cref="ResolveAuthoredOverrides"/> takes, used here for the published
    /// threshold displacement. Null when no airport map is loaded, in which case the pattern is built to
    /// the pavement threshold.
    /// </param>
    public static PatternWaypoints Compute(
        RunwayInfo runway,
        AircraftCategory category,
        string aircraftType,
        double windSpeedKt,
        PatternDirection direction,
        double? sizeOverrideNm,
        double? altitudeOverrideFt,
        IReadOnlyList<RunwayInfo>? airportRunways,
        GroundRunway? authoredRunway
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

        // Flyability floor — wins over the category default, authored/command sizes, AND the
        // deconfliction shrink: an unflyable width overshoots the final onto whatever lies
        // beyond it (issue #412: a PAY3 forced through OAK 28L's authored 0.5 nm pattern onto
        // the parallel 28R final), which is strictly worse than a wide downwind overlying a
        // neighboring runway. AIM 4-3-3.b: pattern size varies with aircraft performance.
        double floorNm = MinFlyablePatternSizeNm(aircraftType, category, windSpeedKt);
        if (patternSize < floorNm)
        {
            Log.LogInformation(
                "Pattern for {Runway} widened from {Requested:F2} nm to the {Type} flyability floor {Floor:F2} nm (wind {Wind:F0} kt)",
                runway.Designator,
                patternSize,
                aircraftType,
                floorNm,
                windSpeedKt
            );
            patternSize = floorNm;
        }

        // The base extension scales proportionally with pattern size (a smaller pattern has a tighter
        // base leg). The crosswind turn, by contrast, is anchored at the runway's departure end (DER):
        // the upwind length is governed by runway geometry, not pattern size. AIM 4-3-2 commences the
        // crosswind turn beyond the departure end of the runway within 300 ft of pattern altitude —
        // UpwindPhase enforces that gate, so a smaller pattern keeps the same at-the-DER upwind.
        double sizeRatio = patternSize / defaultSize;
        double baseExt = CategoryPerformance.BaseExtensionNm(category) * sizeRatio;
        double patternAltAgl = CategoryPerformance.PatternAltitudeAgl(category);
        double patternAlt = altitudeOverrideFt ?? (runway.AirportElevationFt + patternAltAgl);

        // The landing threshold anchors everything the arrival flies to — downwind abeam, the base turn,
        // and the final's aim point. A displaced threshold moves all three downfield with it.
        var threshold = LandingThreshold.Resolve(runway, authoredRunway);

        // Departure end of runway. Deliberately the pavement end, not a displaced-threshold-derived
        // point: the crosswind turn is a *departure* geometry anchor (AIM 4-3-2, "beyond the departure
        // end"), and pre-threshold pavement is available for takeoff in either direction
        // (AIM 2-3-3.b.8.2). Only the arrival side of the pattern moves with the displacement.
        double depEndLat = runway.EndLatitude;
        double depEndLon = runway.EndLongitude;

        // Crosswind turn point: at the departure end of the runway.
        (double Lat, double Lon) crosswindTurn = (depEndLat, depEndLon);

        // Downwind start: crosswind turn + offset perpendicular to runway
        var downwindStart = GeoMath.ProjectPoint(crosswindTurn.Lat, crosswindTurn.Lon, crosswindHdg, patternSize);

        // Downwind abeam: threshold offset perpendicular
        var downwindAbeam = GeoMath.ProjectPoint(threshold.Lat, threshold.Lon, crosswindHdg, patternSize);

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
            ThresholdLat = threshold.Lat,
            ThresholdLon = threshold.Lon,
            UpwindHeading = upwindHdg,
            CrosswindHeading = crosswindHdg,
            DownwindHeading = downwindHdg,
            BaseHeading = baseHdg,
            FinalHeading = finalHdg,
            PatternAltitude = patternAlt,
            PatternSizeNm = patternSize,
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
