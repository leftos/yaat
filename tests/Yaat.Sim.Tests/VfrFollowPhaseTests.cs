using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for the VFR FOLLOW command and <see cref="VfrFollowPhase"/>.
///
/// FOLLOW for a VFR aircraft must work from any airborne state:
/// - From clean vectoring (no active phase) → install VfrFollowPhase
/// - From an existing VfrFollowPhase → retarget in place
/// - From a pattern phase that already honors FollowingCallsign → just set the callsign
///
/// When the lead is in a pattern, VfrFollowPhase pursues the lead until close
/// to the pattern, then swaps itself out for a PatternEntryPhase + pattern circuit
/// copying the lead's runway, direction, and altitude.
/// </summary>
[Collection("NavDbMutator")]
public class VfrFollowPhaseTests : IDisposable
{
    private readonly IDisposable _navDbScope;

    public VfrFollowPhaseTests()
    {
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(DefaultRunway()));
    }

    public void Dispose() => _navDbScope.Dispose();

    // Runway 28 at KTEST: heading 280°, 0 ft MSL
    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 0);

    private static AircraftState MakeVfrAircraft(
        string callsign,
        string type,
        double lat,
        double lon,
        double heading,
        double altitude,
        double ias,
        bool onGround
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
            FlightPlan = new AircraftFlightPlan { Destination = "KTEST", FlightRules = "VFR" },
            IsOnGround = onGround,
            // Default: traffic already in sight. Tests that exercise the RTIS
            // gate explicitly set this to false.
            Approach = new AircraftApproachState { HasReportedTrafficInSight = true },
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static AircraftState MakeVfrAircraft(string callsign = "N123", double lat = 37.0, double lon = -122.0) =>
        MakeVfrAircraft(callsign, "C172", lat, lon, heading: 280, altitude: 2500, ias: 90, onGround: false);

    private static PhaseContext Ctx(AircraftState ac, Func<string, AircraftState?>? lookup, double dt = 1.0)
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

    private static DispatchContext DispatchCtx(Func<string, AircraftState?>? lookup = null) =>
        TestDispatch.Context(Random.Shared, findAircraft: lookup);

    // ---------------------------------------------------------------------
    // Dispatcher: command acceptance
    // ---------------------------------------------------------------------

    [Fact]
    public void Follow_FromClean_VfrAircraft_InstallsVfrFollowPhase()
    {
        var follower = MakeVfrAircraft("FOLL");
        // No current phase — the aircraft is under basic vectoring.
        Assert.Null(follower.Phases?.CurrentPhase);

        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), follower, DispatchCtx());

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.NotNull(follower.Phases);
        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal("LEAD", follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void Follow_FromClean_NullPhases_InstallsVfrFollowPhase()
    {
        var follower = MakeVfrAircraft("FOLL");
        follower.Phases = null;

        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), follower, DispatchCtx());

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.NotNull(follower.Phases);
        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
    }

    [Fact]
    public void Follow_NotVfr_IsRejected()
    {
        var follower = MakeVfrAircraft("FOLL");
        follower.FlightPlan.FlightRules = "IFR";

        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), follower, DispatchCtx());

        Assert.False(result.Success);
        Assert.Contains("VFR", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Follow_OnGround_IsRejected()
    {
        var follower = MakeVfrAircraft("FOLL", "C172", lat: 37.0, lon: -122.0, heading: 280, altitude: 0, ias: 0, onGround: true);

        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), follower, DispatchCtx());

        Assert.False(result.Success);
    }

    [Fact]
    public void Follow_WithoutRtis_IsRejected()
    {
        // A pilot can't follow traffic they haven't visually acquired.
        var follower = MakeVfrAircraft("FOLL");
        follower.Approach.HasReportedTrafficInSight = false;

        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), follower, DispatchCtx());

        Assert.False(result.Success);
        Assert.Contains("traffic not in sight", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Follow_AfterRtisf_Succeeds()
    {
        // Controllers can force traffic-in-sight with RTISF, same as CVA FOLLOW.
        var follower = MakeVfrAircraft("FOLL");
        follower.Approach.HasReportedTrafficInSight = false;

        CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand("LEAD"), follower, DispatchCtx());
        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), follower, DispatchCtx());

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.IsType<VfrFollowPhase>(follower.Phases?.CurrentPhase);
    }

    [Fact]
    public void Follow_RetargetsExistingVfrFollowPhase_WithoutRecreating()
    {
        var follower = MakeVfrAircraft("FOLL");

        var first = CommandDispatcher.Dispatch(new FollowCommand("LEAD1"), follower, DispatchCtx());
        Assert.True(first.Success);
        var phase1 = follower.Phases!.CurrentPhase;
        Assert.IsType<VfrFollowPhase>(phase1);

        var second = CommandDispatcher.Dispatch(new FollowCommand("LEAD2"), follower, DispatchCtx());
        Assert.True(second.Success);

        Assert.Same(phase1, follower.Phases!.CurrentPhase);
        Assert.Equal("LEAD2", follower.Approach.FollowingCallsign);
        Assert.Equal("LEAD2", ((VfrFollowPhase)follower.Phases!.CurrentPhase!).TargetCallsign);
    }

    [Fact]
    public void Follow_ReplacesExistingNonPatternPhase()
    {
        // A VFR aircraft with an existing phase that is NOT pattern/follow
        // should have its phase list replaced entirely so the new VfrFollowPhase
        // becomes the active phase (no stale currentIndex from the old list).
        var follower = MakeVfrAircraft("FOLL");
        var oldPhase = new VfrHoldPhase();
        follower.Phases!.Add(oldPhase);
        var startCtx = CommandDispatcher.BuildMinimalContext(follower);
        follower.Phases.Start(startCtx);

        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), follower, DispatchCtx());

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal("LEAD", follower.Approach.FollowingCallsign);
        // Old phase should no longer be current.
        Assert.NotSame(oldPhase, follower.Phases.CurrentPhase);
    }

    [Fact]
    public void Follow_FromDownwindPhase_OnlySetsFollowingCallsign()
    {
        // A VFR aircraft already on downwind should keep its DownwindPhase and just
        // update FollowingCallsign — existing AirborneFollowHelper handles spacing.
        var follower = MakeVfrAircraft("FOLL");
        var downwind = new DownwindPhase();
        follower.Phases!.Add(downwind);
        // Don't fully Start() the phase — we just need the dispatcher to see it as current.
        // A simpler path: peek via reflection-free Add + manual Start would need a context,
        // but the dispatcher only reads CurrentPhase.
        var ctx = CommandDispatcher.BuildMinimalContext(follower);
        follower.Phases.Start(ctx);

        var result = CommandDispatcher.Dispatch(new FollowCommand("LEAD"), follower, DispatchCtx());

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Same(downwind, follower.Phases.CurrentPhase);
        Assert.Equal("LEAD", follower.Approach.FollowingCallsign);
    }

    // ---------------------------------------------------------------------
    // VfrFollowPhase.OnTick — free flight (lead not in a pattern)
    // ---------------------------------------------------------------------

    [Fact]
    public void VfrFollowPhase_FreeFlight_TurnsTowardLead()
    {
        // Follower at 37.00 N 122.00 W heading 280° (west).
        // Leader ~5 nm due east — the follower should turn toward bearing ~090°.
        var follower = MakeVfrAircraft("FOLL", lat: 37.00, lon: -122.00);
        follower.Approach.FollowingCallsign = "LEAD";
        double lonPerNm = 1.0 / (60.0 * Math.Cos(37.0 * Math.PI / 180.0));
        var lead = MakeVfrAircraft("LEAD", lat: 37.00, lon: -122.00 + (5.0 * lonPerNm));

        var phase = new VfrFollowPhase("LEAD");
        follower.Phases!.Add(phase);
        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? lead : null);
        follower.Phases.Start(ctx);

        bool done = phase.OnTick(ctx);

        Assert.False(done);
        Assert.NotNull(follower.Targets.TargetTrueHeading);
        double bearing = follower.Targets.TargetTrueHeading!.Value.Degrees;
        Assert.InRange(bearing, 85, 95);
    }

    [Fact]
    public void VfrFollowPhase_FreeFlight_DoesNotAlterAltitude()
    {
        // Real pilots told "follow traffic" maintain their assigned altitude —
        // they do not dive/climb onto the lead. VfrFollowPhase respects that.
        // Altitude is picked up by PatternEntryPhase on auto-join.
        var follower = MakeVfrAircraft("FOLL");
        follower.Approach.FollowingCallsign = "LEAD";
        follower.Altitude = 2500;
        follower.Targets.TargetAltitude = 2500; // last controller-assigned altitude
        var lead = MakeVfrAircraft("LEAD", lat: 37.0 + (2.0 / 60.0), lon: -122.0);
        lead.Altitude = 3500;

        var phase = new VfrFollowPhase("LEAD");
        follower.Phases!.Add(phase);
        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? lead : null);
        follower.Phases.Start(ctx);

        phase.OnTick(ctx);

        Assert.Equal(2500, follower.Targets.TargetAltitude);
    }

    [Fact]
    public void VfrFollowPhase_FreeFlight_MatchesLeadSpeed_WithSpacingCorrection()
    {
        // Lead is slow (80 kts); follower behind with good spacing (2nm for a piston
        // desired distance of 1nm → too far, speed correction should *raise* speed
        // above lead's 80 kts toward the normal follow window).
        var follower = MakeVfrAircraft("FOLL", lat: 37.0, lon: -122.0);
        follower.Approach.FollowingCallsign = "LEAD";
        var lead = MakeVfrAircraft("LEAD", lat: 37.0, lon: -122.0 + (2.0 / 54.0));
        lead.IndicatedAirspeed = 80;

        var phase = new VfrFollowPhase("LEAD");
        follower.Phases!.Add(phase);
        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? lead : null);
        follower.Phases.Start(ctx);

        phase.OnTick(ctx);

        Assert.NotNull(follower.Targets.TargetSpeed);
        // Follower is ~2nm behind, desired 1nm piston → error +1nm, +25 kts clamped
        // to +20 → ~100 kts.
        Assert.True(follower.Targets.TargetSpeed > 80, $"Expected > 80 kts, got {follower.Targets.TargetSpeed}");
    }

    [Fact]
    public void VfrFollowPhase_LeadDisappears_PhaseEnds()
    {
        var follower = MakeVfrAircraft("FOLL");
        follower.Approach.FollowingCallsign = "LEAD";

        var phase = new VfrFollowPhase("LEAD");
        follower.Phases!.Add(phase);
        var ctx = Ctx(follower, lookup: _ => null);
        follower.Phases.Start(ctx);

        bool done = phase.OnTick(ctx);

        Assert.True(done);
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.NotEmpty(follower.PendingWarnings);
    }

    [Fact]
    public void VfrFollowPhase_LeadOnGround_PhaseEnds()
    {
        var follower = MakeVfrAircraft("FOLL");
        follower.Approach.FollowingCallsign = "LEAD";
        var lead = MakeVfrAircraft("LEAD", "C172", lat: 37.0, lon: -122.0, heading: 280, altitude: 0, ias: 0, onGround: true);

        var phase = new VfrFollowPhase("LEAD");
        follower.Phases!.Add(phase);
        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? lead : null);
        follower.Phases.Start(ctx);

        bool done = phase.OnTick(ctx);

        Assert.True(done);
    }

    // ---------------------------------------------------------------------
    // VfrFollowPhase.OnTick — lead in pattern, auto-join transition
    // ---------------------------------------------------------------------

    [Fact]
    public void VfrFollowPhase_LeadInDownwind_FollowerFarAway_StaysInPursuit()
    {
        // Runway 28 at 37.72, -122.22. Place follower 20 nm east — far from the pattern.
        // Lead is in DownwindPhase in the same pattern.
        var runway = DefaultRunway();
        var waypoints = PatternGeometry.Compute(
            runway,
            AircraftCategory.Piston,
            PatternDirection.Left,
            sizeOverrideNm: null,
            altitudeOverrideFt: null,
            airportRunways: [runway]
        );

        // Place follower 20 nm east of the runway threshold
        double farLat = runway.ThresholdLatitude;
        double farLon = runway.ThresholdLongitude + (20.0 / (60.0 * Math.Cos(runway.ThresholdLatitude * Math.PI / 180.0)));
        var follower = MakeVfrAircraft("FOLL", lat: farLat, lon: farLon);
        follower.Approach.FollowingCallsign = "LEAD";

        var lead = MakeVfrAircraft("LEAD", lat: waypoints.DownwindAbeamLat, lon: waypoints.DownwindAbeamLon);
        lead.Phases = new PhaseList { AssignedRunway = runway, TrafficDirection = PatternDirection.Left };
        lead.Phases.Add(new DownwindPhase { Waypoints = waypoints });
        // Start the lead's downwind so CurrentPhase is set
        var leadCtx = CommandDispatcher.BuildMinimalContext(lead);
        lead.Phases.Start(leadCtx);

        var phase = new VfrFollowPhase("LEAD");
        follower.Phases!.Add(phase);
        follower.Phases.AssignedRunway = null; // follower hasn't been assigned yet
        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? lead : null);
        follower.Phases.Start(ctx);

        bool done = phase.OnTick(ctx);

        Assert.False(done);
        Assert.IsType<VfrFollowPhase>(follower.Phases.CurrentPhase);
    }

    [Fact]
    public void VfrFollowPhase_LeadInDownwind_FollowerWithinJoinRange_TransitionsToPattern()
    {
        var runway = DefaultRunway();
        var waypoints = PatternGeometry.Compute(
            runway,
            AircraftCategory.Piston,
            PatternDirection.Left,
            sizeOverrideNm: null,
            altitudeOverrideFt: null,
            airportRunways: [runway]
        );

        // Place follower 1 nm further south of the downwind abeam point — within
        // join range and unambiguously on the pattern side of runway 28 for a left
        // pattern (south of the runway).
        var follower = MakeVfrAircraft("FOLL", lat: waypoints.DownwindAbeamLat - (1.0 / 60.0), lon: waypoints.DownwindAbeamLon);
        follower.Approach.FollowingCallsign = "LEAD";

        var lead = MakeVfrAircraft("LEAD", lat: waypoints.DownwindAbeamLat, lon: waypoints.DownwindAbeamLon);
        lead.Phases = new PhaseList { AssignedRunway = runway, TrafficDirection = PatternDirection.Left };
        lead.Phases.Add(new DownwindPhase { Waypoints = waypoints });
        var leadCtx = CommandDispatcher.BuildMinimalContext(lead);
        lead.Phases.Start(leadCtx);

        var phase = new VfrFollowPhase("LEAD");
        follower.Phases!.Add(phase);
        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? lead : null);
        follower.Phases.Start(ctx);

        phase.OnTick(ctx);

        // Phase should have swapped itself out for pattern phases.
        Assert.IsNotType<VfrFollowPhase>(follower.Phases.CurrentPhase);
        Assert.NotNull(follower.Phases.CurrentPhase);
        // Expected: PatternEntryPhase → DownwindPhase → BasePhase → FinalApproachPhase → LandingPhase
        Assert.True(
            follower.Phases.CurrentPhase is PatternEntryPhase or DownwindPhase,
            $"Expected PatternEntryPhase or DownwindPhase, got {follower.Phases.CurrentPhase!.GetType().Name}"
        );
        Assert.Equal(runway.Designator, follower.Phases.AssignedRunway?.Designator);
        Assert.Equal(PatternDirection.Left, follower.Phases.TrafficDirection);
        Assert.Equal("LEAD", follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void VfrFollowPhase_LeadInDownwind_FollowerOnWrongSideOfRunway_DoesNotJoin()
    {
        // A follower on the opposite side of the runway from the pattern would
        // have to cross final to reach the abeam point — don't auto-join.
        var runway = DefaultRunway();
        var waypoints = PatternGeometry.Compute(
            runway,
            AircraftCategory.Piston,
            PatternDirection.Left,
            sizeOverrideNm: null,
            altitudeOverrideFt: null,
            airportRunways: [runway]
        );

        // Left pattern for runway 28 → downwind is south of the runway.
        // Place the follower NORTH of the runway (wrong side), inside the 3nm join range.
        var follower = MakeVfrAircraft("FOLL", lat: runway.ThresholdLatitude + (0.5 / 60.0), lon: runway.ThresholdLongitude);
        follower.Approach.FollowingCallsign = "LEAD";

        var lead = MakeVfrAircraft("LEAD", lat: waypoints.DownwindAbeamLat, lon: waypoints.DownwindAbeamLon);
        lead.Phases = new PhaseList { AssignedRunway = runway, TrafficDirection = PatternDirection.Left };
        lead.Phases.Add(new DownwindPhase { Waypoints = waypoints });
        var leadCtx = CommandDispatcher.BuildMinimalContext(lead);
        lead.Phases.Start(leadCtx);

        var phase = new VfrFollowPhase("LEAD");
        follower.Phases!.Add(phase);
        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? lead : null);
        follower.Phases.Start(ctx);

        phase.OnTick(ctx);

        Assert.IsType<VfrFollowPhase>(follower.Phases.CurrentPhase);
    }

    [Fact]
    public void VfrFollowPhase_RunawayDistance_CancelsFollowAfterGracePeriod()
    {
        // Follower and lead both head east; follower is slower. Simulate enough ticks
        // of growing distance to exceed the runaway grace period.
        var follower = MakeVfrAircraft("FOLL", lat: 37.0, lon: -122.0);
        follower.Approach.FollowingCallsign = "LEAD";
        var lead = MakeVfrAircraft("LEAD", lat: 37.0, lon: -121.9);

        var phase = new VfrFollowPhase("LEAD");
        follower.Phases!.Add(phase);
        var ctx = Ctx(follower, lookup: cs => cs == "LEAD" ? lead : null, dt: 10.0);
        follower.Phases.Start(ctx);

        // First tick sets the baseline best gap.
        phase.OnTick(ctx);
        Assert.False(ctx.Aircraft.Phases!.IsComplete);

        // Move the lead progressively farther east on each tick until runaway triggers.
        bool cancelled = false;
        for (int i = 0; i < 10; i++)
        {
            lead.Position = new LatLon(lead.Position.Lat, lead.Position.Lon + 0.1); // ~5 nm per tick at this latitude
            if (phase.OnTick(ctx))
            {
                cancelled = true;
                break;
            }
        }

        Assert.True(cancelled, "Expected runaway-distance auto-cancel");
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void VfrFollowPhase_AcceptsFollowCommand_ForRetarget()
    {
        var phase = new VfrFollowPhase("LEAD");
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.Follow));
    }

    [Fact]
    public void VfrFollowPhase_AcceptsBasicVectorCommands()
    {
        // Basic vectoring (heading/altitude/speed) should clear the follow phase
        // so the controller can take over with "direct commands". The user said
        // "basic vectoring should be able to be followed using the core controls"
        // — i.e. issuing a vector cancels the follow.
        var phase = new VfrFollowPhase("LEAD");
        // Any vector command not in the explicit allow-list should clear the phase.
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
    }
}
