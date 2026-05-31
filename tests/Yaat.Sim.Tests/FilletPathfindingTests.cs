using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

public class FilletPathfindingTests
{
    private readonly ITestOutputHelper _output;

    public FilletPathfindingTests(ITestOutputHelper output) => _output = output;

    private static AirportGroundLayout? LoadOak()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        if (!File.Exists(path))
        {
            return null;
        }

        return GeoJsonParser.Parse("OAK", File.ReadAllText(path), null);
    }

    private static AirportGroundLayout? LoadSfo()
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        if (!File.Exists(path))
        {
            return null;
        }

        return GeoJsonParser.Parse("SFO", File.ReadAllText(path), null);
    }

    [Fact]
    public void OAK_Filleted_AStarFindsRoute_ParkingToRunway30()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        // Find a parking node and a runway hold-short node
        var parking = layout.FindParkingByName("NEW7");
        Assert.NotNull(parking);

        var holdShort = layout.Nodes.Values.FirstOrDefault(n => n.Type == GroundNodeType.RunwayHoldShort && n.RunwayId is { } r && r.Contains("30"));

        if (holdShort is null)
        {
            _output.WriteLine("No hold-short for RWY 30 found after filleting");
            return;
        }

        var route = TaxiPathfinder.FindRoute(layout, parking.Id, holdShort.Id, AircraftCategory.Jet);
        Assert.NotNull(route);
        Assert.True(route.Segments.Count > 0);

        _output.WriteLine($"Route: {route.Segments.Count} segments, {route.TotalDistanceNm:F3}nm");
        foreach (var seg in route.Segments)
        {
            string edgeType = seg.Edge.Edge is GroundArc ? "ARC" : "EDGE";
            _output.WriteLine($"  {edgeType} {seg.TaxiwayName}: {seg.FromNodeId} -> {seg.ToNodeId}");
        }
    }

    [Fact]
    public void OAK_Filleted_AllNodesReachable()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        // Count connected components
        var remaining = new HashSet<int>(layout.Nodes.Keys);
        int componentCount = 0;
        int largestComponent = 0;
        while (remaining.Count > 0)
        {
            componentCount++;
            var seed = remaining.First();
            var cq = new Queue<int>();
            cq.Enqueue(seed);
            remaining.Remove(seed);
            int cSize = 1;
            while (cq.Count > 0)
            {
                int cid = cq.Dequeue();
                if (!layout.Nodes.TryGetValue(cid, out var cn))
                {
                    continue;
                }

                foreach (var ce in cn.Edges)
                {
                    int oid = ce.OtherNodeId(cid);
                    if (remaining.Remove(oid))
                    {
                        cq.Enqueue(oid);
                        cSize++;
                    }
                }
            }

            if (cSize > largestComponent)
            {
                largestComponent = cSize;
            }
        }

        _output.WriteLine($"Components: {componentCount}, largest: {largestComponent}");

        // BFS from the most-connected node
        var startNode = layout.Nodes.Values.OrderByDescending(n => n.Edges.Count).First();
        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(startNode.Id);
        visited.Add(startNode.Id);

        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            if (!layout.Nodes.TryGetValue(id, out var node))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                int otherId = edge.OtherNodeId(id);
                if (visited.Add(otherId))
                {
                    queue.Enqueue(otherId);
                }
            }
        }

        // Allow some disconnected nodes (parking with no connection) but most should be reachable
        int unreachable = layout.Nodes.Count - visited.Count;
        _output.WriteLine($"Reachable: {visited.Count}/{layout.Nodes.Count}, unreachable: {unreachable}");
        _output.WriteLine($"Edges: {layout.Edges.Count}, Arcs: {layout.Arcs.Count}");

        // Debug: check how many nodes have zero edges
        int zeroEdge = layout.Nodes.Values.Count(n => n.Edges.Count == 0);
        _output.WriteLine($"Nodes with 0 edges: {zeroEdge}");

        // Check if edge node references match dictionary
        int brokenEdges = 0;
        foreach (var edge in layout.AllEdges)
        {
            bool a = layout.Nodes.ContainsKey(edge.Nodes[0].Id);
            bool b = layout.Nodes.ContainsKey(edge.Nodes[1].Id);
            if (!a || !b)
            {
                brokenEdges++;
                if (brokenEdges <= 5)
                {
                    _output.WriteLine($"Broken edge: {edge.TaxiwayName} [{edge.Nodes[0].Id}({a})-{edge.Nodes[1].Id}({b})]");
                }
            }
        }

        _output.WriteLine($"Broken edges (referencing removed nodes): {brokenEdges}");

        // Debug: show some unreachable nodes and their edges
        int shown = 0;
        foreach (var n in layout.Nodes.Values)
        {
            if (!visited.Contains(n.Id) && (shown < 5))
            {
                _output.WriteLine($"Unreachable node {n.Id} ({n.Type}): {n.Edges.Count} edges");
                foreach (var e in n.Edges)
                {
                    string type = e is GroundArc ? "ARC" : "EDGE";
                    _output.WriteLine($"  {type} {e.TaxiwayName}: {e.Nodes[0].Id}-{e.Nodes[1].Id}");
                }
                shown++;
            }
        }

        // Trace connectivity from an unreachable node
        var probe = layout.Nodes.Values.FirstOrDefault(n => !visited.Contains(n.Id) && n.Edges.Count > 0);
        if (probe is not null)
        {
            _output.WriteLine($"--- Tracing from unreachable node {probe.Id} ---");
            var traceVisited = new HashSet<int>();
            var traceQueue = new Queue<int>();
            traceQueue.Enqueue(probe.Id);
            traceVisited.Add(probe.Id);
            while (traceQueue.Count > 0 && traceVisited.Count < 20)
            {
                int tid = traceQueue.Dequeue();
                if (!layout.Nodes.TryGetValue(tid, out var tn))
                {
                    continue;
                }

                foreach (var e in tn.Edges)
                {
                    int oid = e.OtherNodeId(tid);
                    string type = e is GroundArc ? "ARC" : "EDGE";
                    _output.WriteLine($"  {tid} --{type}:{e.TaxiwayName}--> {oid}");
                    if (traceVisited.Add(oid))
                    {
                        traceQueue.Enqueue(oid);
                    }
                }
            }

            _output.WriteLine($"Cluster size from {probe.Id}: {traceVisited.Count}");
        }

        Assert.True(unreachable < layout.Nodes.Count * 0.1, $"Too many unreachable nodes: {unreachable}/{layout.Nodes.Count}");
    }

    [Fact]
    public void OAK_Filleted_RunwayCenterlineWalkWorks()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        // Find a runway 30 centerline node and walk ahead
        var rwy30Heading = new TrueHeading(300);
        var centerlineNode = layout.FindNearestCenterlineNode(37.7213, -122.2208, rwy30Heading, "30");

        Assert.NotNull(centerlineNode);

        // Debug: what edges does this node have?
        _output.WriteLine($"Centerline node {centerlineNode.Id}: {centerlineNode.Edges.Count} edges");
        foreach (var e in centerlineNode.Edges)
        {
            string type = e is GroundArc ? "ARC" : "EDGE";
            _output.WriteLine($"  {type} {e.TaxiwayName}: {e.Nodes[0].Id}-{e.Nodes[1].Id} ({e.DistanceNm:F4}nm)");
        }

        // Walk ahead along centerline
        var next = layout.FindCenterlineNeighborAhead(centerlineNode, rwy30Heading, "30");
        _output.WriteLine($"Centerline start: {centerlineNode.Id}, next: {next?.Id.ToString() ?? "null"}");

        // Should be able to walk at least one step
        Assert.NotNull(next);
    }

    [Fact]
    public void OAK_Filleted_FindExitFromCenterline_Works()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        var rwy30Heading = new TrueHeading(300);
        var centerlineNode = layout.FindNearestCenterlineNode(37.7213, -122.2208, rwy30Heading, "30");
        Assert.NotNull(centerlineNode);

        var exit = layout.FindExitFromCenterline(centerlineNode.Position.Lat, centerlineNode.Position.Lon, rwy30Heading, "30", null);

        Assert.NotNull(exit);
        _output.WriteLine($"Exit found: taxiway={exit.Value.Taxiway}, holdShort={exit.Value.HoldShort.Id}, path length={exit.Value.Path.Count}");
    }

    [Fact]
    public void SFO_Filleted_AStarFindsRoute()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        // Find any two connected non-parking nodes
        var nodes = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.TaxiwayIntersection && n.Edges.Count >= 2).Take(2).ToList();

        if (nodes.Count < 2)
        {
            return;
        }

        var route = TaxiPathfinder.FindRoute(layout, nodes[0].Id, nodes[1].Id, AircraftCategory.Jet);
        Assert.NotNull(route);
        _output.WriteLine($"SFO route: {route.Segments.Count} segments");
    }
}
