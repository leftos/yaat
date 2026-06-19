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
/// E2E test for GitHub issue #208: "Aircraft unable to turn early crosswind while on upwind."
///
/// Recording: S2-OAK-5 (1) "Practical Exam Preparation / Advanced Concepts" (ZOA). N157LE
/// (GA, KOAK) was given <c>CTO MRC</c> ("cleared for takeoff runway 28R, right crosswind
/// departure"). During the initial climb the controller issued <c>TC</c> (turn crosswind) to
/// turn it crosswind earlier than normal, but it was rejected with "Not on the leg before
/// crosswind" — the aircraft was still in <see cref="TakeoffPhase"/> (which ends at 400 ft AGL),
/// not yet on the Upwind leg.
///
/// Fix: a <c>TC</c> issued during <see cref="TakeoffPhase"/> arms the pending <see cref="UpwindPhase"/>
/// (<see cref="UpwindPhase.TurnCrosswindArmed"/>), so the aircraft turns crosswind the instant it
/// reaches the upwind leg (~400 ft AGL, the safe-turn floor) instead of being rejected.
///
/// Strategy: hybrid replay (the recording is ~40 min; the fix is localized to the TC handler and
/// the post-Takeoff UpwindPhase tick). Restore the snapshot at t=2310 (N157LE airborne in
/// TakeoffPhase, ~186 ft AGL, pending Upwind → PatternExit), send TC, and tick through 400 ft AGL.
/// OAK field elevation is 9 ft, so 400 ft AGL ≈ 409 ft MSL; the normal auto crosswind-turn floor
/// (pattern altitude − 300 ≈ 709 ft MSL) is well above that, so reaching PatternExit below ~700 ft
/// MSL proves the turn happened early.
/// </summary>
public class Issue208EarlyCrosswindTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue208-early-crosswind-recording.yaat-bug-report-bundle.zip";
    private const int RestoreAtSeconds = 2310;

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
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .EnableCategory("UpwindPhase", LogLevel.Debug)
            .EnableCategory("PatternExitPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void TC_DuringInitialClimb_TurnsCrosswindEarly_NotRejected()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(recording, 0); // load scenario + weather + ARTCC config
            var snapshot = archive.ReadSnapshotAt(RestoreAtSeconds);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            // Precondition: N157LE airborne in the initial climb with a pending Upwind leg —
            // exactly the state where TC was rejected before the fix.
            var ac = engine.FindAircraft("N157LE");
            Assert.NotNull(ac);
            Assert.IsType<TakeoffPhase>(ac!.Phases!.CurrentPhase);
            Assert.False(ac.IsOnGround);
            Assert.Contains(ac.Phases.Phases, p => p is UpwindPhase);
            output.WriteLine($"restore@{snapshot.ElapsedSeconds:F0}s: phase={ac.Phases.CurrentPhase?.Name} alt={ac.Altitude:F0}");

            // The reported bug: this was rejected with "Not on the leg before crosswind".
            var result = engine.SendCommand("N157LE", "TC");
            Assert.True(result.Success, $"TC rejected: {result.Message}");

            // Tick through 400 ft AGL and confirm the aircraft turns crosswind early — it reaches
            // the PatternExit (crosswind departure) leg shortly after the floor, not after flying
            // the full upwind to the normal geometric/altitude turn point.
            PatternExitPhase? exit = null;
            double altAtExit = 0;
            for (int t = 1; t <= 90; t++)
            {
                engine.TickOneSecond();
                ac = engine.FindAircraft("N157LE");
                if (ac is null)
                {
                    break;
                }

                if (ac.Phases?.CurrentPhase is PatternExitPhase pe)
                {
                    exit = pe;
                    altAtExit = ac.Altitude;
                    output.WriteLine($"t+{t}: reached PatternExit at alt={ac.Altitude:F0} ft MSL");
                    break;
                }
            }

            Assert.NotNull(exit);
            Assert.True(
                altAtExit < 700,
                $"Crosswind turn should be early (≈400 ft AGL / ≈409 ft MSL), but PatternExit began at {altAtExit:F0} ft MSL "
                    + "— at/above the normal auto-turn floor (pattern altitude − 300 ≈ 709 ft MSL)"
            );
        }
    }
}
