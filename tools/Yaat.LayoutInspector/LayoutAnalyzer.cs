using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.LayoutInspector;

public sealed class LayoutAnalyzer
{
    public AirportGroundLayout Layout { get; }
    public string AirportId { get; }

    public LayoutAnalyzer(AirportGroundLayout layout)
    {
        Layout = layout;
        AirportId = layout.AirportId;
    }

    public static LayoutAnalyzer Load(string geoJsonPath, string? airportCode)
    {
        string geoJson = File.ReadAllText(geoJsonPath);
        string airportId = Path.GetFileNameWithoutExtension(geoJsonPath).ToUpperInvariant();
        var layout = GeoJsonParser.Parse(airportId, geoJson, airportCode);
        return new LayoutAnalyzer(layout);
    }

    public OverviewResult GetOverview()
    {
        var countsByType = new Dictionary<string, int>();
        foreach (var node in Layout.Nodes.Values)
        {
            string typeName = node.Type.ToString();
            countsByType[typeName] = countsByType.GetValueOrDefault(typeName) + 1;
        }

        var taxiwayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in Layout.Edges)
        {
            if (!edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                taxiwayNames.Add(edge.TaxiwayName);
            }
        }

        var runwayWidths = Layout.Runways.Select(r => new RunwayWidthInfo(r.Name, r.WidthFt)).ToList();

        return new OverviewResult(
            AirportId,
            Layout.Nodes.Count,
            countsByType,
            Layout.Edges.Count,
            taxiwayNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            Layout.Runways.Select(r => r.Name).ToList(),
            runwayWidths
        );
    }

    private NodeInfo BuildNodeInfo(GroundNode node)
    {
        var edges = new List<EdgeInfo>();
        foreach (var edge in node.Edges)
        {
            int neighborId = (edge.FromNodeId == node.Id) ? edge.ToNodeId : edge.FromNodeId;
            Layout.Nodes.TryGetValue(neighborId, out var neighbor);
            edges.Add(
                new EdgeInfo(
                    neighborId,
                    edge.TaxiwayName,
                    edge.DistanceNm,
                    neighbor?.Type.ToString() ?? "Unknown",
                    neighbor?.Name,
                    neighbor?.RunwayId?.ToString()
                )
            );
        }

        return new NodeInfo(
            node.Id,
            node.Latitude,
            node.Longitude,
            node.Type.ToString(),
            node.Name,
            node.RunwayId?.ToString(),
            node.TrueHeading?.Degrees,
            edges
        );
    }

    public NodeInfo? GetNodeDetail(int id)
    {
        return Layout.Nodes.TryGetValue(id, out var node) ? BuildNodeInfo(node) : null;
    }

    public TaxiwayResult GetTaxiwayDetail(string name)
    {
        var nodeIds = new HashSet<int>();
        var connectedTaxiways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int holdShortCount = 0;

        foreach (var edge in Layout.Edges)
        {
            if (string.Equals(edge.TaxiwayName, name, StringComparison.OrdinalIgnoreCase))
            {
                nodeIds.Add(edge.FromNodeId);
                nodeIds.Add(edge.ToNodeId);
            }
        }

        var nodes = new List<NodeInfo>();
        foreach (int id in nodeIds.OrderBy(id => id))
        {
            if (!Layout.Nodes.TryGetValue(id, out var node))
            {
                continue;
            }

            nodes.Add(BuildNodeInfo(node));
            if (node.Type == GroundNodeType.RunwayHoldShort)
            {
                holdShortCount++;
            }

            foreach (var edge in node.Edges)
            {
                if (
                    (!edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                    && (!string.Equals(edge.TaxiwayName, name, StringComparison.OrdinalIgnoreCase))
                )
                {
                    connectedTaxiways.Add(edge.TaxiwayName);
                }
            }
        }

        return new TaxiwayResult(name, nodes, connectedTaxiways.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), holdShortCount);
    }

    public List<NodeInfo> GetParking()
    {
        return Layout
            .Nodes.Values.Where(n => n.Type == GroundNodeType.Parking)
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(BuildNodeInfo)
            .ToList();
    }

    public List<NodeInfo> GetSpots()
    {
        return Layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Spot) || ((n.Name is not null) && (n.Type == GroundNodeType.TaxiwayIntersection)))
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(BuildNodeInfo)
            .ToList();
    }

    public RunwayResult GetRunwayDetail(string designator)
    {
        var centerlineNodes = new List<NodeInfo>();
        var holdShortNodes = new List<NodeInfo>();

        foreach (var node in Layout.Nodes.Values)
        {
            bool isHoldShort = (node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId is { } rwyId) && rwyId.Contains(designator);
            bool hasCenterlineEdge = node.Edges.Any(e =>
                e.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                && e.TaxiwayName.Contains(designator, StringComparison.OrdinalIgnoreCase)
            );

            if (isHoldShort)
            {
                holdShortNodes.Add(BuildNodeInfo(node));
            }
            else if (hasCenterlineEdge)
            {
                centerlineNodes.Add(BuildNodeInfo(node));
            }
        }

        return new RunwayResult(designator, centerlineNodes, holdShortNodes);
    }

    public ExitsResult GetExits(string designator)
    {
        var exits = new List<ExitCandidate>();
        var rwy = Layout.FindGroundRunway(designator);
        TrueHeading? globalRwyHeading = null;

        if (rwy is not null)
        {
            double rwBearing = GeoMath.BearingTo(rwy.Coordinates[0].Lat, rwy.Coordinates[0].Lon, rwy.Coordinates[^1].Lat, rwy.Coordinates[^1].Lon);
            var id = RunwayIdentifier.Parse(rwy.Name);
            if (string.Equals(designator, id.End2, StringComparison.OrdinalIgnoreCase))
            {
                rwBearing = (rwBearing + 180) % 360;
            }

            globalRwyHeading = new TrueHeading(rwBearing);
        }

        foreach (var node in Layout.Nodes.Values)
        {
            bool isCenterline = node.Edges.Any(e =>
                e.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                && e.TaxiwayName.Contains(designator, StringComparison.OrdinalIgnoreCase)
            );
            if (!isCenterline)
            {
                continue;
            }

            TrueHeading rwyHeading = globalRwyHeading ?? EstimateRunwayHeading(node, designator);

            // Search each non-RWY taxiway edge independently so multi-hop exits
            // (like high-speed T at SFO, 8 hops) aren't hidden by shorter exits
            // (like E, 1 hop) that share the same centerline node.
            var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var edge in node.Edges)
            {
                if (edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!searched.Add(edge.TaxiwayName))
                {
                    continue;
                }

                var pref = new ExitPreference { Taxiway = edge.TaxiwayName };
                var result = Layout.FindAdjacentHoldShort(node, designator, rwyHeading, pref);
                if (result is null)
                {
                    continue;
                }

                double? angle = Layout.ComputeExitAngle(result.Value.Node, result.Value.Taxiway, rwyHeading);
                double totalDist = GeoMath.DistanceNm(node.Latitude, node.Longitude, result.Value.Node.Latitude, result.Value.Node.Longitude);

                exits.Add(new ExitCandidate(node.Id, result.Value.Node.Id, result.Value.Taxiway, 1, totalDist, angle, [result.Value.Node.Id]));
            }
        }

        return new ExitsResult(designator, exits.OrderBy(e => e.CenterlineNodeId).ToList());
    }

    private TrueHeading EstimateRunwayHeading(GroundNode node, string designator)
    {
        foreach (var edge in node.Edges)
        {
            if (
                edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                && edge.TaxiwayName.Contains(designator, StringComparison.OrdinalIgnoreCase)
            )
            {
                int neighborId = (edge.FromNodeId == node.Id) ? edge.ToNodeId : edge.FromNodeId;
                if (Layout.Nodes.TryGetValue(neighborId, out var neighbor))
                {
                    return new TrueHeading(GeoMath.BearingTo(node.Latitude, node.Longitude, neighbor.Latitude, neighbor.Longitude));
                }
            }
        }

        return new TrueHeading(0);
    }

    public FullDumpResult GetFullDump()
    {
        var overview = GetOverview();

        var allNodes = new Dictionary<int, NodeInfo>();
        foreach (var node in Layout.Nodes.Values)
        {
            allNodes[node.Id] = BuildNodeInfo(node);
        }

        var taxiways = new Dictionary<string, TaxiwayResult>();
        foreach (string name in overview.TaxiwayNames)
        {
            taxiways[name] = GetTaxiwayDetail(name);
        }

        var runways = new Dictionary<string, RunwayResult>();
        var exits = new Dictionary<string, ExitsResult>();
        foreach (string rwyName in overview.RunwayNames)
        {
            var id = RunwayIdentifier.Parse(rwyName);
            foreach (string designator in new[] { id.End1, id.End2 })
            {
                runways[designator] = GetRunwayDetail(designator);
                exits[designator] = GetExits(designator);
            }
        }

        return new FullDumpResult(overview, allNodes, taxiways, runways, exits, GetParking(), GetSpots());
    }

    public BfsPathResult GetBfsPath(int startNodeId, string taxiway)
    {
        if (!Layout.Nodes.TryGetValue(startNodeId, out var startNode))
        {
            return new BfsPathResult(startNodeId, taxiway, [new BfsStep(startNodeId, "NotFound", 0, [])], null, null, null);
        }

        const int maxDepth = 12;
        var steps = new List<BfsStep>();
        var visited = new HashSet<int> { startNodeId };
        var queue = new Queue<(GroundNode Node, string BranchTaxiway, List<int> Path, double TotalDist, int Depth)>();

        var seedEdges = new List<BfsEdgeExplored>();
        foreach (var edge in startNode.Edges)
        {
            int neighborId = (edge.FromNodeId == startNodeId) ? edge.ToNodeId : edge.FromNodeId;
            Layout.Nodes.TryGetValue(neighborId, out var neighbor);
            string neighborType = neighbor?.Type.ToString() ?? "Unknown";

            if (edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                seedEdges.Add(new BfsEdgeExplored(neighborId, edge.TaxiwayName, edge.DistanceNm, neighborType, "SKIP", "runway edge"));
                continue;
            }

            if (!string.Equals(edge.TaxiwayName, taxiway, StringComparison.OrdinalIgnoreCase))
            {
                seedEdges.Add(
                    new BfsEdgeExplored(
                        neighborId,
                        edge.TaxiwayName,
                        edge.DistanceNm,
                        neighborType,
                        "SKIP",
                        $"taxiway {edge.TaxiwayName} != {taxiway}"
                    )
                );
                continue;
            }

            if (neighbor is null)
            {
                seedEdges.Add(new BfsEdgeExplored(neighborId, edge.TaxiwayName, edge.DistanceNm, "Unknown", "SKIP", "node not found"));
                continue;
            }

            visited.Add(neighborId);
            queue.Enqueue((neighbor, edge.TaxiwayName, [startNodeId, neighborId], edge.DistanceNm, 1));
            seedEdges.Add(new BfsEdgeExplored(neighborId, edge.TaxiwayName, edge.DistanceNm, neighborType, "FOLLOW", $"matches taxiway {taxiway}"));
        }

        steps.Add(new BfsStep(startNodeId, startNode.Type.ToString(), 0, seedEdges));

        while (queue.Count > 0)
        {
            var (current, branchTwy, path, totalDist, depth) = queue.Dequeue();
            var edgesExplored = new List<BfsEdgeExplored>();

            if (current.Type == GroundNodeType.RunwayHoldShort)
            {
                edgesExplored.Add(
                    new BfsEdgeExplored(current.Id, branchTwy, 0, current.Type.ToString(), "FOUND", $"RunwayHoldShort, rwy={current.RunwayId}")
                );
                steps.Add(new BfsStep(current.Id, current.Type.ToString(), depth, edgesExplored));
                return new BfsPathResult(startNodeId, taxiway, steps, path, totalDist, current.RunwayId?.ToString());
            }

            if (depth >= maxDepth)
            {
                edgesExplored.Add(new BfsEdgeExplored(current.Id, branchTwy, 0, current.Type.ToString(), "STOP", $"max depth {maxDepth} reached"));
                steps.Add(new BfsStep(current.Id, current.Type.ToString(), depth, edgesExplored));
                continue;
            }

            foreach (var edge in current.Edges)
            {
                int nextId = (edge.FromNodeId == current.Id) ? edge.ToNodeId : edge.FromNodeId;
                Layout.Nodes.TryGetValue(nextId, out var next);
                string nextType = next?.Type.ToString() ?? "Unknown";

                if (edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                {
                    edgesExplored.Add(new BfsEdgeExplored(nextId, edge.TaxiwayName, edge.DistanceNm, nextType, "SKIP", "runway edge"));
                    continue;
                }

                if (!string.Equals(edge.TaxiwayName, branchTwy, StringComparison.OrdinalIgnoreCase))
                {
                    edgesExplored.Add(
                        new BfsEdgeExplored(nextId, edge.TaxiwayName, edge.DistanceNm, nextType, "SKIP", $"taxiway {edge.TaxiwayName} != {branchTwy}")
                    );
                    continue;
                }

                if (!visited.Add(nextId))
                {
                    edgesExplored.Add(new BfsEdgeExplored(nextId, edge.TaxiwayName, edge.DistanceNm, nextType, "SKIP", "already visited"));
                    continue;
                }

                if (next is null)
                {
                    edgesExplored.Add(new BfsEdgeExplored(nextId, edge.TaxiwayName, edge.DistanceNm, "Unknown", "SKIP", "node not found"));
                    continue;
                }

                var nextPath = new List<int>(path) { nextId };
                queue.Enqueue((next, branchTwy, nextPath, totalDist + edge.DistanceNm, depth + 1));
                edgesExplored.Add(new BfsEdgeExplored(nextId, edge.TaxiwayName, edge.DistanceNm, nextType, "FOLLOW", $"matches taxiway {branchTwy}"));
            }

            steps.Add(new BfsStep(current.Id, current.Type.ToString(), depth, edgesExplored));
        }

        return new BfsPathResult(startNodeId, taxiway, steps, null, null, null);
    }
}
