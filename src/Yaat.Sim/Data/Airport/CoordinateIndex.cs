namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Simple spatial index for fast coordinate snapping within a tolerance.
/// Uses a grid-based bucketing approach.
/// </summary>
internal sealed class CoordinateIndex
{
    private readonly double _tolerance;
    private readonly Dictionary<(int LatBucket, int LonBucket), List<(double Lat, double Lon, int NodeId)>> _grid = [];

    public CoordinateIndex(double tolerance)
    {
        _tolerance = tolerance;
    }

    public void Add(double lat, double lon, int nodeId)
    {
        (int LatBucket, int LonBucket) key = BucketKey(lat, lon);
        if (!_grid.TryGetValue(key, out List<(double Lat, double Lon, int NodeId)>? list))
        {
            list = [];
            _grid[key] = list;
        }

        list.Add((lat, lon, nodeId));
    }

    public int? FindNearest(double lat, double lon)
    {
        (int LatBucket, int LonBucket) key = BucketKey(lat, lon);

        // Check this bucket and neighbors
        for (int dlat = -1; dlat <= 1; dlat++)
        {
            for (int dlon = -1; dlon <= 1; dlon++)
            {
                (int, int) neighborKey = (key.LatBucket + dlat, key.LonBucket + dlon);
                if (!_grid.TryGetValue(neighborKey, out List<(double Lat, double Lon, int NodeId)>? list))
                {
                    continue;
                }

                foreach ((double nLat, double nLon, int nodeId) in list)
                {
                    if (Math.Abs(lat - nLat) <= _tolerance && Math.Abs(lon - nLon) <= _tolerance)
                    {
                        return nodeId;
                    }
                }
            }
        }

        return null;
    }

    private (int LatBucket, int LonBucket) BucketKey(double lat, double lon) =>
        ((int)Math.Floor(lat / _tolerance), (int)Math.Floor(lon / _tolerance));

    public void Add(LatLon position, int nodeId) => Add(position.Lat, position.Lon, nodeId);

    public int? FindNearest(LatLon position) => FindNearest(position.Lat, position.Lon);
}
