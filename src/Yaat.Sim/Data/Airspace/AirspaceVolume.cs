namespace Yaat.Sim.Data.Airspace;

public sealed class AirspaceVolume
{
    public required string Id { get; init; }
    public required string Ident { get; init; }
    public required string IcaoId { get; init; }
    public required string Name { get; init; }
    public required AirspaceClass Class { get; init; }
    public required int LowerFtMsl { get; init; }
    public required int UpperFtMsl { get; init; }

    private readonly IReadOnlyList<IReadOnlyList<AirspacePoint>> _rings = [];
    private LatLonBounds _bounds = LatLonBounds.Empty;

    /// <summary>
    /// Polygon rings (lat/lon). Assigning them eagerly computes the lateral bounding box once, so the
    /// per-point geometry primitives below can reject a point/segment far outside this volume in O(1)
    /// before walking every ring vertex. The national Class B/C set has ~500 volumes; an aircraft is only
    /// ever near one, so this bbox pre-filter turns a per-tick all-volumes point-in-polygon sweep
    /// (hundreds of thousands of vertex ops) into a handful of comparisons.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<AirspacePoint>> Rings
    {
        get => _rings;
        init
        {
            _rings = value;
            _bounds = LatLonBounds.FromRings(value);
        }
    }

    public bool Contains(LatLon position, double altitudeFtMsl)
    {
        if (!ContainsAltitude(altitudeFtMsl))
        {
            return false;
        }

        return ContainsLateral(position);
    }

    public bool ContainsLateral(LatLon position)
    {
        if (_bounds.ExcludesPoint(position))
        {
            return false;
        }

        foreach (var ring in Rings)
        {
            if (PointInRing(position, ring))
            {
                return true;
            }
        }

        return false;
    }

    public bool ContainsAltitude(double altitudeFtMsl) => altitudeFtMsl >= LowerFtMsl && altitudeFtMsl <= UpperFtMsl;

    internal bool Crosses(LatLon from, LatLon to, double altitudeFtMsl, out LatLon intersection)
    {
        intersection = default;
        if (!ContainsAltitude(altitudeFtMsl))
        {
            return false;
        }

        foreach (var hit in FindLateralIntersections(from, to))
        {
            intersection = hit;
            return true;
        }

        return false;
    }

    internal IEnumerable<LatLon> FindLateralIntersections(LatLon from, LatLon to)
    {
        if (_bounds.ExcludesSegment(from, to))
        {
            yield break;
        }

        foreach (var ring in Rings)
        {
            if (ring.Count < 2)
            {
                continue;
            }

            for (int i = 0; i < ring.Count - 1; i++)
            {
                var a = ring[i].ToLatLon();
                var b = ring[i + 1].ToLatLon();
                var hit = GeoMath.SegmentsIntersect(from, to, a, b, excludeEndpoints: false);
                if (hit is not null)
                {
                    yield return hit.Value.Point;
                }
            }
        }
    }

    private static bool PointInRing(LatLon point, IReadOnlyList<AirspacePoint> ring)
    {
        if (ring.Count < 3)
        {
            return false;
        }

        bool inside = false;
        int j = ring.Count - 1;
        for (int i = 0; i < ring.Count; i++)
        {
            double yi = ring[i].Lat;
            double yj = ring[j].Lat;
            double xi = ring[i].Lon;
            double xj = ring[j].Lon;

            bool intersects = ((yi > point.Lat) != (yj > point.Lat)) && (point.Lon < (xj - xi) * (point.Lat - yi) / (yj - yi) + xi);
            if (intersects)
            {
                inside = !inside;
            }

            j = i;
        }

        return inside;
    }

    /// <summary>
    /// Axis-aligned lat/lon bounding box of a volume's rings, used as a conservative O(1) pre-filter.
    /// A point or segment that lies entirely outside the box cannot intersect the polygon, so the
    /// expensive vertex walk is skipped. The box is a superset of the polygon, so it never rejects a
    /// true hit — results are identical to testing every vertex. A volume that straddles the
    /// antimeridian gets a globe-spanning longitude span (never rejects laterally) and simply falls
    /// through to the exact test; correctness is preserved, only the speedup is forfeited for it.
    /// </summary>
    private readonly struct LatLonBounds(double minLat, double maxLat, double minLon, double maxLon)
    {
        private readonly double _minLat = minLat;
        private readonly double _maxLat = maxLat;
        private readonly double _minLon = minLon;
        private readonly double _maxLon = maxLon;

        public static LatLonBounds Empty => new(double.PositiveInfinity, double.NegativeInfinity, double.PositiveInfinity, double.NegativeInfinity);

        public static LatLonBounds FromRings(IReadOnlyList<IReadOnlyList<AirspacePoint>> rings)
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
            Math.Max(a.Lat, b.Lat) < _minLat
            || Math.Min(a.Lat, b.Lat) > _maxLat
            || Math.Max(a.Lon, b.Lon) < _minLon
            || Math.Min(a.Lon, b.Lon) > _maxLon;
    }
}
