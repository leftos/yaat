using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests;

/// <summary>
/// <c>CLAND</c> with an optional runway. A controller can clear an aircraft that has
/// no assigned runway yet — e.g. one still following traffic — to land on a named
/// runway (<c>CLAND 28R</c>), or with a bare <c>CLAND</c> to inherit the lead's
/// runway. The clearance is armed on the follower and applied by
/// <see cref="VfrFollowPhase"/> once it joins the pattern (see
/// <c>VfrFollowSequencesToFinalTests</c> for the end-to-end land-on-arm test).
/// </summary>
public class ClandRunwayTests
{
    // ---- parsing ----

    [Fact]
    public void Parse_ClandWithRunway_SetsRunwayId()
    {
        var cmd = CommandParser.Parse("CLAND 28R");
        var cland = Assert.IsType<ClearedToLandCommand>(cmd.Value);
        Assert.Equal("28R", cland.RunwayId);
        Assert.False(cland.NoDelete);
    }

    [Fact]
    public void Parse_BareCland_HasNullRunwayId()
    {
        var cmd = CommandParser.Parse("CLAND");
        var cland = Assert.IsType<ClearedToLandCommand>(cmd.Value);
        Assert.Null(cland.RunwayId);
    }

    [Fact]
    public void Parse_ClandRunwayWithNodel_BothSet()
    {
        var cmd = CommandParser.Parse("CLAND 28R NODEL");
        var cland = Assert.IsType<ClearedToLandCommand>(cmd.Value);
        Assert.Equal("28R", cland.RunwayId);
        Assert.True(cland.NoDelete);
    }

    [Fact]
    public void Parse_ClandGarbageArg_Rejected()
    {
        var cmd = CommandParser.Parse("CLAND FOO");
        Assert.False(cmd.IsSuccess);
    }

    // ---- handler: arm while following ----

    [Fact]
    public void Handler_FollowingNoRunway_ClandRunway_ArmsClearance()
    {
        var ac = MakeFollower();

        var result = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand { RunwayId = "28R" }, ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases!.LandingClearance);
        Assert.Equal("28R", ac.Phases.ClearedRunwayId);
    }

    [Fact]
    public void Handler_FollowingNoRunway_BareCland_ArmsInheritingLeadRunway()
    {
        var ac = MakeFollower();

        var result = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand(), ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases!.LandingClearance);
        // Null = inherit the lead's runway when the follower joins the pattern.
        Assert.Null(ac.Phases.ClearedRunwayId);
    }

    [Fact]
    public void Handler_NotFollowing_NoApproach_ClandRunway_Rejected()
    {
        // Scope: CLAND <rwy> does not build an approach for an enroute aircraft that
        // is not following traffic — it only fills the missing assignment for a
        // follower (or an aircraft already on an approach/pattern).
        var ac = MakeFollower();
        ac.Approach.FollowingCallsign = null;

        var result = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand { RunwayId = "28R" }, ac);

        Assert.False(result.Success);
        Assert.Contains("no approach", result.Message!, System.StringComparison.OrdinalIgnoreCase);
        Assert.Null(ac.Phases!.LandingClearance);
    }

    private static AircraftState MakeFollower()
    {
        var ac = new AircraftState
        {
            Callsign = "N346G",
            AircraftType = "C172",
            Position = new LatLon(37.80, -122.20),
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Altitude = 1500,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
            Approach = new AircraftApproachState { FollowingCallsign = "N314GT" },
        };
        // Pursuing its lead: no runway/approach assigned yet (AssignedRunway null).
        ac.Phases = new PhaseList();
        ac.Phases.Add(new VfrFollowPhase("N314GT"));
        return ac;
    }
}
