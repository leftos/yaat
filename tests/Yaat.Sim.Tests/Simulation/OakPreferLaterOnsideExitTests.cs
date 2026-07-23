using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the "prefer later on-side exit over earlier off-side exit" rule.
///
/// Recording: S2-OAK-3 (2) | VFR Sequencing — N805FM (P28A) lands on OAK 28R with
/// no exit instruction. Another aircraft (N775JW) is HoldingAfterExit at G's
/// right-side hold-short (HS 508, the parking-bias-preferred side). Without the
/// rule, LandingPhase commits to G_right (correct on-side), then RunwayExitPhase
/// detects occupancy at handoff and falls back to G_left (HS 514) — wrong side.
///
/// The rule says: when the preferred-side branch at the closer exit is unavailable
/// and a later preferred-side exit is comfort-reachable, prefer the later one.
/// Here that means H's right-side hold-short (HS 509) over G's left (HS 514).
/// </summary>
public class OakPreferLaterOnsideExitTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-prefer-later-onside-exit-recording.yaat-bug-report-bundle.zip";

    // Hold-short node IDs for OAK 28R G/H — graph-dependent; regenerate via
    // `Yaat.LayoutInspector --exits 28R` if the ground-graph or hold-short placement changes.
    private const int HoldShortGLeft = 514;
    private const int HoldShortGRight = 508;
    private const int HoldShortHLeft = 515;
    private const int HoldShortHRight = 509;

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
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("LandingPhase", LogLevel.Debug)
            .EnableCategory("RunwayExitPhase", LogLevel.Debug)
            .EnableCategory("AirportGroundLayout", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// E2E: replays the recording and asserts N805FM never commits to G_left
    /// (the off-side fallback). The expected outcome is H_right (on-side, later)
    /// or any other on-side option ahead of G.
    /// </summary>
    [Fact]
    public void N805FM_PrefersLaterOnSideExitOverEarlierOffSide()
    {
        var swTotal = Stopwatch.StartNew();
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Touchdown is around t=805. Replay just before touchdown so we can watch
        // the candidate selection unfold tick-by-tick.
        engine.Replay(recording, 800);

        var ac = engine.FindAircraft("N805FM");
        Assert.NotNull(ac);

        int? plannerCandidateHs = null;
        string? plannerCandidateTaxiway = null;
        int? finalResolvedHs = null;
        string? finalResolvedTaxiway = null;
        bool reachedRunwayExit = false;
        bool reachedHolding = false;
        ExitSide? inferredSide = null;

        for (int t = 1; t <= 200; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N805FM");
            if (ac is null)
            {
                output.WriteLine($"t+{t}: aircraft deleted");
                break;
            }

            var current = ac.Phases?.CurrentPhase;
            string? phaseName = current?.GetType().Name;

            // Track the LATEST LandingPhase candidate every tick. The planner
            // can transiently commit to an earlier exit (e.g. RAMP) that gets
            // marked Unable, then settle on the final on-side choice. We assert
            // against the final commit, which is what propagates to handoff.
            if ((current is LandingPhase landing) && (landing.CandidateExit is { } cand))
            {
                plannerCandidateHs = cand.HoldShortNode.Id;
                plannerCandidateTaxiway = cand.TaxiwayName;
                inferredSide = landing.InferredSide;
            }

            // RunwayExitPhase clears Phases.ResolvedExit in OnStart, so we must
            // read the actively-targeted hold-short from the phase itself.
            if (current is RunwayExitPhase runwayExit)
            {
                reachedRunwayExit = true;
                if ((finalResolvedHs is null) && (runwayExit.TargetHoldShortNodeId is { } targetId))
                {
                    finalResolvedHs = targetId;
                    finalResolvedTaxiway = runwayExit.RunwayId;
                    output.WriteLine($"t+{t}: RunwayExit targeting: HS={finalResolvedHs}");
                }
            }

            if (phaseName == "HoldingAfterExitPhase")
            {
                reachedHolding = true;
                output.WriteLine($"t+{t}: HoldingAfterExit reached at ({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees:F0}");
                break;
            }

            if (t % 10 == 0)
            {
                output.WriteLine(
                    $"t+{t}: alt={ac.Altitude:F0} gs={ac.GroundSpeed:F0} onGround={ac.IsOnGround} phase={phaseName} planner=HS{plannerCandidateHs?.ToString() ?? "?"}({plannerCandidateTaxiway ?? "?"})"
                );
            }
        }

        output.WriteLine(
            $"Final planner candidate: HS={plannerCandidateHs} twy={plannerCandidateTaxiway} inferredSide={inferredSide}; final exit HS={finalResolvedHs}"
        );

        output.WriteLine($"[TIMING] Test total: {swTotal.Elapsed.TotalMilliseconds:F0}ms");

        Assert.True(reachedRunwayExit, "N805FM never reached RunwayExitPhase");
        Assert.True(reachedHolding, "N805FM never reached HoldingAfterExitPhase");
        Assert.NotNull(plannerCandidateHs);
        Assert.NotNull(finalResolvedHs);

        // The inferred side at OAK 28R is Right (parking-bias). Confirm.
        Assert.Equal(ExitSide.Right, inferredSide);

        // The planner must NOT have committed to G_left (HS 514). G_right (HS 508)
        // is occupied by N775JW, so the planner should have looked further forward
        // and committed to an on-side exit at H or beyond.
        Assert.NotEqual(HoldShortGLeft, plannerCandidateHs);

        // Final resolved exit must NOT be G_left either — same rule applies in
        // RunwayExitPhase's relaxation path.
        Assert.NotEqual(HoldShortGLeft, finalResolvedHs);

        // Strong assertion: the final exit should be on the right (preferred) side.
        // Allowed targets: any right-side hold-short at G or beyond — along 28R that is
        // G_right (508), H_right (509), E (510), P_right (511), or J_right (374). Never
        // G_left (514): falling back to the off-side is the bug this test guards.
        var rightSideHoldShorts = new HashSet<int>
        {
            HoldShortGRight,
            HoldShortHRight,
            510, /* E */
            511, /* P_right */
            374, /* J_right */
        };
        Assert.Contains(finalResolvedHs!.Value, rightSideHoldShorts);
    }
}
