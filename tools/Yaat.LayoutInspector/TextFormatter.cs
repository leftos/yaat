namespace Yaat.LayoutInspector;

public sealed class TextFormatter(TextWriter w) : IFormatter
{
    public void WriteOverview(OverviewResult r)
    {
        w.WriteLine($"Airport: {r.AirportId}");
        w.WriteLine();
        if (r.RunwayWidths.Count > 0)
        {
            w.Write("Runway widths: ");
            w.WriteLine(string.Join(", ", r.RunwayWidths.Select(rw => $"{rw.Name}={rw.WidthFt:F0}ft")));
        }

        w.WriteLine();
        w.WriteLine($"Nodes: {r.NodeCount} total");
        foreach (var (type, count) in r.NodeCountsByType.OrderBy(kv => kv.Key))
        {
            w.WriteLine($"  {type}: {count}");
        }

        w.WriteLine();
        w.WriteLine($"Edges: {r.EdgeCount} straight, {r.ArcCount} arcs");
        w.WriteLine();
        w.WriteLine($"Taxiways: {string.Join(", ", r.TaxiwayNames)}");
        w.WriteLine($"Runways: {string.Join(", ", r.RunwayNames)}");
    }

    public void WriteTaxiway(TaxiwayResult r)
    {
        w.WriteLine($"Taxiway {r.Name}: {r.Nodes.Count} nodes, {r.HoldShortCount} hold-shorts");
        if (r.ConnectedTaxiways.Count > 0)
        {
            w.WriteLine($"  Connects to: {string.Join(", ", r.ConnectedTaxiways)}");
        }

        if (r.Intersections.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("  Intersections:");
            foreach (var ix in r.Intersections)
            {
                w.WriteLine($"    {r.Name}/{ix.OtherTaxiway} at #{ix.NodeId}");
            }
        }

        w.WriteLine();
        foreach (var node in r.Nodes)
        {
            WriteNodeCompact(node);
        }
    }

    public void WriteRunway(RunwayResult r)
    {
        w.WriteLine($"Runway {r.Designator}:");
        w.WriteLine($"  Centerline nodes: {r.CenterlineNodes.Count}");
        w.WriteLine($"  Hold-short nodes: {r.HoldShortNodes.Count}");
        w.WriteLine();
        w.WriteLine("  Centerline:");
        foreach (var node in r.CenterlineNodes)
        {
            WriteNodeCompact(node, "    ");
        }

        w.WriteLine();
        w.WriteLine("  Hold-shorts:");
        foreach (var node in r.HoldShortNodes)
        {
            WriteNodeCompact(node, "    ");
        }
    }

    public void WriteNode(NodeInfo n)
    {
        w.WriteLine($"Node {n.Id}: {n.Type}");
        w.WriteLine($"  Lat: {n.Latitude:F6}  Lon: {n.Longitude:F6}");
        w.WriteLine($"  Name: {n.Name ?? "(none)"}");
        w.WriteLine($"  RunwayId: {n.RunwayId ?? "(none)"}");
        if (n.HeadingDeg is not null)
        {
            w.WriteLine($"  Heading: {n.HeadingDeg:F0}°");
        }

        if (n.Origin is not null)
        {
            w.WriteLine($"  Origin: {n.Origin}");
        }

        w.WriteLine();
        w.WriteLine($"  Edges ({n.Edges.Count}):");
        foreach (var e in n.Edges)
        {
            string neighbor = $"[{e.NeighborType}";
            if (e.NeighborName is not null)
            {
                neighbor += $" \"{e.NeighborName}\"";
            }

            if (e.NeighborRunwayId is not null)
            {
                neighbor += $" rwy={e.NeighborRunwayId}";
            }

            neighbor += "]";
            string edgeType = e.IsArc ? " [arc]" : "";
            string bearingStr = $" bearing={e.BearingDeg:F1}°";

            string arcStr = "";
            if (e.Arc is { } a)
            {
                arcStr =
                    $"\n      arc: names=[{string.Join(",", a.TaxiwayNames)}] radius={a.MinRadiusOfCurvatureFt:F0}ft"
                    + $" maxSafe={a.MaxSafeSpeedKts:F1}kt tangent={a.TangentAtParentDeg:F1}°"
                    + $" len={a.ArcLengthNm:F4}nm";
            }

            string originStr = (e.Origin is not null) ? $"\n      origin: {e.Origin}" : "";
            w.WriteLine($"    -> Node {e.NeighborId} via {e.TaxiwayName} ({e.DistanceNm:F4}nm){edgeType}{bearingStr}  {neighbor}{arcStr}{originStr}");
        }
    }

    public void WriteNodeAngles(NodeAnglesResult r)
    {
        w.WriteLine($"  Edge-pair angles ({r.Pairs.Count}, tightest turn first):");
        foreach (var p in r.Pairs)
        {
            string bridge;
            if (p.Bridge is null)
            {
                bridge = "bridge=none (connected only through this node)";
            }
            else if (p.Bridge.BridgeTaxiways.Count == 0)
            {
                bridge = $"bridge=direct ({p.Bridge.Hops} hop, {p.Bridge.DistanceFt:F0}ft)";
            }
            else
            {
                bridge =
                    $"bridge via [{string.Join(",", p.Bridge.BridgeTaxiways)}] "
                    + $"({p.Bridge.Hops} hops, {p.Bridge.DistanceFt:F0}ft, nodes {string.Join("->", p.Bridge.NodeIds)})";
            }

            w.WriteLine(
                $"    {p.TaxiwayA}(->{p.NeighborA}) / {p.TaxiwayB}(->{p.NeighborB}): "
                    + $"fan={p.FanAngleDeg:F1}° turn={p.TurnAngleDeg:F1}°  {bridge}"
            );
        }

        w.WriteLine();
    }

    public void WriteExits(ExitsResult r)
    {
        w.WriteLine($"Exits for runway {r.Designator}: {r.Exits.Count} found");
        w.WriteLine();
        foreach (var e in r.Exits)
        {
            string angle = (e.AngleDeg is not null) ? $"{e.AngleDeg:F0}°" : "?";
            string hs = e.IsHighSpeed ? "  [high-speed]" : "";
            w.WriteLine(
                $"  Centerline {e.CenterlineNodeId} -> HS {e.HoldShortNodeId} via {e.Taxiway, -3} angle={angle, -5} side={e.Side, -5}{hs}  dist={e.TotalDistanceNm:F4}nm"
            );
        }

        w.WriteLine();
        w.WriteLine($"High-speed exits: {r.HighSpeedLeft} Left, {r.HighSpeedRight} Right");
        w.WriteLine($"Avg parking dist: Left={r.AvgParkingDistLeft:F4}nm, Right={r.AvgParkingDistRight:F4}nm");
        w.WriteLine($"Reachable parking: Left={r.ReachableParkingLeft}, Right={r.ReachableParkingRight}");
        if (r.ParallelHsSide is not null)
        {
            w.WriteLine($"Parallel runway HS side: {r.ParallelHsSide}");
        }

        w.WriteLine($"Inferred default side: {r.InferredDefaultSide ?? "(none)"}");
    }

    public void WriteBfsPath(BfsPathResult r)
    {
        w.WriteLine($"BFS from node {r.FromNodeId}, taxiway={r.Taxiway}, looking for RunwayHoldShort");
        w.WriteLine();
        for (int i = 0; i < r.Steps.Count; i++)
        {
            var step = r.Steps[i];
            w.WriteLine($"  Step {i + 1}: Node {step.NodeId} -- {step.NodeType} (depth {step.Depth})");
            foreach (var e in step.EdgesExplored)
            {
                w.WriteLine($"    Edge -> {e.NeighborId} via {e.TaxiwayName} ({e.DistanceNm:F4}nm) [{e.NeighborType}] -- {e.Action} ({e.Reason})");
            }

            w.WriteLine();
        }

        if (r.FoundPath is not null)
        {
            w.WriteLine("  Result: PATH FOUND");
            w.WriteLine($"    {string.Join(" -> ", r.FoundPath)} (RunwayHoldShort, rwy={r.HoldShortRunwayId})");
            w.WriteLine($"    Total distance: {r.TotalDistanceNm:F4}nm");
        }
        else
        {
            w.WriteLine("  Result: NO PATH FOUND");
        }
    }

    public void WriteNodeList(string title, List<NodeInfo> nodes)
    {
        w.WriteLine($"{title}: {nodes.Count}");
        w.WriteLine();
        foreach (var node in nodes)
        {
            WriteNodeCompact(node);
        }
    }

    public void WriteIntersection(IntersectionResult r)
    {
        w.WriteLine($"Intersection {r.Taxiway1}/{r.Taxiway2}: {r.Nodes.Count} node(s)");
        w.WriteLine();
        foreach (var node in r.Nodes)
        {
            WriteNode(node);
            w.WriteLine();
        }
    }

    public void WriteNodeDistance(NodeDistanceResult r)
    {
        w.WriteLine($"Distance #{r.FromNodeId} → #{r.ToNodeId}: {r.StraightLineFt:F1} ft ({r.StraightLineNm:F4} nm) straight-line");
    }

    public void WritePathDistance(PathDistanceResult r)
    {
        w.WriteLine($"Path distance [{string.Join(" → ", r.NodeIds.Select(n => $"#{n}"))}]: {r.TotalFt:F1} ft ({r.TotalNm:F4} nm)");
        foreach (var leg in r.Legs)
        {
            string note = leg.Mode == "straight" ? "  (no direct edge — straight-line)" : "";
            w.WriteLine($"  #{leg.FromNodeId, 5} → #{leg.ToNodeId, -5} {leg.Ft, 8:F1} ft  [{leg.Mode}]{note}");
        }
    }

    public void WriteValidation(ValidationResult r)
    {
        w.WriteLine($"Validation: {r.WarningCount} warning(s)");
        foreach (var warning in r.Warnings)
        {
            string origin = (warning.Origin is not null) ? $" (origin: {warning.Origin})" : "";
            w.WriteLine($"  [{warning.Code}] {warning.Message}{origin}");
        }
    }

    public void WritePathfinder(PathfinderResult r)
    {
        w.WriteLine($"Pathfinder: from node {r.FromNodeId}, taxiways [{string.Join(" ", r.Taxiways)}]");
        w.WriteLine();
        foreach (string line in r.DiagnosticLog)
        {
            w.WriteLine(line);
        }

        w.WriteLine();
        if (r.Segments is null)
        {
            w.WriteLine($"RESULT: no route (reason: {r.FailReason ?? "null"})");
        }
        else
        {
            w.WriteLine($"RESULT: {r.Segments.Count} segments");
            foreach (var seg in r.Segments)
            {
                w.WriteLine($"  {seg.TaxiwayName}: {seg.FromNodeId} -> {seg.ToNodeId}");
            }
        }
    }

    private void WriteNodeCompact(NodeInfo n, string indent = "  ")
    {
        string label = (n.Name is not null) ? $" \"{n.Name}\"" : "";
        string rwy = (n.RunwayId is not null) ? $" rwy={n.RunwayId}" : "";
        string heading = (n.HeadingDeg is not null) ? $" hdg={n.HeadingDeg:F0}" : "";
        string arcs = (n.ArcCount > 0) ? $" arcs={n.ArcCount}" : "";
        w.WriteLine($"{indent}#{n.Id} {n.Type}{label}{rwy}{heading} ({n.Latitude:F6}, {n.Longitude:F6}) -- {n.Edges.Count} edges{arcs}");
    }
}
