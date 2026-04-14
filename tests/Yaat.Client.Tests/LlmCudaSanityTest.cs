using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// Ground-truth sanity test: how long does a single prompt take and is the model loading at all?
///
/// This test exists to answer the timing question before we trust failures in
/// <see cref="LocalLlmPipelineIntegrationTests"/>: if cold load + warm inference are completing
/// at expected speeds (cold &lt;15 s, warm &lt;5 s), backend selection is working and any
/// integration-test failures are real correctness issues. LM-Kit owns backend selection
/// internally so we no longer need a separate native-log capture path.
///
/// Skipped silently when no model source is configured (LMKIT_TEST_MODEL env var unset).
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
            _output.WriteLine("LMKIT_TEST_MODEL env var not set — skipping LM-Kit live test.");
            return;
        }

        var service = _fixture.SharedServiceOrNull;
        Assert.NotNull(service);

        // First inference — loads the weights. We time the whole thing. No grammar: this is a
        // sanity test that exercises freeform inference on the GPU/CPU path, not the constrained
        // command-mapper pipeline.
        var cold = Stopwatch.StartNew();
        var coldResult = await service.GenerateAsync(
            systemPrompt: "You are a helpful assistant. Answer in one short sentence.",
            userPrompt: "What is 2 + 2?",
            gbnfGrammar: null,
            ct: CancellationToken.None
        );
        cold.Stop();

        // Second inference — weights cached, this is pure inference time.
        var warm = Stopwatch.StartNew();
        var warmResult = await service.GenerateAsync(
            systemPrompt: "You are a helpful assistant. Answer in one short sentence.",
            userPrompt: "What is 10 + 5?",
            gbnfGrammar: null,
            ct: CancellationToken.None
        );
        warm.Stop();

        _output.WriteLine($"Cold call (load + inference): {cold.ElapsedMilliseconds} ms");
        _output.WriteLine($"Warm call (inference only):   {warm.ElapsedMilliseconds} ms");
        _output.WriteLine($"Cold raw output: {coldResult ?? "<null>"}");
        _output.WriteLine($"Warm raw output: {warmResult ?? "<null>"}");

        // Both calls must return *something* — if raw is null, either the model isn't producing
        // output or LM-Kit failed to load it. Either way, the integration tests can't work.
        Assert.NotNull(coldResult);
        Assert.NotNull(warmResult);

        // Loose timing sanity check: warm inference should be under 5 s on ANY hardware that can
        // run LLM inference at all. On CUDA we expect well under 1 s; on CPU fallback it'll be
        // 1–3 s. If warm > 5 s something is very wrong.
        Assert.True(warm.ElapsedMilliseconds < 5000, $"Warm inference took {warm.ElapsedMilliseconds} ms — suspiciously slow, check backend");
    }
}
