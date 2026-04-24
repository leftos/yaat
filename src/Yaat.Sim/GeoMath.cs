namespace Yaat.Sim;

public static class GeoMath
{
    private const double EarthRadiusNm = 3440.065;
    private const double DegToRad = Math.PI / 180.0;
    private const double NmPerDegLat = 60.0;

    public const double FeetPerNm = 6076.12;

    /// <summary>
    /// Haversine distance between two lat/lon points, in nautical miles.
    /// </summary>
    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double rLat1 = lat1 * Math.PI / 180.0;
        double rLat2 = lat2 * Math.PI / 180.0;

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(rLat1) * Math.Cos(rLat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusNm * c;
    }

    /// <summary>
    /// Initial bearing (degrees true, 0-360) from point 1 to point 2.
    /// </summary>
    public static double BearingTo(double lat1, double lon1, double lat2, double lon2)
    {
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double rLat1 = lat1 * Math.PI / 180.0;
        double rLat2 = lat2 * Math.PI / 180.0;
        double y = Math.Sin(dLon) * Math.Cos(rLat2);
        double x = Math.Cos(rLat1) * Math.Sin(rLat2) - Math.Sin(rLat1) * Math.Cos(rLat2) * Math.Cos(dLon);
        double bearing = Math.Atan2(y, x) * 180.0 / Math.PI;
        return (bearing + 360.0) % 360.0;
    }

    /// <summary>
    /// Turn current heading toward target bearing by at most maxTurnDeg.
    /// Returns the new heading (0-360).
    /// </summary>
    public static TrueHeading TurnHeadingToward(TrueHeading current, double targetBearing, double maxTurnDeg)
    {
        double diff = targetBearing - current.Degrees;
        while (diff > 180)
        {
            diff -= 360;
        }

        while (diff < -180)
        {
            diff += 360;
        }

        if (Math.Abs(diff) <= maxTurnDeg)
        {
            return new TrueHeading(targetBearing);
        }

        return new TrueHeading(current.Degrees + Math.Sign(diff) * maxTurnDeg);
    }

    /// <summary>
    /// Projects a point from a given lat/lon along a heading for a given distance.
    /// </summary>
    public static (double Lat, double Lon) ProjectPoint(double lat, double lon, TrueHeading heading, double distanceNm) =>
        ProjectPointRaw(lat, lon, heading.Degrees, distanceNm);

    /// <summary>Projects a point along a raw bearing angle (not a typed heading). Internal use only.</summary>
    internal static (double Lat, double Lon) ProjectPointRaw(double lat, double lon, double bearingDeg, double distanceNm)
    {
        double headingRad = bearingDeg * DegToRad;
        double latRad = lat * DegToRad;

        double newLat = lat + (distanceNm * Math.Cos(headingRad) / NmPerDegLat);
        double newLon = lon + (distanceNm * Math.Sin(headingRad) / (NmPerDegLat * Math.Cos(latRad)));

        return (newLat, newLon);
    }

    /// <summary>
    /// Generates intermediate waypoints along a circular arc from startBearing to endBearing,
    /// centered at (centerLat, centerLon) with the given radius.
    /// Does NOT include the start point; DOES include the end point.
    /// </summary>
    public static List<(double Lat, double Lon)> GenerateArcPoints(
        double centerLat,
        double centerLon,
        double radiusNm,
        double startBearingDeg,
        double endBearingDeg,
        bool turnRight,
        double stepDeg = 5.0
    )
    {
        var points = new List<(double Lat, double Lon)>();

        double current;
        double totalSweep;

        if (turnRight)
        {
            totalSweep = endBearingDeg - startBearingDeg;
            if (totalSweep <= 0)
            {
                totalSweep += 360.0;
            }
        }
        else
        {
            totalSweep = startBearingDeg - endBearingDeg;
            if (totalSweep <= 0)
            {
                totalSweep += 360.0;
            }
        }

        double swept = 0;
        while (swept + stepDeg < totalSweep)
        {
            swept += stepDeg;
            current = turnRight ? startBearingDeg + swept : startBearingDeg - swept;
            current = ((current % 360.0) + 360.0) % 360.0;

            var pt = ProjectPointRaw(centerLat, centerLon, current, radiusNm);
            points.Add(pt);
        }

        // Always include the end point at exact end bearing
        var endPt = ProjectPointRaw(centerLat, centerLon, endBearingDeg, radiusNm);
        points.Add(endPt);

        return points;
    }

    /// <summary>
    /// Signed perpendicular distance from a point to a line defined by
    /// a reference point and heading. Positive = right of heading, negative = left.
    /// </summary>
    public static double SignedCrossTrackDistanceNm(double pointLat, double pointLon, double refLat, double refLon, TrueHeading heading) =>
        SignedCrossTrackDistanceNmRaw(pointLat, pointLon, refLat, refLon, heading.Degrees);

    /// <summary>Signed cross-track distance using a raw bearing angle. Internal use only.</summary>
    internal static double SignedCrossTrackDistanceNmRaw(double pointLat, double pointLon, double refLat, double refLon, double headingDeg)
    {
        double bearing = BearingTo(refLat, refLon, pointLat, pointLon);
        double dist = DistanceNm(refLat, refLon, pointLat, pointLon);
        double angleDiff = (bearing - headingDeg) * DegToRad;
        return dist * Math.Sin(angleDiff);
    }

    /// <summary>
    /// Signed distance along a heading from a reference point to a target point.
    /// Positive = ahead (in heading direction), negative = behind.
    /// </summary>
    public static double AlongTrackDistanceNm(double pointLat, double pointLon, double refLat, double refLon, TrueHeading heading) =>
        AlongTrackDistanceNmRaw(pointLat, pointLon, refLat, refLon, heading.Degrees);

    /// <summary>Along-track distance using a raw bearing angle. Internal use only.</summary>
    internal static double AlongTrackDistanceNmRaw(double pointLat, double pointLon, double refLat, double refLon, double headingDeg)
    {
        double bearing = BearingTo(refLat, refLon, pointLat, pointLon);
        double dist = DistanceNm(refLat, refLon, pointLat, pointLon);
        double angleDiff = (bearing - headingDeg) * DegToRad;
        return dist * Math.Cos(angleDiff);
    }

    /// <summary>
    /// Signed angle difference between two raw bearing angles, normalized to [-180, 180).
    /// Use for pure geometry (taxiway/runway bearing comparisons). For headings, use
    /// TrueHeading.SignedAngleTo() or MagneticHeading.SignedAngleTo() instead.
    /// </summary>
    public static double SignedBearingDifference(double fromDeg, double toDeg)
    {
        double diff = (toDeg - fromDeg) % 360.0;
        if (diff > 180.0)
        {
            diff -= 360.0;
        }

        if (diff < -180.0)
        {
            diff += 360.0;
        }

        return diff;
    }

    /// <summary>Absolute bearing difference in [0, 180].</summary>
    public static double AbsBearingDifference(double a, double b) => Math.Abs(SignedBearingDifference(a, b));

    /// <summary>
    /// Interpolate between two bearings by <paramref name="t"/> ∈ [0,1].
    /// At t=0 returns <paramref name="fromDeg"/>, at t=1 returns <paramref name="toDeg"/>.
    /// Takes the shortest angular path between them.
    /// </summary>
    public static double BlendBearings(double fromDeg, double toDeg, double t)
    {
        double diff = SignedBearingDifference(fromDeg, toDeg);
        return ((fromDeg + (diff * t)) % 360.0 + 360.0) % 360.0;
    }

    /// <summary>
    /// Perpendicular distance in feet from a point to a line segment defined by
    /// two endpoints. Clamps to the nearest endpoint if the projection falls
    /// outside the segment.
    /// </summary>
    public static double DistanceToSegmentFt(double pointLat, double pointLon, double segALat, double segALon, double segBLat, double segBLon)
    {
        var (footLat, footLon, _, _) = FootOfPerpendicular(pointLat, pointLon, segALat, segALon, segBLat, segBLon);
        return DistanceNm(pointLat, pointLon, footLat, footLon) * FeetPerNm;
    }

    /// <summary>
    /// Project a point onto a line segment and return the foot of perpendicular
    /// clamped to the segment's endpoints. Also returns the along-segment distance
    /// (nautical miles, 0 at segment start, <see cref="DistanceNm"/> between endpoints
    /// at segment end) and a flag indicating whether the foot was clamped — callers
    /// that need to distinguish "point projects onto segment interior" vs
    /// "point projects past an endpoint" use this to filter out off-segment candidates.
    /// </summary>
    /// <param name="pointLat">Point latitude (degrees).</param>
    /// <param name="pointLon">Point longitude (degrees).</param>
    /// <param name="segALat">Segment start latitude (degrees).</param>
    /// <param name="segALon">Segment start longitude (degrees).</param>
    /// <param name="segBLat">Segment end latitude (degrees).</param>
    /// <param name="segBLon">Segment end longitude (degrees).</param>
    /// <returns>
    /// (FootLat, FootLon) — the foot of perpendicular on the segment, clamped to endpoints.
    /// AlongNm — distance from segment start along the segment to the foot.
    /// Clamped — true if the perpendicular projection fell outside the segment
    /// and the result was clamped to the nearest endpoint.
    /// </returns>
    public static (double FootLat, double FootLon, double AlongNm, bool Clamped) FootOfPerpendicular(
        double pointLat,
        double pointLon,
        double segALat,
        double segALon,
        double segBLat,
        double segBLon
    )
    {
        double segLengthNm = DistanceNm(segALat, segALon, segBLat, segBLon);
        if (segLengthNm < 1e-10)
        {
            return (segALat, segALon, 0.0, true);
        }

        double segBearing = BearingTo(segALat, segALon, segBLat, segBLon);
        double alongNm = AlongTrackDistanceNmRaw(pointLat, pointLon, segALat, segALon, segBearing);

        if (alongNm <= 0.0)
        {
            return (segALat, segALon, 0.0, true);
        }

        if (alongNm >= segLengthNm)
        {
            return (segBLat, segBLon, segLengthNm, true);
        }

        var (footLat, footLon) = ProjectPointRaw(segALat, segALon, segBearing, alongNm);
        return (footLat, footLon, alongNm, false);
    }

    /// <summary>
    /// Parametric line-segment intersection in lat/lon coordinates. Treats the
    /// inputs as 2-D Cartesian for the purposes of the crossing test — adequate
    /// for airport-scale geometry (errors are small-angle). Returns null when
    /// the segments are parallel, when their infinite-line intersection falls
    /// outside either extent, or when they only meet at an endpoint of both
    /// segments (endpoint-only touches are not treated as crossings; segments
    /// share a common endpoint all the time in a connected graph).
    ///
    /// <para>
    /// Lat is treated as the first coordinate and Lon as the second. The
    /// returned <c>T</c> parameter is along segment A (0 at A1, 1 at A2) and
    /// <c>U</c> is along segment B. Use <c>(1 - epsilon, epsilon)</c>
    /// boundaries from either end via the <paramref name="excludeEndpoints"/>
    /// argument to reject touching-at-endpoint cases (useful when callers test
    /// whether a synthetic segment crosses graph edges that share a common
    /// node).
    /// </para>
    /// </summary>
    /// <param name="ax1">Segment A, endpoint 1 latitude.</param>
    /// <param name="ay1">Segment A, endpoint 1 longitude.</param>
    /// <param name="ax2">Segment A, endpoint 2 latitude.</param>
    /// <param name="ay2">Segment A, endpoint 2 longitude.</param>
    /// <param name="bx1">Segment B, endpoint 1 latitude.</param>
    /// <param name="by1">Segment B, endpoint 1 longitude.</param>
    /// <param name="bx2">Segment B, endpoint 2 latitude.</param>
    /// <param name="by2">Segment B, endpoint 2 longitude.</param>
    /// <param name="excludeEndpoints">
    /// When true, intersections within <c>1e-6</c> of either segment's endpoints
    /// are not reported. Use when the inputs may legitimately share endpoints
    /// (e.g. testing a synthesised "aircraft → node" ingress segment against
    /// other graph edges incident to that same node).
    /// </param>
    /// <returns>(Lat, Lon, T, U) at the intersection, or null.</returns>
    public static (double Lat, double Lon, double T, double U)? SegmentsIntersect(
        double ax1,
        double ay1,
        double ax2,
        double ay2,
        double bx1,
        double by1,
        double bx2,
        double by2,
        bool excludeEndpoints = false
    )
    {
        double dx1 = ax2 - ax1;
        double dy1 = ay2 - ay1;
        double dx2 = bx2 - bx1;
        double dy2 = by2 - by1;

        double denom = dx1 * dy2 - dy1 * dx2;
        if (Math.Abs(denom) < 1e-12)
        {
            return null;
        }

        double t = ((bx1 - ax1) * dy2 - (by1 - ay1) * dx2) / denom;
        double u = ((bx1 - ax1) * dy1 - (by1 - ay1) * dx1) / denom;

        if (t < 0.0 || t > 1.0 || u < 0.0 || u > 1.0)
        {
            return null;
        }

        if (excludeEndpoints)
        {
            const double eps = 1e-6;
            if (t < eps || t > 1.0 - eps || u < eps || u > 1.0 - eps)
            {
                return null;
            }
        }

        double lat = ax1 + t * dx1;
        double lon = ay1 + t * dy1;
        return (lat, lon, t, u);
    }

    // LatLon-shaped overloads — thin wrappers around the scalar forms above. The math
    // lives in one place; these just deconstruct and re-pack. Introduced as part of
    // the LatLon refactor; the scalar forms are retired once all callers are migrated.

    /// <summary>Haversine distance between two points, nautical miles.</summary>
    public static double DistanceNm(LatLon a, LatLon b) => DistanceNm(a.Lat, a.Lon, b.Lat, b.Lon);

    /// <summary>Initial bearing (0-360 true) from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static double BearingTo(LatLon from, LatLon to) => BearingTo(from.Lat, from.Lon, to.Lat, to.Lon);

    /// <summary>Project a point along a heading for a given distance.</summary>
    public static LatLon ProjectPoint(LatLon from, TrueHeading heading, double distanceNm)
    {
        var (lat, lon) = ProjectPoint(from.Lat, from.Lon, heading, distanceNm);
        return new LatLon(lat, lon);
    }

    /// <summary>Project a point along a raw bearing angle. Internal use only.</summary>
    internal static LatLon ProjectPointRaw(LatLon from, double bearingDeg, double distanceNm)
    {
        var (lat, lon) = ProjectPointRaw(from.Lat, from.Lon, bearingDeg, distanceNm);
        return new LatLon(lat, lon);
    }

    /// <summary>Arc waypoints from <paramref name="startBearingDeg"/> to <paramref name="endBearingDeg"/> around <paramref name="center"/>.</summary>
    public static List<LatLon> GenerateArcPoints(
        LatLon center,
        double radiusNm,
        double startBearingDeg,
        double endBearingDeg,
        bool turnRight,
        double stepDeg = 5.0
    )
    {
        var raw = GenerateArcPoints(center.Lat, center.Lon, radiusNm, startBearingDeg, endBearingDeg, turnRight, stepDeg);
        var result = new List<LatLon>(raw.Count);
        foreach (var (lat, lon) in raw)
        {
            result.Add(new LatLon(lat, lon));
        }
        return result;
    }

    /// <summary>Signed perpendicular distance (NM) from <paramref name="point"/> to the line through <paramref name="reference"/> with <paramref name="heading"/>. Positive = right.</summary>
    public static double SignedCrossTrackDistanceNm(LatLon point, LatLon reference, TrueHeading heading) =>
        SignedCrossTrackDistanceNm(point.Lat, point.Lon, reference.Lat, reference.Lon, heading);

    /// <summary>Signed along-track distance (NM) from <paramref name="reference"/> in the heading direction toward <paramref name="point"/>.</summary>
    public static double AlongTrackDistanceNm(LatLon point, LatLon reference, TrueHeading heading) =>
        AlongTrackDistanceNm(point.Lat, point.Lon, reference.Lat, reference.Lon, heading);

    /// <summary>Perpendicular distance in feet from <paramref name="point"/> to the segment between <paramref name="segA"/> and <paramref name="segB"/>, clamped to endpoints.</summary>
    public static double DistanceToSegmentFt(LatLon point, LatLon segA, LatLon segB) =>
        DistanceToSegmentFt(point.Lat, point.Lon, segA.Lat, segA.Lon, segB.Lat, segB.Lon);

    /// <summary>Foot of perpendicular from <paramref name="point"/> onto the segment, clamped to endpoints.</summary>
    public static (LatLon Foot, double AlongNm, bool Clamped) FootOfPerpendicular(LatLon point, LatLon segA, LatLon segB)
    {
        var (footLat, footLon, alongNm, clamped) = FootOfPerpendicular(point.Lat, point.Lon, segA.Lat, segA.Lon, segB.Lat, segB.Lon);
        return (new LatLon(footLat, footLon), alongNm, clamped);
    }

    /// <summary>Parametric line-segment intersection. See the scalar form for contract details.</summary>
    public static (LatLon Point, double T, double U)? SegmentsIntersect(LatLon a1, LatLon a2, LatLon b1, LatLon b2, bool excludeEndpoints = false)
    {
        var result = SegmentsIntersect(a1.Lat, a1.Lon, a2.Lat, a2.Lon, b1.Lat, b1.Lon, b2.Lat, b2.Lon, excludeEndpoints);
        if (result is null)
        {
            return null;
        }
        var (lat, lon, t, u) = result.Value;
        return (new LatLon(lat, lon), t, u);
    }
}
