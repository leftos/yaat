namespace Yaat.Sim.Data.Airport;

/// <summary>The shape of a ground-graph edge, read in a direction.</summary>
internal static class EdgeGeometry
{
    /// <summary>
    /// An edge's centreline as points from <paramref name="from"/> to its other end: the two nodes with the edge's
    /// intermediate points between them, in that order.
    /// </summary>
    /// <param name="edge">The edge.</param>
    /// <param name="from">The end node the points start at.</param>
    /// <returns>The centreline's points.</returns>
    internal static List<LatLon> PointsFrom(GroundEdge edge, GroundNode from)
    {
        var points = new List<LatLon> { edge.Nodes[0].Position };
        points.AddRange(edge.IntermediatePoints.Select(q => new LatLon(q.Lat, q.Lon)));
        points.Add(edge.Nodes[1].Position);
        if (edge.Nodes[0].Id != from.Id)
        {
            points.Reverse();
        }

        return points;
    }
}
