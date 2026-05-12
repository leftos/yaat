using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the FOLLOW-after-EXT bug.
///
/// Recording: S2-OAK-4 "VFR Transitions / Radar Concepts" (ZOA), OAK left
/// traffic for 28R. At t=3429 the user issued <c>EXT</c> to N342T (DA42 on
/// Downwind for 28R) → <c>DownwindPhase.IsExtended=true</c>. At t=3528 the
/// user issued <c>FOLLOW</c>, intending it to target N10194 (C172 on
/// FinalApproach for 28R, westbound, descending).
///
/// Observed bug: <c>FOLLOW</c> only set <c>FollowingCallsign</c> and did not
/// clear <c>IsExtended</c>. <c>DownwindPhase.OnTick</c> short-circuits with
/// <c>return false</c> whenever <c>IsExtended</c> is true, so N342T stayed on
/// Downwind heading 112° eastbound for the remaining 90 s of the recording,
/// never turning base. The runaway-distance watchdog then auto-cancelled the
/// follow at t=3565 ("unable to catch up"), but N342T still rode Downwind to
/// the end of the bundle at t=3619.
///
/// Expected after fix: FOLLOW supersedes the prior EXT. <c>IsExtended</c>
/// clears, the normal base-turn check fires (aircraft is already past
/// <c>_baseTurnAlongTrack</c>), the phase completes within a couple of ticks,
/// and N342T proceeds to Base/Final to land #2 behind N10194.
/// </summary>
public class N342TFollowAfterExtendTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/n342t-follow-after-extend-recording.yaat-bug-report-bundle.zip";
    private const string Follower = "N342T";
    private const string Leader = "N10194";

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
            .EnableCategory("CommandDispatcher", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void FollowAfterExtend_LeavesDownwind_AndKeepsFollowing()
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

            // Hybrid replay: replaying from t=0 across 3500+ s of pattern work
            // accumulates micro-divergences (RNG-sensitive dispatch in the
            // scenario plus dozens of pattern circuits) that would land the
            // phase chain in a different leg by t=3527. Pin the pre-FOLLOW
            // state to what the user actually saw via the snapshot stream.
            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(3527);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=3527 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int snapshotTime = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"Restored snapshot at t={snapshotTime}");

            // Sanity: at the restored snapshot the follower is on extended Downwind
            // (the EXT at t=3429 set IsExtended; it has not been cleared since).
            var preFollower = engine.FindAircraft(Follower);
            Assert.NotNull(preFollower);
            var preCurrent = preFollower.Phases?.CurrentPhase;
            Assert.IsType<DownwindPhase>(preCurrent);
            Assert.True(
                ((DownwindPhase)preCurrent!).IsExtended,
                $"Snapshot at t={snapshotTime} should have DownwindPhase.IsExtended=true (set by EXT at t=3429)"
            );

            // Step second-by-second from the restored snapshot through the FOLLOW
            // action at t=3528 and onward. Record the first tick at which the
            // follower leaves Downwind. The fix should land us in Base on the
            // tick after FOLLOW fires because the aircraft is well past
            // _baseTurnAlongTrack at t=3528.
            int? leftDownwindAt = null;
            string? leftDownwindInto = null;
            for (int dt = 1; dt <= 65; dt++)
            {
                engine.ReplayOneSecond();
                int now = snapshotTime + dt;
                var ac = engine.FindAircraft(Follower);
                Assert.NotNull(ac);
                var phase = ac.Phases?.CurrentPhase;
                if (phase is not DownwindPhase)
                {
                    leftDownwindAt = now;
                    leftDownwindInto = phase?.GetType().Name ?? "(null)";
                    output.WriteLine(
                        $"t={now}: left Downwind into {leftDownwindInto} "
                            + $"following={ac.Approach.FollowingCallsign ?? "(null)"} "
                            + $"hdg={ac.TrueHeading.Degrees:F0} alt={ac.Altitude:F0}"
                    );
                    break;
                }
            }

            var post = engine.FindAircraft(Follower);
            Assert.NotNull(post);

            Assert.True(
                leftDownwindAt is not null,
                $"N342T should leave Downwind within 65 s of the restored snapshot. "
                    + $"At t={snapshotTime + 65}: phase={post.Phases?.CurrentPhase?.GetType().Name ?? "(null)"} "
                    + $"hdg={post.TrueHeading.Degrees:F0} "
                    + $"following={post.Approach.FollowingCallsign ?? "(null)"}"
            );

            Assert.True(
                post.Phases?.CurrentPhase is BasePhase or FinalApproachPhase or LandingPhase,
                $"Expected Base/Final/Landing after leaving Downwind, got {leftDownwindInto}"
            );

            Assert.Equal(Leader, post.Approach.FollowingCallsign);
        }
    }
}
