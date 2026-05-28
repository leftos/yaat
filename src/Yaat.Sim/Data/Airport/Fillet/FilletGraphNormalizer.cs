namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>Post-execute distance/adjacency recompute and structural validation (no repair passes).</summary>
public static class FilletGraphNormalizer
{
    public static int Normalize(AirportGroundLayout layout)
    {
        RecomputeDistances(layout);
        layout.RebuildAdjacencyLists();
        return MergeCoincidentNodesDefensive(layout);
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

    private static int MergeCoincidentNodesDefensive(AirportGroundLayout layout)
    {
        int merged = 0;
        bool changed;
        do
        {
            changed = false;
            var mergeMap = BuildMergeMap(layout, FilletConstants.CoincidentNodeThresholdNm);
            if (mergeMap.Count == 0)
            {
                break;
            }

            foreach (var (survivorId, duplicateId) in mergeMap)
            {
                if (!layout.Nodes.ContainsKey(survivorId) || !layout.Nodes.ContainsKey(duplicateId))
                {
                    continue;
                }

                var survivor = layout.Nodes[survivorId];
                var duplicate = layout.Nodes[duplicateId];

                foreach (var edge in layout.Edges.ToList())
                {
                    if (edge.Nodes[0].Id == duplicateId)
                    {
                        edge.Nodes[0] = survivor;
                    }

                    if (edge.Nodes[1].Id == duplicateId)
                    {
                        edge.Nodes[1] = survivor;
                    }
                }

                foreach (var arc in layout.Arcs)
                {
                    if (arc.Nodes[0].Id == duplicateId)
                    {
                        arc.Nodes[0] = survivor;
                        RebuildArcControlPoints(arc);
                    }

                    if (arc.Nodes[1].Id == duplicateId)
                    {
                        arc.Nodes[1] = survivor;
                        RebuildArcControlPoints(arc);
                    }
                }

                layout.Nodes.Remove(duplicateId);
                merged++;
                changed = true;
            }

            layout.RebuildAdjacencyLists();
        } while (changed);

        layout.Arcs.RemoveAll(a => (a.Nodes[0].Id == a.Nodes[1].Id) || (a.MinRadiusOfCurvatureFt < FilletConstants.RadiusFloorFt));
        return merged;
    }

    private static void RebuildArcControlPoints(GroundArc arc)
    {
        if ((arc.EdgeBearingAtNode0Deg == 0) && (arc.EdgeBearingAtNode1Deg == 0))
        {
            return;
        }

        double bearing0To1 = arc.EdgeBearingAtNode0Deg;
        double bearing1To0 = arc.EdgeBearingAtNode1Deg;
        double effectiveTurnDeg = 180.0 - GeoMath.AbsBearingDifference(bearing0To1, bearing1To0);
        double sweepRad = effectiveTurnDeg * (Math.PI / 180.0);
        double kappa = (4.0 / 3.0) * Math.Tan(sweepRad / 4.0);
        double depthNm = kappa * (arc.MinRadiusOfCurvatureFt / GeoMath.FeetPerNm);

        var (p1Lat, p1Lon) = GeoMath.ProjectPointRaw(arc.Nodes[0].Position.Lat, arc.Nodes[0].Position.Lon, bearing0To1, depthNm);
        var (p2Lat, p2Lon) = GeoMath.ProjectPointRaw(arc.Nodes[1].Position.Lat, arc.Nodes[1].Position.Lon, bearing1To0, depthNm);
        arc.P1Lat = p1Lat;
        arc.P1Lon = p1Lon;
        arc.P2Lat = p2Lat;
        arc.P2Lon = p2Lon;
    }

    private static Dictionary<int, int> BuildMergeMap(AirportGroundLayout layout, double thresholdNm)
    {
        var map = new Dictionary<int, int>();
        var nodes = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.TaxiwayIntersection).ToList();
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                double d = GeoMath.DistanceNm(nodes[i].Position, nodes[j].Position);
                if (d <= thresholdNm)
                {
                    int survivor = Math.Min(nodes[i].Id, nodes[j].Id);
                    int duplicate = Math.Max(nodes[i].Id, nodes[j].Id);
                    map[duplicate] = survivor;
                }
            }
        }

        return map;
    }
}
