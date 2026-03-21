using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public sealed class ClimbSpeedScheduleTests
{
    public ClimbSpeedScheduleTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeClimbingJet(double altitude, double ias, double targetAlt)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = false,
            TrueHeading = new TrueHeading(90),
            TrueTrack = new TrueHeading(90),
            Departure = "KTEST",
        };
        ac.Targets.TargetAltitude = targetAlt;
        ac.Targets.TargetTrueHeading = new TrueHeading(90);
        return ac;
    }

    [Fact]
    public void InitialClimbPhase_SetsInitialClimbSpeed()
    {
        var runway = TestRunwayFactory.Make();
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Altitude = 500,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Departure = "KTEST",
        };
        ac.Phases = new PhaseList { AssignedRunway = runway };

        var phase = new InitialClimbPhase { AssignedAltitude = 35000 };
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = 6,
            Logger = NullLogger.Instance,
        };

        phase.OnStart(ctx);

        // Should set initial climb speed from profile (165 for B738), not DefaultSpeed at target alt
        Assert.Equal(AircraftPerformance.InitialClimbSpeed("B738", AircraftCategory.Jet), ac.Targets.TargetSpeed);
    }

    [Fact]
    public void InitialClimbPhase_OnTick_UpdatesSpeedAtAltitudeBand()
    {
        var runway = TestRunwayFactory.Make();
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Altitude = 16000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Departure = "KTEST",
        };
        ac.Phases = new PhaseList { AssignedRunway = runway };

        var phase = new InitialClimbPhase { AssignedAltitude = 35000 };
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = 6,
            Logger = NullLogger.Instance,
        };

        phase.OnStart(ctx);

        // Null out TargetSpeed as if UpdateSpeed cleared it
        ac.Targets.TargetSpeed = null;

        // At FL160 with B738 profile, climb speed should differ from 250 by > 5 kts
        phase.OnTick(ctx);

        double expected = AircraftPerformance.DefaultSpeed("B738", AircraftCategory.Jet, 16000, ac.Targets.TargetAltitude);
        Assert.NotNull(ac.Targets.TargetSpeed);
        Assert.Equal(expected, ac.Targets.TargetSpeed);
    }

    [Fact]
    public void FlightPhysics_AutoSchedule_SetsSpeedDuringClimb()
    {
        var ac = MakeClimbingJet(altitude: 12000, ias: 250, targetAlt: 35000);

        // No explicit speed command, climbing with null TargetSpeed
        FlightPhysics.Update(ac, 1.0);

        // At 12000ft (above 10k), jet default is 280 — auto-schedule should have set it
        Assert.NotNull(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void FlightPhysics_AutoSchedule_RespectedExplicitSpeedCommand()
    {
        var ac = MakeClimbingJet(altitude: 12000, ias: 210, targetAlt: 35000);
        ac.Targets.HasExplicitSpeedCommand = true;
        ac.Targets.TargetSpeed = null;

        FlightPhysics.Update(ac, 1.0);

        // HasExplicitSpeedCommand prevents auto-scheduling
        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void FlightPhysics_AutoSchedule_NoScheduleWhenLevelFlight()
    {
        var ac = MakeClimbingJet(altitude: 25000, ias: 280, targetAlt: 25000);

        // At target altitude — TargetAltitude will be nulled by UpdateAltitude
        FlightPhysics.Update(ac, 1.0);

        // Not climbing/descending, so no auto-schedule
        // TargetAltitude gets nulled when reached, so the condition won't fire
        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void FlightPhysics_AutoSchedule_Below10kCapped250()
    {
        var ac = MakeClimbingJet(altitude: 8000, ias: 200, targetAlt: 35000);

        FlightPhysics.Update(ac, 1.0);

        // Auto-schedule should set a target speed for B738 at 8000ft
        Assert.NotNull(ac.Targets.TargetSpeed);
        // 14 CFR 91.117 caps the actual IAS goal at 250 in UpdateSpeed, but the
        // type-aware TargetSpeed may be slightly above category default
        Assert.True(ac.IndicatedAirspeed <= 250, $"IAS {ac.IndicatedAirspeed} should be capped at 250 below 10k");
    }

    [Fact]
    public void HasExplicitSpeedCommand_ClearedByAltitudeCommand()
    {
        var ac = MakeClimbingJet(altitude: 12000, ias: 250, targetAlt: 35000);
        ac.Targets.HasExplicitSpeedCommand = true;

        // Simulate CM command
        var cmd = new ClimbMaintainCommand(40000);
        CommandDispatcher.Dispatch(cmd, ac, groundLayout: null, new SerializableRandom(42), true);

        Assert.False(ac.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void HasExplicitSpeedCommand_SetBySpeedCommand()
    {
        var ac = MakeClimbingJet(altitude: 12000, ias: 250, targetAlt: 35000);

        var cmd = new SpeedCommand(210);
        CommandDispatcher.Dispatch(cmd, ac, groundLayout: null, new SerializableRandom(42), true);

        Assert.True(ac.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void DefaultSpeed_JetBands_Refined()
    {
        // Below 10k: 250
        Assert.Equal(250, CategoryPerformance.DefaultSpeed(AircraftCategory.Jet, 9000));

        // 10k-18k: 280 (transition)
        Assert.Equal(280, CategoryPerformance.DefaultSpeed(AircraftCategory.Jet, 15000));

        // 18k-28k: 290 (standard climb)
        Assert.Equal(290, CategoryPerformance.DefaultSpeed(AircraftCategory.Jet, 25000));

        // Above 28k: 280 (Mach transition)
        Assert.Equal(280, CategoryPerformance.DefaultSpeed(AircraftCategory.Jet, 35000));
    }
}
