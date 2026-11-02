using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech;
using Yaat.Client.Services.Speech.Bench;

namespace Yaat.Client.Tests;

/// <summary>The figures and lines the speech benchmark window and the Settings line show.</summary>
public sealed class SttBenchTextTests
{
    private static readonly SttBenchPlanRow FastGpuRow = new(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Gpu, null);

    [Fact]
    public void Load_ShowsSecondsToOneDecimal() => Assert.Equal("6.2 s", SttBenchText.Load(Timing(loadSeconds: 6.24)));

    [Fact]
    public void Milliseconds_ShowsWholeMilliseconds() => Assert.Equal("410", SttBenchText.Milliseconds(409.6));

    [Fact]
    public void Bytes_ShowsGigabytesToOneDecimal() => Assert.Equal("1.1 GB", SttBenchText.Bytes(1_100_000_000));

    [Fact]
    public void Bytes_AtZero_IsADash() => Assert.Equal("—", SttBenchText.Bytes(0));

    [Fact]
    public void Bytes_NotMeasured_IsADash() => Assert.Equal("—", SttBenchText.Bytes(-1));

    [Fact]
    public void Wer_ShowsAPercentToOneDecimal() => Assert.Equal("7.8%", SttBenchText.Wer(0.078));

    [Fact]
    public void Wer_Null_IsADash() => Assert.Equal("—", SttBenchText.Wer(null));

    [Fact]
    public void Recall_ShowsAWholePercent() => Assert.Equal("96%", SttBenchText.Recall(0.961));

    [Fact]
    public void Recall_Null_IsADash() => Assert.Equal("—", SttBenchText.Recall(null));

    [Fact]
    public void RunCounts_CountsRanFailedAndAllRows()
    {
        SttBenchLastRow[] rows = [Measured(), Measured(), Failed(), Skipped()];

        Assert.Equal(new SttBenchRunCounts(2, 1, 4), SttBenchText.RunCounts(rows));
    }

    [Fact]
    public void LastRunLine_ShowsTheLocalTimeAndTheRanCount()
    {
        var athens = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
        var run = new SttBenchLastRun(new DateTimeOffset(2026, 11, 1, 12, 2, 0, TimeSpan.Zero), [Measured(), Failed(), Skipped()]);

        Assert.Equal("Last run 1 Nov 2026, 14:02 · 1 of 3 ran", SttBenchText.LastRunLine(run, athens));
    }

    [Fact]
    public void RunningLine_NamesTheCurrentRow()
    {
        var progress = new SttBenchProgress(2, 8, FastGpuRow);

        Assert.Equal("Running 3 of 8: rasr-parakeet v2 · fp32 · Accurate · GPU", SttBenchText.RunningLine(progress));
    }

    [Fact]
    public void RunningLine_BetweenRows_ShowsOnlyTheCount() =>
        Assert.Equal("Running 2 of 8", SttBenchText.RunningLine(new SttBenchProgress(2, 8, null)));

    private static CorpusWerTiming Timing(double loadSeconds) => new("cpu", loadSeconds, 12, 0, 410, 640, 700, 0, 1_100_000_000, -1, -1, null);

    private static SttBenchLastRow Measured() =>
        new(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu, SttBenchResult.Measured(Timing(6.0), 12, SttMode.Fast), null);

    private static SttBenchLastRow Failed() =>
        new(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu, SttBenchResult.Failed("native crashed", 0, null), null);

    private static SttBenchLastRow Skipped() => new(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Gpu, null, "needs the GPU runtime (not installed)");
}
