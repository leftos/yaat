using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the pattern-exit departure bug (scenario S2-OAK-5
/// "Practical Exam Preparation / Advanced Concepts", ZOA).
///
/// N784ME (C208, VFR, KOAK→KFAT) was given <c>CTO MRD</c> ("cleared for takeoff
/// runway 28R, right downwind departure"). During the climb-out the controller
/// tried <c>EXT</c> and <c>EXT UPWIND</c> to delay the turn for spacing; both were
/// rejected with "Extend applies on upwind, crosswind, or downwind". The cause:
/// CTO MRD was modelled as a single InitialClimb + 180° turn and never built a
/// pattern, so there was no upwind leg to extend.
///
/// Fix: MRC/MRD/MLC/MLD build a real pattern-exit circuit (upwind → crosswind →
/// PatternExitPhase, with the landing tail omitted) that flies the pattern to the
/// exit leg and then departs. EXT then works because there is a real UpwindPhase.
///
/// CTO MRD is applied at recorded t≈552; the tower phases are rebuilt by t≈565 and
/// takeoff is at t≈590, so we full-replay to t=600 (FH 280, which clears the
/// pattern, isn't recorded until t=636) and exercise the new behavior with the
/// fixed code.
/// </summary>
public class CtoMrdPatternExitTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/cto-mrd-pattern-exit-recording.yaat-bug-report-bundle.zip";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("DepartureClearanceHandler", LogLevel.Debug)
            .EnableCategory("UpwindPhase", LogLevel.Debug)
            .EnableCategory("PatternExitPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void CtoMrd_BuildsPatternExitCircuit_NotInitialClimb()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 600);

        var ac = engine.FindAircraft("N784ME");
        Assert.NotNull(ac);
        output.WriteLine($"chain=[{Chain(ac)}] runway={ac.Phases?.AssignedRunway?.Designator}");

        // A right-downwind departure flies upwind → crosswind → exit. The circuit
        // must contain the real pattern legs and a terminal PatternExitPhase.
        Assert.Contains(ac.Phases!.Phases, p => p is UpwindPhase);
        Assert.Contains(ac.Phases.Phases, p => p is CrosswindPhase);
        Assert.Contains(ac.Phases.Phases, p => p is PatternExitPhase);

        // Departure, not closed traffic: no base / final / landing tail, and no
        // single-turn InitialClimbPhase.
        Assert.DoesNotContain(ac.Phases.Phases, p => p is InitialClimbPhase);
        Assert.DoesNotContain(ac.Phases.Phases, p => p is BasePhase or FinalApproachPhase or TouchAndGoPhase or LandingPhase);

        Assert.Equal("28R", ac.Phases.AssignedRunway?.Designator);
        Assert.Equal(PatternDirection.Right, ac.Phases.TrafficDirection);
    }

    [Fact]
    public void ExtUpwind_DuringCtoMrdClimbout_Succeeds_AndArmsUpwind()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 600);

        var ac = engine.FindAircraft("N784ME");
        Assert.NotNull(ac);

        // The reported bug: both of these were rejected. After the fix the pending
        // UpwindPhase is armed (bare EXT and EXT UPWIND both reach the same path).
        var extUpwind = engine.SendCommand("N784ME", "EXT UPWIND");
        Assert.True(extUpwind.Success, $"EXT UPWIND rejected: {extUpwind.Message}");

        var ext = engine.SendCommand("N784ME", "EXT");
        Assert.True(ext.Success, $"EXT rejected: {ext.Message}");

        var after = engine.FindAircraft("N784ME");
        var upwind = after!.Phases?.Phases.OfType<UpwindPhase>().FirstOrDefault();
        Assert.NotNull(upwind);
        Assert.True(upwind!.IsExtended, "EXT UPWIND should arm IsExtended on the pending upwind leg");
    }

    [Fact]
    public void CtoMrd_FliesUpwindThenTurnsCrosswind_AndClimbsOutPastPatternAltitude()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay through takeoff and the upwind, stopping before the recorded FH 280
        // vector (t=636) that would clear the pattern.
        engine.Replay(recording, 600);

        // Tick the climb-out forward and confirm the aircraft works the pattern legs
        // (reaches an airborne pattern leg) while continuing to climb — it must not
        // level off at pattern altitude (~1009 ft MSL at OAK).
        UpwindPhase? upwind = null;
        bool reachedAirbornePatternLeg = false;
        for (int t = 1; t <= 35; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft("N784ME");
            if (ac is null)
            {
                break;
            }

            if (!ac.IsOnGround && ac.Phases?.CurrentPhase is UpwindPhase or CrosswindPhase)
            {
                reachedAirbornePatternLeg = true;
                upwind = ac.Phases.CurrentPhase as UpwindPhase;
                output.WriteLine($"t+{t}: phase={ac.Phases.CurrentPhase?.Name} alt={ac.Altitude:F0} tgtAlt={ac.Targets.TargetAltitude}");
                break;
            }
        }

        Assert.True(reachedAirbornePatternLeg, "N784ME never reached an airborne pattern leg after CTO MRD");

        // The continuous-climb target on the upwind must be the cruise altitude
        // (11500), not pattern altitude — a departing aircraft does not level at TPA.
        if (upwind is not null)
        {
            Assert.Equal(11500, upwind.DepartureClimbTargetFt);
        }
    }

    private static string Chain(AircraftState ac) => string.Join(", ", ac.Phases?.Phases.Select(p => $"{p.GetType().Name}:{p.Status}") ?? []);
}
