using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for SFO runway exit bug: SKW3398 given "EL T" at 30+ kts misses
/// T by 0.2 kts due to discrete-tick deceleration overshoot, falls back to D,
/// then overshoots D's 90° exit onto grass.
///
/// Server log from the recording proves the exact sequence:
///   [Landing] SKW3398: candidate exit T, angle=26, turnOffSpeed=30kts
///   [Landing] SKW3398: missed exit T (gs=30.2kts > 30kts), relaxing preference
///   [Landing] SKW3398: candidate exit D, angle=90, turnOffSpeed=15kts
///   [Landing] SKW3398: committing to exit D, gs=13.4kts
///
/// Recording: SFO scenario, SKW3398 (CRJ2) landing 28R, "EL T" at t=382.
/// </summary>
public class SfoRunwayExitTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/sfo-exit-el-t-recording.yaat-bug-report-bundle.zip";

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
    /// Hybrid replay: restore server snapshot just before the exit, then replay
    /// with current code. Verifies the fix works against the exact state the
    /// server had when the bug occurred.
    /// </summary>
    [Fact]
    public void HybridReplay_SKW3398_ExitsOnT()
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

            // Load scenario first (Replay to t=0 sets up scenario + weather)
            engine.Replay(recording, 0);

            // Restore snapshot at t=380 (just before EL T at t=382)
            var snapshot = archive.ReadSnapshotAt(380);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=380, skipping hybrid replay");
                return;
            }

            engine.RestoreFromSnapshot(snapshot.State);
            output.WriteLine($"Restored snapshot at t={snapshot.ElapsedSeconds}");

            // Replay from snapshot time through the exit (EL T at 382, exit by ~400)
            int startTime = (int)snapshot.ElapsedSeconds;
            int endTime = Math.Min(startTime + 80, (int)recording.TotalElapsedSeconds);

            engine.ReplayRange(startTime, endTime, recording.Actions);

            var aircraft = engine.FindAircraft("SKW3398");
            Assert.NotNull(aircraft);

            output.WriteLine(
                $"SKW3398: phase={aircraft.Phases?.CurrentPhase?.Name ?? "none"}, "
                    + $"twy={aircraft.CurrentTaxiway ?? "none"}, "
                    + $"pos=({aircraft.Position.Lat:F6},{aircraft.Position.Lon:F6}), "
                    + $"hdg={aircraft.TrueHeading.Degrees:F0}"
            );

            // Must have exited on T
            Assert.Equal("T", aircraft.CurrentTaxiway);
        }
    }

    /// <summary>
    /// Diagnostic: replay the recording and log SKW3398 exit state each second.
    /// </summary>
    [Fact]
    public void Diagnostic_LogExitProfile()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        output.WriteLine($"Recording: {recording.Actions?.Count ?? 0} actions, {recording.TotalElapsedSeconds}s total");
        if (recording.Actions is not null)
        {
            foreach (var action in recording.Actions)
            {
                string desc = action.ToString() ?? action.GetType().Name;
                if (desc.Contains("SKW3398", StringComparison.OrdinalIgnoreCase) || desc.Contains("EL ", StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine($"  t={action.ElapsedSeconds}: {desc}");
                }
            }
        }

        engine.Replay(recording, 0);

        int spawnTime = -1;
        for (int t = 1; t <= (int)recording.TotalElapsedSeconds; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("SKW3398");
            if (ac is not null && spawnTime < 0)
            {
                spawnTime = t;
                output.WriteLine(
                    $"\nSKW3398 spawned at t={t}, " + $"rwy={ac.Phases?.AssignedRunway?.Designator ?? "?"}, " + $"alt={ac.Altitude:F0}ft"
                );
                break;
            }
        }

        if (spawnTime < 0)
        {
            output.WriteLine("SKW3398 never appeared in recording");
            return;
        }

        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("SKW3398");
            if (ac is null)
            {
                output.WriteLine($"t={spawnTime + t}: SKW3398 deleted/gone");
                break;
            }

            string phaseName = ac.Phases?.CurrentPhase?.Name ?? "none";
            string reqExit = ac.Phases?.RequestedExit is { } req ? $"side={req.Side}, twy={req.Taxiway ?? "any"}" : "none";
            string currentTwy = ac.CurrentTaxiway ?? "none";

            if (t % 5 == 0 || phaseName.Contains("Exit") || phaseName.Contains("Landing"))
            {
                output.WriteLine(
                    $"t={spawnTime + t}: phase={phaseName}, gs={ac.GroundSpeed:F1}kts, "
                        + $"hdg={ac.TrueHeading.Degrees:F0}, "
                        + $"pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}), "
                        + $"reqExit=[{reqExit}], twy={currentTwy}"
                );
            }
        }
    }
}
