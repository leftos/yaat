using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// Verifies the discrete-spinner scroll accumulator: at sensitivity 1.0 every wheel event yields one
// step (historical behavior); lower sensitivity requires proportionally more events per step; a
// direction reversal resets the accumulator so it takes effect immediately.
public class ScrollStepAccumulatorTests
{
    [Fact]
    public void Sensitivity1_EmitsOneStepPerEvent()
    {
        var acc = new ScrollStepAccumulator();

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(1, acc.Accumulate(1, 1.0));
        }
    }

    [Fact]
    public void SensitivityQuarter_EmitsOneStepEveryFourEvents()
    {
        var acc = new ScrollStepAccumulator();

        Assert.Equal(0, acc.Accumulate(1, 0.25));
        Assert.Equal(0, acc.Accumulate(1, 0.25));
        Assert.Equal(0, acc.Accumulate(1, 0.25));
        Assert.Equal(1, acc.Accumulate(1, 0.25));
    }

    [Fact]
    public void DirectionReversal_ResetsAccumulator()
    {
        var acc = new ScrollStepAccumulator();

        // Build up partial forward accumulation (0.5, no step yet).
        Assert.Equal(0, acc.Accumulate(1, 0.25));
        Assert.Equal(0, acc.Accumulate(1, 0.25));

        // Reversing clears the partial forward accumulation; the reverse run starts fresh.
        Assert.Equal(0, acc.Accumulate(-1, 0.25));
        Assert.Equal(0, acc.Accumulate(-1, 0.25));
        Assert.Equal(0, acc.Accumulate(-1, 0.25));
        Assert.Equal(-1, acc.Accumulate(-1, 0.25));
    }

    [Fact]
    public void ZeroDirection_ReturnsZero()
    {
        var acc = new ScrollStepAccumulator();

        Assert.Equal(0, acc.Accumulate(0, 1.0));
    }

    [Fact]
    public void NegativeDirection_AtFullSensitivity_EmitsOneStepPerEvent()
    {
        var acc = new ScrollStepAccumulator();

        Assert.Equal(-1, acc.Accumulate(-1, 1.0));
        Assert.Equal(-1, acc.Accumulate(-1, 1.0));
    }
}
