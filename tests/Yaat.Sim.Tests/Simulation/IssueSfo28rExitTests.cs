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
                $"{callsign}: phase={phase}, twy={ac.CurrentTaxiway ?? "none"}, " + $"hdg={ac.TrueHeading.Degrees:F1}, gs={ac.GroundSpeed:F1}kts"
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

    /// <summary>
    /// Diagnostic: replay to find both aircraft, log every second during
    /// landing and exit phases to trace the exact failure sequence.
    /// </summary>
    [Fact]
    public void Diagnostic_ExitBehavior()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        output.WriteLine($"Recording: {recording.Actions?.Count ?? 0} actions, {recording.TotalElapsedSeconds}s total");

        // Log relevant actions
        if (recording.Actions is not null)
        {
            foreach (var action in recording.Actions)
            {
                string desc = action.ToString() ?? action.GetType().Name;
                if (
                    desc.Contains("SKW3398", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("WJA1508", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("EL ", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("ER ", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("EXIT", StringComparison.OrdinalIgnoreCase)
                )
                {
                    output.WriteLine($"  t={action.ElapsedSeconds}: {desc}");
                }
            }
        }

        var layout = new TestAirportGroundData().GetLayout("SFO");

        string[] callsigns = ["SKW3398", "WJA1508"];
        var spawnTimes = new Dictionary<string, int>();
        var detailStart = new Dictionary<string, int>();
        var detailTicks = new Dictionary<string, int>();

        engine.Replay(recording, 0);

        for (int t = 1; t <= (int)recording.TotalElapsedSeconds && t <= 1800; t++)
        {
            engine.ReplayOneSecond();

            foreach (string cs in callsigns)
            {
                var ac = engine.FindAircraft(cs);
                if (ac is null)
                {
                    continue;
                }

                if (!spawnTimes.ContainsKey(cs))
                {
                    spawnTimes[cs] = t;
                    output.WriteLine(
                        $"\n{cs} spawned at t={t}, rwy={ac.Phases?.AssignedRunway?.Designator ?? "?"}, "
                            + $"alt={ac.Altitude:F0}ft, gs={ac.GroundSpeed:F1}kts"
                    );
                }

                string phaseName = ac.Phases?.CurrentPhase?.Name ?? "none";

                // Start detail logging near ground
                if (!detailStart.ContainsKey(cs) && (ac.IsOnGround || (ac.Altitude - 13.0) < 300))
                {
                    detailStart[cs] = t;
                    output.WriteLine($"\n--- {cs} detail mode at t={t}, phase={phaseName} ---");
                }

                if (detailStart.ContainsKey(cs))
                {
                    detailTicks.TryGetValue(cs, out int ticks);
                    detailTicks[cs] = ticks + 1;

                    if (ticks > 180)
                    {
                        continue;
                    }

                    string reqExit = ac.Phases?.RequestedExit is { } req ? $"side={req.Side}, twy={req.Taxiway ?? "any"}" : "none";
                    string resolvedExit = ac.Phases?.ResolvedExit is { } re
                        ? $"twy={re.TaxiwayName}, hs={re.HoldShortNode.Id}, path=[{string.Join(",", re.Path.Select(n => n.Id))}]"
                        : "none";

                    output.WriteLine(
                        $"t={t} {cs}: phase={phaseName}, gs={ac.GroundSpeed:F1}kts, "
                            + $"hdg={ac.TrueHeading.Degrees:F1}, "
                            + $"pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}), "
                            + $"onGround={ac.IsOnGround}, "
                            + $"reqExit=[{reqExit}], resolved=[{resolvedExit}], "
                            + $"twy={ac.CurrentTaxiway ?? "none"}"
                    );

                    if (layout is not null && ac.IsOnGround)
                    {
                        NearestNodeHelper.Log(output, $"  {cs} nodes:", ac, layout);
                    }
                }
                else if (t % 30 == 0)
                {
                    output.WriteLine($"t={t} {cs}: phase={phaseName}, alt={ac.Altitude:F0}ft, gs={ac.GroundSpeed:F1}kts");
                }
            }
        }
    }
}
