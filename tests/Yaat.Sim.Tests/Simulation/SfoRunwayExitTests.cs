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
                    + $"twy={aircraft.Ground.CurrentTaxiway ?? "none"}, "
                    + $"pos=({aircraft.Position.Lat:F6},{aircraft.Position.Lon:F6}), "
                    + $"hdg={aircraft.TrueHeading.Degrees:F0}"
            );

            // Must have exited on T
            Assert.Equal("T", aircraft.Ground.CurrentTaxiway);
        }
    }
}
