using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech.Bench;

namespace Yaat.Client.Tests;

/// <summary>Which rows a benchmark run holds, in which order, and which of them are skipped and why.</summary>
public sealed class SttBenchPlanTests
{
    private const string AccurateReason = "this speech-recognition build cannot run Accurate mode";
    private const string GpuReason = "needs the GPU runtime (not installed)";

    private static readonly SttEngine[] TwoModels = [SttEngine.RasrV2Fp32, SttEngine.RasrV2Int8];

    [Fact]
    public void Two_Models_With_Everything_Available_Give_Eight_Rows_In_Mock_Order()
    {
        IReadOnlyList<SttBenchPlanRow> rows = SttBenchPlan.Build(TwoModels, null, null);

        SttBenchPlanRow[] expected =
        [
            new(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu, null),
            new(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Gpu, null),
            new(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu, null),
            new(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Gpu, null),
            new(SttEngine.RasrV2Int8, SttMode.Accurate, SttDevice.Cpu, null),
            new(SttEngine.RasrV2Int8, SttMode.Accurate, SttDevice.Gpu, null),
            new(SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Cpu, null),
            new(SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Gpu, null),
        ];
        Assert.Equal(expected, rows);
    }

    [Fact]
    public void Models_Keep_The_Order_They_Were_Given()
    {
        IReadOnlyList<SttBenchPlanRow> rows = SttBenchPlan.Build([SttEngine.RasrV2Int8, SttEngine.RasrV2Fp32], null, null);

        Assert.Equal(SttEngine.RasrV2Int8, rows[0].Engine);
        Assert.Equal(SttEngine.RasrV2Fp32, rows[^1].Engine);
    }

    [Fact]
    public void No_Gpu_Runtime_Keeps_Eight_Rows_And_Skips_The_Four_Gpu_Ones_With_The_Reason()
    {
        IReadOnlyList<SttBenchPlanRow> rows = SttBenchPlan.Build(TwoModels, null, GpuReason);

        Assert.Equal(8, rows.Count);
        Assert.All(rows, row => Assert.Equal((row.Device == SttDevice.Gpu) ? GpuReason : null, row.SkipReason));
        Assert.Equal(4, rows.Count(row => row.SkipReason is not null));
    }

    [Fact]
    public void Accurate_Unavailable_Skips_The_Accurate_Rows_With_The_Reason()
    {
        IReadOnlyList<SttBenchPlanRow> rows = SttBenchPlan.Build(TwoModels, AccurateReason, null);

        Assert.Equal(8, rows.Count);
        Assert.All(rows, row => Assert.Equal((row.Mode == SttMode.Accurate) ? AccurateReason : null, row.SkipReason));
    }

    [Fact]
    public void A_Row_Both_Reasons_Hit_Names_The_Accurate_One()
    {
        IReadOnlyList<SttBenchPlanRow> rows = SttBenchPlan.Build([SttEngine.RasrV2Fp32], AccurateReason, GpuReason);

        Assert.Equal([AccurateReason, AccurateReason, null, GpuReason], rows.Select(row => row.SkipReason));
    }

    [Fact]
    public void No_Models_Give_No_Rows() => Assert.Empty(SttBenchPlan.Build([], null, null));

    [Fact]
    public void Whisper_Never_Gets_A_Row() => Assert.Empty(SttBenchPlan.Build([SttEngine.Whisper], null, null));
}
