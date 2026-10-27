using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Issue #453: at SFO ASA826 (cleared <c>TAXI B H A M1 @B10</c>, stopped at E/B) was told <c>GIVEWAY ASA1187</c> at
/// t=344, with ASA1187 coming along F1 to merge onto B ahead of it on the same B → H → A route. ASA826 stayed held
/// after ASA1187 had passed until the instructor issued <c>RES</c> at t=368. The test restores the snapshot after the
/// GIVEWAY and runs the live sim without the recorded <c>RES</c>: the hold must release on its own once ASA1187's tail
/// has cleared the F1/B merge (node 152), with ASA1187 on B, and not wait for the later node 1265, where the two B → H → A
/// routes split (the fillet against the square corner) and rejoin in the same direction.
/// </summary>
public class GiveWayInTrailReleaseTests(ITestOutputHelper output)
{
    private const string BundlePath = "TestData/issue453-asa826-giveway-recording.yaat-bug-report-bundle.zip";

    [Fact]
    public void GiveWayBehindTrafficMergingAhead_ReleasesOnceTailClearsMerge_NotAtLaterRejoin()
    {
        // Pinned to the recorded layout: the restored snapshot's saved routes name layout node ids.
        var groundData = RecordingGroundData.VersionPinned(BundlePath, "a GIVEWAY hold releasing on its own once the traffic has passed");
        if (groundData.GetLayout("SFO") is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        RecordingArchive? archive = RecordingLoader.OpenArchive(BundlePath);
        Assert.NotNull(archive);

        using (archive)
        {
            var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
            engine.Replay(archive.ToBaseSessionRecording(), 0);
            TimedSnapshot snapshot = Assert.IsType<TimedSnapshot>(archive.ReadSnapshotAt(345));
            engine.RestoreFromSnapshot(snapshot.State);

            AircraftState held = Assert.IsType<AircraftState>(engine.FindAircraft("ASA826"));
            Assert.True(held.Ground.Hold?.IsGiveWayFor("ASA1187") == true, "test setup: ASA826 gives way to ASA1187 at the restore point");

            int? releasedAt = null;
            string? targetTwyAtRelease = null;
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
                    targetTwyAtRelease = engine.FindAircraft("ASA1187")?.Ground.CurrentTaxiway;
                    break;
                }

                if (target is not null)
                {
                    Assert.True(
                        GroundConflictDetector.GiveWayJunctionOf(held, target)?.NodeId != 1265,
                        $"t={t}: ASA826 gives way to ASA1187 at node 1265, where their shared B-H-A routes split and rejoin in the same direction"
                    );
                }
            }

            // Once ASA1187's tail has cleared the F1/B merge (node 152) the two run in trail on B → H → A: the hold ends there.
            // Node 1265, where the held route's fillet and ASA1187's square corner off H rejoin onto A, is no crossing.
            Assert.True(
                releasedAt is not null,
                "ASA826 still gave way to ASA1187 at t=460: a later node where the shared B-H-A routes split and rejoin was judged a crossing"
            );
            Assert.Equal("B", targetTwyAtRelease);
        }
    }
}
