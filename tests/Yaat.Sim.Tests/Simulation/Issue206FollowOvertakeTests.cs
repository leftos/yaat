using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #206: a follower told to FOLLOW much-slower traffic
/// overtakes it and flies directly on top instead of sequencing behind.
///
/// Recording: S2-OAK-5 "Practical Exam Preparation/Advanced Concepts" (ZOA), OAK
/// 28R right traffic. N655EX (C210, Vref ~85 kt, pattern ~106 kt) is flying its own
/// pattern (ERD 28R -> MidfieldCrossing -> Downwind -> Base -> FinalApproach). At
/// t=1928, on Base ~0.78 nm behind and overtaking, it is given <c>FOLLOW N163LE</c>
/// (a C152 on a straight-in final to 28R at ~56 kt). The follower never slows enough,
/// has no lateral escape on base/final, and the in-trail gap collapses to ~0.006 nm
/// (co-located) by t=1970 — it flew on top of the lead, then went around only because
/// it had no landing clearance, well after the loss of separation.
///
/// Expected after fix: the follower must never close inside the 0.5 nm same-runway
/// separation floor (7110.65 3-10-3). When it cannot maintain spacing behind the
/// much-slower lead it should break off the follow ("unable to maintain separation")
/// and go around rather than overflying it.
///
/// Replay strategy: hybrid (restore snapshot before the FOLLOW, then ReplayRange
/// forward). Like issue #148 (same scenario family), full replay from t=0 diverges.
/// </summary>
public class Issue206FollowOvertakeTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue206-follow-overtake-recording.yaat-bug-report-bundle.zip";
    private const string Follower = "N655EX";
    private const string Leader = "N163LE";

    /// <summary>Same-runway loss-of-separation floor for two Category I aircraft (3,000 ft).</summary>
    private const double SeparationFloorNm = 0.5;

    /// <summary>
    /// Vertical separation (ft) below which a sub-<see cref="SeparationFloorNm"/> horizontal
    /// gap counts as a genuine conflict. After breaking off and going around the follower
    /// climbs to pattern altitude while the lead descends to land, so it can pass over the
    /// lead horizontally close but well separated vertically — that is the correct, safe
    /// resolution, not the "directly on top" co-altitude overflight this issue is about.
    /// </summary>
    private const double VerticalConflictFt = 500.0;

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
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .EnableCategory("BasePhase", LogLevel.Debug)
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .EnableCategory("FinalApproachPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Diagnostic: replays the FOLLOW window second-by-second and logs the follower's
    /// phase, follow target, target speed, IAS, ground speed, and the in-trail gap to
    /// the lead. Run with detailed console output to pin exactly why the follow
    /// slow-down / break-off never engaged. Not an assertion (always passes).
    /// </summary>
    [Fact]
    public void Diagnostic_LogFollowWindow()
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

            var snapshot = archive.ReadSnapshotAt(1900);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"Restored snapshot at t={startTime}");

            for (int t = startTime; t < 2015; t++)
            {
                engine.ReplayRange(t, t + 1, recording.Actions);

                var f = engine.FindAircraft(Follower);
                var l = engine.FindAircraft(Leader);
                if (f is null)
                {
                    output.WriteLine($"t={t + 1} follower gone");
                    break;
                }

                string gap = l is not null ? $"{GeoMath.DistanceNm(f.Position, l.Position):F3}" : "(lead gone)";
                string vsep = l is not null ? $"{Math.Abs(f.Altitude - l.Altitude):F0}" : "-";
                output.WriteLine(
                    $"t={t + 1} phase={f.Phases?.CurrentPhase?.GetType().Name ?? "(none)", -18} "
                        + $"foll={f.Approach.FollowingCallsign ?? "-", -7} "
                        + $"tgtSpd={(f.Targets.TargetSpeed.HasValue ? f.Targets.TargetSpeed.Value.ToString("F0") : "-"), -4} "
                        + $"ias={f.IndicatedAirspeed:F1} gs={f.GroundSpeed:F1} alt={f.Altitude:F0} gap={gap} vsep={vsep}"
                        + (
                            l is not null
                                ? $" leadAlt={l.Altitude:F0} leadIas={l.IndicatedAirspeed:F1} leadPhase={l.Phases?.CurrentPhase?.GetType().Name}"
                                : ""
                        )
                );
            }
        }
    }

    /// <summary>
    /// Core regression assertion: the follower must never enter the conflict box of the
    /// lead — inside <see cref="SeparationFloorNm"/> horizontally AND
    /// <see cref="VerticalConflictFt"/> vertically — while both are airborne. On current
    /// code the gap collapses to ~0.006 nm co-altitude (the "directly on top" overflight);
    /// after the fix the follower breaks off and goes around, climbing clear of the
    /// descending lead.
    /// </summary>
    [Fact]
    public void Follower_DoesNotOverflyLead()
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

            var snapshot = archive.ReadSnapshotAt(1900);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;

            double worstHorizontalInConflict = double.MaxValue;
            double vsepAtWorst = double.MaxValue;
            int worstTime = -1;

            for (int t = startTime; t < 2015; t++)
            {
                engine.ReplayRange(t, t + 1, recording.Actions);

                var f = engine.FindAircraft(Follower);
                var l = engine.FindAircraft(Leader);
                if (f is null || l is null)
                {
                    break;
                }

                // Only count while both are airborne — once the lead lands/clears the
                // surface, lateral proximity on the ground isn't an in-trail violation.
                if (f.IsOnGround || l.IsOnGround)
                {
                    continue;
                }

                double horizontal = GeoMath.DistanceNm(f.Position, l.Position);
                double vertical = Math.Abs(f.Altitude - l.Altitude);

                // Track the closest approach that is ALSO co-altitude (a real conflict).
                if ((vertical < VerticalConflictFt) && (horizontal < worstHorizontalInConflict))
                {
                    worstHorizontalInConflict = horizontal;
                    vsepAtWorst = vertical;
                    worstTime = t + 1;
                }
            }

            output.WriteLine(
                worstTime < 0
                    ? "Follower never entered the lead's co-altitude conflict band — broke off and climbed clear."
                    : $"Closest co-altitude approach: {worstHorizontalInConflict:F3} nm horizontal, {vsepAtWorst:F0} ft vertical at t={worstTime}"
            );

            Assert.True(
                worstHorizontalInConflict >= SeparationFloorNm,
                $"Follower {Follower} closed to {worstHorizontalInConflict:F3} nm horizontal / {vsepAtWorst:F0} ft vertical of "
                    + $"lead {Leader} at t={worstTime} (floor {SeparationFloorNm} nm / {VerticalConflictFt} ft) — it overflew the "
                    + "traffic it was told to follow instead of breaking off and going around."
            );
        }
    }
}
