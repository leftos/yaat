namespace Yaat.Sim.Data.Airport;

/// <summary>
/// A cubic bezier curve defined by four control points in lat/lon coordinates.
/// P0 and P3 are the endpoints; P1 and P2 are the control points that determine curvature.
/// All evaluation works in raw lat/lon (negligible distortion at airport scale).
/// Curvature and bearing computations convert to local feet for accuracy.
/// </summary>
public readonly struct CubicBezier(double p0Lat, double p0Lon, double p1Lat, double p1Lon, double p2Lat, double p2Lon, double p3Lat, double p3Lon)
{
    private const double NmPerDegLat = 60.0;
    public double P0Lat { get; } = p0Lat;
    public double P0Lon { get; } = p0Lon;
    public double P1Lat { get; } = p1Lat;
    public double P1Lon { get; } = p1Lon;
    public double P2Lat { get; } = p2Lat;
    public double P2Lon { get; } = p2Lon;
    public double P3Lat { get; } = p3Lat;
    public double P3Lon { get; } = p3Lon;

    /// <summary>
    /// Evaluate the bezier curve position at parameter t ∈ [0,1].
    /// B(t) = (1-t)³P0 + 3(1-t)²tP1 + 3(1-t)t²P2 + t³P3
    /// </summary>
    public (double Lat, double Lon) Evaluate(double t)
    {
        double u = 1.0 - t;
        double u2 = u * u;
        double u3 = u2 * u;
        double t2 = t * t;
        double t3 = t2 * t;

        double lat = (u3 * P0Lat) + (3.0 * u2 * t * P1Lat) + (3.0 * u * t2 * P2Lat) + (t3 * P3Lat);
        double lon = (u3 * P0Lon) + (3.0 * u2 * t * P1Lon) + (3.0 * u * t2 * P2Lon) + (t3 * P3Lon);
        return (lat, lon);
    }

    /// <summary>
    /// First derivative B'(t) = 3(1-t)²(P1-P0) + 6(1-t)t(P2-P1) + 3t²(P3-P2).
    /// Returns the derivative in lat/lon space.
    /// </summary>
    public (double DLat, double DLon) Derivative(double t)
    {
        double u = 1.0 - t;
        double u2 = u * u;
        double t2 = t * t;

        double dLat = (3.0 * u2 * (P1Lat - P0Lat)) + (6.0 * u * t * (P2Lat - P1Lat)) + (3.0 * t2 * (P3Lat - P2Lat));
        double dLon = (3.0 * u2 * (P1Lon - P0Lon)) + (6.0 * u * t * (P2Lon - P1Lon)) + (3.0 * t2 * (P3Lon - P2Lon));
        return (dLat, dLon);
    }

    /// <summary>
    /// Second derivative B''(t) = 6(1-t)(P2-2P1+P0) + 6t(P3-2P2+P1).
    /// </summary>
    public (double DDLat, double DDLon) SecondDerivative(double t)
    {
        double u = 1.0 - t;

        double a0Lat = P2Lat - (2.0 * P1Lat) + P0Lat;
        double a0Lon = P2Lon - (2.0 * P1Lon) + P0Lon;
        double a1Lat = P3Lat - (2.0 * P2Lat) + P1Lat;
        double a1Lon = P3Lon - (2.0 * P2Lon) + P1Lon;

        double ddLat = (6.0 * u * a0Lat) + (6.0 * t * a1Lat);
        double ddLon = (6.0 * u * a0Lon) + (6.0 * t * a1Lon);
        return (ddLat, ddLon);
    }

    /// <summary>
    /// Bearing (degrees true, 0-360) of the tangent at parameter t.
    /// Converts derivative to local XY (feet) for correct bearing at the given latitude.
    /// </summary>
    public double TangentBearing(double t)
    {
        var (dLat, dLon) = Derivative(t);
        var (lat, _) = Evaluate(t);

        // Convert to local feet: dY = dLat in north direction, dX = dLon in east direction
        double dyFt = dLat * NmPerDegLat * GeoMath.FeetPerNm;
        double dxFt = dLon * Math.Cos(lat * (Math.PI / 180.0)) * NmPerDegLat * GeoMath.FeetPerNm;

        double bearingRad = Math.Atan2(dxFt, dyFt); // atan2(east, north) = bearing from north
        double bearingDeg = bearingRad * (180.0 / Math.PI);
        if (bearingDeg < 0)
        {
            bearingDeg += 360.0;
        }

        return bearingDeg;
    }

    /// <summary>
    /// Radius of curvature at parameter t, in feet.
    /// κ = |x'y'' - y'x''| / (x'² + y'²)^(3/2), R = 1/κ.
    /// Converts to local XY feet for correct units.
    /// </summary>
    public double RadiusOfCurvatureFt(double t, double refLat)
    {
        var (dLat, dLon) = Derivative(t);
        var (ddLat, ddLon) = SecondDerivative(t);

        double cosLat = Math.Cos(refLat * (Math.PI / 180.0));
        double scale = NmPerDegLat * GeoMath.FeetPerNm;

        // Convert to feet
        double dx = dLon * cosLat * scale;
        double dy = dLat * scale;
        double ddx = ddLon * cosLat * scale;
        double ddy = ddLat * scale;

        double cross = Math.Abs((dx * ddy) - (dy * ddx));
        double speed = Math.Sqrt((dx * dx) + (dy * dy));
        double speed3 = speed * speed * speed;

        if (cross < 1e-12)
        {
            return double.MaxValue; // Straight section — infinite radius
        }

        return speed3 / cross;
    }

    /// <summary>
    /// Minimum radius of curvature across the curve, sampled at evenly-spaced points.
    /// Used for worst-case speed constraint computation.
    /// </summary>
    public double MinRadiusOfCurvatureFt(double refLat, int samples)
    {
        double minRadius = double.MaxValue;
        for (int i = 0; i <= samples; i++)
        {
            double t = (double)i / samples;
            double r = RadiusOfCurvatureFt(t, refLat);
            if (r < minRadius)
            {
                minRadius = r;
            }
        }

        return minRadius;
    }

    /// <summary>
    /// Arc length via polyline approximation (sum of great-circle segment distances).
    /// </summary>
    public double ArcLengthNm(int segments)
    {
        double totalNm = 0;
        var (prevLat, prevLon) = Evaluate(0);

        for (int i = 1; i <= segments; i++)
        {
            double t = (double)i / segments;
            var (lat, lon) = Evaluate(t);
            totalNm += GeoMath.DistanceNm(prevLat, prevLon, lat, lon);
            prevLat = lat;
            prevLon = lon;
        }

        return totalNm;
    }

    /// <summary>
    /// Find the parameter t ∈ [0,1] where the curve is closest to the given point.
    /// Uses coarse scan followed by iterative refinement.
    /// </summary>
    public double ClosestT(double lat, double lon, int iterations)
    {
        // Phase 1: Coarse scan — find the best t among evenly-spaced samples
        const int coarseSamples = 20;
        double bestT = 0;
        double bestDist = double.MaxValue;

        for (int i = 0; i <= coarseSamples; i++)
        {
            double t = (double)i / coarseSamples;
            var (pLat, pLon) = Evaluate(t);
            double dLat = pLat - lat;
            double dLon = pLon - lon;
            double dist = (dLat * dLat) + (dLon * dLon); // squared distance (monotonic)
            if (dist < bestDist)
            {
                bestDist = dist;
                bestT = t;
            }
        }

        // Phase 2: Binary search refinement around the best coarse sample
        double lo = Math.Max(0, bestT - (1.0 / coarseSamples));
        double hi = Math.Min(1, bestT + (1.0 / coarseSamples));

        for (int i = 0; i < iterations; i++)
        {
            double mid1 = lo + ((hi - lo) / 3.0);
            double mid2 = hi - ((hi - lo) / 3.0);

            var (p1Lat, p1Lon) = Evaluate(mid1);
            double d1 = Sq(p1Lat - lat) + Sq(p1Lon - lon);

            var (p2Lat, p2Lon) = Evaluate(mid2);
            double d2 = Sq(p2Lat - lat) + Sq(p2Lon - lon);

            if (d1 < d2)
            {
                hi = mid2;
            }
            else
            {
                lo = mid1;
            }
        }

        return (lo + hi) / 2.0;
    }

    private static double Sq(double x) => x * x;
}
