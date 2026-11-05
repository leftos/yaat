using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

public class SfoMidFilletLegacyRestoreTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/sfo-gc-28-01-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "SKW3396";
    private const int RestoreAt = 1155;
    private const int CheckAt = 1160;
    private const double MaxDriftFt = 10.0;

    /// <summary>
    /// SKW3396 restored from a snapshot written before the navigator carried its playback, partway round the
    /// 1254-1256 fillet on route segment 9 (heading 3 deg off the curve's tangent there, 74 deg off its start tangent):
    /// it resumes the fillet and is on G where the recording has it at t=1160, rather than looping off it to
    /// align with the start tangent and being blended back 14.5 ft in one sub-tick.
    /// </summary>
    [Fact]
    public void Skw3396_RestoredMidFilletWithoutPlayback_ResumesTheFillet()
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            TestVnasData.EnsureInitialized();
            SimLogBuilder.CreateForTest(output).EnableCategory("GroundNavigator", LogLevel.Debug).InitializeSimLog();
            var engine = new SimulationEngine(
                RecordingGroundData.VersionPinned(RecordingPath, "SKW3396's taxi route node ids as recorded"),
                PushPlanStore.CreateShipped()
            );
            engine.Replay(archive.ToBaseSessionRecording(), 0);
            TimedSnapshot? restore = archive.ReadSnapshotAt(RestoreAt);
            TimedSnapshot? recorded = archive.ReadSnapshotAt(CheckAt);
            Assert.NotNull(restore);
            Assert.NotNull(recorded);

            // The case under test: a pre-playback snapshot, mid-fillet past route segment 0.
            AircraftSnapshotDto before = restore.State.Aircraft.Single(a => a.Callsign == Callsign);
            Assert.Null(((TaxiingPhaseDto)before.Phases!.Phases[0]).Navigator!.Playback);
            Assert.Equal(9, before.Ground.AssignedTaxiRoute!.CurrentSegmentIndex);

            engine.RestoreFromSnapshot(restore.State);
            AircraftState? ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            Assert.IsType<GroundArc>(ac.Ground.AssignedTaxiRoute!.Segments[9].Edge.Edge);
            for (int t = RestoreAt + 1; t <= CheckAt; t++)
            {
                engine.ReplayOneSecond();
            }

            AircraftSnapshotDto after = recorded.State.Aircraft.Single(a => a.Callsign == Callsign);
            double driftFt = GeoMath.DistanceNm(ac.Position, after.Position) * GeoMath.FeetPerNm;
            Assert.True(
                ac.Ground.AssignedTaxiRoute?.CurrentSegmentIndex >= 10,
                $"still on segment {ac.Ground.AssignedTaxiRoute?.CurrentSegmentIndex} at t={CheckAt}, hdg {ac.TrueHeading.Degrees:F1}"
            );
            Assert.True(driftFt <= MaxDriftFt, $"{driftFt:F1} ft from the recorded t={CheckAt} position");
        }
    }
}
