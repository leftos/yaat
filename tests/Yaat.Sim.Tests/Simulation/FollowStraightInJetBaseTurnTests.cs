using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for the late base turn behind a fast straight-in lead.
///
/// Recording: S2-OAK-5 (2) "Practical Exam Preparation / Advanced Concepts" (ZOA, OAK 28R).
/// N629PU (C172) is on an extended left downwind at ~415 ft. At t=1889 the instructor issues
/// <c>RTIS N7036Q</c> and at t=1930 <c>FOLLOW</c>; N7036Q is an LJ60 on a straight-in final at
/// 125 kt, ~1.3 nm from the threshold, and passes abeam the follower about ten seconds later.
///
/// Observed: the sequencing hold compared instantaneous along-track positions against the
/// 3.0 nm jet spacing, so N629PU kept flying downwind and only turned base at t=1995 — the tick
/// the LJ60 touched down, ~3 nm past the threshold abeam point.
///
/// Expected: a pilot who accepts FOLLOW sequences by projection (7110.65 §3-10-6 anticipating
/// separation; AIM §4-4-14.b in-trail responsibility). Once the Learjet is aft of the follower's
/// 3-9 line and the follower's own threshold ETA falls comfortably after the Learjet's touchdown
/// plus its runway-clearance time, the base turn is flown — while the lead is still airborne.
/// The follower must still cross the threshold only after the Category III lead is clear of the
/// runway (§3-10-3.a.1), which is what the touchdown-interval assertion pins.
///
/// The recording carries the instructor's own later commands (GA at t=2142); the assertion window
/// ends well before them and ticks physics-only after the FOLLOW / TG pair.
/// </summary>
public class FollowStraightInJetBaseTurnTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak5-follow-heli-recording.zip";
    private const string Follower = "N629PU";
    private const string Leader = "N7036Q";

    // Restore just before the FOLLOW (t=1930) and replay through the TG (t=1931).
    private const int RestoreAtSeconds = 1925;
    private const int ReplayStopSeconds = 1935;

    // The Learjet passes abeam at ~t=1940; a base turn later than this is the old behaviour
    // (t=1995, at touchdown) rather than sequencing by projection.
    private const int LatestAcceptableBaseTurn = 1970;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void N629PU_TurnsBase_WhileStraightInJetIsStillAirborne()
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
            var snapshot = archive.ReadSnapshotAt(RestoreAtSeconds);
            if (snapshot is null)
            {
                output.WriteLine($"No snapshot near t={RestoreAtSeconds} — skipping");
                return;
            }

            engine.RestoreFromSnapshot(snapshot.State);
            int t0 = (int)snapshot.ElapsedSeconds;

            var pre = engine.FindAircraft(Follower);
            Assert.NotNull(pre);
            Assert.IsType<DownwindPhase>(pre.Phases?.CurrentPhase);

            for (int t = t0 + 1; t <= ReplayStopSeconds; t++)
            {
                engine.ReplayOneSecond();
            }

            var afterFollow = engine.FindAircraft(Follower);
            Assert.NotNull(afterFollow);
            Assert.Equal(Leader, afterFollow.Approach.FollowingCallsign);

            int? leftDownwindAt = null;
            bool leadAirborneWhenLeftDownwind = false;
            bool followActiveWhenLeftDownwind = false;
            int? leadTouchdownAt = null;
            int? followerTouchdownAt = null;
            bool leadAheadWhenFollowerRolledOut = true;
            bool rolledOutObserved = false;
            bool followerWentAround = false;

            for (int t = ReplayStopSeconds + 1; t <= ReplayStopSeconds + 240; t++)
            {
                engine.TickOneSecond();
                var f = engine.FindAircraft(Follower);
                var l = engine.FindAircraft(Leader);
                if (f is null || l is null)
                {
                    break;
                }

                if (leadTouchdownAt is null && l.IsOnGround)
                {
                    leadTouchdownAt = t;
                }

                if (leftDownwindAt is null && f.Phases?.CurrentPhase is not DownwindPhase)
                {
                    leftDownwindAt = t;
                    leadAirborneWhenLeftDownwind = !l.IsOnGround;
                    followActiveWhenLeftDownwind = string.Equals(f.Approach.FollowingCallsign, Leader, StringComparison.OrdinalIgnoreCase);
                    output.WriteLine(
                        $"t={t}: {Follower} left Downwind into {f.Phases?.CurrentPhase?.GetType().Name}; "
                            + $"{Leader} onGround={l.IsOnGround} alt={l.Altitude:F0} foll={f.Approach.FollowingCallsign ?? "-"}"
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
                        $"t={t}: {Follower} rolled out on final {followerDist:F2} nm out; " + $"{Leader} onGround={l.IsOnGround} at {leadDist:F2} nm"
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
                        $"t={t}: {Follower} touched down ({f.Phases?.CurrentPhase?.GetType().Name}); " + $"lead touched down at t={leadTouchdownAt}"
                    );
                    break;
                }
            }

            Assert.True(leftDownwindAt is not null, $"{Follower} never left Downwind within the tick window.");
            Assert.True(
                leadAirborneWhenLeftDownwind,
                $"{Follower} turned base at t={leftDownwindAt} only after {Leader} was on the ground — traffic in the landing phase is no "
                    + "longer a factor (AIM §4-4-14.a.2 NOTE); the follower sequences by projecting threshold ETAs (the pilot-side mirror of "
                    + "7110.65 §3-10-6), not by waiting for the lead's touchdown."
            );
            Assert.True(
                leftDownwindAt <= LatestAcceptableBaseTurn,
                $"{Follower} turned base at t={leftDownwindAt}; the Learjet passed abeam at ~t=1940 and the projection releases the "
                    + $"turn within seconds of that, so the turn must come by t={LatestAcceptableBaseTurn}."
            );
            Assert.True(followActiveWhenLeftDownwind, "The FOLLOW must still be active when the follower turns base behind the lead.");
            Assert.True(leadTouchdownAt is not null, $"{Leader} never touched down within the tick window.");
            Assert.True(rolledOutObserved, $"{Follower} never rolled out on final within the tick window.");
            Assert.True(
                leadAheadWhenFollowerRolledOut,
                $"{Follower} rolled out on final ahead of {Leader} — it must roll out behind the traffic it was told to follow."
            );
            Assert.False(
                followerWentAround,
                $"{Follower} was sent around after sequencing itself behind {Leader} — the runway-clearance allowance the projection used must "
                    + "exceed the simulated runway occupancy, or a pilot who did everything right is forced around "
                    + "(OccupiedRunwayGoAround, §3-10-3)."
            );
            Assert.True(followerTouchdownAt is not null, $"{Follower} never touched down within the tick window.");
            double clearance = AirborneFollowHelper.RunwayClearanceSeconds(AircraftCategory.Jet);
            Assert.True(
                followerTouchdownAt - leadTouchdownAt >= clearance,
                $"{Follower} touched down {followerTouchdownAt - leadTouchdownAt} s after the Category III lead; the projection promised at "
                    + $"least the jet runway-occupancy allowance ({clearance:F0} s) so the Learjet is clear of the runway (7110.65 §3-10-3.a.1)."
            );
        }
    }
}
