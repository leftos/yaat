using System.Text.Json;
using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// Text-only regression corpus promoted from opted-in user telemetry by
/// <c>tools/speech_telemetry.py promote</c>. One row per promoted case: the transcript a real user
/// spoke, the canonical command the rule mapper should produce for it, and the scenario context the
/// pipeline had at capture time (active callsigns, programmed fixes, runways by airport, taxiways,
/// destinations). Audio, CIDs, session files and timestamps never enter the file, which is why the
/// corpus travels with the repo.
///
/// Each row maps with the production text pipeline — callsign extraction then
/// <see cref="PhraseologyCommandMapper"/>, the same call <c>tools/Yaat.SpeechSandbox</c> makes when
/// it verifies a synthetic corpus label — so a rule regression shows up as the exact transcript
/// that broke. No LLM mapper and no STT stage: the corpus pins the deterministic half of the
/// pipeline.
/// </summary>
public sealed class TelemetryTranscriptRegressionTests
{
    private static readonly string CorpusPath = Path.Combine(
        AppContext.BaseDirectory,
        "TestData",
        "speech-transcripts",
        "telemetry-regressions.json"
    );

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoRunways = new Dictionary<string, IReadOnlyList<string>>(
        StringComparer.OrdinalIgnoreCase
    );

    private static readonly IReadOnlyDictionary<string, string> NoDestinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>One promoted case. Lists are optional in JSON; a hand-written row may omit them.</summary>
    public sealed record Entry(
        string Name,
        string Transcript,
        string Canonical,
        string? Callsign,
        IReadOnlyList<string>? ActiveCallsigns,
        IReadOnlyList<string>? ProgrammedFixes,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? AvailableRunwaysByAirport,
        IReadOnlyList<string>? TaxiwayNames,
        IReadOnlyDictionary<string, string>? AircraftDestinations
    );

    /// <summary>What the mapping pipeline produced for one entry, plus whether it matched the label.</summary>
    internal sealed record Mapped(string? Canonical, string? Callsign, bool CanonicalMatches, bool CallsignMatches);

    /// <summary>
    /// In-code rows that must map, used to prove the harness bites. Both are rule-engine hits with a
    /// phonetic callsign, so they hold without the LLM fallback and without navdata (the client test
    /// project carries none — see <c>NaturalCommandNormalizerTests</c>).
    /// </summary>
    private static readonly Entry[] KnownGoodEntries =
    [
        new(
            "in-code-sw-123-descent",
            "southwest one two three descend and maintain five thousand",
            "DM 5000",
            "SWA123",
            ["SWA123"],
            [],
            NoRunways,
            [],
            NoDestinations
        ),
        new(
            "in-code-ual-234-climb",
            "united two three four climb and maintain one zero thousand",
            "CM 10000",
            "UAL234",
            ["UAL234"],
            [],
            NoRunways,
            [],
            NoDestinations
        ),
    ];

    private static List<Entry> LoadEntries()
    {
        if (!File.Exists(CorpusPath))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(CorpusPath), JsonOptions) ?? [];
    }

    /// <summary>One theory row per promoted entry, displayed under the entry's own name.</summary>
    public static IEnumerable<TheoryDataRow<Entry>> Rows() =>
        LoadEntries().Select(entry => new TheoryDataRow<Entry>(entry) { TestDisplayName = entry.Name });

    [Fact]
    public void CorpusParsesAndEveryEntryCarriesItsLabels()
    {
        Assert.True(File.Exists(CorpusPath), $"missing corpus file: {CorpusPath}");

        List<Entry> entries = LoadEntries();
        foreach (Entry entry in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name), "an entry has no name");
            Assert.False(string.IsNullOrWhiteSpace(entry.Transcript), $"'{entry.Name}' has no transcript");
            Assert.False(string.IsNullOrWhiteSpace(entry.Canonical), $"'{entry.Name}' has no canonical");
        }
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(Rows))]
    public async Task PromotedTranscriptMapsToItsCanonical(Entry entry)
    {
        Mapped mapped = await MapAsync(entry);

        Assert.True(mapped.CanonicalMatches, $"\"{entry.Transcript}\" mapped to \"{mapped.Canonical ?? "<null>"}\", expected \"{entry.Canonical}\"");
        Assert.True(
            mapped.CallsignMatches,
            $"\"{entry.Transcript}\" extracted callsign \"{mapped.Callsign ?? "<null>"}\", expected \"{entry.Callsign ?? "(any)"}\""
        );
    }

    /// <summary>
    /// Proves the two assertions above can fail: the known-good rows match, and the same rows with a
    /// deliberately wrong canonical or callsign do not.
    /// </summary>
    [Fact]
    public async Task HarnessBites_WhenALabelIsWrong()
    {
        foreach (Entry entry in KnownGoodEntries)
        {
            Mapped mapped = await MapAsync(entry);
            Assert.True(
                mapped is { CanonicalMatches: true, CallsignMatches: true },
                $"\"{entry.Transcript}\" mapped to \"{mapped.Canonical ?? "<null>"}\" / \"{mapped.Callsign ?? "<null>"}\""
            );
        }

        Mapped wrongCanonical = await MapAsync(KnownGoodEntries[0] with { Canonical = "CM 5000" });
        Assert.False(wrongCanonical.CanonicalMatches);
        Assert.Equal("DM 5000", wrongCanonical.Canonical);

        Mapped wrongCallsign = await MapAsync(KnownGoodEntries[0] with { Callsign = "SWA999" });
        Assert.True(wrongCallsign.CanonicalMatches);
        Assert.False(wrongCallsign.CallsignMatches);
    }

    /// <summary>
    /// Runs one entry through the production text pipeline and reports both the result and whether
    /// it matched the entry's labels — the comparison is a value, not an exception, so a test can
    /// assert on a mismatch directly.
    /// </summary>
    internal static async Task<Mapped> MapAsync(Entry entry)
    {
        // Production builds SpeechContext from live simulation state (MainViewModel.BuildSpeechContext);
        // the promoted entry carries the same state, so every field it has goes back in.
        var context = new SpeechContext(entry.ActiveCallsigns ?? [], entry.ProgrammedFixes ?? [], WhisperBiasingPrompt.Default)
        {
            AvailableRunways = ToRunways(entry.AvailableRunwaysByAirport),
            AircraftDestinations = new Dictionary<string, string>(entry.AircraftDestinations ?? NoDestinations, StringComparer.OrdinalIgnoreCase),
            TaxiwayNames = new HashSet<string>(entry.TaxiwayNames ?? [], StringComparer.OrdinalIgnoreCase),
        };

        TranscriptMapResult mapped = await SpeechRecognitionService.MapTranscriptAsync(
            entry.Transcript,
            context,
            new PhraseologyCommandMapper(),
            llmMapper: null,
            callsignResolver: null,
            CancellationToken.None
        );

        return new Mapped(
            mapped.Canonical,
            mapped.Callsign,
            string.Equals(mapped.Canonical, entry.Canonical, StringComparison.OrdinalIgnoreCase),
            entry.Callsign is null || string.Equals(mapped.Callsign, entry.Callsign, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ToRunways(IReadOnlyDictionary<string, IReadOnlyList<string>>? source)
    {
        var runways = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return runways;
        }

        foreach ((string airport, IReadOnlyList<string> designators) in source)
        {
            runways[airport] = designators;
        }

        return runways;
    }
}
