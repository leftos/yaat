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
        var key = BucketKey(lat, lon);
        if (!_grid.TryGetValue(key, out var list))
        {
            list = [];
            _grid[key] = list;
        }

        list.Add((lat, lon, nodeId));
    }

    public int? FindNearest(double lat, double lon)
    {
        var key = BucketKey(lat, lon);

        // Check this bucket and neighbors
        for (int dlat = -1; dlat <= 1; dlat++)
        {
            for (int dlon = -1; dlon <= 1; dlon++)
            {
                var neighborKey = (key.LatBucket + dlat, key.LonBucket + dlon);
                if (!_grid.TryGetValue(neighborKey, out var list))
                {
                    continue;
                }

                foreach (var (nLat, nLon, nodeId) in list)
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

    private (int LatBucket, int LonBucket) BucketKey(double lat, double lon)
    {
        return ((int)Math.Floor(lat / _tolerance), (int)Math.Floor(lon / _tolerance));
    }
}
