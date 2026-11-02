using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech;
using Yaat.Client.Services.Speech.Bench;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// The real benchmark engine source over a model folder of empty files, with the n-best probe scripted, so no
/// sherpa-onnx native call is made.
/// </summary>
public sealed class FactorySttBenchEngineSourceTests : IDisposable
{
    private const SttEngine Rasr = SttEngine.RasrV2Int8;

    private static readonly string ClipsDirectory = Path.Combine(AppContext.BaseDirectory, "Speech", "BenchClips");

    private readonly string _installRoot = Directory.CreateTempSubdirectory("stt-bench-source-").FullName;

    public void Dispose() => Directory.Delete(_installRoot, recursive: true);

    [Fact]
    public async Task A_Whisper_Job_Fails_The_Row_As_Not_Benchmarked()
    {
        var source = new FactorySttBenchEngineSource(_ => null);
        SttBenchJob job = Job(SttEngine.Whisper, SttMode.Fast, Path.Combine(_installRoot, "whisper"));

        SttBenchResult result = await SttBenchRunner.RunAsync(job, source, NullMeters(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("load failed: Whisper is not benchmarked", result.Failure);
    }

    [Fact]
    public async Task An_Accurate_Job_With_A_Native_Problem_Returns_The_Reason_And_A_Fast_Engine()
    {
        var source = new FactorySttBenchEngineSource(_ => "this native has no n-best");

        SttBenchEngine created = await source.CreateAsync(Job(Rasr, SttMode.Accurate, ModelDirectory()), CancellationToken.None);

        using (created.Engine)
        {
            Assert.Equal("this native has no n-best", created.AccurateUnavailableReason);
            Assert.Equal(TranscriptionMode.Fast, created.Engine.Mode);
        }
    }

    [Fact]
    public async Task An_Accurate_Job_Without_A_Native_Problem_Returns_A_Beam_Engine()
    {
        var source = new FactorySttBenchEngineSource(_ => null);

        SttBenchEngine created = await source.CreateAsync(Job(Rasr, SttMode.Accurate, ModelDirectory()), CancellationToken.None);

        using (created.Engine)
        {
            Assert.Null(created.AccurateUnavailableReason);
            Assert.Equal(TranscriptionMode.Accurate, created.Engine.Mode);
        }
    }

    [Fact]
    public async Task A_Fast_Job_Returns_A_Greedy_Engine_Without_Probing()
    {
        int probes = 0;
        var source = new FactorySttBenchEngineSource(_ =>
        {
            probes++;
            return "never asked";
        });

        SttBenchEngine created = await source.CreateAsync(Job(Rasr, SttMode.Fast, ModelDirectory()), CancellationToken.None);

        using (created.Engine)
        {
            Assert.Null(created.AccurateUnavailableReason);
            Assert.Equal(TranscriptionMode.Fast, created.Engine.Mode);
            Assert.Equal(0, probes);
        }
    }

    [Fact]
    public async Task A_Model_Folder_Not_Named_For_The_Engine_Is_Refused()
    {
        string other = Path.Combine(_installRoot, "some-other-model");
        var source = new FactorySttBenchEngineSource(_ => null);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.CreateAsync(Job(Rasr, SttMode.Fast, other), CancellationToken.None)
        );

        Assert.Contains("rasr-parakeet-v2-int8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Relative_Model_Folder_Fails_The_Row()
    {
        string relative = Path.Combine("models", "rasr-parakeet-v2-int8");
        var source = new FactorySttBenchEngineSource(_ => null);

        SttBenchResult result = await SttBenchRunner.RunAsync(Job(Rasr, SttMode.Fast, relative), source, NullMeters(), CancellationToken.None);

        Assert.Equal($"load failed: the model folder {relative} is not an absolute path", result.Failure);
    }

    [Fact]
    public async Task The_Model_Folder_Name_Is_Matched_Ignoring_Case()
    {
        string upper = Path.Combine(_installRoot, "RASR-PARAKEET-V2-INT8");
        var source = new FactorySttBenchEngineSource(_ => null);

        SttBenchEngine created = await source.CreateAsync(Job(Rasr, SttMode.Fast, upper), CancellationToken.None);

        created.Engine.Dispose();
    }

    private string ModelDirectory()
    {
        string dir = Path.Combine(_installRoot, "rasr-parakeet-v2-int8");
        Directory.CreateDirectory(dir);
        foreach (string name in (string[])["encoder.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt"])
        {
            File.WriteAllText(Path.Combine(dir, name), string.Empty);
        }
        return dir;
    }

    private SttBenchJob Job(SttEngine engine, SttMode mode, string modelDirectory) =>
        new(engine, mode, SttDevice.Cpu, modelDirectory, ClipsDirectory, Path.Combine(_installRoot, "result.json"));

    private static SttBenchMeters NullMeters() => new(new NoGpu(), new FixedMemory(), TimeProvider.System);

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
