using Yaat.Sim.Speech.Context;

namespace Yaat.SpeechSandbox;

/// <summary>One scored clip: its segment (id, feed, provenance), its gold slots and the decoder's result for it.</summary>
/// <param name="Segment">The clip's segment, which carries the feed and provenance its group rows pool by.</param>
/// <param name="Gold">The clip's gold slots.</param>
/// <param name="Result">The decoder's output for the clip.</param>
public sealed record CorpusSlotClip(CorpusSegment Segment, IReadOnlyList<CorpusSlot> Gold, DecodeResult Result);

/// <summary>One kind's counts in one group. Recall is Recovered / Gold, 0 when Gold is 0.</summary>
/// <param name="Kind">The kind's label, or <c>all</c> for the row pooling every kind.</param>
/// <param name="Gold">How many gold slots of the kind the group's clips hold.</param>
/// <param name="Recovered">How many of them the decoder recovered.</param>
/// <param name="Inserted">How many entities of the kind the decoder output that the gold does not hold.</param>
/// <param name="Recall">Recovered / Gold, 0 when Gold is 0.</param>
/// <param name="InsertedPer100Clips">Inserted per hundred clips of the group, 0 when the group has none.</param>
public sealed record CorpusSlotKindRow(string Kind, int Gold, int Recovered, int Inserted, double Recall, double InsertedPer100Clips);

/// <summary>
/// One group's rows: the <c>all</c> kind row first, then one row per <see cref="EntityKind"/> in enum order.
/// CallsignClips: clips with at least one gold callsign. WrongAddressee: clips whose resolved callsign is not
/// among the clip's gold callsigns. SayAgain / NoCallsign: clips with at least one gold callsign whose result
/// state is SayAgain / NoCallsign.
/// </summary>
/// <param name="Group">The pool's label: <c>all</c>, <c>owner</c>, <c>owner+agreed</c>, a feed, or <c>unknown</c>.</param>
/// <param name="Clips">How many clips the pool holds.</param>
/// <param name="Kinds">The pool's rows, the <c>all</c> row first.</param>
/// <param name="CallsignClips">How many of the pool's clips hold at least one gold callsign.</param>
/// <param name="WrongAddressee">How many resolved a callsign the pool's clip does not gold.</param>
/// <param name="SayAgain">How many hold a gold callsign and settled on a say-again.</param>
/// <param name="NoCallsign">How many hold a gold callsign and settled on no callsign at all.</param>
public sealed record CorpusSlotGroup(
    string Group,
    int Clips,
    IReadOnlyList<CorpusSlotKindRow> Kinds,
    int CallsignClips,
    int WrongAddressee,
    int SayAgain,
    int NoCallsign
);

/// <summary>
/// Scores a context decoder's entity output against a corpus folder's gold slots: per kind, how much of the
/// gold the decoder recovered and how much it invented, pooled overall and by provenance and feed.
/// </summary>
public static class CorpusSlotScorer
{
    /// <summary>The label of the row that pools every kind, and of the group that pools every clip.</summary>
    private const string AllLabel = "all";

    /// <summary>
    /// One group per pool of clips, in this order: every clip as <c>all</c>; the <c>owner</c> and
    /// <c>owner+agreed</c> pools only when a clip carries a provenance; then one pool per feed in ordinal
    /// order and <c>unknown</c> for the clips without one, only when a clip carries a feed. A pool with no
    /// clips still gets its rows, all zeros.
    /// </summary>
    public static IReadOnlyList<CorpusSlotGroup> Score(IReadOnlyList<CorpusSlotClip> clips)
    {
        var groups = new List<CorpusSlotGroup>();
        foreach ((string label, Func<CorpusSlotClip, bool> includes) in GroupDefs(clips))
        {
            groups.Add(ScoreGroup(label, [.. clips.Where(includes)]));
        }
        return groups;
    }

    private static List<(string Label, Func<CorpusSlotClip, bool> Includes)> GroupDefs(IReadOnlyList<CorpusSlotClip> clips)
    {
        var groups = new List<(string Label, Func<CorpusSlotClip, bool> Includes)> { (AllLabel, _ => true) };
        AddProvenanceGroups(clips, groups);
        AddFeedGroups(clips, groups);
        return groups;
    }

    /// <summary>Adds the provenance pools, exactly the ones <see cref="CorpusWerGroups.ProvenanceRows"/> reports.</summary>
    private static void AddProvenanceGroups(IReadOnlyList<CorpusSlotClip> clips, List<(string Label, Func<CorpusSlotClip, bool> Includes)> groups)
    {
        if (clips.All(clip => clip.Segment.Provenance is null))
        {
            return;
        }
        groups.Add(("owner", clip => clip.Segment.Provenance == "owner"));
        groups.Add(("owner+agreed", clip => (clip.Segment.Provenance == "owner") || (clip.Segment.Provenance == "agreed")));
    }

    /// <summary>
    /// Adds one pool per feed in ordinal order and <c>unknown</c> last, exactly the ones <see cref="CorpusWerGroups.FeedRows"/>
    /// reports.
    /// </summary>
    private static void AddFeedGroups(IReadOnlyList<CorpusSlotClip> clips, List<(string Label, Func<CorpusSlotClip, bool> Includes)> groups)
    {
        if (clips.All(clip => clip.Segment.Feed is null))
        {
            return;
        }
        IEnumerable<string> feeds = clips
            .Where(clip => clip.Segment.Feed is not null)
            .Select(clip => clip.Segment.Feed!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (string feed in feeds)
        {
            groups.Add((feed, clip => clip.Segment.Feed == feed));
        }
        if (clips.Any(clip => clip.Segment.Feed is null))
        {
            groups.Add(("unknown", clip => clip.Segment.Feed is null));
        }
    }

    private static CorpusSlotGroup ScoreGroup(string label, IReadOnlyList<CorpusSlotClip> clips)
    {
        SlotCounts[] tallies = [.. Enum.GetValues<EntityKind>().Select(_ => new SlotCounts())];
        foreach (CorpusSlotClip clip in clips)
        {
            AddClip(clip, tallies);
        }
        int clipCount = clips.Count;
        var rows = new List<CorpusSlotKindRow>(tallies.Length + 1) { SumRow(AllLabel, tallies, clipCount) };
        foreach (EntityKind kind in Enum.GetValues<EntityKind>())
        {
            SlotCounts counts = tallies[(int)kind];
            rows.Add(Row(CorpusSlots.KindLabel(kind), counts.Gold, counts.Recovered, counts.Inserted, clipCount));
        }
        return new CorpusSlotGroup(
            label,
            clipCount,
            rows,
            clips.Count(HasGoldCallsign),
            clips.Count(IsWrongAddressee),
            clips.Count(clip => StateWithGoldCallsign(clip, DecodeState.SayAgain)),
            clips.Count(clip => StateWithGoldCallsign(clip, DecodeState.NoCallsign))
        );
    }

    /// <summary>Adds one clip's gold and output counts, kinds apart, into the group's tallies.</summary>
    private static void AddClip(CorpusSlotClip clip, SlotCounts[] tallies)
    {
        Dictionary<(EntityKind Kind, string Value), int> gold = Multiset(clip.Gold.Select(slot => (slot.Kind, slot.Value)));
        Dictionary<(EntityKind Kind, string Value), int> output = Multiset(OutputSlots(clip.Result));
        foreach (KeyValuePair<(EntityKind Kind, string Value), int> entry in gold)
        {
            SlotCounts counts = tallies[(int)entry.Key.Kind];
            counts.Gold += entry.Value;
            counts.Recovered += Math.Min(entry.Value, output.GetValueOrDefault(entry.Key));
        }
        foreach (KeyValuePair<(EntityKind Kind, string Value), int> entry in output)
        {
            tallies[(int)entry.Key.Kind].Inserted += Math.Max(0, entry.Value - gold.GetValueOrDefault(entry.Key));
        }
    }

    /// <summary>
    /// A clip's output slots: its resolved callsign (never the say-again's best guess), then every entity
    /// the decoder matched in the remainder, traffic callsigns among them.
    /// </summary>
    private static IEnumerable<(EntityKind Kind, string Value)> OutputSlots(DecodeResult result)
    {
        EntityMatch? callsign = result.Callsign;
        if ((result.State == DecodeState.Resolved) && (callsign is not null))
        {
            yield return (callsign.Entity.Kind, callsign.Entity.Value);
        }
        foreach (EntityMatch match in result.Entities)
        {
            yield return (match.Entity.Kind, match.Entity.Value);
        }
    }

    /// <summary>Slots counted per value: the kind exactly, the value ignoring case.</summary>
    private static Dictionary<(EntityKind Kind, string Value), int> Multiset(IEnumerable<(EntityKind Kind, string Value)> slots)
    {
        var counts = new Dictionary<(EntityKind Kind, string Value), int>(SlotKeyComparer.Instance);
        foreach ((EntityKind Kind, string Value) slot in slots)
        {
            counts[slot] = counts.GetValueOrDefault(slot) + 1;
        }
        return counts;
    }

    private static CorpusSlotKindRow SumRow(string label, SlotCounts[] tallies, int clips) =>
        Row(label, tallies.Sum(counts => counts.Gold), tallies.Sum(counts => counts.Recovered), tallies.Sum(counts => counts.Inserted), clips);

    private static CorpusSlotKindRow Row(string kind, int gold, int recovered, int inserted, int clips) =>
        new(kind, gold, recovered, inserted, (gold == 0) ? 0 : recovered / (double)gold, (clips == 0) ? 0 : inserted * 100.0 / clips);

    private static bool HasGoldCallsign(CorpusSlotClip clip) => clip.Gold.Any(slot => slot.Kind == EntityKind.Callsign);

    private static bool StateWithGoldCallsign(CorpusSlotClip clip, DecodeState state) => (clip.Result.State == state) && HasGoldCallsign(clip);

    private static bool GoldNamesCallsign(CorpusSlotClip clip, string callsign) =>
        clip.Gold.Any(slot => (slot.Kind == EntityKind.Callsign) && StringComparer.OrdinalIgnoreCase.Equals(slot.Value, callsign));

    private static bool IsWrongAddressee(CorpusSlotClip clip)
    {
        EntityMatch? callsign = clip.Result.Callsign;
        if ((clip.Result.State != DecodeState.Resolved) || (callsign is null))
        {
            return false;
        }
        return !GoldNamesCallsign(clip, callsign.Entity.Value);
    }

    /// <summary>One kind's raw counts inside one group, before they become a row.</summary>
    private sealed class SlotCounts
    {
        public int Gold { get; set; }

        public int Recovered { get; set; }

        public int Inserted { get; set; }
    }

    /// <summary>Multiset keys: the kind exactly, the value ignoring case.</summary>
    private sealed class SlotKeyComparer : IEqualityComparer<(EntityKind Kind, string Value)>
    {
        public static readonly SlotKeyComparer Instance = new();

        public bool Equals((EntityKind Kind, string Value) x, (EntityKind Kind, string Value) y) =>
            (x.Kind == y.Kind) && StringComparer.OrdinalIgnoreCase.Equals(x.Value, y.Value);

        public int GetHashCode((EntityKind Kind, string Value) obj) =>
            HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value));
    }
}
