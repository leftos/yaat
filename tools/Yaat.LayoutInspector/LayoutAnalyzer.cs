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

    public static LayoutAnalyzer Load(string geoJsonPath, string? airportCode, FilletMode filletMode)
    {
        string geoJson = File.ReadAllText(geoJsonPath);
        string airportId = Path.GetFileNameWithoutExtension(geoJsonPath).ToUpperInvariant();
        var layout = GeoJsonParser.Parse(airportId, geoJson, airportCode, filletMode);
        return new LayoutAnalyzer(layout);
    }

    /// <summary>
    /// Invoke FindAdjacentHoldShort from every centerline node of the runway with
    /// the given preference. Used by --exit-query to expose the full scoring trace
    /// (via GroundLayout debug logs) for a specific taxiway/side preference.
    /// </summary>
    public void RunExitQuery(string runwayDesignator, ExitPreference preference)
    {
        var rwy = Layout.FindGroundRunway(runwayDesignator);
        if (rwy is null)
        {
            Console.Error.WriteLine($"Runway {runwayDesignator} not found");
            return;
        }

        double rwBearing = GeoMath.BearingTo(
            new LatLon(rwy.Coordinates[0].Lat, rwy.Coordinates[0].Lon),
            new LatLon(rwy.Coordinates[^1].Lat, rwy.Coordinates[^1].Lon)
        );
        var id = RunwayIdentifier.Parse(rwy.Name);
        if (string.Equals(runwayDesignator, id.End2, StringComparison.OrdinalIgnoreCase))
        {
            rwBearing = (rwBearing + 180) % 360;
        }

        var rwyHeading = new TrueHeading(rwBearing);

        foreach (var node in Layout.Nodes.Values)
        {
            if (!node.Edges.Any(e => e.MatchesRunway(runwayDesignator)))
            {
                continue;
            }

            Console.WriteLine($"\n--- Centerline #{node.Id} ({node.Position.Lat:F6},{node.Position.Lon:F6}) ---");
            var result = Layout.FindAdjacentHoldShort(node, runwayDesignator, rwyHeading, preference);
            if (result is not null)
            {
                Console.WriteLine($"  → Selected: HS #{result.Value.Node.Id} via {result.Value.Taxiway}");
            }
            else
            {
                Console.WriteLine($"  → No exit found");
            }
        }
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
            if (!edge.IsRunwayCenterline)
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

            double bearing = (neighbor is not null) ? GeoMath.BearingTo(node.Position, neighbor.Position) : 0;

            ArcDetail? arcDetail = null;
            if (edge is GroundArc arc)
            {
                bool parentIsNode0 = arc.Nodes[0].Id == node.Id;
                double tangentDeg = ComputeArcTangentAtNode(arc, parentIsNode0);

                arcDetail = new ArcDetail(
                    arc.TaxiwayNames,
                    arc.MinRadiusOfCurvatureFt,
                    arc.MaxSafeSpeedKts(AircraftCategory.Jet),
                    arc.DistanceNm,
                    tangentDeg,
                    arc.TurnAngleDeg,
                    arc.EdgeBearingAtNode0Deg,
                    arc.EdgeBearingAtNode1Deg,
                    arc.P1Lat,
                    arc.P1Lon,
                    arc.P2Lat,
                    arc.P2Lon
                );
            }

            edges.Add(
                new EdgeInfo(
                    neighborId,
                    edge.TaxiwayName,
                    edge.DistanceNm,
                    neighbor?.Type.ToString() ?? "Unknown",
                    neighbor?.Name,
                    neighbor?.RunwayId?.ToString(),
                    edge is GroundArc,
                    edge.IsRunwayCenterline,
                    edge.IsRamp,
                    bearing,
                    arcDetail,
                    edge.Origin
                )
            );
        }

        int arcCount = edges.Count(e => e.IsArc);

        return new NodeInfo(
            node.Id,
            node.Position.Lat,
            node.Position.Lon,
            node.Type.ToString(),
            node.Name,
            node.RunwayId?.ToString(),
            node.TrueHeading?.Degrees,
            edges,
            node.Origin,
            arcCount
        );
    }

    /// <summary>
    /// Compute the tangent direction (degrees true) of a bezier arc at a specific endpoint.
    /// At Nodes[0]: direction from P0 toward P1 (departure tangent).
    /// At Nodes[1]: direction from P3 toward P2 (arrival tangent, reversed to show departure direction).
    /// </summary>
    private static double ComputeArcTangentAtNode(GroundArc arc, bool atNode0)
    {
        double fromLat,
            fromLon,
            toLat,
            toLon;
        if (atNode0)
        {
            // Tangent at start: P0 → P1
            fromLat = arc.Nodes[0].Position.Lat;
            fromLon = arc.Nodes[0].Position.Lon;
            toLat = arc.P1Lat;
            toLon = arc.P1Lon;
        }
        else
        {
            // Tangent at end: P3 → P2 (reversed to show departure direction from this node)
            fromLat = arc.Nodes[1].Position.Lat;
            fromLon = arc.Nodes[1].Position.Lon;
            toLat = arc.P2Lat;
            toLon = arc.P2Lon;
        }

        return GeoMath.BearingTo(fromLat, fromLon, toLat, toLon);
    }

    public NodeInfo? GetNodeDetail(int id)
    {
        return Layout.Nodes.TryGetValue(id, out var node) ? BuildNodeInfo(node) : null;
    }

    /// <summary>Max hops the bridge BFS walks looking for an alternate route between an edge pair's arms.</summary>
    private const int BridgeMaxHops = 6;

    /// <summary>
    /// Pairwise fan/turn angles between every edge fanning out of <paramref name="id"/>, plus the
    /// shortest alternate path between each pair's neighbors that avoids the node (the bridging
    /// taxiway). Pairs are returned tightest-turn-first so un-filletable corners surface at the top.
    /// </summary>
    public NodeAnglesResult? GetNodeAngles(int id)
    {
        if (!Layout.Nodes.TryGetValue(id, out var node))
        {
            return null;
        }

        var arms = new List<(int Neighbor, string Taxiway, double DepartBearing)>();
        foreach (var edge in node.Edges)
        {
            int neighborId = edge.OtherNodeId(node.Id);
            double depart;
            if (edge is GroundArc arc)
            {
                depart = ComputeArcTangentAtNode(arc, arc.Nodes[0].Id == node.Id);
            }
            else if (Layout.Nodes.TryGetValue(neighborId, out var neighbor))
            {
                depart = GeoMath.BearingTo(node.Position, neighbor.Position);
            }
            else
            {
                depart = 0;
            }

            arms.Add((neighborId, edge.TaxiwayName, depart));
        }

        var pairs = new List<EdgePairAngle>();
        for (int i = 0; i < arms.Count; i++)
        {
            for (int j = i + 1; j < arms.Count; j++)
            {
                double fan = FanAngle(arms[i].DepartBearing, arms[j].DepartBearing);
                var bridge = FindBridge(node.Id, arms[i].Neighbor, arms[j].Neighbor, arms[i].Taxiway, arms[j].Taxiway);
                pairs.Add(new EdgePairAngle(arms[i].Taxiway, arms[i].Neighbor, arms[j].Taxiway, arms[j].Neighbor, fan, 180.0 - fan, bridge));
            }
        }

        pairs.Sort((a, b) => b.TurnAngleDeg.CompareTo(a.TurnAngleDeg));
        return new NodeAnglesResult(node.Id, node.Type.ToString(), pairs);
    }

    /// <summary>Included angle (0..180°) between two outbound bearings: 0 = same direction, 180 = opposite.</summary>
    private static double FanAngle(double bearingADeg, double bearingBDeg)
    {
        return Math.Abs((((bearingADeg - bearingBDeg) + 540.0) % 360.0) - 180.0);
    }

    /// <summary>
    /// Shortest hop path from <paramref name="from"/> to <paramref name="to"/> that never revisits
    /// <paramref name="excludeNode"/>, capped at <see cref="BridgeMaxHops"/>. Returns null when no
    /// alternate route exists within the cap (the pair is connected only through the shared node).
    /// </summary>
    private BridgeInfo? FindBridge(int excludeNode, int from, int to, string taxiwayA, string taxiwayB)
    {
        if (from == to)
        {
            return new BridgeInfo([], [from], 0, 0);
        }

        var prev = new Dictionary<int, int>();
        var depth = new Dictionary<int, int> { [from] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (cur == to)
            {
                break;
            }

            if (depth[cur] >= BridgeMaxHops || !Layout.Nodes.TryGetValue(cur, out var curNode))
            {
                continue;
            }

            foreach (var edge in curNode.Edges)
            {
                int nb = edge.OtherNodeId(cur);
                if (nb == excludeNode || depth.ContainsKey(nb))
                {
                    continue;
                }

                depth[nb] = depth[cur] + 1;
                prev[nb] = cur;
                queue.Enqueue(nb);
            }
        }

        if (!depth.ContainsKey(to))
        {
            return null;
        }

        var path = new List<int> { to };
        int n = to;
        while (n != from)
        {
            n = prev[n];
            path.Add(n);
        }

        path.Reverse();

        double distFt = 0;
        var bridgeTaxiways = new List<string>();
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { taxiwayA, taxiwayB };
        for (int k = 0; k + 1 < path.Count; k++)
        {
            if (!Layout.Nodes.TryGetValue(path[k], out var a))
            {
                continue;
            }

            foreach (var edge in a.Edges)
            {
                if (edge.OtherNodeId(path[k]) != path[k + 1])
                {
                    continue;
                }

                distFt += edge.DistanceNm * GeoMath.FeetPerNm;
                foreach (string twy in CollectNonRunwayTaxiwayNames(edge))
                {
                    if (!excluded.Contains(twy) && !bridgeTaxiways.Contains(twy))
                    {
                        bridgeTaxiways.Add(twy);
                    }
                }

                break;
            }
        }

        return new BridgeInfo(bridgeTaxiways, path, distFt, path.Count - 1);
    }

    public TaxiwayResult GetTaxiwayDetail(string name)
    {
        var nodeIds = new HashSet<int>();
        var connectedTaxiways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var intersections = new List<TaxiwayIntersectionInfo>();
        var seenIntersections = new HashSet<(string, int)>();
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
                if (edge.IsRunwayCenterline || edge.MatchesTaxiway(name))
                {
                    continue;
                }

                foreach (string twyName in CollectNonRunwayTaxiwayNames(edge))
                {
                    connectedTaxiways.Add(twyName);
                    if (seenIntersections.Add((twyName, id)))
                    {
                        intersections.Add(new TaxiwayIntersectionInfo(twyName, id));
                    }
                }
            }
        }

        intersections.Sort(
            (a, b) =>
            {
                int cmp = string.Compare(a.OtherTaxiway, b.OtherTaxiway, StringComparison.OrdinalIgnoreCase);
                return cmp != 0 ? cmp : a.NodeId.CompareTo(b.NodeId);
            }
        );

        return new TaxiwayResult(
            name,
            nodes,
            connectedTaxiways.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            holdShortCount,
            intersections
        );
    }

    public IntersectionResult GetIntersection(string twy1, string twy2)
    {
        var result = new List<NodeInfo>();
        foreach (var (id, node) in Layout.Nodes)
        {
            bool hasTwy1 = node.Edges.Any(e => e.MatchesTaxiway(twy1));
            bool hasTwy2 = node.Edges.Any(e => e.MatchesTaxiway(twy2));
            if (hasTwy1 && hasTwy2)
            {
                result.Add(BuildNodeInfo(node));
            }
        }

        result.Sort((a, b) => a.Id.CompareTo(b.Id));
        return new IntersectionResult(twy1, twy2, result);
    }

    /// <summary>
    /// Every individual runway-end designator known to this layout (e.g. "10L", "28R").
    /// Flattens GroundRunway names of the form "10L/28R" into both ends.
    /// </summary>
    public List<string> KnownRunwayDesignators()
    {
        var designators = new List<string>();
        foreach (var rwy in Layout.Runways)
        {
            var id = RunwayIdentifier.Parse(rwy.Name);
            designators.Add(id.End1);
            designators.Add(id.End2);
        }

        return designators.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool HasRunwayDesignator(string designator)
    {
        foreach (var rwy in Layout.Runways)
        {
            var id = RunwayIdentifier.Parse(rwy.Name);
            if (id.Contains(designator))
            {
                return true;
            }
        }

        return false;
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
            double rwBearing = GeoMath.BearingTo(
                new LatLon(rwy.Coordinates[0].Lat, rwy.Coordinates[0].Lon),
                new LatLon(rwy.Coordinates[^1].Lat, rwy.Coordinates[^1].Lon)
            );
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
                        double totalDist = GeoMath.DistanceNm(node.Position, result.Value.Node.Position);
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

        // Delegate parallel-runway detection and the final inferred-side decision to
        // the runtime AirportGroundLayout so the tool reports the same answer the sim
        // uses. The diagnostic counts above are LayoutInspector's per-(centerline,
        // hold-short) view; the runtime dedupes by hold-short alone, so its hsLeft/
        // hsRight may differ — that's intentional, the diagnostic counts are tool data.
        string? parallelHsSide = globalRwyHeading is { } heading
            ? Layout.FindParallelRunwayHsSide(designator, heading) switch
            {
                ExitSide.Left => "Left",
                ExitSide.Right => "Right",
                _ => null,
            }
            : null;

        string? inferredSide = globalRwyHeading is { } infHeading
            ? Layout.InferPreferredExitSide(designator, infHeading) switch
            {
                ExitSide.Left => "Left",
                ExitSide.Right => "Right",
                _ => null,
            }
            : null;

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
                if (edge.IsRunwayCenterline)
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
            var nearest = parkingNodes.Select(p => GeoMath.DistanceNm(hsNode.Position, p.Position)).OrderBy(d => d).Take(sampleCount).ToList();

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
                    return new TrueHeading(GeoMath.BearingTo(node.Position, neighbor.Position));
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

        var rawEdges = Layout
            .Edges.Select(e => new RawEdgeInfo(e.Nodes[0].Id, e.Nodes[1].Id, e.TaxiwayName, e.DistanceNm, e.IsRunwayCenterline, e.Origin))
            .ToList();
        var rawArcs = Layout
            .Arcs.Select(a => new RawArcInfo(
                a.Nodes[0].Id,
                a.Nodes[1].Id,
                a.TaxiwayName,
                a.TaxiwayNames,
                a.DistanceNm,
                a.MinRadiusOfCurvatureFt,
                a.TurnAngleDeg,
                a.Origin
            ))
            .ToList();

        return new FullDumpResult(overview, allNodes, taxiways, runways, exits, GetParking(), GetSpots(), rawEdges, rawArcs);
    }

    public BfsPathResult GetBfsPath(int startNodeId, string taxiway)
    {
        if (!Layout.Nodes.TryGetValue(startNodeId, out var startNode))
        {
            return new BfsPathResult(startNodeId, taxiway, [new BfsStep(startNodeId, "NotFound", 0, [])], null, null, null);
        }

        const int maxDepth = 20;
        var steps = new List<BfsStep>();
        var visited = new HashSet<int> { startNodeId };
        var queue = new Queue<(GroundNode Node, string BranchTaxiway, List<int> Path, double TotalDist, int Depth)>();

        var seedEdges = new List<BfsEdgeExplored>();
        foreach (var edge in startNode.Edges)
        {
            int neighborId = edge.OtherNodeId(startNodeId);
            Layout.Nodes.TryGetValue(neighborId, out var neighbor);
            string neighborType = neighbor?.Type.ToString() ?? "Unknown";

            if (edge.IsRunwayCenterline)
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

                if (edge.IsRunwayCenterline)
                {
                    edgesExplored.Add(new BfsEdgeExplored(nextId, edge.TaxiwayName, edge.DistanceNm, nextType, "SKIP", "runway edge"));
                    continue;
                }

                if (!edge.MatchesTaxiway(branchTwy))
                {
                    edgesExplored.Add(
                        new BfsEdgeExplored(
                            nextId,
                            edge.TaxiwayName,
                            edge.DistanceNm,
                            nextType,
                            "SKIP",
                            $"taxiway {edge.TaxiwayName} does not match {branchTwy}"
                        )
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
        else if (!edge.IsRunwayCenterline)
        {
            names.Add(edge.TaxiwayName);
        }

        return names;
    }
}
