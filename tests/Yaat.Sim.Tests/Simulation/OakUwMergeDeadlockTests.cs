using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for the OAK U/W (ground node 17) taxiway-merge deadlock. Two departures to RWY 30 merge
/// onto one shared lane at node 17: JSX177 arrives on twy W (677-&gt;17) and SWA897 on twy U
/// (679-&gt;17), then both run the identical lane 17-&gt;676-&gt;16-&gt;29(W1)... The
/// <see cref="GroundConflictDetector"/> classified the pair as Converging and correctly picked
/// SWA897 as the yielder, but its closing-proximity safety net then pinned BOTH aircraft to
/// SpeedLimit=0 — a mutual deadlock that only cleared when the user manually issued BREAK at
/// t=973. After the merge-arbitration fix the merge-order leader (JSX177, nearer node 17)
/// proceeds while SWA897 holds, so the pair is never simultaneously stopped.
///
/// Recording: <c>oak-uw-merge-deadlock-recording.zip</c> (S2-OAK-5, ZOA), trimmed to ~t=1050.
/// With realistic (v/R-coupled) taxi timing the two aircraft no longer reach node 17 simultaneously:
/// JSX177 leads through the merge (~t=961) and SWA897 trails onto the shared W lane and auto-yields
/// to it (~t=988, after JSX's manual BREAK at t=973 has already expired — so the yield is the auto
/// merge-sequencer, not the BREAK). The window runs to t=1000 to capture that break-independent
/// auto-yield; the anti-deadlock assertion (never both-stopped) holds throughout because the leader
/// keeps moving while the follower yields.
/// </summary>
public class OakUwMergeDeadlockTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-uw-merge-deadlock-recording.zip";

    // JSX177 leads through node 17 (~t=961); SWA897 trails and auto-yields on the shared W lane (~t=988,
    // after the manual BREAK at t=973 has expired). The window spans the whole sequence.
    private const int WindowStart = 940;
    private const int MergeWindowEnd = 1000;

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
    public void Diagnostic_MergeWindow()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, WindowStart);
        for (int t = WindowStart; t <= 1010; t++)
        {
            var jsx = engine.FindAircraft("JSX177");
            var swa = engine.FindAircraft("SWA897");
            if (jsx is not null && swa is not null)
            {
                output.WriteLine(
                    $"t={t, 4} JSX177 gs={jsx.IndicatedAirspeed, 5:F1} spdlim={Fmt(jsx.Ground.SpeedLimit)} seg={Seg(jsx)} brk={jsx.Ground.ConflictBreakRemainingSeconds:F0} | "
                        + $"SWA897 gs={swa.IndicatedAirspeed, 5:F1} spdlim={Fmt(swa.Ground.SpeedLimit)} ayt={swa.Ground.AutoYieldTarget ?? "-"} seg={Seg(swa)}"
                );
            }
            engine.ReplayOneSecond();
        }
    }

    [Fact]
    public void Merge_NeverDeadlocksBothAircraft_BeforeBreak()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, WindowStart);

        int maxConsecutiveBothStopped = 0;
        int consecutiveBothStopped = 0;
        bool conflictEngaged = false;
        int jsxStartSeg = -1;
        int jsxEndSeg = -1;

        for (int t = WindowStart; t <= MergeWindowEnd; t++)
        {
            var jsx = engine.FindAircraft("JSX177");
            var swa = engine.FindAircraft("SWA897");
            if (jsx is null || swa is null)
            {
                engine.ReplayOneSecond();
                continue;
            }

            int jsxSeg = jsx.Ground.AssignedTaxiRoute?.CurrentSegmentIndex ?? -1;
            if (jsxStartSeg < 0)
            {
                jsxStartSeg = jsxSeg;
            }
            jsxEndSeg = jsxSeg;

            // The merge conflict has engaged once either aircraft is speed-capped or annotated as
            // yielding — proves the assertion is not passing vacuously (they really met at node 17).
            if ((jsx.Ground.SpeedLimit is not null) || (swa.Ground.SpeedLimit is not null) || swa.Ground.AutoYieldTarget == "JSX177")
            {
                conflictEngaged = true;
            }

            bool jsxStopped = jsx.IndicatedAirspeed < 1.0 && jsx.Ground.SpeedLimit is { } jl && jl <= 0.5;
            bool swaStopped = swa.IndicatedAirspeed < 1.0 && swa.Ground.SpeedLimit is { } sl && sl <= 0.5;
            if (jsxStopped && swaStopped)
            {
                consecutiveBothStopped++;
                maxConsecutiveBothStopped = Math.Max(maxConsecutiveBothStopped, consecutiveBothStopped);
            }
            else
            {
                consecutiveBothStopped = 0;
            }

            engine.ReplayOneSecond();
        }

        output.WriteLine(
            $"conflictEngaged={conflictEngaged} maxConsecutiveBothStopped={maxConsecutiveBothStopped}s jsxSeg {jsxStartSeg}->{jsxEndSeg}"
        );

        Assert.True(conflictEngaged, "Expected JSX177/SWA897 to actually meet at the U/W merge (node 17) — test is vacuous otherwise.");

        // Pre-fix: both pinned to 0 for ~10+ consecutive seconds (only BREAK freed them).
        // Post-fix: the merge-order leader proceeds, so the pair is never both stopped for long.
        Assert.True(
            maxConsecutiveBothStopped <= 4,
            $"Both JSX177 and SWA897 were stopped (gs<1, SpeedLimit~0) for {maxConsecutiveBothStopped} consecutive seconds before BREAK — "
                + "the U/W merge deadlocked instead of sequencing one-at-a-time."
        );

        // The merge-order leader must make forward progress through node 17 (not sit stuck).
        Assert.True(jsxEndSeg > jsxStartSeg, $"JSX177 did not advance through the merge: CurrentSegmentIndex stayed at {jsxStartSeg}..{jsxEndSeg}.");
    }

    private static string Fmt(double? v) => v is { } x ? x.ToString("F1") : "-";

    private static string Seg(AircraftState ac)
    {
        var rt = ac.Ground.AssignedTaxiRoute;
        if (rt is null || rt.CurrentSegmentIndex < 0 || rt.CurrentSegmentIndex >= rt.Segments.Count)
        {
            return "-";
        }
        var s = rt.Segments[rt.CurrentSegmentIndex];
        return $"{s.FromNodeId}->{s.ToNodeId}[{s.TaxiwayName}]";
    }
}
