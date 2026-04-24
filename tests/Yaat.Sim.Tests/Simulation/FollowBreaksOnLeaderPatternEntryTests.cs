using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the FOLLOW-during-leader-pattern-entry bug.
///
/// Recording: S2-OAK-3 (1) "VFR Sequencing" (ZOA) — 6 C172s inbound to KOAK.
/// At t=6 N9225L receives ERD 28R (PatternEntryPhase navigating to the
/// downwind abeam point). At t=118 N436MS is given FOLLOW N9225L. The live
/// session cancelled the follow at t≈207 with "unable to catch up".
///
/// Root-cause sequence reconstructed from recorded snapshots via
/// `tools/bug_bundle.py track --pair N436MS N9225L`:
/// 1. While N9225L is in PatternEntryPhase, ExtractPatternWaypoints returns
///    null, so VfrFollowPhase.TryJoinLeadPattern rejects every join attempt.
/// 2. VfrFollowPhase's free-flight spacing control slows N436MS to its
///    approach speed (~72.7 kts) to create 1.5 nm of spacing — but that
///    leaves it ~5 kts slower than N9225L (77.5 kts). After the gap reaches
///    its minimum (~1.30 nm at t=170), the follower's under-leader speed
///    causes the gap to creep outward by ~0.04 nm over 30s.
/// 3. VfrFollowPhase's runaway-gap detector compares gap to the running
///    minimum and fires a cancel at t=205 when the gap has been strictly
///    above the running min for 30 consecutive seconds — even though the
///    geometry is normal spacing settling, not a runaway.
///
/// Intended fix: let ExtractPatternWaypoints peek at the next pattern-leg
/// phase when the leader is in PatternEntryPhase (unblocking the join), and
/// make PatternEntryPhase apply AirborneFollowHelper.GetAdjustedSpeed so the
/// follower keeps closing/holding pattern-tight spacing after the join.
/// </summary>
public class FollowBreaksOnLeaderPatternEntryTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak3-vfr-sequencing-recording.yaat-bug-report-bundle.zip";
    private const string Follower = "N436MS";
    private const string Leader = "N9225L";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("VfrFollowPhase", LogLevel.Debug)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .EnableCategory("PatternEntryPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Diagnostic: hybrid replay restoring the recorded snapshot at t=115 (just
    /// before FOLLOW at t=118), then ticking through the bug window while
    /// logging follower/leader state each second.
    /// </summary>
    [Fact]
    public void Diagnostic_HybridReplay_TickByTick()
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

            var snapshot = archive.ReadSnapshotAt(115);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=115 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"Restored snapshot at t={startTime}");

            engine.ReplayRange(startTime, Math.Min(startTime + 120, (int)recording.TotalElapsedSeconds), recording.Actions);

            // ReplayRange advances the engine internally; after it returns we can inspect terminal state.
            LogTerminalState("post-ReplayRange", engine);
        }
    }

    /// <summary>
    /// Core assertion: after the leader's PatternEntryPhase-through-Downwind
    /// transition, the follower must not have been cancelled with
    /// "unable to catch up". Either it has auto-joined the pattern (no longer
    /// in VfrFollowPhase) or it's still in VfrFollowPhase with an active
    /// FollowingCallsign.
    /// </summary>
    [Fact]
    public void FollowerNotCancelled_DuringLeaderPatternEntry()
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

            // Snapshot at t=115 is 3s before FOLLOW at t=118 and 62s before
            // the live cancel at t≈207. Run forward to t=220 so we pass the
            // cancel window in both original and fixed code.
            var snapshot = archive.ReadSnapshotAt(115);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=115 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;
            engine.ReplayRange(startTime, 220, recording.Actions);

            var follower = engine.FindAircraft(Follower);
            var lead = engine.FindAircraft(Leader);
            Assert.NotNull(follower);
            Assert.NotNull(lead);

            LogTerminalState("t=220", engine);

            Assert.DoesNotContain(follower.PendingWarnings, w => w.Contains("unable to catch up", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Leader, follower.FollowingCallsign);

            var followerPhase = follower.Phases?.CurrentPhase;
            Assert.True(
                followerPhase is VfrFollowPhase || followerPhase is PatternEntryPhase || followerPhase is DownwindPhase,
                $"Expected VfrFollowPhase / PatternEntryPhase / DownwindPhase, got {followerPhase?.GetType().Name ?? "(null)"}"
            );
        }
    }

    private void LogTerminalState(string label, SimulationEngine engine)
    {
        var follower = engine.FindAircraft(Follower);
        var lead = engine.FindAircraft(Leader);
        if (follower is null || lead is null)
        {
            output.WriteLine($"{label}: follower or leader missing");
            return;
        }
        double gap = GeoMath.DistanceNm(follower.Position.Lat, follower.Position.Lon, lead.Position.Lat, lead.Position.Lon);
        output.WriteLine($"{label}:");
        output.WriteLine($"  leader   {Leader}  phase={lead.Phases?.CurrentPhase?.GetType().Name ?? "none"}  ias={lead.IndicatedAirspeed:F1}");
        output.WriteLine(
            $"  follower {Follower}  phase={follower.Phases?.CurrentPhase?.GetType().Name ?? "none"}  ias={follower.IndicatedAirspeed:F1}  tgtSpd={follower.Targets.TargetSpeed?.ToString("F1") ?? "null"}  follCS={follower.FollowingCallsign ?? "null"}"
        );
        output.WriteLine($"  gap={gap:F3} nm  warnings=[{string.Join("; ", follower.PendingWarnings)}]");
    }
}
