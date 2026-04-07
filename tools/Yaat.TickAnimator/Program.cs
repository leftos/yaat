using SkiaSharp;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Testing;

namespace Yaat.TickAnimator;

/// <summary>
/// Renders an animated GIF of aircraft movement over an airport ground layout.
///
/// Usage:
///   dotnet run --project tools/Yaat.TickAnimator -- [options]
///
/// Required:
///   --layout &lt;geojson&gt;    Airport GeoJSON file
///   --ticks &lt;csv&gt;         Tick data CSV (columns: t,lat,lon,hdg,gs,phase,twy)
///
/// Optional:
///   --output &lt;path&gt;       Output file (default: .tmp/ticks.gif)
///   --aircraft &lt;type&gt;     ICAO aircraft type for dimensions (default: B738)
///   --padding &lt;nm&gt;        Padding around tick bounds in nm (default: 0.05)
///   --width &lt;px&gt;          Frame width in pixels (default: 800)
///   --fps &lt;n&gt;             Frames per second (default: 10)
///   --trail &lt;n&gt;           Number of trail dots to show (default: 30)
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts is null)
        {
            PrintUsage();
            return 1;
        }

        TryLoadNavData();

        // Load airport layout
        string geoJson = File.ReadAllText(opts.LayoutPath);
        string airportId = Path.GetFileNameWithoutExtension(opts.LayoutPath).ToUpperInvariant();
        var layout = GeoJsonParser.Parse(airportId, geoJson, airportId);
        Console.Error.WriteLine($"Loaded layout: {airportId}, {layout.Nodes.Count} nodes, {layout.Edges.Count} edges");

        // Load tick data, filter by --start/--end
        var ticks = LoadTicks(opts.TicksPath);
        if (opts.StartTick is not null)
        {
            ticks = ticks.Where(t => t.Time >= opts.StartTick.Value).ToList();
        }

        if (opts.EndTick is not null)
        {
            ticks = ticks.Where(t => t.Time <= opts.EndTick.Value).ToList();
        }

        Console.Error.WriteLine($"Loaded {ticks.Count} ticks");
        if (ticks.Count == 0)
        {
            Console.Error.WriteLine("Error: no tick data");
            return 1;
        }

        // Aircraft dimensions
        var acRecord = FaaAircraftDatabase.Get(opts.AircraftType);
        double lengthFt = acRecord?.LengthFt ?? 130;
        double wingspanFt = acRecord?.WingspanFt ?? 110;
        Console.Error.WriteLine($"Aircraft: {opts.AircraftType}, length={lengthFt:F0}ft, wingspan={wingspanFt:F0}ft");

        // Render
        var renderer = new FrameRenderer(layout, ticks, lengthFt, wingspanFt, opts);
        renderer.RenderGif();

        Console.Error.WriteLine($"Saved to {opts.OutputPath}");
        return 0;
    }

    private static List<TickRecord> LoadTicks(string path)
    {
        var ticks = new List<TickRecord>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("t,") || line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split(',');
            if (parts.Length < 5)
            {
                continue;
            }

            ticks.Add(
                new TickRecord
                {
                    Time = int.Parse(parts[0].Trim()),
                    Lat = double.Parse(parts[1].Trim()),
                    Lon = double.Parse(parts[2].Trim()),
                    Hdg = double.Parse(parts[3].Trim()),
                    Gs = double.Parse(parts[4].Trim()),
                    Phase = parts.Length > 5 ? parts[5].Trim() : "",
                    Twy = parts.Length > 6 ? parts[6].Trim() : "",
                }
            );
        }

        return ticks;
    }

    private static Options? ParseArgs(string[] args)
    {
        string? layoutPath = null;
        string? ticksPath = null;
        string outputPath = ".tmp/ticks.gif";
        string aircraftType = "B738";
        double paddingNm = 0.05;
        int width = 800;
        int fps = 10;
        int trail = 30;
        int? startTick = null;
        int? endTick = null;
        bool fitLayout = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--layout" when i + 1 < args.Length:
                    layoutPath = args[++i];
                    break;
                case "--ticks" when i + 1 < args.Length:
                    ticksPath = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    outputPath = args[++i];
                    break;
                case "--aircraft" when i + 1 < args.Length:
                    aircraftType = args[++i];
                    break;
                case "--padding" when i + 1 < args.Length:
                    paddingNm = double.Parse(args[++i]);
                    break;
                case "--width" when i + 1 < args.Length:
                    width = int.Parse(args[++i]);
                    break;
                case "--fps" when i + 1 < args.Length:
                    fps = int.Parse(args[++i]);
                    break;
                case "--trail" when i + 1 < args.Length:
                    trail = int.Parse(args[++i]);
                    break;
                case "--start" when i + 1 < args.Length:
                    startTick = int.Parse(args[++i]);
                    break;
                case "--end" when i + 1 < args.Length:
                    endTick = int.Parse(args[++i]);
                    break;
                case "--fit-layout":
                    fitLayout = true;
                    break;
                case "--help" or "-h":
                    return null;
            }
        }

        if (layoutPath is null || ticksPath is null)
        {
            return null;
        }

        return new Options
        {
            LayoutPath = layoutPath,
            TicksPath = ticksPath,
            OutputPath = outputPath,
            AircraftType = aircraftType,
            PaddingNm = paddingNm,
            Width = width,
            Fps = fps,
            TrailLength = trail,
            StartTick = startTick,
            EndTick = endTick,
            FitLayout = fitLayout,
        };
    }

    private static void TryLoadNavData()
    {
        string? navdataDir = FindDefaultNavDataDir();
        if (navdataDir is null)
        {
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
        Console.WriteLine("Usage: dotnet run --project tools/Yaat.TickAnimator -- [options]");
        Console.WriteLine();
        Console.WriteLine("Required:");
        Console.WriteLine("  --layout <geojson>    Airport GeoJSON file");
        Console.WriteLine("  --ticks <csv>         Tick data CSV (t,lat,lon,hdg,gs,phase,twy)");
        Console.WriteLine();
        Console.WriteLine("Optional:");
        Console.WriteLine("  --output <path>       Output file (default: .tmp/ticks.gif)");
        Console.WriteLine("  --aircraft <type>     ICAO aircraft type (default: B738)");
        Console.WriteLine("  --padding <nm>        Padding around tick bounds in nm (default: 0.05)");
        Console.WriteLine("  --width <px>          Frame width in pixels (default: 800)");
        Console.WriteLine("  --fps <n>             Frames per second (default: 10)");
        Console.WriteLine("  --trail <n>           Trail dot count (default: 30)");
        Console.WriteLine("  --start <t>           Start at tick t (viewport fits filtered ticks)");
        Console.WriteLine("  --end <t>             End at tick t");
        Console.WriteLine("  --fit-layout          Fit viewport to entire airport layout");
    }
}

internal sealed class Options
{
    public required string LayoutPath { get; init; }
    public required string TicksPath { get; init; }
    public required string OutputPath { get; init; }
    public required string AircraftType { get; init; }
    public required double PaddingNm { get; init; }
    public required int Width { get; init; }
    public required int Fps { get; init; }
    public required int TrailLength { get; init; }
    public int? StartTick { get; init; }
    public int? EndTick { get; init; }
    public bool FitLayout { get; init; }
}

internal sealed class TickRecord
{
    public required int Time { get; init; }
    public required double Lat { get; init; }
    public required double Lon { get; init; }
    public required double Hdg { get; init; }
    public required double Gs { get; init; }
    public required string Phase { get; init; }
    public required string Twy { get; init; }
}
