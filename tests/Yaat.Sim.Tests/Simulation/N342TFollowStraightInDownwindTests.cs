using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the FOLLOW cut-in bug (RC1).
///
/// Recording: S2-OAK-4 "VFR Transitions / Radar Concepts" (ZOA), OAK closed
/// traffic. N342T (DA42, light twin) is on Downwind doing touch-and-goes. At
/// t=957 the user issued <c>RTIS N70CS</c> and at t=984 <c>FOLLOW</c> (bare —
/// resolves to the last reported traffic, N70CS). N70CS is a C25C (jet) on a
/// straight-in FinalApproach to the SAME runway (28L), about to land.
///
/// Observed bug: the follower cannot open the jet-category 3.0 nm spacing by
/// speed alone, so it slowed to minimum approach speed. The at-min-speed cancel
/// in <see cref="AirborneFollowHelper.ComputeAdjustedSpeedWithDesired"/> then
/// fired ("unable to maintain separation") and cleared FollowingCallsign at
/// ~t=1003 — so N342T would turn base at its normal point while N70CS was still
/// airborne on final, cutting in front of landing traffic (STCA fired). In the
/// live session the user hand-corrected with <c>ELB 28L 1</c> at t=1026.
///
/// Expected after fix: while the lead is pattern-flow-ahead on the same runway,
/// the speed loop holds at min speed instead of cancelling; DownwindPhase
/// extends the downwind (holds the base turn); and only once N70CS is on the
/// ground does the lead-lifecycle release the follow so N342T turns base BEHIND
/// the landed traffic. 7110.65 §3-10-3.a.1 (no reduced separation landing behind
/// a Category III jet — the preceding aircraft must be clear of the runway).
///
/// The assertion replays through the FOLLOW (past the ~t=1003 cancel point) and
/// then switches to physics-only ticking so the user's recorded ELB correction
/// (t=1026) does not mask the automatic follow behavior.
/// </summary>
public class N342TFollowStraightInDownwindTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/follow-straightin-sequencing-recording.zip";
    private const string Follower = "N342T";
    private const string Leader = "N70CS";

    // Replay through the bare FOLLOW (t=984) and past the ~t=1003 pre-fix cancel
    // point, but stop before the user's recorded ELB 28L 1 correction (t=1026).
    private const int ReplayStopSeconds = 1010;

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
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Replays through FOLLOW then ticks physics-only, logging N342T and N70CS
    /// each second so the follow / base-turn / lead-landing sequence is visible.
    /// </summary>
    [Fact]
    public void Diagnostic_LogFollowAndBaseTurn()
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

            engine.Replay(recording, 0);
            var snapshot = archive.ReadSnapshotAt(982);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=982 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int t0 = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"Restored snapshot at t={t0}");

            for (int t = t0 + 1; t <= ReplayStopSeconds; t++)
            {
                engine.ReplayOneSecond();
            }

            for (int t = ReplayStopSeconds + 1; t <= ReplayStopSeconds + 110; t++)
            {
                engine.TickOneSecond();
                var f = engine.FindAircraft(Follower);
                var l = engine.FindAircraft(Leader);
                if (f is null)
                {
                    break;
                }
                string lead = l is null ? "(gone)" : $"{l.Phases?.CurrentPhase?.GetType().Name} onGnd={l.IsOnGround} alt={l.Altitude:F0}";
                output.WriteLine(
                    $"t={t} {f.Phases?.CurrentPhase?.GetType().Name} foll={f.Approach.FollowingCallsign ?? "-"} "
                        + $"ias={f.IndicatedAirspeed:F0} | N70CS: {lead}"
                );
            }
        }
    }

    /// <summary>
    /// The core assertion: N342T must not turn base (leave Downwind) until N70CS
    /// is on the ground. Pre-fix the follow cancels at min speed (~t=1003) and
    /// N342T turns base while N70CS is still airborne on final; post-fix it holds
    /// the downwind and only turns base after N70CS has landed.
    /// </summary>
    [Fact]
    public void N342T_HoldsDownwind_UntilStraightInLands()
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

            engine.Replay(recording, 0);
            var snapshot = archive.ReadSnapshotAt(982);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=982 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int t0 = (int)snapshot.ElapsedSeconds;

            // Sanity: at the restored snapshot N342T is on Downwind and the FOLLOW
            // has not fired yet (it is at t=984).
            var pre = engine.FindAircraft(Follower);
            Assert.NotNull(pre);
            Assert.IsType<DownwindPhase>(pre.Phases?.CurrentPhase);

            // Replay through the FOLLOW and past the pre-fix cancel point.
            for (int t = t0 + 1; t <= ReplayStopSeconds; t++)
            {
                engine.ReplayOneSecond();
            }

            // Physics-only from here so the user's recorded ELB correction (t=1026)
            // does not force the base turn and mask the automatic follow behavior.
            bool followActiveWhileLeadAirborne = false;
            int? leftDownwindAt = null;
            bool leadOnGroundWhenLeftDownwind = false;

            for (int t = ReplayStopSeconds + 1; t <= ReplayStopSeconds + 120; t++)
            {
                engine.TickOneSecond();
                var f = engine.FindAircraft(Follower);
                var l = engine.FindAircraft(Leader);
                if (f is null || l is null)
                {
                    break;
                }

                if (string.Equals(f.Approach.FollowingCallsign, Leader, StringComparison.OrdinalIgnoreCase) && !l.IsOnGround)
                {
                    followActiveWhileLeadAirborne = true;
                }

                if (f.Phases?.CurrentPhase is not DownwindPhase)
                {
                    leftDownwindAt = t;
                    leadOnGroundWhenLeftDownwind = l.IsOnGround;
                    output.WriteLine(
                        $"t={t}: N342T left Downwind into {f.Phases?.CurrentPhase?.GetType().Name}; "
                            + $"N70CS onGround={l.IsOnGround} alt={l.Altitude:F0} foll={f.Approach.FollowingCallsign ?? "-"}"
                    );
                    break;
                }
            }

            Assert.True(
                followActiveWhileLeadAirborne,
                "FOLLOW should stay active on N70CS while it is airborne on final — the at-min-speed cancel "
                    + "must not clear it (pre-fix it cancelled at ~t=1003)."
            );
            Assert.True(leftDownwindAt is not null, "N342T never left Downwind within the tick window.");
            Assert.True(
                leadOnGroundWhenLeftDownwind,
                $"N342T turned base at t={leftDownwindAt} while N70CS was still airborne — cutting in front of "
                    + "the landing jet. It must extend downwind and hold the base turn until N70CS is on the ground "
                    + "(7110.65 §3-10-3.a.1)."
            );
        }
    }
}
