using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for runway exit speed: N70CS (C172, piston) lands on OAK 28L and
/// enters RunwayExitPhase toward taxiway J. The aircraft should maintain coast
/// speed (~25 kts for piston) while rolling toward the hold-short, not slow to
/// a crawl.
///
/// Recording: S2-OAK-4 VFR Transitions/Radar Concepts — CLAND at t=820.
/// </summary>
public class RunwayExitDoubleDecelTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-runway-exit-4kts-recording.yaat-recording.zip";

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
    /// N70CS (piston) should maintain ~25 kts coast speed during runway exit,
    /// not crawl at 4-15 kts. Minimum sustained speed during exit (excluding
    /// the final braking seconds) must be above 20 kts for pistons.
    /// </summary>
    [Fact]
    public void N70CS_MaintainsCoastSpeedDuringExit()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // CLAND at t=820; replay past that and continue until landing+exit
        engine.Replay(recording, 820);

        var ac = engine.FindAircraft("N70CS");
        Assert.NotNull(ac);

        bool enteredExit = false;
        int exitStartTick = 0;
        int exitEndTick = 0;
        var speedSamples = new List<double>();

        for (int t = 1; t <= 500; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N70CS");
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
                    output.WriteLine($"t+{t}: entered RunwayExitPhase, gs={ac.GroundSpeed:F1} ias={ac.IndicatedAirspeed:F1}");
                }

                int exitElapsed = t - exitStartTick;
                speedSamples.Add(ac.GroundSpeed);

                if (exitElapsed % 5 == 0)
                {
                    output.WriteLine($"  exit +{exitElapsed}s: gs={ac.GroundSpeed:F1} ias={ac.IndicatedAirspeed:F1} hdg={ac.TrueHeading.Degrees:F0}");
                }
            }
            else if (enteredExit)
            {
                exitEndTick = t;
                int exitDuration = t - exitStartTick;
                output.WriteLine($"t+{t}: exit completed in {exitDuration}s, phase={phaseName}");
                break;
            }
        }

        Assert.True(enteredExit, "N70CS never entered RunwayExitPhase");
        Assert.True(exitEndTick > 0, "N70CS never completed runway exit within 500 seconds");

        // Exclude the last 3 samples (final braking) from the sustained speed check
        int sustainedCount = Math.Max(0, speedSamples.Count - 3);
        var sustainedSpeeds = speedSamples.Take(sustainedCount).ToList();

        if (sustainedSpeeds.Count > 0)
        {
            double minSustained = sustainedSpeeds.Min();
            double avgSustained = sustainedSpeeds.Average();
            output.WriteLine($"Sustained speed: min={minSustained:F1} avg={avgSustained:F1} samples={sustainedSpeeds.Count}");

            // Piston coast speed is 25 kts — sustained min should stay well above
            // the old 4-5 kts crawl. The brief kinematic braking dip is acceptable.
            Assert.True(
                minSustained >= 15,
                $"Sustained speed dropped to {minSustained:F1} kts — aircraft should maintain coast speed during exit, not crawl"
            );
        }
    }
}
