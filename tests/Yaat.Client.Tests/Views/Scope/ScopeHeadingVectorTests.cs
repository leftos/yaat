using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The Scope's elastic heading vector as a pure state machine: a press arms it from the press point, a move past the drag
/// threshold turns a release into the confirm, a release without one arms a click-confirm, a left press while armed confirms
/// toward its press point, a right press cancels, and the magnetic heading snaps to five degrees with zero read as 360.
/// </summary>
public class ScopeHeadingVectorTests
{
    private const string Callsign = "UAL238";

    // A Bay Area position: its declination is what the magnetic conversion reads.
    private static readonly LatLon Aircraft = new(37.75, -122.40);

    private static readonly LatLon Dragged = new(37.95, -122.10);

    private static ScopeHeadingVectorState Begin() => ScopeHeadingVectorState.Begin(Callsign, Aircraft, 100, 100);

    private static ScopeHeadingVectorState Armed() => Begin().Released(Aircraft).State!.Value;

    [Fact]
    public void Begin_HoldsTheButton_FromThePressPoint()
    {
        ScopeHeadingVectorState state = Begin();

        Assert.Equal(Callsign, state.Callsign);
        Assert.Equal(Aircraft, state.Cursor);
        Assert.Equal(100, state.DragOriginX);
        Assert.Equal(100, state.DragOriginY);
        Assert.True(state.ButtonHeld);
        Assert.False(state.DraggedPastThreshold);
    }

    [Fact]
    public void MovedPastTheThreshold_SetsDraggedPastThreshold()
    {
        ScopeHeadingVectorState state = Begin().MovedTo(Dragged, 105, 100);

        Assert.True(state.DraggedPastThreshold);
        Assert.Equal(Dragged, state.Cursor);
    }

    [Fact]
    public void MovedWithinTheThreshold_LeavesItUncrossed()
    {
        ScopeHeadingVectorState state = Begin().MovedTo(Dragged, 103, 100);

        Assert.False(state.DraggedPastThreshold);
    }

    [Fact]
    public void ReleaseWithoutMovement_ArmsTheClickConfirm()
    {
        ScopeHeadingVectorTransition transition = Begin().Released(Aircraft);

        Assert.Equal(ScopeHeadingVectorOutcome.None, transition.Outcome);
        Assert.True(transition.State.HasValue);
        Assert.False(transition.State.Value.ButtonHeld);
    }

    [Fact]
    public void ReleaseAfterADrag_Confirms()
    {
        ScopeHeadingVectorTransition transition = Begin().MovedTo(Dragged, 160, 40).Released(Dragged);

        Assert.Equal(ScopeHeadingVectorOutcome.Confirm, transition.Outcome);
        Assert.Equal(Dragged, transition.State!.Value.Cursor);
    }

    [Fact]
    public void ReleaseWithTheButtonAlreadyUp_ChangesOnlyTheCursor()
    {
        ScopeHeadingVectorTransition transition = Armed().Released(Dragged);

        Assert.Equal(ScopeHeadingVectorOutcome.None, transition.Outcome);
        Assert.Equal(Dragged, transition.State!.Value.Cursor);
        Assert.False(transition.State.Value.ButtonHeld);
    }

    [Fact]
    public void LeftPressWhileArmed_ConfirmsTowardThePressPoint()
    {
        ScopeHeadingVectorTransition transition = Armed().Pressed(left: true, Dragged);

        Assert.Equal(ScopeHeadingVectorOutcome.Confirm, transition.Outcome);
        Assert.Equal(Dragged, transition.State!.Value.Cursor);
    }

    [Fact]
    public void RightPressWhileArmed_Cancels()
    {
        ScopeHeadingVectorTransition transition = Armed().Pressed(left: false, Dragged);

        Assert.Equal(ScopeHeadingVectorOutcome.Cancel, transition.Outcome);
        Assert.Null(transition.State);
    }

    [Fact]
    public void MovingWhileArmed_LeavesTheThresholdUncrossed()
    {
        ScopeHeadingVectorState moved = Armed().MovedTo(Dragged, 500, 500);

        Assert.False(moved.DraggedPastThreshold);
        Assert.Equal(Dragged, moved.Cursor);
    }

    [Theory]
    [InlineData(272, 270)]
    [InlineData(273, 275)]
    [InlineData(357.6, 360)]
    [InlineData(0, 360)]
    [InlineData(2.4, 360)]
    [InlineData(2.6, 5)]
    [InlineData(360, 360)]
    public void SnapHeadingTo5_MapsToTheNearestFive_WithZeroReadAs360(double magneticDeg, int expected) =>
        Assert.Equal(expected, ScopeHeadingVectorState.SnapHeadingTo5(magneticDeg));

    [Fact]
    public void MagneticHeading_ConvertsTheTrueBearingAtTheAircraftPosition()
    {
        double trueBearing = GeoMath.BearingTo(Aircraft, Dragged);
        int expected = ScopeHeadingVectorState.SnapHeadingTo5(MagneticDeclination.TrueToMagnetic(trueBearing, Aircraft));

        Assert.Equal(expected, ScopeHeadingVectorState.MagneticHeading(Aircraft, Dragged));
    }

    [Fact]
    public void FormatEta_RendersMinutesAndSeconds_WithDashesBelowOneKnot()
    {
        Assert.Equal("0:48", ScopeHeadingVectorState.FormatEta(3.2, 240));
        Assert.Equal("--:--", ScopeHeadingVectorState.FormatEta(3.2, 0.5));
    }
}
