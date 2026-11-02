using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech;
using Yaat.Client.Services.Speech.Bench;
using Yaat.Client.ViewModels;

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

    [Fact]
    public void RunningLine_EveryRowDone_DoesNotOvershoot() =>
        Assert.Equal("Running 8 of 8", SttBenchText.RunningLine(new SttBenchProgress(8, 8, null)));

    [Fact]
    public void SettingsSummary_NeverRun_SaysSo() =>
        Assert.Equal(
            new SttBenchSummaryLine("Never run on this machine", "", ""),
            SttBenchText.SettingsSummary(Finished(null), CpuSetting, TimeZoneInfo.Utc)
        );

    [Fact]
    public void SettingsSummary_Unloading_ShowsTheStatusMessage() =>
        Assert.Equal(
            new SttBenchSummaryLine("Unloading speech models…", "", ""),
            SttBenchText.SettingsSummary(
                new SttBenchSnapshot(SttBenchState.Unloading, Idle, "Unloading speech models…", Run(CpuMeasured)),
                CpuSetting,
                TimeZoneInfo.Utc
            )
        );

    [Fact]
    public void SettingsSummary_Running_CountsTheRowInFlight() =>
        Assert.Equal(
            new SttBenchSummaryLine("Running 3 of 8…", "", ""),
            SttBenchText.SettingsSummary(
                new SttBenchSnapshot(SttBenchState.Running, new SttBenchProgress(2, 8, FastGpuRow), null, Run(CpuMeasured)),
                CpuSetting,
                TimeZoneInfo.Utc
            )
        );

    [Fact]
    public void SettingsSummary_RunningWithEveryRowDone_DoesNotOvershoot() =>
        Assert.Equal(
            new SttBenchSummaryLine("Running 8 of 8…", "", ""),
            SttBenchText.SettingsSummary(
                new SttBenchSnapshot(SttBenchState.Running, new SttBenchProgress(8, 8, null), null, Run(CpuMeasured)),
                CpuSetting,
                TimeZoneInfo.Utc
            )
        );

    [Fact]
    public void SettingsSummary_AllRan_CountsTheRuns() =>
        Assert.Equal(
            new SttBenchSummaryLine("Last run 1 Nov 2026: 8 runs", "", ""),
            SttBenchText.SettingsSummary(Finished(Run([.. Enumerable.Repeat(GpuMeasured, 8)])), Unmeasured, TimeZoneInfo.Utc)
        );

    [Fact]
    public void SettingsSummary_SkippedRowsAndNoFailure_CountsWhatRan() =>
        Assert.Equal(
            new SttBenchSummaryLine("Last run 1 Nov 2026: 6 of 8 ran", "", ""),
            SttBenchText.SettingsSummary(Finished(Run([.. Enumerable.Repeat(GpuMeasured, 6), Skipped(), Skipped()])), Unmeasured, TimeZoneInfo.Utc)
        );

    [Fact]
    public void SettingsSummary_OneFailed_PutsTheFailedCountApart() =>
        Assert.Equal(
            new SttBenchSummaryLine("Last run 1 Nov 2026: 7 of 8 ran,", "1 failed", ""),
            SttBenchText.SettingsSummary(Finished(Run([.. Enumerable.Repeat(GpuMeasured, 7), CpuFailed])), Unmeasured, TimeZoneInfo.Utc)
        );

    [Fact]
    public void SettingsSummary_ThisSettingOnTheGpu_ShowsItsLatencyAndVram() =>
        Assert.Equal(
            new SttBenchSummaryLine("Last run 1 Nov 2026: 2 runs", "", "· this setting 60 ms p50, 3.6 GB VRAM"),
            SttBenchText.SettingsSummary(Finished(Run(GpuMeasured, CpuMeasured)), GpuSetting, TimeZoneInfo.Utc)
        );

    [Fact]
    public void SettingsSummary_ThisSettingOnTheCpu_ShowsItsLatencyAndRam() =>
        Assert.Equal(
            new SttBenchSummaryLine("Last run 1 Nov 2026: 2 runs", "", "· this setting 410 ms p50, 1.1 GB RAM"),
            SttBenchText.SettingsSummary(Finished(Run(GpuMeasured, CpuMeasured)), CpuSetting, TimeZoneInfo.Utc)
        );

    [Fact]
    public void SettingsSummary_TheCurrentSettingFailed_HasNoThisSettingPart() =>
        Assert.Equal(
            new SttBenchSummaryLine("Last run 1 Nov 2026: 1 of 2 ran,", "1 failed", ""),
            SttBenchText.SettingsSummary(Finished(Run(GpuMeasured, CpuFailed)), CpuSetting, TimeZoneInfo.Utc)
        );

    [Fact]
    public void CardMeasured_ASucceededGpuRow_ShowsItsLatencyAndVram() =>
        Assert.Equal(
            "60 ms p50, 3.6 GB VRAM (Accurate, GPU)",
            SttBenchText.CardMeasured(Run(GpuMeasured, CpuMeasured), SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Gpu)
        );

    [Fact]
    public void CardMeasured_ASucceededCpuRow_ShowsItsLatencyAndRam() =>
        Assert.Equal(
            "410 ms p50, 1.1 GB RAM (Fast, CPU)",
            SttBenchText.CardMeasured(Run(GpuMeasured, CpuMeasured), SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu)
        );

    [Fact]
    public void CardMeasured_AFailedRow_SaysItFailed() =>
        Assert.Equal(
            "Failed in the last benchmark run",
            SttBenchText.CardMeasured(Run(GpuMeasured, CpuFailed), SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu)
        );

    [Fact]
    public void CardMeasured_NoSuchRowOrNoRun_IsNotMeasured()
    {
        Assert.Equal(
            SttModelCardViewModel.NotMeasuredText,
            SttBenchText.CardMeasured(Run(GpuMeasured, CpuMeasured), SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Cpu)
        );
        Assert.Equal(SttModelCardViewModel.NotMeasuredText, SttBenchText.CardMeasured(null, SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu));
    }

    private static readonly SttBenchProgress Idle = new(0, 0, null);
    private static readonly (SttEngine, SttMode, SttDevice) CpuSetting = (SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu);
    private static readonly (SttEngine, SttMode, SttDevice) GpuSetting = (SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Gpu);
    private static readonly (SttEngine, SttMode, SttDevice) Unmeasured = (SttEngine.RasrV2Int8, SttMode.Accurate, SttDevice.Cpu);

    private static readonly SttBenchLastRow GpuMeasured = new(
        SttEngine.RasrV2Fp32,
        SttMode.Accurate,
        SttDevice.Gpu,
        SttBenchResult.Measured(new CorpusWerTiming("cuda", 6.2, 12, 0, 60, 90, 100, 0, 2_000_000_000, 0, 3_600_000_000, null), 12, SttMode.Accurate),
        null
    );

    private static readonly SttBenchLastRow CpuMeasured = Measured();

    private static readonly SttBenchLastRow CpuFailed = Failed();

    private static SttBenchSnapshot Finished(SttBenchLastRun? lastRun) => new(SttBenchState.Idle, Idle, null, lastRun);

    private static SttBenchLastRun Run(params SttBenchLastRow[] rows) => new(new DateTimeOffset(2026, 11, 1, 12, 2, 0, TimeSpan.Zero), rows);

    private static CorpusWerTiming Timing(double loadSeconds) => new("cpu", loadSeconds, 12, 0, 410, 640, 700, 0, 1_100_000_000, -1, -1, null);

    private static SttBenchLastRow Measured() =>
        new(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu, SttBenchResult.Measured(Timing(6.0), 12, SttMode.Fast), null);

    private static SttBenchLastRow Failed() =>
        new(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu, SttBenchResult.Failed("native crashed", 0, null), null);

    private static SttBenchLastRow Skipped() => new(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Gpu, null, "needs the GPU runtime (not installed)");
}
