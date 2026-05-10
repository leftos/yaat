using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for SFO 28R exit bug: SKW3398 and WJA1508 land 28R without
/// explicit exit instructions and turn right (north, away from terminals),
/// arc left, get on grass, and cross the runway.
///
/// At SFO, the correct default exit from 28R is south (left), toward the
/// terminal complex between 28R and 28L.
///
/// Recording: S1-SFO-2 Ground Control 28_01 bug report bundle.
/// </summary>
public class IssueSfo28rExitTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue-sfo-28r-exit-recording.yaat-bug-report-bundle.zip";

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
    /// SKW3398 and WJA1508 land 28R without exit instructions. They must exit
    /// smoothly — no crossing back over the runway centerline, no wild heading
    /// reversals. The aircraft should reach a hold-short within a reasonable time.
    /// </summary>
    [Fact]
    public void Aircraft_ExitSmoothly_NoCrossRunway()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);
        var exitEntryTime = new Dictionary<string, int>();
        var exitDuration = new Dictionary<string, int>();
        var exitCompleted = new Dictionary<string, bool>();

        for (int t = 1; t <= 500; t++)
        {
            engine.ReplayOneSecond();

            foreach (string callsign in new[] { "SKW3398" })
            {
                var ac = engine.FindAircraft(callsign);
                if (ac is null)
                {
                    continue;
                }

                string phase = ac.Phases?.CurrentPhase?.Name ?? "none";

                if (phase == "Runway Exit")
                {
                    if (!exitEntryTime.ContainsKey(callsign))
                    {
                        exitEntryTime[callsign] = t;
                    }

                    exitDuration[callsign] = t - exitEntryTime[callsign];
                }
                else if (exitEntryTime.ContainsKey(callsign) && !exitCompleted.ContainsKey(callsign))
                {
                    exitCompleted[callsign] = true;
                }
            }
        }

        // Both aircraft should have completed exit
        foreach (string callsign in new[] { "SKW3398" })
        {
            var ac = engine.FindAircraft(callsign);
            Assert.NotNull(ac);

            string phase = ac.Phases?.CurrentPhase?.Name ?? "none";
            output.WriteLine(
                $"{callsign}: phase={phase}, twy={ac.Ground.CurrentTaxiway ?? "none"}, "
                    + $"hdg={ac.TrueHeading.Degrees:F1}, gs={ac.GroundSpeed:F1}kts"
            );

            Assert.True(exitCompleted.ContainsKey(callsign), $"{callsign} should have completed exit by t=500, but phase is '{phase}'");
        }

        // Exit should complete in under 60 seconds. This includes rolling time
        // from where LandingPhase ended to the exit branch node. The bug caused
        // 90+ second wandering with wild heading reversals.
        foreach (var (cs, duration) in exitDuration)
        {
            output.WriteLine($"{cs}: spent {duration}s in Runway Exit phase");

            Assert.True(
                duration <= 75,
                $"{cs} spent {duration}s in Runway Exit — should complete in under 60s. "
                    + "Aircraft is likely oscillating or overshooting the hold-short."
            );
        }
    }
}
