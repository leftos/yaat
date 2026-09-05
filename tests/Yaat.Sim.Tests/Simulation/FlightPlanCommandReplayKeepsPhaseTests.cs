using Xunit;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Replay fidelity: a recorded flight-plan command must not change what the aircraft is flying.
///
/// Recording: S2-OAK-5 (2) (ZOA, OAK). N20662 (R22) is hovering in a <c>VfrHoldPhase</c> at VPMOR
/// when, at t=1771, a CRC flight-plan amendment (aircraft type "R22") is recorded twice: as a
/// <c>RecordedAmendFlightPlan</c> and as the STARS echo command <c>AS 3O DA R22</c>. Live, the
/// amendment went through the server's flight-plan handler and the hover carried on (the server log
/// shows the aircraft still "HoldingAtFix" two minutes later). The bundle's reconstructed snapshots
/// show <c>Phases: null</c> from t=1775 — the echo command was replayed through the command
/// dispatcher, where a hold treats any non-additive command as cancelling the phase.
///
/// Bundle snapshots are reconstructions, not live captures, so a snapshot/log disagreement is a
/// replay defect. This pins the Sim replay path; the server reconstruction path has its own test
/// in yaat-server (<c>FlightPlanCommandReconstructionKeepsPhaseTests</c>).
/// </summary>
public class FlightPlanCommandReplayKeepsPhaseTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak5-follow-heli-recording.zip";
    private const string Callsign = "N20662";

    // Restore in the hover (t=1765) and replay through the amendment + echo command (t=1771).
    private const int RestoreAtSeconds = 1765;
    private const int ReplayStopSeconds = 1780;

    [Fact]
    public void N20662_HoverHold_SurvivesReplayedFlightPlanAmendment()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            SimLogBuilder.CreateForTest(output).InitializeSimLog();
            var engine = new SimulationEngine(new TestAirportGroundData());
            var recording = archive.ToBaseSessionRecording();
            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(RestoreAtSeconds);
            if (snapshot is null)
            {
                output.WriteLine($"No snapshot near t={RestoreAtSeconds} — skipping");
                return;
            }

            engine.RestoreFromSnapshot(snapshot.State);
            int t0 = (int)snapshot.ElapsedSeconds;

            var pre = engine.FindAircraft(Callsign);
            Assert.NotNull(pre);
            Assert.IsType<VfrHoldPhase>(pre.Phases?.CurrentPhase);
            var holdPosition = pre.Position;

            for (int t = t0 + 1; t <= ReplayStopSeconds; t++)
            {
                engine.ReplayOneSecond();
            }

            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            double movedNm = GeoMath.DistanceNm(holdPosition, ac.Position);
            output.WriteLine(
                $"t={ReplayStopSeconds}: phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "none"} ias={ac.IndicatedAirspeed:F0} "
                    + $"fpType={ac.FlightPlan.AircraftType} suffix={ac.FlightPlan.EquipmentSuffix} moved={movedNm:F3} nm"
            );

            Assert.IsType<VfrHoldPhase>(ac.Phases?.CurrentPhase);
            Assert.True(ac.IndicatedAirspeed < 5, $"{Callsign} should still be hovering, IAS {ac.IndicatedAirspeed:F0}");
            Assert.True(movedNm < 0.05, "The hover must not have drifted off the fix.");
            Assert.Equal("R22", ac.FlightPlan.AircraftType);
            Assert.Equal("A", ac.FlightPlan.EquipmentSuffix);
        }
    }
}
