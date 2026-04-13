using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// Ground-truth sanity test: is CUDA actually being used, and how long does a single prompt take?
///
/// This test exists to answer two questions before we trust timings or failures in
/// <see cref="LocalLlmPipelineIntegrationTests"/>:
/// <list type="number">
///   <item><description>Did LLamaSharp load the CUDA backend or silently fall back to CPU? The
///     <see cref="LlmCudaFixture.NativeLogLines"/> buffer will show "ggml_cuda_init" or similar
///     when CUDA is live.</description></item>
///   <item><description>How fast is one warm inference? Under ~500 ms on GPU, >2 s on CPU.</description></item>
/// </list>
///
/// Skipped silently when the opt-in GGUF is absent.
/// </summary>
[Collection("LLM")]
public sealed class LlmCudaSanityTest
{
    private readonly LlmCudaFixture _fixture;
    private readonly ITestOutputHelper _output;

    public LlmCudaSanityTest(LlmCudaFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task LoadModel_RunTrivialPrompt_ReportTiming()
    {
        if (!LlmCudaFixture.ModelAvailable)
        {
            _output.WriteLine($"GGUF absent at {LlmCudaFixture.ModelPath} — skipping.");
            return;
        }

        var service = _fixture.SharedServiceOrNull;
        Assert.NotNull(service);

        // First inference — loads the weights. We time the whole thing.
        var cold = Stopwatch.StartNew();
        var coldResult = await service.GenerateAsync(
            systemPrompt: "You are a helpful assistant. Answer in one short sentence.",
            userPrompt: "What is 2 + 2?",
            ct: CancellationToken.None
        );
        cold.Stop();

        // Second inference — weights cached, this is pure inference time.
        var warm = Stopwatch.StartNew();
        var warmResult = await service.GenerateAsync(
            systemPrompt: "You are a helpful assistant. Answer in one short sentence.",
            userPrompt: "What is 10 + 5?",
            ct: CancellationToken.None
        );
        warm.Stop();

        _output.WriteLine($"Cold call (load + inference): {cold.ElapsedMilliseconds} ms");
        _output.WriteLine($"Warm call (inference only):   {warm.ElapsedMilliseconds} ms");
        _output.WriteLine($"Cold raw output: {coldResult ?? "<null>"}");
        _output.WriteLine($"Warm raw output: {warmResult ?? "<null>"}");

        _output.WriteLine("");
        _output.WriteLine("=== Native log lines (most recent 60) ===");
        var lines = _fixture.NativeLogLines.ToArray();
        var start = Math.Max(0, lines.Length - 60);
        for (var i = start; i < lines.Length; i++)
        {
            _output.WriteLine(lines[i]);
        }

        // Both calls must return *something* — if raw is null, either the model isn't producing
        // output or LLamaSharp failed to load. Either way, the integration tests can't work.
        Assert.NotNull(coldResult);
        Assert.NotNull(warmResult);

        // Loose timing sanity check: warm inference should be under 5 s on ANY hardware that can
        // run LLM inference at all. On CUDA we expect well under 1 s; on CPU fallback it'll be
        // 1–3 s. If warm > 5 s something is very wrong.
        Assert.True(warm.ElapsedMilliseconds < 5000, $"Warm inference took {warm.ElapsedMilliseconds} ms — suspiciously slow, check backend");
    }
}
