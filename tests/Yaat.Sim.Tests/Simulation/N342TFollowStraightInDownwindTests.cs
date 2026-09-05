using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Phases;
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
/// the speed loop holds at min speed instead of cancelling, and DownwindPhase holds
/// the base turn until the jet has passed the follower's 3-9 line and the projected
/// threshold ETAs show the runway clear when N342T arrives (7110.65 §3-10-6). The
/// follower then turns base BEHIND the jet — while it may still be airborne — and
/// crosses the threshold only after the Category III lead has had time to clear the
/// runway (7110.65 §3-10-3.a.1: no reduced separation landing behind a Category III).
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
    /// The core assertion: N342T must not cut in front of N70CS. Pre-fix the follow
    /// cancels at min speed (~t=1003) and N342T turns base with the jet still forward
    /// of its wingline; post-fix it turns base only once the jet has passed, rolls out
    /// on final behind a landed jet, and reaches the runway at least the jet
    /// runway-clearance allowance after the jet's touchdown.
    /// </summary>
    [Fact]
    public void N342T_SequencesBehind_StraightInJet()
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
            bool leadAftOfWinglineWhenLeftDownwind = false;
            bool leadAheadWhenFollowerRolledOut = true;
            bool rolledOutObserved = false;
            bool followerWentAround = false;
            int? leadTouchdownAt = null;
            int? followerTouchdownAt = null;

            for (int t = ReplayStopSeconds + 1; t <= ReplayStopSeconds + 300; t++)
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

                if (leadTouchdownAt is null && l.IsOnGround)
                {
                    leadTouchdownAt = t;
                }

                if (leftDownwindAt is null && f.Phases?.CurrentPhase is not DownwindPhase)
                {
                    leftDownwindAt = t;
                    // The gate releases at |relative bearing| > 90°; by the first Base tick the follower's
                    // heading has already swung a few degrees into the turn, so allow that much.
                    double relativeBearing = GeoMath.SignedBearingDifference(f.TrueHeading.Degrees, GeoMath.BearingTo(f.Position, l.Position));
                    leadAftOfWinglineWhenLeftDownwind = Math.Abs(relativeBearing) >= 85.0;
                    output.WriteLine(
                        $"t={t}: N342T left Downwind into {f.Phases?.CurrentPhase?.GetType().Name}; "
                            + $"N70CS onGround={l.IsOnGround} alt={l.Altitude:F0} relBrg={relativeBearing:F0} "
                            + $"foll={f.Approach.FollowingCallsign ?? "-"}"
                    );
                }

                // No cut-in: when the follower rolls out on final the lead must be on the ground or
                // nearer the threshold than the follower (14 CFR §91.113(g); AIM §4-3-4.d).
                if (!rolledOutObserved && f.Phases?.CurrentPhase is FinalApproachPhase && f.Phases.AssignedRunway is { } rwy)
                {
                    rolledOutObserved = true;
                    var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
                    double leadDist = GeoMath.DistanceNm(l.Position, threshold);
                    double followerDist = GeoMath.DistanceNm(f.Position, threshold);
                    leadAheadWhenFollowerRolledOut = l.IsOnGround || (leadDist < followerDist);
                    output.WriteLine(
                        $"t={t}: N342T rolled out on final {followerDist:F2} nm out; " + $"N70CS onGround={l.IsOnGround} at {leadDist:F2} nm"
                    );
                }

                if (f.Phases?.CurrentPhase is GoAroundPhase)
                {
                    followerWentAround = true;
                }

                if (followerTouchdownAt is null && f.IsOnGround)
                {
                    followerTouchdownAt = t;
                    output.WriteLine(
                        $"t={t}: N342T touched down ({f.Phases?.CurrentPhase?.GetType().Name}); " + $"N70CS touched down at t={leadTouchdownAt}"
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
                leadAftOfWinglineWhenLeftDownwind,
                $"N342T turned base at t={leftDownwindAt} with N70CS still forward of its wingline — cutting in front of the landing jet "
                    + "(14 CFR §91.113(g)). It must hold the base turn until the jet has passed."
            );
            Assert.True(leadTouchdownAt is not null, "N70CS never touched down within the tick window.");
            Assert.True(rolledOutObserved, "N342T never rolled out on final within the tick window.");
            Assert.True(
                leadAheadWhenFollowerRolledOut,
                "N342T rolled out on final ahead of N70CS — it must roll out behind the jet it was told to follow."
            );
            Assert.True(followerTouchdownAt is not null, "N342T never touched down within the tick window.");
            Assert.True(followerTouchdownAt > leadTouchdownAt, "N342T must land after the jet it was told to follow.");
            // §3-10-3 itself is enforced by OccupiedRunwayGoAround at short final: a follower that sequenced correctly must
            // never be sent around by it, which is the authoritative "runway was clear when it arrived" check.
            Assert.False(
                followerWentAround,
                "N342T was sent around after sequencing itself behind N70CS — the jet was still on the runway when N342T arrived "
                    + "(7110.65 §3-10-3.a.1)."
            );
        }
    }
}
