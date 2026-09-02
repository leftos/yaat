namespace Yaat.Sim.Simulation.Oracle;

/// <summary>
/// One normalized path that diverged during a sweep, the second it first did, and a few concrete instances.
/// </summary>
public sealed record DivergenceSummaryEntry(string Path, int FirstSecond, IReadOnlyList<string> Examples);

/// <summary>
/// Folds a per-second stream of <see cref="SnapshotDivergence"/> into a summary keyed by normalized path.
///
/// Normalizing is what keeps the summary bounded. A divergence that removes an aircraft from one run cascades into
/// every field of every later second for that aircraft; keyed by concrete path the result grows with seconds ×
/// aircraft × fields, keyed by normalized path it is bounded by the snapshot's field count and stays a list a person
/// can read and retire entry by entry.
/// </summary>
public sealed class DivergenceAccumulator
{
    /// <summary>Concrete instances kept per path — enough to recognise the shape, not enough to bloat the report.</summary>
    public const int MaxExamplesPerPath = 3;

    private readonly Dictionary<string, Entry> _byPath = new(StringComparer.Ordinal);

    /// <summary>The earliest second at which the two runs disagreed about anything, or null if they never did.</summary>
    public int? FirstDivergentSecond { get; private set; }

    /// <summary>Every normalized path that diverged, ordered by path.</summary>
    public IReadOnlyList<DivergenceSummaryEntry> Entries =>
        _byPath
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new DivergenceSummaryEntry(pair.Key, pair.Value.FirstSecond, pair.Value.Examples))
            .ToList();

    /// <summary>Folds one second's divergences in. Safe to call with an empty list.</summary>
    public void Add(int elapsedSeconds, IReadOnlyList<SnapshotDivergence> divergences)
    {
        if (divergences.Count == 0)
        {
            return;
        }

        FirstDivergentSecond ??= elapsedSeconds;

        foreach (var divergence in divergences)
        {
            string normalized = DivergencePath.Normalize(divergence.Path);
            if (!_byPath.TryGetValue(normalized, out var entry))
            {
                entry = new Entry(elapsedSeconds);
                _byPath[normalized] = entry;
            }

            if (entry.Examples.Count < MaxExamplesPerPath)
            {
                entry.Examples.Add($"t={elapsedSeconds} {divergence.Path}: live={divergence.Left} test={divergence.Right}");
            }
        }
    }

    private sealed class Entry(int firstSecond)
    {
        public int FirstSecond { get; } = firstSecond;

        public List<string> Examples { get; } = [];
    }
}
