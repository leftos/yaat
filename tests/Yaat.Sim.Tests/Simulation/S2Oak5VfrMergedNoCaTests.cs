using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the S2-OAK-5 (Practical Exam Prep) bundle: a student reported getting
/// no Conflict Alerts (CA) between VFR aircraft even when their targets were merged at
/// the same altitude.
///
/// CRC STARS (docs/crc/stars.md) fires CA on any two associated tracks within 3 NM /
/// 1,000 ft — no VFR/IFR distinction; only the geometric approach-corridor zone near a
/// runway final suppresses. The detector previously applied a tighter 0.25 NM / 500 ft
/// "target-resolution" threshold whenever either track was VFR, which suppressed alerts
/// for same-altitude VFR pairs that were merged on the scope but more than 0.25 NM apart.
///
/// Repro: at t=400 the two VFR transitions N436MS and N10194 sit ~1.9 NM apart at the
/// SAME altitude (0 ft vertical), converging, well clear of any OAK approach corridor —
/// a textbook same-altitude merge that must raise CA at the standard 3 NM / 1,000 ft
/// thresholds but was silently dropped by the old 0.25 NM VFR threshold.
/// </summary>
[Collection("NavDbMutator")]
public class S2Oak5VfrMergedNoCaTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/e5c26ff62464.zip";
    private const int RestoreAtSeconds = 400;
    private const string CallsignA = "N436MS";
    private const string CallsignB = "N10194";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void MergedVfrPair_SameAltitude_RaisesConflictAlert()
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
            var a = aircraft.SingleOrDefault(x => x.Callsign == CallsignA);
            var b = aircraft.SingleOrDefault(x => x.Callsign == CallsignB);
            Assert.NotNull(a);
            Assert.NotNull(b);

            // Both are airborne VFR with Mode C — eligible for CA.
            Assert.False(a.IsOnGround);
            Assert.False(b.IsOnGround);
            Assert.True(a.FlightPlan.IsVfr, $"{CallsignA} should be VFR");
            Assert.True(b.FlightPlan.IsVfr, $"{CallsignB} should be VFR");

            // Merged on the scope at the same altitude: within standard CA thresholds.
            double horizontalNm = GeoMath.DistanceNm(a.Position, b.Position);
            double verticalFt = Math.Abs(a.Altitude - b.Altitude);
            output.WriteLine($"{CallsignA} <-> {CallsignB}: {horizontalNm:F2} nm / {verticalFt:F0} ft");
            Assert.True(horizontalNm < 3.0, $"Expected merged (<3 nm) but was {horizontalNm:F2} nm");
            Assert.True(verticalFt < 1000, $"Expected co-altitude (<1000 ft) but was {verticalFt:F0} ft");

            var corridors = ConflictAlertDetector.BuildCorridors(["OAK"], NavigationDatabase.Instance);
            Assert.NotEmpty(corridors);

            var conflicts = ConflictAlertDetector.Detect(aircraft, new ConflictAlertContext([], corridors));
            output.WriteLine($"Conflicts: {string.Join(", ", conflicts.Select(c => $"{c.CallsignA}/{c.CallsignB}"))}");

            Assert.Contains(
                conflicts,
                p => (p.CallsignA == CallsignA && p.CallsignB == CallsignB) || (p.CallsignA == CallsignB && p.CallsignB == CallsignA)
            );
        }
    }
}
