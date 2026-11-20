using Avalonia;
using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Sim;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The Scope's elastic heading vector as a pure state machine: a press arms it from the press point, a move past the drag
/// threshold turns a release more than the threshold from the press into the confirm, any other release arms a click-confirm,
/// a left press while armed confirms toward its press point, a right press cancels, and the magnetic heading snaps to five
/// degrees with zero read as 360. Its geometry rolls straight ahead for the roll-in lead, turns at the type's turn rate and
/// runs the straight leg along the heading it sends, from the arc's end.
/// </summary>
public class ScopeHeadingVectorTests
{
    private const string Callsign = "UAL238";

    // A Bay Area position: its declination is what the magnetic conversion reads.
    private static readonly LatLon Aircraft = new(37.75, -122.40);

    private static readonly LatLon Dragged = new(37.95, -122.10);

    // A KOAK-area position for the vector's geometry.
    private static readonly LatLon Koak = new(37.72, -122.22);

    private static ScopeHeadingVectorState Begin() => ScopeHeadingVectorState.Begin(Callsign, Aircraft, new Point(100, 100));

    private static ScopeHeadingVectorState Armed() => Begin().Released(Aircraft, 100, 100).State!.Value;

    public ScopeHeadingVectorTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static ScopeHeadingVectorOverlay Overlay(double presentHeadingTrue, LatLon cursor) =>
        new(Callsign, Koak, presentHeadingTrue, 250, cursor, AircraftPerformance.TurnRate("B738", AircraftCategory.Jet), AircraftCategory.Jet);

    // An overlay for a type at its own category and turn rate, heading north from KOAK.
    private static ScopeHeadingVectorOverlay TypeOverlay(string aircraftType, double groundSpeedKts)
    {
        AircraftCategory category = AircraftCategorization.Categorize(aircraftType);
        return new(Callsign, Koak, 0, groundSpeedKts, Koak, AircraftPerformance.TurnRate(aircraftType, category), category);
    }

    [Fact]
    public void Begin_HoldsTheButton_FromThePressPoint()
    {
        ScopeHeadingVectorState state = Begin();

        Assert.Equal(Callsign, state.Callsign);
        Assert.Equal(Aircraft, state.Cursor);
        Assert.Equal(new Point(100, 100), state.DragOrigin);
        Assert.True(state.ButtonHeld);
        Assert.False(state.DraggedPastThreshold);
    }

    [Fact]
    public void MovedPastTheThreshold_SetsDraggedPastThreshold()
    {
        ScopeHeadingVectorState state = Begin().MovedTo(Dragged, 111, 100);

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
        ScopeHeadingVectorTransition transition = Begin().Released(Aircraft, 100, 100);

        Assert.Equal(ScopeHeadingVectorOutcome.None, transition.Outcome);
        Assert.True(transition.State.HasValue);
        Assert.False(transition.State.Value.ButtonHeld);
    }

    [Fact]
    public void ReleaseAfterADrag_Confirms()
    {
        ScopeHeadingVectorTransition transition = Begin().MovedTo(Dragged, 160, 40).Released(Dragged, 160, 40);

        Assert.Equal(ScopeHeadingVectorOutcome.Confirm, transition.Outcome);
        Assert.Equal(Dragged, transition.State!.Value.Cursor);
    }

    [Fact]
    public void ReleaseWithTheButtonAlreadyUp_ChangesOnlyTheCursor()
    {
        ScopeHeadingVectorTransition transition = Armed().Released(Dragged, 160, 40);

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

    [Fact]
    public void TurnRadius_FollowsTheTypesTurnRate()
    {
        double jetRadius = ScopeHeadingVectorState.TurnRadiusNm(250, AircraftPerformance.TurnRate("B738", AircraftCategory.Jet));
        double pistonRadius = ScopeHeadingVectorState.TurnRadiusNm(100, AircraftPerformance.TurnRate("C172", AircraftCategory.Piston));

        Assert.Equal(250 / (20 * Math.PI * 2.5), jetRadius, 0.01);
        Assert.Equal(100 / (20 * Math.PI * 3.0), pistonRadius, 0.01);
    }

    [Fact]
    public void LegAfterA90DegreeRightTurn_RunsAlongTheNewHeading()
    {
        LatLon cursor = GeoMath.ProjectPoint(Koak, new TrueHeading(90), 5);
        ScopeHeadingVectorGeometry geometry = ScopeHeadingVectorState.Geometry(Overlay(0, cursor), 90);
        double rate = AircraftPerformance.TurnRate("B738", AircraftCategory.Jet);
        double r = ScopeHeadingVectorState.TurnRadiusNm(250, rate);
        double lead = TurnShape.LeadNm(new TurnLeadInput(250, 90, rate, AircraftCategory.Jet, TurnModel.Ramp));

        Assert.True(geometry.HasArc);
        Assert.Equal(Math.Sqrt(((r + lead) * (r + lead)) + (r * r)), GeoMath.DistanceNm(Koak, geometry.ArcEnd), 0.02);
        Assert.Equal(5 - r, GeoMath.DistanceNm(geometry.ArcEnd, geometry.LegEnd), 0.05);
        Assert.Equal(90, GeoMath.BearingTo(geometry.ArcEnd, geometry.LegEnd), 0.5);
    }

    [Fact]
    public void LegAfterA180DegreeTurn_RunsAlongTheNewHeading()
    {
        LatLon cursor = GeoMath.ProjectPoint(Koak, new TrueHeading(180), 5);
        ScopeHeadingVectorGeometry geometry = ScopeHeadingVectorState.Geometry(Overlay(0, cursor), 180);

        Assert.True(geometry.HasArc);
        Assert.True(GeoMath.DistanceNm(geometry.ArcEnd, geometry.LegEnd) > 1);
        Assert.Equal(180, GeoMath.BearingTo(geometry.ArcEnd, geometry.LegEnd), 0.5);
    }

    [Fact]
    public void CursorBehindTheArcEnd_LeavesNoLeg()
    {
        // 1.45 NM east of the aircraft: behind the arc's end at the jet's 2.5 deg/s, but ahead of it at a 3 deg/s standard rate.
        LatLon cursor = GeoMath.ProjectPoint(Koak, new TrueHeading(90), 1.45);
        ScopeHeadingVectorGeometry geometry = ScopeHeadingVectorState.Geometry(Overlay(0, cursor), 90);

        Assert.True(geometry.HasArc);
        Assert.Equal(0, GeoMath.DistanceNm(geometry.ArcEnd, geometry.LegEnd), 0.0005);
    }

    [Fact]
    public void AChangeUnderThreeDegrees_DrawsNoArc_AndTheLegStartsAtTheAircraft()
    {
        LatLon cursor = GeoMath.ProjectPoint(Koak, new TrueHeading(2), 5);
        ScopeHeadingVectorGeometry geometry = ScopeHeadingVectorState.Geometry(Overlay(0, cursor), 2);

        Assert.False(geometry.HasArc);
        Assert.Equal(Koak, geometry.ArcEnd);
        Assert.True(GeoMath.DistanceNm(geometry.ArcEnd, geometry.LegEnd) > 1);
        Assert.Equal(2, GeoMath.BearingTo(geometry.ArcEnd, geometry.LegEnd), 0.5);
    }

    [Fact]
    public void AVectorWithNoArc_HasNoLead()
    {
        LatLon cursor = GeoMath.ProjectPoint(Koak, new TrueHeading(2), 5);
        ScopeHeadingVectorGeometry geometry = ScopeHeadingVectorState.Geometry(Overlay(0, cursor), 2);

        Assert.False(geometry.HasArc);
        Assert.Equal(Koak, geometry.LeadEnd);
    }

    [Fact]
    public void TurnFor_Jet_AtFullRate()
    {
        ScopeHeadingVectorTurn turn = ScopeHeadingVectorState.TurnFor(TypeOverlay("B738", 250), 90);

        Assert.Equal(2.5, turn.TurnRateDegPerSec, 4);
        Assert.Equal(1.5915, turn.RadiusNm, 4);
        Assert.Equal(0.1736, turn.LeadNm, 4);
    }

    [Fact]
    public void TurnFor_Piston_LeftTurnAtFullRate()
    {
        ScopeHeadingVectorTurn turn = ScopeHeadingVectorState.TurnFor(TypeOverlay("C172", 100), -90);

        Assert.Equal(3.0, turn.TurnRateDegPerSec, 4);
        Assert.Equal(0.5305, turn.RadiusNm, 4);
        Assert.Equal(0.0417, turn.LeadNm, 4);
    }

    [Fact]
    public void TurnFor_Jet_SmallTurn_ShorterLead()
    {
        ScopeHeadingVectorTurn turn = ScopeHeadingVectorState.TurnFor(TypeOverlay("B738", 250), 5);

        Assert.Equal(2.5, turn.TurnRateDegPerSec, 4);
        Assert.Equal(1.5915, turn.RadiusNm, 4);
        Assert.Equal(0.1502, turn.LeadNm, 4);
    }

    [Fact]
    public void TurnFor_Helicopter_UsesHelicopterRate()
    {
        ScopeHeadingVectorTurn turn = ScopeHeadingVectorState.TurnFor(TypeOverlay("H60", 100), 90);

        Assert.Equal(5.0, turn.TurnRateDegPerSec, 4);
        Assert.Equal(0.3183, turn.RadiusNm, 4);
    }

    [Fact]
    public void TurnGeometry_RightTurn_CentreIsAbeamRightOfTheLeadEnd()
    {
        var position = new LatLon(37.7, -122.2);
        ScopeHeadingVectorOverlay overlay = Overlay(0, position) with { Aircraft = position };
        var turn = new ScopeHeadingVectorTurn(2.5, 1.5, 0.2);

        (LatLon leadEnd, LatLon center) = ScopeHeadingVectorState.TurnGeometry(overlay, 90, turn);

        Assert.Equal(0.2, GeoMath.DistanceNm(position, leadEnd), 3);
        Assert.Equal(0, Math.Abs(GeoMath.SignedBearingDifference(0, GeoMath.BearingTo(position, leadEnd))), 3);
        Assert.Equal(1.5, GeoMath.DistanceNm(leadEnd, center), 2);
        Assert.Equal(90, GeoMath.BearingTo(leadEnd, center), 0);
    }

    [Fact]
    public void TurnGeometry_LeftTurn_CentreIsAbeamLeftOfTheLeadEnd()
    {
        var position = new LatLon(37.7, -122.2);
        ScopeHeadingVectorOverlay overlay = Overlay(0, position) with { Aircraft = position };
        var turn = new ScopeHeadingVectorTurn(2.5, 1.5, 0.2);

        (LatLon leadEnd, LatLon center) = ScopeHeadingVectorState.TurnGeometry(overlay, -90, turn);

        Assert.Equal(0.2, GeoMath.DistanceNm(position, leadEnd), 3);
        Assert.Equal(0, Math.Abs(GeoMath.SignedBearingDifference(0, GeoMath.BearingTo(position, leadEnd))), 3);
        Assert.Equal(1.5, GeoMath.DistanceNm(leadEnd, center), 2);
        Assert.Equal(270, GeoMath.BearingTo(leadEnd, center), 0);
    }
}
