using System.Text.Json;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech;
using Yaat.Client.Services.Speech.Bench;

namespace Yaat.Client.Tests;

/// <summary>The job and result files the benchmark parent and its child exchange.</summary>
public sealed class SttBenchJsonTests
{
    [Fact]
    public void A_Job_Round_Trips_With_Its_Enums_As_Names()
    {
        var job = new SttBenchJob(
            SttEngine.RasrV2Int8,
            SttMode.Accurate,
            SttDevice.Gpu,
            @"C:\models\int8",
            @"C:\app\Speech\BenchClips",
            @"C:\tmp\r.json"
        );

        string json = JsonSerializer.Serialize(job, SttBenchJsonContext.Default.SttBenchJob);
        SttBenchJob? back = JsonSerializer.Deserialize(json, SttBenchJsonContext.Default.SttBenchJob);

        Assert.Equal(job, back);
        Assert.Contains("\"RasrV2Int8\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Accurate\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Gpu\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Successful_Result_Round_Trips_With_Its_Timing()
    {
        var timing = new CorpusWerTiming("gpu", 6.5, 12, 0, 61.2, 88.4, 120.0, 400_000_000, 3_100_000_000, 100_000_000, 3_400_000_000, null);
        var result = new SttBenchResult(true, null, timing, 12, SttMode.Fast);

        string json = JsonSerializer.Serialize(result, SttBenchJsonContext.Default.SttBenchResult);
        SttBenchResult? back = JsonSerializer.Deserialize(json, SttBenchJsonContext.Default.SttBenchResult);

        Assert.Equal(result, back);
        Assert.Contains("\"Fast\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Failed_Result_Round_Trips_Without_Timing()
    {
        var result = new SttBenchResult(false, "accurate mode unavailable: the engine loaded in Fast mode", null, 12, SttMode.Fast);

        string json = JsonSerializer.Serialize(result, SttBenchJsonContext.Default.SttBenchResult);
        SttBenchResult? back = JsonSerializer.Deserialize(json, SttBenchJsonContext.Default.SttBenchResult);

        Assert.Equal(result, back);
    }
}
