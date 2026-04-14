using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// Unit tests for <see cref="LocalLlmService.PrewarmAsync"/> in its short-circuit paths. The
/// heavy-path integration tests (real model load) live in <c>LocalLlmPipelineIntegrationTests</c>
/// under the <c>LlmCudaFixture</c>.
///
/// We use a rooted-but-missing file path (not a bare filename) because <see cref="LocalLlmService.IsConfigured"/>
/// dispatches on the model source: rooted paths require <see cref="File.Exists"/>, while bare
/// strings are treated as LM-Kit curated model IDs that LM-Kit downloads on demand. The
/// "missing file" branch is the one we want to exercise here.
/// </summary>
public class LocalLlmServicePrewarmTests
{
    private static string MissingPath => Path.Combine(Path.GetTempPath(), "yaat-test-nonexistent-{Guid.NewGuid():N}.gguf");

    [Fact]
    public async Task PrewarmAsync_WhenModelMissing_ReturnsImmediatelyWithoutThrowing()
    {
        var service = new LocalLlmService(new FakeConfig(MissingPath));
        await service.PrewarmAsync(CancellationToken.None);
        Assert.False(service.IsConfigured);
    }

    [Fact]
    public async Task PrewarmAsync_IsIdempotent_WhenUnconfigured()
    {
        var service = new LocalLlmService(new FakeConfig(MissingPath));
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
