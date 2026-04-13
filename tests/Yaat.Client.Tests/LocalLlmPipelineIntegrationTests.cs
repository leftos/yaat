using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// Opt-in end-to-end tests for the Phase 4 LLM fallback pipeline. Gated on the presence of a GGUF
/// model file at <see cref="ModelPath"/> — when the file is absent the tests silently pass (matching
/// the repo's <c>TestVnasData</c> convention per CLAUDE.md).
///
/// When enabled, these tests actually load the model (via CUDA if available, CPU fallback otherwise)
/// and exercise the full <see cref="LocalLlmCommandMapper"/> → <see cref="LocalLlmService"/> →
/// LLamaSharp path. Assertions are intentionally tolerant because small instruct models (0.5B–3B)
/// can drift at inference time even with Temperature=0.1.
///
/// To enable locally:
/// 1. Follow <c>TestData/llm/README.md</c> to download a small GGUF model,
/// 2. Place it at <c>tests/Yaat.Client.Tests/TestData/llm/test-model.gguf</c>,
/// 3. Run <c>dotnet test --filter FullyQualifiedName~LocalLlmPipelineIntegration</c>.
/// </summary>
[Collection("LLM")]
public sealed class LocalLlmPipelineIntegrationTests
{
    // Matches the pattern used by LiveWeatherRealDataTests: walk back from the test bin directory
    // to the source TestData/ folder so we don't need to copy the multi-GB GGUF on every build.
    private static readonly string ModelPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", "llm", "test-model.gguf");

    private static bool ModelAvailable => File.Exists(ModelPath);

    private static LocalLlmCommandMapper CreateMapper()
    {
        var config = new TestLlmConfig(ModelPath, gpuLayers: 999);
        var service = new LocalLlmService(config);
        return new LocalLlmCommandMapper(service);
    }

    [Fact]
    public async Task ClimbAndMaintain_ProducesAltitudeCommand()
    {
        if (!ModelAvailable)
        {
            return;
        }

        var mapper = CreateMapper();
        var result = await mapper.MapAsync("climb and maintain five thousand", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("5000", result.CanonicalCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescendAndMaintain_ProducesAltitudeCommand()
    {
        if (!ModelAvailable)
        {
            return;
        }

        var mapper = CreateMapper();
        var result = await mapper.MapAsync("descend and maintain three thousand", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("3000", result.CanonicalCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlyHeading_ProducesHeadingCommand()
    {
        if (!ModelAvailable)
        {
            return;
        }

        var mapper = CreateMapper();
        var result = await mapper.MapAsync("fly heading two seven zero", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("270", result.CanonicalCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurnRight_ProducesTurnCommand()
    {
        if (!ModelAvailable)
        {
            return;
        }

        var mapper = CreateMapper();
        var result = await mapper.MapAsync("turn right heading zero nine zero", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("090", result.CanonicalCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Squawk_ProducesSquawkCommand()
    {
        if (!ModelAvailable)
        {
            return;
        }

        var mapper = CreateMapper();
        var result = await mapper.MapAsync("squawk seven five zero zero", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("7500", result.CanonicalCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectTo_ProducesDirectCommand()
    {
        if (!ModelAvailable)
        {
            return;
        }

        var mapper = CreateMapper();
        var context = new MapContext([], ["CEPIN", "SUNOL"]);
        var result = await mapper.MapAsync("direct to CEPIN", context, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("CEPIN", result.CanonicalCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearedForTakeoff_ProducesCtoCommand()
    {
        if (!ModelAvailable)
        {
            return;
        }

        var mapper = CreateMapper();
        var result = await mapper.MapAsync("cleared for takeoff", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(result);
        // Accept either CTO or any variant the model might produce — what matters is that the
        // validator accepted it AND it references takeoff.
        Assert.Contains("CTO", result.CanonicalCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReduceSpeed_ProducesSpeedCommand()
    {
        if (!ModelAvailable)
        {
            return;
        }

        var mapper = CreateMapper();
        var result = await mapper.MapAsync("reduce speed to two three zero", MapContext.Empty, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("230", result.CanonicalCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NaturalLanguageChitChat_ReturnsNull()
    {
        if (!ModelAvailable)
        {
            return;
        }

        var mapper = CreateMapper();
        var result = await mapper.MapAsync("good morning how are you doing today", MapContext.Empty, CancellationToken.None);

        // Either the model returns nothing, or NormalizeOutput rejects the response as non-canonical.
        // Both outcomes mean MapAsync returns null — we don't want this to pass through as a command.
        Assert.Null(result);
    }

    [Fact]
    public async Task DisabledConfig_ReturnsNullWithoutLoadingModel()
    {
        if (!ModelAvailable)
        {
            return;
        }

        var config = new TestLlmConfig(ModelPath, gpuLayers: 999, enabled: false);
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
