using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class FlightCommandFeedbackTests
{
    private static AircraftState CreateAircraft(double heading = 180, double altitude = 5000, double declination = 0)
    {
        return new AircraftState
        {
            Callsign = "N98W",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            Latitude = 37.0,
            Longitude = -122.0,
            Declination = declination,
        };
    }

    // --- Altitude feedback ---

    [Fact]
    public void ClimbMaintain_NoPriorAltitude_NoSuffix()
    {
        var ac = CreateAircraft(altitude: 5000);

        var result = CommandDispatcher.Dispatch(new ClimbMaintainCommand(19000), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Climb and maintain 19000", result.Message);
    }

    [Fact]
    public void ClimbMaintain_WithPriorAltitude_ShowsPrevious()
    {
        var ac = CreateAircraft(altitude: 5000);
        ac.Targets.AssignedAltitude = 5000;

        var result = CommandDispatcher.Dispatch(new ClimbMaintainCommand(19000), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Climb and maintain 19000 (was 5000)", result.Message);
    }

    [Fact]
    public void ClimbMaintain_SameAltitude_NoSuffix()
    {
        var ac = CreateAircraft(altitude: 5000);
        ac.Targets.AssignedAltitude = 19000;

        var result = CommandDispatcher.Dispatch(new ClimbMaintainCommand(19000), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Climb and maintain 19000", result.Message);
    }

    [Fact]
    public void DescendMaintain_WithPriorAltitude_ShowsPrevious()
    {
        var ac = CreateAircraft(altitude: 19000);
        ac.Targets.AssignedAltitude = 19000;

        var result = CommandDispatcher.Dispatch(new DescendMaintainCommand(5000), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Descend and maintain 5000 (was 19000)", result.Message);
    }

    // --- Heading feedback: previous heading ---

    [Fact]
    public void FlyHeading_NoPriorGuidance_NoSuffix()
    {
        var ac = CreateAircraft();

        var result = CommandDispatcher.Dispatch(new FlyHeadingCommand(new MagneticHeading(200)), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Fly heading 200", result.Message);
    }

    [Fact]
    public void FlyHeading_WithPriorHeading_ShowsPrevious()
    {
        var ac = CreateAircraft();
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(180);

        var result = CommandDispatcher.Dispatch(new FlyHeadingCommand(new MagneticHeading(200)), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Fly heading 200 (was heading 180)", result.Message);
    }

    [Fact]
    public void TurnLeft_WithPriorHeading_ShowsPrevious()
    {
        var ac = CreateAircraft();
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(180);

        var result = CommandDispatcher.Dispatch(new TurnLeftCommand(new MagneticHeading(090)), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Turn left heading 090 (was heading 180)", result.Message);
    }

    [Fact]
    public void TurnRight_WithPriorHeading_ShowsPrevious()
    {
        var ac = CreateAircraft();
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(180);

        var result = CommandDispatcher.Dispatch(new TurnRightCommand(new MagneticHeading(270)), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Turn right heading 270 (was heading 180)", result.Message);
    }

    // --- Heading feedback: previous DCT ---

    [Fact]
    public void FlyHeading_WithPriorDct_ShowsPrevious()
    {
        var ac = CreateAircraft();
        ac.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "SUNOL",
                Latitude = 37.5,
                Longitude = -121.9,
            }
        );

        var result = CommandDispatcher.Dispatch(new FlyHeadingCommand(new MagneticHeading(200)), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Fly heading 200 (was DCT SUNOL)", result.Message);
    }

    // --- Heading feedback: previous SID/STAR ---

    [Fact]
    public void FlyHeading_WithPriorSid_ShowsPrevious()
    {
        var ac = CreateAircraft();
        ac.ActiveSidId = "OFFSH2";

        var result = CommandDispatcher.Dispatch(new FlyHeadingCommand(new MagneticHeading(200)), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Fly heading 200 (was SID OFFSH2)", result.Message);
    }

    [Fact]
    public void FlyHeading_WithPriorStar_ShowsPrevious()
    {
        var ac = CreateAircraft();
        ac.ActiveStarId = "BDEGA2";

        var result = CommandDispatcher.Dispatch(new FlyHeadingCommand(new MagneticHeading(200)), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Fly heading 200 (was STAR BDEGA2)", result.Message);
    }

    // --- Heading feedback priority: assigned heading > DCT > SID/STAR ---

    [Fact]
    public void FlyHeading_HeadingTakesPriorityOverDct()
    {
        var ac = CreateAircraft();
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(180);
        ac.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "SUNOL",
                Latitude = 37.5,
                Longitude = -121.9,
            }
        );

        var result = CommandDispatcher.Dispatch(new FlyHeadingCommand(new MagneticHeading(200)), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Fly heading 200 (was heading 180)", result.Message);
    }

    // --- FPH: show actual heading ---

    [Fact]
    public void FlyPresentHeading_ShowsActualHeading()
    {
        var ac = CreateAircraft(heading: 180);

        var result = CommandDispatcher.Dispatch(new FlyPresentHeadingCommand(), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Fly present heading 180", result.Message);
    }

    [Fact]
    public void FlyPresentHeading_WithPriorDct_ShowsBoth()
    {
        var ac = CreateAircraft(heading: 180);
        ac.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "SUNOL",
                Latitude = 37.5,
                Longitude = -121.9,
            }
        );

        var result = CommandDispatcher.Dispatch(new FlyPresentHeadingCommand(), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Fly present heading 180 (was DCT SUNOL)", result.Message);
    }

    // --- Relative turns ---

    [Fact]
    public void LeftTurn_WithPriorHeading_ShowsPrevious()
    {
        var ac = CreateAircraft(heading: 180);
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(180);

        var result = CommandDispatcher.Dispatch(new LeftTurnCommand(30), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Turn 30 degrees left, heading 150 (was heading 180)", result.Message);
    }

    [Fact]
    public void RightTurn_WithPriorDct_ShowsPrevious()
    {
        var ac = CreateAircraft(heading: 180);
        ac.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "SUNOL",
                Latitude = 37.5,
                Longitude = -121.9,
            }
        );

        var result = CommandDispatcher.Dispatch(new RightTurnCommand(30), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("Turn 30 degrees right, heading 210 (was DCT SUNOL)", result.Message);
    }
}
