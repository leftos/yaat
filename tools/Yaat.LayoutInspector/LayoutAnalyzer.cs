using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

using static Yaat.Sim.Data.Airport.AirportGroundLayout;

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

    public static LayoutAnalyzer Load(string geoJsonPath, string? airportCode, bool applyFillets)
    {
        string geoJson = File.ReadAllText(geoJsonPath);
        string airportId = Path.GetFileNameWithoutExtension(geoJsonPath).ToUpperInvariant();
        var layout = GeoJsonParser.Parse(airportId, geoJson, airportCode, applyFillets);
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
        foreach (var edge in Layout.AllEdges)
        {
            if (!edge.IsRunway)
            {
                if (edge is GroundArc arc)
                {
                    foreach (string name in arc.TaxiwayNames)
                    {
                        if (!name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                        {
                            taxiwayNames.Add(name);
                        }
                    }
                }
                else
                {
                    taxiwayNames.Add(edge.TaxiwayName);
                }
            }
        }

        var runwayWidths = Layout.Runways.Select(r => new RunwayWidthInfo(r.Name, r.WidthFt)).ToList();

        return new OverviewResult(
            AirportId,
            Layout.Nodes.Count,
            countsByType,
            Layout.Edges.Count,
            Layout.Arcs.Count,
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
            int neighborId = edge.OtherNodeId(node.Id);
            Layout.Nodes.TryGetValue(neighborId, out var neighbor);
            edges.Add(
                new EdgeInfo(
                    neighborId,
                    edge.TaxiwayName,
                    edge.DistanceNm,
                    neighbor?.Type.ToString() ?? "Unknown",
                    neighbor?.Name,
                    neighbor?.RunwayId?.ToString(),
                    edge is GroundArc,
                    edge.IsRunway,
                    edge.IsRamp
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

        foreach (var edge in Layout.AllEdges)
        {
            if (edge.MatchesTaxiway(name))
            {
                nodeIds.Add(edge.Nodes[0].Id);
                nodeIds.Add(edge.Nodes[1].Id);
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
                if (edge.IsRunway || edge.MatchesTaxiway(name))
                {
                    continue;
                }

                foreach (string twyName in CollectNonRunwayTaxiwayNames(edge))
                {
                    connectedTaxiways.Add(twyName);
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
            bool hasCenterlineEdge = node.Edges.Any(e => e.MatchesRunway(designator));

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
            bool isCenterline = node.Edges.Any(e => e.MatchesRunway(designator));
            if (!isCenterline)
            {
                continue;
            }

            TrueHeading rwyHeading = globalRwyHeading ?? EstimateRunwayHeading(node, designator);

            // Search each non-RWY taxiway edge independently so multi-hop exits
            // (like high-speed T at SFO, 8 hops) aren't hidden by shorter exits
            // (like E, 1 hop) that share the same centerline node.
            // Search both sides per taxiway to enumerate hold-shorts on each side
            // (e.g., SFO E has HS 836 south and HS 837 north from the same centerline node).
            var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var edge in node.Edges)
            {
                // Collect individual taxiway names from this edge (arcs may have multiple)
                var edgeTaxiwayNames = CollectNonRunwayTaxiwayNames(edge);
                foreach (string twyName in edgeTaxiwayNames)
                {
                    if (!searched.Add(twyName))
                    {
                        continue;
                    }

                    ExitSide[] sides = [ExitSide.Left, ExitSide.Right];
                    foreach (var side in sides)
                    {
                        var pref = new ExitPreference { Taxiway = twyName, Side = side };
                        var result = Layout.FindAdjacentHoldShort(node, designator, rwyHeading, pref);
                        if (result is null)
                        {
                            continue;
                        }

                        // Deduplicate: same centerline + same hold-short already found
                        if (exits.Any(e => (e.CenterlineNodeId == node.Id) && (e.HoldShortNodeId == result.Value.Node.Id)))
                        {
                            continue;
                        }

                        double? angle = Layout.ComputeExitAngle(result.Value.Node, result.Value.Taxiway, rwyHeading);
                        double totalDist = GeoMath.DistanceNm(node.Latitude, node.Longitude, result.Value.Node.Latitude, result.Value.Node.Longitude);
                        bool isHighSpeed = (angle is not null) && (angle.Value <= 45.0);
                        string sideName = side == ExitSide.Left ? "Left" : "Right";

                        exits.Add(
                            new ExitCandidate(
                                node.Id,
                                result.Value.Node.Id,
                                result.Value.Taxiway,
                                1,
                                totalDist,
                                angle,
                                sideName,
                                isHighSpeed,
                                [result.Value.Node.Id]
                            )
                        );
                    }
                }
            }
        }

        var sorted = exits.OrderBy(e => e.CenterlineNodeId).ThenBy(e => e.Side).ToList();
        int hsLeft = sorted.Count(e => e.IsHighSpeed && (e.Side == "Left"));
        int hsRight = sorted.Count(e => e.IsHighSpeed && (e.Side == "Right"));

        // Compute average parking distance for hold-shorts on each side.
        // Lower = closer to parking = better.
        var parkingNodes = Layout.Nodes.Values.Where(n => n.Type == GroundNodeType.Parking).ToList();
        double avgParkLeft = ComputeAvgParkingDist(sorted.Where(e => e.Side == "Left"), parkingNodes);
        double avgParkRight = ComputeAvgParkingDist(sorted.Where(e => e.Side == "Right"), parkingNodes);

        // Connectivity check: from each side's hold-shorts, walk the taxiway graph
        // (no runway crossings) and count reachable parking nodes. A side that can't
        // reach parking without crossing a runway is a dead end.
        int reachableParkingLeft = CountReachableParking(sorted.Where(e => e.Side == "Left"));
        int reachableParkingRight = CountReachableParking(sorted.Where(e => e.Side == "Right"));

        // Adjacent runway check: for parallel runways, if one has HS exits on a side,
        // the parallel should prefer the same side (traffic flow). Check if a parallel
        // runway's HS exits point in the same direction as our candidate side.
        string? parallelHsSide = FindParallelRunwayHsSide(designator, globalRwyHeading);

        // Infer default side using layered signals:
        // 1. High-speed exits, validated by parking proximity (dead-end override)
        // 2. Parallel runway HS inheritance (traffic flow, trusted without validation)
        // 3. Parking proximity (fallback)
        string? inferredSide;
        string? hsSide =
            (hsLeft > hsRight) ? "Left"
            : (hsRight > hsLeft) ? "Right"
            : null;
        string? parkingSide =
            (avgParkLeft < avgParkRight) ? "Left"
            : (avgParkRight < avgParkLeft) ? "Right"
            : null;

        if (hsSide is not null)
        {
            // HS exits are a strong signal, but validate: if parking proximity
            // favors the other side, the HS exit leads to a dead end (e.g.,
            // OAK 28R J exits left toward 28L with no parking on that side).
            inferredSide = (parkingSide is not null) && (parkingSide != hsSide) ? parkingSide : hsSide;
        }
        else if (parallelHsSide is not null)
        {
            // No HS exits on this runway, but a parallel runway has them.
            // Inherit the same side — traffic flow from the parallel runway's
            // HS exits crosses this runway in that direction.
            inferredSide = parallelHsSide;
        }
        else
        {
            inferredSide = parkingSide;
        }

        return new ExitsResult(
            designator,
            sorted,
            hsLeft,
            hsRight,
            avgParkLeft,
            avgParkRight,
            reachableParkingLeft,
            reachableParkingRight,
            parallelHsSide,
            inferredSide
        );
    }

    /// <summary>
    /// From a set of exits' hold-short nodes, walk the taxiway graph (no runway
    /// edge crossings) and count distinct reachable parking nodes.
    /// </summary>
    private int CountReachableParking(IEnumerable<ExitCandidate> sideExits)
    {
        var startIds = sideExits.Select(e => e.HoldShortNodeId).Distinct().ToHashSet();
        if (startIds.Count == 0)
        {
            return 0;
        }

        var visited = new HashSet<int>(startIds);
        var queue = new Queue<int>(startIds);
        int parkingCount = 0;

        while (queue.Count > 0)
        {
            int nodeId = queue.Dequeue();
            if (!Layout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            if (node.Type == GroundNodeType.Parking)
            {
                parkingCount++;
            }

            foreach (var edge in node.Edges)
            {
                // Don't cross runways — runway edges connect centerline nodes
                if (edge.IsRunway)
                {
                    continue;
                }

                int neighborId = edge.OtherNodeId(nodeId);
                if (visited.Add(neighborId))
                {
                    queue.Enqueue(neighborId);
                }
            }
        }

        return parkingCount;
    }

    /// <summary>
    /// Find a parallel runway (same heading ±10°, different designator) and return
    /// the side where its high-speed exits are, or null if none found.
    /// </summary>
    private string? FindParallelRunwayHsSide(string designator, TrueHeading? runwayHeading)
    {
        if (runwayHeading is null)
        {
            return null;
        }

        foreach (var rwy in Layout.Runways)
        {
            var id = RunwayIdentifier.Parse(rwy.Name);
            string? parallelDesignator = null;

            // Check both ends of this runway
            if (
                string.Equals(id.End1, designator, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id.End2, designator, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue; // Same runway
            }

            // Check if either end is parallel to our designator
            double rwBearing = GeoMath.BearingTo(rwy.Coordinates[0].Lat, rwy.Coordinates[0].Lon, rwy.Coordinates[^1].Lat, rwy.Coordinates[^1].Lon);
            double end1Heading = rwBearing;
            double end2Heading = (rwBearing + 180) % 360;

            double diff1 = Math.Abs(new TrueHeading(end1Heading).SignedAngleTo(runwayHeading.Value));
            double diff2 = Math.Abs(new TrueHeading(end2Heading).SignedAngleTo(runwayHeading.Value));

            if (diff1 <= 10)
            {
                parallelDesignator = id.End1;
            }
            else if (diff2 <= 10)
            {
                parallelDesignator = id.End2;
            }

            if (parallelDesignator is null)
            {
                continue;
            }

            // Found a parallel runway. Get its exits and check HS distribution.
            TrueHeading parallelHeading = new(diff1 <= 10 ? end1Heading : end2Heading);
            var parallelExits = GetExitsRaw(parallelDesignator, parallelHeading);

            int parallelHsLeft = parallelExits.Count(e => e.IsHighSpeed && (e.Side == "Left"));
            int parallelHsRight = parallelExits.Count(e => e.IsHighSpeed && (e.Side == "Right"));

            if (parallelHsLeft > parallelHsRight)
            {
                return "Left";
            }

            if (parallelHsRight > parallelHsLeft)
            {
                return "Right";
            }
        }

        return null;
    }

    /// <summary>
    /// Raw exit enumeration (without summary stats) for use by parallel runway check.
    /// </summary>
    private List<ExitCandidate> GetExitsRaw(string designator, TrueHeading rwyHeading)
    {
        var exits = new List<ExitCandidate>();

        foreach (var node in Layout.Nodes.Values)
        {
            bool isCenterline = node.Edges.Any(e => e.MatchesRunway(designator));
            if (!isCenterline)
            {
                continue;
            }

            var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var edge in node.Edges)
            {
                var edgeTaxiwayNames = CollectNonRunwayTaxiwayNames(edge);
                foreach (string twyName in edgeTaxiwayNames)
                {
                    if (!searched.Add(twyName))
                    {
                        continue;
                    }

                    ExitSide[] sides = [ExitSide.Left, ExitSide.Right];
                    foreach (var side in sides)
                    {
                        var pref = new ExitPreference { Taxiway = twyName, Side = side };
                        var result = Layout.FindAdjacentHoldShort(node, designator, rwyHeading, pref);
                        if (result is null)
                        {
                            continue;
                        }

                        if (exits.Any(e => (e.CenterlineNodeId == node.Id) && (e.HoldShortNodeId == result.Value.Node.Id)))
                        {
                            continue;
                        }

                        double? angle = Layout.ComputeExitAngle(result.Value.Node, result.Value.Taxiway, rwyHeading);
                        double totalDist = GeoMath.DistanceNm(node.Latitude, node.Longitude, result.Value.Node.Latitude, result.Value.Node.Longitude);
                        bool isHighSpeed = (angle is not null) && (angle.Value <= 45.0);
                        string sideName = side == ExitSide.Left ? "Left" : "Right";

                        exits.Add(
                            new ExitCandidate(
                                node.Id,
                                result.Value.Node.Id,
                                result.Value.Taxiway,
                                1,
                                totalDist,
                                angle,
                                sideName,
                                isHighSpeed,
                                [result.Value.Node.Id]
                            )
                        );
                    }
                }
            }
        }

        return exits;
    }

    /// <summary>
    /// Average distance from each side's hold-short nodes to their 3 nearest parking nodes.
    /// Lower values mean the hold-shorts on that side are closer to parking.
    /// </summary>
    private double ComputeAvgParkingDist(IEnumerable<ExitCandidate> sideExits, List<GroundNode> parkingNodes)
    {
        if (parkingNodes.Count == 0)
        {
            return 0;
        }

        var holdShortIds = sideExits.Select(e => e.HoldShortNodeId).Distinct().ToList();
        if (holdShortIds.Count == 0)
        {
            return double.MaxValue;
        }

        const int sampleCount = 3;
        double totalAvg = 0;
        int counted = 0;
        foreach (int hsId in holdShortIds)
        {
            if (!Layout.Nodes.TryGetValue(hsId, out var hsNode))
            {
                continue;
            }

            // Find the 3 nearest parking nodes
            var nearest = parkingNodes
                .Select(p => GeoMath.DistanceNm(hsNode.Latitude, hsNode.Longitude, p.Latitude, p.Longitude))
                .OrderBy(d => d)
                .Take(sampleCount)
                .ToList();

            if (nearest.Count > 0)
            {
                totalAvg += nearest.Average();
                counted++;
            }
        }

        return counted > 0 ? totalAvg / counted : double.MaxValue;
    }

    private TrueHeading EstimateRunwayHeading(GroundNode node, string designator)
    {
        foreach (var edge in node.Edges)
        {
            if (edge.MatchesRunway(designator))
            {
                int neighborId = edge.OtherNodeId(node.Id);
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
            int neighborId = edge.OtherNodeId(startNodeId);
            Layout.Nodes.TryGetValue(neighborId, out var neighbor);
            string neighborType = neighbor?.Type.ToString() ?? "Unknown";

            if (edge.IsRunway)
            {
                seedEdges.Add(new BfsEdgeExplored(neighborId, edge.TaxiwayName, edge.DistanceNm, neighborType, "SKIP", "runway edge"));
                continue;
            }

            if (!edge.MatchesTaxiway(taxiway))
            {
                seedEdges.Add(
                    new BfsEdgeExplored(
                        neighborId,
                        edge.TaxiwayName,
                        edge.DistanceNm,
                        neighborType,
                        "SKIP",
                        $"taxiway {edge.TaxiwayName} does not match {taxiway}"
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
            queue.Enqueue((neighbor, taxiway, [startNodeId, neighborId], edge.DistanceNm, 1));
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
                int nextId = edge.OtherNodeId(current.Id);
                Layout.Nodes.TryGetValue(nextId, out var next);
                string nextType = next?.Type.ToString() ?? "Unknown";

                if (edge.IsRunway)
                {
                    edgesExplored.Add(new BfsEdgeExplored(nextId, edge.TaxiwayName, edge.DistanceNm, nextType, "SKIP", "runway edge"));
                    continue;
                }

                if (!edge.MatchesTaxiway(branchTwy))
                {
                    edgesExplored.Add(
                        new BfsEdgeExplored(nextId, edge.TaxiwayName, edge.DistanceNm, nextType, "SKIP", $"taxiway {edge.TaxiwayName} does not match {branchTwy}")
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

    /// <summary>
    /// Returns the individual non-runway taxiway names from an edge.
    /// For a GroundArc with TaxiwayNames ["RWY28R", "K"], returns ["K"].
    /// For a GroundEdge named "K", returns ["K"].
    /// For a runway-only edge, returns an empty list.
    /// </summary>
    private static List<string> CollectNonRunwayTaxiwayNames(IGroundEdge edge)
    {
        var names = new List<string>();
        if (edge is GroundArc arc)
        {
            foreach (string name in arc.TaxiwayNames)
            {
                if (!name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }
        else if (!edge.IsRunway)
        {
            names.Add(edge.TaxiwayName);
        }

        return names;
    }
}
