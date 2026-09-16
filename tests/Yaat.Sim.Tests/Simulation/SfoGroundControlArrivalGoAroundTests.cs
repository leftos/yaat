using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E arms over the <c>sfo-gc-arrival-goarounds</c> bundle — a <c>S1-SFO-2 | Ground Control 28/01</c>
/// session flown from SFO_GND with <c>AutoGoAroundOnOccupiedRunway</c> on. Three
/// <c>OccupiedRunwayGoAround</c> triggers fired in the recorded run; this class pins which of them the
/// sim should still produce, and — for the two arrival-behind-arrival cases — that the simulated-TRACON
/// same-runway protection is running on them and that the go-around it could not prevent is still a
/// legal one.
///
/// <list type="bullet">
/// <item><b>WJA1508 (28R)</b> — sent around at t=357 with SKW3398 still rolling out 4,705 ft down the
/// runway. The protection engages and pushes the go-around out to t≈364.</item>
/// <item><b>SKW5536 (28L)</b> — the same shape at t=689 behind UAL2627, pushed out to t≈708.</item>
/// <item><b>SKW5416 (28L, control)</b> — sent around at t=1109 because the student issued
/// <c>CROSS 28L</c> to SWA2644 at t=1092 with SKW5416 1.2 nm from the threshold. That is a legitimate,
/// student-caused incursion and the go-around must keep firing.</item>
/// </list>
///
/// <para><b>Why "it lands" is not the criterion.</b> The two arrival arms deliberately do <em>not</em>
/// assert a landing. This scenario delivers those pairs ~53–57 s in trail against a ~76.6 s required
/// threshold interval, and once the speed reduction is held to the §5-7-3.c floors there is only ~8 s of
/// net authority left in the 13→5 nm window the pass may operate in (§5-7-1.b.4 closes it at 5 nm). A
/// real TRACON opens a gap that size with vectors (§5-7-1.a.1); speed is this pass's only actuator, so it
/// cannot repair this geometry and hands what is left to the go-around. The residual cause is runway
/// occupancy — 59–77 s measured here against a real-world ~50 s, dominated by the rollout braking rate —
/// which is tracked as its own backlog item in <c>docs/plans/MAIN.md</c>. <b>When that rollout-braking
/// item lands, these two arms should flip to asserting a landing</b> (no go-around, reaches
/// <c>LandingPhase</c>): they are a deliberate waypoint, not a weakened test.</para>
///
/// <para><b>What each arrival arm pins instead.</b> (1) The protection engaged on the follower at some
/// point in the window — the regression guard that the pass runs on scenario-scripted arrivals at all,
/// which is the whole feature. (2) The go-around is legitimate: at the tick the follower enters
/// <c>GoAroundPhase</c> the leader is demonstrably still on or leaving the runway (§3-10-3.a.1 — the
/// preceding aircraft must be clear before the succeeding one crosses the threshold), never already
/// taxiing clear. That is the assertion that catches the vacate projection regressing back toward
/// fail-open and suppressing a *correct* go-around. (3) The reduction measurably delayed the go-around
/// past the unprotected recorded second.</para>
///
/// Hybrid replay (docs/e2e-tdd-issue-debugging.md §5b): each arm restores the recorded snapshot at its
/// own T and steps forward with current code. A full replay from t=0 would re-simulate 1,200 s of
/// chaos-sensitive SFO ground traffic before reaching the assertion.
///
/// Each arm then applies the §5c cutoff technique, because the recording carries the instructor's own
/// correction for the very bug under test: they deleted both aircraft seconds after it sent them around
/// (<c>DEL WJA1508</c> at t=413, <c>DEL SKW5536</c> at t=728). Spacing the arrival necessarily moves its
/// touchdown later, so a straight <c>ReplayOneSecond</c> loop would run it into its own deletion and the
/// arm would fail for the wrong reason. Each arm therefore replays to a cutoff past the go-around moment
/// but before the delete, then switches to <c>TickOneSecond</c> — physics and phases only — to watch the
/// automatic behaviour. The leader still vacates under <c>TickOneSecond</c> because the rollout and exit
/// are phase-driven, and landing clearance is automatic in this session (<c>AutoClearedToLand</c>).
/// </summary>
public class SfoGroundControlArrivalGoAroundTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/sfo-gc-arrival-goarounds-recording.yaat-bug-report-bundle.zip";

    /// <summary>
    /// Second WJA1508 went around in the recorded, unprotected run (bundle terminal log, t=357). The arm asserts the
    /// protected run goes around strictly later than this, which is what "the reduction bought real time" means here.
    /// </summary>
    private const int RecordedGoAroundSecondWja1508 = 357;

    /// <summary>Second SKW5536 went around in the recorded, unprotected run (bundle terminal log, t=689).</summary>
    private const int RecordedGoAroundSecondSkw5536 = 689;

    /// <summary>
    /// What one arm watched over its window: every distinct phase the follower passed through, the second it first
    /// entered <c>GoAroundPhase</c> (null when it never did), the leader's phase at that same second, and whether the
    /// same-runway protection ever owned a speed ceiling on the follower.
    /// </summary>
    private sealed record ArmTrace(HashSet<string> FollowerPhases, int? GoAroundSecond, string? LeaderPhaseAtGoAround, bool ProtectionEngaged);

    /// <summary>
    /// Restores the snapshot at <paramref name="restoreAt"/>, replays recorded actions second-by-second
    /// through <paramref name="replayUntil"/>, then ticks physics and phases alone through
    /// <paramref name="endAt"/> so the instructor's own corrective <c>DEL</c> is never applied. Watches
    /// <paramref name="callsign"/> and — when one is named — <paramref name="leaderCallsign"/>, the arrival ahead of
    /// it on the same runway. Returns null when the fixture or navdata is unavailable (silent skip).
    /// </summary>
    private ArmTrace? Observe(string callsign, string? leaderCallsign, int restoreAt, int replayUntil, int endAt)
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return null;
        }

        using (archive)
        {
            TestVnasData.EnsureInitialized();
            if (TestVnasData.NavigationDb is null)
            {
                return null;
            }

            SimLogBuilder.CreateForTest(output).EnableCategory("OccupiedRunwayGoAround", LogLevel.Debug).InitializeSimLog();

            var recording = archive.ToBaseSessionRecording();
            var engine = new SimulationEngine(new TestAirportGroundData());
            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(restoreAt);
            if (snapshot is null)
            {
                return null;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int? goAroundSecond = null;
            string? leaderPhaseAtGoAround = null;
            bool protectionEngaged = false;

            var pre = engine.FindAircraft(callsign);
            if (pre?.Phases?.CurrentPhase is { } startPhase)
            {
                seen.Add(startPhase.GetType().Name);
            }
            output.WriteLine(
                $"t={restoreAt}: {callsign} phase={pre?.Phases?.CurrentPhase?.GetType().Name ?? "(none)"} "
                    + $"alt={pre?.Altitude:F0} ias={pre?.IndicatedAirspeed:F0}"
            );

            for (int t = restoreAt + 1; t <= endAt; t++)
            {
                if (t <= replayUntil)
                {
                    engine.ReplayOneSecond();
                }
                else
                {
                    engine.TickOneSecond();
                }

                var ac = engine.FindAircraft(callsign);
                if (ac is null)
                {
                    continue;
                }

                if (ac.Approach.SameRunwayProtectionCeilingKts is { } ceiling)
                {
                    if (!protectionEngaged)
                    {
                        output.WriteLine($"t={t}: {callsign} same-runway protection engaged, ceiling {ceiling:F0} kt");
                    }
                    protectionEngaged = true;
                }

                if (ac.Phases?.CurrentPhase is not { } phase)
                {
                    continue;
                }

                string name = phase.GetType().Name;
                if (seen.Add(name))
                {
                    output.WriteLine($"t={t}: {callsign} entered {name} (alt={ac.Altitude:F0}, ias={ac.IndicatedAirspeed:F0})");
                }

                if (goAroundSecond is null && name.Contains("GoAround", StringComparison.Ordinal))
                {
                    goAroundSecond = t;
                    var leader = leaderCallsign is null ? null : engine.FindAircraft(leaderCallsign);
                    leaderPhaseAtGoAround = leader?.Phases?.CurrentPhase?.GetType().Name ?? "(not in the world)";
                    output.WriteLine($"t={t}: {callsign} went around; {leaderCallsign ?? "(no leader watched)"} phase={leaderPhaseAtGoAround}");
                }
            }

            output.WriteLine($"{callsign} phases seen t={restoreAt}..{endAt}: {string.Join(", ", seen.OrderBy(n => n, StringComparer.Ordinal))}");
            return new ArmTrace(seen, goAroundSecond, leaderPhaseAtGoAround, protectionEngaged);
        }
    }

    /// <summary>
    /// Restores at <paramref name="restoreAt"/> and returns every distinct current-phase type name
    /// <paramref name="callsign"/> passed through, or null when the fixture or navdata is unavailable.
    /// </summary>
    private HashSet<string>? CollectPhases(string callsign, int restoreAt, int replayUntil, int endAt) =>
        Observe(callsign, leaderCallsign: null, restoreAt, replayUntil, endAt)?.FollowerPhases;

    private static bool HasGoAround(HashSet<string> phases) => phases.Any(n => n.Contains("GoAround", StringComparison.Ordinal));

    /// <summary>
    /// The three things an arrival-behind-arrival arm pins: the protection ran on this follower, the go-around it
    /// could not prevent was legal (§3-10-3.a.1 — the leader was still on the pavement), and the reduction pushed it
    /// past the second it fired at in the unprotected recorded run.
    /// </summary>
    private static void AssertSpacedAndLegitimate(ArmTrace trace, string follower, string leader, string runway, int recordedGoAroundSecond)
    {
        string seen = string.Join(", ", trace.FollowerPhases.OrderBy(n => n, StringComparer.Ordinal));

        Assert.True(
            trace.ProtectionEngaged,
            $"The same-runway arrival protection never engaged on {follower} ({runway}) — a scenario-scripted arrival "
                + $"stream is running with no in-trail management at all. Phases seen: {seen}."
        );

        Assert.True(
            trace.GoAroundSecond is not null,
            $"{follower} never went around behind {leader} on {runway}. That is better than this arm pins: the "
                + $"rollout-braking retune (docs/plans/MAIN.md) has evidently landed and runway occupancy now fits "
                + $"inside the delivered interval. Flip this arm to assert the landing — no go-around, reaches "
                + $"LandingPhase — and delete the recorded-baseline constant. Phases seen: {seen}."
        );

        Assert.True(
            trace.LeaderPhaseAtGoAround is "LandingPhase" or "RunwayExitPhase",
            $"{follower} went around at t={trace.GoAroundSecond} while {leader} was already in "
                + $"{trace.LeaderPhaseAtGoAround} — clear of {runway}. A go-around for an aircraft that has vacated is "
                + $"not a §3-10-3.a.1 trigger; the vacate projection has regressed toward reporting the runway "
                + $"occupied when it is not."
        );

        Assert.True(
            trace.GoAroundSecond > recordedGoAroundSecond,
            $"{follower} went around at t={trace.GoAroundSecond}, no later than the t={recordedGoAroundSecond} of the "
                + $"unprotected recorded run — the speed reduction bought no time, so the protection is engaging too "
                + $"late, or its ceiling is a no-op against the scheduled profile."
        );
    }

    /// <summary>
    /// WJA1508 behind SKW3398 on 28R. Recorded actions stop at t=410 — the instructor deletes WJA1508 at t=413 — and
    /// the arm then ticks on to t=470 so the spaced arrival's own outcome, not the deletion, is what is observed.
    /// </summary>
    [Fact]
    public void WJA1508_IsSpacedAndItsGoAroundStaysLegitimate_28R()
    {
        var trace = Observe("WJA1508", leaderCallsign: "SKW3398", restoreAt: 125, replayUntil: 410, endAt: 470);
        if (trace is null)
        {
            return;
        }

        AssertSpacedAndLegitimate(trace, "WJA1508", "SKW3398", "28R", RecordedGoAroundSecondWja1508);
    }

    /// <summary>
    /// SKW5536 behind UAL2627 on 28L. Recorded actions stop at t=725 — the instructor deletes SKW5536 at t=728 — and
    /// the arm then ticks on to t=790.
    /// </summary>
    [Fact]
    public void SKW5536_IsSpacedAndItsGoAroundStaysLegitimate_28L()
    {
        var trace = Observe("SKW5536", leaderCallsign: "UAL2627", restoreAt: 500, replayUntil: 725, endAt: 790);
        if (trace is null)
        {
            return;
        }

        AssertSpacedAndLegitimate(trace, "SKW5536", "UAL2627", "28L", RecordedGoAroundSecondSkw5536);
    }

    /// <summary>
    /// Control arm: SKW5416 must still go around for the student's own <c>CROSS 28L</c> (t=1092).
    /// </summary>
    [Fact]
    public void SKW5416_StillGoesAroundForStudentCrossing_28L()
    {
        var phases = CollectPhases("SKW5416", restoreAt: 1040, replayUntil: 1130, endAt: 1130);
        if (phases is null)
        {
            return;
        }

        string seen = string.Join(", ", phases.OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(
            HasGoAround(phases),
            $"SKW5416 did not go around for SWA2644 crossing 28L in front of it — that trigger is legitimate. Phases seen: {seen}."
        );
    }
}
