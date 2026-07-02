using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for GitHub issue #222: pushback stalls when an aircraft is parked at the
/// adjacent gate. In the OAK S1 practical-exam scenario, SWA1182 (B737) at gate 25
/// issues <c>PUSH TE</c> at t=340, but <see cref="GroundConflictDetector"/> hard-pins
/// it to SpeedLimit=0 because SWA3998 (B737) is parked at gate 26 (~145 ft away, inside
/// the 200 ft pushback buffer and in the rear ±90° arc). The user had to issue BREAK
/// three times (t=411, 461, 484) to walk it out. After the fix a genuinely parked/held
/// neighbor is a passable obstacle, so the pushback completes on its own.
///
/// Recording: <c>issue222-oak-pushback-parked-neighbor-recording.zip</c> (S1-OAK-P, ZOA),
/// trimmed to ~t=520. Assertions are scoped to before the user's first BREAK (t=411) so
/// that BREAK cannot mask the bug.
/// </summary>
public class Issue222PushbackParkedNeighborTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue222-oak-pushback-parked-neighbor-recording.zip";

    private const string Pusher = "SWA1182";
    private const string ParkedNeighbor = "SWA3998";

    // PUSH TE is at t=340; the first (masking) BREAK is at t=411.
    private const int AfterPush = 341;
    private const int FirstBreak = 411;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundConflictDetector", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Diagnostic_PushbackWindow()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, AfterPush);
        for (int t = AfterPush; t <= 500; t++)
        {
            var ac = engine.FindAircraft(Pusher);
            var nb = engine.FindAircraft(ParkedNeighbor);
            if (ac is not null && nb is not null)
            {
                double gapFt = GeoMath.DistanceNm(ac.Position, nb.Position) * 6076.12;
                output.WriteLine(
                    $"t={t, 4} phase={ac.Phases?.CurrentPhase?.Name, -22} ias={ac.IndicatedAirspeed, 5:F1} "
                        + $"spdlim={(ac.Ground.SpeedLimit is { } l ? l.ToString("F1") : "-"), 5} "
                        + $"brk={ac.Ground.ConflictBreakRemainingSeconds:F0} gap={gapFt:F0}ft"
                );
            }
            engine.ReplayOneSecond();
        }
    }

    [Fact]
    public void Pushback_CompletesWithoutBreak_PastParkedNeighbor()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, AfterPush);

        var start = engine.FindAircraft(Pusher);
        Assert.NotNull(start);
        Assert.Equal("Pushback", start.Phases?.CurrentPhase?.Name);
        var startPos = start.Position;

        // Tick faithfully but stop before the user's first BREAK (t=411) so it cannot
        // free the aircraft — proving the pushback clears the parked neighbor on its own.
        bool completed = false;
        int completedAt = -1;
        double maxGapClosedFt = 0;
        for (int t = AfterPush; t < FirstBreak; t++)
        {
            var ac = engine.FindAircraft(Pusher);
            if (ac is not null)
            {
                maxGapClosedFt = Math.Max(maxGapClosedFt, GeoMath.DistanceNm(ac.Position, startPos) * 6076.12);
                if (ac.Phases?.CurrentPhase?.Name == "Holding After Pushback")
                {
                    completed = true;
                    completedAt = t;
                    break;
                }
            }
            engine.ReplayOneSecond();
        }

        output.WriteLine($"completed={completed} at t={completedAt}, pushed {maxGapClosedFt:F0}ft");

        Assert.True(
            completed,
            $"{Pusher} never completed pushback before the user's first BREAK (t={FirstBreak}): it stayed pinned in Pushback "
                + $"by {ParkedNeighbor} parked at the adjacent gate (pushed only {maxGapClosedFt:F0}ft)."
        );
    }
}
