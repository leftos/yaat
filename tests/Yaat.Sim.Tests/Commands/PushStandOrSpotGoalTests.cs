using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// <see cref="GroundCommandHandler.ResolveStandOrSpotGoal"/> on the real SFO layout: the body both the server's
/// <c>PUSH $spot</c> / <c>PUSH @stand</c> and the ground view's one-target preview resolve their destination through.
/// A spot takes the push's <c>FACE</c> heading converted to true, a stand refuses any facing (before the missing-layout
/// refusal), an unknown spot or stand is refused by name, and a node or marked-point destination is a caller bug that
/// throws.
/// </summary>
public class PushStandOrSpotGoalTests
{
    [Fact]
    public void SpotWithAFaceHeading_ReturnsTheTrueFacingAndTheForcedKind()
    {
        if (LoadSfo() is not { } layout)
        {
            return; // test data absent — skip
        }

        LatLon aircraft = layout.FindParkingByName("F8")!.Position;
        var face = new MagneticHeading(270);

        StandOrSpotGoal resolved = GroundCommandHandler.ResolveStandOrSpotGoal(
            aircraft,
            face,
            null,
            PushDestination.AtSpot("7A", PushbackLegKind.Pull),
            layout
        );

        Assert.Null(resolved.Refusal);
        Assert.NotNull(resolved.Goal);
        Assert.Equal(TugGoalKind.Spot, resolved.Goal!.Kind);
        Assert.Equal(layout.FindSpotNodeByName("7A")!.Id, resolved.Goal.Node!.Id);
        Assert.Equal(PushbackLegKind.Pull, resolved.Goal.ForcedKind);
        Assert.Equal(MagneticDeclination.MagneticToTrue(270, aircraft), resolved.FinalFacingTrueDeg!.Value, 9);
    }

    [Fact]
    public void StandWithAFacing_ReturnsTheStandFacingRefusal()
    {
        if (LoadSfo() is not { } layout)
        {
            return; // test data absent — skip
        }

        LatLon aircraft = layout.FindParkingByName("F8")!.Position;

        StandOrSpotGoal resolved = GroundCommandHandler.ResolveStandOrSpotGoal(
            aircraft,
            new MagneticHeading(180),
            null,
            PushDestination.AtParking("F8", null),
            layout
        );

        Assert.Null(resolved.Goal);
        Assert.Null(resolved.FinalFacingTrueDeg);
        Assert.Equal("PUSH @F8 does not take a facing — the aircraft parks on the stand's own heading", resolved.Refusal);
    }

    [Fact]
    public void UnknownSpot_IsRefusedByName()
    {
        if (LoadSfo() is not { } layout)
        {
            return; // test data absent — skip
        }

        LatLon aircraft = layout.FindParkingByName("F8")!.Position;

        StandOrSpotGoal resolved = GroundCommandHandler.ResolveStandOrSpotGoal(aircraft, null, null, PushDestination.AtSpot("X", null), layout);

        Assert.Null(resolved.Goal);
        Assert.Equal("Cannot find spot 'X'", resolved.Refusal);
    }

    [Fact]
    public void UnknownStand_IsRefusedByName()
    {
        if (LoadSfo() is not { } layout)
        {
            return; // test data absent — skip
        }

        LatLon aircraft = layout.FindParkingByName("F8")!.Position;

        StandOrSpotGoal resolved = GroundCommandHandler.ResolveStandOrSpotGoal(aircraft, null, null, PushDestination.AtParking("ZZ99", null), layout);

        Assert.Null(resolved.Goal);
        Assert.Equal("Cannot find parking 'ZZ99'", resolved.Refusal);
    }

    // The stand's facing refusal is the parser's own words, so it answers before the missing-layout refusal: a stand
    // takes no facing whatever the layout.
    [Fact]
    public void StandWithAHeading_AndNoLayout_ReturnsTheStandFacingRefusal()
    {
        StandOrSpotGoal resolved = GroundCommandHandler.ResolveStandOrSpotGoal(
            AnyPosition,
            new MagneticHeading(180),
            null,
            PushDestination.AtParking("F8", null),
            null
        );

        Assert.Null(resolved.Goal);
        Assert.Equal("PUSH @F8 does not take a facing — the aircraft parks on the stand's own heading", resolved.Refusal);
    }

    [Fact]
    public void SpotWithNoLayout_IsRefusedForTheMissingLayout()
    {
        StandOrSpotGoal resolved = GroundCommandHandler.ResolveStandOrSpotGoal(AnyPosition, null, null, PushDestination.AtSpot("7A", null), null);

        Assert.Null(resolved.Goal);
        Assert.Equal("No airport ground layout available", resolved.Refusal);
    }

    [Fact]
    public void StandFacingATaxiway_IsRefusedAsAFacing()
    {
        StandOrSpotGoal resolved = GroundCommandHandler.ResolveStandOrSpotGoal(AnyPosition, null, "A", PushDestination.AtParking("F8", null), null);

        Assert.Null(resolved.Goal);
        Assert.Equal("PUSH @F8 does not take a facing — the aircraft parks on the stand's own heading", resolved.Refusal);
    }

    // A node or a marked point is not a PUSH $spot / PUSH @stand destination: a caller bug, thrown before any refusal,
    // with or without a layout.
    [Fact]
    public void NodeOrMarkedPointDestination_Throws()
    {
        var node = PushDestination.AtNode(5, null);
        var markedPoint = PushDestination.AtFreePose(new PushFreePose(37.615, -122.39, null), null);

        Assert.Throws<ArgumentException>(() => GroundCommandHandler.ResolveStandOrSpotGoal(AnyPosition, null, null, node, null));
        Assert.Throws<ArgumentException>(() => GroundCommandHandler.ResolveStandOrSpotGoal(AnyPosition, null, null, markedPoint, null));

        if (LoadSfo() is not { } layout)
        {
            return; // test data absent — the layout half is skipped
        }

        Assert.Throws<ArgumentException>(() => GroundCommandHandler.ResolveStandOrSpotGoal(AnyPosition, null, null, node, layout));
        Assert.Throws<ArgumentException>(() => GroundCommandHandler.ResolveStandOrSpotGoal(AnyPosition, null, null, markedPoint, layout));
    }

    // SFO's ramp, for the resolutions that never reach a layout.
    private static readonly LatLon AnyPosition = new(37.615, -122.39);

    private static AirportGroundLayout? LoadSfo() => new TestAirportGroundData().GetLayout("SFO");
}
