using Yaat.LayoutInspector.Tick;

namespace Yaat.LayoutInspector.Commands;

/// <summary>
/// Runs the tick-table output mode: reads a TickRecorder CSV and emits
/// either a per-tick compact table (--tick-table) or a per-segment summary
/// (--tick-summary) to stdout. Optional --tick-ref adds cross-track and
/// heading-error columns; optional --tick-hold-shorts adds along-track
/// distance columns to named hold-shorts.
/// </summary>
public sealed class TickTableCommand : ICommand
{
    public int Execute(LayoutAnalyzer analyzer, CliOptions options)
    {
        if (options.TicksCsvPath is null)
        {
            Console.Error.WriteLine("error: --tick-table and --tick-summary require --ticks <csv>");
            return 2;
        }

        if (!File.Exists(options.TicksCsvPath))
        {
            Console.Error.WriteLine($"error: {options.TicksCsvPath} not found");
            return 1;
        }

        RunwayReference? refLine = null;
        if (options.TickRefRunway is not null)
        {
            refLine = RunwayReference.Load(options.TickRefRunway);
            if (refLine is null)
            {
                return 1;
            }
        }

        var exitRefs = new List<ExitRef>();
        if (options.TickHoldShorts.Count > 0)
        {
            if (options.TickRefRunway is null)
            {
                Console.Error.WriteLine("error: --tick-hold-shorts requires --tick-ref to know which runway's hold-shorts to query");
                return 2;
            }

            var parts = options.TickRefRunway.Split('/');
            string rwy = parts[1].ToUpperInvariant();

            foreach (string twy in options.TickHoldShorts)
            {
                var nodes = HoldShortResolver.Find(analyzer.Layout, rwy, twy);
                if (nodes.Count == 0)
                {
                    Console.Error.WriteLine($"warn: no hold-short nodes found for runway {rwy} taxiway {twy}");
                    continue;
                }

                foreach (var n in nodes)
                {
                    Console.Error.WriteLine($"# exit {twy}: node #{n.Id} at ({n.Position.Lat:F6},{n.Position.Lon:F6})");
                }

                exitRefs.Add(new ExitRef(twy, nodes));
            }
        }

        var rows = TickCsvReader.Read(options.TicksCsvPath);
        if (options.TickRange is { } r)
        {
            rows = rows.Where(x => x.Time >= r.Lo && x.Time <= r.Hi).ToList();
        }

        if (rows.Count == 0)
        {
            Console.Error.WriteLine("error: no matching rows in csv");
            return 1;
        }

        if (options.TickSummary)
        {
            TickTableFormatter.PrintSummary(rows, refLine);
        }
        else
        {
            TickTableFormatter.PrintTable(rows, refLine, exitRefs);
        }

        return 0;
    }
}
