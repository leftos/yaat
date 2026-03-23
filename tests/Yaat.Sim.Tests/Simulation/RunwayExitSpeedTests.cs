using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
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
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

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
        double minSpeedDuringExit = double.MaxValue;
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

            if (phaseName == "RunwayExitPhase")
            {
                if (!enteredExit)
                {
                    enteredExit = true;
                    exitStartTick = t;
                    output.WriteLine($"t+{t}: entered RunwayExitPhase, gs={ac.GroundSpeed:F1}");
                }

                minSpeedDuringExit = Math.Min(minSpeedDuringExit, ac.GroundSpeed);
                maxSpeedDuringExit = Math.Max(maxSpeedDuringExit, ac.GroundSpeed);

                if ((t - exitStartTick) % 3 == 0)
                {
                    output.WriteLine($"  exit t+{t}: gs={ac.GroundSpeed:F1} hdg={ac.TrueHeading.Degrees:F0}");
                }
            }
            else if (enteredExit && phaseName != "RunwayExitPhase")
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
        output.WriteLine($"Exit duration: {exitDuration}s, speed range: {minSpeedDuringExit:F1}-{maxSpeedDuringExit:F1} kts");

        // The exit should complete in under 20 seconds, not 37+
        Assert.True(exitDuration <= 20, $"Exit took {exitDuration}s — expected ≤20s. Aircraft was creeping too slowly.");

        // Minimum speed during exit should be above 4.5 kts
        // (5 kts floor is only acceptable in the final braking moment)
        Assert.True(minSpeedDuringExit >= 4.5, $"Speed dropped to {minSpeedDuringExit:F1} kts — expected exit speed maintained");
    }
}
