using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim.Speech;
using Yaat.Sim.Speech.Context;

namespace Yaat.Client.Tests;

/// <summary>
/// The Speech Debug window's per-session decode rows: the STT mode, the n-best list, the context-decode state, the
/// say-again best guess and the decode time, each read from <see cref="SpeechSessionTrace"/>. A trace that predates the
/// accurate-mode fields carries nulls for all six, and a session may have no trace at all, so every row member answers
/// without throwing and the absent states read their own text (<c>unknown</c>, <c>not run</c>, no best guess, no time).
/// </summary>
public class SpeechDebugSessionRowTests
{
    [Theory]
    [InlineData(TranscriptionMode.Fast, "Fast")]
    [InlineData(TranscriptionMode.Accurate, "Accurate")]
    public void ModeTextOf_NamesTheModeThatRan(TranscriptionMode mode, string expected) =>
        Assert.Equal(expected, SpeechDebugSessionRow.ModeTextOf(mode));

    [Fact]
    public void ModeTextOf_ReadsUnknownWithoutAMode() => Assert.Equal("unknown", SpeechDebugSessionRow.ModeTextOf(null));

    [Theory]
    [InlineData(DecodeState.Resolved, null, "Resolved")]
    [InlineData(DecodeState.SayAgain, null, "Say again")]
    [InlineData(DecodeState.NoCallsign, null, "No callsign")]
    public void DecodeStateTextOf_NamesTheDecodeState(DecodeState state, string? decodeError, string expected) =>
        Assert.Equal(expected, SpeechDebugSessionRow.DecodeStateTextOf(state, decodeError));

    [Fact]
    public void DecodeStateTextOf_ReadsNotRunWithoutAStateOrAnError() => Assert.Equal("not run", SpeechDebugSessionRow.DecodeStateTextOf(null, null));

    [Fact]
    public void DecodeStateTextOf_ReadsTheFallbackForAnErrorWithoutAState() =>
        Assert.Equal("Fell back to fast: boom", SpeechDebugSessionRow.DecodeStateTextOf(null, "boom"));

    [Fact]
    public void DecodeStateTextOf_GivesTheFallbackErrorPrecedenceOverTheState() =>
        Assert.Equal("Fell back to fast: boom", SpeechDebugSessionRow.DecodeStateTextOf(DecodeState.Resolved, "boom"));

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(DecodeState.Resolved, null, false)]
    [InlineData(null, "boom", false)]
    public void DecodeStateIsNotRunOf_IsTrueOnlyWithoutAStateOrAnError(DecodeState? state, string? decodeError, bool expected) =>
        Assert.Equal(expected, SpeechDebugSessionRow.DecodeStateIsNotRunOf(state, decodeError));

    [Fact]
    public void BestGuessTextOf_ShowsTheGuessForASayAgain() =>
        Assert.Equal("Best guess: SWA2495", SpeechDebugSessionRow.BestGuessTextOf(DecodeState.SayAgain, "SWA2495"));

    [Fact]
    public void BestGuessTextOf_IsHiddenForAResolvedCallsign() => Assert.Null(SpeechDebugSessionRow.BestGuessTextOf(DecodeState.Resolved, "SWA2495"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BestGuessTextOf_IsHiddenWithoutAGuess(string? bestGuess) =>
        Assert.Null(SpeechDebugSessionRow.BestGuessTextOf(DecodeState.SayAgain, bestGuess));

    [Fact]
    public void DecodeTimeTextOf_RendersWholeMilliseconds()
    {
        Assert.Equal("Decode 12 ms", SpeechDebugSessionRow.DecodeTimeTextOf(12.4));
        Assert.Equal("Decode 0 ms", SpeechDebugSessionRow.DecodeTimeTextOf(0.4));
    }

    [Fact]
    public void DecodeTimeTextOf_IsNullWithoutADecodeTime() => Assert.Null(SpeechDebugSessionRow.DecodeTimeTextOf(null));

    [Fact]
    public void NBestLineOf_RanksTheHypothesisAndShowsItsScore() =>
        Assert.Equal("#1 cleared to land  (-3.14)", SpeechDebugSessionRow.NBestLineOf(1, new SpeechTraceHypothesis("cleared to land", -3.14159)));

    [Fact]
    public void NBestLineOf_OmitsAMissingScore() =>
        Assert.Equal("#2 cleared to land", SpeechDebugSessionRow.NBestLineOf(2, new SpeechTraceHypothesis("cleared to land", null)));

    [Fact]
    public void RowWithoutATrace_AnswersEveryDecodeMemberSafely()
    {
        var row = new SpeechDebugSessionRow(MakeSession(trace: null));

        Assert.Equal("unknown", row.ModeText);
        Assert.Equal("not run", row.DecodeStateText);
        Assert.False(row.DecodeStateIsFailure);
        Assert.True(row.DecodeStateIsNotRun);
        Assert.Null(row.BestGuessText);
        Assert.False(row.HasBestGuess);
        Assert.Null(row.DecodeTimeText);
        Assert.False(row.HasNBest);
        Assert.Empty(row.NBestLines);
        Assert.Equal("N-best (0)", row.NBestHeader);
    }

    [Fact]
    public void RowWithAnAccurateSayAgainTrace_ShowsTheModeTheHypothesesAndTheGuess()
    {
        SpeechSessionTrace trace = MakeTrace(
            mode: TranscriptionMode.Accurate,
            nBest: [new SpeechTraceHypothesis("cleared to land", -3.14159), new SpeechTraceHypothesis("cleared to land", null)],
            decodeState: DecodeState.SayAgain,
            bestGuess: "SWA2495",
            decodeMs: 12.4,
            decodeError: null
        );
        var row = new SpeechDebugSessionRow(MakeSession(trace));

        Assert.Equal("Accurate", row.ModeText);
        Assert.Equal("Say again", row.DecodeStateText);
        Assert.False(row.DecodeStateIsFailure);
        Assert.False(row.DecodeStateIsNotRun);
        Assert.Equal("Best guess: SWA2495", row.BestGuessText);
        Assert.True(row.HasBestGuess);
        Assert.Equal("Decode 12 ms", row.DecodeTimeText);
        Assert.True(row.HasNBest);
        Assert.Equal("N-best (2)", row.NBestHeader);
        Assert.Equal<string>(["#1 cleared to land  (-3.14)", "#2 cleared to land"], row.NBestLines);
    }

    [Fact]
    public void RowWithAFallbackTrace_ShowsTheFastModeAndTheDecodeError()
    {
        SpeechSessionTrace trace = MakeTrace(
            mode: TranscriptionMode.Fast,
            nBest: null,
            decodeState: null,
            bestGuess: null,
            decodeMs: null,
            decodeError: "boom"
        );
        var row = new SpeechDebugSessionRow(MakeSession(trace));

        Assert.Equal("Fast", row.ModeText);
        Assert.Equal("Fell back to fast: boom", row.DecodeStateText);
        Assert.True(row.DecodeStateIsFailure);
        Assert.False(row.DecodeStateIsNotRun);
        Assert.Null(row.BestGuessText);
        Assert.False(row.HasBestGuess);
        Assert.Null(row.DecodeTimeText);
        Assert.False(row.HasNBest);
    }

    private static SpeechSession MakeSession(SpeechSessionTrace? trace)
    {
        return new SpeechSession(
            TimestampUtc: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            SampleCount: 16000,
            AudioDurationSeconds: 1.0,
            Transcript: "southwest twenty four ninety five cleared to land",
            CanonicalCommand: "CLAND RWY 28R",
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
    }

    private static SpeechSessionTrace MakeTrace(
        TranscriptionMode? mode,
        IReadOnlyList<SpeechTraceHypothesis>? nBest,
        DecodeState? decodeState,
        string? bestGuess,
        double? decodeMs,
        string? decodeError
    )
    {
        return new SpeechSessionTrace(
            WhisperBiasingPrompt: "callsigns: SWA2495",
            RawTranscript: "southwest twenty four ninety five cleared to land",
            CallsignStrippedTranscript: "cleared to land",
            CallsignExtracted: "SWA2495",
            Rule: new RuleMapperTrace("cleared to land", null, ["cleared to land {rwy}"], "CLAND RWY 28R", null),
            Llm: null,
            ActiveCallsigns: ["SWA2495"],
            ProgrammedFixes: ["KOAK"],
            AvailableRunwaysByAirport: new Dictionary<string, IReadOnlyList<string>> { ["KOAK"] = ["28R", "28L"] },
            TaxiwayNames: ["A", "B"],
            AircraftDestinations: new Dictionary<string, string> { ["SWA2495"] = "KOAK" },
            Mode: mode,
            NBest: nBest,
            DecodeState: decodeState,
            BestGuess: bestGuess,
            DecodeMs: decodeMs,
            DecodeError: decodeError
        );
    }
}
