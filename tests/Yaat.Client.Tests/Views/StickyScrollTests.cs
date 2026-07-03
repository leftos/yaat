using Xunit;
using Yaat.Client.Views.VStrips;

namespace Yaat.Client.Tests.Views;

// Decision logic for the vStrips racks' sticky-bottom scroll. Deltas are
// "current minus previous", matching ScrollChangedEventArgs.
public class StickyScrollTests
{
    private const double Eps = 1.0;

    [Fact]
    public void GrewWhileAtBottom_PinsToNewBottom()
    {
        // Was at bottom (prevOffset 26 == prevMax 26), content grew by 74; new max is 100.
        var pinned = StickyScroll.PinnedBottomOffset(
            offsetY: 26,
            extentHeight: 200,
            viewportHeight: 100,
            offsetDeltaY: 0,
            extentDeltaY: 74,
            viewportDeltaY: 0,
            epsilon: Eps
        );

        Assert.Equal(100, pinned);
    }

    [Fact]
    public void GrewWhileScrolledUp_DoesNotPin()
    {
        // prevMax = (200-74) - 100 = 26; prevOffset 0 is not at the bottom.
        var pinned = StickyScroll.PinnedBottomOffset(
            offsetY: 0,
            extentHeight: 200,
            viewportHeight: 100,
            offsetDeltaY: 0,
            extentDeltaY: 74,
            viewportDeltaY: 0,
            epsilon: Eps
        );

        Assert.Null(pinned);
    }

    [Fact]
    public void ContentShrank_DoesNotPin()
    {
        var pinned = StickyScroll.PinnedBottomOffset(
            offsetY: 26,
            extentHeight: 126,
            viewportHeight: 100,
            offsetDeltaY: 0,
            extentDeltaY: -74,
            viewportDeltaY: 0,
            epsilon: Eps
        );

        Assert.Null(pinned);
    }

    [Fact]
    public void AlreadyAtNewBottom_NoRepinNeeded()
    {
        // Was at bottom AND the current offset already equals the new max — nothing to do.
        var pinned = StickyScroll.PinnedBottomOffset(
            offsetY: 100,
            extentHeight: 200,
            viewportHeight: 100,
            offsetDeltaY: 74,
            extentDeltaY: 74,
            viewportDeltaY: 0,
            epsilon: Eps
        );

        Assert.Null(pinned);
    }
}
