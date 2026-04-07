using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for SFO 28R exit-left-T bug: SKW3398 given "EL T" blows past T,
/// then before D turns and cuts through grass heading back toward T, snaps onto
/// it, overshoots, makes a 180, and oscillates.
///
/// Recording: S1-SFO-2 Ground Control 28_01, SKW3398 (CRJ2) landing 28R.
/// T exit from 28R is a 9-node, 0.11nm path (nodes 230→231→...→835) with a
/// shallow 19° exit angle — the longest exit on 28R.
/// </summary>
public class SfoExitLeftTTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue-sfo-28r-el-t-recording.zip";

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
    /// Diagnostic: replay the recording and log SKW3398 exit state every tick (0.25s).
    /// Focus on the landing/exit transition to see exactly where things go wrong.
    /// </summary>
    [Fact]
    public void Diagnostic_TickByTick_ExitProfile()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        output.WriteLine($"Recording: {recording.Actions?.Count ?? 0} actions, {recording.TotalElapsedSeconds}s total");

        // Log all SKW3398-related and exit-related actions
        if (recording.Actions is not null)
        {
            foreach (var action in recording.Actions)
            {
                string desc = action.ToString() ?? action.GetType().Name;
                if (
                    desc.Contains("SKW3398", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("EL ", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("EXIT", StringComparison.OrdinalIgnoreCase)
                )
                {
                    output.WriteLine($"  t={action.ElapsedSeconds}: {desc}");
                }
            }
        }

        // Replay to find SKW3398 spawn time
        engine.Replay(recording, 0);
        int spawnTime = -1;
        for (int t = 1; t <= (int)recording.TotalElapsedSeconds; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("SKW3398");
            if (ac is not null)
            {
                spawnTime = t;
                output.WriteLine(
                    $"\nSKW3398 spawned at t={t}, rwy={ac.Phases?.AssignedRunway?.Designator ?? "?"}, "
                        + $"alt={ac.Altitude:F0}ft, gs={ac.GroundSpeed:F1}kts"
                );
                break;
            }
        }

        if (spawnTime < 0)
        {
            output.WriteLine("SKW3398 never appeared in recording");
            return;
        }

        // Now advance second-by-second until near touchdown, then switch to tick-by-tick
        bool inDetailMode = false;
        int tickCount = 0;
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
            bool isGroundPhase =
                phaseName.Contains("Exit") || phaseName.Contains("Taxi") || phaseName.Contains("Hold") || phaseName.Contains("Landing");

            // Switch to detail mode once we're near the ground
            if (!inDetailMode && (ac.IsOnGround || (ac.Altitude - 13.0) < 200))
            {
                inDetailMode = true;
                output.WriteLine($"\n--- Detail mode at t={spawnTime + t} ---");
            }

            if (inDetailMode)
            {
                string reqExit = ac.Phases?.RequestedExit is { } req ? $"side={req.Side}, twy={req.Taxiway ?? "any"}" : "none";
                string currentTwy = ac.CurrentTaxiway ?? "none";
                string resolvedExit = ac.Phases?.ResolvedExit is { } re
                    ? $"twy={re.TaxiwayName}, hs={re.HoldShortNode.Id}, bp={re.BranchPointNode.Id}, path=[{string.Join(",", re.Path.Select(n => n.Id))}]"
                    : "none";

                output.WriteLine(
                    $"t={spawnTime + t}: phase={phaseName}, gs={ac.GroundSpeed:F1}kts, "
                        + $"hdg={ac.TrueHeading.Degrees:F1}, "
                        + $"pos=({ac.Latitude:F6},{ac.Longitude:F6}), "
                        + $"onGround={ac.IsOnGround}, "
                        + $"reqExit=[{reqExit}], resolved=[{resolvedExit}], twy={currentTwy}"
                );
                tickCount++;

                if (tickCount > 120)
                {
                    output.WriteLine("... stopping after 120 detail seconds");
                    break;
                }
            }
            else if (t % 10 == 0)
            {
                output.WriteLine($"t={spawnTime + t}: phase={phaseName}, alt={ac.Altitude:F0}ft, gs={ac.GroundSpeed:F1}kts");
            }
        }
    }

    /// <summary>
    /// SKW3398 given "EL T" must exit on taxiway T, not D or any other taxiway.
    /// </summary>
    [Fact]
    public void SKW3398_ExitsOnTaxiwayT()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, (int)recording.TotalElapsedSeconds);

        var aircraft = engine.FindAircraft("SKW3398");
        Assert.NotNull(aircraft);

        output.WriteLine(
            $"SKW3398: phase={aircraft.Phases?.CurrentPhase?.Name ?? "none"}, "
                + $"twy={aircraft.CurrentTaxiway ?? "none"}, "
                + $"pos=({aircraft.Latitude:F6},{aircraft.Longitude:F6}), "
                + $"hdg={aircraft.TrueHeading.Degrees:F0}, gs={aircraft.GroundSpeed:F1}kts"
        );

        Assert.Equal("T", aircraft.CurrentTaxiway);
    }
}
