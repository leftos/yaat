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
        bool jsonOutput = false;

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
                case "--json":
                    jsonOutput = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown flag: {args[i]}");
                    PrintUsage();
                    return 2;
            }
        }

        TryLoadNavData(navdataDir);

        LayoutAnalyzer analyzer;
        try
        {
            analyzer = LayoutAnalyzer.Load(geoJsonPath, airportCode);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to parse GeoJSON: {ex.Message}");
            return 1;
        }

        IFormatter formatter = jsonOutput ? new JsonFormatter(Console.Out) : new TextFormatter(Console.Out);

        bool anyFilter =
            (taxiway is not null)
            || (runway is not null)
            || (nodeId is not null)
            || (exitsRunway is not null)
            || (pathNodeId is not null)
            || showParking
            || showSpots;

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
        Console.WriteLine("  --json                   Output as JSON");
        Console.WriteLine("  --airport-code <ICAO>    Airport code for NavData runway widths");
        Console.WriteLine("  --navdata <dir>          Path to NavData.dat + FAACIFP18.gz directory");
    }
}
