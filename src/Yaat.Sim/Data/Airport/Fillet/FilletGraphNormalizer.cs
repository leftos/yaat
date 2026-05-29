namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>
/// Post-execute distance/radius recompute and structural cleanup. Drops self-loop/degenerate arcs
/// and edges, then sweeps isolated intersection nodes. There is no coincident-node merge: the plan
/// guarantees no coincident tangent cuts (<c>SharedArmTangentPass.ApplyGlobalCoincidentCutCoalesce</c>)
/// and the runway-crossing projector reuses pre-existing nodes rather than minting coincident ones.
/// </summary>
public static class FilletGraphNormalizer
{
    public static int Normalize(AirportGroundLayout layout)
    {
        RecomputeDistances(layout);
        layout.RebuildAdjacencyLists();
        int changed = RemoveDegenerateArcsAndEdges(layout);
        layout.RebuildAdjacencyLists();
        changed += RemoveIsolatedIntersectionNodes(layout);
        return changed;
    }

    private static int RemoveIsolatedIntersectionNodes(AirportGroundLayout layout)
    {
        var toRemove = layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.TaxiwayIntersection) && (n.Edges.Count == 0))
            .Select(n => n.Id)
            .ToList();
        foreach (int id in toRemove)
        {
            layout.Nodes.Remove(id);
        }

        return toRemove.Count;
    }

    private static void RecomputeDistances(AirportGroundLayout layout)
    {
        foreach (var edge in layout.Edges)
        {
            edge.DistanceNm = GeoMath.DistanceNm(edge.Nodes[0].Position, edge.Nodes[1].Position);
        }

        foreach (var arc in layout.Arcs)
        {
            var bezier = new CubicBezier(
                arc.Nodes[0].Position.Lat,
                arc.Nodes[0].Position.Lon,
                arc.P1Lat,
                arc.P1Lon,
                arc.P2Lat,
                arc.P2Lon,
                arc.Nodes[1].Position.Lat,
                arc.Nodes[1].Position.Lon
            );
            arc.MinRadiusOfCurvatureFt = bezier.MinRadiusOfCurvatureFt(arc.Nodes[0].Position.Lat, 10);
            arc.DistanceNm = bezier.ArcLengthNm(20);
        }
    }

    private static int RemoveDegenerateArcsAndEdges(AirportGroundLayout layout)
    {
        int before = layout.Arcs.Count + layout.Edges.Count;
        layout.Arcs.RemoveAll(a => (a.Nodes[0].Id == a.Nodes[1].Id) || (a.MinRadiusOfCurvatureFt < FilletConstants.RadiusFloorFt));
        layout.Edges.RemoveAll(e => e.Nodes[0].Id == e.Nodes[1].Id);
        return before - (layout.Arcs.Count + layout.Edges.Count);
    }
}
