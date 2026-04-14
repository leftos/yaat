using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// Opt-in end-to-end tests for the Phase 4 LLM fallback pipeline. Gated on the presence of a GGUF
/// model file at <see cref="LlmCudaFixture.ModelPath"/> — when the file is absent the tests silently
/// pass (matching the repo's <c>TestVnasData</c> convention per CLAUDE.md).
///
/// When enabled, these tests actually load the model (via CUDA if available, CPU fallback otherwise)
/// and exercise the full <see cref="LocalLlmCommandMapper"/> → <see cref="LocalLlmService"/> →
/// LLamaSharp path. Assertions are strict (exact-match on the canonical command) — prompt drift
/// that produces wrong verbs should fail the suite so we notice regressions.
///
/// To enable locally:
/// 1. Follow <c>TestData/llm/README.md</c> to download a small GGUF model,
/// 2. Place it at <c>tests/Yaat.Client.Tests/TestData/llm/test-model.gguf</c>,
/// 3. Run <c>dotnet test --filter FullyQualifiedName~LocalLlmPipelineIntegration</c>.
/// </summary>
[Collection("LLM")]
public sealed class LocalLlmPipelineIntegrationTests
{
    private readonly LocalLlmCommandMapper? _mapper;

    public LocalLlmPipelineIntegrationTests(LlmCudaFixture fixture)
    {
        // Reuse the fixture's shared LocalLlmService so the 1.1 GB Qwen weights load exactly once
        // per test run instead of once per test. Null when the GGUF is absent.
        _mapper = fixture.SharedServiceOrNull is { } shared ? new LocalLlmCommandMapper(shared) : null;
    }

    [Fact]
    public async Task ClimbAndMaintain_ProducesExactCM()
    {
        if (_mapper is null)
        {
            return;
        }

        var result = await _mapper.MapAsync("climb and maintain five thousand", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("CM 5000", result.CanonicalCommand);
    }

    [Fact]
    public async Task DescendAndMaintain_ProducesExactDM()
    {
        if (_mapper is null)
        {
            return;
        }

        var result = await _mapper.MapAsync("descend and maintain three thousand", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("DM 3000", result.CanonicalCommand);
    }

    [Fact]
    public async Task FlyHeading_ProducesExactFH()
    {
        if (_mapper is null)
        {
            return;
        }

        var result = await _mapper.MapAsync("fly heading two seven zero", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("FH 270", result.CanonicalCommand);
    }

    [Fact]
    public async Task TurnRight_ProducesExactTR()
    {
        if (_mapper is null)
        {
            return;
        }

        var result = await _mapper.MapAsync("turn right heading zero nine zero", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("TR 090", result.CanonicalCommand);
    }

    [Fact]
    public async Task Squawk_ProducesExactSQ()
    {
        if (_mapper is null)
        {
            return;
        }

        var result = await _mapper.MapAsync("squawk seven five zero zero", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("SQ 7500", result.CanonicalCommand);
    }

    [Fact]
    public async Task DirectTo_ProducesExactDCT()
    {
        if (_mapper is null)
        {
            return;
        }

        var context = new MapContext([], ["CEPIN", "SUNOL"]);
        var result = await _mapper.MapAsync("direct to CEPIN", context, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("DCT CEPIN", result.CanonicalCommand);
    }

    [Fact]
    public async Task ClearedForTakeoff_ProducesExactCTO()
    {
        if (_mapper is null)
        {
            return;
        }

        var result = await _mapper.MapAsync("cleared for takeoff", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("CTO", result.CanonicalCommand);
    }

    [Fact]
    public async Task ReduceSpeed_ProducesExactSPD()
    {
        if (_mapper is null)
        {
            return;
        }

        var result = await _mapper.MapAsync("reduce speed to two three zero", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("SPD 230", result.CanonicalCommand);
    }

    [Fact]
    public async Task NaturalLanguageChitChat_ReturnsNull()
    {
        if (_mapper is null)
        {
            return;
        }

        var result = await _mapper.MapAsync("good morning how are you doing today", MapContext.Empty, CancellationToken.None);

        // Either the model returns nothing, or NormalizeOutput rejects the response as non-canonical.
        // Both outcomes mean MapAsync returns null — we don't want this to pass through as a command.
        Assert.Null(result);
    }

    [Fact]
    public async Task GarbledTranscript_ReturnsNull()
    {
        if (_mapper is null)
        {
            return;
        }

        // Random word salad with no recognizable phraseology. Pre-grammar this could occasionally
        // sneak through if the model latched onto a word like "speed" and emitted SPD with garbage
        // args; with the GBNF + NormalizeOutput defence it must fail cleanly to null.
        var result = await _mapper.MapAsync("zoo turnip blender forty seventeen quack", MapContext.Empty, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SequentialCalls_BothSucceed()
    {
        if (_mapper is null)
        {
            return;
        }

        // Regression test for grammar-state leakage across PTT presses. LLamaSharp's Grammar
        // tracks position internally and must NOT be reused across calls without Reset(). Our
        // implementation builds a fresh Grammar per GenerateAsync, so two back-to-back MapAsync
        // calls on the same mapper instance should both produce valid output. If this test fails
        // (the second call returns null with empty output) the per-call allocation has regressed.
        var first = await _mapper.MapAsync("climb and maintain six thousand", MapContext.Empty, CancellationToken.None);
        var second = await _mapper.MapAsync("descend and maintain four thousand", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal("CM 6000", first.CanonicalCommand);
        Assert.NotNull(second);
        Assert.Equal("DM 4000", second.CanonicalCommand);
    }

    [Fact]
    public async Task GrammarRejectsInvalidVerb_FallsBackToNull()
    {
        if (_mapper is null)
        {
            return;
        }

        // Phrasing that pre-grammar sometimes coaxed the small instruct model into emitting
        // "EMERG" or "MAYDAY" — verbs YAAT doesn't have. The grammar can't even let the model
        // reach an invalid verb token, so the model must either pick a real verb or run out of
        // tokens with garbled output that NormalizeOutput rejects. Either way: null result.
        var result = await _mapper.MapAsync("declaring an emergency mayday mayday", MapContext.Empty, CancellationToken.None);

        Assert.Null(result);
    }
}
