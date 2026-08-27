namespace Yaat.Sim.Data.Artcc;

/// <summary>
/// One ARTCC's lateral boundary as a set of polygon rings (lat/lon), with the bounding box
/// pre-computed so a point far from the center is rejected before any vertex is touched.
/// </summary>
public sealed class ArtccBoundary
{
    public required string Id { get; init; }

    private readonly IReadOnlyList<IReadOnlyList<LatLon>> _rings = [];
    private double _minLat = double.PositiveInfinity;
    private double _maxLat = double.NegativeInfinity;
    private double _minLon = double.PositiveInfinity;
    private double _maxLon = double.NegativeInfinity;

    public required IReadOnlyList<IReadOnlyList<LatLon>> Rings
    {
        get => _rings;
        init
        {
            _rings = value;
            foreach (var ring in value)
            {
                foreach (var p in ring)
                {
                    _minLat = Math.Min(_minLat, p.Lat);
                    _maxLat = Math.Max(_maxLat, p.Lat);
                    _minLon = Math.Min(_minLon, p.Lon);
                    _maxLon = Math.Max(_maxLon, p.Lon);
                }
            }
        }
    }

    public double MinLat => _minLat;
    public double MaxLat => _maxLat;
    public double MinLon => _minLon;
    public double MaxLon => _maxLon;

    public bool Contains(LatLon position)
    {
        if ((position.Lat < _minLat) || (position.Lat > _maxLat) || (position.Lon < _minLon) || (position.Lon > _maxLon))
        {
            return false;
        }

        foreach (var ring in _rings)
        {
            if (GeoMath.PointInRing(position, ring))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Great-circle distance (nm) from the position to the nearest boundary edge; 0 inside.</summary>
    public double DistanceToEdgeNm(LatLon position)
    {
        if (Contains(position))
        {
            return 0;
        }

        double bestFt = double.PositiveInfinity;
        foreach (var ring in _rings)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                bestFt = Math.Min(bestFt, GeoMath.DistanceToSegmentFt(position, a, b));
            }
        }

        return bestFt / GeoMath.FeetPerNm;
    }
}
