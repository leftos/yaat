using System.Diagnostics;
using System.Text;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

public sealed record FilletRunResult(
    string GeneratorId,
    FilletStatistics Stats,
    int NodeCount,
    int EdgeCount,
    int ArcCount,
    IReadOnlyDictionary<GroundNodeType, int> NodeCountByType,
    long ElapsedMs
);

public sealed record FilletComparisonReport(
    IReadOnlyList<FilletRunResult> Runs,
    int ArcCountDelta,
    int NodeCountDelta,
    bool ConnectivityMatch,
    FilletComparisonGateReport Gates,
    string Summary
);

/// <summary>
/// Runs each <see cref="IFilletArcGenerator"/> on an independent deep clone of
/// <paramref name="preFilletLayout"/> and compares structural metrics.
/// </summary>
public static class FilletComparison
{
    public static FilletComparisonReport Compare(AirportGroundLayout preFilletLayout, IReadOnlyList<IFilletArcGenerator> generators)
    {
        if (generators.Count == 0)
        {
            throw new ArgumentException("At least one generator is required.", nameof(generators));
        }

        var runs = new List<FilletRunResult>(generators.Count);
        var gatesByGeneratorId = new Dictionary<string, FilletGateResults>(StringComparer.OrdinalIgnoreCase);

        foreach (var generator in generators)
        {
            var layout = LayoutCloner.DeepClone(preFilletLayout);
            var sw = Stopwatch.StartNew();
            var stats = generator.Apply(layout);
            sw.Stop();

            gatesByGeneratorId[generator.Id] = FilletComparisonGates.Evaluate(preFilletLayout, layout, stats);
            runs.Add(
                new FilletRunResult(
                    generator.Id,
                    stats,
                    layout.Nodes.Count,
                    layout.Edges.Count,
                    layout.Arcs.Count,
                    CountNodesByType(layout),
                    sw.ElapsedMilliseconds
                )
            );
        }

        int minArcs = runs.Min(r => r.ArcCount);
        int maxArcs = runs.Max(r => r.ArcCount);
        int minNodes = runs.Min(r => r.NodeCount);
        int maxNodes = runs.Max(r => r.NodeCount);

        var gateReport = FilletComparisonGates.CompareGenerators(gatesByGeneratorId);
        bool connectivityMatch = gateReport.HoldShortConnectivityMatch;

        string summary =
            runs.Count == 1
                ? $"{runs[0].GeneratorId}: {runs[0].ArcCount} arcs, {runs[0].NodeCount} nodes"
                : string.Join("; ", runs.Select(r => $"{r.GeneratorId}: {r.ArcCount} arcs, {r.NodeCount} nodes"));

        return new FilletComparisonReport(runs, maxArcs - minArcs, maxNodes - minNodes, connectivityMatch, gateReport, summary);
    }

    public static string FormatReport(FilletComparisonReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(report.Summary);
        sb.AppendLine($"  Arc count delta: {report.ArcCountDelta}");
        sb.AppendLine($"  Node count delta: {report.NodeCountDelta}");
        FilletComparisonGates.AppendGateReport(sb, report);
        foreach (var run in report.Runs)
        {
            sb.AppendLine(
                $"  [{run.GeneratorId}] arcs={run.ArcCount} edges={run.EdgeCount} nodes={run.NodeCount} "
                    + $"created={run.Stats.ArcsCreated} ms={run.ElapsedMs}"
            );
        }

        return sb.ToString().TrimEnd();
    }

    public static bool V2MeetsHardGates(FilletComparisonReport report)
    {
        var v2Run = report.Runs.FirstOrDefault(r => r.GeneratorId == "v2");
        if (v2Run is null || !report.Gates.GatesByGeneratorId.TryGetValue("v2", out var v2Gates))
        {
            return false;
        }

        return v2Gates.Structural.IsValid
            && v2Gates.RepairCountersZero
            && report.Gates.HoldShortConnectivityMatch
            && report.Gates.ParkingConnectivityMatch;
    }

    private static Dictionary<GroundNodeType, int> CountNodesByType(AirportGroundLayout layout)
    {
        var counts = new Dictionary<GroundNodeType, int>();
        foreach (var node in layout.Nodes.Values)
        {
            counts.TryGetValue(node.Type, out int n);
            counts[node.Type] = n + 1;
        }

        return counts;
    }
}
