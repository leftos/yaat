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

    // -------------------------------------------------------------------------
    // CheckLeadLifecycle: pattern-flow-ahead guard
    // -------------------------------------------------------------------------

    /// <summary>
    /// Build an aircraft already "established" on a pattern phase of a given type.
    /// Skips the phase's OnStart (Waypoints/Runway needed) and just stamps the
    /// PhaseList's CurrentIndex via the public Start path on a phase that no-ops
    /// when its required setup is missing. <see cref="AirborneFollowHelper.CheckLeadLifecycle"/>
    /// only inspects <c>CurrentPhase</c>'s type and <c>AssignedRunway.Designator</c>.
    /// </summary>
    private static AircraftState MakeAircraftOnPatternPhase<TPhase>(
        string callsign,
        string type,
        double lat,
        double lon,
        double heading,
        string runwayDesignator = "28",
        string? followingCallsign = null
    )
        where TPhase : Phase, new()
    {
        var ac = MakeAircraft(callsign: callsign, type: type, lat: lat, lon: lon, heading: heading, followingCallsign: followingCallsign);
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(designator: runwayDesignator, heading: 280, elevationFt: 0) };
        ac.Phases.Add(new TPhase());
        // Phase.OnStart returns early when Waypoints / Runway are unset; calling Start
        // here only flips Status to Active and pins CurrentIndex=0.
        var startCtx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(type),
            DeltaSeconds = 1.0,
            Runway = null,
            FieldElevation = 0,
            Logger = NullLogger.Instance,
        };
        ac.Phases.Start(startCtx);
        return ac;
    }

    /// <summary>
    /// Reproduces the geometry from the N342T bug bundle: follower on Downwind
    /// (eastbound) and lead on FinalApproach (westbound) of the same runway.
    /// The point-to-point gap grows for the entire duration of the follower's
    /// downwind leg, but this is expected pattern flow — the lead is on a later
    /// pattern leg and the gap will close once the follower turns base. The
    /// runaway watchdog must NOT cancel the follow.
    /// </summary>
    [Fact]
    public void CheckLeadLifecycle_DoesNotCancel_WhenLeadOnFinalAndFollowerOnDownwind()
    {
        const string LeadCallsign = "LEAD";

        // Follower piston on Downwind, eastbound, south of centerline. Lead piston
        // on FinalApproach, westbound, on centerline at the same longitude as the
        // follower — closest-approach geometry, so any motion from here can only
        // grow the gap (no initial closing phase that would let the runaway timer
        // reset bestSoFar to a smaller value before the test windows ends).
        const double StartLon = -121.99;
        var follower = MakeAircraftOnPatternPhase<DownwindPhase>(
            callsign: "FOLL",
            type: "C172",
            lat: 36.99,
            lon: StartLon,
            heading: 100,
            followingCallsign: LeadCallsign
        );
        var lead = MakeAircraftOnPatternPhase<FinalApproachPhase>(callsign: LeadCallsign, type: "C172", lat: 37.00, lon: StartLon, heading: 280);

        // Lead heads west (lon decreases), follower heads east (lon increases) —
        // longitudinal gap grows monotonically every tick, well past the 0.1 nm
        // runaway tolerance within the 35 s grace window.
        const int Ticks = 35;
        const double LonStepDeg = 0.001;
        for (int i = 0; i < Ticks; i++)
        {
            follower.Position = new LatLon(follower.Position.Lat, follower.Position.Lon + LonStepDeg);
            lead.Position = new LatLon(lead.Position.Lat, lead.Position.Lon - LonStepDeg);
            var ctx = Ctx(follower, lookup: cs => cs == LeadCallsign ? lead : null);
            AirborneFollowHelper.CheckLeadLifecycle(ctx);
        }

        Assert.Equal(LeadCallsign, follower.Approach.FollowingCallsign);
        Assert.Equal(0, follower.Approach.FollowRunawaySeconds);
    }

    // -------------------------------------------------------------------------
    // Fix 3: Upwind / Crosswind follow-aware spacing wiring
    // -------------------------------------------------------------------------

    private static PatternWaypoints DefaultPatternWaypoints() =>
        PatternGeometry.Compute(DefaultRunway(), AircraftCategory.Piston, PatternDirection.Left, null, null, null);

    /// <summary>
    /// Upwind followers must call <see cref="AirborneFollowHelper.CheckLeadLifecycle"/>
    /// in their OnTick so a vanished lead clears <c>FollowingCallsign</c> instead
    /// of leaking for the duration of the climb-out.
    /// </summary>
    [Fact]
    public void UpwindPhase_OnTick_ClearsFollow_WhenLeadDespawns()
    {
        var wp = DefaultPatternWaypoints();
        var ac = MakeAircraft(
            lat: wp.DepartureEndLat,
            lon: wp.DepartureEndLon,
            heading: wp.UpwindHeading.Degrees,
            altitude: 500,
            followingCallsign: "GHOST"
        );
        var phase = new UpwindPhase { Waypoints = wp };
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = DefaultRunway(),
            FieldElevation = DefaultRunway().ElevationFt,
            AircraftLookup = _ => null,
            Logger = NullLogger.Instance,
        };
        phase.OnStart(ctx);

        phase.OnTick(ctx);

        Assert.Null(ac.Approach.FollowingCallsign);
    }

    /// <summary>
    /// Upwind followers must apply <see cref="AirborneFollowHelper.GetAdjustedSpeed"/>
    /// so the climbing aircraft slows when it's bearing down on a lead too closely
    /// from behind. Without the wiring the climbout charges at full DownwindSpeed
    /// into the back of the lead.
    /// </summary>
    [Fact]
    public void UpwindPhase_OnTick_AppliesFollowSpeedAdjustment()
    {
        var wp = DefaultPatternWaypoints();
        var ac = MakeAircraft(
            lat: wp.DepartureEndLat,
            lon: wp.DepartureEndLon,
            heading: wp.UpwindHeading.Degrees,
            altitude: 500,
            followingCallsign: "LEAD"
        );
        // Lead 0.7 nm ahead — close enough that piston desired (1.0 nm) calls
        // for a slowdown, but past the 0.5 nm "can't maintain separation"
        // threshold so the helper adjusts speed instead of cancelling follow.
        var lead = MakeAircraft(callsign: "LEAD", type: "C172", lat: ac.Position.Lat, lon: ac.Position.Lon + (0.7 / 48.0));

        var phase = new UpwindPhase { Waypoints = wp };
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = DefaultRunway(),
            FieldElevation = DefaultRunway().ElevationFt,
            AircraftLookup = cs => cs == "LEAD" ? lead : null,
            Logger = NullLogger.Instance,
        };
        phase.OnStart(ctx);

        double baseline = AircraftPerformance.DownwindSpeed(ac.AircraftType, AircraftCategorization.Categorize(ac.AircraftType));
        ac.Targets.TargetSpeed = baseline; // OnStart already set this; pin it explicitly for clarity

        phase.OnTick(ctx);

        Assert.NotNull(ac.Targets.TargetSpeed);
        Assert.True(
            ac.Targets.TargetSpeed!.Value < baseline,
            $"Expected target speed below baseline {baseline} when too close to lead, got {ac.Targets.TargetSpeed}"
        );
    }

    /// <summary>
    /// Crosswind followers get the same lifecycle watchdog wiring as Upwind.
    /// </summary>
    [Fact]
    public void CrosswindPhase_OnTick_ClearsFollow_WhenLeadDespawns()
    {
        var wp = DefaultPatternWaypoints();
        var ac = MakeAircraft(
            lat: wp.CrosswindTurnLat,
            lon: wp.CrosswindTurnLon,
            heading: wp.CrosswindHeading.Degrees,
            altitude: wp.PatternAltitude,
            followingCallsign: "GHOST"
        );
        var phase = new CrosswindPhase { Waypoints = wp };
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = DefaultRunway(),
            FieldElevation = DefaultRunway().ElevationFt,
            AircraftLookup = _ => null,
            Logger = NullLogger.Instance,
        };
        phase.OnStart(ctx);

        phase.OnTick(ctx);

        Assert.Null(ac.Approach.FollowingCallsign);
    }

    /// <summary>
    /// Crosswind followers apply the same speed-adjustment as Upwind.
    /// </summary>
    [Fact]
    public void CrosswindPhase_OnTick_AppliesFollowSpeedAdjustment()
    {
        var wp = DefaultPatternWaypoints();
        var ac = MakeAircraft(
            lat: wp.CrosswindTurnLat,
            lon: wp.CrosswindTurnLon,
            heading: wp.CrosswindHeading.Degrees,
            altitude: wp.PatternAltitude,
            followingCallsign: "LEAD"
        );
        // Lead 0.7 nm north — close enough for piston desired (1.0 nm) to call
        // for a slowdown, past the 0.5 nm "can't maintain separation" threshold.
        var lead = MakeAircraft(callsign: "LEAD", type: "C172", lat: ac.Position.Lat + (0.7 / 60.0), lon: ac.Position.Lon);

        double baseline = AircraftPerformance.DownwindSpeed(ac.AircraftType, AircraftCategorization.Categorize(ac.AircraftType));
        ac.Targets.TargetSpeed = baseline;

        var phase = new CrosswindPhase { Waypoints = wp };
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = DefaultRunway(),
            FieldElevation = DefaultRunway().ElevationFt,
            AircraftLookup = cs => cs == "LEAD" ? lead : null,
            Logger = NullLogger.Instance,
        };
        phase.OnStart(ctx);

        phase.OnTick(ctx);

        Assert.NotNull(ac.Targets.TargetSpeed);
        Assert.True(
            ac.Targets.TargetSpeed!.Value < baseline,
            $"Expected target speed below baseline {baseline} when too close to lead, got {ac.Targets.TargetSpeed}"
        );
    }

    // -------------------------------------------------------------------------
    // Fix 1: FOLLOW dispatch clears IsExtended on all extended pattern legs
    // -------------------------------------------------------------------------

    private static AircraftState MakeAirborneVfrAircraft(string callsign = "FOLL", string type = "C172")
    {
        var ac = MakeAircraft(callsign: callsign, type: type);
        ac.FlightPlan.FlightRules = "VFR";
        ac.Approach.HasReportedTrafficInSight = true;
        ac.Phases = new PhaseList();
        return ac;
    }

    [Fact]
    public void Follow_ClearsExtendedUpwind()
    {
        var ac = MakeAirborneVfrAircraft();
        ac.Phases!.Add(new UpwindPhase { IsExtended = true });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal("LEAD", ac.Approach.FollowingCallsign);
        Assert.False(((UpwindPhase)ac.Phases.CurrentPhase!).IsExtended);
    }

    [Fact]
    public void Follow_ClearsExtendedCrosswind()
    {
        var ac = MakeAirborneVfrAircraft();
        ac.Phases!.Add(new CrosswindPhase { IsExtended = true });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal("LEAD", ac.Approach.FollowingCallsign);
        Assert.False(((CrosswindPhase)ac.Phases.CurrentPhase!).IsExtended);
    }

    [Fact]
    public void Follow_ClearsExtendedDownwind()
    {
        var ac = MakeAirborneVfrAircraft();
        ac.Phases!.Add(new DownwindPhase { IsExtended = true });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal("LEAD", ac.Approach.FollowingCallsign);
        Assert.False(((DownwindPhase)ac.Phases.CurrentPhase!).IsExtended);
    }

    /// <summary>
    /// Control case: when both aircraft are airborne and the lead is on the
    /// SAME leg (Downwind) but the gap genuinely grows (lead pulling away),
    /// the runaway watchdog must still fire — the pattern-flow-ahead guard
    /// applies only when the lead is on a LATER leg.
    /// </summary>
    [Fact]
    public void CheckLeadLifecycle_StillCancels_WhenSameLegAndGapGrows()
    {
        const string LeadCallsign = "LEAD";

        var follower = MakeAircraftOnPatternPhase<DownwindPhase>(
            callsign: "FOLL",
            type: "C172",
            lat: 37.00,
            lon: -122.0,
            heading: 100,
            followingCallsign: LeadCallsign
        );
        var lead = MakeAircraftOnPatternPhase<DownwindPhase>(callsign: LeadCallsign, type: "C172", lat: 37.00, lon: -121.99, heading: 100);
        // Lead has been on Downwind 60 s longer than follower so IsLeadPatternFlowBehind
        // returns false (lead is ahead in the same-leg ordering — that's the *runaway*
        // direction, not the *flow-ahead* one). Without further input the gap is just
        // open; explicit lead motion away from follower makes the watchdog fire.
        lead.Phases!.CurrentPhase!.ElapsedSeconds = 60;

        bool cancelled = false;
        for (int i = 0; i < 35; i++)
        {
            // Lead moves east faster than follower — distance grows monotonically.
            lead.Position = new LatLon(lead.Position.Lat, lead.Position.Lon + 0.001);
            var ctx = Ctx(follower, lookup: cs => cs == LeadCallsign ? lead : null);
            if (AirborneFollowHelper.CheckLeadLifecycle(ctx))
            {
                cancelled = true;
                break;
            }
        }

        Assert.True(cancelled, "Runaway watchdog should still fire when lead on same leg pulls away monotonically");
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    // -------------------------------------------------------------------------
    // ShouldHoldForLeadSequencing: extend downwind to sequence behind an
    // ahead lead instead of turning base early and overtaking it.
    // -------------------------------------------------------------------------

    private static readonly LatLon SeqThreshold = new(37.0, -122.0);

    // Downwind axis = reciprocal of runway 28's 280° heading.
    private static readonly TrueHeading SeqDownwindHeading = new(100.0);

    /// <summary>Lat/lon of a point at along-track distance <paramref name="alongTrackNm"/>
    /// from <see cref="SeqThreshold"/> along the downwind axis (larger = further out
    /// in the approach direction = further back in the landing sequence).</summary>
    private static (double Lat, double Lon) AtAlongTrack(double alongTrackNm)
    {
        var p = GeoMath.ProjectPoint(SeqThreshold, SeqDownwindHeading, alongTrackNm);
        return (p.Lat, p.Lon);
    }

    private AircraftState FollowerOnDownwindAt(double alongTrackNm, string? leadCallsign, string type = "C172")
    {
        var (lat, lon) = AtAlongTrack(alongTrackNm);
        return MakeAircraftOnPatternPhase<DownwindPhase>(
            callsign: "FOLL",
            type: type,
            lat: lat,
            lon: lon,
            heading: SeqDownwindHeading.Degrees,
            followingCallsign: leadCallsign
        );
    }

    private AircraftState LeadOnPhaseAt<TPhase>(double alongTrackNm, string type = "C172")
        where TPhase : Phase, new()
    {
        var (lat, lon) = AtAlongTrack(alongTrackNm);
        return MakeAircraftOnPatternPhase<TPhase>(callsign: "LEAD", type: type, lat: lat, lon: lon, heading: 280);
    }

    [Fact]
    public void ShouldHoldForLeadSequencing_True_WhenLeadAheadOnFinal_AndUnderSpaced()
    {
        // Follower on Downwind 0.5 nm out; lead on Final 0.86 nm out (just rolled
        // out ahead). aFollower - aLead = -0.36 < 1.0 (piston desired) → hold.
        var follower = FollowerOnDownwindAt(0.5, "LEAD");
        var lead = LeadOnPhaseAt<FinalApproachPhase>(0.86);
        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? lead : null);

        Assert.True(AirborneFollowHelper.ShouldHoldForLeadSequencing(ctx, SeqThreshold, SeqDownwindHeading));
    }

    [Fact]
    public void ShouldHoldForLeadSequencing_True_WhenLeadAheadOnBase()
    {
        var follower = FollowerOnDownwindAt(0.5, "LEAD");
        var lead = LeadOnPhaseAt<BasePhase>(0.9);
        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? lead : null);

        Assert.True(AirborneFollowHelper.ShouldHoldForLeadSequencing(ctx, SeqThreshold, SeqDownwindHeading));
    }

    [Fact]
    public void ShouldHoldForLeadSequencing_False_WhenFollowerAlreadyAdequatelyBehind()
    {
        // Follower has extended well downwind (2.5 nm out) while the lead is short
        // final (0.3 nm). aFollower - aLead = 2.2 >= 1.0 → release, turn base.
        var follower = FollowerOnDownwindAt(2.5, "LEAD");
        var lead = LeadOnPhaseAt<FinalApproachPhase>(0.3);
        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? lead : null);

        Assert.False(AirborneFollowHelper.ShouldHoldForLeadSequencing(ctx, SeqThreshold, SeqDownwindHeading));
    }

    [Fact]
    public void ShouldHoldForLeadSequencing_False_WhenLeadIsPatternFlowBehind()
    {
        // Lead is on an EARLIER leg (Upwind) — it is trailing, not leading. The
        // base-turn hold must not fire; spacing for a trailing lead is the speed
        // path's concern.
        var follower = FollowerOnDownwindAt(0.5, "LEAD");
        var lead = LeadOnPhaseAt<UpwindPhase>(0.5);
        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? lead : null);

        Assert.False(AirborneFollowHelper.ShouldHoldForLeadSequencing(ctx, SeqThreshold, SeqDownwindHeading));
    }

    [Fact]
    public void ShouldHoldForLeadSequencing_False_WhenLeadOnSameLeg()
    {
        // Both on Downwind — only a strictly later leg counts as pattern-flow-ahead,
        // so a co-leg lead does not trigger the sequencing hold (same-leg spacing is
        // handled by the proximity / speed paths).
        var follower = FollowerOnDownwindAt(0.3, "LEAD");
        var lead = LeadOnPhaseAt<DownwindPhase>(0.9);
        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? lead : null);

        Assert.False(AirborneFollowHelper.ShouldHoldForLeadSequencing(ctx, SeqThreshold, SeqDownwindHeading));
    }

    [Fact]
    public void ShouldHoldForLeadSequencing_False_WhenNoFollowOrLeadMissing()
    {
        var noFollow = FollowerOnDownwindAt(0.5, leadCallsign: null);
        Assert.False(AirborneFollowHelper.ShouldHoldForLeadSequencing(Ctx(noFollow), SeqThreshold, SeqDownwindHeading));

        var orphan = FollowerOnDownwindAt(0.5, "GHOST");
        var ctx = Ctx(orphan, lookup: _ => null);
        Assert.False(AirborneFollowHelper.ShouldHoldForLeadSequencing(ctx, SeqThreshold, SeqDownwindHeading));
    }

    [Fact]
    public void ShouldHoldForLeadSequencing_WiderHoldForJetLead()
    {
        // A jet lead wants 3.0 nm of trail vs 1.0 for a piston. At a 1.5 nm sequence
        // gap the piston follower would release but a jet follower must keep holding.
        var followerBehindPiston = FollowerOnDownwindAt(1.8, "LEAD");
        var pistonLead = LeadOnPhaseAt<FinalApproachPhase>(0.3, type: "C172");
        var pistonCtx = Ctx(followerBehindPiston, lookup: cs => cs == "LEAD" ? pistonLead : null);
        Assert.False(AirborneFollowHelper.ShouldHoldForLeadSequencing(pistonCtx, SeqThreshold, SeqDownwindHeading));

        var followerBehindJet = FollowerOnDownwindAt(1.8, "LEAD");
        var jetLead = LeadOnPhaseAt<FinalApproachPhase>(0.3, type: "B738");
        var jetCtx = Ctx(followerBehindJet, lookup: cs => cs == "LEAD" ? jetLead : null);
        Assert.True(AirborneFollowHelper.ShouldHoldForLeadSequencing(jetCtx, SeqThreshold, SeqDownwindHeading));
    }
}
