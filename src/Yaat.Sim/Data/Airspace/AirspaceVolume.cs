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
    public required IReadOnlyList<IReadOnlyList<AirspacePoint>> Rings { get; init; }

    public bool Contains(LatLon position, double altitudeFtMsl)
    {
        if (!ContainsAltitude(altitudeFtMsl))
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
                    intersection = hit.Value.Point;
                    return true;
                }
            }
        }

        return false;
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
}
