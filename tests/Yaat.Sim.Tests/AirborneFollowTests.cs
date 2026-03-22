using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class AirborneFollowTests
{
    public AirborneFollowTests()
    {
        TestVnasData.EnsureInitialized();
        NavigationDatabase.SetInstance(TestNavDbFactory.WithRunways(DefaultRunway()));
    }

    // Runway 28 at KTEST: heading 280°
    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 0);

    private static AircraftState MakeAircraft(
        string callsign = "N123",
        string type = "C172",
        double lat = 37.0,
        double lon = -122.0,
        double heading = 280,
        double altitude = 1000,
        double ias = 90,
        string? followingCallsign = null
    )
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Latitude = lat,
            Longitude = lon,
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            FollowingCallsign = followingCallsign,
            Destination = "KTEST",
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static PhaseContext Ctx(AircraftState ac, Func<string, AircraftState?>? lookup = null, double dt = 1.0)
    {
        var rwy = DefaultRunway();
        return new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = dt,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            AircraftLookup = lookup,
            Logger = NullLogger.Instance,
        };
    }

    // -------------------------------------------------------------------------
    // GetAdjustedSpeed
    // -------------------------------------------------------------------------

    [Fact]
    public void GetAdjustedSpeed_ReturnsNull_WhenNoFollowingCallsign()
    {
        var ac = MakeAircraft(followingCallsign: null);
        var ctx = Ctx(ac);

        var result = AirborneFollowHelper.GetAdjustedSpeed(ctx, 90.0, 65.0);

        Assert.Null(result);
    }

    [Fact]
    public void GetAdjustedSpeed_ClearsFollow_WhenLeaderNotFound()
    {
        var ac = MakeAircraft(followingCallsign: "LEADER");
        var ctx = Ctx(ac, lookup: _ => null);

        var result = AirborneFollowHelper.GetAdjustedSpeed(ctx, 90.0, 65.0);

        Assert.Null(result);
        Assert.Null(ac.FollowingCallsign);
    }

    [Fact]
    public void GetAdjustedSpeed_IncreasesSpeed_WhenTooFarFromLeader()
    {
        // Place follower and leader far apart (3nm for a piston leader with 1.0nm desired)
        var follower = MakeAircraft(callsign: "FOLL", lat: 37.0, lon: -122.0, followingCallsign: "LEAD");
        var leader = MakeAircraft(callsign: "LEAD", type: "C172", lat: 37.0, lon: -122.05);

        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? leader : null);
        double normalSpeed = 90.0;

        var result = AirborneFollowHelper.GetAdjustedSpeed(ctx, normalSpeed, 65.0);

        Assert.NotNull(result);
        Assert.True(result > normalSpeed, $"Expected speed above {normalSpeed}, got {result}");
    }

    [Fact]
    public void GetAdjustedSpeed_DecreasesSpeed_WhenTooCloseToLeader()
    {
        // Place follower and leader very close (0.2nm)
        var follower = MakeAircraft(callsign: "FOLL", lat: 37.0, lon: -122.0, followingCallsign: "LEAD");
        var leader = MakeAircraft(
            callsign: "LEAD",
            type: "C172",
            lat: 37.0,
            lon: -122.0 + (0.2 / 60.0) // ~0.2nm east
        );

        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? leader : null);
        double normalSpeed = 90.0;

        var result = AirborneFollowHelper.GetAdjustedSpeed(ctx, normalSpeed, 65.0);

        Assert.NotNull(result);
        Assert.True(result < normalSpeed, $"Expected speed below {normalSpeed}, got {result}");
    }

    [Fact]
    public void GetAdjustedSpeed_NeverBelowMinSpeed()
    {
        // Place follower extremely close to leader so max deceleration kicks in
        var follower = MakeAircraft(callsign: "FOLL", lat: 37.0, lon: -122.0, followingCallsign: "LEAD");
        var leader = MakeAircraft(callsign: "LEAD", type: "C172", lat: 37.0, lon: -122.0 + (0.01 / 60.0));

        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? leader : null);
        double minSpeed = 65.0;

        var result = AirborneFollowHelper.GetAdjustedSpeed(ctx, 90.0, minSpeed);

        Assert.NotNull(result);
        Assert.True(result >= minSpeed, $"Expected speed >= {minSpeed}, got {result}");
    }

    [Fact]
    public void GetAdjustedSpeed_LargerDesiredDistance_ForJetLeader()
    {
        // Piston follower behind a jet leader — should want more distance
        var follower = MakeAircraft(callsign: "FOLL", lat: 37.0, lon: -122.0, followingCallsign: "LEAD");
        // Leader is 1.5nm away — close for a jet (desired 2.0) but ok for a piston (desired 1.0)
        var jetLeader = MakeAircraft(callsign: "LEAD", type: "B738", lat: 37.0, lon: -122.0 + (1.5 / 54.0));
        var pistonLeader = MakeAircraft(callsign: "LEAD", type: "C172", lat: 37.0, lon: -122.0 + (1.5 / 54.0));

        var ctxJet = Ctx(follower, lookup: cs => cs == "LEAD" ? jetLeader : null);
        var resultJet = AirborneFollowHelper.GetAdjustedSpeed(ctxJet, 90.0, 65.0);

        // Reset follow state (cleared if leader disappears)
        follower.FollowingCallsign = "LEAD";

        var ctxPiston = Ctx(follower, lookup: cs => cs == "LEAD" ? pistonLeader : null);
        var resultPiston = AirborneFollowHelper.GetAdjustedSpeed(ctxPiston, 90.0, 65.0);

        Assert.NotNull(resultJet);
        Assert.NotNull(resultPiston);
        // Behind a jet at 1.5nm (want 2.0nm) → too close, slows down
        // Behind a piston at 1.5nm (want 1.0nm) → too far, speeds up
        // So piston result is faster (more correction needed to open distance)
        Assert.True(resultJet < resultPiston, $"Expected jet speed ({resultJet}) < piston speed ({resultPiston})");
    }

    // -------------------------------------------------------------------------
    // ShouldExtendDownwind
    // -------------------------------------------------------------------------

    [Fact]
    public void ShouldExtendDownwind_False_WhenNoFollow()
    {
        var ac = MakeAircraft(followingCallsign: null);
        var ctx = Ctx(ac);

        Assert.False(AirborneFollowHelper.ShouldExtendDownwind(ctx));
    }

    [Fact]
    public void ShouldExtendDownwind_False_WhenLeaderNotFound()
    {
        var ac = MakeAircraft(followingCallsign: "LEAD");
        var ctx = Ctx(ac, lookup: _ => null);

        Assert.False(AirborneFollowHelper.ShouldExtendDownwind(ctx));
    }

    [Fact]
    public void ShouldExtendDownwind_True_WhenTooClose()
    {
        // Place follower and leader very close (0.3nm < 1.0 * 0.6 = 0.6nm for piston)
        var follower = MakeAircraft(callsign: "FOLL", lat: 37.0, lon: -122.0, followingCallsign: "LEAD");
        var leader = MakeAircraft(callsign: "LEAD", type: "C172", lat: 37.0, lon: -122.0 + (0.3 / 60.0));

        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? leader : null);

        Assert.True(AirborneFollowHelper.ShouldExtendDownwind(ctx));
    }

    [Fact]
    public void ShouldExtendDownwind_False_WhenAdequateSpacing()
    {
        // Place follower and leader at 2nm apart (>> 1.0 * 0.6 for piston)
        var follower = MakeAircraft(callsign: "FOLL", lat: 37.0, lon: -122.0, followingCallsign: "LEAD");
        var leader = MakeAircraft(callsign: "LEAD", type: "C172", lat: 37.0, lon: -122.0 + (2.0 / 60.0));

        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? leader : null);

        Assert.False(AirborneFollowHelper.ShouldExtendDownwind(ctx));
    }

    // -------------------------------------------------------------------------
    // DesiredDistanceForLeader
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(AircraftCategory.Jet, 2.0)]
    [InlineData(AircraftCategory.Turboprop, 1.5)]
    [InlineData(AircraftCategory.Piston, 1.0)]
    [InlineData(AircraftCategory.Helicopter, 1.0)]
    public void DesiredDistance_VariesByLeaderCategory(AircraftCategory cat, double expected)
    {
        Assert.Equal(expected, AirborneFollowHelper.DesiredDistanceForLeader(cat));
    }

    // -------------------------------------------------------------------------
    // Airborne FOLLOW command dispatch
    // -------------------------------------------------------------------------

    [Fact]
    public void Follow_Airborne_SetsFollowingCallsign()
    {
        var ac = MakeAircraft();
        ac.Phases = new PhaseList();
        ac.Phases.Add(new DownwindPhase());

        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), ac, null, Random.Shared, true);

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
    }

    [Fact]
    public void Follow_Airborne_FailsWithNoPhases()
    {
        var ac = MakeAircraft();
        ac.Phases = null;

        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), ac, null, Random.Shared, true);

        Assert.False(result.Success);
    }

    [Fact]
    public void FollowGround_RoutesToGroundHandler()
    {
        var ac = MakeAircraft();
        ac.IsOnGround = true;

        // Ground follow needs a ground layout — without one, it should fail gracefully
        var result = CommandDispatcher.Dispatch(new FollowGroundCommand("LEAD"), ac, null, Random.Shared, true);

        // Ground handler rejects without ground layout
        Assert.False(result.Success);
    }

    // -------------------------------------------------------------------------
    // CVA FOLLOW + RTIS gate
    // -------------------------------------------------------------------------

    [Fact]
    public void CvaFollow_Fails_WhenRtisNotReported()
    {
        var ac = MakeAircraft(type: "B738", heading: 280, altitude: 3000, lat: 37.05, lon: -122.1);
        ac.HasReportedTrafficInSight = false;

        var cmd = new ClearedVisualApproachCommand("28", null, null, "LEAD");
        var result = ApproachCommandHandler.TryClearedVisualApproach(cmd, ac);

        Assert.False(result.Success);
        Assert.Contains("traffic not in sight", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CvaFollow_Succeeds_WhenRtisReported()
    {
        var ac = MakeAircraft(type: "B738", heading: 280, altitude: 3000, lat: 37.05, lon: -122.1);
        ac.HasReportedTrafficInSight = true;

        var cmd = new ClearedVisualApproachCommand("28", null, null, "LEAD");
        var result = ApproachCommandHandler.TryClearedVisualApproach(cmd, ac);

        Assert.True(result.Success);
        Assert.Equal("LEAD", ac.FollowingCallsign);
    }

    [Fact]
    public void Rtisf_ForcesTrafficInSight()
    {
        var ac = MakeAircraft();
        ac.HasReportedTrafficInSight = false;

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand("LEAD"), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.True(ac.HasReportedTrafficInSight);
    }

    [Fact]
    public void Rfisf_ForcesFieldInSight()
    {
        var ac = MakeAircraft();
        ac.HasReportedFieldInSight = false;

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightForcedCommand(), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.True(ac.HasReportedFieldInSight);
    }

    [Fact]
    public void CvaFollow_Succeeds_AfterRtisf()
    {
        var ac = MakeAircraft(type: "B738", heading: 280, altitude: 3000, lat: 37.05, lon: -122.1);
        ac.HasReportedTrafficInSight = false;

        // Force traffic in sight via RTISF
        CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand("LEAD"), ac, null, Random.Shared, true);

        // Now CVA FOLLOW should work
        var cmd = new ClearedVisualApproachCommand("28", null, null, "LEAD");
        var result = ApproachCommandHandler.TryClearedVisualApproach(cmd, ac);

        Assert.True(result.Success);
        Assert.Equal("LEAD", ac.FollowingCallsign);
    }

    // -------------------------------------------------------------------------
    // Follow acceptance in pattern phases
    // -------------------------------------------------------------------------

    [Fact]
    public void Follow_AcceptedInDownwindPhase()
    {
        var phase = new DownwindPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.Follow));
    }

    [Fact]
    public void Follow_AcceptedInBasePhase()
    {
        var phase = new BasePhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.Follow));
    }

    [Fact]
    public void Follow_AcceptedInFinalApproachPhase()
    {
        var phase = new FinalApproachPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.Follow));
    }
}
