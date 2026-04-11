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
        string? taxiway = null;
        string? runway = null;
        int? nodeId = null;
        string? exitsRunway = null;
        int? pathNodeId = null;
        string? pathTaxiway = null;
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
                    taxiway = args[++i].ToUpperInvariant();
                    break;
                case "--runway" when i + 1 < args.Length:
                    runway = args[++i].ToUpperInvariant();
                    break;
                case "--node" when i + 1 < args.Length:
                    nodeId = int.Parse(args[++i]);
                    break;
                case "--exits" when i + 1 < args.Length:
                    exitsRunway = args[++i].ToUpperInvariant();
                    break;
                case "--path" when i + 2 < args.Length:
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

        // Always run validation and print warnings to stderr
        var validator = new LayoutValidator(analyzer.Layout);
        var warnings = validator.Validate();
        if (warnings.Count > 0)
        {
            Console.Error.WriteLine($"VALIDATION: {warnings.Count} warning(s):");
            foreach (var w in warnings)
            {
                Console.Error.WriteLine($"  [{w.Code}] {w.Message}{(w.Origin is not null ? $" (origin: {w.Origin})" : "")}");
            }

            Console.Error.WriteLine();
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
            (taxiway is not null)
            || (runway is not null)
            || (nodeId is not null)
            || (exitsRunway is not null)
            || (pathNodeId is not null)
            || showParking
            || showSpots
            || validate
            || (intersectTwy1 is not null);

        if (!anyFilter)
        {
            formatter.WriteOverview(analyzer.GetOverview());
        }

        if (taxiway is not null)
        {
            formatter.WriteTaxiway(analyzer.GetTaxiwayDetail(taxiway));
        }

        if (runway is not null)
        {
            formatter.WriteRunway(analyzer.GetRunwayDetail(runway));
        }

        if (nodeId is not null)
        {
            var node = analyzer.GetNodeDetail(nodeId.Value);
            if (node is null)
            {
                Console.Error.WriteLine($"Node {nodeId} not found");
                return 1;
            }

            formatter.WriteNode(node);
        }

        if (exitsRunway is not null)
        {
            formatter.WriteExits(analyzer.GetExits(exitsRunway));
        }

        if ((pathNodeId is not null) && (pathTaxiway is not null))
        {
            formatter.WriteBfsPath(analyzer.GetBfsPath(pathNodeId.Value, pathTaxiway));
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
        Console.WriteLine("  --node <id>              Show detail for a single node");
        Console.WriteLine("  --exits <designator>     Show all exits for a runway (BFS)");
        Console.WriteLine("  --path <node-id> <twy>   BFS trace from node through taxiway to hold-short");
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
    }
}
