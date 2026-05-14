using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for the S1-OAK-P S1 Rating Practical Exam bundle:
/// SWA863 (B737) and NKS743 (A320) were both routed `... U W W1 30`. They converged
/// onto taxiway U from different feeders (TE for SWA863, T for NKS743). The
/// convergence detector correctly picked NKS743 as the yielder, but as SWA863
/// (the winner) closed on the now-slow NKS743, the head-on fallback + closing-proximity
/// pinned both at gs=0 around t≈100s for ~30 seconds, even though there was lateral
/// clearance on the merge.
///
/// Bundle snapshots (every 1s):
///   t= 80s   SWA863 gs=26.5 | NKS743 gs= 5.0   gap=0.096 nm  (winner past, yielder slow)
///   t= 95s   SWA863 gs=14.1 | NKS743 gs= 5.0   gap=0.051 nm
///   t=100s   SWA863 gs= 1.2 | NKS743 gs= 1.2   gap=0.044 nm  ← BOTH PINNED
///   t=110s   SWA863 gs= 1.2 | NKS743 gs= 1.2   gap=0.041 nm
///   t=130s   SWA863 gs= 0.0 | NKS743 gs= 0.0   gap=0.037 nm  ← user saved bundle
///
/// The fix should let SWA863 (the convergence winner) maintain taxi speed through
/// the merge — only the yielder slows.
/// </summary>
public class IssueS1OakDeadlockTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s1-oak-taxi-deadlock-recording.yaat-bug-report-bundle.zip";
    private const string Winner = "SWA863";
    private const string Yielder = "NKS743";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundConflictDetector", LogLevel.Debug)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// SWA863 is the convergence winner on the U-westbound corridor. It should
    /// taxi through the merge without getting pinned for an extended period.
    /// Today it stalls at ~1 kt for roughly 30+ consecutive seconds around
    /// t=98–130 because the head-on fallback fires against the slow yielder
    /// despite convergence already picking a winner.
    ///
    /// We measure the longest run of consecutive seconds at which the winner
    /// is below a meaningful taxi speed — that's the user-observable "stuck"
    /// duration. After the fix the winner may still briefly dip during the merge
    /// (e.g. trailing the yielder for a moment) but shouldn't sit pinned.
    /// </summary>
    [Fact]
    public void ConvergenceWinner_DoesNotStallAcrossMerge()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay through the start of the bug window — winner is still rolling.
        engine.Replay(recording, 95);

        const double StallThresholdKts = 2.0;
        const int MaxAllowedStallSeconds = 8;

        int currentRun = 0;
        int longestRun = 0;
        int longestRunEndTick = -1;

        // Step second-by-second through the bug window (t=95 → t=130).
        for (int t = 96; t <= 130; t++)
        {
            engine.ReplayOneSecond();
            var w = engine.FindAircraft(Winner);
            var y = engine.FindAircraft(Yielder);
            Assert.NotNull(w);
            Assert.NotNull(y);

            output.WriteLine(
                $"t={t}: "
                    + $"{Winner} gs={w.GroundSpeed:F1} lim={w.Ground.SpeedLimit?.ToString("F1") ?? "null"} | "
                    + $"{Yielder} gs={y.GroundSpeed:F1} lim={y.Ground.SpeedLimit?.ToString("F1") ?? "null"} "
                    + $"gap={GeoMath.DistanceNm(w.Position, y.Position) * 6076.12:F0}ft"
            );

            if (w.GroundSpeed < StallThresholdKts)
            {
                currentRun++;
                if (currentRun > longestRun)
                {
                    longestRun = currentRun;
                    longestRunEndTick = t;
                }
            }
            else
            {
                currentRun = 0;
            }
        }

        output.WriteLine(
            $"{Winner} longest consecutive seconds below {StallThresholdKts:F1} kts: {longestRun}s " + $"(run ending at t={longestRunEndTick})"
        );

        // Winner is on a different edge than the yielder (T merging into U) with
        // lateral wingspan clearance. The fix:
        //   - extends the lateral-clearance bypass to slowed convergence yielders,
        //   - suppresses the head-on fallback for pairs convergence has already
        //     classified.
        // After the fix, the winner taxis through with at most a brief lull.
        Assert.True(
            longestRun <= MaxAllowedStallSeconds,
            $"{Winner} (convergence winner) was below {StallThresholdKts:F1} kts for "
                + $"{longestRun} consecutive seconds (ending at t={longestRunEndTick}). "
                + $"Expected ≤{MaxAllowedStallSeconds}s — head-on / proximity should not pin "
                + $"the winner when convergence already classified a yielder on a different edge."
        );
    }
}
