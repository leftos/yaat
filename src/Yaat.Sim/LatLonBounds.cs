namespace Yaat.Sim;

/// <summary>
/// Axis-aligned lat/lon bounding box of one or more polygon rings, used as a conservative O(1)
/// pre-filter. A point or segment that lies entirely outside the box cannot intersect the polygon,
/// so the expensive per-vertex walk is skipped. The box is a superset of the polygon, so it never
/// rejects a true hit — results are identical to testing every vertex. A polygon that straddles the
/// antimeridian gets a globe-spanning longitude span (never rejects laterally) and simply falls
/// through to the exact test; correctness is preserved, only the speedup is forfeited for it.
/// </summary>
internal readonly struct LatLonBounds(double minLat, double maxLat, double minLon, double maxLon)
{
    private readonly double _minLat = minLat;
    private readonly double _maxLat = maxLat;
    private readonly double _minLon = minLon;
    private readonly double _maxLon = maxLon;

    public static LatLonBounds Empty => new(double.PositiveInfinity, double.NegativeInfinity, double.PositiveInfinity, double.NegativeInfinity);

    public static LatLonBounds FromRings(IReadOnlyList<IReadOnlyList<LatLon>> rings)
    {
        double minLat = double.PositiveInfinity;
        double maxLat = double.NegativeInfinity;
        double minLon = double.PositiveInfinity;
        double maxLon = double.NegativeInfinity;

        foreach (var ring in rings)
        {
            foreach (var p in ring)
            {
                if (p.Lat < minLat)
                {
                    minLat = p.Lat;
                }
                if (p.Lat > maxLat)
                {
                    maxLat = p.Lat;
                }
                if (p.Lon < minLon)
                {
                    minLon = p.Lon;
                }
                if (p.Lon > maxLon)
                {
                    maxLon = p.Lon;
                }
            }
        }

        return new LatLonBounds(minLat, maxLat, minLon, maxLon);
    }

    public bool ExcludesPoint(LatLon p) => p.Lat < _minLat || p.Lat > _maxLat || p.Lon < _minLon || p.Lon > _maxLon;

    public bool ExcludesSegment(LatLon a, LatLon b) =>
        Math.Max(a.Lat, b.Lat) < _minLat || Math.Min(a.Lat, b.Lat) > _maxLat || Math.Max(a.Lon, b.Lon) < _minLon || Math.Min(a.Lon, b.Lon) > _maxLon;
}
