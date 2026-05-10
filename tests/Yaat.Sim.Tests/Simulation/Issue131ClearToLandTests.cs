using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #131: Clear to Land (CL/CLAND) not being remembered.
///
/// Recording: S2-OAK-4 VFR Transitions Radar Concepts — OAK tower scenario.
/// Multiple aircraft receive CLAND but the clearance state is lost before
/// FinalApproachPhase checks it, causing unexpected go-arounds.
/// </summary>
public class Issue131ClearToLandTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue131-clear-to-land-recording.json";

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
    /// After entering the pattern and receiving CLAND on downwind, the aircraft
    /// must land instead of going around. Replays N775JW to downwind (after the
    /// turn fix removes the 360), issues CLAND, then ticks until the aircraft
    /// either lands or goes around.
    /// </summary>
    [Fact]
    public void PatternEntry_ClandOnDownwind_AircraftLands()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay past ERD 28R (t=539) and EF 28R (t=705).
        // By t=610 N775JW is on PatternEntry heading toward downwind.
        engine.Replay(recording, 610);

        var ac = engine.FindAircraft("N775JW");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        output.WriteLine($"t=610: phase={ac.Phases.CurrentPhase?.GetType().Name} alt={ac.Altitude:F0}");

        // Tick until we're on DownwindPhase
        for (int t = 1; t <= 200; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N775JW");
            if (ac is null)
            {
                break;
            }

            if (ac.Phases?.CurrentPhase is Yaat.Sim.Phases.Pattern.DownwindPhase)
            {
                output.WriteLine($"t+{t}: reached downwind, issuing CLAND");
                var result = engine.SendCommand("N775JW", "CLAND");
                output.WriteLine($"  CLAND result: {result.Success} — {result.Message}");
                Assert.True(result.Success);
                break;
            }
        }

        ac = engine.FindAircraft("N775JW");
        Assert.NotNull(ac);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases?.LandingClearance);

        // Now tick until the aircraft lands or goes around (max 300s)
        bool landed = false;
        bool goAround = false;
        for (int t = 1; t <= 300; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N775JW");
            if (ac is null)
            {
                output.WriteLine($"t+{t}: aircraft deleted (landed and removed)");
                landed = true;
                break;
            }

            if (ac.IsOnGround && ac.GroundSpeed < 30)
            {
                output.WriteLine($"t+{t}: landed, gs={ac.GroundSpeed:F0}kts");
                landed = true;
                break;
            }

            foreach (var w in ac.PendingWarnings)
            {
                if (w.Contains("going around", StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine($"t+{t}: WARNING: {w}");
                    goAround = true;
                }
            }
        }

        Assert.True(landed, "Aircraft should have landed after CLAND on downwind");
        Assert.False(goAround, "Aircraft should not have gone around after CLAND");
    }
}
