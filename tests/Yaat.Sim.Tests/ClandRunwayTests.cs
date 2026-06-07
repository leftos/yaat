using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

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
    public void Parse_ClandSingleDigitRunway_NormalizesToPadded()
    {
        // FAA names single-digit runways without a leading zero ("8R"), but the rest of
        // the sim keys runway identity on the zero-padded canonical ("08R"). Normalize the
        // token at the parse boundary so an unpadded designator never enters sim state.
        var cmd = CommandParser.Parse("CLAND 8R");
        var cland = Assert.IsType<ClearedToLandCommand>(cmd.Value);
        Assert.Equal("08R", cland.RunwayId);
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

    // ---- handler: single-digit runway (8R vs canonical 08R) ----

    [Fact]
    public void Handler_EstablishedFor08R_Cland8R_Accepted()
    {
        // Aircraft is established for the canonical "08R"; the controller (or an agent
        // constructing the command directly, bypassing the parse-boundary normalize)
        // clears it with the FAA form "8R". These name the same runway end and must match.
        var ac = MakeEstablished("8R");

        var result = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand { RunwayId = "8R" }, ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases!.LandingClearance);
    }

    [Fact]
    public void Handler_EstablishedFor08R_Cland26L_Rejected()
    {
        // Direction-sensitivity guard: "26L" is the OPPOSITE end of the 08R/26L runway.
        // Clearing an aircraft established for 08R to land 26L must still be rejected — the
        // normalization fix must not collapse opposite ends (i.e. must not use Id.Contains).
        var ac = MakeEstablished("8R");

        var result = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand { RunwayId = "26L" }, ac);

        Assert.False(result.Success);
        Assert.Contains("established for runway", result.Message!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Handler_FollowingNoRunway_ClandSingleDigit_ArmsNormalizedRunway()
    {
        // A following aircraft arms the clearance for later application by VfrFollowPhase.
        // The armed runway must be stored canonical ("08R") so the runway match at join
        // (against the joined runway's normalized Designator) succeeds.
        var ac = MakeFollower();

        var result = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand { RunwayId = "8R" }, ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal("08R", ac.Phases!.ClearedRunwayId);
    }

    [Fact]
    public void VfrFollow_ArmedSingleDigit_AppliedOnMatchingRunway()
    {
        // The armed clearance carries "8R"; the follower joins the canonical "08R" pattern.
        // ApplyArmedLandingClearance must recognize these as the same end and apply the
        // clearance instead of deferring and awaiting an explicit re-clearance.
        var phase = new VfrFollowPhase("N314GT");
        var ac = MakeFollower();
        var runway = TestRunwayFactory.Make(designator: "8R", airportId: "KMIA", heading: 87);

        phase.ApplyArmedLandingClearance(ac, ClearanceType.ClearedToLand, "8R", runway);

        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases!.LandingClearance);
        Assert.Equal("08R", ac.Phases.ClearedRunwayId);
    }

    [Fact]
    public void VfrFollow_ArmedOppositeEnd_NotApplied()
    {
        // Direction-sensitivity guard: armed for the opposite end (26L) but joining 08R —
        // the clearance must NOT be applied.
        var phase = new VfrFollowPhase("N314GT");
        var ac = MakeFollower();
        var runway = TestRunwayFactory.Make(designator: "8R", airportId: "KMIA", heading: 87);

        phase.ApplyArmedLandingClearance(ac, ClearanceType.ClearedToLand, "26L", runway);

        Assert.Null(ac.Phases!.LandingClearance);
    }

    private static AircraftState MakeEstablished(string designator)
    {
        var ac = new AircraftState
        {
            Callsign = "N346G",
            AircraftType = "C172",
            Position = new LatLon(25.80, -80.35),
            TrueHeading = new TrueHeading(87),
            Altitude = 1500,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KMIA", FlightRules = "VFR" },
        };
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(designator: designator, airportId: "KMIA", heading: 87) };
        ac.Phases.Add(new LandingPhase());
        return ac;
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
