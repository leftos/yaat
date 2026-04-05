using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for runway exit turn-off speed: aircraft should maintain their
/// computed exit speed through the turn, not immediately decelerate to
/// a 5 kts crawl.
///
/// Recording: S2-OAK-4 VFR Transitions/Radar Concepts — N569SX (PA34,
/// piston) lands on 28R and exits at taxiway G (standard exit, >45° angle).
/// Before the fix, the aircraft decelerates to 5 kts floor immediately
/// on entering the turn-off, taking ~37 seconds to complete the exit.
/// </summary>
[Collection("NavDbMutator")]
public class RunwayExitSpeedTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-slow-exit-recording.yaat-recording.zip";

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
    /// N569SX exits OAK 28R at taxiway G. The exit should complete in a
    /// reasonable time — not creeping at 5 kts for 30+ seconds.
    /// Piston StandardExitSpeed = 12 kts; at that speed the turn-off
    /// distance (~0.02nm) should take roughly 6 seconds, not 37.
    /// </summary>
    [Fact]
    public void N569SX_ExitsRunwayWithinReasonableTime()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // N569SX spawns on final ~10nm out. With speed maintained until 5nm,
        // it arrives earlier than the original test expected. Start at t=400
        // to catch the full landing→exit sequence.
        engine.Replay(recording, 400);

        var ac = engine.FindAircraft("N569SX");
        Assert.NotNull(ac);

        output.WriteLine($"t=400: gs={ac.GroundSpeed:F1} phase={ac.Phases?.CurrentPhase?.GetType().Name}");

        bool enteredExit = false;
        int exitStartTick = 0;
        int exitEndTick = 0;
        int ticksBelowThreshold = 0;
        int totalExitTicks = 0;
        double maxSpeedDuringExit = 0;

        for (int t = 1; t <= 300; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N569SX");
            if (ac is null)
            {
                break;
            }

            string? phaseName = ac.Phases?.CurrentPhase?.GetType().Name;

            // The exit sequence is: RunwayExitPhase (may be instant) → TaxiingPhase (follows exit path)
            // → HoldingAfterExitPhase (stopped). RunwayExitPhase hands off to TaxiingPhase within
            // one tick, so at 1-second resolution we may only see TaxiingPhase. Detect the exit start
            // by the phase name (RunwayExitPhase or TaxiingPhase following LandingPhase).
            bool isExitPhase =
                phaseName == "RunwayExitPhase"
                || (phaseName == "TaxiingPhase" && ac.CurrentTaxiway is not null && !enteredExit)
                || (enteredExit && phaseName == "TaxiingPhase");

            if (isExitPhase && !enteredExit)
            {
                enteredExit = true;
                exitStartTick = t;
                output.WriteLine($"t+{t}: entered exit ({phaseName}), gs={ac.GroundSpeed:F1}");
            }

            if (isExitPhase)
            {
                totalExitTicks++;
                if (ac.GroundSpeed < 4.5)
                {
                    ticksBelowThreshold++;
                }

                maxSpeedDuringExit = Math.Max(maxSpeedDuringExit, ac.GroundSpeed);

                if ((t - exitStartTick) % 3 == 0)
                {
                    output.WriteLine($"  exit t+{t}: gs={ac.GroundSpeed:F1} hdg={ac.TrueHeading.Degrees:F0}");
                }
            }
            else if (enteredExit && !isExitPhase)
            {
                exitEndTick = t;
                output.WriteLine($"t+{t}: exit completed, gs={ac.GroundSpeed:F1}, phase={phaseName}");
                break;
            }

            if (!enteredExit && (t % 10 == 0))
            {
                output.WriteLine($"t+{t}: gs={ac.GroundSpeed:F1} hdg={ac.TrueHeading.Degrees:F0} phase={phaseName}");
            }
        }

        Assert.True(enteredExit, "N569SX never entered RunwayExitPhase");
        Assert.True(exitEndTick > 0, "N569SX never completed runway exit within 300 seconds");

        int exitDuration = exitEndTick - exitStartTick;
        output.WriteLine($"Exit duration: {exitDuration}s, ticks below 4.5kts: {ticksBelowThreshold}/{totalExitTicks}");

        // The exit should complete in under 20 seconds, not 37+
        Assert.True(exitDuration <= 20, $"Exit took {exitDuration}s — expected ≤20s. Aircraft was creeping too slowly.");

        // Speed should stay above 4.5 kts for most of the exit turn — brief final
        // braking (last 2–3 ticks) is fine. The old bug had the aircraft crawling at
        // 5 kts for the entire turn. Allow at most 20% of ticks below the threshold.
        double fractionBelowThreshold = (double)ticksBelowThreshold / totalExitTicks;
        Assert.True(
            fractionBelowThreshold <= 0.20,
            $"Speed was below 4.5 kts for {ticksBelowThreshold}/{totalExitTicks} ticks ({fractionBelowThreshold:P0}) — "
                + "aircraft is crawling through the turn instead of maintaining exit speed"
        );
    }
}
