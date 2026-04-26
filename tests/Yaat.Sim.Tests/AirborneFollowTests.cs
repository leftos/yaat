using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
public class AirborneFollowTests : IDisposable
{
    private readonly IDisposable _navDbScope;

    public AirborneFollowTests()
    {
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(DefaultRunway()));
    }

    public void Dispose() => _navDbScope.Dispose();

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
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            Approach = new AircraftApproachState { FollowingCallsign = followingCallsign },
            FlightPlan = new AircraftFlightPlan { Destination = "KTEST" },
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

        var result = AirborneFollowHelper.GetAdjustedSpeed(ctx, 90.0, 65.0, AirborneFollowHelper.MaxSpeedAdjustKts);

        Assert.Null(result);
    }

    [Fact]
    public void GetAdjustedSpeed_ClearsFollow_WhenLeaderNotFound()
    {
        var ac = MakeAircraft(followingCallsign: "LEADER");
        var ctx = Ctx(ac, lookup: _ => null);

        var result = AirborneFollowHelper.GetAdjustedSpeed(ctx, 90.0, 65.0, AirborneFollowHelper.MaxSpeedAdjustKts);

        Assert.Null(result);
        Assert.Null(ac.Approach.FollowingCallsign);
    }

    [Fact]
    public void GetAdjustedSpeed_CeilingDoesNotCompoundAcrossTicks()
    {
        // The helper's output must ONLY depend on the phase baseline fed in — feeding
        // the prior tick's adjusted value back in is what allowed IAS to escape the
        // stabilized-approach gate. Repeated calls with the same baseline must yield
        // the same upper bound (baseline + MaxSpeedAdjustKts).
        var follower = MakeAircraft(callsign: "FOLL", lat: 37.0, lon: -122.0, followingCallsign: "LEAD");
        var leader = MakeAircraft(callsign: "LEAD", type: "C172", lat: 37.0, lon: -122.05);

        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? leader : null);
        double baseline = 90.0;

        double? first = AirborneFollowHelper.GetAdjustedSpeed(ctx, baseline, 65.0, AirborneFollowHelper.MaxSpeedAdjustKts);
        double? second = AirborneFollowHelper.GetAdjustedSpeed(ctx, baseline, 65.0, AirborneFollowHelper.MaxSpeedAdjustKts);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Value, second.Value, precision: 6);
        Assert.True(first.Value <= baseline + AirborneFollowHelper.MaxSpeedAdjustKts + 1e-6, $"Expected <= {baseline + 20}, got {first}");
    }

    [Fact]
    public void GetAdjustedSpeed_FinalCeilingTighter_KeepsUnderStabilizedGate()
    {
        // On final approach the unstabilized go-around gate fires at IAS > 1.3·Vref.
        // For a C172 with Vref=75, that's 97.5 kt — and FAS+20 = 95 would leave only
        // 2.5 kt of margin. MaxSpeedAdjustFinalKts caps catch-up closer to Vref so
        // chasing the leader can't trip the gate.
        var follower = MakeAircraft(callsign: "FOLL", lat: 37.0, lon: -122.0, followingCallsign: "LEAD");
        var leader = MakeAircraft(callsign: "LEAD", type: "C172", lat: 37.0, lon: -122.05);

        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? leader : null);
        double vref = 75.0;

        double? result = AirborneFollowHelper.GetAdjustedSpeed(ctx, vref, vref, AirborneFollowHelper.MaxSpeedAdjustFinalKts);

        Assert.NotNull(result);
        Assert.True(result <= vref + AirborneFollowHelper.MaxSpeedAdjustFinalKts + 1e-6, $"Expected <= {vref + 10}, got {result}");
        Assert.True(result < vref * 1.3, $"Expected below unstabilized gate {vref * 1.3}, got {result}");
    }

    [Fact]
    public void GetAdjustedSpeedFreeFlight_IncreasesSpeed_WhenTooFarFromLeader()
    {
        // Outside the pattern (VfrFollow / PatternEntry) the follower still needs to
        // chase the leader — preserve the +MaxSpeedAdjustKts ceiling for free-flight.
        var follower = MakeAircraft(callsign: "FOLL", lat: 37.0, lon: -122.0, followingCallsign: "LEAD");
        var leader = MakeAircraft(callsign: "LEAD", type: "C172", lat: 37.0, lon: -122.05);

        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? leader : null);
        double normalSpeed = 90.0;

        var result = AirborneFollowHelper.GetAdjustedSpeedFreeFlight(ctx, normalSpeed, 65.0);

        Assert.NotNull(result);
        Assert.True(result > normalSpeed, $"Expected free-flight speed above {normalSpeed}, got {result}");
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

        var result = AirborneFollowHelper.GetAdjustedSpeed(ctx, normalSpeed, 65.0, AirborneFollowHelper.MaxSpeedAdjustKts);

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

        var result = AirborneFollowHelper.GetAdjustedSpeed(ctx, 90.0, minSpeed, AirborneFollowHelper.MaxSpeedAdjustKts);

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
        var resultJet = AirborneFollowHelper.GetAdjustedSpeed(ctxJet, 90.0, 65.0, AirborneFollowHelper.MaxSpeedAdjustKts);

        // Reset follow state (cleared if leader disappears)
        follower.Approach.FollowingCallsign = "LEAD";

        var ctxPiston = Ctx(follower, lookup: cs => cs == "LEAD" ? pistonLeader : null);
        var resultPiston = AirborneFollowHelper.GetAdjustedSpeed(ctxPiston, 90.0, 65.0, AirborneFollowHelper.MaxSpeedAdjustKts);

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
    [InlineData(AircraftCategory.Jet, 3.0)] // FAA 7110.65 §5-5-4 IFR radar separation minimum
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
        ac.FlightPlan.FlightRules = "VFR";
        ac.Approach.HasReportedTrafficInSight = true;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new DownwindPhase());
        // Start the phase so CurrentPhase is set.
        var startCtx = CommandDispatcher.BuildMinimalContext(ac);
        ac.Phases.Start(startCtx);

        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Equal("LEAD", ac.Approach.FollowingCallsign);
    }

    [Fact]
    public void Follow_Airborne_NotVfr_Rejected()
    {
        // FOLLOW only applies to VFR aircraft — IFR traffic uses CVA FOLLOW for visual separation.
        var ac = MakeAircraft();
        ac.FlightPlan.FlightRules = "IFR";

        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
    }

    [Fact]
    public void FollowGround_RoutesToGroundHandler()
    {
        var ac = MakeAircraft();
        ac.IsOnGround = true;

        // Ground follow needs a ground layout — without one, it should fail gracefully
        var result = CommandDispatcher.Dispatch(new FollowGroundCommand("LEAD"), ac, TestDispatch.Context(Random.Shared));

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
        ac.Approach.HasReportedTrafficInSight = false;

        var cmd = new ClearedVisualApproachCommand("28", null, null, "LEAD");
        var result = ApproachCommandHandler.TryClearedVisualApproach(cmd, ac);

        Assert.False(result.Success);
        Assert.Contains("traffic not in sight", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CvaFollow_Succeeds_WhenRtisReported()
    {
        var ac = MakeAircraft(type: "B738", heading: 280, altitude: 3000, lat: 37.05, lon: -122.1);
        ac.Approach.HasReportedTrafficInSight = true;

        var cmd = new ClearedVisualApproachCommand("28", null, null, "LEAD");
        var result = ApproachCommandHandler.TryClearedVisualApproach(cmd, ac);

        Assert.True(result.Success);
        Assert.Equal("LEAD", ac.Approach.FollowingCallsign);
    }

    [Fact]
    public void Rtisf_ForcesTrafficInSight()
    {
        var ac = MakeAircraft();
        ac.Approach.HasReportedTrafficInSight = false;

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand("LEAD"), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.True(ac.Approach.HasReportedTrafficInSight);
    }

    [Fact]
    public void Rfisf_ForcesFieldInSight()
    {
        var ac = MakeAircraft();
        ac.Approach.HasReportedFieldInSight = false;

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightForcedCommand(), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.True(ac.Approach.HasReportedFieldInSight);
    }

    [Fact]
    public void CvaFollow_Succeeds_AfterRtisf()
    {
        var ac = MakeAircraft(type: "B738", heading: 280, altitude: 3000, lat: 37.05, lon: -122.1);
        ac.Approach.HasReportedTrafficInSight = false;

        // Force traffic in sight via RTISF
        CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand("LEAD"), ac, TestDispatch.Context(Random.Shared));

        // Now CVA FOLLOW should work
        var cmd = new ClearedVisualApproachCommand("28", null, null, "LEAD");
        var result = ApproachCommandHandler.TryClearedVisualApproach(cmd, ac);

        Assert.True(result.Success);
        Assert.Equal("LEAD", ac.Approach.FollowingCallsign);
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
