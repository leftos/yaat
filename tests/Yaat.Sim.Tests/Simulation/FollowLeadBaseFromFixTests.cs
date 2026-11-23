using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-2778. Recording: S2-OAK-3 (ZOA), seed 2052219157, trimmed to t=0, 625 and 1580. N805FM follows N775JW, which flies
/// DCT VPMOR VPCBT; ERB 28R. At t=628 the lead begins its right base to 28R at VPCBT. The follower, in trail on the pre-base
/// leg, is 1.26 nm runway-side of the lead's base start, past the base join's limit, and already past the downwind abeam
/// point. The downwind join used to be accepted anyway, building a pattern entry back to the abeam point 2.15 nm behind it:
/// a turn to 259.7T, against the 112.26T downwind. Expected: the downwind join is refused while the lead is on base and the
/// follower is past the abeam point, and the follower stays on the downwind behind the lead.
/// </summary>
public class FollowLeadBaseFromFixTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak3-follow-recording.zip";
    private const string Follower = "N805FM";
    private const string Lead = "N775JW";
    private const double DownwindTrueDeg = 112.26;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = RecordingGroundData.Evergreen("FOLLOW joins behind a lead turning base from a fix on today's OAK");
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("VfrFollowPhase", LogLevel.Debug)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
    }

    [Fact]
    public void InTrailFollower_LeadEntersBaseFromFix_NeverTurnsAgainstTheDownwind()
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            SessionRecording recording = archive.ToBaseSessionRecording();
            SimulationEngine? engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(recording, 0);
            TimedSnapshot? snapshot = archive.ReadSnapshotAt(625);
            Assert.NotNull(snapshot);
            engine.RestoreFromSnapshot(snapshot.State);
            Assert.Equal(625, (int)snapshot.ElapsedSeconds);

            var downwind = new TrueHeading(DownwindTrueDeg);
            int checkedSeconds = 0;
            int? leadBaseAt = null;
            for (int t = 626; t <= 660; t++)
            {
                engine.ReplayOneSecond();
                AircraftState follower = Assert.IsType<AircraftState>(engine.FindAircraft(Follower));
                AircraftState lead = Assert.IsType<AircraftState>(engine.FindAircraft(Lead));
                leadBaseAt ??= lead.Phases?.CurrentPhase is BasePhase ? t : null;
                Assert.False(
                    (follower.Phases?.CurrentPhase is BasePhase) && (leadBaseAt is null),
                    $"t={t}: {Follower} entered its base before {Lead} entered its own"
                );
                string phase = follower.Phases?.CurrentPhase?.GetType().Name ?? "(none)";
                if (follower.Targets.TargetTrueHeading is not { } target)
                {
                    output.WriteLine($"t={t}: {phase}, no target heading");
                    continue;
                }

                output.WriteLine(
                    $"t={t}: {phase}, target {target.Degrees:F1}T, heading {follower.TrueHeading.Degrees:F1}T; {InTrailMeasures(follower, engine)}"
                );
                Assert.True(
                    target.AbsAngleTo(downwind) <= 100.0,
                    $"t={t}: {Follower} on {phase} targets {target.Degrees:F1}T, against the {DownwindTrueDeg}T downwind"
                );
                checkedSeconds++;
            }

            Assert.True(checkedSeconds >= 30, $"only {checkedSeconds} of 35 seconds carried a target heading");
            AircraftState followerAtEnd = Assert.IsType<AircraftState>(engine.FindAircraft(Follower));
            Assert.Equal(Lead, followerAtEnd.Approach.FollowingCallsign);
        }
    }

    /// <summary>
    /// The measures the turn-out's "level with or ahead" test compares, for the report: the straight gap to the lead, the
    /// follower's shortest path to the 28R threshold and the lead's remaining path to it.
    /// </summary>
    private static string InTrailMeasures(AircraftState follower, SimulationEngine engine)
    {
        if ((engine.FindAircraft(Lead) is not { } lead) || (lead.Phases?.AssignedRunway is not { } runway))
        {
            return "lead has no runway";
        }

        var circuit = new FollowPatternReturn(runway, PatternDirection.Right, 1000, FromBase: false);
        return $"lead {lead.Phases.CurrentPhase?.GetType().Name}, gap {GeoMath.DistanceNm(follower.Position, lead.Position):F2} nm, "
            + $"path {VfrFollowPhase.FollowerPathToThresholdNm(follower, circuit):F2} "
            + $"vs lead {AirborneFollowHelper.LeadRemainingPathNm(lead, runway):F2} nm";
    }
}
