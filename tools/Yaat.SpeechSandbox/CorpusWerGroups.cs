namespace Yaat.SpeechSandbox;

/// <summary>
/// The rows a folder corpus's clips pool into beyond speaker role: per reference provenance, so the
/// owner-checked figure is reported beside the pooled one, and per feed, so one bad airport or
/// controller frequency cannot hide in the corpus total.
/// </summary>
public static class CorpusWerGroups
{
    /// <summary>
    /// The <c>owner</c> and <c>owner+agreed</c> rows, in that order, each pooling its segments' counts;
    /// null when no segment carries a provenance. A pool with no segments still gets its row, at a WER of 0.
    /// </summary>
    public static IReadOnlyList<CorpusWerRoleTotals>? ProvenanceRows(IReadOnlyList<(CorpusSegment Segment, WerCounts Counts)> scored)
    {
        if (scored.All(s => s.Segment.Provenance is null))
        {
            return null;
        }
        return
        [
            Tally("owner", scored.Where(s => s.Segment.Provenance == "owner")),
            Tally("owner+agreed", scored.Where(s => (s.Segment.Provenance == "owner") || (s.Segment.Provenance == "agreed"))),
        ];
    }

    /// <summary>
    /// One row per distinct feed in ordinal order, then the segments without a feed pooled last under
    /// <c>unknown</c>; null when no segment carries a feed.
    /// </summary>
    public static IReadOnlyList<CorpusWerRoleTotals>? FeedRows(IReadOnlyList<(CorpusSegment Segment, WerCounts Counts)> scored)
    {
        if (scored.All(s => s.Segment.Feed is null))
        {
            return null;
        }
        IEnumerable<string> feeds = scored
            .Where(s => s.Segment.Feed is not null)
            .Select(s => s.Segment.Feed!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var rows = new List<CorpusWerRoleTotals>();
        foreach (string feed in feeds)
        {
            rows.Add(Tally(feed, scored.Where(s => s.Segment.Feed == feed)));
        }
        if (scored.Any(s => s.Segment.Feed is null))
        {
            rows.Add(Tally("unknown", scored.Where(s => s.Segment.Feed is null)));
        }
        return rows;
    }

    /// <summary>The row <paramref name="label"/> pools <paramref name="scored"/> into.</summary>
    private static CorpusWerRoleTotals Tally(string label, IEnumerable<(CorpusSegment Segment, WerCounts Counts)> scored)
    {
        var tally = new WerTally();
        foreach ((_, WerCounts counts) in scored)
        {
            tally.Add(counts);
        }
        return CorpusWerRoleTotals.From(label, tally);
    }
}
