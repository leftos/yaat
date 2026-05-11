using Yaat.LayoutInspector.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.LayoutInspector;

/// <summary>
/// Entry point for the LayoutInspector CLI. Responsibilities:
///   1. Parse args via <see cref="CliOptions.TryParse"/>.
///   2. Bootstrap NavData and debug logging via <see cref="Bootstrap"/>.
///   3. Load the requested GeoJSON into a <see cref="LayoutAnalyzer"/>.
///   4. Dispatch to one <see cref="ICommand"/> based on which output mode the
///      options request.
/// Heavy lifting (arg parsing, rendering, queries) lives in dedicated files
/// under Commands/ and Tick/.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            UsageText.Print();
            return 2;
        }

        if (!CliOptions.TryParse(args, out var options, out string? parseError))
        {
            Console.Error.WriteLine(parseError);
            UsageText.Print();
            return 2;
        }

        string? geoJsonPath = options.GeoJsonPath;

        if (options.DownloadAirportId is { } downloadCode)
        {
            try
            {
                using var downloader = new AirportLayoutDownloader();
                geoJsonPath = downloader.GetGeoJsonAsync(downloadCode).GetAwaiter().GetResult() is null
                    ? null
                    : downloader.GetCachePath(downloadCode);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to download airport layout for {downloadCode}: {ex.Message}");
                return 1;
            }

            if (geoJsonPath is null)
            {
                Console.Error.WriteLine($"No airport layout available from vNAS for {downloadCode}");
                return 1;
            }

            Console.Error.WriteLine($"Loaded airport {downloadCode} from {geoJsonPath}");
        }

        if ((geoJsonPath is null) || !File.Exists(geoJsonPath))
        {
            Console.Error.WriteLine($"File not found: {geoJsonPath}");
            return 1;
        }

        Bootstrap.TryLoadNavData(options.NavDataDir);
        Bootstrap.ConfigureDebugLogging(options);

        LayoutAnalyzer analyzer;
        try
        {
            analyzer = LayoutAnalyzer.Load(geoJsonPath, options.AirportCode, applyFillets: !options.NoFillets);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to parse GeoJSON: {ex.Message}");
            return 1;
        }

        ICommand command = options switch
        {
            { HtmlOutputPath: not null } => new HtmlRenderCommand(),
            { DumpAll: true } => new DumpCommand(),
            { TickTable: true } or { TickSummary: true } => new TickTableCommand(),
            _ => new QueryCommand(),
        };

        return command.Execute(analyzer, options);
    }
}
