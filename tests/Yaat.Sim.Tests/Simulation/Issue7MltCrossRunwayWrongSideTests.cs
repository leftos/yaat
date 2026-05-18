using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for GitHub issue #7: <c>MLT 28L</c> issued mid-pattern after a
/// touch-and-go on 28R right traffic leaks the old crosswind heading.
///
/// Recording: S2-OAK-4 "VFR Transitions / Radar Concepts" (ZOA). At t=1265
/// N342T (DA42) is on a fresh Upwind for 28R right traffic (third
/// touch-and-go cycle). At t=1278 the user issues <c>MLT 28L</c> intending
/// to switch to 28L left traffic. Aircraft is ~0.56 nm north of 28L
/// threshold — wrong side for 28L left traffic (which is south of the field).
///
/// Observed bug: <c>TryChangePatternDirection</c> updated
/// <c>AssignedRunway</c>, <c>TrafficDirection</c>, <c>DestinationRunway</c>,
/// and patched the pending Crosswind's <c>Waypoints</c> reference (so
/// <c>CrosswindHeadingDeg</c> read 202° in the t=1280 snapshot), but the
/// Crosswind's <c>OnStart</c> had already latched
/// <c>Targets.TargetTrueHeading = 22.26°</c> (the OLD 28R/right value).
/// <c>UpdateWaypoints</c> never refreshes the applied target heading.
/// Snapshot at t=1280 shows aircraft already banked +27° turning right
/// toward 22°. There was also no <c>MidfieldCrossingPhase</c> inserted,
/// even though the aircraft is on the wrong physical side of the new runway.
///
/// Expected after fix: <c>TryChangePatternDirection</c> rebuilds the phase
/// chain from the current leg when an active pattern leg
/// (Upwind/Crosswind/Downwind/Base) is in flight; inserts
/// <c>MidfieldCrossingPhase</c> when the aircraft is on the wrong side for
/// the new pattern. After <c>MLT 28L</c> the chain contains
/// <c>MidfieldCrossingPhase</c>, the target heading turns toward the south
/// pattern side (NOT 22°), and the aircraft banks left.
/// </summary>
public class Issue7MltCrossRunwayWrongSideTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue7-mlt-cross-runway-wrong-side-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N342T";

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
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .EnableCategory("CrosswindPhase", LogLevel.Debug)
            .EnableCategory("MidfieldCrossingPhase", LogLevel.Debug)
            .EnableCategory("CommandDispatcher", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Mlt28L_OnUpwindFrom28R_InsertsMidfieldCrossingAndTurnsLeft()
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

            // Hybrid replay: pin pre-MLT state via the recorded snapshot so
            // RNG drift across 1275 s of pattern work doesn't move the chain
            // to a different leg by the time MLT fires.
            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(1275);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=1275 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int snapshotTime = (int)snapshot.ElapsedSeconds;
            output.WriteLine($"Restored snapshot at t={snapshotTime}");

            // Sanity: at t=1275 N342T is on Upwind for 28R right traffic.
            var pre = engine.FindAircraft(Callsign);
            Assert.NotNull(pre);
            Assert.IsType<UpwindPhase>(pre.Phases?.CurrentPhase);
            Assert.Equal("28R", pre.Phases!.AssignedRunway?.Designator);
            Assert.Equal(PatternDirection.Right, pre.Phases.TrafficDirection);
            output.WriteLine(
                $"t={snapshotTime}: hdg={pre.TrueHeading.Degrees:F0} "
                    + $"tgtHdg={pre.Targets.TargetTrueHeading?.Degrees:F0} "
                    + $"pos=({pre.Position.Lat:F4},{pre.Position.Lon:F4}) "
                    + $"alt={pre.Altitude:F0}"
            );

            // ReplayOneSecond through t=1278 (recorded MLT 28L fires) and
            // stop before t=1283 (recorded user workaround FH 270). The
            // recorded MLT is exactly the bug trigger we want to exercise.
            for (int dt = 1; dt <= 5; dt++)
            {
                engine.ReplayOneSecond();
            }

            var post = engine.FindAircraft(Callsign);
            Assert.NotNull(post);
            int now = snapshotTime + 5;

            output.WriteLine(
                $"t={now}: phase={post.Phases?.CurrentPhase?.GetType().Name ?? "(null)"} "
                    + $"hdg={post.TrueHeading.Degrees:F0} "
                    + $"tgtHdg={post.Targets.TargetTrueHeading?.Degrees:F0} "
                    + $"bank={post.BankAngle:F0} "
                    + $"rwy={post.Phases?.AssignedRunway?.Designator} "
                    + $"dir={post.Phases?.TrafficDirection}"
            );

            // The MLT must have stamped the new pattern intent.
            Assert.Equal("28L", post.Phases?.AssignedRunway?.Designator);
            Assert.Equal(PatternDirection.Left, post.Phases?.TrafficDirection);

            // (a) Wrong-side recovery: a MidfieldCrossingPhase must appear in
            // the chain. Without the fix the chain has Upwind→Crosswind→… all
            // for 28R/right and no MidfieldCrossing.
            bool hasMidfield = post.Phases?.Phases.Any(p => p is MidfieldCrossingPhase && p.Status != PhaseStatus.Completed) == true;
            Assert.True(
                hasMidfield,
                $"Expected MidfieldCrossingPhase in chain after MLT 28L from wrong side. "
                    + $"Chain: [{string.Join(", ", post.Phases?.Phases.Select(p => $"{p.GetType().Name}:{p.Status}") ?? [])}]"
            );

            // (b) Target heading must be on the new (south) pattern side
            // — somewhere in [120°, 240°] is "south-ish". Old 28R/right
            // crosswind heading is ~22° (north). Anything in [340°, 60°]
            // is the bug.
            Assert.NotNull(post.Targets.TargetTrueHeading);
            double tgt = post.Targets.TargetTrueHeading.Value.Degrees;
            Assert.False(
                tgt is >= 340 or <= 60,
                $"TargetTrueHeading={tgt:F1}° looks like the OLD 28R/right crosswind heading (~22°). "
                    + $"Expected south-ish after MLT 28L from wrong side."
            );

            // (c) Aircraft should be turning left (or already turned south),
            // not banked right.
            Assert.True(
                post.BankAngle <= 5.0,
                $"Aircraft is banked right ({post.BankAngle:F1}°) {now - snapshotTime - 3}s after MLT 28L. "
                    + $"Expected wings-level or left bank — turning toward south pattern side."
            );
        }
    }
}
