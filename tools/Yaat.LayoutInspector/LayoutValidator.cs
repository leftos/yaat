using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using static Yaat.Sim.Data.Airport.AirportGroundLayout;

namespace Yaat.LayoutInspector;

/// <summary>
/// Validates airport ground layout geometry and reports warnings about
/// structural issues — stale node references, degenerate arcs, tangent
/// misalignment, etc. Runs after fillet generation to catch bugs.
/// </summary>
public sealed class LayoutValidator
{
    private readonly AirportGroundLayout _layout;
    private readonly List<ValidationWarning> _warnings = [];

    public LayoutValidator(AirportGroundLayout layout) => _layout = layout;

    public List<ValidationWarning> Validate()
    {
        _warnings.Clear();
        CheckArcEndpointPositions();
        CheckArcTangentAlignment();
        CheckDegenerateArcRadius();
        CheckSelfLoopEdges();
        CheckOrphanNodes();
        CheckTaxiwayConnectivity();
        return [.. _warnings];
    }

    /// <summary>
    /// Warn if an arc's Nodes[k] position doesn't match the position stored in
    /// layout.Nodes for that ID. This catches stale object references after
    /// node repositioning (e.g., midpoint merges).
    /// </summary>
    private void CheckArcEndpointPositions()
    {
        foreach (var arc in _layout.Arcs)
        {
            for (int k = 0; k < arc.Nodes.Length; k++)
            {
                var arcNode = arc.Nodes[k];
                if (!_layout.Nodes.TryGetValue(arcNode.Id, out var layoutNode))
                {
                    Warn(
                        "arc-missing-node",
                        $"Arc {arc.TaxiwayName} ({arc.Nodes[0].Id}->{arc.Nodes[1].Id}): "
                            + $"Nodes[{k}] references #{arcNode.Id} which doesn't exist in layout.Nodes",
                        arc
                    );
                    continue;
                }

                double dLat = Math.Abs(arcNode.Latitude - layoutNode.Latitude);
                double dLon = Math.Abs(arcNode.Longitude - layoutNode.Longitude);
                if ((dLat > 1e-9) || (dLon > 1e-9))
                {
                    double dFt =
                        GeoMath.DistanceNm(arcNode.Latitude, arcNode.Longitude, layoutNode.Latitude, layoutNode.Longitude) * GeoMath.FeetPerNm;
                    Warn(
                        "arc-stale-node-ref",
                        $"Arc {arc.TaxiwayName} ({arc.Nodes[0].Id}->{arc.Nodes[1].Id}): "
                            + $"Nodes[{k}] (#{arcNode.Id}) at ({arcNode.Latitude:F6},{arcNode.Longitude:F6}) "
                            + $"but layout.Nodes[{arcNode.Id}] at ({layoutNode.Latitude:F6},{layoutNode.Longitude:F6}) — "
                            + $"{dFt:F1}ft apart. Stale object reference after node reposition?",
                        arc
                    );
                }
            }
        }

        // Same check for straight edges
        foreach (var edge in _layout.Edges)
        {
            for (int k = 0; k < edge.Nodes.Length; k++)
            {
                var edgeNode = edge.Nodes[k];
                if (!_layout.Nodes.TryGetValue(edgeNode.Id, out var layoutNode))
                {
                    Warn(
                        "edge-missing-node",
                        $"Edge {edge.TaxiwayName} ({edge.Nodes[0].Id}->{edge.Nodes[1].Id}): "
                            + $"Nodes[{k}] references #{edgeNode.Id} which doesn't exist in layout.Nodes",
                        edge
                    );
                    continue;
                }

                double dLat = Math.Abs(edgeNode.Latitude - layoutNode.Latitude);
                double dLon = Math.Abs(edgeNode.Longitude - layoutNode.Longitude);
                if ((dLat > 1e-9) || (dLon > 1e-9))
                {
                    double dFt =
                        GeoMath.DistanceNm(edgeNode.Latitude, edgeNode.Longitude, layoutNode.Latitude, layoutNode.Longitude) * GeoMath.FeetPerNm;
                    Warn(
                        "edge-stale-node-ref",
                        $"Edge {edge.TaxiwayName} ({edge.Nodes[0].Id}->{edge.Nodes[1].Id}): "
                            + $"Nodes[{k}] (#{edgeNode.Id}) is {dFt:F1}ft from layout.Nodes[{edgeNode.Id}]. "
                            + $"Stale object reference?",
                        edge
                    );
                }
            }
        }
    }

    /// <summary>
    /// Warn if an arc's tangent direction at an endpoint deviates significantly from
    /// the bearing of the adjacent straight edge at that node. A well-formed fillet arc
    /// should be tangent to the taxiway edge it connects to — the tangent at each endpoint
    /// should roughly align with the straight edge direction.
    /// </summary>
    private void CheckArcTangentAlignment()
    {
        foreach (var arc in _layout.Arcs)
        {
            var bezier = arc.ToBezier();

            for (int k = 0; k < 2; k++)
            {
                var node = arc.Nodes[k];
                double t = k == 0 ? 0.0 : 1.0;

                // Compute tangent direction at this endpoint
                var (dLat, dLon) = bezier.Derivative(t);
                double cosLat = Math.Cos(node.Latitude * (Math.PI / 180.0));
                double tangentBearing = Math.Atan2(dLon * cosLat, dLat) * (180.0 / Math.PI);
                if (t > 0.5)
                {
                    tangentBearing += 180; // Reverse for arrival direction
                }

                tangentBearing = ((tangentBearing % 360) + 360) % 360;

                // Find straight edges at this node to compare against
                foreach (var edge in node.Edges)
                {
                    if (edge is GroundArc)
                    {
                        continue;
                    }

                    // Check if this straight edge shares a taxiway with the arc
                    if (!arc.MatchesTaxiway(edge.TaxiwayName))
                    {
                        continue;
                    }

                    var other = edge.OtherNode(node);
                    double edgeBearing = GeoMath.BearingTo(node.Latitude, node.Longitude, other.Latitude, other.Longitude);

                    // The arc tangent at this endpoint should be roughly aligned with
                    // the straight edge (within ~45°). Larger deviations mean the arc
                    // approaches the edge at a sharp angle — bad for taxi navigation.
                    double diff = Math.Abs(GeoMath.AbsBearingDifference(tangentBearing, edgeBearing));
                    if (diff > 45)
                    {
                        Warn(
                            "arc-tangent-misaligned",
                            $"Arc {arc.TaxiwayName} ({arc.Nodes[0].Id}->{arc.Nodes[1].Id}): "
                                + $"tangent at Nodes[{k}] (#{node.Id}) is {tangentBearing:F1}° "
                                + $"but adjacent edge {edge.TaxiwayName} -> #{other.Id} bearing is {edgeBearing:F1}° "
                                + $"(diff={diff:F1}°). Arc approaches the edge at a sharp angle.",
                            arc
                        );
                    }
                }
            }
        }
    }

    /// <summary>
    /// Warn if a genuine turn arc (TurnAngleDeg > 30°) has MaxSafeSpeedKts below 1kt.
    /// Near-collinear arcs with degenerate beziers are expected to have low values.
    /// </summary>
    private void CheckDegenerateArcRadius()
    {
        foreach (var arc in _layout.Arcs)
        {
            if (arc.TurnAngleDeg <= 30.0)
            {
                continue;
            }

            double maxSafe = arc.MaxSafeSpeedKts(20.0);
            if (maxSafe < 1.0)
            {
                Warn(
                    "arc-degenerate-radius",
                    $"Arc {arc.TaxiwayName} ({arc.Nodes[0].Id}->{arc.Nodes[1].Id}): "
                        + $"turn={arc.TurnAngleDeg:F1}° but radius={arc.MinRadiusOfCurvatureFt:F1}ft, "
                        + $"maxSafe={maxSafe:F2}kt. Degenerate bezier for a genuine turn.",
                    arc
                );
            }
        }
    }

    /// <summary>Warn if any edge or arc has both endpoints as the same node.</summary>
    private void CheckSelfLoopEdges()
    {
        foreach (var edge in _layout.Edges)
        {
            if (edge.Nodes[0].Id == edge.Nodes[1].Id)
            {
                Warn("self-loop-edge", $"Edge {edge.TaxiwayName}: self-loop at #{edge.Nodes[0].Id}", edge);
            }
        }

        foreach (var arc in _layout.Arcs)
        {
            if (arc.Nodes[0].Id == arc.Nodes[1].Id)
            {
                Warn("self-loop-arc", $"Arc {arc.TaxiwayName}: self-loop at #{arc.Nodes[0].Id}", arc);
            }
        }
    }

    /// <summary>Warn about nodes with zero edges (disconnected from graph).</summary>
    private void CheckOrphanNodes()
    {
        foreach (var node in _layout.Nodes.Values)
        {
            if (node.Edges.Count == 0)
            {
                Warn(
                    "orphan-node",
                    $"Node #{node.Id} ({node.Type}) at ({node.Latitude:F6},{node.Longitude:F6}) " + $"has no edges. Origin: {node.Origin}",
                    null
                );
            }
        }
    }

    private void CheckTaxiwayConnectivity()
    {
        // Build adjacency via non-runway-centerline edges only
        var adj = new Dictionary<int, HashSet<int>>();
        foreach (var edge in _layout.Edges)
        {
            if (edge.IsRunwayCenterline)
            {
                continue;
            }

            int a = edge.Nodes[0].Id;
            int b = edge.Nodes[1].Id;
            if (!adj.ContainsKey(a))
            {
                adj[a] = [];
            }

            if (!adj.ContainsKey(b))
            {
                adj[b] = [];
            }

            adj[a].Add(b);
            adj[b].Add(a);
        }

        foreach (var arc in _layout.Arcs)
        {
            int a = arc.Nodes[0].Id;
            int b = arc.Nodes[1].Id;
            if (!adj.ContainsKey(a))
            {
                adj[a] = [];
            }

            if (!adj.ContainsKey(b))
            {
                adj[b] = [];
            }

            adj[a].Add(b);
            adj[b].Add(a);
        }

        if (adj.Count == 0)
        {
            return;
        }

        // BFS from first node to find main component
        var visited = new HashSet<int>();
        var components = new List<HashSet<int>>();

        foreach (int startId in adj.Keys)
        {
            if (visited.Contains(startId))
            {
                continue;
            }

            var component = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(startId);
            component.Add(startId);
            visited.Add(startId);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                if (!adj.TryGetValue(current, out var neighbors))
                {
                    continue;
                }

                foreach (int n in neighbors)
                {
                    if (component.Add(n))
                    {
                        visited.Add(n);
                        queue.Enqueue(n);
                    }
                }
            }

            components.Add(component);
        }

        if (components.Count <= 1)
        {
            return;
        }

        // Sort by size descending — largest is the main graph
        components.Sort((a, b) => b.Count.CompareTo(a.Count));

        for (int i = 1; i < components.Count; i++)
        {
            var comp = components[i];
            // Collect taxiway names in this component
            var taxiways = new HashSet<string>();
            foreach (int nodeId in comp)
            {
                if (!_layout.Nodes.TryGetValue(nodeId, out var node))
                {
                    continue;
                }

                foreach (var edge in node.Edges)
                {
                    if (!edge.IsRunwayCenterline)
                    {
                        taxiways.Add(edge.TaxiwayName);
                    }
                }
            }

            string nodeList = string.Join(", ", comp.OrderBy(id => id).Take(10).Select(id => $"#{id}"));
            if (comp.Count > 10)
            {
                nodeList += $", ... ({comp.Count} total)";
            }

            Warn(
                "disconnected-subgraph",
                $"Disconnected taxiway subgraph with {comp.Count} node(s) on [{string.Join(", ", taxiways.OrderBy(t => t))}]: {nodeList}",
                (IGroundEdge?)null
            );
        }
    }

    private void Warn(string code, string message, IGroundEdge? edge)
    {
        _warnings.Add(new ValidationWarning(code, message, edge?.Origin));
    }

    private void Warn(string code, string message, GroundArc? arc)
    {
        _warnings.Add(new ValidationWarning(code, message, (arc as IGroundEdge)?.Origin));
    }
}

public sealed record ValidationWarning(string Code, string Message, string? Origin);
