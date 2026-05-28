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
        var filletedReachability = new List<HashSet<int>>(generators.Count);
        foreach (var generator in generators)
        {
            var layout = LayoutCloner.DeepClone(preFilletLayout);
            var sw = Stopwatch.StartNew();
            var stats = generator.Apply(layout);
            sw.Stop();

            filletedReachability.Add(ReachableFromHoldShorts(layout));
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

        bool connectivityMatch = runs.Count <= 1 || filletedReachability.All(set => set.SetEquals(filletedReachability[0]));

        string summary =
            runs.Count == 1
                ? $"{runs[0].GeneratorId}: {runs[0].ArcCount} arcs, {runs[0].NodeCount} nodes"
                : string.Join("; ", runs.Select(r => $"{r.GeneratorId}: {r.ArcCount} arcs, {r.NodeCount} nodes"));

        return new FilletComparisonReport(runs, maxArcs - minArcs, maxNodes - minNodes, connectivityMatch, summary);
    }

    public static string FormatReport(FilletComparisonReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(report.Summary);
        sb.AppendLine($"  Arc count delta: {report.ArcCountDelta}");
        sb.AppendLine($"  Node count delta: {report.NodeCountDelta}");
        sb.AppendLine($"  Hold-short reachability match: {report.ConnectivityMatch}");
        foreach (var run in report.Runs)
        {
            sb.AppendLine(
                $"  [{run.GeneratorId}] arcs={run.ArcCount} edges={run.EdgeCount} nodes={run.NodeCount} "
                    + $"created={run.Stats.ArcsCreated} ms={run.ElapsedMs}"
            );
        }

        return sb.ToString().TrimEnd();
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

    private static HashSet<int> ReachableFromHoldShorts(AirportGroundLayout layout)
    {
        var seeds = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort).Select(n => n.Id).ToList();
        if (seeds.Count == 0)
        {
            return layout.Nodes.Keys.ToHashSet();
        }

        var reachable = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (int seed in seeds)
        {
            if (reachable.Add(seed))
            {
                queue.Enqueue(seed);
            }
        }

        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            if (!layout.Nodes.TryGetValue(id, out var node))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                int otherId = edge.OtherNodeId(id);
                if (reachable.Add(otherId))
                {
                    queue.Enqueue(otherId);
                }
            }
        }

        return reachable;
    }
}
