using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Issue #453: at SFO ASA826 (cleared <c>TAXI B H A M1 @B10</c>, stopped at E/B) was told <c>GIVEWAY ASA1187</c> at
/// t=344, with ASA1187 coming along F1 to merge onto B ahead of it on the same B → H → A route. ASA826 stayed held
/// after ASA1187 had passed until the instructor issued <c>RES</c> at t=368. The test restores the snapshot after the
/// GIVEWAY and runs the live sim without the recorded <c>RES</c>: the hold must release on its own once ASA1187 is past.
/// </summary>
public class GiveWayInTrailReleaseTests(ITestOutputHelper output)
{
    private const string BundlePath = "TestData/issue453-asa826-giveway-recording.yaat-bug-report-bundle.zip";

    [Fact]
    public void GiveWayBehindTrafficMergingAhead_ReleasesOnItsOwn()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        RecordingArchive? archive = RecordingLoader.OpenArchive(BundlePath);
        Assert.NotNull(archive);

        using (archive)
        {
            var engine = new SimulationEngine(groundData);
            engine.Replay(archive.ToBaseSessionRecording(), 0);
            TimedSnapshot snapshot = Assert.IsType<TimedSnapshot>(archive.ReadSnapshotAt(345));
            engine.RestoreFromSnapshot(snapshot.State);

            AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft("ASA826"));
            Assert.True(held.Ground.Hold?.IsGiveWayFor("ASA1187") == true, "test setup: ASA826 gives way to ASA1187 at the restore point");

            int? releasedAt = null;
            for (int t = (int)snapshot.ElapsedSeconds + 1; t <= 460; t++)
            {
                engine.TickOneSecond();
                AircraftState? target = engine.FindAircraft("ASA1187");
                output.WriteLine(
                    $"t={t} ASA826 hold={held.Ground.Hold?.Kind.ToString() ?? "-"} hdg={held.TrueHeading.Degrees:F0} gs={held.GroundSpeed:F1} "
                        + $"| ASA1187 twy={target?.Ground.CurrentTaxiway ?? "?"} hdg={target?.TrueHeading.Degrees:F0} "
                        + $"dist={(target is null ? double.NaN : GeoMath.DistanceNm(held.Position, target.Position) * GeoMath.FeetPerNm):F0}ft"
                );
                if (held.Ground.Hold is null)
                {
                    releasedAt = t;
                    // Still on A, ASA1187 has yet to turn toward the merge: GIVEWAY must hold until it has.
                    Assert.NotEqual("A", target?.Ground.CurrentTaxiway);
                    break;
                }
            }

            // ASA1187 was on F1 committed to the B merge well before the instructor's RES at t=368; the hold must let
            // ASA826 go by then instead of waiting for ASA1187 to reach B (t=374).
            Assert.True(releasedAt is not null, "ASA826 was still giving way to ASA1187 at t=460 — the hold never released on its own");
            Assert.True(
                releasedAt <= 368,
                $"ASA826 released at t={releasedAt}, only once ASA1187 had reached B — not when it was committed to the merge"
            );
        }
    }
}
