using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// How a <see cref="SherpaSttEngine"/> tells that a <c>cuda</c> load fell back to the CPU, given a native that returns a
/// recognizer and no error either way: a fallback line during the load, else no GPU memory rise, says so.
/// </summary>
[Collection(CpuFallbackReasonCollection.Name)]
public sealed class SherpaSttEngineGpuFallbackTests : IDisposable
{
    private const long MiB = 1024L * 1024;
    private const string NativeReason = @"Error loading ""onnxruntime_providers_cuda.dll"" which depends on ""cublasLt64_13.dll"" which is missing.";

    private readonly string? _savedReason = SherpaNativeLoader.CpuFallbackReason;
    private readonly string _dir = CreateModelDirectory();
    private int _changedEvents;

    public SherpaSttEngineGpuFallbackTests()
    {
        SherpaNativeLoader.ResetCpuFallbackForTests(null);
        SherpaNativeLoader.CpuFallbackReasonChanged += OnChanged;
    }

    public void Dispose()
    {
        SherpaNativeLoader.CpuFallbackReasonChanged -= OnChanged;
        SherpaNativeLoader.ResetCpuFallbackForTests(_savedReason);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Cuda_NoGpuMemoryRise_NoLine_FallsBackForTheMemoryReason()
    {
        string? reason = LoadOnce("cuda", new GpuSamples(1000 * MiB, 1000 * MiB), new FallbackLines());

        Assert.Equal(SherpaSttEngine.NoGpuMemoryRiseReason, reason);
    }

    [Fact]
    public void Cuda_GpuMemoryRise_NoLine_ReachedTheGpu_AndClearsAnEarlierFallback()
    {
        SherpaNativeLoader.ResetCpuFallbackForTests("an earlier load fell back");

        string? reason = LoadOnce("cuda", new GpuSamples(300 * MiB, 3626 * MiB), new FallbackLines());

        Assert.Null(reason);
        Assert.Null(SherpaNativeLoader.CpuFallbackReason);
        Assert.Equal(1, _changedEvents);
    }

    [Fact]
    public async Task Cuda_LineArrivesDuringTheLoad_FallsBackForTheNativeReason_EvenWithAGpuMemoryRise()
    {
        using var api = new SherpaSttEngineConcurrencyTests.FakeSherpaApi();
        var lines = new FallbackLines();
        lines.Arrive("an earlier load's line");
        using var engine = new SherpaSttEngine(_dir, "cuda", SherpaDecoding.Greedy, api, Probes(new GpuSamples(0, 3326 * MiB), lines));
        api.ArmCreate();
        Task<IReadOnlyList<SttHypothesis>> transcribe = Task.Run(() => engine.Transcribe(Silence(), AudioCaptureService.SampleRate, string.Empty));

        Assert.True(api.CreateEntered.Wait(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken), "CreateRecognizer was never entered");
        lines.Arrive(NativeReason);
        api.ReleaseCreate();
        Assert.NotEmpty(await transcribe.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));

        Assert.Equal(NativeReason, engine.ProviderFallbackReason);
    }

    [Fact]
    public void Cuda_GpuMemoryUnreadable_NoLine_IsNoFallback()
    {
        string? reason = LoadOnce("cuda", new GpuSamples(-1, -1), new FallbackLines());

        Assert.Null(reason);
    }

    [Fact]
    public void Cuda_NoCaptureInstalled_FallsBackOnTheMemoryRuleAlone()
    {
        Assert.Equal(NativeFallbackLine.None, NativeStderrCapture.Current.Latest);

        string? reason = LoadOnce("cuda", new GpuSamples(500 * MiB, 500 * MiB), NativeStderrCapture.Current);

        Assert.Equal(SherpaSttEngine.NoGpuMemoryRiseReason, reason);
    }

    [Fact]
    public void Cpu_NeverSamplesTheGpu_AndIsNoFallback()
    {
        var samples = new GpuSamples();

        string? reason = LoadOnce("cpu", samples, new FallbackLines());

        Assert.Null(reason);
        Assert.Equal(0, samples.Calls);
        Assert.Null(SherpaNativeLoader.CpuFallbackReason);
        Assert.Equal(0, _changedEvents);
    }

    [Fact]
    public void Fallback_SetsTheSharedReason_AndRaisesTheEventOnce()
    {
        LoadOnce("cuda", new GpuSamples(0, 0), new FallbackLines());

        Assert.Equal(SherpaSttEngine.NoGpuMemoryRiseReason, SherpaNativeLoader.CpuFallbackReason);
        Assert.Equal(1, _changedEvents);
    }

    private string? LoadOnce(string provider, GpuSamples samples, INativeFallbackSource lines)
    {
        using var api = new SherpaSttEngineConcurrencyTests.FakeSherpaApi();
        using var engine = new SherpaSttEngine(_dir, provider, SherpaDecoding.Greedy, api, Probes(samples, lines));
        Assert.NotEmpty(engine.Transcribe(Silence(), AudioCaptureService.SampleRate, string.Empty));
        Assert.Null(engine.LoadError);
        return engine.ProviderFallbackReason;
    }

    private static SherpaGpuFallbackProbes Probes(GpuSamples samples, INativeFallbackSource lines) => new(samples.Next, lines);

    private void OnChanged(object? sender, EventArgs e) => Interlocked.Increment(ref _changedEvents);

    private static float[] Silence() => new float[AudioCaptureService.SampleRate / 2];

    private static string CreateModelDirectory()
    {
        string dir = Directory.CreateTempSubdirectory("sherpa-engine-gpu-fallback-").FullName;
        foreach (string name in new[] { "encoder.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt" })
        {
            File.WriteAllText(Path.Combine(dir, name), string.Empty);
        }
        return dir;
    }

    /// <summary>Hands out the GPU byte figures a test scripts, in order, and counts the samples taken.</summary>
    private sealed class GpuSamples(params long[] values)
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public long Next()
        {
            int index = Interlocked.Increment(ref _calls) - 1;
            Assert.True(index < values.Length, $"The GPU was sampled {index + 1} times; the test scripted {values.Length}.");
            return values[index];
        }
    }

    /// <summary>Stands in for the stderr capture: each <see cref="Arrive"/> is a new fallback line.</summary>
    private sealed class FallbackLines : INativeFallbackSource
    {
        private NativeFallbackLine _latest = NativeFallbackLine.None;

        public NativeFallbackLine Latest => Volatile.Read(ref _latest);

        public void Arrive(string reason) => Volatile.Write(ref _latest, new NativeFallbackLine(Latest.Sequence + 1, reason));
    }
}
