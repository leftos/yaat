using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// Unit tests for <see cref="LocalLlmService.PrewarmAsync"/> in its short-circuit paths. The
/// heavy-path integration tests (real model load) live in <c>LocalLlmPipelineIntegrationTests</c>
/// under the <c>LlmCudaFixture</c>.
/// </summary>
public class LocalLlmServicePrewarmTests
{
    [Fact]
    public async Task PrewarmAsync_WhenModelMissing_ReturnsImmediatelyWithoutThrowing()
    {
        var service = new LocalLlmService(new FakeConfig("nonexistent.gguf"));
        await service.PrewarmAsync(CancellationToken.None);
        Assert.False(service.IsConfigured);
    }

    [Fact]
    public async Task PrewarmAsync_IsIdempotent_WhenUnconfigured()
    {
        var service = new LocalLlmService(new FakeConfig("nonexistent.gguf"));
        await service.PrewarmAsync(CancellationToken.None);
        await service.PrewarmAsync(CancellationToken.None);
        await service.PrewarmAsync(CancellationToken.None);
        Assert.False(service.IsConfigured);
    }

    private sealed class FakeConfig : ILlmRuntimeConfig
    {
        public FakeConfig(string modelPath)
        {
            ModelPath = modelPath;
        }

        public string ModelPath { get; }
        public int GpuLayers => 0;
    }
}
