using Microsoft.Extensions.Time.Testing;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech;
using Yaat.Client.Services.Speech.Bench;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// One benchmark row run in-process over the committed bench clips, with a scripted engine, GPU sampler, working-set
/// sampler and clock standing in for a real model, the Windows GPU counters, the process and wall time.
/// </summary>
public sealed class SttBenchRunnerTests : IDisposable
{
    private const long Megabyte = 1024 * 1024;

    private static readonly string ClipsDirectory = Path.Combine(AppContext.BaseDirectory, "Speech", "BenchClips");

    private readonly FakeTimeProvider _clock = new();

    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "yaat-tests", "stt-bench-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_CPU_Job_Fills_Every_Timing_Field()
    {
        var engine = new FakeEngine(TranscriptionMode.Fast, _clock);
        using var memory = new CurrentProcessMemory();

        SttBenchResult result = await SttBenchRunner.RunAsync(
            Job(SttMode.Fast, SttDevice.Cpu),
            new FakeSource(engine, _clock),
            new SttBenchMeters(new ScriptedGpu(-1), memory, _clock),
            CancellationToken.None
        );

        Assert.True(result.Succeeded, result.Failure);
        Assert.Null(result.Failure);
        Assert.Equal(12, result.ClipCount);
        Assert.Equal(SttMode.Fast, result.LoadedMode);
        CorpusWerTiming timing = Assert.IsType<CorpusWerTiming>(result.Timing);
        Assert.Equal("cpu", timing.Device);
        Assert.Equal(FakeEngine.LatencyMs, timing.LatencyP50Ms);
        Assert.Equal(FakeEngine.LatencyMs, timing.LatencyP90Ms);
        Assert.Equal(FakeEngine.LatencyMs, timing.LatencyMaxMs);
        Assert.True(timing.PreLoadWorkingSetBytes > 0);
        Assert.True(timing.PeakWorkingSetBytes >= timing.PreLoadWorkingSetBytes);
        Assert.True(engine.Disposed);
    }

    [Fact]
    public async Task The_Load_Time_Covers_Creating_And_Loading_The_Engine()
    {
        var engine = new FakeEngine(TranscriptionMode.Fast, _clock);

        SttBenchResult result = await RunAsync(Job(SttMode.Fast, SttDevice.Cpu), new FakeSource(engine, _clock), new ScriptedGpu(-1));

        Assert.Equal((FakeSource.CreateSeconds + FakeEngine.WarmUpSeconds), result.Timing!.LoadSeconds);
    }

    [Fact]
    public async Task The_Peak_Working_Set_Is_The_Larger_Of_The_Platform_Peak_And_The_Sampled_Working_Sets()
    {
        // A platform that reports no peak (0) must not hide the working set sampled through the run.
        var memory = new ScriptedMemory(peakBytes: 0, 500 * Megabyte, 2000 * Megabyte, 2600 * Megabyte, 2100 * Megabyte);

        SttBenchResult result = await RunAsync(Job(SttMode.Fast, SttDevice.Cpu), Source(TranscriptionMode.Fast), new ScriptedGpu(-1), memory);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(500 * Megabyte, result.Timing!.PreLoadWorkingSetBytes);
        Assert.Equal(2600 * Megabyte, result.Timing.PeakWorkingSetBytes);
    }

    [Fact]
    public async Task The_Peak_Working_Set_Never_Reads_Below_The_Pre_Load_Sample()
    {
        // A platform that reports no peak, and a working set that shrank after the pre-load sample, still reports a peak
        // covering every working set the run read, the pre-load sample included.
        var memory = new ScriptedMemory(peakBytes: 0, 2600 * Megabyte, 500 * Megabyte, 600 * Megabyte, 550 * Megabyte);

        SttBenchResult result = await RunAsync(Job(SttMode.Fast, SttDevice.Cpu), Source(TranscriptionMode.Fast), new ScriptedGpu(-1), memory);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(2600 * Megabyte, result.Timing!.PeakWorkingSetBytes);
    }

    [Fact]
    public async Task The_Platform_Peak_Wins_When_It_Is_Larger()
    {
        var memory = new ScriptedMemory(peakBytes: 4000 * Megabyte, 500 * Megabyte, 2000 * Megabyte);

        SttBenchResult result = await RunAsync(Job(SttMode.Fast, SttDevice.Cpu), Source(TranscriptionMode.Fast), new ScriptedGpu(-1), memory);

        Assert.Equal(4000 * Megabyte, result.Timing!.PeakWorkingSetBytes);
    }

    [Fact]
    public async Task The_Warm_Up_Transcription_Is_Not_Timed()
    {
        var engine = new FakeEngine(TranscriptionMode.Fast, _clock);

        SttBenchResult result = await RunAsync(Job(SttMode.Fast, SttDevice.Cpu), new FakeSource(engine, _clock), new ScriptedGpu(-1));

        Assert.Equal(result.ClipCount + 1, engine.Calls);
        Assert.Equal(result.ClipCount, result.Timing!.TimedSegments);
    }

    [Fact]
    public async Task A_GPU_Job_Whose_Memory_Did_Not_Rise_Is_Not_A_GPU_Row()
    {
        SttBenchResult result = await RunAsync(Job(SttMode.Fast, SttDevice.Gpu), Source(TranscriptionMode.Fast), new ScriptedGpu(0, 0));

        Assert.False(result.Succeeded);
        Assert.StartsWith("GPU not used:", result.Failure, StringComparison.Ordinal);
        Assert.Null(result.Timing);
    }

    [Fact]
    public async Task A_GPU_Job_Whose_Memory_Could_Not_Be_Read_Is_Not_A_GPU_Row()
    {
        SttBenchResult result = await RunAsync(Job(SttMode.Fast, SttDevice.Gpu), Source(TranscriptionMode.Fast), new ScriptedGpu(-1));

        Assert.False(result.Succeeded);
        Assert.Equal("GPU not used: could not read this process's GPU memory", result.Failure);
    }

    [Fact]
    public async Task A_GPU_Job_Whose_Memory_Rose_3_GB_Succeeds_With_Its_Peak()
    {
        var gpu = new ScriptedGpu(100 * Megabyte, 3172 * Megabyte, 3200 * Megabyte, 3300 * Megabyte, 3250 * Megabyte);

        SttBenchResult result = await RunAsync(Job(SttMode.Fast, SttDevice.Gpu), Source(TranscriptionMode.Fast), gpu);

        Assert.True(result.Succeeded, result.Failure);
        CorpusWerTiming timing = result.Timing!;
        Assert.Equal("gpu", timing.Device);
        Assert.Equal(100 * Megabyte, timing.PreLoadGpuBytes);
        Assert.Equal(3300 * Megabyte, timing.PeakGpuBytes);
    }

    [Fact]
    public async Task An_Accurate_Job_Whose_Engine_Loaded_Fast_Is_Not_An_Accurate_Row()
    {
        SttBenchResult result = await RunAsync(Job(SttMode.Accurate, SttDevice.Cpu), Source(TranscriptionMode.Fast), new ScriptedGpu(-1));

        Assert.False(result.Succeeded);
        Assert.Equal("accurate mode unavailable: the model loaded in Fast mode", result.Failure);
        Assert.Equal(SttMode.Fast, result.LoadedMode);
        Assert.Null(result.Timing);
    }

    [Fact]
    public async Task An_Accurate_Job_Whose_Source_Gives_A_Reason_Fails_With_That_Reason()
    {
        var source = new FakeSource(new FakeEngine(TranscriptionMode.Fast, _clock), _clock)
        {
            AccurateUnavailableReason = "the speech native has no n-best decoding",
        };

        SttBenchResult result = await RunAsync(Job(SttMode.Accurate, SttDevice.Cpu), source, new ScriptedGpu(-1));

        Assert.False(result.Succeeded);
        Assert.Equal("accurate mode unavailable: the speech native has no n-best decoding", result.Failure);
        Assert.Null(result.Timing);
    }

    [Fact]
    public async Task An_Accurate_Job_Whose_Engine_Loaded_Accurate_Succeeds()
    {
        SttBenchResult result = await RunAsync(Job(SttMode.Accurate, SttDevice.Cpu), Source(TranscriptionMode.Accurate), new ScriptedGpu(-1));

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(SttMode.Accurate, result.LoadedMode);
    }

    [Fact]
    public async Task A_Transcription_That_Throws_Names_Its_Clip()
    {
        // Call 1 is the warm-up of clip-01, so call 4 is the timed run of clip-03.
        var engine = new FakeEngine(TranscriptionMode.Fast, _clock) { ThrowOnCall = 4 };

        SttBenchResult result = await RunAsync(Job(SttMode.Fast, SttDevice.Cpu), new FakeSource(engine, _clock), new ScriptedGpu(-1));

        Assert.False(result.Succeeded);
        Assert.Equal("transcribe failed on clip-03: decoder exploded", result.Failure);
        Assert.Null(result.Timing);
    }

    [Fact]
    public async Task A_Warm_Up_Transcription_That_Throws_Names_The_First_Clip()
    {
        var engine = new FakeEngine(TranscriptionMode.Fast, _clock) { ThrowOnCall = 1 };

        SttBenchResult result = await RunAsync(Job(SttMode.Fast, SttDevice.Cpu), new FakeSource(engine, _clock), new ScriptedGpu(-1));

        Assert.False(result.Succeeded);
        Assert.Equal("transcribe failed on clip-01: decoder exploded", result.Failure);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task A_Load_That_Throws_Fails_The_Row()
    {
        var engine = new FakeEngine(TranscriptionMode.Fast, _clock) { LoadFailure = new InvalidOperationException("model file missing") };

        SttBenchResult result = await RunAsync(Job(SttMode.Fast, SttDevice.Cpu), new FakeSource(engine, _clock), new ScriptedGpu(-1));

        Assert.False(result.Succeeded);
        Assert.Equal("load failed: model file missing", result.Failure);
        Assert.True(engine.Disposed);
    }

    [Fact]
    public async Task A_Source_That_Throws_Fails_The_Row()
    {
        var source = new FakeSource(new FakeEngine(TranscriptionMode.Fast, _clock), _clock)
        {
            CreateFailure = new DirectoryNotFoundException("no model in unused-model-dir"),
        };

        SttBenchResult result = await RunAsync(Job(SttMode.Fast, SttDevice.Cpu), source, new ScriptedGpu(-1));

        Assert.False(result.Succeeded);
        Assert.Equal("load failed: no model in unused-model-dir", result.Failure);
        Assert.Null(result.LoadedMode);
    }

    [Fact]
    public async Task A_Folder_Without_Clips_Fails_Before_Loading()
    {
        Directory.CreateDirectory(_tempDirectory);
        var engine = new FakeEngine(TranscriptionMode.Fast, _clock);
        SttBenchJob job = Job(SttMode.Fast, SttDevice.Cpu) with { ClipsDirectory = _tempDirectory };

        SttBenchResult result = await RunAsync(job, new FakeSource(engine, _clock), new ScriptedGpu(-1));

        Assert.False(result.Succeeded);
        Assert.Equal($"no clips found in {_tempDirectory}", result.Failure);
        Assert.False(engine.Loaded);
    }

    [Fact]
    public async Task A_Malformed_Clip_List_Throws()
    {
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(Path.Combine(_tempDirectory, BenchClips.ManifestFileName), "[ { \"id\": ");
        SttBenchJob job = Job(SttMode.Fast, SttDevice.Cpu) with { ClipsDirectory = _tempDirectory };

        await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(job, Source(TranscriptionMode.Fast), new ScriptedGpu(-1)));
    }

    [Fact]
    public async Task Cancelling_After_Clip_2_Stops_Before_Clip_3()
    {
        using var cts = new CancellationTokenSource();
        // Call 1 is the warm-up, calls 2 and 3 are clip-01 and clip-02.
        var engine = new FakeEngine(TranscriptionMode.Fast, _clock) { OnCall = call => CancelAfter(call, 3, cts) };
        var meters = new SttBenchMeters(new ScriptedGpu(-1), DefaultMemory(), _clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SttBenchRunner.RunAsync(Job(SttMode.Fast, SttDevice.Cpu), new FakeSource(engine, _clock), meters, cts.Token)
        );
        Assert.Equal(3, engine.Calls);
        Assert.True(engine.Disposed);
    }

    private static void CancelAfter(int call, int lastCall, CancellationTokenSource cts)
    {
        if (call == lastCall)
        {
            cts.Cancel();
        }
    }

    private static ScriptedMemory DefaultMemory() => new(peakBytes: 900 * Megabyte, 400 * Megabyte);

    private FakeSource Source(TranscriptionMode mode) => new(new FakeEngine(mode, _clock), _clock);

    private Task<SttBenchResult> RunAsync(SttBenchJob job, FakeSource source, ScriptedGpu gpu) => RunAsync(job, source, gpu, DefaultMemory());

    private Task<SttBenchResult> RunAsync(SttBenchJob job, FakeSource source, ScriptedGpu gpu, ScriptedMemory memory) =>
        SttBenchRunner.RunAsync(job, source, new SttBenchMeters(gpu, memory, _clock), CancellationToken.None);

    private static SttBenchJob Job(SttMode mode, SttDevice device) =>
        new(SttEngine.RasrV2Fp32, mode, device, "unused-model-dir", ClipsDirectory, "unused-result.json");

    /// <summary>Hands out its engine after advancing the clock by <see cref="CreateSeconds"/>.</summary>
    private sealed class FakeSource(FakeEngine engine, FakeTimeProvider clock) : ISttBenchEngineSource
    {
        public const double CreateSeconds = 5;

        public string? AccurateUnavailableReason { get; init; }

        public Exception? CreateFailure { get; init; }

        public Task<SttBenchEngine> CreateAsync(SttBenchJob job, CancellationToken ct)
        {
            clock.Advance(TimeSpan.FromSeconds(CreateSeconds));
            if (CreateFailure is not null)
            {
                throw CreateFailure;
            }
            return Task.FromResult(new SttBenchEngine(engine, AccurateUnavailableReason));
        }
    }

    /// <summary>Loads in <see cref="WarmUpSeconds"/> and transcribes in <see cref="LatencyMs"/> of the fake clock.</summary>
    private sealed class FakeEngine(TranscriptionMode mode, FakeTimeProvider clock) : ISttEngine
    {
        public const double WarmUpSeconds = 2;
        public const double LatencyMs = 50;

        public TranscriptionMode Mode { get; } = mode;

        public int Calls { get; private set; }

        public int ThrowOnCall { get; init; }

        public Exception? LoadFailure { get; init; }

        public Action<int>? OnCall { get; init; }

        public bool Loaded { get; private set; }

        public bool Disposed { get; private set; }

        public Task WarmUpAsync(CancellationToken ct)
        {
            clock.Advance(TimeSpan.FromSeconds(WarmUpSeconds));
            if (LoadFailure is not null)
            {
                throw LoadFailure;
            }
            Loaded = true;
            return Task.CompletedTask;
        }

        public Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct)
        {
            Calls++;
            clock.Advance(TimeSpan.FromMilliseconds(LatencyMs));
            if (Calls == ThrowOnCall)
            {
                throw new InvalidOperationException("decoder exploded");
            }
            OnCall?.Invoke(Calls);
            return Task.FromResult(SttHypotheses.Single("november five one four romeo mike", null, false));
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>Returns its working sets in order, then repeats the last one; the peak is fixed.</summary>
    private sealed class ScriptedMemory(long peakBytes, params long[] workingSets) : IProcessMemorySampler
    {
        private int _next;

        public long WorkingSetBytes()
        {
            long value = workingSets[Math.Min(_next, workingSets.Length - 1)];
            _next++;
            return value;
        }

        public long PeakWorkingSetBytes() => peakBytes;
    }

    /// <summary>Returns its values in order, then repeats the last one.</summary>
    private sealed class ScriptedGpu(params long[] values) : IGpuMemorySampler
    {
        private int _next;

        public long Sample()
        {
            long value = values[Math.Min(_next, values.Length - 1)];
            _next++;
            return value;
        }
    }
}
