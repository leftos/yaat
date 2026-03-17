using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class TowerPhaseTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RunwayInfo DefaultRunway(double elevationFt = 100) =>
        TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: elevationFt);

    private static AircraftState MakeAircraft(
        double lat = 37.0,
        double lon = -122.0,
        double heading = 280,
        double altitude = 100,
        double ias = 0,
        bool onGround = true,
        string type = "B738"
    )
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = type,
            Latitude = lat,
            Longitude = lon,
            Heading = heading,
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = onGround,
            Departure = "TEST",
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo? rwy = null, double dt = 1.0)
    {
        rwy ??= DefaultRunway(ac.Altitude);
        return new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = dt,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };
    }

    // -------------------------------------------------------------------------
    // LinedUpAndWaitingPhase
    // -------------------------------------------------------------------------

    [Fact]
    public void LinedUpAndWaiting_OnStart_SetsSpeedZeroAndRunwayHeading()
    {
        var ac = MakeAircraft(heading: 100);
        var rwy = DefaultRunway();
        var phase = new LinedUpAndWaitingPhase();
        var ctx = Ctx(ac, rwy);

        phase.OnStart(ctx);

        Assert.Equal(0, ac.Targets.TargetSpeed);
        Assert.Equal(280, ac.Targets.TargetHeading);
        Assert.True(ac.IsOnGround);
    }

    [Fact]
    public void LinedUpAndWaiting_HoldsUntilClearedForTakeoff()
    {
        var ac = MakeAircraft();
        var phase = new LinedUpAndWaitingPhase();
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        // Not cleared yet — should not advance
        Assert.False(phase.OnTick(ctx));
        Assert.Equal(0, ac.Targets.TargetSpeed);

        // Satisfy clearance
        phase.SatisfyClearance(ClearanceType.ClearedForTakeoff);
        Assert.True(phase.OnTick(ctx));
    }

    [Fact]
    public void LinedUpAndWaiting_AcceptsCTO_RejectsOthers()
    {
        var phase = new LinedUpAndWaitingPhase();

        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClearedForTakeoff));
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.CancelTakeoffClearance));
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.Delete));
        Assert.Equal(CommandAcceptance.Rejected, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
        Assert.Equal(CommandAcceptance.Rejected, phase.CanAcceptCommand(CanonicalCommandType.Speed));
    }

    // -------------------------------------------------------------------------
    // GoAroundPhase
    // -------------------------------------------------------------------------

    [Fact]
    public void GoAround_OnStart_SetsClimbAndRunwayHeading()
    {
        var rwy = DefaultRunway(100);
        var ac = MakeAircraft(altitude: 100, onGround: false);
        var phase = new GoAroundPhase();
        var ctx = Ctx(ac, rwy);

        phase.OnStart(ctx);

        Assert.False(ac.IsOnGround);
        Assert.Equal(280, ac.Targets.TargetHeading);
        // Target altitude = field elevation + 2000ft AGL
        Assert.Equal(2100, ac.Targets.TargetAltitude);
        Assert.True(ac.Targets.DesiredVerticalRate > 0);
    }

    [Fact]
    public void GoAround_CompletesAt2000AGL()
    {
        var rwy = DefaultRunway(100);
        var ac = MakeAircraft(altitude: 100, onGround: false);
        var phase = new GoAroundPhase();
        var ctx = Ctx(ac, rwy);

        phase.OnStart(ctx);

        // Below target — not done
        ac.Altitude = 1500;
        Assert.False(phase.OnTick(ctx));

        // At 2100 (2000 AGL) — done
        ac.Altitude = 2100;
        Assert.True(phase.OnTick(ctx));
    }

    [Fact]
    public void GoAround_WithAssignedHeading_TurnsAfter400AGL()
    {
        var rwy = DefaultRunway(100);
        var ac = MakeAircraft(altitude: 100, onGround: false);
        var phase = new GoAroundPhase { AssignedHeading = 360 };
        var ctx = Ctx(ac, rwy);

        phase.OnStart(ctx);
        Assert.Equal(280, ac.Targets.TargetHeading); // runway heading initially

        // Below 400 AGL — still runway heading
        ac.Altitude = 400;
        phase.OnTick(ctx);
        Assert.Equal(280, ac.Targets.TargetHeading);

        // Above 400 AGL — turns to assigned heading
        ac.Altitude = 550;
        phase.OnTick(ctx);
        Assert.Equal(360, ac.Targets.TargetHeading);
    }

    [Fact]
    public void GoAround_WithTargetAltitude_CompletesAtThatAltitude()
    {
        var rwy = DefaultRunway(100);
        var ac = MakeAircraft(altitude: 100, onGround: false);
        var phase = new GoAroundPhase { TargetAltitude = 3000 };
        var ctx = Ctx(ac, rwy);

        phase.OnStart(ctx);
        Assert.Equal(3000, ac.Targets.TargetAltitude);

        ac.Altitude = 2100; // would be complete at 2000 AGL without override
        Assert.False(phase.OnTick(ctx));

        ac.Altitude = 3000;
        Assert.True(phase.OnTick(ctx));
    }

    [Fact]
    public void GoAround_AnyCommand_ClearsPhase()
    {
        var phase = new GoAroundPhase();

        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.Speed));
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.Delete));
    }

    // -------------------------------------------------------------------------
    // TouchAndGoPhase
    // -------------------------------------------------------------------------

    [Fact]
    public void TouchAndGo_OnStart_OnGroundAndDecelerating()
    {
        var ac = MakeAircraft(onGround: false);
        var phase = new TouchAndGoPhase();
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.True(ac.IsOnGround);
        Assert.Equal(280, ac.Targets.TargetHeading);
        Assert.NotNull(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void TouchAndGo_Rollout_ThenReaccelerate_ThenAirborne()
    {
        var rwy = DefaultRunway(100);
        var ac = MakeAircraft(altitude: 100, onGround: false, ias: 130);
        var phase = new TouchAndGoPhase();
        var ctx = Ctx(ac, rwy, dt: 1.0);

        phase.OnStart(ctx);

        // Tick through rollout (Jet = 4s)
        for (int i = 0; i < 5; i++)
        {
            Assert.False(phase.OnTick(ctx));
        }

        // After rollout completes, it reaccelerates. Tick until GS builds.
        // Simulate many ticks — eventually should go airborne.
        bool wentAirborne = false;
        for (int i = 0; i < 200; i++)
        {
            if (phase.OnTick(ctx))
            {
                wentAirborne = true;
                break;
            }
            // Simulate altitude gain once airborne
            if (!ac.IsOnGround)
            {
                ac.Altitude += 20; // approximate climb per tick
            }
        }

        // Verify it eventually completed (went airborne and reached 400ft AGL)
        Assert.True(wentAirborne || !ac.IsOnGround, "Should have gone airborne during reacceleration");
    }

    [Fact]
    public void TouchAndGo_AcceptsGoAround_RejectsOthers()
    {
        var phase = new TouchAndGoPhase();

        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.GoAround));
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.Delete));
        Assert.Equal(CommandAcceptance.Rejected, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
    }

    // -------------------------------------------------------------------------
    // StopAndGoPhase
    // -------------------------------------------------------------------------

    [Fact]
    public void StopAndGo_OnStart_DecelerateToZero()
    {
        var ac = MakeAircraft(onGround: false);
        var phase = new StopAndGoPhase();
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.True(ac.IsOnGround);
        Assert.Equal(0, ac.Targets.TargetSpeed);
    }

    [Fact]
    public void StopAndGo_FullStop_ThenPause_ThenReaccelerate()
    {
        var rwy = DefaultRunway(100);
        var ac = MakeAircraft(altitude: 100, onGround: true, ias: 2);
        var phase = new StopAndGoPhase();
        var ctx = Ctx(ac, rwy, dt: 1.0);

        phase.OnStart(ctx);

        // GS < 3 → should detect full stop on next tick
        Assert.False(phase.OnTick(ctx));
        Assert.Equal(0.0, ac.GroundSpeed); // full stop detected

        // Tick through pause (Jet = 5s)
        for (int i = 0; i < 6; i++)
        {
            Assert.False(phase.OnTick(ctx));
        }

        // Now reaccelerating — tick until airborne
        bool wentAirborne = false;
        for (int i = 0; i < 200; i++)
        {
            if (phase.OnTick(ctx))
            {
                wentAirborne = true;
                break;
            }
            if (!ac.IsOnGround)
            {
                ac.Altitude += 20;
            }
        }

        Assert.True(wentAirborne || !ac.IsOnGround, "Should have gone airborne after stop-and-go");
    }

    [Fact]
    public void StopAndGo_AcceptsGoAround_RejectsOthers()
    {
        var phase = new StopAndGoPhase();

        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.GoAround));
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.Delete));
        Assert.Equal(CommandAcceptance.Rejected, phase.CanAcceptCommand(CanonicalCommandType.Speed));
    }

    [Fact]
    public void StopAndGo_AcceptsGo()
    {
        var phase = new StopAndGoPhase();

        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.Go));
    }

    [Fact]
    public void StopAndGo_TriggerGo_BypassesPauseTimer()
    {
        var rwy = DefaultRunway(100);
        var ac = MakeAircraft(altitude: 100, onGround: true, ias: 2);
        var phase = new StopAndGoPhase();
        var ctx = Ctx(ac, rwy, dt: 1.0);

        phase.OnStart(ctx);

        // GS < 3 → detect full stop on the next tick
        phase.OnTick(ctx);
        Assert.Equal(0.0, ac.GroundSpeed);

        // Trigger GO before the pause timer expires
        phase.TriggerGo();

        // First tick after TriggerGo: sets _reaccelerating = true and returns (pause block exits)
        phase.OnTick(ctx);
        // Second tick: enters the reacceleration block and increases IAS
        phase.OnTick(ctx);

        // IAS should be increasing and TargetSpeed cleared (managed manually in reacceleration)
        Assert.True(ac.IndicatedAirspeed > 0, "Expected IAS to increase once reacceleration begins after TriggerGo");
    }

    // -------------------------------------------------------------------------
    // LowApproachPhase
    // -------------------------------------------------------------------------

    [Fact]
    public void LowApproach_OnStart_SetsRunwayHeadingAndApproachSpeed()
    {
        var rwy = DefaultRunway(100);
        var ac = MakeAircraft(altitude: 500, onGround: false, ias: 140);
        var phase = new LowApproachPhase();
        var ctx = Ctx(ac, rwy);

        phase.OnStart(ctx);

        Assert.Equal(280, ac.Targets.TargetHeading);
        Assert.NotNull(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void LowApproach_ClimbsOutAndCompletesAt1500AGL()
    {
        var rwy = DefaultRunway(100);
        var ac = MakeAircraft(altitude: 180, onGround: false, ias: 140);
        var phase = new LowApproachPhase();
        var ctx = Ctx(ac, rwy);

        phase.OnStart(ctx);

        // Descend to go-around altitude (Jet: 100ft AGL = 200ft MSL)
        ac.Altitude = 200;
        Assert.False(phase.OnTick(ctx));

        // At go-around alt → starts climbing
        ac.Altitude = 199;
        phase.OnTick(ctx);

        // Climb to 1500 AGL (1600 MSL)
        ac.Altitude = 1500;
        Assert.False(phase.OnTick(ctx));

        ac.Altitude = 1600;
        Assert.True(phase.OnTick(ctx));
    }

    [Fact]
    public void LowApproach_AnyCommand_ClearsPhase()
    {
        var phase = new LowApproachPhase();
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
    }

    // -------------------------------------------------------------------------
    // LandingPhase
    // -------------------------------------------------------------------------

    [Fact]
    public void Landing_TouchdownSetsOnGround()
    {
        var rwy = DefaultRunway(100);
        var ac = MakeAircraft(altitude: 110, onGround: false, ias: 140);
        var phase = new LandingPhase();
        var ctx = Ctx(ac, rwy);

        phase.OnStart(ctx);
        Assert.Equal(100.0, ac.Targets.TargetAltitude);

        // Still above field — no touchdown yet
        ac.Altitude = 105;
        phase.OnTick(ctx);
        Assert.False(ac.IsOnGround);

        // At or below field elevation → touchdown (AGL <= 0)
        ac.Altitude = 100;
        phase.OnTick(ctx);

        Assert.True(ac.IsOnGround);
        Assert.Equal(100, ac.Altitude); // clamped to field elevation
        Assert.Equal(0.0, ac.VerticalSpeed);
    }

    [Fact]
    public void Landing_Rollout_CompletesAt20Kts()
    {
        var rwy = DefaultRunway(100);
        var ac = MakeAircraft(altitude: 100, onGround: false, ias: 140);
        var phase = new LandingPhase();
        var ctx = Ctx(ac, rwy, dt: 1.0);

        phase.OnStart(ctx);

        // Force touchdown
        ac.Altitude = 99;
        phase.OnTick(ctx);
        Assert.True(ac.IsOnGround);

        // Rollout: decelerate until <= 20 kts
        bool completed = false;
        for (int i = 0; i < 100; i++)
        {
            if (phase.OnTick(ctx))
            {
                completed = true;
                break;
            }
        }

        Assert.True(completed, "Landing should complete after rollout to 20kts");
        Assert.True(ac.GroundSpeed <= 20.0);
    }

    [Fact]
    public void Landing_BeforeTouchdown_AcceptsGoAroundAndExits()
    {
        var phase = new LandingPhase();

        // Before touchdown (flare)
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.GoAround));
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ExitLeft));
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ExitRight));
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ExitTaxiway));
        Assert.Equal(CommandAcceptance.Rejected, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
    }

    // -------------------------------------------------------------------------
    // VfrHoldPhase — hold at fix
    // -------------------------------------------------------------------------

    [Fact]
    public void VfrHold_AtFix_OnStart_NavigatesToFix()
    {
        var ac = MakeAircraft(altitude: 5000, onGround: false, ias: 200);
        var phase = new VfrHoldPhase
        {
            FixName = "EDDYY",
            FixLat = 37.5,
            FixLon = -122.5,
            OrbitDirection = TurnDirection.Right,
        };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal("EDDYY", ac.Targets.NavigationRoute[0].Name);
    }

    [Fact]
    public void VfrHold_AtFix_ArrivesAtFix_ClearsRouteAndOrbits()
    {
        var ac = MakeAircraft(lat: 37.5, lon: -122.5, altitude: 5000, onGround: false, ias: 200, heading: 90);
        var phase = new VfrHoldPhase
        {
            FixName = "EDDYY",
            FixLat = 37.5,
            FixLon = -122.5,
            OrbitDirection = TurnDirection.Right,
        };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);
        phase.OnTick(ctx); // dist < 0.5nm → arrives

        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Equal(TurnDirection.Right, ac.Targets.PreferredTurnDirection);
        // Target should be 180° ahead of current heading
        Assert.Equal(270, ac.Targets.TargetHeading);
    }

    [Fact]
    public void VfrHold_AtFix_OrbitTargetStaysAhead_AcrossTicks()
    {
        var ac = MakeAircraft(lat: 37.5, lon: -122.5, altitude: 5000, onGround: false, ias: 200, heading: 90);
        var phase = new VfrHoldPhase
        {
            FixName = "EDDYY",
            FixLat = 37.5,
            FixLon = -122.5,
            OrbitDirection = TurnDirection.Left,
        };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);
        phase.OnTick(ctx); // arrive at fix

        // Now simulate heading changes while orbiting
        double[] headings = [90, 80, 45, 350, 270, 180, 100, 90];
        foreach (double hdg in headings)
        {
            ac.Heading = hdg;
            phase.OnTick(ctx);

            Assert.Equal(TurnDirection.Left, ac.Targets.PreferredTurnDirection);
            double expected = (hdg + 360 - 180) % 360;
            Assert.Equal(expected, ac.Targets.TargetHeading);
        }
    }

    [Fact]
    public void VfrHold_AtFix_NeverSelfCompletes()
    {
        var ac = MakeAircraft(lat: 37.5, lon: -122.5, altitude: 5000, onGround: false, ias: 200);
        var phase = new VfrHoldPhase
        {
            FixName = "EDDYY",
            FixLat = 37.5,
            FixLon = -122.5,
            OrbitDirection = TurnDirection.Right,
        };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        for (int i = 0; i < 500; i++)
        {
            Assert.False(phase.OnTick(ctx));
        }
    }

    [Fact]
    public void VfrHold_AtFix_HelicopterHover_ZeroSpeed()
    {
        var ac = MakeAircraft(lat: 37.5, lon: -122.5, altitude: 5000, onGround: false, ias: 80);
        var phase = new VfrHoldPhase
        {
            FixName = "EDDYY",
            FixLat = 37.5,
            FixLon = -122.5,
            OrbitDirection = null, // helicopter hover
        };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);
        phase.OnTick(ctx); // arrive

        Assert.Equal(0, ac.Targets.TargetSpeed);
        Assert.Null(ac.Targets.PreferredTurnDirection);
    }

    [Theory]
    [InlineData(CanonicalCommandType.ClimbMaintain)]
    [InlineData(CanonicalCommandType.DescendMaintain)]
    [InlineData(CanonicalCommandType.Speed)]
    [InlineData(CanonicalCommandType.Mach)]
    public void VfrHold_AtFix_AllowsAltitudeAndSpeedCommands(CanonicalCommandType cmd)
    {
        var phase = new VfrHoldPhase
        {
            FixName = "X",
            FixLat = 0,
            FixLon = 0,
            OrbitDirection = TurnDirection.Left,
        };
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
    }

    [Theory]
    [InlineData(CanonicalCommandType.FlyHeading)]
    [InlineData(CanonicalCommandType.DirectTo)]
    [InlineData(CanonicalCommandType.ClearedToLand)]
    public void VfrHold_AtFix_OtherCommands_ClearPhase(CanonicalCommandType cmd)
    {
        var phase = new VfrHoldPhase
        {
            FixName = "X",
            FixLat = 0,
            FixLon = 0,
            OrbitDirection = TurnDirection.Left,
        };
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(cmd));
    }

    [Fact]
    public void VfrHold_AtFix_Name_ReflectsState()
    {
        var phase = new VfrHoldPhase
        {
            FixName = "EDDYY",
            FixLat = 37.5,
            FixLon = -122.5,
            OrbitDirection = TurnDirection.Right,
        };

        Assert.Equal("ProceedToFix", phase.Name);

        // After arriving, name changes
        var ac = MakeAircraft(lat: 37.5, lon: -122.5, altitude: 5000, onGround: false, ias: 200);
        var ctx = Ctx(ac);
        phase.OnStart(ctx);
        phase.OnTick(ctx); // arrive

        Assert.Equal("HoldingAtFix", phase.Name);
    }

    // -------------------------------------------------------------------------
    // VfrHoldPhase — hold present position
    // -------------------------------------------------------------------------

    [Fact]
    public void VfrHold_PresentPosition_Orbit_SetsPreferredTurn()
    {
        var ac = MakeAircraft(altitude: 5000, onGround: false, ias: 200, heading: 90);
        var phase = new VfrHoldPhase { OrbitDirection = TurnDirection.Left };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.Equal(TurnDirection.Left, ac.Targets.PreferredTurnDirection);
        Assert.Empty(ac.Targets.NavigationRoute);
        // Target should be ~180° from current heading in the turn direction (270 for left from 90)
        Assert.Equal(270, ac.Targets.TargetHeading);
    }

    [Fact]
    public void VfrHold_PresentPosition_OrbitTargetStaysAhead_AcrossTicks()
    {
        var ac = MakeAircraft(altitude: 5000, onGround: false, ias: 200, heading: 90);
        var phase = new VfrHoldPhase { OrbitDirection = TurnDirection.Right };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        // Simulate heading changes across ticks — physics turns the aircraft
        double[] headings = [90, 95, 110, 150, 200, 270, 350, 30, 80, 90];
        foreach (double hdg in headings)
        {
            ac.Heading = hdg;
            phase.OnTick(ctx);

            // PreferredTurnDirection must never be cleared
            Assert.Equal(TurnDirection.Right, ac.Targets.PreferredTurnDirection);
            // Target should always be 180° ahead in the turn direction
            double expected = (hdg + 180) % 360;
            Assert.Equal(expected, ac.Targets.TargetHeading);
        }
    }

    [Fact]
    public void VfrHold_PresentPosition_CumulativeTurnTracking()
    {
        var ac = MakeAircraft(altitude: 5000, onGround: false, ias: 200, heading: 0);
        var phase = new VfrHoldPhase { OrbitDirection = TurnDirection.Right };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        // Simulate a full 360° of right turns in 10° increments
        for (int i = 1; i <= 36; i++)
        {
            ac.Heading = (i * 10) % 360;
            Assert.False(phase.OnTick(ctx));
        }

        // Phase should never self-complete regardless of how many orbits
        Assert.False(phase.OnTick(ctx));
    }

    [Fact]
    public void VfrHold_PresentPosition_HelicopterHover_ZeroSpeed()
    {
        var ac = MakeAircraft(altitude: 500, onGround: false, ias: 80, heading: 180);
        var phase = new VfrHoldPhase { OrbitDirection = null };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.Equal(0, ac.Targets.TargetSpeed);
        Assert.Null(ac.Targets.PreferredTurnDirection);
    }

    [Fact]
    public void VfrHold_PresentPosition_NeverSelfCompletes()
    {
        var ac = MakeAircraft(altitude: 5000, onGround: false, ias: 200);
        var phase = new VfrHoldPhase { OrbitDirection = TurnDirection.Right };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        for (int i = 0; i < 500; i++)
        {
            Assert.False(phase.OnTick(ctx));
        }
    }

    [Theory]
    [InlineData(CanonicalCommandType.ClimbMaintain)]
    [InlineData(CanonicalCommandType.DescendMaintain)]
    [InlineData(CanonicalCommandType.Speed)]
    [InlineData(CanonicalCommandType.Mach)]
    public void VfrHold_PresentPosition_AllowsAltitudeAndSpeedCommands(CanonicalCommandType cmd)
    {
        var phase = new VfrHoldPhase { OrbitDirection = TurnDirection.Right };
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
    }

    [Theory]
    [InlineData(CanonicalCommandType.FlyHeading)]
    [InlineData(CanonicalCommandType.DirectTo)]
    public void VfrHold_PresentPosition_OtherCommands_ClearPhase(CanonicalCommandType cmd)
    {
        var phase = new VfrHoldPhase { OrbitDirection = TurnDirection.Right };
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(cmd));
    }

    [Fact]
    public void VfrHold_PresentPosition_Name_ReflectsDirection()
    {
        Assert.Equal("HPP-L", new VfrHoldPhase { OrbitDirection = TurnDirection.Left }.Name);
        Assert.Equal("HPP-R", new VfrHoldPhase { OrbitDirection = TurnDirection.Right }.Name);
        Assert.Equal("HPP", new VfrHoldPhase { OrbitDirection = null }.Name);
    }
}
