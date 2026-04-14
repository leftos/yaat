using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// End-to-end integration tests for the speech mapping pipeline. Start with a literal transcript
/// (i.e. whatever Whisper produced in a real debug log) and run it through the full
/// <see cref="SpeechRecognitionService.MapTranscriptAsync"/> path: callsign extraction →
/// <see cref="PhraseologyCommandMapper"/> rule engine → <see cref="LocalLlmCommandMapper"/> LLM
/// fallback → <see cref="LocalLlmCallsignResolver"/> callsign disambiguation. Asserts the final
/// (callsign, canonical) result so regressions in either the rules or the LLM prompts show up
/// as a clear failure tied to the exact transcript that triggered the issue.
///
/// Gated on the presence of a GGUF model at <see cref="LlmCudaFixture.ModelPath"/> — when absent
/// the tests silently pass, matching the rest of the opt-in LLM suite. The audio-to-Whisper step
/// is deliberately excluded: we want to verify "given transcript X, the pipeline produces Y"
/// without flaking on acoustic model variability.
/// </summary>
[Collection("LLM")]
public sealed class SpeechPipelineTranscriptIntegrationTests
{
    private readonly LocalLlmCommandMapper? _llmMapper;
    private readonly LocalLlmCallsignResolver? _callsignResolver;
    private readonly PhraseologyCommandMapper _ruleMapper = new();

    public SpeechPipelineTranscriptIntegrationTests(LlmCudaFixture fixture)
    {
        if (fixture.SharedServiceOrNull is { } shared)
        {
            _llmMapper = new LocalLlmCommandMapper(shared);
            _callsignResolver = new LocalLlmCallsignResolver(shared);
        }
    }

    /// <summary>
    /// Happy-path regression: clean Whisper output of a correctly-spoken phonetic callsign plus
    /// a textbook "turn left heading" command. Exercises the new digits + trailing-NATO-letter
    /// branch in <see cref="CallsignParser"/> and the unmodified rule mapper. No LLM involvement
    /// expected — rule engine should handle it entirely.
    /// </summary>
    [Fact]
    public async Task Transcript_CleanHeadingCommand_RuleEngineProducesTurnLeft()
    {
        if (_llmMapper is null)
        {
            return;
        }

        var ctx = BuildContext(["N346G"]);
        var result = await SpeechRecognitionService.MapTranscriptAsync(
            "november three four six golf turn left heading three one zero",
            ctx,
            _ruleMapper,
            _llmMapper,
            _callsignResolver,
            CancellationToken.None
        );

        Assert.Equal("N346G", result.Callsign);
        Assert.Equal("TL 310", result.Canonical);
        Assert.False(result.UsedLlmFallback);
    }

    /// <summary>
    /// Regression for the exact debug log the user reported: Whisper transcribed "heading" as
    /// "hitting" and "three" as "tree". Callsign extraction succeeds at the pipeline layer
    /// ("november three four six golf" → N346G) and the rule mapper fails on the garbled command
    /// ("turn left hitting 310" doesn't match any pattern). This test asserts the ideal outcome:
    /// the LLM command mapper should recover "turn left ... 310" as TL 310 despite the "hitting"
    /// noise.
    ///
    /// KNOWN FAILING: the Qwen2.5-1.5B model currently picks the verb correctly ("TL") but
    /// hallucinates the heading ("TL 090" as of this writing). The test is intentionally strict
    /// so prompt/model improvements show up as the test starting to pass. Until then, it
    /// documents the gap for anyone working on the LLM fallback. The partial-fallback path for
    /// when the LLM is unavailable is covered by
    /// <see cref="Transcript_WhisperHittingForHeading_NoLlm_SurfacesPartialFallback"/>.
    /// </summary>
    [Fact]
    public async Task Transcript_WhisperHittingForHeading_LlmRecoversTurnLeft310()
    {
        if (_llmMapper is null)
        {
            return;
        }

        var ctx = BuildContext(["N346G"]);
        var result = await SpeechRecognitionService.MapTranscriptAsync(
            "november three four six golf turn left hitting tree one zero",
            ctx,
            _ruleMapper,
            _llmMapper,
            _callsignResolver,
            CancellationToken.None
        );

        Assert.Equal("N346G", result.Callsign);
        Assert.Equal("TL 310", result.Canonical);
    }

    /// <summary>
    /// Companion to <see cref="Transcript_WhisperHittingForHeading_LlmRecoversTurnLeft310"/> that
    /// runs the same transcript through the pipeline with the LLM command mapper disabled. Even
    /// without LLM recovery, the pipeline must (a) extract the callsign via the rule parser and
    /// (b) surface the stripped command text so the user can hand-correct "hitting" → "heading"
    /// without losing context. This path runs always (no LLM gate) because the rule mapper +
    /// partial-fallback logic is pure and deterministic.
    /// </summary>
    [Fact]
    public async Task Transcript_WhisperHittingForHeading_NoLlm_SurfacesPartialFallback()
    {
        var ctx = BuildContext(["N346G"]);
        var result = await SpeechRecognitionService.MapTranscriptAsync(
            "november three four six golf turn left hitting tree one zero",
            ctx,
            _ruleMapper,
            llmMapper: null,
            callsignResolver: null,
            CancellationToken.None
        );

        Assert.Equal("N346G", result.Callsign);
        Assert.NotNull(result.Canonical);
        Assert.Contains("hitting", result.Canonical!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("310", result.Canonical!, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.UsedLlmFallback);
    }

    /// <summary>
    /// Regression for the first debug log the user reported: rule-path command mapping succeeds
    /// ("CM 2000") but Whisper mistranscribed "niner" as "diner" so <see cref="CallsignParser"/>
    /// can't recover the callsign. The <see cref="LocalLlmCallsignResolver"/> is expected to
    /// disambiguate "november diner 225 lima" against the active list and return N9225L.
    /// </summary>
    [Fact]
    public async Task Transcript_WhisperDinerForNiner_LlmResolverRecoversCallsign()
    {
        if (_llmMapper is null)
        {
            return;
        }

        var ctx = BuildContext(["N9225L", "SWA123"]);
        var result = await SpeechRecognitionService.MapTranscriptAsync(
            "november diner 225 lima climb and maintain 2000",
            ctx,
            _ruleMapper,
            _llmMapper,
            _callsignResolver,
            CancellationToken.None
        );

        Assert.Equal("CM 2000", result.Canonical);
        // LLM callsign resolver must map "november diner 225 lima" → N9225L against the active list.
        // If the model fails to disambiguate, the assertion below fails loudly with the raw output.
        Assert.Equal("N9225L", result.Callsign);
    }

    /// <summary>
    /// Regression for the hybrid form: Whisper's <c>initial_prompt</c> seeded the ICAO form, so
    /// it emitted "november N9225L" rather than the fully phonetic version. Rule-layer callsign
    /// extraction handles this directly (no LLM needed).
    /// </summary>
    [Fact]
    public async Task Transcript_HybridWhisperForm_RuleLayerRecoversCallsign()
    {
        if (_llmMapper is null)
        {
            return;
        }

        var ctx = BuildContext(["N9225L"]);
        var result = await SpeechRecognitionService.MapTranscriptAsync(
            "november N9225L climb and maintain 2000",
            ctx,
            _ruleMapper,
            _llmMapper,
            _callsignResolver,
            CancellationToken.None
        );

        Assert.Equal("N9225L", result.Callsign);
        Assert.Equal("CM 2000", result.Canonical);
        Assert.False(result.UsedLlmFallback);
    }

    private static SpeechContext BuildContext(string[] activeCallsigns)
    {
        // Empty programmed fixes + empty initial prompt — callers above don't exercise fix-based
        // rules. The Whisper initial_prompt field is unused by MapTranscriptAsync (it's only
        // consumed by the transcribe stage), so any value works; empty keeps the test minimal.
        return new SpeechContext(activeCallsigns, [], string.Empty);
    }
}
