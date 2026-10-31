using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>The fixed-size slices the streaming engine feeds a clip in, the shorter last one included.</summary>
public sealed class StreamingChunkTests
{
    [Fact]
    public void No_Samples_Have_No_Ranges() => Assert.Empty(StreamingChunks.Ranges(0, 1600));

    [Fact]
    public void A_Short_Clip_Is_One_Shorter_Range() => AssertRanges(1000, 1600, [(0, 1000)]);

    [Fact]
    public void A_Clip_Of_Two_Chunks_Is_Two_Full_Ranges() => AssertRanges(3200, 1600, [(0, 1600), (1600, 1600)]);

    [Fact]
    public void A_Clip_Past_A_Chunk_End_Ends_With_Its_Remainder() => AssertRanges(3500, 1600, [(0, 1600), (1600, 1600), (3200, 300)]);

    [Fact]
    public void A_Chunk_Of_No_Samples_Throws() => Assert.Throws<ArgumentOutOfRangeException>(() => StreamingChunks.Ranges(1000, 0));

    private static void AssertRanges(int totalSamples, int chunkSamples, IReadOnlyList<(int Start, int Length)> expected) =>
        Assert.Equal(expected, StreamingChunks.Ranges(totalSamples, chunkSamples));
}
