using Microsoft.Extensions.Logging;
using Yaat.Sim;
using Yaat.Sim.Testing;

namespace Yaat.LayoutInspector;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        string geoJsonPath = args[0];
        if (!File.Exists(geoJsonPath))
        {
            Console.Error.WriteLine($"File not found: {geoJsonPath}");
            return 1;
        }

        string? airportCode = null;
        string? navdataDir = null;
        var taxiways = new List<string>();
        var runways = new List<string>();
        var nodeIds = new List<int>();
        var exitsRunways = new List<string>();
        int? pathNodeId = null;
        string? pathTaxiway = null;
        int? pfNodeId = null;
        var pfTaxiways = new List<string>();
        bool showParking = false;
        bool showSpots = false;
        bool validate = false;
        string? intersectTwy1 = null;
        string? intersectTwy2 = null;
        bool jsonOutput = false;
        bool dumpAll = false;
        bool noFillets = false;
        bool debugFillets = false;
        string? svgOutput = null;
        string? ticksCsvPath = null;
        var svgHighlightTaxiways = new List<string>();
        var svgHighlightRunways = new List<string>();
        var svgHighlightNodes = new List<int>();
        var svgAnnotations = new List<(int NodeId, string Text)>();
        var svgRouteNodes = new List<int>();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--airport-code" when i + 1 < args.Length:
                    airportCode = args[++i];
                    break;
                case "--navdata" when i + 1 < args.Length:
                    navdataDir = args[++i];
                    break;
                case "--taxiway" when i + 1 < args.Length:
                    taxiways.Add(args[++i].ToUpperInvariant());
                    break;
                case "--runway" when i + 1 < args.Length:
                    runways.Add(args[++i].ToUpperInvariant());
                    break;
                case "--node" when i + 1 < args.Length:
                    nodeIds.Add(int.Parse(args[++i]));
                    break;
                case "--exits" when i + 1 < args.Length:
                    exitsRunways.Add(args[++i].ToUpperInvariant());
                    break;
                case "--bfs" when i + 2 < args.Length:
                    pathNodeId = int.Parse(args[++i]);
                    pathTaxiway = args[++i].ToUpperInvariant();
                    break;
                case "--parking":
                    showParking = true;
                    break;
                case "--spots":
                    showSpots = true;
                    break;
                case "--validate":
                    validate = true;
                    break;
                case "--intersection" when i + 2 < args.Length:
                    intersectTwy1 = args[++i].ToUpperInvariant();
                    intersectTwy2 = args[++i].ToUpperInvariant();
                    break;
                case "--json":
                    jsonOutput = true;
                    break;
                case "--dump":
                    dumpAll = true;
                    jsonOutput = true;
                    break;
                case "--no-fillets":
                    noFillets = true;
                    break;
                case "--debug-fillets":
                    debugFillets = true;
                    break;
                case "--svg" when i + 1 < args.Length:
                    svgOutput = args[++i];
                    break;
                case "--ticks" when i + 1 < args.Length:
                    ticksCsvPath = args[++i];
                    break;
                case "--svg-taxiway" when i + 1 < args.Length:
                    svgHighlightTaxiways.Add(args[++i].ToUpperInvariant());
                    break;
                case "--svg-runway" when i + 1 < args.Length:
                    svgHighlightRunways.Add(args[++i].ToUpperInvariant());
                    break;
                case "--svg-node" when i + 1 < args.Length:
                    svgHighlightNodes.Add(int.Parse(args[++i]));
                    break;
                case "--svg-annotate" when i + 2 < args.Length:
                    svgAnnotations.Add((int.Parse(args[++i]), args[++i]));
                    break;
                case "--svg-route" when i + 1 < args.Length:
                {
                    foreach (string nid in args[++i].Split(','))
                    {
                        svgRouteNodes.Add(int.Parse(nid.Trim()));
                    }

                    break;
                }
                case "--pathfinder" when i + 2 < args.Length:
                    pfNodeId = int.Parse(args[++i]);
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    {
                        pfTaxiways.Add(args[++i].ToUpperInvariant());
                    }

                    break;
                default:
                    Console.Error.WriteLine($"Unknown flag: {args[i]}");
                    PrintUsage();
                    return 2;
            }
        }

        TryLoadNavData(navdataDir);

        if (debugFillets)
        {
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddSimpleConsole(opts =>
                {
                    opts.SingleLine = true;
                    opts.IncludeScopes = false;
                });
                builder.AddFilter("FilletArcGenerator", LogLevel.Debug);
                builder.AddFilter("RunwayCrossingDetector", LogLevel.Debug);
                builder.SetMinimumLevel(LogLevel.Warning);
            });
            SimLog.Initialize(loggerFactory);
        }

        LayoutAnalyzer analyzer;
        try
        {
            analyzer = LayoutAnalyzer.Load(geoJsonPath, airportCode, applyFillets: !noFillets);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to parse GeoJSON: {ex.Message}");
            return 1;
        }

        // Run validation only when explicitly requested via --validate
        List<ValidationWarning> warnings = [];
        if (validate)
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

        // Resolve pathfinder route early so it can be used for HTML rendering
        Yaat.Sim.Data.Airport.TaxiRoute? pfRoute = null;
        string? pfFailReason = null;
        if ((pfNodeId is not null) && (pfTaxiways.Count > 0))
        {
            pfRoute = Yaat.Sim.Data.Airport.TaxiPathfinder.ResolveExplicitPath(analyzer.Layout, pfNodeId.Value, pfTaxiways, out pfFailReason);
        }

        if (svgOutput is not null)
        {
            var htmlRenderer = new HtmlRenderer(analyzer.Layout);
            foreach (string t in svgHighlightTaxiways)
            {
                htmlRenderer.HighlightTaxiway(t);
            }

            foreach (string r in svgHighlightRunways)
            {
                htmlRenderer.HighlightRunway(r);
            }

            foreach (int n in svgHighlightNodes)
            {
                htmlRenderer.HighlightNode(n);
            }

            foreach (var (nid, text) in svgAnnotations)
            {
                htmlRenderer.AnnotateNode(nid, text);
            }

            foreach (int nid in svgRouteNodes)
            {
                htmlRenderer.AddRouteNode(nid);
            }

            if (pfRoute is not null)
            {
                var routeNodeIds = new HashSet<int>();
                foreach (var seg in pfRoute.Segments)
                {
                    routeNodeIds.Add(seg.FromNodeId);
                    routeNodeIds.Add(seg.ToNodeId);
                }

                foreach (int nid in routeNodeIds)
                {
                    htmlRenderer.AddRouteNode(nid);
                }
            }

            if (ticksCsvPath is not null)
            {
                var ticks = LoadTicksCsv(ticksCsvPath);
                htmlRenderer.SetTickData(ticks);
                Console.Error.WriteLine($"Loaded {ticks.Count} ticks from {ticksCsvPath}");
            }

            string html = htmlRenderer.Render();
            File.WriteAllText(svgOutput, html);
            Console.Error.WriteLine($"Wrote interactive HTML to {svgOutput}");
            return 0;
        }

        if (dumpAll)
        {
            var dump = analyzer.GetFullDump();
            var json = System.Text.Json.JsonSerializer.Serialize(dump, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Console.Write(json);
            return 0;
        }

        IFormatter formatter = jsonOutput ? new JsonFormatter(Console.Out) : new TextFormatter(Console.Out);

        bool anyFilter =
            (taxiways.Count > 0)
            || (runways.Count > 0)
            || (nodeIds.Count > 0)
            || (exitsRunways.Count > 0)
            || (pathNodeId is not null)
            || (pfNodeId is not null)
            || showParking
            || showSpots
            || validate
            || (intersectTwy1 is not null);

        if (!anyFilter)
        {
            formatter.WriteOverview(analyzer.GetOverview());
        }

        foreach (string taxiway in taxiways)
        {
            formatter.WriteTaxiway(analyzer.GetTaxiwayDetail(taxiway));
        }

        foreach (string runway in runways)
        {
            formatter.WriteRunway(analyzer.GetRunwayDetail(runway));
        }

        foreach (int nodeId in nodeIds)
        {
            var node = analyzer.GetNodeDetail(nodeId);
            if (node is null)
            {
                Console.Error.WriteLine($"Node {nodeId} not found");
                return 1;
            }

            formatter.WriteNode(node);
        }

        foreach (string exitsRunway in exitsRunways)
        {
            formatter.WriteExits(analyzer.GetExits(exitsRunway));
        }

        if ((pathNodeId is not null) && (pathTaxiway is not null))
        {
            formatter.WriteBfsPath(analyzer.GetBfsPath(pathNodeId.Value, pathTaxiway));
        }

        if ((pfNodeId is not null) && (pfTaxiways.Count > 0))
        {
            Console.Out.WriteLine($"Pathfinder: from node {pfNodeId.Value}, taxiways [{string.Join(" ", pfTaxiways)}]");
            Console.Out.WriteLine();

            // Re-resolve with diagnostic logging for text output
            Yaat.Sim.Data.Airport.TaxiPathfinder.ResolveExplicitPath(
                analyzer.Layout,
                pfNodeId.Value,
                pfTaxiways,
                out string? _,
                diagnosticLog: msg => Console.Out.WriteLine(msg)
            );

            Console.Out.WriteLine();
            if (pfRoute is null)
            {
                Console.Out.WriteLine($"RESULT: no route (reason: {pfFailReason ?? "null"})");
            }
            else
            {
                Console.Out.WriteLine($"RESULT: {pfRoute.Segments.Count} segments");
                foreach (var seg in pfRoute.Segments)
                {
                    Console.Out.WriteLine($"  {seg.TaxiwayName}: {seg.FromNodeId} -> {seg.ToNodeId}");
                }
            }
        }

        if (showParking)
        {
            formatter.WriteNodeList("Parking", analyzer.GetParking());
        }

        if (showSpots)
        {
            formatter.WriteNodeList("Spots", analyzer.GetSpots());
        }

        if (intersectTwy1 is not null && intersectTwy2 is not null)
        {
            formatter.WriteIntersection(analyzer.GetIntersection(intersectTwy1, intersectTwy2));
        }

        if (validate)
        {
            var validationResult = new ValidationResult(
                warnings.Count,
                warnings.Select(w => new ValidationWarningDto(w.Code, w.Message, w.Origin)).ToList()
            );
            formatter.WriteValidation(validationResult);
        }

        return 0;
    }

    private static void TryLoadNavData(string? navdataDir)
    {
        navdataDir ??= FindDefaultNavDataDir();
        if (navdataDir is null)
        {
            Console.Error.WriteLine("Warning: NavData not found, using default runway widths (150ft)");
            return;
        }

        try
        {
            TestVnasData.SetTestDataDir(navdataDir);
            TestVnasData.EnsureInitialized();
            Console.Error.WriteLine($"Loaded NavData from {navdataDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to load NavData: {ex.Message}");
        }
    }

    private static string? FindDefaultNavDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "yaat.slnx")))
            {
                var testData = Path.Combine(dir.FullName, "tests", "Yaat.Sim.Tests", "TestData");
                return Directory.Exists(testData) ? testData : null;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: Yaat.LayoutInspector <geojson-path> [flags]");
        Console.WriteLine();
        Console.WriteLine("Flags:");
        Console.WriteLine("  --taxiway <name>         Show nodes/edges for a taxiway");
        Console.WriteLine("  --runway <designator>    Show centerline/hold-shorts for a runway");
        Console.WriteLine("  --node <id>              Show detail for a single node (repeatable)");
        Console.WriteLine("  --exits <designator>     Show all exits for a runway (BFS, repeatable)");
        Console.WriteLine("  --bfs <node-id> <twy>    BFS trace from node through taxiway to hold-short");
        Console.WriteLine("  --pathfinder <node-id> <twy1> [twy2 ...]  Resolve taxi route with diagnostic trace");
        Console.WriteLine("  --parking                Show all parking nodes");
        Console.WriteLine("  --spots                  Show all spot/named nodes");
        Console.WriteLine("  --intersection <T1> <T2> Show nodes where two taxiways meet");
        Console.WriteLine("  --validate               Run validation and print warnings to stdout");
        Console.WriteLine("  --no-fillets             Skip fillet arc generation (unfilleted graph for comparison)");
        Console.WriteLine("  --debug-fillets          Enable debug logging for FilletArcGenerator");
        Console.WriteLine("  --dump                   Dump everything (nodes, taxiways, runways, exits) as JSON");
        Console.WriteLine("  --json                   Output as JSON");
        Console.WriteLine("  --airport-code <ICAO>    Airport code for NavData runway widths");
        Console.WriteLine("  --navdata <dir>          Path to NavData.dat + FAACIFP18.gz directory");
        Console.WriteLine();
        Console.WriteLine("SVG output:");
        Console.WriteLine("  --svg <path>             Render full layout to SVG file");
        Console.WriteLine("  --svg-taxiway <name>     Highlight a taxiway (repeatable)");
        Console.WriteLine("  --svg-runway <desig>     Highlight a runway (repeatable)");
        Console.WriteLine("  --svg-node <id>          Highlight a node (repeatable)");
        Console.WriteLine("  --svg-annotate <id> <text>  Add annotation label to a node");
        Console.WriteLine("  --ticks <csv>            Overlay tick data (CSV from TickRecorder) with animation player");
    }

    private static List<TickDataRow> LoadTicksCsv(string path)
    {
        var rows = new List<TickDataRow>();
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2)
        {
            return rows;
        }

        var headers = lines[0].Split(',');
        int Col(string name) => Array.IndexOf(headers, name);

        int iT = Col("t"),
            iLat = Col("lat"),
            iLon = Col("lon"),
            iHdg = Col("hdg"),
            iGs = Col("gs");
        int iPhase = Col("phase"),
            iTwy = Col("twy");
        int iNavTarget = Col("navTarget"),
            iNavDist = Col("navDist"),
            iNavBrg = Col("navBrg");
        int iNavTargetSpd = Col("navTargetSpd"),
            iNavBrakeLimit = Col("navBrakeLimit");
        int iNavArcLimit = Col("navArcLimit"),
            iNavOnArc = Col("navOnArc"),
            iNavNodeReqSpd = Col("navNodeReqSpd");

        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            if (parts.Length < 7)
            {
                continue;
            }

            rows.Add(
                new TickDataRow(
                    Time: int.Parse(parts[iT]),
                    Lat: double.Parse(parts[iLat], System.Globalization.CultureInfo.InvariantCulture),
                    Lon: double.Parse(parts[iLon], System.Globalization.CultureInfo.InvariantCulture),
                    Hdg: double.Parse(parts[iHdg], System.Globalization.CultureInfo.InvariantCulture),
                    Gs: double.Parse(parts[iGs], System.Globalization.CultureInfo.InvariantCulture),
                    Phase: parts[iPhase],
                    Twy: parts[iTwy],
                    NavTarget: TryParseInt(parts, iNavTarget),
                    NavDist: TryParseDouble(parts, iNavDist),
                    NavBrg: TryParseDouble(parts, iNavBrg),
                    NavTargetSpd: TryParseDouble(parts, iNavTargetSpd),
                    NavBrakeLimit: TryParseDouble(parts, iNavBrakeLimit),
                    NavArcLimit: TryParseDouble(parts, iNavArcLimit),
                    NavOnArc: TryParseInt(parts, iNavOnArc) == 1,
                    NavNodeReqSpd: TryParseDouble(parts, iNavNodeReqSpd)
                )
            );
        }

        return rows;
    }

    private static int? TryParseInt(string[] parts, int idx)
    {
        if ((idx < 0) || (idx >= parts.Length) || string.IsNullOrEmpty(parts[idx]))
        {
            return null;
        }

        return int.TryParse(parts[idx], out int v) ? v : null;
    }

    private static double? TryParseDouble(string[] parts, int idx)
    {
        if ((idx < 0) || (idx >= parts.Length) || string.IsNullOrEmpty(parts[idx]))
        {
            return null;
        }

        return double.TryParse(parts[idx], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? v
            : null;
    }
}
