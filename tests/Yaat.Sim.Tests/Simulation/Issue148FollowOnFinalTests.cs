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
/// E2E tests for the FOLLOW-when-leader-on-FinalApproach bug.
///
/// Recording: S2-OAK-5 "Practical Exam Preparation/Advanced Concepts" (ZOA),
/// OAK pattern, right traffic for 28R. At t=755 N294MG (TBM9 on right downwind,
/// alt 1009 ft, hdg 111°, ias 200, **no active phase**) is given
/// <c>FOLLOW N10194</c>. N10194 is a C172 already in
/// <see cref="FinalApproachPhase"/> for 28R (alt 402 ft, hdg 292°, ias 62).
/// Expected: N294MG continues downwind, sequences behind, then turns base
/// after the leader has landed/cleared.
///
/// Observed (the bug): N294MG enters <see cref="VfrFollowPhase"/> and
/// immediately commands a southbound heading (toward N10194's current position
/// over the runway) — a "cut the pattern" turn that no controller would
/// expect. Root cause: <c>VfrFollowPhase.WaypointsOf</c> only matches
/// Downwind/Base/Crosswind/Upwind, so when the leader is on FinalApproach
/// <c>ExtractPatternWaypoints</c> returns null and <c>TryJoinLeadPattern</c>
/// falls through to free pursuit.
///
/// **Replay strategy:** Hybrid (snapshot restore at t=750, then
/// <c>ReplayRange</c> forward). Full replay from t=0 diverges from the
/// recorded state — by t=755 the leader is in GoAroundPhase rather than
/// FinalApproachPhase, which doesn't reproduce the bug. Hybrid pins the
/// pre-bug state to what the user actually saw.
/// </summary>
public class Issue148FollowOnFinalTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue148-follow-final-recording.yaat-bug-report-bundle.zip";
    private const string Follower = "N294MG";
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
            .EnableCategory("VfrFollowPhase", LogLevel.Debug)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Diagnostic: hybrid replay from t=750 snapshot through the FOLLOW window,
    /// logging follower/leader state each second. Use this to confirm what the
    /// follower is actually doing tick-by-tick when investigating regressions.
    /// </summary>
    [Fact]
    public void Diagnostic_HybridReplay_AroundFollow()
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

            var snapshot = archive.ReadSnapshotAt(750);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=750 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"=== Hybrid replay from snapshot t={startTime} ===");

            for (int t = startTime + 1; t <= 790; t++)
            {
                engine.ReplayRange(t - 1, t, recording.Actions);
                var f = engine.FindAircraft(Follower);
                var l = engine.FindAircraft(Leader);
                if (f is null || l is null)
                {
                    continue;
                }

                double gap = GeoMath.DistanceNm(f.Position, l.Position);
                string fPhase = f.Phases?.CurrentPhase?.GetType().Name ?? "none";
                string lPhase = l.Phases?.CurrentPhase?.GetType().Name ?? "none";
                string foll = f.Approach.FollowingCallsign ?? "null";
                double tgtHdg = f.Targets.TargetTrueHeading?.Degrees ?? double.NaN;
                output.WriteLine(
                    $"t={t}: F.hdg={f.TrueHeading.Degrees:F0} F.tgtHdg={tgtHdg:F0} F.alt={f.Altitude:F0} F.ias={f.IndicatedAirspeed:F0} "
                        + $"F.phase={fPhase} F.foll={foll} | L.phase={lPhase} L.alt={l.Altitude:F0} | gap={gap:F2}nm"
                );
            }
        }
    }

    /// <summary>
    /// Core assertion: in the seconds after FOLLOW dispatch the follower must
    /// not be in <see cref="VfrFollowPhase"/> free-pursuit mode. After the fix
    /// the join logic should swap in a pattern circuit (PatternEntry/Downwind
    /// /Base/Final/Landing) that uses the leader's runway and direction.
    /// </summary>
    [Fact]
    public void Follower_DoesNotFreePursueLeaderOnFinal()
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

            var snapshot = archive.ReadSnapshotAt(750);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;

            // Run forward 35 seconds — well past the FOLLOW at t=755.
            engine.ReplayRange(startTime, 785, recording.Actions);

            var follower = engine.FindAircraft(Follower);
            Assert.NotNull(follower);
            Assert.Equal(Leader, follower.Approach.FollowingCallsign);

            var phase = follower.Phases?.CurrentPhase;
            Assert.False(
                phase is VfrFollowPhase,
                $"Follower stuck in VfrFollowPhase free-pursuit at t=785 — should have joined leader's pattern. Heading={follower.TrueHeading.Degrees:F0}°, target={follower.Targets.TargetTrueHeading?.Degrees:F0}°"
            );

            // After fix the follower should be in a pattern leg phase.
            Assert.True(
                phase is PatternEntryPhase || phase is DownwindPhase || phase is BasePhase || phase is FinalApproachPhase,
                $"Follower should be on a pattern leg, got {phase?.GetType().Name ?? "(null)"}"
            );
        }
    }

    /// <summary>
    /// After the join, the follower's <see cref="PhaseList.AssignedRunway"/>
    /// and <see cref="PhaseList.TrafficDirection"/> should match the leader's
    /// (28R right traffic).
    /// </summary>
    [Fact]
    public void Follower_JoinsLeaderRunwayAndDirection()
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

            var snapshot = archive.ReadSnapshotAt(750);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            engine.ReplayRange((int)snapshot.ElapsedSeconds, 770, recording.Actions);

            var follower = engine.FindAircraft(Follower);
            Assert.NotNull(follower);

            Assert.Equal("28R", follower.Phases?.AssignedRunway?.Designator);
            Assert.Equal(PatternDirection.Right, follower.Phases?.TrafficDirection);
        }
    }

    /// <summary>
    /// While the leader (C172, piston) is still airborne on
    /// <see cref="FinalApproachPhase"/>, the follower should extend downwind
    /// rather than turn base immediately. With the wider "leader-on-final"
    /// extension threshold (1.5× desired = 1.5 nm for piston leader) and a
    /// gap of ~1.24 nm at t=755, the helper should keep the follower on
    /// downwind heading instead of pointing it at the leader.
    ///
    /// Asserts the follower's actual heading stays close to right-downwind
    /// for 28R (~111°), not snapped south toward the leader's position.
    /// </summary>
    [Fact]
    public void Follower_DoesNotSnapHeadingTowardLeader()
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

            var snapshot = archive.ReadSnapshotAt(750);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            engine.ReplayRange((int)snapshot.ElapsedSeconds, 775, recording.Actions);

            var follower = engine.FindAircraft(Follower);
            Assert.NotNull(follower);

            // Right-downwind for 28R is ~111° true. A free-pursuit south turn
            // produces 160°-200° within seconds. Allow up to 145° to permit
            // small wind/track corrections and reject any meaningful turn south.
            double hdg = follower.TrueHeading.Degrees;
            Assert.True(hdg < 145 || hdg > 350, $"Follower heading {hdg:F0}° at t=775 — turned south toward leader instead of extending downwind");
        }
    }
}
