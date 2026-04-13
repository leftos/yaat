using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for diagonal runway lineup on SFO RWY 28R from taxiway E.
/// N346G would enter perpendicular, then taxi diagonally to the centerline
/// instead of smoothly turning onto the runway heading. The diagonal was
/// caused by Stage 2 (NavigateToTarget) kicking in after the on-runway
/// node was reached even though the aircraft was already close to centerline.
///
/// Fix: skip Stage 2 when cross-track at Stage 1 completion is within
/// OnRunwayNodeThresholdNm, going directly to heading alignment.
///
/// Recording: S1-SFO-2 Ground Control 28/01 — N346G gets CTO at t=250.
/// </summary>
public class SfoLineupDiagonalTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/09304e0c727e.zip";

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

    /// <summary>
    /// Tick recorder for the SFO 28R lineup scenario. Writes
    /// <c>.tmp/sfo-lineup28r-ticks.csv</c> covering the replay window
    /// around CTO (t=248..270). Render with LayoutInspector --ticks.
    /// </summary>
    [Fact]
    public void Diagnostic_RecordLineupTicks()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 248);

        var ac = engine.FindAircraft("N346G");
        if (ac is null)
        {
            return;
        }

        var recorder = new TickRecorder(ac);
        recorder.Record(248);

        // Capture seconds 249..280 (CTO at 250, give a generous window)
        for (int t = 249; t <= 280; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N346G");
            if (ac is null)
            {
                break;
            }
            recorder.Record(t);

            if (ac.Phases?.CurrentPhase is TakeoffPhase or LinedUpAndWaitingPhase)
            {
                output.WriteLine($"[diag] lineup-complete at t={t} phase={ac.Phases.CurrentPhase.Name}");
                // Record a few more ticks past the transition then stop
                for (int t2 = t + 1; t2 <= Math.Min(t + 5, 280); t2++)
                {
                    engine.ReplayOneSecond();
                    ac = engine.FindAircraft("N346G");
                    if (ac is null)
                    {
                        break;
                    }
                    recorder.Record(t2);
                }
                break;
            }
        }

        string csvPath = Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "sfo-lineup28r-ticks.csv");
        recorder.WriteCsv(csvPath);
        output.WriteLine($"[diag] wrote {recorder.Count} ticks to {csvPath}");
    }
}
