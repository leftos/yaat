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

    private readonly IReadOnlyList<IReadOnlyList<LatLon>> _rings = [];
    private LatLonBounds _bounds = LatLonBounds.Empty;

    /// <summary>
    /// Polygon rings (lat/lon). Assigning them eagerly computes the lateral bounding box once, so the
    /// per-point geometry primitives below can reject a point/segment far outside this volume in O(1)
    /// before walking every ring vertex. The national Class B/C set has ~500 volumes; an aircraft is only
    /// ever near one, so this bbox pre-filter turns a per-tick all-volumes point-in-polygon sweep
    /// (hundreds of thousands of vertex ops) into a handful of comparisons.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<LatLon>> Rings
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
            if (GeoMath.PointInRing(position, ring))
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
                var a = ring[i];
                var b = ring[i + 1];
                var hit = GeoMath.SegmentsIntersect(from, to, a, b, excludeEndpoints: false);
                if (hit is not null)
                {
                    yield return hit.Value.Point;
                }
            }
        }
    }
}
