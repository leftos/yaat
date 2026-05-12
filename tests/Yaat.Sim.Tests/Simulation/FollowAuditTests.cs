using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests born from the FOLLOW audit of bundle
/// `S2-OAK-3 (2) | VFR Sequencing` (recorded 2026-05-10). The audit identified
/// five distinct FOLLOW behaviors that did not match user expectations across
/// seven FOLLOW commands in the recording. Each `[Fact]` here pins one of them
/// against the bundled snapshot stream so a regression is caught at CI time.
///
/// Bundle is installed under
/// `TestData/4d4344011a72.zip`
/// (distinct from `s2-oak3-vfr-sequencing-recording...` used by
/// <see cref="FollowBreaksOnLeaderPatternEntryTests"/>).
/// </summary>
public class FollowAuditTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/4d4344011a72.zip";

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
            .EnableCategory("CommandDispatcher", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Bug 1: FOLLOW issued while the follower is on Upwind (just touched-and-went
    /// and is climbing on the next circuit) currently throws away the in-pattern
    /// state and routes through `[PatternEntry] -> Downwind -> Base -> Final ->
    /// Landing` with a `[PTN-LEADIN, PTN-ENTRY]` route — even though the new lead
    /// is on the same runway. After the fix, the existing UpwindPhase should be
    /// preserved and only `FollowingCallsign` updated.
    ///
    /// Recorded incident: at t=1108, N172SP (in UpwindPhase after the t=1060
    /// touch-and-go) was given `FOLLOW N2BP`. Recording shows phases rebuilt to
    /// PatternEntry at t=1110.
    /// </summary>
    [Fact]
    public void Bug1_FollowFromUpwind_PreservesPattern()
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

            // Snapshot at t=1107 lands on snap=221 (t=1105) — 3s before FOLLOW
            // at t=1108. Replay forward to t=1115 to observe the post-FOLLOW
            // phase rebuild (recording shows it at t=1110).
            var snapshot = archive.ReadSnapshotAt(1107);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=1107 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"Restored snapshot at t={startTime}");

            engine.ReplayRange(startTime, 1115, recording.Actions);

            var follower = engine.FindAircraft("N172SP");
            Assert.NotNull(follower);
            int phaseCount = follower.Phases?.Phases.Count ?? 0;
            output.WriteLine(
                $"t=1115: N172SP phase={follower.Phases?.CurrentPhase?.GetType().Name ?? "(null)"} "
                    + $"chainLen={phaseCount} "
                    + $"following={follower.Approach.FollowingCallsign ?? "(null)"} "
                    + $"route=[{string.Join(",", follower.Targets.NavigationRoute.Select(n => n.Name))}]"
            );

            Assert.Equal("N2BP", follower.Approach.FollowingCallsign);
            // The fix should preserve the existing pattern circuit (12 phases from
            // the t=1090 MRT rebuild covering one full TG circuit + the next).
            // Under the bug, VfrFollowPhase rebuilds to a 5-phase chain
            // [PatternEntry, Downwind, Base, Final, Landing] with a [PTN-LEADIN,
            // PTN-ENTRY] route. Either symptom would be a regression.
            Assert.NotNull(follower.Phases);
            Assert.True(
                follower.Phases.Phases.Count >= 8,
                $"Expected the existing pattern circuit (≥8 phases) to be preserved; got {follower.Phases.Phases.Count}"
            );
            Assert.False(
                follower.Phases.CurrentPhase is PatternEntryPhase,
                "FOLLOW from Upwind should not route the follower back through PatternEntry"
            );
            Assert.DoesNotContain(follower.Targets.NavigationRoute, n => n.Name == "PTN-LEADIN" || n.Name == "PTN-ENTRY");
        }
    }

    /// <summary>
    /// Bug 2: in pattern phases, FollowingCallsign currently leaks for hundreds
    /// of seconds after the lead is despawned. Recording: at t=1108 N172SP began
    /// FOLLOW N2BP. N2BP was deleted at t=1257. N172SP's FollowingCallsign was
    /// cleared only at t=1460 — 200 s of stale "following N2BP" prefix.
    ///
    /// Fix: pattern-phase OnTick calls AirborneFollowHelper.CheckLeadLifecycle,
    /// which mirrors VfrFollowPhase's lookup-null branch (clear + transmission)
    /// for any aircraft with FollowingCallsign set.
    /// </summary>
    [Fact]
    public void Bug2_FollowingCleared_AfterLeadDespawn()
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

            // Snapshot at t=1255 — 2 s before N2BP DEL action at t=1257. N172SP
            // is in DownwindPhase (per recording history). Replay through the
            // despawn and on for 30 s to give the lifecycle helper time to fire.
            var snapshot = archive.ReadSnapshotAt(1255);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=1255 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"Restored snapshot at t={startTime}");

            engine.ReplayRange(startTime, 1290, recording.Actions);

            var follower = engine.FindAircraft("N172SP");
            Assert.NotNull(follower);
            var lead = engine.FindAircraft("N2BP");
            output.WriteLine(
                $"t=1290: N172SP phase={follower.Phases?.CurrentPhase?.GetType().Name ?? "(null)"} "
                    + $"following={follower.Approach.FollowingCallsign ?? "(null)"} "
                    + $"leadExists={(lead is not null)}"
            );

            // N2BP was despawned 33 s ago. Lifecycle watchdog should have fired.
            Assert.Null(lead);
            Assert.Null(follower.Approach.FollowingCallsign);
        }
    }

    /// <summary>
    /// Bug 3: in pattern phases, FollowingCallsign currently does not clear when
    /// the lead transitions to on-ground (Landing → RunwayExit → HoldingAfterExit).
    /// Recording: at t=420 N172SP began FOLLOW N569SX. N569SX touched down at
    /// t=460 and exited the runway by t=500, but N172SP closed to within 0.39 nm
    /// of the now-stationary lead without any "traffic landed" cancellation.
    ///
    /// Fix: same `CheckLeadLifecycle` helper picks up the on-ground branch.
    /// </summary>
    [Fact]
    public void Bug3_FollowingCleared_AfterLeadOnGround()
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

            // Snapshot at t=455 — 5 s before N569SX hits LandingPhase at t=460.
            // Replay through Landing → RunwayExit → HoldingAfterExit (lead becomes
            // IsOnGround at t=480 when RunwayExit phase activates).
            var snapshot = archive.ReadSnapshotAt(455);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=455 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"Restored snapshot at t={startTime}");

            engine.ReplayRange(startTime, 510, recording.Actions);

            var follower = engine.FindAircraft("N172SP");
            Assert.NotNull(follower);
            var lead = engine.FindAircraft("N569SX");
            Assert.NotNull(lead);
            output.WriteLine(
                $"t=510: N172SP follow={follower.Approach.FollowingCallsign ?? "(null)"} "
                    + $"leadOnGround={lead.IsOnGround} leadIas={lead.IndicatedAirspeed:F0}"
            );

            // Lead has been on the ground for ~30 s. Lifecycle watchdog should fire.
            Assert.True(lead.IsOnGround, "Test setup: lead expected on ground by t=510");
            Assert.Null(follower.Approach.FollowingCallsign);
        }
    }

    /// <summary>
    /// Bug 5: in pattern phases, FollowingCallsign currently does not auto-cancel
    /// when the gap to the lead grows monotonically beyond the 30 s grace window
    /// (the runaway-distance check exists only in VfrFollowPhase). Recording: at
    /// t=1138 N2BP began FOLLOW N80ZU; N80ZU was on short final, slowed for
    /// landing then stopped. The gap diverged from 0.75 nm at t=1160 to 1.50 nm
    /// by t=1205 (45 s of monotonic growth) with no cancellation.
    ///
    /// Fix: same `CheckLeadLifecycle` helper picks up the runaway-distance branch.
    /// </summary>
    [Fact]
    public void Bug5_FollowingCleared_OnRunawayDistance()
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

            // Snapshot at t=1135 — 3 s before FOLLOW action at t=1138.
            var snapshot = archive.ReadSnapshotAt(1135);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=1135 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"Restored snapshot at t={startTime}");

            engine.ReplayRange(startTime, 1200, recording.Actions);

            var follower = engine.FindAircraft("N2BP");
            Assert.NotNull(follower);
            output.WriteLine(
                $"t=1200: N2BP phase={follower.Phases?.CurrentPhase?.GetType().Name ?? "(null)"} "
                    + $"following={follower.Approach.FollowingCallsign ?? "(null)"}"
            );

            // Gap began monotonically diverging at t=1165 (lead landing). By t=1200
            // (35 s later) the runaway watchdog should have fired. Either path that
            // ends up with FollowingCallsign cleared is acceptable — the on-ground
            // branch may fire first (lead lands at t=1190).
            Assert.Null(follower.Approach.FollowingCallsign);
        }
    }

    /// <summary>
    /// Bug 4: a follower geographically close to its lead but pattern-flow-AHEAD
    /// (e.g. follower on Downwind, lead still on PatternEntry feeder) should not
    /// be slowed down — the lead has yet to enter the pattern leg the follower
    /// is already flying. Recorded incident: at t=1691 N172SP turned Downwind on
    /// 28R; gap to N428KK (still on PatternEntry from the NW) was only 0.67 nm.
    /// The helper saw "too close, desired ≈ 1.0 nm" and slowed N172SP to 62 KIAS
    /// for the entire downwind, doubling the leg duration (160 s vs ~80 s normal).
    ///
    /// Fix: AirborneFollowHelper.GetAdjustedSpeed[FreeFlight] returns null when
    /// `IsLeadPatternFlowBehind` is true, so the phase keeps its baseline speed.
    /// </summary>
    [Fact]
    public void Bug4_FollowSuppressed_WhenLeadPatternFlowBehind()
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

            // Snapshot at t=1690 — 1 s before FOLLOW action at t=1691. N172SP is
            // on Downwind for 28R; N428KK is on PatternEntry feeder for 28R.
            var snapshot = archive.ReadSnapshotAt(1690);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=1690 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"Restored snapshot at t={startTime}");

            // Replay through 30 s of post-FOLLOW Downwind — under the bug, IAS
            // drops to 62 KIAS (Vref) by ~t=1755. With the fix, IAS should stay
            // at the Downwind baseline (~77.5 KIAS for piston).
            engine.ReplayRange(startTime, 1750, recording.Actions);

            var follower = engine.FindAircraft("N172SP");
            Assert.NotNull(follower);
            output.WriteLine(
                $"t=1750: N172SP phase={follower.Phases?.CurrentPhase?.GetType().Name ?? "(null)"} "
                    + $"following={follower.Approach.FollowingCallsign ?? "(null)"} "
                    + $"ias={follower.IndicatedAirspeed:F1} tgtSpd={follower.Targets.TargetSpeed?.ToString("F1") ?? "(null)"}"
            );

            Assert.Equal("N428KK", follower.Approach.FollowingCallsign);
            Assert.IsType<DownwindPhase>(follower.Phases?.CurrentPhase);
            // Downwind baseline for piston is 77.5 KIAS. The bug pulled IAS down
            // toward Vref (62 KIAS). Allow a 5-kt tolerance for normal pattern-
            // tight settling once the lead enters Downwind itself (which it does
            // at t=1710, 60 s before this assertion fires).
            Assert.True(
                follower.Targets.TargetSpeed is null or >= 70.0,
                $"Expected target speed near Downwind baseline (≥70 kt) while lead is pattern-flow-behind; got {follower.Targets.TargetSpeed?.ToString("F1") ?? "null"}"
            );
        }
    }
}
