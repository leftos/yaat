using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

public enum GroundNodeType
{
    TaxiwayIntersection,
    Parking,
    Spot,
    RunwayHoldShort,
    Helipad,
}

public sealed class GroundNode
{
    public required int Id { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required GroundNodeType Type { get; init; }
    public string? Name { get; init; }

    /// <summary>
    /// Parking heading (nose-in direction, degrees true). Only set for Parking nodes.
    /// </summary>
    public TrueHeading? TrueHeading { get; init; }

    /// <summary>
    /// Runway ID that this hold-short node protects. Only set for RunwayHoldShort nodes.
    /// </summary>
    public RunwayIdentifier? RunwayId { get; init; }

    /// <summary>
    /// Adjacent edges for graph traversal. Populated during layout construction.
    /// </summary>
    public List<GroundEdge> Edges { get; init; } = [];
}

public sealed class GroundEdge
{
    public required int FromNodeId { get; init; }
    public required int ToNodeId { get; init; }
    public required string TaxiwayName { get; init; }
    public required double DistanceNm { get; init; }

    /// <summary>
    /// Intermediate coordinates along this edge (lat, lon pairs) for curved paths.
    /// Does NOT include the from/to node positions — those are looked up from nodes.
    /// </summary>
    public List<(double Lat, double Lon)> IntermediatePoints { get; init; } = [];

    /// <summary>
    /// Node reference, populated during layout construction. Null for virtual sentinel nodes.
    /// </summary>
    public GroundNode? FromNode { get; set; }

    /// <summary>
    /// Node reference, populated during layout construction or for virtual nodes.
    /// </summary>
    public GroundNode? ToNode { get; set; }

    /// <summary>
    /// Returns the node on the other end of this edge from <paramref name="node"/>.
    /// Uses object references — no layout dictionary lookup needed.
    /// </summary>
    public GroundNode? OtherNode(GroundNode node) => FromNode == node ? ToNode : FromNode;
}

public sealed class GroundRunway
{
    public required string Name { get; init; }
    public required List<(double Lat, double Lon)> Coordinates { get; init; }
    public required double WidthFt { get; init; }
}

public sealed class AirportGroundLayout
{
    public required string AirportId { get; init; }

    public Dictionary<int, GroundNode> Nodes { get; init; } = [];
    public List<GroundEdge> Edges { get; init; } = [];
    public List<GroundRunway> Runways { get; init; } = [];

    /// <summary>
    /// Populate <see cref="GroundEdge.FromNode"/> and <see cref="GroundEdge.ToNode"/>
    /// on all edges from the <see cref="Nodes"/> dictionary. Call after constructing
    /// edges outside of <see cref="GeoJsonParser"/> (e.g., in tests).
    /// </summary>
    public void WireEdgeNodeReferences()
    {
        foreach (var edge in Edges)
        {
            if (edge.FromNode is null && Nodes.TryGetValue(edge.FromNodeId, out var fromNode))
            {
                edge.FromNode = fromNode;
            }

            if (edge.ToNode is null && Nodes.TryGetValue(edge.ToNodeId, out var toNode))
            {
                edge.ToNode = toNode;
            }
        }
    }

    public GroundNode? FindParkingByName(string name)
    {
        foreach (var node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.Parking && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        return null;
    }

    public GroundNode? FindHelipadByName(string name)
    {
        foreach (var node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.Helipad && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// Find a named spot, searching helipads first, then parking, then spot nodes.
    /// Used by LAND command to resolve destination by name.
    /// </summary>
    public GroundNode? FindSpotByName(string name)
    {
        return FindHelipadByName(name) ?? FindParkingByName(name) ?? FindSpotNodeByName(name);
    }

    /// <summary>
    /// Find a named spot node (GroundNodeType.Spot only).
    /// Used by $ prefix commands to resolve spot-only destinations.
    /// </summary>
    public GroundNode? FindSpotNodeByName(string name)
    {
        foreach (var node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.Spot && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        return null;
    }

    public GroundNode? FindNearestNode(double lat, double lon)
    {
        GroundNode? best = null;
        double bestDist = double.MaxValue;

        foreach (var node in Nodes.Values)
        {
            double dist = GeoMath.DistanceNm(lat, lon, node.Latitude, node.Longitude);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Find the nearest runway centerline node that is ahead of or abeam the
    /// aircraft along the given heading. When <paramref name="runwayDesignator"/>
    /// is provided, only considers nodes with RWY edges matching that runway.
    /// Falls back to the nearest matching centerline node if none is ahead.
    /// </summary>
    public GroundNode? FindNearestCenterlineNode(double lat, double lon, TrueHeading runwayHeading, string? runwayDesignator = null)
    {
        GroundNode? bestAhead = null;
        double bestAheadDist = double.MaxValue;
        GroundNode? bestAny = null;
        double bestAnyDist = double.MaxValue;

        foreach (var node in Nodes.Values)
        {
            if (!HasRunwayCenterlineEdge(node))
            {
                continue;
            }

            // Filter to edges matching the specific runway if designator provided
            if (runwayDesignator is not null && !HasRunwayEdgeForDesignator(node, runwayDesignator))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(lat, lon, node.Latitude, node.Longitude);

            if (dist < bestAnyDist)
            {
                bestAnyDist = dist;
                bestAny = node;
            }

            double bearing = GeoMath.BearingTo(lat, lon, node.Latitude, node.Longitude);
            double diff = runwayHeading.AbsAngleTo(new TrueHeading(bearing));
            if (diff <= 90 && dist < bestAheadDist)
            {
                bestAheadDist = dist;
                bestAhead = node;
            }
        }

        return bestAhead ?? bestAny;
    }

    /// <summary>
    /// Returns true if the node has a RWY edge whose name contains the given
    /// runway designator (e.g., "RWY10L/28R" contains "28R").
    /// </summary>
    private static bool HasRunwayEdgeForDesignator(GroundNode node, string designator)
    {
        foreach (var edge in node.Edges)
        {
            if (IsRunwayEdge(edge) && edge.TaxiwayName.Contains(designator, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// From a runway centerline node, find the next centerline node ahead along
    /// the given heading. Walks RWY-prefixed edges and picks the neighbor whose
    /// bearing is closest to the runway heading (within 90°).
    /// </summary>
    public GroundNode? FindCenterlineNeighborAhead(GroundNode currentNode, TrueHeading runwayHeading, string? runwayDesignator = null)
    {
        GroundNode? best = null;
        double bestDiff = double.MaxValue;

        foreach (var edge in currentNode.Edges)
        {
            if (!IsRunwayEdge(edge))
            {
                continue;
            }

            if (runwayDesignator is not null && !edge.TaxiwayName.Contains(runwayDesignator, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int neighborId = edge.FromNodeId == currentNode.Id ? edge.ToNodeId : edge.FromNodeId;
            if (!Nodes.TryGetValue(neighborId, out var neighbor))
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(currentNode.Latitude, currentNode.Longitude, neighbor.Latitude, neighbor.Longitude);
            double diff = runwayHeading.AbsAngleTo(new TrueHeading(bearing));
            if (diff < 90 && diff < bestDiff)
            {
                bestDiff = diff;
                best = neighbor;
            }
        }

        return best;
    }

    /// <summary>
    /// Walk centerline nodes ahead of the aircraft and search outward at each one
    /// for an exit matching the preference. Returns the first match with its path
    /// (starting at the centerline branch point, ending at the hold-short).
    /// This is the correct search direction: runway → taxiway → hold-short.
    /// </summary>
    public (GroundNode HoldShort, string Taxiway, List<GroundNode> Path, double ExitAngle)? FindExitFromCenterline(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        string runwayDesignator,
        ExitPreference? preference,
        HashSet<int>? excludeBranchPoints = null,
        HashSet<int>? excludeHoldShortNodes = null
    )
    {
        var startNode = FindNearestCenterlineNode(lat, lon, runwayHeading, runwayDesignator);
        if (startNode is null)
        {
            return null;
        }

        // Walk along-track: only consider centerline nodes ahead of the aircraft
        const int maxCenterlineHops = 30;
        var current = startNode;
        for (int hop = 0; hop < maxCenterlineHops && current is not null; hop++)
        {
            double alongTrack = GeoMath.AlongTrackDistanceNm(current.Latitude, current.Longitude, lat, lon, runwayHeading);
            if (alongTrack < -0.005)
            {
                // Node is behind the aircraft — skip
                current = FindCenterlineNeighborAhead(current, runwayHeading, runwayDesignator);
                continue;
            }

            // Skip centerline nodes where the aircraft already declared "unable"
            if ((excludeBranchPoints is not null) && excludeBranchPoints.Contains(current.Id))
            {
                current = FindCenterlineNeighborAhead(current, runwayHeading, runwayDesignator);
                continue;
            }

            var result = FindAdjacentHoldShort(current, runwayDesignator, runwayHeading, preference, excludeHoldShortNodes);
            if (result is not null)
            {
                double? exitAngle = ComputeExitAngle(result.Value.Node, result.Value.Taxiway, runwayHeading);
                return (result.Value.Node, result.Value.Taxiway, result.Value.Path, exitAngle ?? 90);
            }

            current = FindCenterlineNeighborAhead(current, runwayHeading, runwayDesignator);
        }

        return null;
    }

    /// <summary>
    /// From a runway centerline node, find a hold-short node reachable via
    /// non-RWY edges using BFS (max 12 hops). Each branch is constrained by
    /// the taxiway name of its first non-RWY edge. Optionally filters by
    /// runway designator, exit side, or taxiway name preference.
    /// Returns the hold-short node, taxiway name, and path from centerline.
    /// </summary>
    public (GroundNode Node, string Taxiway, List<GroundNode> Path)? FindAdjacentHoldShort(
        GroundNode centerlineNode,
        string? runwayDesignator,
        TrueHeading runwayHeading,
        ExitPreference? preference,
        HashSet<int>? excludeHoldShortNodes = null
    )
    {
        const int maxDepth = 12;
        GroundNode? best = null;
        string? bestTaxiway = null;
        List<GroundNode>? bestPath = null;
        double bestScore = double.MaxValue;

        var visited = new HashSet<int> { centerlineNode.Id };
        var queue = new Queue<(GroundNode Node, string Taxiway, List<GroundNode> Path, double TotalDist, int Depth)>();

        foreach (var edge in centerlineNode.Edges)
        {
            if (IsRunwayEdge(edge))
            {
                continue;
            }

            int neighborId = edge.FromNodeId == centerlineNode.Id ? edge.ToNodeId : edge.FromNodeId;
            if (!Nodes.TryGetValue(neighborId, out var neighbor))
            {
                continue;
            }

            if (!visited.Add(neighborId))
            {
                continue;
            }

            if (preference?.Taxiway is { } prefTwy && !string.Equals(edge.TaxiwayName, prefTwy, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            queue.Enqueue((neighbor, edge.TaxiwayName, [centerlineNode, neighbor], edge.DistanceNm, 1));
        }

        while (queue.Count > 0)
        {
            var (current, branchTwy, path, totalDist, depth) = queue.Dequeue();

            if (current.Type == GroundNodeType.RunwayHoldShort)
            {
                if (runwayDesignator is not null && current.RunwayId is { } rwyId && !rwyId.Contains(runwayDesignator))
                {
                    continue;
                }

                // Skip hold-short nodes already occupied by another aircraft
                if ((excludeHoldShortNodes is not null) && excludeHoldShortNodes.Contains(current.Id))
                {
                    continue;
                }

                if (preference?.Side is { } side)
                {
                    double bearing = GeoMath.BearingTo(centerlineNode.Latitude, centerlineNode.Longitude, current.Latitude, current.Longitude);
                    double relative = runwayHeading.SignedAngleTo(new TrueHeading(bearing));
                    bool isOnRequestedSide = side == ExitSide.Left ? relative < 0 : relative > 0;
                    if (!isOnRequestedSide)
                    {
                        continue;
                    }
                }

                double parkingBias = AverageNearestParkingDistanceNm(current, ParkingSampleCount) * ParkingProximityWeight;

                // Penalize exits that go backward (>100° from runway heading).
                // Without this, a short backward exit (e.g. E at 111° from node 230
                // at SFO) can outscore a longer forward exit (T at 19°) due to
                // distance alone, causing the caller to filter the result and miss
                // the valid forward exit entirely.
                double anglePenalty = 0;
                double? exitAngle = ComputeExitAngle(current, branchTwy, runwayHeading);
                if ((exitAngle is not null) && (exitAngle.Value > 100) && (preference?.Taxiway is null))
                {
                    anglePenalty = 10.0;
                }

                // Bonus for high-speed exits (≤45°): these have higher turn-off speeds
                // (30kts vs 15kts) and gentler turns, making them strongly preferred for
                // default selection. The bonus ensures T (19°, 0.11nm) beats E (70°, 0.03nm)
                // at the same centerline node.
                double highSpeedBonus = 0;
                if ((exitAngle is not null) && (exitAngle.Value <= 45.0) && (preference?.Taxiway is null))
                {
                    highSpeedBonus = HighSpeedExitBonus;
                }

                double score = totalDist + parkingBias + anglePenalty - highSpeedBonus;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = current;
                    bestTaxiway = branchTwy;
                    bestPath = path;
                }
                continue;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var edge in current.Edges)
            {
                if (IsRunwayEdge(edge))
                {
                    continue;
                }

                if (!string.Equals(edge.TaxiwayName, branchTwy, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int nextId = edge.FromNodeId == current.Id ? edge.ToNodeId : edge.FromNodeId;
                if (!visited.Add(nextId))
                {
                    continue;
                }

                if (!Nodes.TryGetValue(nextId, out var next))
                {
                    continue;
                }

                var nextPath = new List<GroundNode>(path) { next };
                queue.Enqueue((next, branchTwy, nextPath, totalDist + edge.DistanceNm, depth + 1));
            }
        }

        if (best is null || bestTaxiway is null || bestPath is null)
        {
            return null;
        }

        return (best, bestTaxiway, bestPath);
    }

    /// <summary>
    /// Walk backward from a hold-short node through same-name taxiway edges
    /// until reaching a runway centerline node. Returns the ordered path
    /// [branch-point, intermediates..., hold-short]. Returns null if no path
    /// to the centerline is found.
    /// </summary>
    public List<GroundNode>? FindExitPath(GroundNode holdShortNode, string taxiwayName)
    {
        const int maxDepth = 15;
        var visited = new HashSet<int> { holdShortNode.Id };
        var queue = new Queue<(GroundNode Node, List<GroundNode> Path)>();
        queue.Enqueue((holdShortNode, [holdShortNode]));

        while (queue.Count > 0)
        {
            var (current, path) = queue.Dequeue();
            if (path.Count > maxDepth)
            {
                continue;
            }

            foreach (var edge in current.Edges)
            {
                if (!string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int neighborId = edge.FromNodeId == current.Id ? edge.ToNodeId : edge.FromNodeId;
                if (!visited.Add(neighborId))
                {
                    continue;
                }

                if (!Nodes.TryGetValue(neighborId, out var neighbor))
                {
                    continue;
                }

                var nextPath = new List<GroundNode>(path) { neighbor };

                bool onCenterline = false;
                foreach (var nEdge in neighbor.Edges)
                {
                    if (IsRunwayEdge(nEdge))
                    {
                        onCenterline = true;
                        break;
                    }
                }

                if (onCenterline)
                {
                    nextPath.Reverse();
                    return nextPath;
                }

                queue.Enqueue((neighbor, nextPath));
            }
        }

        return null;
    }

    /// <summary>
    /// Find the nearest ahead hold-short node for the given runway, measured by
    /// along-track distance. Used by LandingPhase for exit-aware braking.
    /// </summary>
    public GroundNode? FindNearestHoldShortAhead(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        string runwayDesignator,
        ExitPreference? preference
    )
    {
        GroundNode? best = null;
        double bestAlongTrack = double.MaxValue;

        foreach (var node in GetRunwayHoldShortNodes(runwayDesignator))
        {
            double alongTrack = GeoMath.AlongTrackDistanceNm(node.Latitude, node.Longitude, lat, lon, runwayHeading);
            if (alongTrack <= 0)
            {
                continue;
            }

            if (preference?.Side is { } side)
            {
                double bearing = GeoMath.BearingTo(lat, lon, node.Latitude, node.Longitude);
                double relative = runwayHeading.SignedAngleTo(new TrueHeading(bearing));
                bool isOnRequestedSide = side == ExitSide.Left ? relative < 0 : relative > 0;
                if (!isOnRequestedSide)
                {
                    continue;
                }
            }

            if (preference?.Taxiway is { } taxiway)
            {
                bool hasMatchingEdge = false;
                foreach (var edge in node.Edges)
                {
                    if (!IsRunwayEdge(edge) && string.Equals(edge.TaxiwayName, taxiway, StringComparison.OrdinalIgnoreCase))
                    {
                        hasMatchingEdge = true;
                        break;
                    }
                }

                if (!hasMatchingEdge)
                {
                    continue;
                }
            }

            // Score by along-track distance + parking proximity bias
            double parkingBias = AverageNearestParkingDistanceNm(node, ParkingSampleCount) * ParkingProximityWeight;
            double score = alongTrack + parkingBias;
            if (score < bestAlongTrack)
            {
                bestAlongTrack = score;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Find a GroundRunway where either end matches the given designator (e.g., "28L").
    /// GroundRunway.Name format: "10R/28L".
    /// </summary>
    public GroundRunway? FindGroundRunway(string designator)
    {
        foreach (var rwy in Runways)
        {
            var id = RunwayIdentifier.Parse(rwy.Name);
            if (id.Contains(designator))
            {
                return rwy;
            }
        }

        return null;
    }

    /// <summary>
    /// Find the nearest taxiway node suitable as a runway exit, considering aircraft heading.
    /// Prefers exits that don't require turns greater than 90 degrees.
    /// When <paramref name="runwayDesignator"/> is provided, filters out exits that are closer
    /// to a different parallel runway's centerline.
    /// </summary>
    public GroundNode? FindNearestExit(double lat, double lon, TrueHeading runwayHeading, string? runwayDesignator, double maxSearchNm = 0.5)
    {
        GroundNode? best = null;
        double bestScore = double.MaxValue;
        GroundRunway? targetRunway = runwayDesignator is not null ? FindGroundRunway(runwayDesignator) : null;

        foreach (var node in Nodes.Values)
        {
            if (!IsValidExitCandidate(node, targetRunway))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(lat, lon, node.Latitude, node.Longitude);
            if (dist > maxSearchNm)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(lat, lon, node.Latitude, node.Longitude);
            double turnAngle = runwayHeading.AbsAngleTo(new TrueHeading(bearing));
            double parkingBias = AverageNearestParkingDistanceNm(node, ParkingSampleCount) * ParkingProximityWeight;
            double score = dist + (turnAngle > 90 ? 10.0 : 0.0) + parkingBias;

            if (score < bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Find the nearest exit on the specified side of the runway heading.
    /// Falls back to FindNearestExit if no exits match the requested side.
    /// </summary>
    public GroundNode? FindExitBySide(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        ExitSide side,
        string? runwayDesignator,
        double maxSearchNm = 0.5
    )
    {
        GroundNode? best = null;
        double bestScore = double.MaxValue;
        GroundRunway? targetRunway = runwayDesignator is not null ? FindGroundRunway(runwayDesignator) : null;

        foreach (var node in Nodes.Values)
        {
            if (!IsValidExitCandidate(node, targetRunway))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(lat, lon, node.Latitude, node.Longitude);
            if (dist > maxSearchNm)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(lat, lon, node.Latitude, node.Longitude);
            double relative = runwayHeading.SignedAngleTo(new TrueHeading(bearing));

            // Left = negative relative angle, Right = positive
            bool isOnRequestedSide = side == ExitSide.Left ? relative < 0 : relative > 0;
            if (!isOnRequestedSide)
            {
                continue;
            }

            double turnAngle = Math.Abs(relative);
            double parkingBias = AverageNearestParkingDistanceNm(node, ParkingSampleCount) * ParkingProximityWeight;
            double score = dist + (turnAngle > 90 ? 10.0 : 0.0) + parkingBias;

            if (score < bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        // Fall back to nearest exit if none found on the requested side
        return best ?? FindNearestExit(lat, lon, runwayHeading, runwayDesignator, maxSearchNm);
    }

    /// <summary>
    /// Find an exit node connected to the named taxiway.
    /// Uses a wider search radius since the taxiway might be further ahead.
    /// </summary>
    public GroundNode? FindExitByTaxiway(double lat, double lon, string taxiwayName, double maxSearchNm = 1.0)
    {
        GroundNode? best = null;
        double bestDist = double.MaxValue;

        foreach (var node in Nodes.Values)
        {
            if (node.Type is GroundNodeType.Parking or GroundNodeType.Helipad)
            {
                continue;
            }

            // Skip nodes that sit on the runway surface — they're not valid exit points
            if (HasRunwayCenterlineEdge(node))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(lat, lon, node.Latitude, node.Longitude);
            if (dist > maxSearchNm)
            {
                continue;
            }

            bool hasMatchingEdge = false;
            foreach (var edge in node.Edges)
            {
                if (!IsRunwayEdge(edge) && string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
                {
                    hasMatchingEdge = true;
                    break;
                }
            }

            if (!hasMatchingEdge)
            {
                continue;
            }

            if (dist < bestDist)
            {
                bestDist = dist;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Get the heading along the named taxiway at the given node, choosing the
    /// direction closest to <paramref name="preferredBearing"/>.
    /// Returns null if no matching taxiway edge exists at the node.
    /// </summary>
    public double? GetEdgeBearingForTaxiway(GroundNode node, string taxiwayName, double preferredBearing)
    {
        double? bestBearing = null;
        double bestDiff = double.MaxValue;

        foreach (var edge in node.Edges)
        {
            if (IsRunwayEdge(edge))
            {
                continue;
            }

            if (!string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int otherNodeId = edge.FromNodeId == node.Id ? edge.ToNodeId : edge.FromNodeId;
            if (!Nodes.TryGetValue(otherNodeId, out var otherNode))
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(node.Latitude, node.Longitude, otherNode.Latitude, otherNode.Longitude);
            double diff = GeoMath.AbsBearingDifference(bearing, preferredBearing);

            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestBearing = bearing;
            }
        }

        return bestBearing;
    }

    /// <summary>
    /// Get the taxiway name for the edge connected to a node that leads away from the runway.
    /// </summary>
    public string? GetExitTaxiwayName(GroundNode exitNode)
    {
        foreach (var edge in exitNode.Edges)
        {
            if (!IsRunwayEdge(edge))
            {
                return edge.TaxiwayName;
            }
        }

        return null;
    }

    /// <summary>
    /// Get all hold-short nodes for a specific runway.
    /// </summary>
    public List<GroundNode> GetRunwayHoldShortNodes(string runwayId)
    {
        var result = new List<GroundNode>();
        foreach (var node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } id && id.Contains(runwayId))
            {
                result.Add(node);
            }
        }

        return result;
    }

    /// <summary>
    /// Find the next node along the taxiway past the exit intersection, so the
    /// aircraft can roll clear of the runway surface. Follows the non-runway edge
    /// whose heading is closest to the aircraft's exit bearing.
    /// </summary>
    public GroundNode? FindClearNode(GroundNode exitNode, string taxiwayName, TrueHeading runwayHeading)
    {
        GroundNode? best = null;
        double bestDiff = double.MaxValue;

        foreach (var edge in exitNode.Edges)
        {
            if (IsRunwayEdge(edge))
            {
                continue;
            }

            if (!string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int otherNodeId = edge.FromNodeId == exitNode.Id ? edge.ToNodeId : edge.FromNodeId;
            if (!Nodes.TryGetValue(otherNodeId, out var otherNode))
            {
                continue;
            }

            // Prefer the direction that doesn't require turning back toward the runway
            double bearing = GeoMath.BearingTo(exitNode.Latitude, exitNode.Longitude, otherNode.Latitude, otherNode.Longitude);
            double diff = runwayHeading.AbsAngleTo(new TrueHeading(bearing));

            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = otherNode;
            }
        }

        return best;
    }

    /// <summary>
    /// Compute the angle between the runway heading and the exit taxiway at the given node.
    /// Returns the absolute angle in degrees (0 = aligned with runway, 90 = perpendicular).
    /// Returns null if no taxiway edge heading can be determined.
    /// </summary>
    public double? ComputeExitAngle(GroundNode exitNode, string taxiwayName, TrueHeading runwayHeading)
    {
        // Find the edge that leads AWAY from the runway (neighbor is not on the
        // centerline) and return its angle from the runway heading. This is the
        // actual exit direction — a high-speed exit has a small angle (~30°), a
        // standard exit has a larger angle (~90°).
        double? bestAngle = null;

        foreach (var edge in exitNode.Edges)
        {
            if (IsRunwayEdge(edge))
            {
                continue;
            }

            if (!string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int otherNodeId = edge.FromNodeId == exitNode.Id ? edge.ToNodeId : edge.FromNodeId;
            if (!Nodes.TryGetValue(otherNodeId, out var otherNode))
            {
                continue;
            }

            // Skip edges going toward the runway centerline — we want the away direction
            if (HasRunwayCenterlineEdge(otherNode))
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(exitNode.Latitude, exitNode.Longitude, otherNode.Latitude, otherNode.Longitude);
            double angle = runwayHeading.AbsAngleTo(new TrueHeading(bearing));

            if (bestAngle is null || angle < bestAngle.Value)
            {
                bestAngle = angle;
            }
        }

        return bestAngle;
    }

    /// <summary>
    /// Infers the preferred exit side for a runway using layered signals:
    /// 1. High-speed exits (≤45°), validated by parking proximity
    /// 2. Parallel runway HS inheritance (for runways with no HS exits)
    /// 3. Parking proximity (fallback)
    /// Returns null if no preference can be determined.
    /// </summary>
    public ExitSide? InferPreferredExitSide(string runwayDesignator, TrueHeading runwayHeading)
    {
        // Enumerate all exits on both sides
        var exits = EnumerateExitsBothSides(runwayDesignator, runwayHeading);
        if (exits.Count == 0)
        {
            return null;
        }

        int hsLeft = exits.Count(e => e.IsHighSpeed && (e.Side == ExitSide.Left));
        int hsRight = exits.Count(e => e.IsHighSpeed && (e.Side == ExitSide.Right));

        // Parking proximity per side: average distance from hold-shorts to nearest parking
        double avgParkLeft = AvgParkingDistForSide(exits.Where(e => e.Side == ExitSide.Left));
        double avgParkRight = AvgParkingDistForSide(exits.Where(e => e.Side == ExitSide.Right));

        ExitSide? hsSide =
            (hsLeft > hsRight) ? ExitSide.Left
            : (hsRight > hsLeft) ? ExitSide.Right
            : null;
        ExitSide? parkingSide =
            (avgParkLeft < avgParkRight) ? ExitSide.Left
            : (avgParkRight < avgParkLeft) ? ExitSide.Right
            : null;

        if (hsSide is not null)
        {
            // HS exits are a strong signal, but if parking proximity favors the
            // other side, the HS exit leads to a dead end (e.g., OAK 28R J exits
            // left toward 28L with no parking). Override with parking side.
            return (parkingSide is not null) && (parkingSide != hsSide) ? parkingSide : hsSide;
        }

        // No HS exits — check parallel runway for traffic flow inheritance.
        ExitSide? parallelHsSide = FindParallelRunwayHsSide(runwayDesignator, runwayHeading);
        if (parallelHsSide is not null)
        {
            return parallelHsSide;
        }

        return parkingSide;
    }

    /// <summary>
    /// Enumerate all exits for a runway, searching both sides per taxiway.
    /// </summary>
    private List<(int HoldShortId, ExitSide Side, bool IsHighSpeed)> EnumerateExitsBothSides(string designator, TrueHeading rwyHeading)
    {
        var exits = new List<(int HoldShortId, ExitSide Side, bool IsHighSpeed)>();
        var seen = new HashSet<int>();

        foreach (var node in Nodes.Values)
        {
            bool isCenterline = node.Edges.Any(e =>
                e.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                && e.TaxiwayName.Contains(designator, StringComparison.OrdinalIgnoreCase)
            );
            if (!isCenterline)
            {
                continue;
            }

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

                ExitSide[] sides = [ExitSide.Left, ExitSide.Right];
                foreach (var side in sides)
                {
                    var pref = new ExitPreference { Taxiway = edge.TaxiwayName, Side = side };
                    var result = FindAdjacentHoldShort(node, designator, rwyHeading, pref);
                    if (result is null)
                    {
                        continue;
                    }

                    if (!seen.Add(result.Value.Node.Id))
                    {
                        continue;
                    }

                    double? angle = ComputeExitAngle(result.Value.Node, result.Value.Taxiway, rwyHeading);
                    bool isHighSpeed = (angle is not null) && (angle.Value <= 45.0);
                    exits.Add((result.Value.Node.Id, side, isHighSpeed));
                }
            }
        }

        return exits;
    }

    /// <summary>
    /// Average distance from a side's hold-short nodes to their 3 nearest parking nodes.
    /// </summary>
    private double AvgParkingDistForSide(IEnumerable<(int HoldShortId, ExitSide Side, bool IsHighSpeed)> sideExits)
    {
        var holdShortIds = sideExits.Select(e => e.HoldShortId).Distinct().ToList();
        if (holdShortIds.Count == 0)
        {
            return double.MaxValue;
        }

        double totalAvg = 0;
        int counted = 0;
        foreach (int hsId in holdShortIds)
        {
            if (!Nodes.TryGetValue(hsId, out var hsNode))
            {
                continue;
            }

            totalAvg += AverageNearestParkingDistanceNm(hsNode, ParkingSampleCount);
            counted++;
        }

        return counted > 0 ? totalAvg / counted : double.MaxValue;
    }

    /// <summary>
    /// Find a parallel runway (same heading ±10°) and return the side where its
    /// high-speed exits are. Used for traffic flow inheritance when this runway
    /// has no high-speed exits of its own.
    /// </summary>
    private ExitSide? FindParallelRunwayHsSide(string designator, TrueHeading runwayHeading)
    {
        foreach (var rwy in Runways)
        {
            var id = RunwayIdentifier.Parse(rwy.Name);

            if (
                string.Equals(id.End1, designator, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id.End2, designator, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            double rwBearing = GeoMath.BearingTo(rwy.Coordinates[0].Lat, rwy.Coordinates[0].Lon, rwy.Coordinates[^1].Lat, rwy.Coordinates[^1].Lon);
            double end1Heading = rwBearing;
            double end2Heading = (rwBearing + 180) % 360;

            double diff1 = Math.Abs(new TrueHeading(end1Heading).SignedAngleTo(runwayHeading));
            double diff2 = Math.Abs(new TrueHeading(end2Heading).SignedAngleTo(runwayHeading));

            string? parallelDesignator = null;
            double parallelBearing = 0;
            if (diff1 <= 10)
            {
                parallelDesignator = id.End1;
                parallelBearing = end1Heading;
            }
            else if (diff2 <= 10)
            {
                parallelDesignator = id.End2;
                parallelBearing = end2Heading;
            }

            if (parallelDesignator is null)
            {
                continue;
            }

            var parallelExits = EnumerateExitsBothSides(parallelDesignator, new TrueHeading(parallelBearing));
            int pHsLeft = parallelExits.Count(e => e.IsHighSpeed && (e.Side == ExitSide.Left));
            int pHsRight = parallelExits.Count(e => e.IsHighSpeed && (e.Side == ExitSide.Right));

            if (pHsLeft > pHsRight)
            {
                return ExitSide.Left;
            }

            if (pHsRight > pHsLeft)
            {
                return ExitSide.Right;
            }
        }

        return null;
    }

    /// <summary>
    /// Find a runway exit that is ahead of the aircraft along the runway heading.
    /// Applies the given exit preference (taxiway name, side, or nearest).
    /// Returns the exit node and its taxiway name, or null if no suitable exit is ahead.
    /// </summary>
    public (GroundNode Node, string Taxiway)? FindExitAheadOnRunway(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        ExitPreference? preference,
        string? runwayDesignator,
        double maxSearchNm = 1.5
    )
    {
        GroundNode? best = null;
        double bestScore = double.MaxValue;
        GroundRunway? targetRunway = runwayDesignator is not null ? FindGroundRunway(runwayDesignator) : null;

        foreach (var node in Nodes.Values)
        {
            if (!IsValidExitCandidate(node, targetRunway))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(lat, lon, node.Latitude, node.Longitude);
            if (dist > maxSearchNm)
            {
                continue;
            }

            // Only consider exits ahead of the aircraft along the runway
            double alongTrack = GeoMath.AlongTrackDistanceNm(node.Latitude, node.Longitude, lat, lon, runwayHeading);
            if (alongTrack <= 0)
            {
                continue;
            }

            // Check for taxiway preference match
            bool matchesPreference = false;

            if (preference?.Taxiway is { } taxiway)
            {
                foreach (var edge in node.Edges)
                {
                    if (!IsRunwayEdge(edge) && string.Equals(edge.TaxiwayName, taxiway, StringComparison.OrdinalIgnoreCase))
                    {
                        matchesPreference = true;
                        break;
                    }
                }
            }

            // Apply preference filters
            if (preference?.Taxiway is not null && !matchesPreference)
            {
                continue;
            }

            if (preference?.Side is { } side)
            {
                double bearing = GeoMath.BearingTo(lat, lon, node.Latitude, node.Longitude);
                double relative = runwayHeading.SignedAngleTo(new TrueHeading(bearing));
                bool isOnRequestedSide = side == ExitSide.Left ? relative < 0 : relative > 0;
                if (!isOnRequestedSide)
                {
                    continue;
                }
            }

            // Score by along-track distance (prefer nearest ahead exit), biased toward parking
            double parkingBias = AverageNearestParkingDistanceNm(node, ParkingSampleCount) * ParkingProximityWeight;
            double score = alongTrack + parkingBias;
            if (score < bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        if (best is null)
        {
            return null;
        }

        string? taxiwayName = GetExitTaxiwayName(best);
        if (taxiwayName is null)
        {
            return null;
        }

        return (best, taxiwayName);
    }

    /// <summary>
    /// Number of nearest parking nodes to average when computing parking proximity bias.
    /// </summary>
    private const int ParkingSampleCount = 3;

    /// <summary>
    /// Weight applied to average parking distance when scoring exit candidates.
    /// Higher values make exits near parking more strongly preferred.
    /// </summary>
    private const double ParkingProximityWeight = 2.0;

    /// <summary>
    /// Score bonus (subtracted from score) for high-speed exits (≤45°) when no
    /// specific taxiway is requested. Ensures high-speed exits beat steeper exits
    /// at the same centerline node despite longer taxiway paths.
    /// </summary>
    private const double HighSpeedExitBonus = 0.15;

    /// <summary>
    /// Compute the average distance from a node to the N nearest parking nodes.
    /// Returns 0 if there are no parking nodes in the layout.
    /// </summary>
    private double AverageNearestParkingDistanceNm(GroundNode exitNode, int count)
    {
        // Collect distances to all parking nodes, keep the N smallest
        Span<double> nearest = stackalloc double[count];
        nearest.Fill(double.MaxValue);

        bool anyParking = false;
        foreach (var node in Nodes.Values)
        {
            if (node.Type != GroundNodeType.Parking)
            {
                continue;
            }

            anyParking = true;
            double dist = GeoMath.DistanceNm(exitNode.Latitude, exitNode.Longitude, node.Latitude, node.Longitude);

            // Insert into sorted top-N if smaller than the current largest
            if (dist < nearest[count - 1])
            {
                nearest[count - 1] = dist;
                // Bubble down to maintain sorted order
                for (int i = count - 2; i >= 0; i--)
                {
                    if (nearest[i] > nearest[i + 1])
                    {
                        (nearest[i], nearest[i + 1]) = (nearest[i + 1], nearest[i]);
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        if (!anyParking)
        {
            return 0;
        }

        // Average only the slots that were filled (handles layouts with fewer than N parking nodes)
        double sum = 0;
        int filled = 0;
        for (int i = 0; i < count; i++)
        {
            if (nearest[i] < double.MaxValue)
            {
                sum += nearest[i];
                filled++;
            }
        }

        return filled > 0 ? sum / filled : 0;
    }

    /// <summary>
    /// Returns true if the node is closer to <paramref name="targetRunway"/>'s centerline
    /// than to any other runway's centerline. If there are no other runways, returns true.
    /// </summary>
    private bool IsCloserToRunway(GroundNode node, GroundRunway targetRunway)
    {
        double targetDist = MinDistanceToRunwayCenterline(node, targetRunway);

        foreach (var rwy in Runways)
        {
            if (ReferenceEquals(rwy, targetRunway))
            {
                continue;
            }

            double otherDist = MinDistanceToRunwayCenterline(node, rwy);
            if (otherDist < targetDist)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Compute the minimum distance from a node to a runway's centerline polyline.
    /// Uses point-to-segment distances for each consecutive pair of coordinates.
    /// </summary>
    private static double MinDistanceToRunwayCenterline(GroundNode node, GroundRunway runway)
    {
        double minDist = double.MaxValue;
        var coords = runway.Coordinates;

        for (int i = 0; i < coords.Count - 1; i++)
        {
            double dist = PointToSegmentDistanceNm(node.Latitude, node.Longitude, coords[i].Lat, coords[i].Lon, coords[i + 1].Lat, coords[i + 1].Lon);
            if (dist < minDist)
            {
                minDist = dist;
            }
        }

        // Fallback: if runway has only one coordinate, use point-to-point distance
        if (coords.Count == 1)
        {
            minDist = GeoMath.DistanceNm(node.Latitude, node.Longitude, coords[0].Lat, coords[0].Lon);
        }

        return minDist;
    }

    /// <summary>
    /// Approximate distance from a point to a line segment on the Earth's surface.
    /// Projects the point onto the segment and returns the distance to the nearest point
    /// (endpoint or projected point).
    /// </summary>
    private static double PointToSegmentDistanceNm(double pLat, double pLon, double aLat, double aLon, double bLat, double bLon)
    {
        // Use a flat-earth approximation (valid for short distances like runway widths)
        double cosLat = Math.Cos(pLat * Math.PI / 180.0);
        double dx = (bLon - aLon) * cosLat;
        double dy = bLat - aLat;
        double px = (pLon - aLon) * cosLat;
        double py = pLat - aLat;

        double segLenSq = (dx * dx) + (dy * dy);
        if (segLenSq < 1e-20)
        {
            return GeoMath.DistanceNm(pLat, pLon, aLat, aLon);
        }

        double t = Math.Clamp(((px * dx) + (py * dy)) / segLenSq, 0.0, 1.0);
        double closestLat = aLat + (t * (bLat - aLat));
        double closestLon = aLon + (t * (bLon - aLon));

        return GeoMath.DistanceNm(pLat, pLon, closestLat, closestLon);
    }

    /// <summary>
    /// Returns true if the node is a valid runway exit candidate. Filters out:
    /// - Parking/Helipad nodes
    /// - Nodes on the runway centerline (with RWY edges)
    /// - Nodes with no taxiway edges
    /// - Nodes that are closer to a different parallel runway
    /// - Nodes within the runway surface width (intermediate routing vertices
    ///   from GeoJSON LineStrings that sit just off the centerline but aren't
    ///   real taxiway exit points)
    /// </summary>
    private bool IsValidExitCandidate(GroundNode node, GroundRunway? targetRunway)
    {
        if (node.Type is GroundNodeType.Parking or GroundNodeType.Helipad)
        {
            return false;
        }

        if (HasRunwayCenterlineEdge(node))
        {
            return false;
        }

        bool hasTaxiwayEdge = false;
        foreach (var edge in node.Edges)
        {
            if (!IsRunwayEdge(edge))
            {
                hasTaxiwayEdge = true;
                break;
            }
        }

        if (!hasTaxiwayEdge)
        {
            return false;
        }

        if (targetRunway is not null && !IsCloserToRunway(node, targetRunway))
        {
            return false;
        }

        // Filter out nodes within the runway surface. These are intermediate
        // GeoJSON routing vertices that sit just off the centerline but aren't
        // real taxiway intersections where an aircraft can exit.
        if (targetRunway is not null)
        {
            double crossTrackNm = MinDistanceToRunwayCenterline(node, targetRunway);
            double runwayHalfWidthNm = (targetRunway.WidthFt / 2.0) / 6076.12;
            double minExitDistanceNm = runwayHalfWidthNm + (50.0 / 6076.12);
            if (crossTrackNm < minExitDistanceNm)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRunwayEdge(GroundEdge edge)
    {
        return edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRunwayCenterlineEdge(GroundNode node)
    {
        foreach (var edge in node.Edges)
        {
            if (edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
