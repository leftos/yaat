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
    public async Task DisabledConfig_ReturnsNullWithoutLoadingModel()
    {
        // This one intentionally does NOT use the shared service — it tests the "disabled" guard
        // and must construct a fresh LocalLlmService to prove the early-return path works without
        // ever touching LLamaSharp weights.
        if (!LlmCudaFixture.ModelAvailable)
        {
            return;
        }

        var config = new TestLlmConfig(LlmCudaFixture.ModelPath, gpuLayers: 999, enabled: false);
        var service = new LocalLlmService(config);
        var mapper = new LocalLlmCommandMapper(service);

        var result = await mapper.MapAsync("climb and maintain five thousand", MapContext.Empty, CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>Minimal <see cref="ILlmRuntimeConfig"/> double — no touching of %LOCALAPPDATA%.</summary>
    private sealed class TestLlmConfig : ILlmRuntimeConfig
    {
        public TestLlmConfig(string modelPath, int gpuLayers, bool enabled = true)
        {
            ModelPath = modelPath;
            GpuLayers = gpuLayers;
            Enabled = enabled;
        }

        public bool Enabled { get; }
        public string ModelPath { get; }
        public int GpuLayers { get; }
    }
}
