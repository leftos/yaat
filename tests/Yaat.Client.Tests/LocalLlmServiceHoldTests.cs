using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// <see cref="LocalLlmService.UnloadAsync"/>'s hold, with no real model: a load reads the GPU layer preference first, so a
/// prewarm or an inference that reads none has not tried to load.
/// </summary>
public sealed class LocalLlmServiceHoldTests
{
    [Fact]
    public async Task LocalLlmService_RefusesToLoadWhileHeld()
    {
        var config = new CountingConfig();
        using var service = new LocalLlmService(config);

        await service.UnloadAsync(CancellationToken.None);
        await service.PrewarmAsync(CancellationToken.None);
        string? generated = await service.GenerateAsync("system", "user", null, CancellationToken.None);

        Assert.True(service.IsConfigured);
        Assert.Null(generated);
        Assert.Equal(0, config.GpuLayerReads);
    }

    // A bare string is an LM-Kit model ID, which counts as configured without a file.
    private sealed class CountingConfig : ILlmRuntimeConfig
    {
        private int _gpuLayerReads;

        public string ModelPath => "yaat-test-held-model";

        public int GpuLayers
        {
            get
            {
                Interlocked.Increment(ref _gpuLayerReads);
                return 0;
            }
        }

        public int GpuLayerReads => Volatile.Read(ref _gpuLayerReads);
    }
}
