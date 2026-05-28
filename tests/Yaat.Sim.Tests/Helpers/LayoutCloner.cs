using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Deep-clones graph topology for fillet comparison: nodes, edges, arcs, and runway
/// centerline geometry. Each <see cref="IFilletArcGenerator"/> runs on an independent graph
/// (fillet mutates nodes, edges, and arcs in place). Runway metadata used only by landing/
/// pattern logic (<see cref="GroundRunway.TurnoffByEnd"/>, pattern fields) is not copied.
/// </summary>
public static class LayoutCloner
{
    public static AirportGroundLayout DeepClone(AirportGroundLayout source)
    {
        var clone = new AirportGroundLayout { AirportId = source.AirportId };
        var nodesById = new Dictionary<int, GroundNode>(source.Nodes.Count);

        foreach (var node in source.Nodes.Values)
        {
            var copy = new GroundNode
            {
                Id = node.Id,
                Position = node.Position,
                Type = node.Type,
                Name = node.Name,
                TrueHeading = node.TrueHeading,
                RunwayId = node.RunwayId,
                Origin = node.Origin,
                FilletProvenance = node.FilletProvenance,
                SourceIntersectionPosition = node.SourceIntersectionPosition,
            };
            nodesById[node.Id] = copy;
            clone.Nodes[node.Id] = copy;
        }

        foreach (var edge in source.Edges)
        {
            clone.Edges.Add(CloneEdge(edge, nodesById));
        }

        foreach (var arc in source.Arcs)
        {
            clone.Arcs.Add(CloneArc(arc, nodesById));
        }

        foreach (var runway in source.Runways)
        {
            clone.Runways.Add(
                new GroundRunway
                {
                    Name = runway.Name,
                    Coordinates = runway.Coordinates.ToList(),
                    WidthFt = runway.WidthFt,
                }
            );
        }

        clone.RebuildAdjacencyLists();
        return clone;
    }

    private static GroundEdge CloneEdge(GroundEdge edge, Dictionary<int, GroundNode> nodesById)
    {
        return new GroundEdge
        {
            Nodes = [nodesById[edge.Nodes[0].Id], nodesById[edge.Nodes[1].Id]],
            TaxiwayName = edge.TaxiwayName,
            DistanceNm = edge.DistanceNm,
            Origin = edge.Origin,
            FilletProvenance = edge.FilletProvenance,
            IntermediatePoints = edge.IntermediatePoints.ToList(),
        };
    }

    private static GroundArc CloneArc(GroundArc arc, Dictionary<int, GroundNode> nodesById)
    {
        return new GroundArc
        {
            Nodes = [nodesById[arc.Nodes[0].Id], nodesById[arc.Nodes[1].Id]],
            P1Lat = arc.P1Lat,
            P1Lon = arc.P1Lon,
            P2Lat = arc.P2Lat,
            P2Lon = arc.P2Lon,
            MinRadiusOfCurvatureFt = arc.MinRadiusOfCurvatureFt,
            EdgeBearingAtNode0Deg = arc.EdgeBearingAtNode0Deg,
            EdgeBearingAtNode1Deg = arc.EdgeBearingAtNode1Deg,
            TurnAngleDeg = arc.TurnAngleDeg,
            DistanceNm = arc.DistanceNm,
            TaxiwayNames = arc.TaxiwayNames.ToArray(),
            Origin = arc.Origin,
            FilletProvenance = arc.FilletProvenance,
        };
    }
}
