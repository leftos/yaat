using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the "N500M: MLT makes them turn left immediately" bug from the
/// S2-OAK-P (S2 Rating Practical Exam) bundle (ZOA, OAK).
///
/// At t≈1780 N500M (C182) goes around off 28L and climbs straight out on the
/// Upwind leg, on the 28L extended centerline (TrueHeading ≈ 292°, ~600 ft). At
/// t=1801 the controller issues bare <c>MLT</c> (make left traffic, same runway).
///
/// Observed bug: <c>TryChangePatternDirection</c> treats the aircraft as being on
/// the "wrong side" of the runway because its cross-track offset is a hair
/// negative (−0.02 nm — essentially ON the centerline). It inserts a
/// <see cref="MidfieldCrossingPhase"/> whose OnStart immediately points the
/// aircraft at a midfield target south of the field, so the C182 banks ~21° left
/// within a couple seconds and cuts across the runway at 600 ft (by t=1811,
/// 10 s after MLT, it has swung from heading 292° to 242°).
///
/// Expected after fix: an aircraft established on the upwind centerline that is
/// given "make left traffic" flies standard left closed traffic — continue
/// upwind, then left crosswind, then left downwind (AIM 4-3-3). No
/// MidfieldCrossingPhase and no immediate left turn; it holds runway heading on
/// upwind until the crosswind turn near the departure end. The
/// wrong-side/midfield-crossing path is reserved for a genuinely displaced
/// aircraft (see <see cref="Issue7MltCrossRunwayWrongSideTests"/>, ~0.17 nm off
/// the parallel runway), which a small cross-track deadband preserves.
/// </summary>
public class BugN500mMltUpwindTurnsLeftImmediateTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/n500m-mlt-upwind-immediate-left-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N500M";

    // Snapshot just before the recorded MLT at t=1801; N500M is on Upwind for 28L.
    private const int SnapshotTime = 1800;

    // Seconds to replay past the snapshot: covers the MLT (t=1801) and settles ~10 s
    // later, where the pre-fix aircraft has already swung ~50° left (hdg 242°).
    private const int SecondsToReplay = 11;

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
            .EnableCategory("MidfieldCrossingPhase", LogLevel.Debug)
            .EnableCategory("UpwindPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Mlt_OnUpwindCenterline_ContinuesUpwind_NoMidfieldCrossing()
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

            // Hybrid replay: pin pre-MLT state via the recorded snapshot so RNG drift
            // over ~1800 s of pattern work doesn't move N500M off the upwind leg by
            // the time MLT fires (mirrors Issue7MltCrossRunwayWrongSideTests).
            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(SnapshotTime);
            if (snapshot is null)
            {
                output.WriteLine($"No snapshot near t={SnapshotTime} — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int snapshotTime = (int)snapshot.ElapsedSeconds;

            // Sanity: N500M is climbing out on Upwind for 28L, on the extended
            // centerline (heading roughly aligned with the runway).
            var pre = engine.FindAircraft(Callsign);
            Assert.NotNull(pre);
            Assert.IsType<UpwindPhase>(pre.Phases?.CurrentPhase);
            Assert.Equal("28L", pre.Phases!.AssignedRunway?.Designator);
            var runwayHeading = pre.Phases.AssignedRunway!.TrueHeading;
            Assert.True(
                pre.TrueHeading.AbsAngleTo(runwayHeading) < 20,
                $"Pre-MLT N500M should be aligned with the runway on upwind, "
                    + $"was hdg={pre.TrueHeading.Degrees:F0} vs rwy={runwayHeading.Degrees:F0}"
            );

            // Replay through the recorded MLT at t=1801 and a bit beyond. Assert that
            // no midfield crossing is ever inserted along the way.
            for (int dt = 1; dt <= SecondsToReplay; dt++)
            {
                engine.ReplayOneSecond();
                var ac = engine.FindAircraft(Callsign);
                Assert.NotNull(ac);
                Assert.DoesNotContain(ac.Phases?.Phases ?? [], p => p is MidfieldCrossingPhase);
            }

            var post = engine.FindAircraft(Callsign);
            Assert.NotNull(post);
            int now = snapshotTime + SecondsToReplay;

            output.WriteLine(
                $"t={now}: phase={post.Phases?.CurrentPhase?.GetType().Name} "
                    + $"hdg={post.TrueHeading.Degrees:F0} rwyHdg={runwayHeading.Degrees:F0} "
                    + $"bank={post.BankAngle:F0} dir={post.Phases?.TrafficDirection} "
                    + $"rwy={post.Phases?.AssignedRunway?.Designator} "
                    + $"chain=[{string.Join(",", post.Phases?.Phases.Select(p => p.GetType().Name) ?? [])}]"
            );

            // MLT stamped left traffic on the same runway.
            Assert.Equal(PatternDirection.Left, post.Phases?.TrafficDirection);
            Assert.Equal("28L", post.Phases?.AssignedRunway?.Designator);

            // The circuit is rebuilt from the upwind leg for left traffic — it must
            // start with Upwind → Crosswind (standard left closed traffic), NOT a
            // MidfieldCrossing.
            Assert.IsType<UpwindPhase>(post.Phases?.CurrentPhase);
            Assert.Equal(PatternDirection.Left, ((UpwindPhase)post.Phases!.CurrentPhase!).Waypoints?.Direction);

            // Behavioral: 10 s after MLT the aircraft is still tracking the runway on
            // upwind — it has NOT banked hard left across the field. Pre-fix it is ~50°
            // off the runway heading (hdg 242°) in a 21° left bank by now.
            Assert.True(
                post.TrueHeading.AbsAngleTo(runwayHeading) < 40,
                $"N500M swung {post.TrueHeading.AbsAngleTo(runwayHeading):F0}° off the runway heading "
                    + $"{now - snapshotTime}s after MLT — expected to continue upwind, not cut left across the field."
            );
            Assert.True(
                post.BankAngle > -10,
                $"N500M is in a {post.BankAngle:F0}° left bank {now - snapshotTime}s after MLT — "
                    + $"expected roughly wings-level while continuing upwind."
            );
        }
    }
}
