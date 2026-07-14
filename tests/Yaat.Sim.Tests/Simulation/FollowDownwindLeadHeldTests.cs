using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the FOLLOW-behind-a-held-downwind-lead bug.
///
/// Recording: S2-OAK-3 (2) "VFR Sequencing" (ZOA), OAK 28R right traffic.
/// N2BP (SR22) is on Downwind told to <c>FOLLOW N172SP</c> (t=1127). The lead
/// N172SP (C172) is ~1 nm ahead on the SAME downwind and holding it out: it was
/// EXT'd at t=1092, then told to <c>FOLLOW N80ZU</c> (t=1144), which CLEARED its
/// <c>DownwindPhase.IsExtended</c> flag. N172SP keeps holding out on the downwind
/// via its own follow-hold (behind N80ZU, on final) and does not turn base until
/// t=1180.
///
/// Observed bug: N2BP's downwind sequence-hold is gated on
/// <c>IsLeadPatternFlowAhead</c>, whose same-leg exception fired only for a lead
/// with <c>IsExtended==true</c>. Once N172SP's flag cleared at t=1145, N2BP no
/// longer treated it as flow-ahead, released its hold, and turned base at its
/// normal point (t=1155) — rolling out on final AHEAD of the aircraft it was told
/// to follow (an overtake / broken sequence, AIM §4-3-5). The controller had to
/// manually <c>EXT DOWNWIND</c> N2BP at t=1164 to re-sequence it.
///
/// Expected after fix: a same-leg lead still on the downwind but past its own
/// base-turn point (holding out for ANY reason) counts as flow-ahead, so N2BP
/// extends its downwind and turns base only AFTER N172SP has turned base.
///
/// The continuation uses <see cref="SimulationEngine.TickOneSecond"/> (physics
/// only), NOT <see cref="SimulationEngine.ReplayOneSecond"/>, so the user's own
/// t=1164 EXT DOWNWIND correction is not applied — the test exercises N2BP's
/// automatic follow behavior in isolation.
/// </summary>
public class FollowDownwindLeadHeldTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/follow-downwind-lead-held-recording.yaat-bug-report-bundle.zip";
    private const string Follower = "N2BP";
    private const string Lead = "N172SP";
    private const string LeadsLead = "N80ZU";

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

    [Fact]
    public void Follower_DoesNotTurnBaseAheadOf_HeldDownwindLead()
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

            // Pin the state the user actually saw just before N2BP would turn base:
            // both aircraft on the 28R downwind, N2BP following N172SP, N172SP held
            // out past its base turn with IsExtended already cleared (t=1145).
            var snapshot = archive.ReadSnapshotAt(1150);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=1150 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int snapshotTime = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"Restored snapshot at t={snapshotTime}");

            // Sanity: the exact bug setup.
            var preFollower = engine.FindAircraft(Follower);
            var preLead = engine.FindAircraft(Lead);
            Assert.NotNull(preFollower);
            Assert.NotNull(preLead);
            Assert.IsType<DownwindPhase>(preFollower.Phases?.CurrentPhase);
            Assert.IsType<DownwindPhase>(preLead.Phases?.CurrentPhase);
            Assert.Equal(Lead, preFollower.Approach.FollowingCallsign);
            Assert.Equal(LeadsLead, preLead.Approach.FollowingCallsign);
            Assert.False(
                ((DownwindPhase)preLead.Phases!.CurrentPhase!).IsExtended,
                "Trigger precondition: N172SP's IsExtended was cleared when it was told to FOLLOW N80ZU"
            );

            // Tick physics forward. Latch the first second each aircraft leaves
            // Downwind for Base/Final/Landing, and what the lead was doing when the
            // follower turned base.
            int? followerBaseAt = null;
            int? leadBaseAt = null;
            string leadPhaseWhenFollowerTurnedBase = "(none)";

            for (int dt = 1; dt <= 200; dt++)
            {
                engine.TickOneSecond();
                int now = snapshotTime + dt;

                var lead = engine.FindAircraft(Lead);
                if (leadBaseAt is null && IsBaseOrLater(lead))
                {
                    leadBaseAt = now;
                    output.WriteLine($"t={now}: {Lead} turned base");
                }

                var follower = engine.FindAircraft(Follower);
                if (followerBaseAt is null && IsBaseOrLater(follower))
                {
                    followerBaseAt = now;
                    leadPhaseWhenFollowerTurnedBase = lead?.Phases?.CurrentPhase?.GetType().Name ?? "(gone)";
                    output.WriteLine($"t={now}: {Follower} turned base while {Lead} was on {leadPhaseWhenFollowerTurnedBase}");
                    break;
                }
            }

            Assert.True(followerBaseAt is not null, $"{Follower} never turned base within 200 s — possible deadlock in the follow hold");

            // The core invariant: the follower must not turn base ahead of the lead
            // it was told to follow. With the bug, N2BP turns base ~t=1155 while
            // N172SP is still on Downwind; with the fix, N172SP turns base first.
            Assert.True(
                leadBaseAt is not null && leadBaseAt <= followerBaseAt,
                $"OVERTAKE: {Follower} turned base at t={followerBaseAt} while its lead {Lead} "
                    + $"was still on {leadPhaseWhenFollowerTurnedBase} (leadBaseAt={leadBaseAt?.ToString() ?? "never"}). "
                    + $"FOLLOW must keep {Follower} behind {Lead} until it is safe to turn base behind it."
            );
        }
    }

    private static bool IsBaseOrLater(AircraftState? ac) => ac?.Phases?.CurrentPhase is BasePhase or FinalApproachPhase or LandingPhase;
}
