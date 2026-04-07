using System.Text;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

public class FilletArcDumpTests
{
    private readonly ITestOutputHelper _output;

    public FilletArcDumpTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void OAK_FilletTrace_DumpToFile()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        if (!File.Exists(path))
        {
            return;
        }

        // GeoJsonParser now auto-applies fillets
        var layout = GeoJsonParser.Parse("OAK", File.ReadAllText(path), null);

        string outPath = Path.Combine(".tmp", "fillet-trace.txt");
        Directory.CreateDirectory(".tmp");

        using var writer = new StreamWriter(outPath);
        writer.WriteLine("=== OAK Fillet Arc Dump (auto-filleted) ===");
        writer.WriteLine();

        // Dump filleted state
        writer.WriteLine("--- FILLETED STATE ---");
        DumpGraph(writer, layout);

        // Count components
        var remaining = new HashSet<int>(layout.Nodes.Keys);
        int componentCount = 0;
        int largestComponent = 0;
        while (remaining.Count > 0)
        {
            componentCount++;
            var seed = remaining.First();
            var q = new Queue<int>();
            q.Enqueue(seed);
            remaining.Remove(seed);
            int size = 1;
            while (q.Count > 0)
            {
                int id = q.Dequeue();
                if (!layout.Nodes.TryGetValue(id, out var n))
                {
                    continue;
                }

                foreach (var e in n.Edges)
                {
                    int oid = e.OtherNodeId(id);
                    if (remaining.Remove(oid))
                    {
                        q.Enqueue(oid);
                        size++;
                    }
                }
            }

            if (size > largestComponent)
            {
                largestComponent = size;
            }
        }

        writer.WriteLine();
        writer.WriteLine($"Components: {componentCount}, largest: {largestComponent}");
        writer.Flush();

        _output.WriteLine($"Trace written to {Path.GetFullPath(outPath)}");
        _output.WriteLine($"Components: {componentCount}, largest: {largestComponent}");
    }

    private static void DumpGraph(StreamWriter w, AirportGroundLayout layout)
    {
        w.WriteLine($"Nodes: {layout.Nodes.Count}");
        w.WriteLine($"Edges: {layout.Edges.Count}");
        w.WriteLine($"Arcs: {layout.Arcs.Count}");

        // Just count by type, don't list all nodes
        var typeCounts = layout.Nodes.Values.GroupBy(n => n.Type).OrderBy(g => g.Key);
        foreach (var g in typeCounts)
        {
            w.WriteLine($"  {g.Key}: {g.Count()}");
        }

        int zeroEdge = layout.Nodes.Values.Count(n => n.Edges.Count == 0);
        w.WriteLine($"Nodes with 0 edges: {zeroEdge}");
    }
}

/// <summary>
/// A traced version of FilletArcGenerator that writes every operation to a StreamWriter.
/// </summary>
internal static class FilletArcGeneratorTraced
{
    private const double MinFilletAngleDeg = 15.0;
    private const double CollinearThresholdDeg = 15.0;
    private const double DefaultRadiusFt = 75.0;
    private const double HighSpeedExitRadiusFt = 150.0;
    private const double RunwayExitRadiusFt = 100.0;
    private const double RampRadiusFt = 50.0;

    public static void Apply(AirportGroundLayout layout, StreamWriter w)
    {
        int nextNodeId = layout.Nodes.Keys.DefaultIfEmpty(0).Max() + 1;

        var intersections = layout.Nodes.Values.Where(IsEligible).ToList();

        w.WriteLine($"\nEligible intersections: {intersections.Count}");

        int processed = 0;
        foreach (var node in intersections)
        {
            if (!layout.Nodes.ContainsKey(node.Id))
            {
                w.WriteLine($"\n[SKIP] Node {node.Id} already removed");
                continue;
            }

            layout.RebuildAdjacencyLists();

            if (node.Edges.Count < 2)
            {
                w.WriteLine($"\n[SKIP] Node {node.Id} has {node.Edges.Count} edges after rebuild");
                continue;
            }

            processed++;
            w.WriteLine($"\n===== FILLET NODE {node.Id} (edges: {node.Edges.Count}) =====");

            // List all edges at this node
            foreach (var e in node.Edges)
            {
                string type = e is GroundArc ? "ARC" : "EDGE";
                w.WriteLine($"  {type} {e.TaxiwayName}: {e.Nodes[0].Id}--{e.Nodes[1].Id} (dist={e.DistanceNm:F4}nm)");
            }

            int edgesBefore = layout.Edges.Count;
            int arcsBefore = layout.Arcs.Count;
            int nodesBefore = layout.Nodes.Count;

            FilletNodeTraced(layout, node, ref nextNodeId, w);

            w.WriteLine(
                $"  RESULT: nodes {nodesBefore}→{layout.Nodes.Count}, edges {edgesBefore}→{layout.Edges.Count}, arcs {arcsBefore}→{layout.Arcs.Count}"
            );

            // Check for anomalies after each node
            layout.RebuildAdjacencyLists();
            int zeroEdgeNow = layout.Nodes.Values.Count(n => n.Edges.Count == 0);
            if (zeroEdgeNow > 0)
            {
                w.WriteLine($"  *** ANOMALY: {zeroEdgeNow} nodes with 0 edges after processing node {node.Id} ***");
                foreach (var zn in layout.Nodes.Values.Where(n => n.Edges.Count == 0).Take(3))
                {
                    w.WriteLine($"      Zero-edge node: {zn.Id} ({zn.Type})");
                }
            }
        }

        layout.RebuildAdjacencyLists();
    }

    private static bool IsEligible(GroundNode node)
    {
        if (node.Type != GroundNodeType.TaxiwayIntersection)
        {
            return false;
        }

        if (node.Edges.Count < 2)
        {
            return false;
        }

        int rwyCount = 0;
        foreach (var e in node.Edges)
        {
            if (e.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                rwyCount++;
            }
        }

        return rwyCount != 1;
    }

    private static void FilletNodeTraced(AirportGroundLayout layout, GroundNode intersection, ref int nextNodeId, StreamWriter? w)
    {
        var edges = new List<GroundEdge>();
        foreach (var e in intersection.Edges)
        {
            if (e is GroundEdge ge)
            {
                edges.Add(ge);
            }
        }

        if (edges.Count < 2)
        {
            return;
        }

        var edgeBearings = new List<(GroundEdge Edge, GroundNode OtherNode, double Bearing)>();
        foreach (var edge in edges)
        {
            var other = edge.OtherNode(intersection);
            double bearing = InitialBearing(intersection, other, edge);
            edgeBearings.Add((edge, other, bearing));
        }

        // Phase A: Plan
        var edgeTangentSpecs = new Dictionary<GroundEdge, (double Lat, double Lon, double TangentDistNm)>();
        var plannedArcs = new List<(GroundEdge EdgeA, GroundEdge EdgeB, double RadiusFt, double TurnAngleDeg)>();
        var plannedMerges = new List<(GroundEdge EdgeA, GroundNode OtherA, GroundEdge EdgeB, GroundNode OtherB)>();

        for (int i = 0; i < edgeBearings.Count; i++)
        {
            for (int j = i + 1; j < edgeBearings.Count; j++)
            {
                var (edgeA, otherA, bearingA) = edgeBearings[i];
                var (edgeB, otherB, bearingB) = edgeBearings[j];
                double turnAngle = 180.0 - GeoMath.AbsBearingDifference(bearingA, bearingB);

                if (turnAngle < CollinearThresholdDeg)
                {
                    plannedMerges.Add((edgeA, otherA, edgeB, otherB));
                    w?.WriteLine($"  PLAN MERGE: {edgeA.TaxiwayName}({otherA.Id}) + {edgeB.TaxiwayName}({otherB.Id}), turn={turnAngle:F1}°");
                }
                else if (turnAngle >= MinFilletAngleDeg)
                {
                    double halfAngleRad = (turnAngle / 2.0) * (Math.PI / 180.0);
                    double tanHalf = Math.Tan(halfAngleRad);

                    double edgeALenFt =
                        GeoMath.DistanceNm(intersection.Latitude, intersection.Longitude, otherA.Latitude, otherA.Longitude) * GeoMath.FeetPerNm;
                    double edgeBLenFt =
                        GeoMath.DistanceNm(intersection.Latitude, intersection.Longitude, otherB.Latitude, otherB.Longitude) * GeoMath.FeetPerNm;

                    double maxFitRadiusFt = Math.Min(edgeALenFt, edgeBLenFt) / tanHalf;
                    double maxRadiusFt = SelectRadius(edgeA, edgeB, turnAngle);
                    double radiusFt = Math.Min(maxFitRadiusFt, maxRadiusFt);

                    double tangentDistFt = radiusFt * tanHalf;
                    double tangentDistNm = tangentDistFt / GeoMath.FeetPerNm;

                    RecordTangentPoint(edgeTangentSpecs, edgeA, intersection, bearingA, tangentDistNm);
                    RecordTangentPoint(edgeTangentSpecs, edgeB, intersection, bearingB, tangentDistNm);
                    plannedArcs.Add((edgeA, edgeB, radiusFt, turnAngle));
                    w?.WriteLine(
                        $"  PLAN ARC: {edgeA.TaxiwayName}({otherA.Id}) + {edgeB.TaxiwayName}({otherB.Id}), turn={turnAngle:F1}°, R={radiusFt:F0}ft (max={maxRadiusFt:F0}, fit={maxFitRadiusFt:F0})"
                    );
                }
            }
        }

        if ((plannedArcs.Count + plannedMerges.Count) == 0)
        {
            return;
        }

        w?.WriteLine($"  Planned: {plannedArcs.Count} arcs, {plannedMerges.Count} merges, {edgeTangentSpecs.Count} tangent points");

        // Phase B: Create tangent nodes
        var edgeTangentNodes = new Dictionary<GroundEdge, GroundNode>();
        foreach (var (edge, (lat, lon, _)) in edgeTangentSpecs)
        {
            int id = nextNodeId++;
            var node = new GroundNode
            {
                Id = id,
                Latitude = lat,
                Longitude = lon,
                Type = GroundNodeType.TaxiwayIntersection,
            };
            layout.Nodes[id] = node;
            edgeTangentNodes[edge] = node;
            w?.WriteLine($"  CREATE TANGENT NODE {id} on edge {edge.TaxiwayName}({edge.Nodes[0].Id}--{edge.Nodes[1].Id})");
        }

        // Phase C: Create arcs
        int arcsCreated = 0;
        foreach (var (edgeA, edgeB, radiusFt, turnAngleDeg) in plannedArcs)
        {
            if (!edgeTangentNodes.TryGetValue(edgeA, out var tanA) || !edgeTangentNodes.TryGetValue(edgeB, out var tanB))
            {
                continue;
            }

            int idxA = edgeBearings.FindIndex(x => x.Edge == edgeA);
            int idxB = edgeBearings.FindIndex(x => x.Edge == edgeB);
            double bearingA = edgeBearings[idxA].Bearing;
            double bearingB = edgeBearings[idxB].Bearing;
            var (cLat, cLon) = ComputeArcCenter(intersection, bearingA, bearingB, radiusFt);
            double sweepRad = (180.0 - turnAngleDeg) * (Math.PI / 180.0);
            double arcLenNm = (radiusFt * sweepRad) / GeoMath.FeetPerNm;

            bool sameTaxiway = edgeA.SharesTaxiway(edgeB);

            layout.Arcs.Add(
                new GroundArc
                {
                    Nodes = [tanA, tanB],
                    TaxiwayNames = sameTaxiway ? [edgeA.TaxiwayName] : [edgeA.TaxiwayName, edgeB.TaxiwayName],
                    CenterLat = cLat,
                    CenterLon = cLon,
                    RadiusFt = radiusFt,
                    DistanceNm = arcLenNm,
                }
            );
            arcsCreated++;
            w?.WriteLine($"  CREATE ARC: {edgeA.TaxiwayName}/{edgeB.TaxiwayName} {tanA.Id}--{tanB.Id}");
        }

        // Phase D: Rebuild edges
        var consumedEdges = new HashSet<GroundEdge>();

        // Shorten edges with tangent points
        foreach (var (edge, tangentNode) in edgeTangentNodes)
        {
            var otherNode = edge.OtherNode(intersection);
            double newDist = GeoMath.DistanceNm(otherNode.Latitude, otherNode.Longitude, tangentNode.Latitude, tangentNode.Longitude);
            var shortened = new GroundEdge
            {
                Nodes = [otherNode, tangentNode],
                TaxiwayName = edge.TaxiwayName,
                DistanceNm = newDist,
            };
            layout.Edges.Add(shortened);
            consumedEdges.Add(edge);
            w?.WriteLine($"  SHORTEN: {edge.TaxiwayName} {edge.Nodes[0].Id}--{edge.Nodes[1].Id} → {otherNode.Id}--{tangentNode.Id}");
        }

        // Merge collinear
        int edgesMerged = 0;
        foreach (var (edgeA, otherA, edgeB, otherB) in plannedMerges)
        {
            bool aHas = edgeTangentNodes.ContainsKey(edgeA);
            bool bHas = edgeTangentNodes.ContainsKey(edgeB);
            if (aHas || bHas)
            {
                consumedEdges.Add(edgeA);
                consumedEdges.Add(edgeB);
                w?.WriteLine($"  MERGE SKIP (has tangent): {edgeA.TaxiwayName}({otherA.Id}) + {edgeB.TaxiwayName}({otherB.Id})");
                continue;
            }

            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [otherA, otherB],
                    TaxiwayName = edgeA.TaxiwayName,
                    DistanceNm = edgeA.DistanceNm + edgeB.DistanceNm,
                }
            );
            consumedEdges.Add(edgeA);
            consumedEdges.Add(edgeB);
            edgesMerged++;
            w?.WriteLine($"  MERGE: {edgeA.TaxiwayName} {otherA.Id}--{otherB.Id}");
        }

        // Orphaned edges
        foreach (var edge in edges)
        {
            if (consumedEdges.Contains(edge))
            {
                continue;
            }

            var otherNode = edge.OtherNode(intersection);
            // Collect reconnect candidates: tangent points + merge endpoints
            var reconnectCandidates = new List<GroundNode>(edgeTangentNodes.Values);
            foreach (var (_, mergeOtherA, _, mergeOtherB) in plannedMerges)
            {
                reconnectCandidates.Add(mergeOtherA);
                reconnectCandidates.Add(mergeOtherB);
            }

            GroundNode? bestTarget = FindNearest(otherNode, reconnectCandidates);
            if (bestTarget is not null)
            {
                double newDist = GeoMath.DistanceNm(otherNode.Latitude, otherNode.Longitude, bestTarget.Latitude, bestTarget.Longitude);
                layout.Edges.Add(
                    new GroundEdge
                    {
                        Nodes = [otherNode, bestTarget],
                        TaxiwayName = edge.TaxiwayName,
                        DistanceNm = newDist,
                    }
                );
                w?.WriteLine($"  ORPHAN RECONNECT: {edge.TaxiwayName} {otherNode.Id} → {bestTarget.Id}");
            }
            else
            {
                w?.WriteLine($"  ORPHAN LOST: {edge.TaxiwayName} from {otherNode.Id}");
            }
            consumedEdges.Add(edge);
        }

        // Remove consumed
        int removed = layout.Edges.RemoveAll(e => consumedEdges.Contains(e));
        w?.WriteLine($"  REMOVE: {removed} consumed edges (expected {consumedEdges.Count})");

        layout.Nodes.Remove(intersection.Id);
        w?.WriteLine($"  DELETE NODE {intersection.Id}");
    }

    private static void RecordTangentPoint(
        Dictionary<GroundEdge, (double Lat, double Lon, double TangentDistNm)> specs,
        GroundEdge edge,
        GroundNode intersection,
        double bearing,
        double tangentDistNm
    )
    {
        if (specs.TryGetValue(edge, out var existing) && (existing.TangentDistNm >= tangentDistNm))
        {
            return;
        }

        var (lat, lon) = GeoMath.ProjectPointRaw(intersection.Latitude, intersection.Longitude, bearing, tangentDistNm);
        specs[edge] = (lat, lon, tangentDistNm);
    }

    private static GroundNode? FindNearest(GroundNode target, List<GroundNode> candidates)
    {
        GroundNode? best = null;
        double bestDist = double.MaxValue;
        foreach (var c in candidates)
        {
            double d = GeoMath.DistanceNm(target.Latitude, target.Longitude, c.Latitude, c.Longitude);
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }
        return best;
    }

    private static double InitialBearing(GroundNode intersection, GroundNode other, GroundEdge edge)
    {
        if (edge.IntermediatePoints.Count > 0)
        {
            if (edge.Nodes[0].Id == intersection.Id)
            {
                var pt = edge.IntermediatePoints[0];
                return GeoMath.BearingTo(intersection.Latitude, intersection.Longitude, pt.Lat, pt.Lon);
            }
            else
            {
                var pt = edge.IntermediatePoints[^1];
                return GeoMath.BearingTo(intersection.Latitude, intersection.Longitude, pt.Lat, pt.Lon);
            }
        }
        return GeoMath.BearingTo(intersection.Latitude, intersection.Longitude, other.Latitude, other.Longitude);
    }

    private static (double Lat, double Lon) ComputeArcCenter(GroundNode intersection, double bearingA, double bearingB, double radiusFt)
    {
        double bisector = ComputeInsideBisector(bearingA, bearingB);
        double halfAngleDeg = GeoMath.AbsBearingDifference(bearingA, bisector);
        double halfAngleRad = halfAngleDeg * (Math.PI / 180.0);
        double centerDistNm = (radiusFt / Math.Cos(halfAngleRad)) / GeoMath.FeetPerNm;
        return GeoMath.ProjectPointRaw(intersection.Latitude, intersection.Longitude, bisector, centerDistNm);
    }

    private static double ComputeInsideBisector(double bearingA, double bearingB)
    {
        double a = bearingA * (Math.PI / 180.0);
        double b = bearingB * (Math.PI / 180.0);
        double x = Math.Cos(a) + Math.Cos(b);
        double y = Math.Sin(a) + Math.Sin(b);
        double deg = Math.Atan2(y, x) * (180.0 / Math.PI);
        return deg < 0 ? deg + 360.0 : deg;
    }

    private static double SelectRadius(GroundEdge edgeA, GroundEdge edgeB, double turnAngleDeg)
    {
        bool hasRwy =
            edgeA.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
            || edgeB.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase);
        bool hasRamp =
            string.Equals(edgeA.TaxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase)
            || string.Equals(edgeB.TaxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase);
        if (hasRamp)
        {
            return RampRadiusFt;
        }

        if (hasRwy && (turnAngleDeg <= 45.0))
        {
            return HighSpeedExitRadiusFt;
        }

        if (hasRwy)
        {
            return RunwayExitRadiusFt;
        }

        return DefaultRadiusFt;
    }
}
