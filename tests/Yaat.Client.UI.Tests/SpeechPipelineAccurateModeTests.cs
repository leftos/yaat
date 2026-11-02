using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;
using Yaat.Sim.Speech;
using Yaat.Sim.Speech.Context;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests;

/// <summary>
/// The live push-to-talk pipeline picks its mapping path from the loaded engine: an accurate (beam) engine with a room
/// decodes the n-best against it, and every other press, or a decode that throws, takes the fast path on hypothesis 0.
/// The session is appended on the UI thread, so these run on the headless dispatcher.
/// </summary>
public sealed class SpeechPipelineAccurateModeTests
{
    // A hang guard, never a deadline: a passing run leaves each wait as soon as the pipeline records its session.
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    private const string Climb = "southwest twenty four ninety five climb and maintain one zero thousand";

    private static readonly SttHypotheses ClimbNBest = new(
        [
            new SttHypothesis(Climb, [], [], -1.0),
            new SttHypothesis("southwest twenty four ninety five climb and maintain one zero thousand feet", [], [], -2.5),
            new SttHypothesis("southwest twenty four ninety climb and maintain one zero thousand", [], [], -3.75),
        ],
        null,
        false
    );

    public SpeechPipelineAccurateModeTests() => TestVnasData.EnsureInitialized();

    [AvaloniaFact]
    public async Task Accurate_ResolvedCallsign_MapsTheRemainderAndRecordsTheDecode()
    {
        (SpeechResult result, SpeechSession session) = await PressAsync(TranscriptionMode.Accurate, ClimbNBest, Room("KOAK"));

        TranscriptMapResult fast = await FastMappingAsync(Climb);
        Assert.Equal("SWA2495", result.Callsign);
        Assert.NotNull(fast.Canonical);
        Assert.StartsWith("CM", result.CanonicalCommand);
        Assert.Equal(fast.Canonical, result.CanonicalCommand);
        Assert.Equal(Climb, session.Transcript);
        SpeechSessionTrace trace = Assert.IsType<SpeechSessionTrace>(session.Trace);
        Assert.Equal(TranscriptionMode.Accurate, trace.Mode);
        Assert.Equal(DecodeState.Resolved, trace.DecodeState);
        Assert.Equal(ClimbNBest.Entries.Select(e => (e.Text, e.Score)), trace.NBest!.Select(h => (h.Text, h.Score)));
        Assert.NotNull(trace.DecodeMs);
        Assert.True(trace.DecodeMs >= 0);
        Assert.Null(trace.DecodeError);
    }

    [AvaloniaFact]
    public async Task Accurate_EntryWithoutAScore_RecordsANullScore()
    {
        var nBest = new SttHypotheses([new SttHypothesis(Climb, [], [], -1.0), new SttHypothesis(Climb, [], [], null)], null, false);

        (_, SpeechSession session) = await PressAsync(TranscriptionMode.Accurate, nBest, Room("KOAK"));

        SpeechSessionTrace trace = Assert.IsType<SpeechSessionTrace>(session.Trace);
        Assert.Equal(TranscriptionMode.Accurate, trace.Mode);
        double?[] expected = [-1.0, null];
        Assert.Equal(expected, trace.NBest!.Select(h => h.Score));
    }

    [AvaloniaFact]
    public async Task Accurate_NoRoom_TakesTheFastPath()
    {
        (SpeechResult result, SpeechSession session) = await PressAsync(TranscriptionMode.Accurate, ClimbNBest, null);

        TranscriptMapResult fast = await FastMappingAsync(Climb);
        Assert.Equal(fast.Canonical, result.CanonicalCommand);
        AssertFastTrace(session, decodeError: false);
    }

    [AvaloniaFact]
    public async Task Fast_Engine_KeepsTodaysPath()
    {
        (SpeechResult result, SpeechSession session) = await PressAsync(TranscriptionMode.Fast, ClimbNBest, Room("KOAK"));

        TranscriptMapResult fast = await FastMappingAsync(Climb);
        Assert.Equal(fast.Callsign, result.Callsign);
        Assert.Equal(fast.Canonical, result.CanonicalCommand);
        Assert.Equal(Climb, session.Transcript);
        AssertFastTrace(session, decodeError: false);
    }

    [AvaloniaFact]
    public async Task Accurate_DecodeThrows_FallsBackAndMarksTheTrace()
    {
        (SpeechResult result, SpeechSession session) = await PressAsync(TranscriptionMode.Accurate, ClimbNBest, Room("KZZZ"));

        TranscriptMapResult fast = await FastMappingAsync(Climb);
        Assert.Equal(fast.Callsign, result.Callsign);
        Assert.Equal(fast.Canonical, result.CanonicalCommand);
        Assert.Equal(SpeechSessionOutcome.CommandAccepted, session.Outcome);
        AssertFastTrace(session, decodeError: true);
    }

    private const string TwoAddressees = "united twelve and delta thirty four turn left heading two seven zero";

    private const string TrafficRefusal = "number two follow the united twelve";

    // "thirty five" keeps the second callsign a near miss for DAL34, so the decode stays SayAgain instead of switching to it.
    private const string Correction = "united twelve correction delta thirty five descend and maintain five thousand";

    [AvaloniaFact]
    public async Task Accurate_SayAgain_AcceptsTheDecodersBestGuess()
    {
        var nBest = new SttHypotheses([new SttHypothesis(TwoAddressees, [], [], -1.0)], null, false);

        (SpeechResult result, SpeechSession session) = await PressAsync(TranscriptionMode.Accurate, nBest, Room("KOAK", "UAL12", "DAL34"));

        SpeechSessionTrace trace = Assert.IsType<SpeechSessionTrace>(session.Trace);
        Assert.Equal(DecodeState.SayAgain, trace.DecodeState);
        Assert.Equal("UAL12", trace.BestGuess);
        Assert.Equal("UAL12", result.Callsign);
        Assert.Equal("TL 270", result.CanonicalCommand);
        Assert.Null(result.SayAgainText);
    }

    [AvaloniaFact]
    public async Task Accurate_SayAgain_RejectsACallsignThatDiffersFromTheBestGuess()
    {
        var nBest = new SttHypotheses([new SttHypothesis(Correction, [], [], -1.0)], null, false);

        (SpeechResult result, SpeechSession session) = await PressAsync(TranscriptionMode.Accurate, nBest, Room("KOAK", "UAL12", "DAL34"));

        SpeechSessionTrace trace = Assert.IsType<SpeechSessionTrace>(session.Trace);
        Assert.Equal(DecodeState.SayAgain, trace.DecodeState);
        Assert.Equal("DAL34", trace.BestGuess);
        Assert.Contains("united 12", trace.CallsignStrippedTranscript);
        Assert.Null(result.Callsign);
        Assert.Null(result.CanonicalCommand);
        Assert.Equal("Say again: callsign unclear (best guess DAL34)", result.SayAgainText);
    }

    [AvaloniaFact]
    public async Task Accurate_NoCallsign_StagesTheTranscriptWithoutACallsign()
    {
        var nBest = new SttHypotheses([new SttHypothesis(TrafficRefusal, [], [], -1.0)], null, false);

        (SpeechResult result, SpeechSession session) = await PressAsync(TranscriptionMode.Accurate, nBest, Room("KOAK", "UAL12"));

        SpeechSessionTrace trace = Assert.IsType<SpeechSessionTrace>(session.Trace);
        Assert.Equal(DecodeState.NoCallsign, trace.DecodeState);
        Assert.Null(result.Callsign);
        Assert.Null(result.CanonicalCommand);
        Assert.Equal("Say again: callsign unclear", result.SayAgainText);
    }

    private static void AssertFastTrace(SpeechSession session, bool decodeError)
    {
        SpeechSessionTrace trace = Assert.IsType<SpeechSessionTrace>(session.Trace);
        Assert.Equal(TranscriptionMode.Fast, trace.Mode);
        Assert.Null(trace.NBest);
        Assert.Null(trace.DecodeState);
        Assert.Null(trace.BestGuess);
        Assert.Null(trace.DecodeMs);
        if (decodeError)
        {
            Assert.False(string.IsNullOrEmpty(trace.DecodeError));
        }
        else
        {
            Assert.Null(trace.DecodeError);
        }
    }

    private static RoomSnapshot Room(string airportIcao) => Room(airportIcao, "SWA2495");

    private static RoomSnapshot Room(string airportIcao, params string[] callsigns) =>
        new(
            airportIcao,
            false,
            null,
            [.. callsigns.Select(callsign => new RoomAircraftSnapshot(callsign, "B738", null, null, null, null, null, null, null))],
            []
        );

    private static SpeechContext Context(RoomSnapshot? room) =>
        new(room is null ? ["SWA2495"] : [.. room.Aircraft.Select(aircraft => aircraft.Callsign)], [], string.Empty) { Room = room };

    private static Task<TranscriptMapResult> FastMappingAsync(string transcript) =>
        SpeechRecognitionService.MapTranscriptAsync(transcript, Context(null), new PhraseologyCommandMapper(), null, null, CancellationToken.None);

    /// <summary>One push-to-talk press through the live pipeline, waited on until its result and its session are both out.</summary>
    private static async Task<(SpeechResult Result, SpeechSession Session)> PressAsync(
        TranscriptionMode mode,
        SttHypotheses hypotheses,
        RoomSnapshot? room
    )
    {
        var preferences = new UserPreferences();
        preferences.SetSpeechEnabled(true);
        using var engine = new CannedEngine(mode, hypotheses);
        using var service = new SpeechRecognitionService(
            preferences,
            new RecordingAudio(),
            engine,
            null,
            new PhraseologyCommandMapper(),
            null,
            null,
            () => Context(room),
            null,
            () => true
        );
        var result = new TaskCompletionSource<SpeechResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new TaskCompletionSource<SpeechSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CommandReady += r => result.TrySetResult(r);
        service.SessionRecorded += s => session.TrySetResult(s);

        Assert.True(service.StartPtt());
        service.StopPtt();

        SpeechResult ready = await result.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        SpeechSession recorded = await session.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        return (ready, recorded);
    }

    /// <summary>A microphone that always opens and records a second of silence.</summary>
    private sealed class RecordingAudio : IAudioCapture
    {
        public bool StartCapture() => true;

        public float[] StopCapture() => new float[AudioCaptureService.SampleRate];
    }

    /// <summary>An engine loaded for <paramref name="mode"/> that returns the same n-best for every clip.</summary>
    private sealed class CannedEngine(TranscriptionMode mode, SttHypotheses hypotheses) : ISttEngine
    {
        public TranscriptionMode Mode => mode;

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct) => Task.FromResult(hypotheses);

        public void Dispose() { }
    }
}
