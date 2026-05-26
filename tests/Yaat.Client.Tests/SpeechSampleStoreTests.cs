using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for <see cref="SpeechSampleStore"/> — the disk-backed ring buffer that holds opt-in
/// speech samples. Covers basic CRUD, FIFO eviction when the MB cap is breached, rescan
/// idempotency, and round-trip integrity of an exported <c>.yaat-speech-sample.zip</c>.
/// Uses an explicit temp directory rather than YaatPaths so tests don't depend on global env.
/// </summary>
public sealed class SpeechSampleStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-sst-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException) { }
        }
    }

    private static SpeechSession MakeSession(DateTime ts, string transcript = "test transcript", string? canonical = "FH 270")
    {
        var trace = new SpeechSessionTrace(
            WhisperBiasingPrompt: "callsigns: N123AB",
            RawTranscript: transcript,
            CallsignStrippedTranscript: transcript,
            CallsignExtracted: null,
            Rule: new RuleMapperTrace("fly heading 270", null, ["fly heading {hdg}"], canonical, null),
            Llm: null,
            ActiveCallsigns: ["N123AB"],
            ProgrammedFixes: ["KOAK"],
            AvailableRunwaysByAirport: new Dictionary<string, IReadOnlyList<string>> { ["KOAK"] = new[] { "28R", "28L" } },
            TaxiwayNames: ["A", "B"],
            AircraftDestinations: new Dictionary<string, string> { ["N123AB"] = "KOAK" }
        );
        return new SpeechSession(
            TimestampUtc: ts,
            SampleCount: 16000,
            AudioDurationSeconds: 1.0,
            Transcript: transcript,
            CanonicalCommand: canonical,
            UsedLlmFallback: false,
            TranscribeElapsedMs: 200,
            MapElapsedMs: 5,
            TotalElapsedMs: 210,
            Outcome: canonical is null ? SpeechSessionOutcome.NoMappingFound : SpeechSessionOutcome.CommandAccepted,
            ErrorMessage: null
        )
        {
            Trace = trace,
        };
    }

    /// <summary>Synthesize N seconds of silence at 16 kHz to size-tune the on-disk WAV per test.</summary>
    private static float[] MakeAudio(double seconds) => new float[(int)(seconds * 16000)];

    private static UserPreferences MakePrefs(int capMb)
    {
        var prefs = new UserPreferences();
        prefs.SetSpeechSampleSettings(enabled: true, maxMb: capMb);
        return prefs;
    }

    [Fact]
    public void Add_Persists_Audio_And_Session_Json()
    {
        var store = new SpeechSampleStore(MakePrefs(50), _root);
        var id = store.Add(MakeSession(DateTime.UtcNow), MakeAudio(0.5));
        Assert.NotNull(id);
        Assert.Single(store.Entries);

        var folder = Path.Combine(_root, id!);
        Assert.True(File.Exists(Path.Combine(folder, "audio.wav")), "audio.wav should be written");
        Assert.True(File.Exists(Path.Combine(folder, "session.json")), "session.json should be written");

        var entry = store.Entries[0];
        Assert.Equal(id, entry.Id);
        Assert.Equal(id, entry.Session.SampleId);
        Assert.True(entry.TotalBytes > 0);
    }

    [Fact]
    public void Add_Rejects_Empty_Audio_Buffer()
    {
        var store = new SpeechSampleStore(MakePrefs(50), _root);
        var id = store.Add(MakeSession(DateTime.UtcNow), audioSamples: []);
        Assert.Null(id);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void Eviction_Drops_Oldest_Folders_When_Cap_Exceeded()
    {
        // 1 MB cap is the floor we can hit reliably with short WAVs. Each 5-second WAV at 16 kHz
        // mono int16 is ~160 KB → headroom for ~6 entries before eviction kicks in. We add 10 with
        // monotonically increasing timestamps and expect the oldest to be evicted first.
        var store = new SpeechSampleStore(MakePrefs(capMb: 1), _root);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 10; i++)
        {
            store.Add(MakeSession(baseTime.AddSeconds(i)), MakeAudio(5));
        }

        Assert.True(store.Entries.Count < 10, "Some entries should have been evicted under the 1 MB cap.");
        Assert.True(store.TotalBytes <= store.MaxBytes, $"Total bytes {store.TotalBytes} must stay under cap {store.MaxBytes}.");

        // Surviving entries should be the newest contiguous suffix — verify by timestamp ordering.
        var survivors = store.Entries.Select(e => e.Session.TimestampUtc).ToList();
        Assert.Equal(survivors.OrderByDescending(t => t).ToList(), survivors);
        Assert.Equal(baseTime.AddSeconds(9), survivors[0]);
    }

    [Fact]
    public void Delete_Removes_Single_Entry_And_Folder()
    {
        var store = new SpeechSampleStore(MakePrefs(50), _root);
        var id1 = store.Add(MakeSession(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), MakeAudio(0.5));
        var id2 = store.Add(MakeSession(new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc)), MakeAudio(0.5));
        Assert.Equal(2, store.Entries.Count);

        store.Delete(id1!);
        Assert.Single(store.Entries);
        Assert.False(Directory.Exists(Path.Combine(_root, id1!)));
        Assert.True(Directory.Exists(Path.Combine(_root, id2!)));
    }

    [Fact]
    public void DeleteAll_Removes_Every_Entry()
    {
        var store = new SpeechSampleStore(MakePrefs(50), _root);
        for (var i = 0; i < 3; i++)
        {
            store.Add(MakeSession(DateTime.UtcNow.AddSeconds(i)), MakeAudio(0.2));
        }
        Assert.Equal(3, store.Entries.Count);
        store.DeleteAll();
        Assert.Empty(store.Entries);
        // Root may still exist as an empty dir; per-sample folders should all be gone.
        if (Directory.Exists(_root))
        {
            Assert.Empty(Directory.EnumerateDirectories(_root));
        }
    }

    [Fact]
    public void Rescan_Repopulates_From_Disk()
    {
        var prefs = MakePrefs(50);
        var first = new SpeechSampleStore(prefs, _root);
        first.Add(MakeSession(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), MakeAudio(0.3));
        first.Add(MakeSession(new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc)), MakeAudio(0.3));

        var second = new SpeechSampleStore(prefs, _root);
        Assert.Equal(2, second.Entries.Count);
        // Newest first.
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc), second.Entries[0].Session.TimestampUtc);
    }

    [Fact]
    public void ExportBundle_Single_Sample_Produces_Zip_With_Subfolder_Layout()
    {
        var store = new SpeechSampleStore(MakePrefs(50), _root);
        var id = store.Add(MakeSession(DateTime.UtcNow), MakeAudio(0.5));
        Assert.NotNull(id);

        var zipPath = Path.Combine(_root, "export.zip");
        var written = store.ExportBundle([id!], zipPath);
        Assert.Equal(1, written);
        Assert.True(File.Exists(zipPath));

        using var fs = File.OpenRead(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("manifest.json", names);
        Assert.Contains($"samples/{id}/audio.wav", names);
        Assert.Contains($"samples/{id}/session.json", names);

        var manifestEntry = zip.GetEntry("manifest.json")!;
        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<JsonDocument>(manifestStream);
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        var samples = manifest.RootElement.GetProperty("samples").EnumerateArray().ToList();
        Assert.Single(samples);
        Assert.Equal(id, samples[0].GetProperty("id").GetString());
        Assert.Equal("CommandAccepted", samples[0].GetProperty("outcome").GetString());

        var sessionEntry = zip.GetEntry($"samples/{id}/session.json")!;
        using var sessionStream = sessionEntry.Open();
        var jsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };
        var sessionRoundTrip = JsonSerializer.Deserialize<SpeechSession>(sessionStream, jsonOpts);
        Assert.NotNull(sessionRoundTrip);
        Assert.Equal(id, sessionRoundTrip!.SampleId);
        Assert.Equal("FH 270", sessionRoundTrip.CanonicalCommand);
        Assert.NotNull(sessionRoundTrip.Trace);
        Assert.Equal("callsigns: N123AB", sessionRoundTrip.Trace!.WhisperBiasingPrompt);
    }

    [Fact]
    public void ExportBundle_Multiple_Samples_Contains_All_Subfolders()
    {
        var store = new SpeechSampleStore(MakePrefs(50), _root);
        var id1 = store.Add(MakeSession(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), MakeAudio(0.3))!;
        var id2 = store.Add(MakeSession(new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc), canonical: null), MakeAudio(0.3))!;
        var id3 = store.Add(MakeSession(new DateTime(2026, 1, 1, 0, 0, 2, DateTimeKind.Utc)), MakeAudio(0.3))!;

        var zipPath = Path.Combine(_root, "bundle.zip");
        // Pick two of three; the third must not appear in the zip.
        var written = store.ExportBundle([id1, id3], zipPath);
        Assert.Equal(2, written);

        using var fs = File.OpenRead(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains($"samples/{id1}/audio.wav", names);
        Assert.Contains($"samples/{id3}/audio.wav", names);
        Assert.DoesNotContain($"samples/{id2}/audio.wav", names);

        var manifest = JsonSerializer.Deserialize<JsonDocument>(zip.GetEntry("manifest.json")!.Open())!;
        var sampleIds = manifest.RootElement.GetProperty("samples").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { id1, id3 }, sampleIds);
    }

    [Fact]
    public void ExportBundle_Skips_Unknown_Ids_Without_Writing_File()
    {
        var store = new SpeechSampleStore(MakePrefs(50), _root);
        var zipPath = Path.Combine(_root, "noop.zip");
        var written = store.ExportBundle(["does-not-exist", "also-missing"], zipPath);
        Assert.Equal(0, written);
        Assert.False(File.Exists(zipPath));
    }
}
