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

        Console.WriteLine($"Layout inspector: {geoJsonPath}");
        return 0;
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
