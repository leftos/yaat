using Avalonia;
using Xunit;
using Yaat.Client.Views.Map;

namespace Yaat.Client.UI.Tests.Views;

// The right mouse button does two jobs on both map canvases: a click opens a context menu, a drag pans
// the view. They are only distinguishable by movement, so the decision is deferred to release. Deciding
// on press is what used to make a right-drag starting on a ground datablock, aircraft, or node fail to
// pan.
public class RightClickGestureTests
{
    [Fact]
    public void PressAndReleaseWithoutMoving_IsAClick()
    {
        var gesture = new RightClickGesture();
        gesture.Press(new Point(100, 100));

        Assert.Equal(new Point(100, 100), gesture.Release());
    }

    [Fact]
    public void PressAndReleaseAfterASmallJitter_IsStillAClick()
    {
        var gesture = new RightClickGesture();
        gesture.Press(new Point(100, 100));

        // 2px of hand tremor between press and release must not cost the user their menu.
        gesture.Move(new Point(102, 100));

        Assert.False(gesture.HasDragged);
        Assert.Equal(new Point(100, 100), gesture.Release());
    }

    [Fact]
    public void PressAndDragBeyondTheThreshold_IsADragAndOwesNoMenu()
    {
        var gesture = new RightClickGesture();
        gesture.Press(new Point(100, 100));

        gesture.Move(new Point(140, 100));

        Assert.True(gesture.HasDragged);
        Assert.Null(gesture.Release());
    }

    [Fact]
    public void ThresholdIsExclusiveAtFivePixels()
    {
        // Exactly 5 px away is 25 px² — the comparison is strictly greater, so this is still a click.
        var atThreshold = new RightClickGesture();
        atThreshold.Press(new Point(100, 100));
        atThreshold.Move(new Point(105, 100));
        Assert.False(atThreshold.HasDragged);

        var pastThreshold = new RightClickGesture();
        pastThreshold.Press(new Point(100, 100));
        pastThreshold.Move(new Point(106, 100));
        Assert.True(pastThreshold.HasDragged);
    }

    [Fact]
    public void ReturningToTheOriginMidDragDoesNotResurrectTheMenu()
    {
        // Panning out and back must not end in a context menu — once a drag, always a drag.
        var gesture = new RightClickGesture();
        gesture.Press(new Point(100, 100));

        gesture.Move(new Point(300, 300));
        gesture.Move(new Point(100, 100));

        Assert.True(gesture.HasDragged);
        Assert.Null(gesture.Release());
    }

    [Fact]
    public void ReleaseReportsThePressPositionNotTheReleasePosition()
    {
        // Anchoring the menu at the press point keeps it on a small target (a ground node) even when the
        // pointer drifted a pixel or two before button-up.
        var gesture = new RightClickGesture();
        gesture.Press(new Point(200, 150));
        gesture.Move(new Point(203, 152));

        Assert.Equal(new Point(200, 150), gesture.Release());
    }

    [Fact]
    public void MovementWithNoPressIsIgnored()
    {
        var gesture = new RightClickGesture();

        gesture.Move(new Point(500, 500));

        Assert.False(gesture.IsDown);
        Assert.False(gesture.HasDragged);
        Assert.Null(gesture.Release());
    }

    [Fact]
    public void ReleaseWithoutAPressOwesNothing()
    {
        Assert.Null(new RightClickGesture().Release());
    }

    [Fact]
    public void DoubleReleaseOnlyOwesOneMenu()
    {
        // Guards against a second release (or a released-twice event) reopening the menu.
        var gesture = new RightClickGesture();
        gesture.Press(new Point(10, 10));

        Assert.NotNull(gesture.Release());
        Assert.Null(gesture.Release());
    }

    [Fact]
    public void CancelAbandonsThePressWithoutOwingAMenu()
    {
        var gesture = new RightClickGesture();
        gesture.Press(new Point(10, 10));

        gesture.Cancel();

        Assert.False(gesture.IsDown);
        Assert.Null(gesture.Release());
    }

    [Fact]
    public void EachPressStartsFresh()
    {
        var gesture = new RightClickGesture();
        gesture.Press(new Point(0, 0));
        gesture.Move(new Point(200, 200));
        gesture.Release();

        // A drag must not poison the next press.
        gesture.Press(new Point(50, 50));

        Assert.False(gesture.HasDragged);
        Assert.Equal(new Point(50, 50), gesture.Release());
    }
}
