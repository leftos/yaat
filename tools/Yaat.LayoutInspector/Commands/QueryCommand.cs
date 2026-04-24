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

        foreach (int nodeId in options.NodeIds)
        {
            var node = analyzer.GetNodeDetail(nodeId);
            if (node is null)
            {
                Console.Error.WriteLine($"Node {nodeId} not found");
                return 1;
            }

            formatter.WriteNode(node);
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

            // Report any reversals (a,b)(b,a) in the combined segment list.
            if (combinedSegments is not null)
            {
                for (int i = 0; i + 1 < combinedSegments.Count; i++)
                {
                    var a = combinedSegments[i];
                    var b = combinedSegments[i + 1];
                    if (a.FromNodeId == b.ToNodeId && a.ToNodeId == b.FromNodeId)
                    {
                        diagLog.Add($"[LI] REVERSAL at index {i}: ({a.FromNodeId}→{a.ToNodeId}) then ({b.FromNodeId}→{b.ToNodeId})");
                    }
                }
            }

            var pfResult = new PathfinderResult(options.PathfinderNodeId.Value, options.PathfinderTaxiways, diagLog, combinedSegments, pfFailReason);
            formatter.WritePathfinder(pfResult);
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
}
