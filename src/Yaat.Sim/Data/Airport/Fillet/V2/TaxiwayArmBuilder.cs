namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal static class TaxiwayArmBuilder
{
    public static IReadOnlyList<TaxiwayArm> BuildArms(GroundNode intersection, HashSet<int> manualArcNodes)
    {
        var edges = CollectEdges(intersection);
        if (edges.Count < 2)
        {
            return [];
        }

        var arms = new List<TaxiwayArm>();
        int armId = 0;
        foreach (var edge in edges)
        {
            var other = edge.OtherNode(intersection);
            double bearing = FilletGeometry.InitialBearing(intersection, other, edge);
            var walk = TaxiwayWalk.Walk(edge, intersection, manualArcNodes);
            double capFt = TaxiwayWalk.DistToFirstIntersectionFt(walk);
            bool capTerminal = FilletEligibility.IsEligible(walk.TerminalNode) && (walk.TerminalNode.SourceIntersectionPosition is null);
            double intersectionCapFt = capTerminal ? Math.Min(walk.AvailableLengthFt / 2.0, capFt) : Math.Min(walk.AvailableLengthFt, capFt);
            if (intersectionCapFt > FilletConstants.MaxTangentDistFt)
            {
                intersectionCapFt = FilletConstants.MaxTangentDistFt;
            }

            var terminus = TaxiwayWalk.ClassifyTerminus(walk.TerminalNode, walk, edge.IsRunwayCenterline);
            arms.Add(
                new TaxiwayArm(
                    Id: armId++,
                    JunctionNodeId: intersection.Id,
                    RootEdge: edge,
                    TaxiwayName: edge.TaxiwayName,
                    BearingFromJunctionDeg: bearing,
                    LengthFt: walk.AvailableLengthFt,
                    IntersectionCapFt: intersectionCapFt,
                    Terminus: terminus,
                    TerminalNode: walk.TerminalNode,
                    IsRunwayCenterline: edge.IsRunwayCenterline,
                    Walk: walk
                )
            );
        }

        return arms;
    }

    private static List<GroundEdge> CollectEdges(GroundNode intersection)
    {
        var edges = new List<GroundEdge>();
        var seen = new HashSet<(int OtherNodeId, string TaxiwayName)>();
        foreach (var e in intersection.Edges)
        {
            // Runway-crossing connectors are not taxi corners — fillet never curves onto them.
            if (e is GroundEdge ge && !ge.IsRunwayCrossingLink)
            {
                var other = ge.OtherNode(intersection);
                if (seen.Add((other.Id, ge.TaxiwayName)))
                {
                    edges.Add(ge);
                }
            }
        }

        return edges;
    }
}
