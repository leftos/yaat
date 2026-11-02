using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech;
using Yaat.Client.Services.Speech.Bench;

namespace Yaat.Client.Tests;

/// <summary>The last benchmark run's file: what it keeps across a restart, and the two ways it can have nothing to give.</summary>
public sealed class SttBenchLastRunTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stt-bench-last-run-").FullName;
    private readonly WarningLog _log = new();

    private string FilePath => Path.Combine(_dir, "stt-benchmark.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_Run_Round_Trips_With_Its_Measured_Failed_And_Skipped_Rows()
    {
        var timing = new CorpusWerTiming("cpu", 6.5, 12, 0, 300, 420, 500, 1_000, 2_000, -1, -1, null);
        SttBenchLastRow[] rows =
        [
            new(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu, SttBenchResult.Measured(timing, 12, SttMode.Accurate), null),
            new(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu, SttBenchResult.Failed("failed: exit 3", 0, null), null),
            new(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Gpu, null, "needs the GPU runtime (not installed)"),
        ];
        var run = new SttBenchLastRun(new DateTimeOffset(2026, 11, 1, 14, 2, 0, TimeSpan.Zero), rows);

        SttBenchLastRunStore.Save(FilePath, run);
        SttBenchLastRun? read = SttBenchLastRunStore.Load(FilePath, _log);

        Assert.NotNull(read);
        Assert.Equal(run.FinishedUtc, read.FinishedUtc);
        Assert.Equal(rows, read.Rows);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void A_Missing_File_Is_No_Last_Run_And_No_Warning()
    {
        Assert.Null(SttBenchLastRunStore.Load(FilePath, _log));
        Assert.Empty(_log.Warnings);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{ "finishedUtc": "2026-11-01T14:02:00+00:00", "rows": [ { "engine": "NoSuchModel" } ] }""")]
    public void A_Malformed_File_Is_No_Last_Run_With_A_Warning(string contents)
    {
        File.WriteAllText(FilePath, contents);

        Assert.Null(SttBenchLastRunStore.Load(FilePath, _log));
        Assert.Single(_log.Warnings);
    }

    private sealed class WarningLog : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
