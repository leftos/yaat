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
/// `TestData/s2-oak3-vfr-sequencing-followaudit-recording.yaat-bug-report-bundle.zip`
/// (distinct from `s2-oak3-vfr-sequencing-recording...` used by
/// <see cref="FollowBreaksOnLeaderPatternEntryTests"/>).
/// </summary>
public class FollowAuditTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak3-vfr-sequencing-followaudit-recording.yaat-bug-report-bundle.zip";

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
}
