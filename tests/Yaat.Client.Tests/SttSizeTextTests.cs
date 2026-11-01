using Xunit;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.Tests;

/// <summary>Model and runtime sizes as decimal gigabytes, the way Settings > Speech shows them.</summary>
public sealed class SttSizeTextTests
{
    private const long Fp32Bytes = 2_550_014_451;

    [Fact]
    public void Total_Has_Two_Decimals() => Assert.Equal("2.55 GB", SttSizeText.Total(Fp32Bytes));

    [Fact]
    public void Total_Of_Zero_Bytes() => Assert.Equal("0.00 GB", SttSizeText.Total(0));

    [Fact]
    public void Progress_Has_One_Decimal_For_The_Done_Part() => Assert.Equal("1.1 of 2.55 GB", SttSizeText.Progress(0.42, Fp32Bytes));

    [Fact]
    public void Ready_Names_The_Size_On_Disk() => Assert.Equal("Ready (0.67 GB on disk)", SttSizeText.Ready(670_123_456));

    [Theory]
    [InlineData(-0.5, "0.0 of 2.55 GB")]
    [InlineData(1.5, "2.55 of 2.55 GB")]
    [InlineData(1.0, "2.55 of 2.55 GB")]
    public void Progress_Clamps_The_Fraction_And_Never_Shows_More_Than_The_Total(double fraction, string expected) =>
        Assert.Equal(expected, SttSizeText.Progress(fraction, Fp32Bytes));

    [Fact]
    public void Progress_At_100_Percent_Shows_The_Totals_Own_Text() => Assert.Equal("2.54 of 2.54 GB", SttSizeText.Progress(1.0, 2_540_000_000));

    [Fact]
    public void Progress_Rounds_Down_When_Rounding_Would_Reach_The_Total() => Assert.Equal("0.6 of 0.67 GB", SttSizeText.Progress(0.99, 670_000_000));
}
