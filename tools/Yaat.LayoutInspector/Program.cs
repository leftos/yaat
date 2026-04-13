using Yaat.LayoutInspector.Commands;

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

        if (!File.Exists(options.GeoJsonPath))
        {
            Console.Error.WriteLine($"File not found: {options.GeoJsonPath}");
            return 1;
        }

        Bootstrap.TryLoadNavData(options.NavDataDir);
        Bootstrap.ConfigureDebugLogging(options);

        LayoutAnalyzer analyzer;
        try
        {
            analyzer = LayoutAnalyzer.Load(options.GeoJsonPath, options.AirportCode, applyFillets: !options.NoFillets);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to parse GeoJSON: {ex.Message}");
            return 1;
        }

        ICommand command = options switch
        {
            { SvgOutputPath: not null } => new HtmlRenderCommand(),
            { DumpAll: true } => new DumpCommand(),
            { TickTable: true } or { TickSummary: true } => new TickTableCommand(),
            _ => new QueryCommand(),
        };

        return command.Execute(analyzer, options);
    }
}
