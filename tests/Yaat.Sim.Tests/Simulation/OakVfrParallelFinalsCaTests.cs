using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the OAK S2-OAK-3 (2) VFR Sequencing bundle. Two VFR pattern
/// aircraft on visual finals to OAK 28L/28R simultaneously trigger CA on STARS
/// even though both are inside their respective runway approach corridors at
/// an internal airport. The corridor suppression must apply purely from the
/// geometric volumes — STARS does not consult phase or active approach state.
///
/// Repro snapshot: t=785 — N805FM on final 28R at 152 ft, N70CS on final 28L
/// at 151 ft, 0.22 NM lateral / 1 ft vertical. Both VFR, no ActiveApproach.
/// </summary>
[Collection("NavDbMutator")]
public class OakVfrParallelFinalsCaTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-vfr-parallel-finals-recording.yaat-bug-report-bundle.zip";
    private const int RestoreAtSeconds = 785;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void ParallelFinalsToOakInsideCorridor_NoConflictAlert()
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
            output.WriteLine($"Restored snapshot at t={snapshot.ElapsedSeconds}");

            var aircraft = engine.World.GetSnapshot();
            var n805fm = aircraft.SingleOrDefault(a => a.Callsign == "N805FM");
            var n70cs = aircraft.SingleOrDefault(a => a.Callsign == "N70CS");
            Assert.NotNull(n805fm);
            Assert.NotNull(n70cs);
            Assert.False(n805fm.IsOnGround);
            Assert.False(n70cs.IsOnGround);

            var corridors = ConflictAlertDetector.BuildCorridors(["OAK"], NavigationDatabase.Instance);
            Assert.NotEmpty(corridors);

            // Sanity check: without the corridors (empty list), CA WOULD fire — proves the
            // scenario is in the conflict envelope. This documents that the test exercises
            // the suppression path, not just an absence-of-conflict.
            var fireResult = ConflictAlertDetector.Detect(aircraft, new ConflictAlertContext([], []));
            output.WriteLine($"Without corridors: {fireResult.Count} conflicts");
            Assert.Contains(
                fireResult,
                p => (p.CallsignA == "N805FM" && p.CallsignB == "N70CS") || (p.CallsignA == "N70CS" && p.CallsignB == "N805FM")
            );

            // With OAK corridors built, both tracks land inside the volumes →
            // suppression engages and the pair must NOT appear in the result.
            var suppressed = ConflictAlertDetector.Detect(aircraft, new ConflictAlertContext([], corridors));
            output.WriteLine($"With OAK corridors: {suppressed.Count} conflicts");
            Assert.DoesNotContain(
                suppressed,
                p => (p.CallsignA == "N805FM" && p.CallsignB == "N70CS") || (p.CallsignA == "N70CS" && p.CallsignB == "N805FM")
            );
        }
    }
}
