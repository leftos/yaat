using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.LayoutInspector.Commands;

/// <summary>
/// Default execution mode: runs the stack of query filters specified in
/// <see cref="CliOptions"/> (--taxiway, --runway, --node, --exits, --bfs,
/// --pathfinder, --parking, --spots, --intersection, --validate) and writes
/// results through an <see cref="IFormatter"/> (text or json).
/// </summary>
public sealed class QueryCommand : ICommand
{
    public int Execute(LayoutAnalyzer analyzer, CliOptions options)
    {
        // --validate is run eagerly so warnings land on stderr before any query
        // output; the validation result is still emitted through the formatter
        // below when --validate is set.
        List<ValidationWarning> warnings = [];
        if (options.Validate)
        {
            var validator = new LayoutValidator(analyzer.Layout);
            warnings = validator.Validate();
            if (warnings.Count > 0)
            {
                Console.Error.WriteLine($"VALIDATION: {warnings.Count} warning(s):");
                foreach (var w in warnings)
                {
                    Console.Error.WriteLine($"  [{w.Code}] {w.Message}{(w.Origin is not null ? $" (origin: {w.Origin})" : "")}");
                }

                Console.Error.WriteLine();
            }
        }

        IFormatter formatter = options.JsonOutput ? new JsonFormatter(Console.Out) : new TextFormatter(Console.Out);

        if (!options.HasAnyQueryFilter)
        {
            formatter.WriteOverview(analyzer.GetOverview());
        }

        foreach (string taxiway in options.Taxiways)
        {
            formatter.WriteTaxiway(analyzer.GetTaxiwayDetail(taxiway));
        }

        foreach (string runway in options.Runways)
        {
            if (!analyzer.HasRunwayDesignator(runway))
            {
                Console.Error.WriteLine(
                    $"Runway {runway} not found at {analyzer.AirportId}. " + $"Known runways: {string.Join(", ", analyzer.KnownRunwayDesignators())}"
                );
                return 1;
            }

            formatter.WriteRunway(analyzer.GetRunwayDetail(runway));
        }

        var nodeIdsToPrint = ExpandNodeIds(analyzer, options.NodeIds, options.NodeDepth);
        foreach (int nodeId in nodeIdsToPrint)
        {
            var node = analyzer.GetNodeDetail(nodeId);
            if (node is null)
            {
                Console.Error.WriteLine($"Node {nodeId} not found");
                return 1;
            }

            formatter.WriteNode(node);

            if (options.NodeAngles)
            {
                var angles = analyzer.GetNodeAngles(nodeId);
                if (angles is not null)
                {
                    formatter.WriteNodeAngles(angles);
                }
            }
        }

        foreach (string exitsRunway in options.ExitsRunways)
        {
            if (!analyzer.HasRunwayDesignator(exitsRunway))
            {
                Console.Error.WriteLine(
                    $"Runway {exitsRunway} not found at {analyzer.AirportId}. "
                        + $"Known runways: {string.Join(", ", analyzer.KnownRunwayDesignators())}"
                );
                return 1;
            }

            formatter.WriteExits(analyzer.GetExits(exitsRunway));
        }

        foreach (var (qRwy, qTwy, qSide) in options.ExitQueries)
        {
            ExitSide? parsedSide = qSide?.ToLowerInvariant() switch
            {
                "left" => ExitSide.Left,
                "right" => ExitSide.Right,
                _ => null,
            };
            Console.WriteLine($"\n=== Exit query: runway={qRwy} taxiway={qTwy} side={parsedSide?.ToString() ?? "(none)"} ===");
            var pref = new ExitPreference { Taxiway = string.IsNullOrEmpty(qTwy) || qTwy == "_" ? null : qTwy, Side = parsedSide };
            analyzer.RunExitQuery(qRwy, pref);
        }

        if ((options.BfsNodeId is not null) && (options.BfsTaxiway is not null))
        {
            formatter.WriteBfsPath(analyzer.GetBfsPath(options.BfsNodeId.Value, options.BfsTaxiway));
        }

        if (options.WalkTraceNodeId is not null && options.WalkTraceTaxiway is not null)
        {
            RunWalkTrace(analyzer, options.WalkTraceNodeId.Value, options.WalkTraceTaxiway);
        }

        if ((options.PathfinderNodeId is not null) && (options.PathfinderTaxiways.Count > 0))
        {
            int destFlagsSet =
                (options.PathfinderDestParking is not null ? 1 : 0)
                + (options.PathfinderDestSpot is not null ? 1 : 0)
                + (options.PathfinderDestNodeId is not null ? 1 : 0);
            if (destFlagsSet > 1)
            {
                Console.Error.WriteLine("At most one of --pf-dest-parking / --pf-dest-spot / --pf-dest-node may be set");
                return 1;
            }

            GroundNode? destHintNode = null;
            if (options.PathfinderDestParking is not null)
            {
                destHintNode =
                    analyzer.Layout.FindHelipadByName(options.PathfinderDestParking)
                    ?? analyzer.Layout.FindParkingByName(options.PathfinderDestParking);
                if (destHintNode is null)
                {
                    Console.Error.WriteLine($"Parking/helipad '{options.PathfinderDestParking}' not found at {analyzer.AirportId}");
                    return 1;
                }
            }
            else if (options.PathfinderDestSpot is not null)
            {
                destHintNode = analyzer.Layout.FindSpotNodeByName(options.PathfinderDestSpot);
                if (destHintNode is null)
                {
                    Console.Error.WriteLine($"Spot '{options.PathfinderDestSpot}' not found at {analyzer.AirportId}");
                    return 1;
                }
            }
            else if (options.PathfinderDestNodeId is not null)
            {
                if (!analyzer.Layout.Nodes.TryGetValue(options.PathfinderDestNodeId.Value, out destHintNode))
                {
                    Console.Error.WriteLine($"Node #{options.PathfinderDestNodeId.Value} not found at {analyzer.AirportId}");
                    return 1;
                }
            }

            var diagLog = new List<string>();
            if (destHintNode is not null)
            {
                diagLog.Add(
                    $"[LI] DestinationHintNode resolved: id={destHintNode.Id} type={destHintNode.Type} name={destHintNode.Name ?? "(null)"} lat={destHintNode.Position.Lat:F6} lon={destHintNode.Position.Lon:F6}"
                );
            }

            var pfRoute = TaxiPathfinder.ResolveExplicitPath(
                analyzer.Layout,
                options.PathfinderNodeId.Value,
                options.PathfinderTaxiways,
                out string? pfFailReason,
                new ExplicitPathOptions
                {
                    DestinationRunway = options.PathfinderDestinationRunway,
                    ExplicitHoldShorts = options.PathfinderHoldShorts.Count > 0 ? options.PathfinderHoldShorts : null,
                    AirportId = analyzer.AirportId,
                    DestinationHintNode = destHintNode,
                    DiagnosticLog = msg => diagLog.Add(msg),
                }
            );

            // If a destination hint is set but the explicit walk didn't reach it,
            // run FindRoute to extend — same as GroundCommandHandler.ResolveParkingRoute.
            // This lets us observe the full combined route (with any reversals) from the CLI.
            List<PathfinderSegment>? combinedSegments = pfRoute
                ?.Segments.Select(s => new PathfinderSegment(s.TaxiwayName, s.FromNodeId, s.ToNodeId))
                .ToList();
            if (destHintNode is not null && pfRoute is not null && pfRoute.Segments.Count > 0)
            {
                int explicitEndNodeId = pfRoute.Segments[^1].ToNodeId;
                if (explicitEndNodeId != destHintNode.Id)
                {
                    var extension = TaxiPathfinder.FindRoute(analyzer.Layout, explicitEndNodeId, destHintNode.Id);
                    if (extension is not null)
                    {
                        diagLog.Add($"[LI] Extension via FindRoute({explicitEndNodeId} → {destHintNode.Id}): {extension.Segments.Count} segment(s)");
                        combinedSegments!.AddRange(extension.Segments.Select(s => new PathfinderSegment(s.TaxiwayName, s.FromNodeId, s.ToNodeId)));
                    }
                    else
                    {
                        diagLog.Add($"[LI] Extension via FindRoute({explicitEndNodeId} → {destHintNode.Id}): NULL (no route)");
                    }
                }
            }

            if (combinedSegments is not null)
            {
                int explicitWalkCount = ComputeExplicitWalkCount(combinedSegments.Count, diagLog);
                ScanRouteForAnomalies(analyzer, combinedSegments, options.PathfinderTaxiways, explicitWalkCount, diagLog);
            }

            var pfResult = new PathfinderResult(options.PathfinderNodeId.Value, options.PathfinderTaxiways, diagLog, combinedSegments, pfFailReason);
            formatter.WritePathfinder(pfResult);
        }

        if (options.AutoRouteNodeId is not null && options.AutoRouteRunway is not null)
        {
            RunAutoRoute(analyzer, options.AutoRouteNodeId.Value, options.AutoRouteRunway);
        }

        if (options.ShowParking)
        {
            formatter.WriteNodeList("Parking", analyzer.GetParking());
        }

        if (options.ShowSpots)
        {
            formatter.WriteNodeList("Spots", analyzer.GetSpots());
        }

        if (options.IntersectionTaxiway1 is not null && options.IntersectionTaxiway2 is not null)
        {
            formatter.WriteIntersection(analyzer.GetIntersection(options.IntersectionTaxiway1, options.IntersectionTaxiway2));
        }

        if (options.DistanceFromNodeId is not null && options.DistanceToNodeId is not null)
        {
            var distance = analyzer.GetNodeDistance(options.DistanceFromNodeId.Value, options.DistanceToNodeId.Value);
            if (distance is null)
            {
                Console.Error.WriteLine(
                    $"--distance: node #{options.DistanceFromNodeId} or #{options.DistanceToNodeId} not found at {analyzer.AirportId}"
                );
                return 1;
            }

            formatter.WriteNodeDistance(distance);
        }

        if (options.PathDistanceNodes.Count > 0)
        {
            var pathDistance = analyzer.GetPathDistance(options.PathDistanceNodes);
            if (pathDistance is null)
            {
                Console.Error.WriteLine(
                    $"--path-distance: needs >= 2 node ids, all known at {analyzer.AirportId} (got [{string.Join(",", options.PathDistanceNodes)}])"
                );
                return 1;
            }

            formatter.WritePathDistance(pathDistance);
        }

        if (options.Validate)
        {
            var validationResult = new ValidationResult(
                warnings.Count,
                warnings.Select(w => new ValidationWarningDto(w.Code, w.Message, w.Origin)).ToList()
            );
            formatter.WriteValidation(validationResult);
        }

        return 0;
    }

    /// <summary>
    /// Maximum safe arc speed (kts) below which a route segment is flagged as
    /// dynamically untaxiable in the pathfinder diagnostic output.
    /// </summary>
    private const double TightArcMaxSafeKts = 5.0;

    /// <summary>
    /// Expand the seed node id list to also include every node within
    /// <paramref name="depth"/> graph hops via a BFS over <see cref="GroundNode.Edges"/>.
    /// Returns the seeds in input order followed by any newly-discovered ids in BFS
    /// order. <paramref name="depth"/> = 0 returns the seeds unchanged.
    /// </summary>
    private static List<int> ExpandNodeIds(LayoutAnalyzer analyzer, IReadOnlyList<int> seedIds, int depth)
    {
        if (depth <= 0 || seedIds.Count == 0)
        {
            return [.. seedIds];
        }

        var visited = new HashSet<int>(seedIds);
        var ordered = new List<int>(seedIds);
        var frontier = new List<int>(seedIds);
        for (int hop = 0; hop < depth && frontier.Count > 0; hop++)
        {
            var next = new List<int>();
            foreach (int id in frontier)
            {
                if (!analyzer.Layout.Nodes.TryGetValue(id, out var node))
                {
                    continue;
                }

                foreach (var edge in node.Edges)
                {
                    foreach (var nb in edge.Nodes)
                    {
                        if (visited.Add(nb.Id))
                        {
                            ordered.Add(nb.Id);
                            next.Add(nb.Id);
                        }
                    }
                }
            }

            frontier = next;
        }

        return ordered;
    }

    /// <summary>
    /// Run the same auto-route resolution that <c>TAXIAUTO &lt;RWY&gt;</c> uses at
    /// runtime: pick the full-length lineup hold-short via
    /// <see cref="TaxiPathfinder.FindFullLengthLineupHoldShort"/>, then run A*
    /// from <paramref name="startNodeId"/>. Prints the chosen hold-short, the
    /// taxiway sequence, and per-segment detail with any runway crossings called
    /// out so the route's intent is auditable from the CLI.
    /// </summary>
    private static void RunAutoRoute(LayoutAnalyzer analyzer, int startNodeId, string runwayId)
    {
        Console.WriteLine($"=== auto-route from #{startNodeId} to RWY {runwayId} ===");

        if (!analyzer.Layout.Nodes.TryGetValue(startNodeId, out var startNode))
        {
            Console.Error.WriteLine($"Node {startNodeId} not found");
            return;
        }

        var holdShortNodes = analyzer.Layout.GetRunwayHoldShortNodes(runwayId);
        if (holdShortNodes.Count == 0)
        {
            Console.Error.WriteLine($"No hold-short nodes for runway {runwayId}");
            return;
        }

        var targetHs = TaxiPathfinder.FindFullLengthLineupHoldShort(analyzer.Layout, startNode, runwayId, holdShortNodes);

        Console.WriteLine(
            $"start:       #{startNode.Id} {startNode.Type} {startNode.Name ?? ""} ({startNode.Position.Lat:F6}, {startNode.Position.Lon:F6})"
        );
        Console.WriteLine(
            $"target HS:   #{targetHs.Id} RunwayHoldShort rwy={targetHs.RunwayId} ({targetHs.Position.Lat:F6}, {targetHs.Position.Lon:F6})"
        );
        Console.WriteLine($"hold-short candidates considered: {holdShortNodes.Count}");
        foreach (var node in holdShortNodes)
        {
            string marker = node.Id == targetHs.Id ? " ← chosen (full-length lineup)" : "";
            string twyEdges = string.Join(",", node.Edges.Select(e => e.TaxiwayName).Distinct().Where(t => t != "RAMP"));
            Console.WriteLine($"  #{node.Id} ({node.Position.Lat:F6}, {node.Position.Lon:F6}) edges=[{twyEdges}]{marker}");
        }
        Console.WriteLine();

        var route = TaxiPathfinder.FindRoute(analyzer.Layout, startNode.Id, targetHs.Id);
        if (route is null)
        {
            Console.Error.WriteLine($"No A* route from #{startNodeId} to #{targetHs.Id}");
            return;
        }

        double totalNm = 0;
        var taxiwaySequence = new List<string>();
        string? prevTwy = null;
        foreach (var seg in route.Segments)
        {
            totalNm += seg.Edge.DistanceNm;
            if (!string.Equals(seg.TaxiwayName, prevTwy, StringComparison.OrdinalIgnoreCase))
            {
                taxiwaySequence.Add(seg.TaxiwayName);
                prevTwy = seg.TaxiwayName;
            }
        }

        Console.WriteLine($"summary:     {string.Join(" → ", taxiwaySequence)}");
        Console.WriteLine($"segments:    {route.Segments.Count}");
        Console.WriteLine($"total:       {totalNm:F4} nm ({totalNm * GeoMath.FeetPerNm:F0} ft)");

        int crossings = 0;
        foreach (var seg in route.Segments)
        {
            if (analyzer.Layout.Nodes.TryGetValue(seg.ToNodeId, out var toNode) && toNode.Type == GroundNodeType.RunwayHoldShort)
            {
                if (toNode.Id != targetHs.Id)
                {
                    crossings++;
                    Console.WriteLine($"⚠ crosses runway at HS #{toNode.Id} rwy={toNode.RunwayId}");
                }
            }
        }
        if (crossings > 0)
        {
            Console.WriteLine($"⚠ total mid-route runway crossings: {crossings}");
        }

        Console.WriteLine();
        Console.WriteLine("segments:");
        int idx = 0;
        foreach (var seg in route.Segments)
        {
            string label = $"#{seg.FromNodeId} → #{seg.ToNodeId}";
            Console.WriteLine($"  [{idx, 3}] {seg.TaxiwayName, -8} {label, -22} {seg.Edge.DistanceNm * GeoMath.FeetPerNm, 7:F0} ft");
            idx++;
        }
    }

    /// <summary>
    /// Single-taxiway walk via <see cref="TaxiPathfinder.ResolveExplicitPath"/>
    /// with one taxiway argument. The pathfinder's diagnostic log captures every
    /// step of the underlying <c>WalkTaxiway</c> and the reason it stopped
    /// (dead-end / next-twy match / hold-short / hint-reached). Prints the trace
    /// followed by the resulting segment list and any failure reason.
    /// </summary>
    private static void RunWalkTrace(LayoutAnalyzer analyzer, int nodeId, string taxiway)
    {
        Console.WriteLine($"=== walk-trace from #{nodeId} on '{taxiway}' ===");
        if (!analyzer.Layout.Nodes.ContainsKey(nodeId))
        {
            Console.Error.WriteLine($"Node {nodeId} not found");
            return;
        }

        var diag = new List<string>();
        var route = TaxiPathfinder.ResolveExplicitPath(
            analyzer.Layout,
            nodeId,
            [taxiway],
            out string? failReason,
            new ExplicitPathOptions { AirportId = analyzer.AirportId, DiagnosticLog = msg => diag.Add(msg) }
        );

        foreach (string line in diag)
        {
            Console.WriteLine($"  {line}");
        }

        if (route is not null)
        {
            Console.WriteLine($"  result: {route.Segments.Count} segment(s)");
            for (int i = 0; i < route.Segments.Count; i++)
            {
                var s = route.Segments[i];
                Console.WriteLine($"    [{i, 3}] {s.FromNodeId, 5} -> {s.ToNodeId, 5} ({s.TaxiwayName})");
            }
        }

        if (failReason is not null)
        {
            Console.WriteLine($"  fail: {failReason}");
        }
    }

    /// <summary>
    /// Scan a resolved route for two anomaly classes that aren't surfaced by
    /// the pathfinder itself: (1) any segment whose <c>TaxiwayName</c> is not in
    /// the user's authorized taxi list (and isn't a runway centerline / RAMP);
    /// (2) any segment backed by an arc with <c>MaxSafeSpeedKts</c> below
    /// <see cref="TightArcMaxSafeKts"/>. Adjacent reversals (i.e. (a→b)(b→a))
    /// are reported up front for symmetry with the runtime warning.
    /// </summary>
    /// <summary>
    /// The pathfinder may append a destination-extension after its explicit-taxiway
    /// walk (logged as "appended cached Shortest extension (N segments…)" or via the
    /// LI-side <c>Extension via FindRoute(…)</c> note). Foreign-taxiway warnings on
    /// that tail are noise — those segments are *expected* to use whatever taxiways
    /// link the named route to the parking. Parse the diagnostic log to subtract
    /// the extension count so the foreign-twy scan only flags anomalies inside the
    /// user-authorized walk.
    /// </summary>
    private static int ComputeExplicitWalkCount(int total, IReadOnlyList<string> diagLog)
    {
        int extensionCount = 0;
        var cachedRe = new System.Text.RegularExpressions.Regex(
            @"cached Shortest extension \((\d+) segments?",
            System.Text.RegularExpressions.RegexOptions.Compiled
        );
        var liRe = new System.Text.RegularExpressions.Regex(
            @"\[LI\] Extension via FindRoute.*?: (\d+) segment",
            System.Text.RegularExpressions.RegexOptions.Compiled
        );
        foreach (string line in diagLog)
        {
            var m = cachedRe.Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n))
            {
                extensionCount += n;
                continue;
            }

            m = liRe.Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n2))
            {
                extensionCount += n2;
            }
        }

        return Math.Max(0, total - extensionCount);
    }

    private static void ScanRouteForAnomalies(
        LayoutAnalyzer analyzer,
        IReadOnlyList<PathfinderSegment> segments,
        IReadOnlyList<string> authorizedTaxiways,
        int explicitWalkCount,
        List<string> diagLog
    )
    {
        for (int i = 0; i + 1 < segments.Count; i++)
        {
            var a = segments[i];
            var b = segments[i + 1];
            if (a.FromNodeId == b.ToNodeId && a.ToNodeId == b.FromNodeId)
            {
                diagLog.Add($"[LI] REVERSAL at index {i}: ({a.FromNodeId}→{a.ToNodeId}) then ({b.FromNodeId}→{b.ToNodeId})");
            }
        }

        // Only the explicit walk is bound by the user's authorized taxi list;
        // segments past explicitWalkCount come from a destination-extension
        // FindRoute call and naturally use whatever taxiways link parking back
        // to the named taxi route, so flagging them as "foreign" is just noise.
        var authorized = new HashSet<string>(authorizedTaxiways, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < explicitWalkCount && i < segments.Count; i++)
        {
            string twy = segments[i].TaxiwayName;
            if (string.IsNullOrEmpty(twy))
            {
                continue;
            }

            if (authorized.Contains(twy))
            {
                continue;
            }

            // RAMP and runway-crossing segments are inserted by the pathfinder
            // implicitly; they're expected and not "foreign".
            if (twy.Equals("RAMP", StringComparison.OrdinalIgnoreCase) || twy.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            diagLog.Add(
                $"[LI] FOREIGN-TWY at index {i}: ({segments[i].FromNodeId}→{segments[i].ToNodeId}) labeled '{twy}', not in authorized list [{string.Join(",", authorizedTaxiways)}]"
            );
        }

        for (int i = 0; i < segments.Count; i++)
        {
            if (TryGetSegmentArc(analyzer, segments[i], out var arc))
            {
                double maxSafe = arc.MaxSafeSpeedKts(AircraftCategory.Jet);
                if (maxSafe < TightArcMaxSafeKts)
                {
                    diagLog.Add(
                        $"[LI] TIGHT-ARC at index {i}: ({segments[i].FromNodeId}→{segments[i].ToNodeId}) "
                            + $"radius={arc.MinRadiusOfCurvatureFt:F1}ft maxSafe={maxSafe:F1}kt — below {TightArcMaxSafeKts:F0}kt taxiable threshold"
                    );
                }
            }
        }
    }

    private static bool TryGetSegmentArc(LayoutAnalyzer analyzer, PathfinderSegment seg, out GroundArc arc)
    {
        arc = null!;
        if (!analyzer.Layout.Nodes.TryGetValue(seg.FromNodeId, out var fromNode))
        {
            return false;
        }

        foreach (var edge in fromNode.Edges)
        {
            if (
                edge is GroundArc candidate
                && (
                    (candidate.Nodes[0].Id == seg.FromNodeId && candidate.Nodes[1].Id == seg.ToNodeId)
                    || (candidate.Nodes[0].Id == seg.ToNodeId && candidate.Nodes[1].Id == seg.FromNodeId)
                )
            )
            {
                arc = candidate;
                return true;
            }
        }

        return false;
    }
}
