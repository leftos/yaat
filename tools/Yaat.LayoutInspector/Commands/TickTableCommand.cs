using Yaat.LayoutInspector.Tick;
using Yaat.Sim.Data.Airport;

namespace Yaat.LayoutInspector.Commands;

/// <summary>
/// Runs the tick-table output mode: reads a TickRecorder JSON recording and emits
/// either a per-tick compact table (--tick-table) or a per-segment summary
/// (--tick-summary) to stdout. Optional --tick-ref adds cross-track and
/// heading-error columns; optional --tick-hold-shorts adds along-track
/// distance columns to named hold-shorts.
/// </summary>
public sealed class TickTableCommand : ICommand
{
    public int Execute(LayoutAnalyzer analyzer, CliOptions options)
    {
        if (options.TickSources.Count == 0)
        {
            Console.Error.WriteLine("error: --tick-table and --tick-summary require --ticks <json>");
            return 2;
        }

        TickSourceRead read = TickRecordingLoader.ReadAll(options.TickSources);
        foreach (TickSourceProblem problem in read.Problems)
        {
            Console.Error.WriteLine(
                problem.Issue == TickSourceIssue.FileNotFound ? $"error: {problem.Path} not found" : $"error: {problem.Path} is empty or unreadable"
            );
        }

        if (read.Problems.Count > 0)
        {
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

            string[] parts = options.TickRefRunway.Split('/');
            string rwy = parts[1].ToUpperInvariant();

            foreach (string twy in options.TickHoldShorts)
            {
                List<GroundNode> nodes = HoldShortResolver.Find(analyzer.Layout, rwy, twy);
                if (nodes.Count == 0)
                {
                    Console.Error.WriteLine($"warn: no hold-short nodes found for runway {rwy} taxiway {twy}");
                    continue;
                }

                foreach (GroundNode n in nodes)
                {
                    Console.Error.WriteLine($"# exit {twy}: node #{n.Id} at ({n.Position.Lat:F6},{n.Position.Lon:F6})");
                }

                exitRefs.Add(new ExitRef(twy, nodes));
            }
        }

        TickRecording recording;
        try
        {
            recording = TickRecordingMerger.Merge(read.Loaded);
        }
        catch (TickMergeException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        var allRows = recording.Ticks.Select(TickDataRow.From).ToList();
        if (options.TickRange is { } r)
        {
            allRows = [.. allRows.Where(x => x.Time >= r.Lo && x.Time <= r.Hi)];
        }

        if (allRows.Count == 0)
        {
            Console.Error.WriteLine("error: no matching rows in recording");
            return 1;
        }

        // Multi-aircraft handling: filter to one callsign or print one block per callsign.
        IEnumerable<string> callsigns;
        if (options.TickCallsign is not null)
        {
            callsigns = [options.TickCallsign];
        }
        else
        {
            callsigns = [.. allRows.Select(x => x.Callsign).Distinct().OrderBy(x => x, StringComparer.Ordinal)];
        }

        bool first = true;
        foreach (string cs in callsigns)
        {
            var rows = allRows.Where(x => string.Equals(x.Callsign, cs, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            if (!first)
            {
                Console.WriteLine();
            }

            Console.WriteLine($"# === {cs} ({rows.Count} rows) ===");
            first = false;

            if (options.TickSummary)
            {
                TickTableFormatter.PrintSummary(rows, refLine);
            }
            else
            {
                TickTableFormatter.PrintTable(rows, refLine, exitRefs);
            }
        }

        return 0;
    }
}
