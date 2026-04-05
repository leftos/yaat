using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for ER/EL taxiway argument being ignored.
///
/// Recording: S1-SFO-2 Ground Control 28/01 — WJA1508 (B38M) lands on 28R
/// with ER D (exit right onto taxiway D). The aircraft ignores the taxiway
/// argument and exits at E instead, because CommandDispatcher drops the
/// Taxiway field from ExitRightCommand/ExitLeftCommand.
/// </summary>
[Collection("NavDbMutator")]
public class ExitRightTaxiwaySelectionTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/er-d-wrong-exit-recording.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// WJA1508 is given ER D before landing on 28R. After exit, the aircraft
    /// should be on taxiway D, not E.
    /// </summary>
    [Fact]
    public void WJA1508_ExitsAtTaxiwayD_NotE()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=950 — after ER D is issued (around t=874 wall-clock)
        // but before landing (~t=1000). The ER D command is in the recording actions.
        engine.Replay(recording, 950);

        var ac = engine.FindAircraft("WJA1508");
        Assert.NotNull(ac);

        output.WriteLine(
            $"t=950: alt={ac.Altitude:F0} gs={ac.GroundSpeed:F0} hdg={ac.TrueHeading.Degrees:F0} phase={ac.Phases?.CurrentPhase?.GetType().Name}"
        );

        // Tick forward until the aircraft exits the runway or 600 seconds elapse
        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("WJA1508");
            if (ac is null)
            {
                output.WriteLine($"t+{t}: aircraft deleted");
                break;
            }

            if (t % 30 == 0)
            {
                output.WriteLine(
                    $"t+{t}: alt={ac.Altitude:F0} gs={ac.GroundSpeed:F0} hdg={ac.TrueHeading.Degrees:F0}"
                        + $" taxiway={ac.CurrentTaxiway ?? "(none)"} phase={ac.Phases?.CurrentPhase?.GetType().Name}"
                );
            }

            // CurrentTaxiway is set when RunwayExitPhase completes
            if (ac.CurrentTaxiway is not null)
            {
                output.WriteLine($"t+{t}: exited runway at taxiway {ac.CurrentTaxiway} hdg={ac.TrueHeading.Degrees:F0}");

                Assert.Equal("D", ac.CurrentTaxiway.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
                return;
            }
        }

        Assert.Fail("WJA1508 never exited the runway within 600 seconds");
    }
}
