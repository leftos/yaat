using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #1357 (YAAT-1330): an aircraft cleared <c>CTO MLT 28L</c> from 28R is judged on 28R for the rest
/// of its pattern work.
///
/// Recording: "S2-OAK-3 (2) | VFR Sequencing" at OAK. At t=472 N342T, lined up on 28R, is cleared <c>CTO MLT 28L</c>: the clearance
/// sets its assigned runway to 28L and its departure runway to 28R, and the departure runway is never cleared. Every occupancy
/// judge read <c>DepartureRunway ?? AssignedRunway</c> whatever the phase, so N342T's later touch-and-goes on 28L counted as
/// occupying 28R: N172SP's 28R takeoff clearance at t=857 drew "traffic on runway 28R (N342T)", and at t=1089 N80ZU, cleared to
/// land 28R, went around for "N342T on 28R (landing rollout, …)".
///
/// The fix judges a phase-driven aircraft against the runway its current phase runs on: the departure runway only while it is
/// lining up, taking off or rejecting a takeoff, the assigned runway otherwise. Full replay from t=0: the fix changes no behaviour
/// before the first misjudgement.
/// </summary>
public class Issue1357CrossRunwayTouchAndGoOccupancyTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue1357-oak-cto-mlt-28l-occupancy-recording.zip";

    // Inside N342T's 28R takeoff roll after its CTO MLT 28L at t=472: the recording shows it on the ground at 56 kt, lifting off by t=500.
    private const int N342TTakeoffRollTime = 495;

    // N172SP's recorded CTO MRT on 28R (client log 19:50:05).
    private const int N172SpTakeoffClearanceTime = 857;

    // N80ZU's recorded go-around for N342T (client log 20:00:00).
    private const int GoAroundTime = 1089;

    // 30 s past the go-around (20:00:30); the recording itself ends at t=1100.
    private const int GoAroundWindowEnd = 1119;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(
            RecordingGroundData.Evergreen("which runway a pattern aircraft occupies must hold on today's OAK"),
            PushPlanStore.CreateShipped()
        );
    }

    private static RunwayInfo Oak(string designator) =>
        NavigationDatabase.Instance.GetRunway("OAK", designator) ?? throw new InvalidOperationException($"OAK {designator} missing from navdata");

    [Fact]
    public void N342T_TouchAndGoOn28L_IsARunwayUserOf28LNot28R()
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if ((recording is null) || (engine is null))
        {
            return;
        }

        engine.Replay(recording, GoAroundTime);
        AircraftState? n342t = engine.FindAircraft("N342T");
        Assert.NotNull(n342t);
        output.WriteLine(
            $"t={GoAroundTime}: phase={n342t.Phases?.CurrentPhase?.GetType().Name} onGround={n342t.IsOnGround} "
                + $"assigned={n342t.Phases?.AssignedRunway?.Designator} departure={n342t.Phases?.DepartureRunway?.Designator}"
        );

        // The recording shows N342T in TouchAndGoPhase at t=1089, already lifted off: a touch-and-go is a departure once
        // airborne (7110.65 3-8-2), so the phase classifier calls it Departing — on 28L, not 28R.
        Assert.False(n342t.IsOnGround);
        Assert.Equal(RunwayUseKind.Departing, RunwayOccupancy.ClassifyByPhase(n342t, Oak("28L")));
        Assert.Null(RunwayOccupancy.ClassifyByPhase(n342t, Oak("28R")));
    }

    [Fact]
    public void N80ZU_LandingOn28R_DoesNotGoAroundForN342TOn28L()
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if ((recording is null) || (engine is null))
        {
            return;
        }

        List<string> goArounds = [];
        engine.WarningEmitted += (callsign, warning) =>
        {
            if ((callsign == "N80ZU") && warning.Contains("go-around: N342T on 28R", StringComparison.Ordinal))
            {
                goArounds.Add(warning);
            }
        };

        engine.Replay(recording, GoAroundTime - 10);
        bool landedOn28R = false;
        for (int t = GoAroundTime - 10; t < GoAroundWindowEnd; t++)
        {
            engine.ReplayOneSecond();
            AircraftState? n80zu = engine.FindAircraft("N80ZU");
            landedOn28R |=
                (n80zu?.Phases is { CurrentPhase: LandingPhase or RunwayExitPhase, AssignedRunway: { } assigned }) && assigned.Id.Contains("28R");
        }

        Assert.Empty(goArounds);
        Assert.True(landedOn28R, "N80ZU should reach its 28R landing or exit inside the window");
    }

    [Fact]
    public void N172SP_TakeoffClearanceOn28R_DrawsNoTrafficWarningForN342T()
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if ((recording is null) || (engine is null))
        {
            return;
        }

        List<string> warnings = [];
        engine.WarningEmitted += (callsign, warning) =>
        {
            if (callsign == "N172SP")
            {
                warnings.Add(warning);
            }
        };

        engine.Replay(recording, N172SpTakeoffClearanceTime - 1);
        engine.ReplayOneSecond();
        engine.ReplayOneSecond();

        AircraftState? n342t = engine.FindAircraft("N342T");
        Assert.NotNull(n342t);
        output.WriteLine($"t={N172SpTakeoffClearanceTime + 1}: N342T phase={n342t.Phases?.CurrentPhase?.GetType().Name}");
        foreach (string warning in warnings)
        {
            output.WriteLine($"N172SP warning: {warning}");
        }

        AircraftState? n172sp = engine.FindAircraft("N172SP");
        Assert.NotNull(n172sp);
        Assert.True(
            n172sp.Phases?.CurrentPhase is LineUpPhase or TakeoffPhase,
            $"N172SP should be lining up or taking off at t={N172SpTakeoffClearanceTime + 1}, was {n172sp.Phases?.CurrentPhase?.GetType().Name}"
        );
        Assert.DoesNotContain(
            warnings,
            w => w.Contains("traffic on runway 28R", StringComparison.Ordinal) && w.Contains("N342T", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void N342T_TakeoffRollOn28R_IsDepartingOn28RNot28L()
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if ((recording is null) || (engine is null))
        {
            return;
        }

        engine.Replay(recording, N342TTakeoffRollTime);
        AircraftState? n342t = engine.FindAircraft("N342T");
        Assert.NotNull(n342t);
        output.WriteLine(
            $"t={N342TTakeoffRollTime}: phase={n342t.Phases?.CurrentPhase?.GetType().Name} "
                + $"assigned={n342t.Phases?.AssignedRunway?.Designator} departure={n342t.Phases?.DepartureRunway?.Designator}"
        );

        Assert.Equal(RunwayUseKind.Departing, RunwayOccupancy.ClassifyByPhase(n342t, Oak("28R")));
        Assert.Null(RunwayOccupancy.ClassifyByPhase(n342t, Oak("28L")));
    }
}
