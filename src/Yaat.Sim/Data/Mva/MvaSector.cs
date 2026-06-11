namespace Yaat.Sim.Data.Mva;

/// <summary>
/// One Minimum Vectoring Altitude sector: a polygon plus the single MSL floor altitude charted for
/// it. Sourced from FAA AJV-A AIXM 5.1 MVA charts (built by tools/build-mva-data.py).
///
/// Unlike <see cref="Airspace.AirspaceVolume"/> — whose rings union as disjoint shelves — an MVA
/// sector's rings are exterior-minus-holes: ring 0 is the boundary, rings 1+ are interior holes cut
/// out around higher-floor islands. A point inside a hole belongs to a different, separately charted
/// sector, not this one.
/// </summary>
public sealed class MvaSector
{
    public required string Sector { get; init; }
    public required int FloorFtMsl { get; init; }
    public required string Facility { get; init; }

    private readonly IReadOnlyList<IReadOnlyList<LatLon>> _rings = [];
    private LatLonBounds _bounds = LatLonBounds.Empty;

    /// <summary>
    /// Polygon rings; ring 0 is the exterior boundary, rings 1+ are interior holes. Assigning them
    /// eagerly computes the lateral bounding box once so <see cref="Contains"/> can reject a point far
    /// outside this sector in O(1) before walking the boundary.
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

    /// <summary>True when the point lies inside the exterior ring and outside every interior hole.</summary>
    public bool Contains(LatLon position)
    {
        if (_bounds.ExcludesPoint(position) || _rings.Count == 0)
        {
            return false;
        }

        if (!GeoMath.PointInRing(position, _rings[0]))
        {
            return false;
        }

        for (int i = 1; i < _rings.Count; i++)
        {
            if (GeoMath.PointInRing(position, _rings[i]))
            {
                return false;
            }
        }

        return true;
    }
}
