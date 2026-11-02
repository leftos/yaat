using System.Text.Json;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech;
using Yaat.Client.Services.Speech.Bench;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// The benchmark child's argument check and its body after logging is set up: the job file in, one result file out,
/// with the native setup, the engine and the meters scripted.
/// </summary>
public sealed class SttBenchChildTests : IDisposable
{
    private const string GpuRuntimeMissing = "GPU not used: the GPU runtime is not installed";

    private static readonly string ClipsDirectory = Path.Combine(AppContext.BaseDirectory, "Speech", "BenchClips");

    private readonly string _dir = Directory.CreateTempSubdirectory("stt-bench-child-").FullName;

    private string JobPath => Path.Combine(_dir, "job.json");

    private string ResultPath => Path.Combine(_dir, "result.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(true, "--stt-bench-one", "job.json")]
    [InlineData(false, "--stt-bench-one")]
    [InlineData(false, "--stt-bench-one", "job.json", "--scenario")]
    [InlineData(false, "--scenario", "job.json")]
    [InlineData(false, "job.json", "--stt-bench-one")]
    [InlineData(false)]
    public void Only_The_Flag_And_One_Path_Start_The_Child(bool expected, params string[] args)
    {
        bool parsed = SttBenchChild.TryParseArgs(args, out string jobPath);

        Assert.Equal(expected, parsed);
        Assert.Equal(expected ? "job.json" : string.Empty, jobPath);
    }

    [Fact]
    public async Task A_Passing_Job_Writes_A_Measured_Result_And_Leaves_No_Temporary_File()
    {
        WriteJob(Job(SttDevice.Cpu, ClipsDirectory, ResultPath));

        int exit = await RunAsync(CpuProcess, new FakeSource(null));

        Assert.Equal(SttBenchChild.ExitResultWritten, exit);
        SttBenchResult result = ReadResult();
        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(12, result.ClipCount);
        Assert.False(File.Exists(ResultPath + ".tmp"));
    }

    [Fact]
    public async Task A_Source_That_Throws_Writes_A_Load_Failure()
    {
        WriteJob(Job(SttDevice.Cpu, ClipsDirectory, ResultPath));

        int exit = await RunAsync(CpuProcess, new FakeSource(new InvalidOperationException("no model here")));

        Assert.Equal(SttBenchChild.ExitResultWritten, exit);
        Assert.Equal("load failed: no model here", ReadResult().Failure);
    }

    [Fact]
    public async Task A_Malformed_Clip_List_Writes_A_Bench_Failure()
    {
        string clips = Directory.CreateDirectory(Path.Combine(_dir, "clips")).FullName;
        File.WriteAllText(Path.Combine(clips, BenchClips.ManifestFileName), "[ { \"id\": ");
        WriteJob(Job(SttDevice.Cpu, clips, ResultPath));

        int exit = await RunAsync(CpuProcess, new FakeSource(null));

        Assert.Equal(SttBenchChild.ExitResultWritten, exit);
        SttBenchResult result = ReadResult();
        Assert.False(result.Succeeded);
        Assert.StartsWith("bench failed: ", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Native_Setup_That_Throws_Writes_A_Bench_Failure()
    {
        WriteJob(Job(SttDevice.Cpu, ClipsDirectory, ResultPath));

        int exit = await RunAsync(_ => throw new InvalidOperationException("resolver exploded"), new FakeSource(null));

        Assert.Equal(SttBenchChild.ExitResultWritten, exit);
        Assert.Equal("bench failed: resolver exploded", ReadResult().Failure);
    }

    [Fact]
    public async Task A_Missing_Job_File_Exits_2_Without_A_Result()
    {
        int exit = await RunAsync(CpuProcess, new FakeSource(null));

        Assert.Equal(SttBenchChild.ExitJobUnreadable, exit);
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task A_Malformed_Job_File_Exits_2_Without_A_Result()
    {
        File.WriteAllText(JobPath, "{ \"engine\": ");

        int exit = await RunAsync(CpuProcess, new FakeSource(null));

        Assert.Equal(SttBenchChild.ExitJobUnreadable, exit);
        Assert.Equal([JobPath], Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task A_Job_Without_A_Result_Path_Exits_2_Without_A_Result()
    {
        string clips = JsonSerializer.Serialize(ClipsDirectory);
        string json =
            $$"""{"engine": "RasrV2Int8", "mode": "Fast", "device": "Cpu", "modelDirectory": "m", "clipsDirectory": {{clips}}, "resultPath": null}""";
        File.WriteAllText(JobPath, json);

        int exit = await RunAsync(CpuProcess, new FakeSource(null));

        Assert.Equal(SttBenchChild.ExitJobUnreadable, exit);
        Assert.Equal([JobPath], Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task An_Invalid_Result_Path_Exits_3()
    {
        WriteJob(Job(SttDevice.Cpu, ClipsDirectory, Path.Combine(_dir, "res\0ult.json")));

        int exit = await RunAsync(CpuProcess, new FakeSource(null));

        Assert.Equal(SttBenchChild.ExitResultUnwritable, exit);
    }

    [Fact]
    public async Task A_Result_Path_In_A_Missing_Folder_Exits_3()
    {
        WriteJob(Job(SttDevice.Cpu, ClipsDirectory, Path.Combine(_dir, "missing", "result.json")));

        int exit = await RunAsync(CpuProcess, new FakeSource(null));

        Assert.Equal(SttBenchChild.ExitResultUnwritable, exit);
    }

    [Fact]
    public async Task A_GPU_Job_On_The_CPU_Set_Is_Not_Run()
    {
        WriteJob(Job(SttDevice.Gpu, ClipsDirectory, ResultPath));
        var source = new FakeSource(null);

        int exit = await RunAsync(CpuProcess, source);

        Assert.Equal(SttBenchChild.ExitResultWritten, exit);
        SttBenchResult result = ReadResult();
        Assert.False(result.Succeeded);
        Assert.Equal(GpuRuntimeMissing, result.Failure);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task A_GPU_Job_Whose_Startup_Fell_Back_Names_The_Reason()
    {
        WriteJob(Job(SttDevice.Gpu, ClipsDirectory, ResultPath));

        int exit = await RunAsync(_ => (SherpaNativeSet.Cpu, "Loading the GPU runtime failed: cudnn missing"), new FakeSource(null));

        Assert.Equal(SttBenchChild.ExitResultWritten, exit);
        Assert.Equal("GPU not used: Loading the GPU runtime failed: cudnn missing", ReadResult().Failure);
    }

    [Fact]
    public async Task The_Native_Setup_Gets_The_Job_Device()
    {
        WriteJob(Job(SttDevice.Gpu, ClipsDirectory, ResultPath));
        SttDevice? asked = null;

        await RunAsync(
            device =>
            {
                asked = device;
                return (SherpaNativeSet.Cpu, null);
            },
            new FakeSource(null)
        );

        Assert.Equal(SttDevice.Gpu, asked);
    }

    [Fact]
    public async Task A_Real_Model_Measures_Every_Bench_Clip_On_The_CPU()
    {
        string? modelDir = Environment.GetEnvironmentVariable("YAAT_TEST_RASR_INT8_DIR");
        Assert.SkipUnless(
            !string.IsNullOrEmpty(modelDir)
                && Directory.Exists(modelDir)
                && (Path.GetFileName(Path.TrimEndingDirectorySeparator(modelDir)) == "rasr-parakeet-v2-int8"),
            "YAAT_TEST_RASR_INT8_DIR names no existing folder named rasr-parakeet-v2-int8."
        );
        WriteJob(new SttBenchJob(SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Cpu, modelDir, ClipsDirectory, ResultPath));
        using var gpu = new GpuMemoryProbe();
        using var memory = new CurrentProcessMemory();

        int exit = await SttBenchChild.RunJobAsync(
            JobPath,
            CpuProcess,
            new FactorySttBenchEngineSource(engine => engine.NativeProblem()),
            new SttBenchMeters(gpu, memory, TimeProvider.System)
        );

        Assert.Equal(SttBenchChild.ExitResultWritten, exit);
        SttBenchResult result = ReadResult();
        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(12, result.ClipCount);
        Assert.True(result.Timing!.LatencyP50Ms > 0);
    }

    private static (SherpaNativeSet Set, string? CpuFallbackReason) CpuProcess(SttDevice device) => (SherpaNativeSet.Cpu, null);

    private Task<int> RunAsync(Func<SttDevice, (SherpaNativeSet Set, string? CpuFallbackReason)> natives, FakeSource source) =>
        SttBenchChild.RunJobAsync(JobPath, natives, source, new SttBenchMeters(new NoGpu(), new FixedMemory(), TimeProvider.System));

    private static SttBenchJob Job(SttDevice device, string clipsDirectory, string resultPath) =>
        new(SttEngine.RasrV2Int8, SttMode.Fast, device, "unused-model-dir", clipsDirectory, resultPath);

    private void WriteJob(SttBenchJob job) => File.WriteAllText(JobPath, JsonSerializer.Serialize(job, SttBenchJsonContext.Default.SttBenchJob));

    private SttBenchResult ReadResult() =>
        JsonSerializer.Deserialize(File.ReadAllText(ResultPath), SttBenchJsonContext.Default.SttBenchResult)
        ?? throw new InvalidDataException($"{ResultPath} holds no result");

    /// <summary>Hands out a fresh Fast engine, or throws <paramref name="failure"/>.</summary>
    private sealed class FakeSource(Exception? failure) : ISttBenchEngineSource
    {
        public int Calls { get; private set; }

        public Task<SttBenchEngine> CreateAsync(SttBenchJob job, CancellationToken ct)
        {
            Calls++;
            if (failure is not null)
            {
                throw failure;
            }
            return Task.FromResult(new SttBenchEngine(new FakeEngine(), null));
        }
    }

    private sealed class FakeEngine : ISttEngine
    {
        public TranscriptionMode Mode => TranscriptionMode.Fast;

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<SttHypotheses> TranscribeAsync(float[] samples16k, string? prompt, CancellationToken ct) =>
            Task.FromResult(SttHypotheses.Single("november five one four romeo mike", null, false));

        public void Dispose() { }
    }

    private sealed class NoGpu : IGpuMemorySampler
    {
        public long Sample() => -1;
    }

    private sealed class FixedMemory : IProcessMemorySampler
    {
        public long WorkingSetBytes() => 100;

        public long PeakWorkingSetBytes() => 100;
    }
}
