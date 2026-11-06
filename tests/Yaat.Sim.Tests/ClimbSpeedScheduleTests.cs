using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

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
            FlightPlan = new AircraftFlightPlan { Departure = "KTEST" },
        };
        ac.Targets.TargetAltitude = targetAlt;
        ac.Targets.TargetTrueHeading = new TrueHeading(90);
        return ac;
    }

    [Fact]
    public void InitialClimbPhase_SetsInitialClimbSpeed()
    {
        RunwayInfo runway = TestRunwayFactory.Make();
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Altitude = 500,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            FlightPlan = new AircraftFlightPlan { Departure = "KTEST" },
            Phases = new PhaseList { AssignedRunway = runway },
        };

        var phase = new InitialClimbPhase { AssignedAltitude = 35000 };
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
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
        RunwayInfo runway = TestRunwayFactory.Make();
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Altitude = 16000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            FlightPlan = new AircraftFlightPlan { Departure = "KTEST" },
            Phases = new PhaseList { AssignedRunway = runway },
        };

        var phase = new InitialClimbPhase { AssignedAltitude = 35000 };
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
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
        AircraftState ac = MakeClimbingJet(altitude: 12000, ias: 250, targetAlt: 35000);

        // No explicit speed command, climbing with null TargetSpeed
        FlightPhysics.Update(ac, 1.0);

        // At 12000ft (above 10k), jet default is 280 — auto-schedule should have set it
        Assert.NotNull(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void FlightPhysics_AutoSchedule_RespectedExplicitSpeedCommand()
    {
        AircraftState ac = MakeClimbingJet(altitude: 12000, ias: 210, targetAlt: 35000);
        ac.Targets.HasExplicitSpeedCommand = true;
        ac.Targets.TargetSpeed = null;

        FlightPhysics.Update(ac, 1.0);

        // HasExplicitSpeedCommand prevents auto-scheduling
        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void FlightPhysics_AutoSchedule_NoScheduleWhenLevelFlight()
    {
        AircraftState ac = MakeClimbingJet(altitude: 25000, ias: 280, targetAlt: 25000);

        // At target altitude — TargetAltitude will be nulled by UpdateAltitude
        FlightPhysics.Update(ac, 1.0);

        // Not climbing/descending, so no auto-schedule
        // TargetAltitude gets nulled when reached, so the condition won't fire
        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void FlightPhysics_AutoSchedule_Below10kCapped250()
    {
        AircraftState ac = MakeClimbingJet(altitude: 8000, ias: 200, targetAlt: 35000);

        FlightPhysics.Update(ac, 1.0);

        // Auto-schedule should set a target speed for B738 at 8000ft
        Assert.NotNull(ac.Targets.TargetSpeed);
        // 14 CFR 91.117 caps the actual IAS goal at 250 in UpdateSpeed, but the
        // type-aware TargetSpeed may be slightly above category default
        Assert.True(ac.IndicatedAirspeed <= 250, $"IAS {ac.IndicatedAirspeed} should be capped at 250 below 10k");
    }

    [Fact]
    public void HasExplicitSpeedCommand_KeptByAltitudeCommand()
    {
        AircraftState ac = MakeClimbingJet(altitude: 12000, ias: 250, targetAlt: 35000);
        ac.Targets.HasExplicitSpeedCommand = true;

        // Simulate CM command
        var cmd = new ClimbMaintainCommand(40000);
        CommandDispatcher.Dispatch(cmd, ac, TestDispatch.Context(new SerializableRandom(42)));

        // CM keeps an ATC speed (AIM 4-4-12.f)
        Assert.True(ac.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void HasExplicitSpeedCommand_SetBySpeedCommand()
    {
        AircraftState ac = MakeClimbingJet(altitude: 12000, ias: 250, targetAlt: 35000);

        var cmd = new SpeedCommand(210);
        CommandDispatcher.Dispatch(cmd, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.True(ac.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void FlightPhysics_AutoSchedule_SkippedDuringPatternPhase()
    {
        // Aircraft climbing from 500ft to 1000ft TPA with null TargetSpeed —
        // auto schedule should NOT fire because a pattern phase is active.
        RunwayInfo runway = TestRunwayFactory.Make();
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "C172",
            Altitude = 500,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            FlightPlan = new AircraftFlightPlan { Departure = "KTEST" },
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
        };
        ac.Targets.TargetAltitude = 1000;
        ac.Targets.TargetTrueHeading = new TrueHeading(280);
        ac.Targets.TargetSpeed = null;

        // Set up a PhaseList with an active PatternEntryPhase
        var phases = new PhaseList { AssignedRunway = runway };
        var entryPhase = new PatternEntryPhase
        {
            EntryLat = runway.ThresholdLatitude,
            EntryLon = runway.ThresholdLongitude,
            PatternAltitude = 1000,
            Kind = PatternEntryKind.Direct,
        };
        phases.Add(entryPhase);
        phases.Start(
            new PhaseContext
            {
                ActiveRunways = ActiveRunways.Empty,
                Aircraft = ac,
                Targets = ac.Targets,
                Category = AircraftCategory.Piston,
                DeltaSeconds = 1.0,
                Runway = runway,
                FieldElevation = 6,
                Logger = NullLogger.Instance,
            }
        );
        ac.Phases = phases;

        // Null out TargetSpeed as if UpdateSpeed cleared it after reaching pattern speed
        ac.Targets.TargetSpeed = null;

        FlightPhysics.Update(ac, 1.0);

        // Auto schedule should NOT have set a climb speed — pattern phase owns speed.
        // Without the fix, this would set DefaultSpeed (~250 kts).
        Assert.Null(ac.Targets.TargetSpeed);
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

    /// <summary>
    /// Above the IAS/Mach crossover the climb and descent schedules fly the profile's Mach, never a higher one
    /// (CAS is held to the crossover, then the Mach).
    /// </summary>
    [Fact]
    public void ClimbAndDescentSchedules_AboveTheCrossover_FlyTheProfileMach()
    {
        // The base profiles and the override-only types, each read through its effective profile.
        string data = Path.Combine(AppContext.BaseDirectory, "Data");
        SortedSet<string> types = new(
            AircraftProfileDatabase.LoadFromFile(Path.Combine(data, "AircraftProfiles.json")).Keys,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (AircraftProfileOverride ov in AircraftProfileDatabase.LoadOverridesFromFile(Path.Combine(data, "AircraftProfileOverrides.json")))
        {
            if (!string.IsNullOrWhiteSpace(ov.TypeCode))
            {
                types.Add(ov.TypeCode.Trim());
            }
        }

        List<string> offenders = [];
        foreach (string type in types)
        {
            if (AircraftProfileDatabase.Get(type) is { } p)
            {
                offenders.AddRange(MachOverruns(type, p, AircraftCategorization.Categorize(type)));
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders.Take(20)) + $"\n({offenders.Count} in all)");
    }

    private static IEnumerable<string> MachOverruns(string type, AircraftProfile p, AircraftCategory cat)
    {
        bool climbMach = IsMach(p.ClimbSpeedFinal);
        bool descentMach = IsMach(p.DescentSpeedInitial);
        for (double alt = 10_000; alt <= p.Ceiling; alt += 100)
        {
            // Every 100 ft from FL240 up, and every 1,000 ft below it: a crossover can sit below FL240.
            bool climbChecked = climbMach && ((alt >= 24_000) || (alt % 1_000 == 0));
            if (climbChecked && (MachPast(AircraftPerformance.ClimbSpeed(type, cat, alt), alt, p.ClimbSpeedFinal) is { } climb))
            {
                yield return $"{type} climbs at M{climb:F3} at {alt:F0} ft, past its final climb M{p.ClimbSpeedFinal:F2}";
            }

            if (descentMach && (MachPast(AircraftPerformance.DescentSpeed(type, cat, alt), alt, p.DescentSpeedInitial) is { } descent))
            {
                yield return $"{type} descends at M{descent:F3} at {alt:F0} ft, past its descent M{p.DescentSpeedInitial:F2}";
            }
        }
    }

    private static bool IsMach(double value) => value is > 0 and < 1.0;

    private static double? MachPast(double ias, double altitudeFt, double mach)
    {
        double flown = WindInterpolator.IasToMach(ias, altitudeFt);
        return (flown > mach + 0.001) ? flown : null;
    }

    /// <summary>
    /// The A320's "290/.78" schedule holds 290 KIAS between FL240 and the crossover in the climb, and between the
    /// crossover and 10,000 ft in the descent, then flies M.78 above the crossover (BADA 3 constant-CAS/constant-Mach).
    /// </summary>
    [Theory]
    [InlineData(26_000)]
    [InlineData(28_000)]
    public void A320Climb_BelowTheCrossover_HoldsItsCas(double altitudeFt) =>
        Assert.Equal(290, AircraftPerformance.ClimbSpeed("A320", AircraftCategory.Jet, altitudeFt), 1.0);

    [Theory]
    [InlineData(15_000)]
    [InlineData(25_000)]
    public void A320Descent_BelowTheCrossover_HoldsItsCas(double altitudeFt) =>
        Assert.Equal(290, AircraftPerformance.DescentSpeed("A320", AircraftCategory.Jet, altitudeFt), 1.0);

    /// <summary>
    /// Types whose crossover sits below FL240 fly their final climb Mach there, not the FL240 CAS.
    /// </summary>
    [Theory]
    [InlineData("C680")]
    [InlineData("E135")]
    [InlineData("E145")]
    public void Climb_CrossoverBelowFl240_FliesNoMachPastItsFinal(string type)
    {
        AircraftProfile? p = AircraftProfileDatabase.Get(type);
        Assert.True(p is not null, $"{type} has no profile");
        double ias = AircraftPerformance.ClimbSpeed(type, AircraftCategorization.Categorize(type), 23_000);
        Assert.True(WindInterpolator.IasToMach(ias, 23_000) <= p.ClimbSpeedFinal + 0.001);
    }

    [Fact]
    public void A320Climb_AboveTheCrossover_FliesItsFinalMach()
    {
        AircraftProfile? p = AircraftProfileDatabase.Get("A320");
        Assert.True(p is not null, "A320 has no profile");
        double mach = WindInterpolator.IasToMach(AircraftPerformance.ClimbSpeed("A320", AircraftCategory.Jet, 35_000), 35_000);
        Assert.Equal(p.ClimbSpeedFinal, mach, 0.005);
    }
}
