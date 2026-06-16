using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the stuck <c>NoLndgClnc</c> datablock flash after a manual go-around.
///
/// SWA2224 was on a visual final to OAK without a landing clearance, so the red
/// <c>NoLndgClnc</c> datablock flash armed (<see cref="FinalApproachPhase"/> writes
/// <c>AircraftState.NoLandingClearanceWarningActive = true</c> each tick). The controller
/// issued <c>GA</c> at t=1498; the phase chain rebuilt FinalApproach → GoAround, but the
/// warning flag stayed <c>true</c> — FinalApproachPhase is the only writer of the flag, and
/// once it stopped ticking nothing reset it, so the flash kept flashing indefinitely.
///
/// Fix: <see cref="FinalApproachPhase.OnEnd"/> clears the flag on every phase exit. This test
/// replays to just before the GA (flag latched), drives through the go-around, and asserts the
/// flash clears once the aircraft enters <see cref="GoAroundPhase"/>.
///
/// Recording: S2-OAK-5 Practical Exam Preparation/Advanced Concepts (trimmed to 1520s).
/// </summary>
public class GoAroundClearsNoLandingClearanceFlashTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/swa2224-ga-nolndgclnc-recording.yaat-bug-report-bundle.zip";

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

    [Fact]
    public void ManualGoAround_ClearsNoLandingClearanceFlash()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to just before the recorded GA command (t=1498). On a visual final to OAK with
        // no landing clearance the NoLndgClnc flash latch is armed (alt ~620 ft AGL, well above
        // the 200 ft AGL auto-go-around gate).
        engine.Replay(recording, 1497);

        var ac = engine.FindAircraft("SWA2224");
        Assert.NotNull(ac);
        Assert.True(
            ac.NoLandingClearanceWarningActive,
            $"precondition: NoLndgClnc flash should be armed on final without a landing clearance (alt={ac.Altitude:F0})"
        );

        // Drive through the go-around. The command carries a ~2 s pilot reaction delay, so the
        // phase rebuilds FinalApproach → GoAround around t=1500.
        GoAroundPhase? goAround = null;
        for (int t = 1; t <= 15; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("SWA2224");
            Assert.NotNull(ac);
            if (ac.Phases?.CurrentPhase is GoAroundPhase ga)
            {
                goAround = ga;
                output.WriteLine($"t+{t}: GoAround reached (alt={ac.Altitude:F0}, NoLndgClnc={ac.NoLandingClearanceWarningActive})");
                break;
            }
        }

        Assert.NotNull(goAround); // the GA command took effect
        Assert.False(ac!.NoLandingClearanceWarningActive, "NoLndgClnc flash must clear once the aircraft is sent around");
    }
}
