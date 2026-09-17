using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E arms over the <c>sfo-gc-arrival-goarounds</c> bundle — a <c>S1-SFO-2 | Ground Control 28/01</c>
/// session flown from SFO_GND with <c>AutoGoAroundOnOccupiedRunway</c> on. Three
/// <c>OccupiedRunwayGoAround</c> triggers fired in the recorded run; this class pins which of them the
/// sim should still produce, and — for the two arrival-behind-arrival cases — that the simulated TRACON
/// and the simulated tower between them now deliver the arrival to a landing instead.
///
/// <list type="bullet">
/// <item><b>WJA1508 (28R)</b> — sent around at t=357 in the recorded run with SKW3398 still rolling out
/// 4,705 ft down the runway. Spaced from 12 nm out, it now lands.</item>
/// <item><b>SKW5536 (28L)</b> — the same shape at t=689 behind UAL2627. It now lands too.</item>
/// <item><b>SKW5416 (28L, control)</b> — sent around at t=1109 because the student issued
/// <c>CROSS 28L</c> to SWA2644 at t=1092 with SKW5416 1.2 nm from the threshold. That is a legitimate,
/// student-caused incursion and the go-around must keep firing.</item>
/// </list>
///
/// <para><b>The two levers that close the gap.</b> This scenario delivers those pairs ~53–57 s in trail
/// against a ~76.6 s required threshold interval, and speed is the only actuator the simulated TRACON
/// has — so where it may start reducing, and how low it may go, is the whole budget.
/// (1) <b>Before the approach clearance.</b> WJA1508's preset is
/// <c>CFIX CEPIN 3000 210; CAPP 28R; AT CEPIN SPD 180 AXMUL</c>: the CAPP waits in the command queue
/// behind the fix condition and did not fire until t=175, so a pass that waited for a clearance had ~12
/// nm of the 20→5 nm window left. The pass manages an arrival from
/// <c>SameRunwayArrivalProtection.PreClearanceRangeNm</c> in on its expected approach alone, and WJA1508
/// is now spaced from t=141 while it is still flying its route with no phase of its own.
/// (2) <b>Final approach speed.</b> Inside <c>SameRunwayArrivalProtection.TowerSpeedAuthorityNm</c> the
/// arrival is on the simulated local controller's frequency and configuring to land, so that controller
/// may say "reduce to final approach speed" (§5-7-3.f) rather than stopping at the §5-7-3.c.1.b 170-kt
/// floor — and keeps it through the §5-7-1.b.4 window, which forbids issuing a new adjustment inside 5
/// nm, not flying one already issued. SKW5536's preset is a bare <c>CAPP 28L</c>, so it is the second
/// lever alone that moves it.</para>
///
/// <para><b>What each arrival arm pins.</b> (1) The protection engaged on the follower — the regression
/// guard that the pass runs on scenario-scripted arrivals at all. (2) The simulated tower issued the
/// final-approach-speed instruction, which is the half of the authority that arrives inside 10 nm.
/// (3) The follower reaches <c>LandingPhase</c> and never enters a go-around: the delivery is legal under
/// §3-10-3.a.1 (the preceding aircraft is clear of the runway before the succeeding one crosses the
/// threshold) without the go-around having to catch it. The control arm below is what keeps this from
/// being bought by a go-around trigger that has gone fail-open.</para>
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
    /// What one arm watched over its window: every distinct phase the follower passed through, the second it first
    /// entered <c>GoAroundPhase</c> (null when it never did), the leader's phase at that same second, whether the
    /// same-runway protection ever owned a speed ceiling on the follower, and whether the simulated tower ever told
    /// it to reduce to final approach speed.
    /// </summary>
    private sealed record ArmTrace(
        HashSet<string> FollowerPhases,
        int? GoAroundSecond,
        string? LeaderPhaseAtGoAround,
        bool ProtectionEngaged,
        bool FasInstructed
    );

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

            // The restore overwrites the flag from the snapshot DTO, and this recording predates the setting.
            engine.Scenario!.AutoArrivalSpacingOnOccupiedRunway = true;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int? goAroundSecond = null;
            string? leaderPhaseAtGoAround = null;
            bool protectionEngaged = false;
            bool fasInstructed = false;

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

                if (ac.Approach.SameRunwayProtectionFasInstructed && !fasInstructed)
                {
                    fasInstructed = true;
                    string held = ac.Targets.SpeedCeiling?.ToString("F0") ?? "(none)";
                    output.WriteLine($"t={t}: {callsign} told to reduce to final approach speed, holding {held} kt");
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
            return new ArmTrace(seen, goAroundSecond, leaderPhaseAtGoAround, protectionEngaged, fasInstructed);
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
    /// What an arrival-behind-arrival arm pins: both levers ran on this follower — the simulated TRACON took its
    /// speed, and the simulated tower issued the final-approach-speed instruction inside 10 nm — and the delivery
    /// that produced is one the follower lands out of, with the go-around never firing. A go-around here is the
    /// regression: the leader was not clear of the runway in time (§3-10-3.a.1), which is what the spacing exists to
    /// prevent.
    /// </summary>
    private static void AssertSpacedAndLands(ArmTrace trace, string follower, string leader, string runway)
    {
        string seen = string.Join(", ", trace.FollowerPhases.OrderBy(n => n, StringComparer.Ordinal));

        Assert.True(
            trace.ProtectionEngaged,
            $"The same-runway arrival protection never engaged on {follower} ({runway}) — a scenario-scripted arrival "
                + $"stream is running with no in-trail management at all. Phases seen: {seen}."
        );

        Assert.True(
            trace.FasInstructed,
            $"The simulated tower never told {follower} to reduce to final approach speed. Inside "
                + $"SameRunwayArrivalProtection.TowerSpeedAuthorityNm that instruction is the rest of the authority "
                + $"this delivery needs (§5-7-3.f); without it the pass is back to the §5-7-3.c.1.b 170-kt floor. "
                + $"Phases seen: {seen}."
        );

        Assert.True(
            trace.FollowerPhases.Contains("LandingPhase"),
            $"{follower} never reached LandingPhase behind {leader} on {runway} — the spaced arrival did not land. " + $"Phases seen: {seen}."
        );

        Assert.True(
            trace.GoAroundSecond is null,
            $"{follower} went around at t={trace.GoAroundSecond} behind {leader} on {runway} ({leader} was in "
                + $"{trace.LeaderPhaseAtGoAround}). The two levers used to buy enough of the 20→5 nm window to land "
                + $"this arrival; something has given that authority back — engaging later, a ceiling that is a no-op "
                + $"against the scheduled profile, or the instruction being cancelled at the §5-7-1.b.4 window."
        );
    }

    /// <summary>
    /// WJA1508 behind SKW3398 on 28R. Recorded actions stop at t=410 — the instructor deletes WJA1508 at t=413 — and
    /// the arm then ticks on to t=470 so the spaced arrival's own outcome, not the deletion, is what is observed.
    /// </summary>
    [Fact]
    public void WJA1508_IsSpacedAndLands_28R()
    {
        var trace = Observe("WJA1508", leaderCallsign: "SKW3398", restoreAt: 125, replayUntil: 410, endAt: 470);
        if (trace is null)
        {
            return;
        }

        AssertSpacedAndLands(trace, "WJA1508", "SKW3398", "28R");
    }

    /// <summary>
    /// SKW5536 behind UAL2627 on 28L. Recorded actions stop at t=725 — the instructor deletes SKW5536 at t=728 — and
    /// the arm then ticks on to t=790.
    /// </summary>
    [Fact]
    public void SKW5536_IsSpacedAndLands_28L()
    {
        var trace = Observe("SKW5536", leaderCallsign: "UAL2627", restoreAt: 500, replayUntil: 725, endAt: 790);
        if (trace is null)
        {
            return;
        }

        AssertSpacedAndLands(trace, "SKW5536", "UAL2627", "28L");
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
