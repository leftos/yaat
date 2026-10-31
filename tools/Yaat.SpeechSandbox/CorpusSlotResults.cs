using System.Globalization;
using System.Text;
using Yaat.Sim.Speech.Context;

namespace Yaat.SpeechSandbox;

/// <summary>One kind's form coverage summed over an arm's clips under one room.</summary>
/// <param name="Kind">The kind's label, as <see cref="CorpusSlots.KindLabel"/> gives it.</param>
/// <param name="Slots">The gold slots of that kind.</param>
/// <param name="Reachable">Those whose entity the room holds.</param>
/// <param name="FormMatched">Those reachable whose entity has a form that tokenizes as the slot's spoken words do.</param>
public sealed record CorpusSlotCoverageRow(string Kind, int Slots, int Reachable, int FormMatched);

/// <summary>One room's slot results for an arm. Rooms appear in the order unpadded, padded, oracle.</summary>
/// <param name="Room"><c>unpadded</c>, <c>padded</c> or <c>oracle</c>.</param>
/// <param name="Groups"><see cref="CorpusSlotScorer.Score"/> over the arm's clips.</param>
/// <param name="Coverage">One row per entity kind, summed over clips.</param>
/// <param name="ResolvedWer">The word error rate of the decoder's rewritten transcripts; null unless <c>--resolve</c>.</param>
/// <param name="ConfidenceGated">Clips whose chosen hypothesis had the log-probability gate on.</param>
public sealed record CorpusSlotRoomResult(
    string Room,
    IReadOnlyList<CorpusSlotGroup> Groups,
    IReadOnlyList<CorpusSlotCoverageRow> Coverage,
    IReadOnlyList<CorpusWerRoleTotals>? ResolvedWer,
    int ConfidenceGated
);

/// <summary>One clip's decode under one room, for <c>results.jsonl</c>.</summary>
/// <param name="Room"><c>unpadded</c>, <c>padded</c> or <c>oracle</c>.</param>
/// <param name="State"><c>resolved</c>, <c>sayagain</c> or <c>nocallsign</c>.</param>
/// <param name="Callsign">The resolved callsign; null when none.</param>
/// <param name="BestGuess">The say-again's best guess; null when none.</param>
/// <param name="Entities">Each matched entity as <c>&lt;kind label&gt;:&lt;value&gt;</c>, in result order.</param>
/// <param name="Rewritten">The transcript with the accepted spans rewritten.</param>
/// <param name="HypothesisIndex">The hypothesis the answer came from.</param>
/// <param name="JointScore">The winning hypothesis's joint score.</param>
/// <param name="ConfidenceGated">Whether the log-probability gate applied.</param>
public sealed record CorpusSlotDecodeLine(
    string Room,
    string State,
    string? Callsign,
    string? BestGuess,
    IReadOnlyList<string> Entities,
    string Rewritten,
    int HypothesisIndex,
    double JointScore,
    bool ConfidenceGated
);

/// <summary>What one clip's slot work adds to its <c>results.jsonl</c> line.</summary>
/// <param name="HotwordPhrases">The clip's room-hotword count; null without <c>--room-hotwords</c>.</param>
/// <param name="Lines">The clip's decode under each room, in room order.</param>
public sealed record CorpusSlotClipLines(int? HotwordPhrases, IReadOnlyList<CorpusSlotDecodeLine> Lines);

/// <summary>The console lines and <c>report.md</c> sections of a <c>--slots</c> run.</summary>
public static class CorpusSlotReport
{
    private static readonly string[] KindLabels = [.. Enum.GetValues<EntityKind>().Select(CorpusSlots.KindLabel)];

    /// <summary>Prints one line per room of an arm that scored slots; nothing for one that did not.</summary>
    public static void WriteConsole(CorpusWerArm arm)
    {
        foreach (CorpusSlotRoomResult room in arm.Slots ?? [])
        {
            Console.WriteLine(ConsoleLine(arm.Prompt, room));
        }
    }

    /// <summary>A room's console line, read from its <c>all</c> group.</summary>
    public static string ConsoleLine(string prompt, CorpusSlotRoomResult room)
    {
        CorpusSlotGroup all = room.Groups[0];
        CorpusSlotKindRow total = all.Kinds[0];
        string callsign = CorpusWerBaselineComparer.Percent(KindRow(all, CorpusSlots.KindLabel(EntityKind.Callsign)).Recall);
        string recall = CorpusWerBaselineComparer.Percent(total.Recall);
        string inserted = total.InsertedPer100Clips.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{prompt} slots {room.Room}: callsign recall {callsign}, all recall {recall}, inserted {inserted}/100 clips, wrong addressee {all.WrongAddressee}";
    }

    /// <summary>The report line naming the callsigns no entity was built for; nothing when there are none.</summary>
    public static void AppendUnbuilt(StringBuilder report, IReadOnlyList<string>? unbuilt)
    {
        if (unbuilt is { Count: > 0 })
        {
            report.AppendLine($"- Callsigns with no entity: {string.Join("; ", unbuilt)}");
        }
    }

    /// <summary>The arm's slot sections, one per room; nothing for an arm that did not score slots.</summary>
    public static void Append(StringBuilder report, CorpusWerArm arm)
    {
        foreach (CorpusSlotRoomResult room in arm.Slots ?? [])
        {
            AppendRoom(report, room);
        }
    }

    private static void AppendRoom(StringBuilder report, CorpusSlotRoomResult room)
    {
        report.AppendLine();
        report.AppendLine($"### Slots: {room.Room}");
        report.AppendLine();
        report.AppendLine($"| Group | Clips | {string.Join(" | ", KindLabels)} | all | Inserted/100 | Wrong addressee | Say again | No callsign |");
        report.AppendLine($"|{string.Concat(Enumerable.Repeat("---|", KindLabels.Length + 7))}");
        foreach (CorpusSlotGroup group in room.Groups)
        {
            report.AppendLine(GroupRow(group));
        }
        report.AppendLine();
        report.AppendLine(CoverageLine(room.Coverage));
        if (room.ResolvedWer is not null)
        {
            report.AppendLine();
            CorpusWerRunner.AppendTable(report, "Resolved", room.ResolvedWer);
        }
        report.AppendLine();
        report.AppendLine($"Confidence gate on for {room.ConfidenceGated} of {room.Groups[0].Clips} clips");
    }

    private static string GroupRow(CorpusSlotGroup group)
    {
        CorpusSlotKindRow total = group.Kinds[0];
        string kinds = string.Join(" | ", KindLabels.Select(label => Cell(KindRow(group, label))));
        string inserted = total.InsertedPer100Clips.ToString("0.0", CultureInfo.InvariantCulture);
        string counts = $"{inserted} | {group.WrongAddressee} | {group.SayAgain} | {group.NoCallsign}";
        return $"| {group.Group} | {group.Clips} | {kinds} | {Cell(total)} | {counts} |";
    }

    private static string Cell(CorpusSlotKindRow row) =>
        (row.Gold == 0) ? "-" : $"{CorpusWerBaselineComparer.Percent(row.Recall)} ({row.Recovered}/{row.Gold})";

    private static CorpusSlotKindRow KindRow(CorpusSlotGroup group, string label) =>
        group.Kinds.FirstOrDefault(row => row.Kind == label) ?? throw new InvalidDataException($"Slot group '{group.Group}' has no '{label}' row.");

    private static string CoverageLine(IReadOnlyList<CorpusSlotCoverageRow> coverage)
    {
        string[] kinds = [.. coverage.Where(row => row.Slots > 0).Select(row => $"{row.Kind} {row.FormMatched}/{row.Reachable}/{row.Slots}")];
        return $"Form coverage (matched/reachable/gold): {((kinds.Length == 0) ? "none" : string.Join(", ", kinds))}";
    }
}
