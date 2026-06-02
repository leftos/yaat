using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tight spacing/delay maneuvers (L360/R360, L270/R270, VFR hold/HPP, S-turns) must be
/// flown at a slow holding speed instead of the aircraft's current/cruise speed, and the
/// aircraft must resume normal speed when the maneuver completes (restoring a prior
/// explicit speed assignment if one existed, otherwise releasing to the schedule).
///
/// Recording: "S2-OAK-5 | Practical Exam Preparation/Advanced Concepts" (ZOA/OAK). N44444
/// (C172, holding speed 82 KIAS) is given L360 at t=1376 while flying 94.7 KIAS and orbits
/// the entire 360 at that speed with no deceleration — the bug this exercises.
/// </summary>
public class ManeuverSlowSpeedTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/l360-maneuver-speed-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N44444";
    private const int L360DispatchTime = 1376;

    // ---- Recording E2E: real-scenario slow-down + resume ------------------------------

    [Fact]
    public void Recording_N44444_L360_SlowsToHoldingSpeed_ThenResumes()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, L360DispatchTime - 1);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        double startIas = ac.IndicatedAirspeed;
        double startHeading = ac.TrueHeading.Degrees;
        double maxHold = AircraftPerformance.HoldingSpeed(ac.AircraftType, ac.Altitude);
        output.WriteLine($"t={L360DispatchTime - 1}: {Callsign} type={ac.AircraftType} ias={startIas:F1} hold={maxHold:F0} hdg={startHeading:F1}");

        // The recording must exercise a real slow-down (current speed above holding speed).
        Assert.True(startIas > maxHold + 5, $"Expected start IAS ({startIas:F1}) above holding speed ({maxHold:F0})");

        var dispatch = engine.SendCommand(Callsign, "L360");
        Assert.True(dispatch.Success, $"L360 dispatch should succeed: {dispatch.Message}");

        double slowestIas = startIas;
        bool phaseCompleted = false;

        for (int t = 1; t <= 180; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            slowestIas = System.Math.Min(slowestIas, ac.IndicatedAirspeed);

            if (ac.Phases?.CurrentPhase is not MakeTurnPhase && t > 20)
            {
                phaseCompleted = true;
            }

            if ((t % 15 == 0) || (t == 180))
            {
                output.WriteLine(
                    $"  t+{t, 3}: ias={ac.IndicatedAirspeed, 6:F1} hdg={ac.TrueHeading.Degrees, 6:F1} phase={ac.Phases?.CurrentPhase?.Name ?? "(none)"}"
                );
            }
        }

        // Slowed to ~holding speed during the orbit (was pinned at startIas before the fix).
        Assert.True(
            slowestIas <= maxHold + 3,
            $"Aircraft should slow to ~holding speed ({maxHold:F0}) during the 360, but slowest IAS was {slowestIas:F1}"
        );

        // Resumed normal speed after completion (no prior explicit speed -> schedule/cruise).
        Assert.True(phaseCompleted, "360 should complete within 180 s");
        double iasAfterComplete = ac!.IndicatedAirspeed;
        Assert.True(
            iasAfterComplete > maxHold + 5,
            $"After the 360, the aircraft should resume normal speed (> {maxHold + 5:F0}), but was {iasAfterComplete:F1}"
        );
    }

    // ---- Phase-level unit tests: deceleration + resume semantics -----------------------

    [Fact]
    public void MakeTurn_Jet_SlowsToHoldingSpeed_OnStart()
    {
        var ac = MakeAircraft("B738", altitude: 5000, ias: 250);
        var ctx = CommandDispatcher.BuildMinimalContext(ac);
        double maxHold = AircraftPerformance.HoldingSpeed("B738", 5000);

        var phase = new MakeTurnPhase { Direction = TurnDirection.Right, TargetDegrees = 360 };
        phase.OnStart(ctx);

        output.WriteLine($"hold={maxHold:F0} target={ac.Targets.TargetSpeed}");
        Assert.NotNull(ac.Targets.TargetSpeed);
        Assert.Equal(maxHold, ac.Targets.TargetSpeed!.Value, 1);
    }

    [Fact]
    public void MakeTurn_NoPriorSpeed_ResumesScheduleOnEnd()
    {
        var ac = MakeAircraft("B738", altitude: 5000, ias: 250);
        var ctx = CommandDispatcher.BuildMinimalContext(ac);

        var phase = new MakeTurnPhase { Direction = TurnDirection.Right, TargetDegrees = 360 };
        phase.OnStart(ctx);
        double maxHold = AircraftPerformance.HoldingSpeed("B738", 5000);
        Assert.Equal(maxHold, ac.Targets.TargetSpeed!.Value, 1);

        phase.OnEnd(ctx, PhaseStatus.Completed);

        double scheduled = AircraftPerformance.DefaultSpeed("B738", ctx.Category, 5000, null);
        output.WriteLine($"resume target={ac.Targets.TargetSpeed} scheduled={scheduled:F0}");
        Assert.NotNull(ac.Targets.TargetSpeed);
        Assert.Equal(scheduled, ac.Targets.TargetSpeed!.Value, 1);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void MakeTurn_WithPriorAssignedSpeed_RestoresItOnEnd()
    {
        var ac = MakeAircraft("B738", altitude: 5000, ias: 250);
        ac.Targets.TargetSpeed = 230;
        ac.Targets.AssignedSpeed = 230;
        ac.Targets.HasExplicitSpeedCommand = true;
        var ctx = CommandDispatcher.BuildMinimalContext(ac);

        var phase = new MakeTurnPhase { Direction = TurnDirection.Left, TargetDegrees = 360 };
        phase.OnStart(ctx);
        double maxHold = AircraftPerformance.HoldingSpeed("B738", 5000);
        Assert.Equal(maxHold, ac.Targets.TargetSpeed!.Value, 1); // slowed for the turn

        phase.OnEnd(ctx, PhaseStatus.Completed);

        output.WriteLine($"restored target={ac.Targets.TargetSpeed}");
        Assert.Equal(230, ac.Targets.TargetSpeed!.Value, 1);
        Assert.True(ac.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void MakeTurn_AlreadySlowerThanHolding_NotSpedUp_NorResumed()
    {
        // C172 holding speed is 82; flying 78 (below holding) must be left untouched.
        var ac = MakeAircraft("C172", altitude: 2000, ias: 78);
        var ctx = CommandDispatcher.BuildMinimalContext(ac);

        var phase = new MakeTurnPhase { Direction = TurnDirection.Left, TargetDegrees = 360 };
        phase.OnStart(ctx);
        Assert.Null(ac.Targets.TargetSpeed);

        phase.OnEnd(ctx, PhaseStatus.Completed);
        Assert.Null(ac.Targets.TargetSpeed);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void MakeTurn_MidManeuverSpeedCommand_NotClobberedOnEnd()
    {
        var ac = MakeAircraft("B738", altitude: 5000, ias: 250);
        var ctx = CommandDispatcher.BuildMinimalContext(ac);

        var phase = new MakeTurnPhase { Direction = TurnDirection.Right, TargetDegrees = 360 };
        phase.OnStart(ctx);

        // Controller takes over speed mid-turn.
        phase.OnCommandAccepted(CanonicalCommandType.Speed, ctx);
        ac.Targets.TargetSpeed = 180;
        ac.Targets.AssignedSpeed = 180;
        ac.Targets.HasExplicitSpeedCommand = true;

        phase.OnEnd(ctx, PhaseStatus.Completed);

        output.WriteLine($"after end target={ac.Targets.TargetSpeed}");
        Assert.Equal(180, ac.Targets.TargetSpeed!.Value, 1);
    }

    [Fact]
    public void VfrHold_HoldPresentPosition_SlowsToHoldingSpeed()
    {
        var ac = MakeAircraft("B738", altitude: 5000, ias: 250);
        var ctx = CommandDispatcher.BuildMinimalContext(ac);
        double maxHold = AircraftPerformance.HoldingSpeed("B738", 5000);

        var phase = new VfrHoldPhase { OrbitDirection = TurnDirection.Left };
        phase.OnStart(ctx);

        output.WriteLine($"hold={maxHold:F0} target={ac.Targets.TargetSpeed}");
        Assert.NotNull(ac.Targets.TargetSpeed);
        Assert.Equal(maxHold, ac.Targets.TargetSpeed!.Value, 1);
    }

    [Fact]
    public void STurn_InsideFinal_DoesNotSlow()
    {
        // 7110.65 §5-7-1.b.4: no speed adjustment inside the FAF / 5 nm final. A fast jet
        // S-turned close-in must NOT be slowed (the never-speed-up guard alone is not enough).
        var runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.73,
            endLon: -122.27,
            heading: 280,
            elevationFt: 9
        );
        var ac = MakeAircraft("B738", altitude: 2000, ias: 250);
        ac.Targets.TargetSpeed = 250;
        ac.Phases = new PhaseList { AssignedRunway = runway }; // aircraft is at the threshold (0 nm)
        var ctx = CommandDispatcher.BuildMinimalContext(ac);

        var phase = new STurnPhase { InitialDirection = TurnDirection.Left };
        phase.OnStart(ctx);

        output.WriteLine($"inside-final target={ac.Targets.TargetSpeed}");
        Assert.Equal(250, ac.Targets.TargetSpeed!.Value, 1);
    }

    [Fact]
    public void STurn_OutsideFinal_SlowsToHoldingSpeed()
    {
        // Outside the FAF / 5 nm, S-turns are a legal place to slow to holding speed.
        var runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.50, // ~13 nm south of the aircraft
            thresholdLon: -122.22,
            endLat: 37.49,
            endLon: -122.27,
            heading: 280,
            elevationFt: 9
        );
        var ac = MakeAircraft("B738", altitude: 5000, ias: 250);
        ac.Phases = new PhaseList { AssignedRunway = runway };
        var ctx = CommandDispatcher.BuildMinimalContext(ac);
        double maxHold = AircraftPerformance.HoldingSpeed("B738", 5000);

        var phase = new STurnPhase { InitialDirection = TurnDirection.Left };
        phase.OnStart(ctx);

        output.WriteLine($"outside-final target={ac.Targets.TargetSpeed} hold={maxHold:F0}");
        Assert.NotNull(ac.Targets.TargetSpeed);
        Assert.Equal(maxHold, ac.Targets.TargetSpeed!.Value, 1);
    }

    // ---- Alias resolution -------------------------------------------------------------

    [Theory]
    [InlineData("ML3", CanonicalCommandType.MakeLeft360)]
    [InlineData("ML360", CanonicalCommandType.MakeLeft360)]
    [InlineData("ml3", CanonicalCommandType.MakeLeft360)]
    [InlineData("MR3", CanonicalCommandType.MakeRight360)]
    [InlineData("MR360", CanonicalCommandType.MakeRight360)]
    [InlineData("mr360", CanonicalCommandType.MakeRight360)]
    public void Aliases_Resolve_ToMakeTurn(string alias, CanonicalCommandType expected)
    {
        Assert.True(CommandRegistry.AliasToCanonicType.TryGetValue(alias, out var type), $"Alias {alias} should resolve");
        Assert.Equal(expected, type);
    }

    // ---- Helpers ----------------------------------------------------------------------

    private static AircraftState MakeAircraft(string type, double altitude, double ias)
    {
        TestVnasData.EnsureInitialized();
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = type,
            Position = new LatLon(37.72, -122.22),
            TrueHeading = new TrueHeading(090),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", FlightRules = "VFR" },
        };
    }

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("MakeTurnPhase", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }
}
