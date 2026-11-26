using Xunit;
using Yaat.Client.Views.Scope.Toolbar.Dcb;

namespace Yaat.Client.Tests.Views.Scope.Toolbar;

/// <summary>The STARS spinner latch: a press latches, the wheel steps within bounds, a press accepts and Esc restores.</summary>
public class DcbSpinnerLatchTests
{
    /// <summary>A latch at 40 stepping by 5 within 20 and 60.</summary>
    private static DcbSpinnerLatch Latch() => new(value: 40, min: 20, max: 60, step: 5);

    [Fact]
    public void Press_Latches()
    {
        DcbSpinnerLatch latch = Latch();

        DcbLatchResult result = latch.Press();

        Assert.Equal(DcbLatchStep.Latched, result.Step);
        Assert.Equal(40d, result.Value);
        Assert.Equal(DcbLatchState.Latched, latch.State);
        Assert.Equal(40d, latch.Value);
    }

    [Fact]
    public void Wheel_WhileLatched_StepsWithinBounds()
    {
        DcbSpinnerLatch latch = Latch();
        latch.Press();

        DcbLatchResult? up = latch.Wheel(1);
        Assert.NotNull(up);
        Assert.Equal(DcbLatchStep.Preview, up.Value.Step);
        Assert.Equal(45d, latch.Value);

        // Up to the top, then the wheel keeps it there.
        latch.Wheel(1);
        latch.Wheel(1);
        latch.Wheel(1);
        Assert.Equal(60d, latch.Value);
        latch.Wheel(1);
        Assert.Equal(60d, latch.Value);

        // And down past the bottom, which keeps it at the bottom.
        for (int i = 0; i < 9; i++)
        {
            latch.Wheel(-1);
        }

        Assert.Equal(20d, latch.Value);
    }

    [Fact]
    public void Wheel_WhileIdle_DoesNothing()
    {
        DcbSpinnerLatch latch = Latch();

        Assert.Null(latch.Wheel(1));
        Assert.Null(latch.Wheel(-3));
        Assert.Equal(40d, latch.Value);
        Assert.Equal(DcbLatchState.Idle, latch.State);
    }

    [Fact]
    public void SecondPress_Accepts()
    {
        DcbSpinnerLatch latch = Latch();
        latch.Press();
        latch.Wheel(-1);

        DcbLatchResult accepted = latch.Press();

        Assert.Equal(DcbLatchStep.Accept, accepted.Step);
        Assert.Equal(35d, accepted.Value);
        Assert.Equal(35d, latch.Value);
        Assert.Equal(DcbLatchState.Idle, latch.State);
    }

    [Fact]
    public void Escape_RestoresTheLatchValue()
    {
        DcbSpinnerLatch latch = Latch();
        latch.Press();
        latch.Wheel(1);
        latch.Wheel(1);

        DcbLatchResult? cancelled = latch.Escape();

        Assert.NotNull(cancelled);
        Assert.Equal(DcbLatchStep.Cancel, cancelled.Value.Step);
        Assert.Equal(40d, cancelled.Value.Value);
        Assert.Equal(40d, latch.Value);
        Assert.Equal(DcbLatchState.Idle, latch.State);
        Assert.Null(latch.Escape());
    }

    [Fact]
    public void Accept_ThenPress_LatchesAgain()
    {
        DcbSpinnerLatch latch = Latch();
        latch.Press();
        latch.Wheel(1);
        latch.Press();

        DcbLatchResult again = latch.Press();

        Assert.Equal(DcbLatchStep.Latched, again.Step);
        Assert.Equal(DcbLatchState.Latched, latch.State);
        Assert.Equal(45d, latch.Value);

        latch.Escape();
        Assert.Equal(45d, latch.Value);
    }
}
