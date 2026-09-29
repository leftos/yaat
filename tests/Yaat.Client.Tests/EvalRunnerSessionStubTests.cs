using System.Text.Json;
using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Speech;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// <see cref="EvalRunner.LoadOrStubExpectation"/> turns a captured speech sample (audio.wav +
/// session.json, as <see cref="SpeechSampleStore"/> writes it) into an unreviewed
/// <c>expected.json</c> stub pre-filled from the recorded session.
/// </summary>
public sealed class EvalRunnerSessionStubTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-eval-stub-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Stub_From_Store_Written_Session_Carries_Canonical_Transcript_And_Callsigns()
    {
        var prefs = new UserPreferences();
        prefs.SetSpeechSampleSettings(enabled: true, maxMb: 50);
        var store = new SpeechSampleStore(prefs, _root, a => a());
        const string rawTranscript = "united two thirty four fly heading two seven zero";
        var trace = new SpeechSessionTrace(
            WhisperBiasingPrompt: "callsigns: UAL234",
            RawTranscript: rawTranscript,
            CallsignStrippedTranscript: "fly heading 270",
            CallsignExtracted: "UAL234",
            Rule: new RuleMapperTrace("fly heading 270", null, ["fly heading {hdg}"], "FH 270", null),
            Llm: null,
            ActiveCallsigns: ["UAL234", "SWA1943"],
            ProgrammedFixes: ["SUNOL"],
            AvailableRunwaysByAirport: new Dictionary<string, IReadOnlyList<string>> { ["KOAK"] = ["28R", "28L"] },
            TaxiwayNames: ["A", "B"],
            AircraftDestinations: new Dictionary<string, string> { ["UAL234"] = "KOAK" }
        );
        var session = new SpeechSession(
            TimestampUtc: DateTime.UtcNow,
            SampleCount: 8000,
            AudioDurationSeconds: 0.5,
            Transcript: rawTranscript,
            CanonicalCommand: "FH 270",
            UsedLlmFallback: false,
            TranscribeElapsedMs: 200,
            MapElapsedMs: 5,
            TotalElapsedMs: 210,
            Outcome: SpeechSessionOutcome.CommandAccepted,
            ErrorMessage: null
        )
        {
            Trace = trace,
        };

        string? id = store.Add(session, new float[8000], queueForUpload: false);
        Assert.NotNull(id);
        string caseDir = Path.Combine(_root, id);

        EvalExpectation? expectation = EvalRunner.LoadOrStubExpectation(caseDir, id);

        Assert.Null(expectation);
        using var stub = JsonDocument.Parse(File.ReadAllText(Path.Combine(caseDir, "expected.json")));
        JsonElement root = stub.RootElement;
        Assert.True(root.GetProperty("unreviewed").GetBoolean());
        Assert.Equal("FH 270", root.GetProperty("canonical").GetString());
        Assert.Equal(rawTranscript, root.GetProperty("transcript").GetString());
        Assert.Equal(["UAL234", "SWA1943"], root.GetProperty("activeCallsigns").Deserialize<List<string>>());
    }
}
