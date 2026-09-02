using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaat.Sim.Simulation.Oracle;

/// <summary>One accepted divergence: a normalized path, when it first appeared, and why it is still here.</summary>
public sealed class TickOracleBaselineEntry
{
    public required string Path { get; init; }

    /// <summary>Informational. Recorded for triage; never asserted — see <see cref="TickOracleBaseline"/>.</summary>
    public int FirstSecond { get; init; }

    /// <summary>The named cause, once triage has attributed it. Empty means "not yet triaged".</summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// The checked-in set of divergences a run kind pair is currently accepted to have, plus the run parameters that
/// produced it. Written once against today's divergent code and then shrunk deliberately, one entry at a time, as
/// the tick-path work retires each cause.
///
/// Two things are asserted and one is not. The <em>path set</em> is asserted exactly, so a new divergence and a
/// silently-fixed one both fail. <see cref="FirstDivergentSecond"/> is asserted as a floor, because it is a single
/// monotone-improving number. Per-entry <see cref="TickOracleBaselineEntry.FirstSecond"/> is <em>not</em> asserted:
/// a frozen weather timeline on one path cascades into positions, so any physics change shifts those seconds without
/// any divergence having been added or removed, and asserting them would fail the file for a non-finding.
/// </summary>
public sealed class TickOracleBaseline
{
    private static readonly JsonSerializerOptions FileOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public string Comparison { get; init; } = "";

    public string Scenario { get; init; } = "";

    public int Seed { get; init; }

    public int Seconds { get; init; }

    /// <summary>
    /// The earliest second the pair is accepted to disagree at; null means they are accepted to agree throughout.
    /// Metadata for a reader of the file: <see cref="CompareTo"/> derives the asserted value from the entries, so an
    /// exempt path cannot set the floor.
    /// </summary>
    public int? FirstDivergentSecond { get; init; }

    public List<TickOracleBaselineEntry> Entries { get; init; } = [];

    /// <summary>Reads a baseline file. A missing file is an empty baseline — the state a first run starts from.</summary>
    public static TickOracleBaseline Load(string filePath) =>
        File.Exists(filePath)
            ? JsonSerializer.Deserialize<TickOracleBaseline>(File.ReadAllText(filePath), FileOptions) ?? new TickOracleBaseline()
            : new TickOracleBaseline();

    /// <summary>Renders a sweep's result as the file's contents, preserving any notes already written against a path.</summary>
    public string Render(string comparison, string scenario, int seed, int seconds, DivergenceAccumulator accumulator)
    {
        var existingNotes = Entries.ToDictionary(e => e.Path, e => e.Note, StringComparer.Ordinal);
        var regenerated = new TickOracleBaseline
        {
            Comparison = comparison,
            Scenario = scenario,
            Seed = seed,
            Seconds = seconds,
            FirstDivergentSecond = accumulator.FirstDivergentSecond,
            Entries = accumulator
                .Entries.Select(e => new TickOracleBaselineEntry
                {
                    Path = e.Path,
                    FirstSecond = e.FirstSecond,
                    Note = existingNotes.GetValueOrDefault(e.Path, ""),
                })
                .ToList(),
        };

        return JsonSerializer.Serialize(regenerated, FileOptions) + Environment.NewLine;
    }

    /// <summary>
    /// Compares a sweep against this baseline. <paramref name="isExempt"/> drops permanently-accepted paths from both
    /// sides first, so an exemption never has to be mirrored into the generated file.
    /// </summary>
    public TickOracleComparison CompareTo(DivergenceAccumulator accumulator, Func<string, bool> isExempt)
    {
        var observedEntries = accumulator.Entries.Where(e => !isExempt(e.Path)).ToList();
        var acceptedEntries = Entries.Where(e => !isExempt(e.Path)).ToList();
        var observed = observedEntries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var accepted = acceptedEntries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);

        // Both onsets are derived from the filtered entries rather than read from the accumulator's or the file's
        // stored FirstDivergentSecond, which are unfiltered. An exempt path would otherwise set the floor for both
        // sides — reintroducing on this assertion exactly the noise the exemption removes from Added and Removed.
        return new TickOracleComparison(
            Added: observed.Except(accepted, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList(),
            Removed: accepted.Except(observed, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList(),
            ObservedFirstDivergentSecond: observedEntries.Select(e => (int?)e.FirstSecond).Min(),
            AcceptedFirstDivergentSecond: acceptedEntries.Select(e => (int?)e.FirstSecond).Min()
        );
    }
}

/// <summary>The verdict of one sweep against the baseline.</summary>
public sealed record TickOracleComparison(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    int? ObservedFirstDivergentSecond,
    int? AcceptedFirstDivergentSecond
)
{
    /// <summary>Divergence started earlier than accepted — the paths agreed for a shorter stretch than they used to.</summary>
    public bool FirstDivergenceRegressed =>
        ObservedFirstDivergentSecond is { } observed && (AcceptedFirstDivergentSecond is not { } accepted || observed < accepted);

    public bool IsClean => Added.Count == 0 && Removed.Count == 0 && !FirstDivergenceRegressed;

    /// <summary>A failure message that says what changed and what to do about it.</summary>
    public string Describe(string rebaselineVariable)
    {
        var message = new StringBuilder();
        message.AppendLine("The live and engine tick paths no longer diverge exactly as recorded in the oracle baseline.");

        if (Added.Count > 0)
        {
            message.AppendLine().AppendLine($"{Added.Count} NEW divergence path(s) — a step was added to one path and not the other:");
            foreach (var path in Added)
            {
                message.AppendLine($"  + {path}");
            }
        }

        if (Removed.Count > 0)
        {
            message.AppendLine().AppendLine($"{Removed.Count} divergence path(s) GONE — if that was the intent, re-baseline to bank it:");
            foreach (var path in Removed)
            {
                message.AppendLine($"  - {path}");
            }
        }

        if (FirstDivergenceRegressed)
        {
            message
                .AppendLine()
                .AppendLine(
                    $"First divergence moved earlier: second {ObservedFirstDivergentSecond} versus the accepted "
                        + $"{AcceptedFirstDivergentSecond?.ToString() ?? "never"}."
                );
        }

        message.AppendLine().AppendLine($"Re-baseline deliberately with {rebaselineVariable}=1, then review the file diff before committing it.");
        return message.ToString();
    }
}
