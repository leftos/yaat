using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests;

public class AltitudeRestrictionCommandTests
{
    [Theory]
    [InlineData(
        "CM B025",
        2500,
        AltitudeAssignmentModifier.AtOrBelow,
        "[N123AB] maintain VFR at or below two thousand five hundred, november one two three alpha bravo."
    )]
    [InlineData(
        "CM B2500",
        2500,
        AltitudeAssignmentModifier.AtOrBelow,
        "[N123AB] maintain VFR at or below two thousand five hundred, november one two three alpha bravo."
    )]
    [InlineData(
        "CM A025",
        2500,
        AltitudeAssignmentModifier.AtOrAbove,
        "[N123AB] maintain VFR at or above two thousand five hundred, november one two three alpha bravo."
    )]
    [InlineData(
        "CM A2500",
        2500,
        AltitudeAssignmentModifier.AtOrAbove,
        "[N123AB] maintain VFR at or above two thousand five hundred, november one two three alpha bravo."
    )]
    public void ClimbMaintain_AltitudeRestriction_ParsesAndReadsBackVfr(
        string text,
        int expectedAltitude,
        AltitudeAssignmentModifier expectedModifier,
        string expectedReadback
    )
    {
        var parsed = CommandParser.Parse(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);

        var command = Assert.IsType<ClimbMaintainCommand>(parsed.Value);
        Assert.Equal(expectedAltitude, command.Altitude);
        Assert.Equal(expectedModifier, command.Modifier);

        var aircraft = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" },
        };
        var compound = new CompoundCommand([new ParsedBlock(null, [command])]);

        var readback = PilotResponder.BuildReadback(compound, aircraft);
        Assert.Equal(expectedReadback, readback);
    }

    [Fact]
    public void ClimbMaintain_AtOrBelowVfr_SetsCeilingWithoutClimbWhenAlreadyBelow()
    {
        var aircraft = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Altitude = 1800,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" },
        };
        var result = FlightCommandHandler.ApplyClimbMaintain(new ClimbMaintainCommand(2500, AltitudeAssignmentModifier.AtOrBelow), aircraft);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2500, aircraft.Targets.AltitudeCeiling);
        Assert.Null(aircraft.Targets.AltitudeFloor);
        Assert.Null(aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void ClimbMaintain_AtOrAboveVfr_SetsFloorAndClimbsWhenBelow()
    {
        var aircraft = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Altitude = 1800,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" },
        };
        var result = FlightCommandHandler.ApplyClimbMaintain(new ClimbMaintainCommand(2500, AltitudeAssignmentModifier.AtOrAbove), aircraft);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2500, aircraft.Targets.AltitudeFloor);
        Assert.Null(aircraft.Targets.AltitudeCeiling);
        Assert.Equal(2500, aircraft.Targets.TargetAltitude);
    }

    [Theory]
    [InlineData("CM 025-")]
    [InlineData("CM 025+")]
    public void ClimbMaintain_TrailingAltitudeRestrictionSyntax_IsNotSupported(string text)
    {
        var parsed = CommandParser.Parse(text);
        Assert.False(parsed.IsSuccess);
    }
}
