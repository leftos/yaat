using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>Nearest-rank latency percentiles, and the per-arm summary that leaves cached segments out of them.</summary>
public sealed class CorpusWerLatencyTests
{
    [Theory]
    [InlineData(50)]
    [InlineData(90)]
    public void One_Value_Is_Its_Own_Percentile(int percentile) => Assert.Equal(42.0, CorpusWerLatency.Percentile([42], percentile));

    [Fact]
    public void Nearest_Rank_Picks_The_Value_At_The_Rank()
    {
        double[] shuffled = [7, 3, 10, 1, 9, 4, 2, 8, 6, 5];

        Assert.Equal(5.0, CorpusWerLatency.Percentile(shuffled, 50));
        Assert.Equal(9.0, CorpusWerLatency.Percentile(shuffled, 90));
        Assert.Equal(10.0, CorpusWerLatency.Percentile(shuffled, 100));
    }

    [Fact]
    public void No_Values_Have_No_Percentile() => Assert.Equal(0.0, CorpusWerLatency.Percentile([], 50));

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void A_Percentile_Outside_One_To_A_Hundred_Throws(int percentile) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CorpusWerLatency.Percentile([1, 2, 3], percentile));

    [Fact]
    public void Cached_Segments_Are_Counted_And_Left_Out_Of_The_Percentiles()
    {
        CorpusWerTiming timing = CorpusWerLatency.Summarize(
            "cloud",
            1.5,
            [(100, false), (500, true), (200, false), (300, false)],
            new CorpusWerMemory(1, 2, -1, -1)
        );

        Assert.Equal(3, timing.TimedSegments);
        Assert.Equal(1, timing.CachedSegments);
        Assert.Equal(200.0, timing.LatencyP50Ms);
        Assert.Equal(300.0, timing.LatencyP90Ms);
        Assert.Equal(300.0, timing.LatencyMaxMs);
    }

    [Fact]
    public void Every_Segment_Cached_Summarizes_As_No_Timed_Segments()
    {
        CorpusWerTiming timing = CorpusWerLatency.Summarize("cloud", 0, [(100, true)], new CorpusWerMemory(1, 2, -1, -1));

        Assert.Equal(0, timing.TimedSegments);
        Assert.Equal(1, timing.CachedSegments);
        Assert.Equal(0.0, timing.LatencyP50Ms);
        Assert.Equal(0.0, timing.LatencyP90Ms);
        Assert.Equal(0.0, timing.LatencyMaxMs);
    }
}
