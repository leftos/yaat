using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for go-around landing-intent preservation.
///
/// Rule: a go-around (manual GA or auto-GA from no clearance / unstable / too-high)
/// must not change what the aircraft was trying to do. A pattern aircraft cleared
/// touch-and-go keeps cycling touch-and-goes; an aircraft attempting a full-stop
/// landing (CLAND or just on visual approach via ERB/ELB/ERD/etc.) keeps trying to
/// land. Without this, GoAroundHelper auto-promoted every VFR aircraft into pattern
/// mode and PhaseRunner unconditionally appended TouchAndGoPhase circuits.
///
/// Recording: S2-OAK-4 VFR Transitions/Radar Concepts.
/// </summary>
public class GoAroundPreservesIntentE2ETests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/66fd6538542e.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// N342T was cleared TG at t=717, completed the first TG, auto-cycled into a
    /// second pattern circuit, and the user issued a manual GA at t=869. Pre-GA
    /// terminator was TouchAndGoPhase. The next auto-cycled circuit (after the
    /// GoAround completes) must still end in TouchAndGoPhase — TG intent preserved.
    /// </summary>
    [Fact]
    public void N342T_AfterManualGoAroundFromTouchAndGo_NextCircuitEndsWithTouchAndGoPhase()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 871);

        var ac = engine.FindAircraft("N342T");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);

        var ga = Assert.IsType<GoAroundPhase>(ac.Phases.CurrentPhase);
        output.WriteLine(
            $"t=871: GoAroundPhase active. NextLandingFullStop={ga.NextLandingFullStop} "
                + $"ReenterPattern={ga.ReenterPattern} TargetAlt={ga.TargetAltitude}"
        );

        Assert.False(ga.NextLandingFullStop, "GoAroundPhase should have captured the pre-GA TG intent (TouchAndGoPhase was pending)");
        Assert.True(ga.ReenterPattern, "Pattern aircraft should re-enter pattern after GA");

        Phase? terminator = null;
        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N342T");
            if (ac is null)
            {
                output.WriteLine($"t+{t}: aircraft deleted");
                break;
            }

            terminator = ac.Phases?.Phases.LastOrDefault(p =>
                p is LandingPhase or HelicopterLandingPhase or TouchAndGoPhase or StopAndGoPhase or LowApproachPhase
            );

            if (terminator is not null && ac.Phases?.CurrentPhase is not GoAroundPhase)
            {
                output.WriteLine($"t+{t}: GoAround complete, next terminator = {terminator.GetType().Name}");
                break;
            }
        }

        Assert.NotNull(terminator);
        Assert.IsType<TouchAndGoPhase>(terminator);
    }

    /// <summary>
    /// N436MS in the recording was a VFR aircraft on visual approach via ERB 28R
    /// with no CLAND yet — pre-GA terminator was LandingPhase. The recording's
    /// live session had this aircraft auto-go-around at t≈815 ("no landing
    /// clearance"); in the deterministic replay the aircraft is established on
    /// FinalApproach by t≈800 with LandingPhase pending. Inject a manual GA
    /// inside that window to exercise the same auto-cycle path with the
    /// aircraft's full-stop intent. After the fix, the next auto-cycled circuit
    /// must end in LandingPhase.
    /// </summary>
    [Fact]
    public void N436MS_GoAroundFromVisualApproach_NextCircuitEndsWithLandingPhase()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 850);

        var ac = engine.FindAircraft("N436MS");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);
        var preGaTerminator = ac.Phases.Phases.LastOrDefault(p =>
            p is LandingPhase or HelicopterLandingPhase or TouchAndGoPhase or StopAndGoPhase or LowApproachPhase
        );
        Assert.IsType<LandingPhase>(preGaTerminator);

        var result = engine.SendCommand("N436MS", "GA");
        Assert.True(result.Success, $"GA command should succeed: {result.Message}");

        ac = engine.FindAircraft("N436MS");
        Assert.NotNull(ac);
        var ga = Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
        output.WriteLine(
            $"GA injected at t=850. GoAroundPhase: NextLandingFullStop={ga.NextLandingFullStop} "
                + $"ReenterPattern={ga.ReenterPattern} TargetAlt={ga.TargetAltitude}"
        );

        Assert.True(ga.NextLandingFullStop, "GoAroundPhase should have captured the pre-GA full-stop intent (LandingPhase was pending)");
        Assert.True(ga.ReenterPattern, "VFR aircraft on visual approach should re-enter pattern after GA");

        Phase? terminator = null;
        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N436MS");
            if (ac is null)
            {
                output.WriteLine($"t+{t}: aircraft deleted");
                break;
            }

            terminator = ac.Phases?.Phases.LastOrDefault(p =>
                p is LandingPhase or HelicopterLandingPhase or TouchAndGoPhase or StopAndGoPhase or LowApproachPhase
            );

            if (terminator is not null && ac.Phases?.CurrentPhase is not GoAroundPhase)
            {
                output.WriteLine($"t+{t}: GoAround complete, next terminator = {terminator.GetType().Name}");
                break;
            }
        }

        Assert.NotNull(terminator);
        Assert.IsType<LandingPhase>(terminator);
    }
}
