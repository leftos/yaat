using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// A census of how every committed recording's commands route on replay today, checked in as
/// <c>TestData/recording-routing-census.json</c> and asserted byte-for-byte. It is the triage worklist for the
/// action-router work (tick-path step 3d): when a routing change makes a fixture replay differently, this fails on
/// exactly the fixtures affected, before any replay E2E does — and names the mechanism (a global command that used
/// to no-op, a reaction delay reconstruction used to skip, a solo room whose scoring replay used to drop).
///
/// <para>
/// Per fixture it records the command count by <see cref="RecordedCommandKind"/>, the commands whose recorded text
/// the current grammar no longer parses (a retired canonical the schema upgrader has not been run over — these
/// replay as an aircraft-scoped chain and are dropped), how many commands carry a baked reaction delay, how many
/// carry an <c>AS</c> prefix, and whether the room was in solo-training mode. Regenerate with
/// <c>YAAT_ROUTING_CENSUS_REGENERATE=1</c>; the run fails anyway so the diff is read and committed deliberately, the
/// tick oracle's re-baseline discipline.
/// </para>
/// </summary>
public class RecordingCorpusRoutingCensusTests(ITestOutputHelper output)
{
    private const string RegenerateVariable = "YAAT_ROUTING_CENSUS_REGENERATE";
    private const string CensusFileName = "recording-routing-census.json";

    private static readonly string TestDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record FixtureCensus(
        string File,
        int Commands,
        SortedDictionary<string, int> ByKind,
        List<string> Unparsed,
        int ReactionDelayed,
        int AsPrefixed,
        bool Solo
    );

    [Fact]
    public void Corpus_RoutesExactlyAsTheCensusRecords()
    {
        var census = new List<FixtureCensus>();
        foreach (var path in Directory.GetFiles(TestDataDir, "*.zip").OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            var entry = Census(path);
            if (entry is not null)
            {
                census.Add(entry);
            }
        }

        Assert.True(census.Count > 0, $"No recordings found under {TestDataDir}");
        // Written with a final newline so the pre-commit EOF hook never rewrites the checked-in file behind the test's back.
        string actual = JsonSerializer.Serialize(census, FileOptions) + "\n";

        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            string target = Path.Combine(FindRepoRoot(), "tests", "Yaat.Sim.Tests", "TestData", CensusFileName);
            File.WriteAllText(target, actual);
            Assert.Fail($"Regenerated {target} ({census.Count} fixtures). Review the diff, then unset {RegenerateVariable}.");
        }

        string censusPath = Path.Combine(TestDataDir, CensusFileName);
        string expected = File.Exists(censusPath) ? File.ReadAllText(censusPath) : "[]";
        if (expected.TrimEnd() == actual.TrimEnd())
        {
            return;
        }

        var expectedByFile = (JsonSerializer.Deserialize<List<FixtureCensus>>(expected, FileOptions) ?? []).ToDictionary(c => c.File);
        var changed = new List<string>();
        foreach (var entry in census)
        {
            if (!expectedByFile.TryGetValue(entry.File, out var was))
            {
                changed.Add($"{entry.File}: new fixture");
            }
            else if (JsonSerializer.Serialize(was, FileOptions) != JsonSerializer.Serialize(entry, FileOptions))
            {
                changed.Add($"{entry.File}: routing changed");
            }
        }

        foreach (var gone in expectedByFile.Keys.Except(census.Select(c => c.File)))
        {
            changed.Add($"{gone}: fixture removed");
        }

        string report = Path.Combine(FindRepoRoot(), ".tmp", "recording-routing-census.actual.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, actual);
        Assert.Fail(
            $"The recording corpus routes differently from {CensusFileName}:{Environment.NewLine}  "
                + string.Join(Environment.NewLine + "  ", changed)
                + $"{Environment.NewLine}Actual census written to {report}. Triage each fixture to a named cause, then {RegenerateVariable}=1."
        );
    }

    private FixtureCensus? Census(string path)
    {
        SessionRecording? recording;
        try
        {
            recording = RecordingLoader.Load(path);
        }
        catch (InvalidDataException)
        {
            output.WriteLine($"SKIP: {Path.GetFileName(path)} is not a zip (Git LFS pointer stub?)");
            return null;
        }

        if (recording is null)
        {
            output.WriteLine($"SKIP: {Path.GetFileName(path)} is not a recording");
            return null;
        }

        var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var unparsed = new List<string>();
        int commands = 0;
        int reactionDelayed = 0;
        int asPrefixed = 0;
        bool solo = false;
        foreach (var action in recording.Actions)
        {
            switch (action)
            {
                case RecordedCommand cmd:
                    commands++;
                    var (remainder, asTcp) = TrackResolver.ExtractAsPrefix(cmd.Command);
                    var classification = RecordedCommandClassifier.Classify(remainder);
                    byKind[classification.Kind.ToString()] = byKind.GetValueOrDefault(classification.Kind.ToString()) + 1;
                    // The single-command parser rejects every multi-verb chain by design; only a body the compound
                    // parser rejects too is text the current grammar no longer accepts.
                    if (classification.Parsed is null && !CommandParser.ParseCompound(remainder).IsSuccess)
                    {
                        unparsed.Add(cmd.Command);
                    }

                    if (cmd.ReactionDelaySeconds is not null)
                    {
                        reactionDelayed++;
                    }

                    if (asTcp is not null)
                    {
                        asPrefixed++;
                    }

                    break;
                case RecordedSettingChange { Setting: "SoloTrainingMode" } setting when bool.TryParse(setting.Value, out var on) && on:
                    solo = true;
                    break;
            }
        }

        return new FixtureCensus(Path.GetFileName(path), commands, byKind, unparsed, reactionDelayed, asPrefixed, solo);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "yaat.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
