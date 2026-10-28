using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation.GroundTaxi;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-449: a grounded aircraft ends with no current phase. In the 0.8.5 KOAK recording SWA9701 is taxiing on W when the
/// t=420 snapshot is taken. The archive predates layout format 1, so a load pins OAK from today's layout, whose node
/// numbering differs from the one the snapshot's route was written against: the route restores as none
/// (<c>TaxiRoute.FromSnapshot</c> returns null on the first segment whose nodes are no longer joined), and the restored
/// <see cref="TaxiingPhase"/> ends on its first tick with nothing after it. The aircraft then sits on the taxiway with an
/// empty phase list, where no ground command has a dispatcher arm, so <c>TAXI</c> is refused.
/// </summary>
public class Yaat449RestoredTaxiWithUnresolvedRouteTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue222-oak-pushback-parked-neighbor-recording.zip";
    private const string Callsign = "SWA9701";

    /// <summary>SWA9701 taxiing east on W (recorded TAXI W B T @10 at t=380).</summary>
    private const int RestoreTime = 420;

    /// <summary>The speed below which a taxi may take its stop (TaxiingPhase's start-node hold arm speed).</summary>
    private const double HoldArmSpeedKts = 3.0;

    /// <summary>Seconds a jet at taxi speed needs, at most, to brake to a crawl at the ground decel rate.</summary>
    private const int StopWithinSeconds = 15;

    [Fact]
    public void Swa9701_RestoredMidTaxiWithARouteTheLayoutCannotResolve_HoldsInPositionAndTakesTaxi()
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            SessionRecording recording = archive.ToBaseSessionRecording();
            TestVnasData.EnsureInitialized();
            var groundData = RecordingGroundData.Evergreen(
                "pins what the server does with an archive that has no layout-format version: the restored route does not resolve on today's layout"
            );
            var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
            engine.Replay(recording, 0);

            TimedSnapshot snapshot = Assert.IsType<TimedSnapshot>(archive.ReadSnapshotAt(RestoreTime));
            engine.RestoreFromSnapshot(snapshot.State);

            // Precondition: the restore lands on a taxi whose recorded route today's OAK graph cannot rebuild.
            AircraftState? restored = engine.FindAircraft(Callsign);
            Assert.NotNull(restored);
            Assert.IsType<TaxiingPhase>(restored.Phases?.CurrentPhase);
            Assert.Null(restored.Ground.AssignedTaxiRoute);
            double restoredIas = restored.IndicatedAirspeed;
            Assert.True(restoredIas > HoldArmSpeedKts, $"test setup: SWA9701 restores at taxi speed, ias={restoredIas:F1}");

            // The taxi brakes to a crawl first and only then hands over to the hold: no instant stop from taxi speed, which
            // would put the hold in place on the first tick.
            AircraftState? ac = null;
            double previousIas = restoredIas;
            int brakingTicks = 0;
            for (int t = RestoreTime + 1; t <= RestoreTime + StopWithinSeconds; t++)
            {
                engine.ReplayOneSecond();
                ac = engine.FindAircraft(Callsign);
                Assert.NotNull(ac);
                output.WriteLine($"t={t}: phase={ac.Phases?.CurrentPhase?.Name ?? "(none)"} onGround={ac.IsOnGround} ias={ac.IndicatedAirspeed:F1}");
                Assert.True(ac.IsOnGround);
                if (ac.Phases?.CurrentPhase is HoldingInPositionPhase)
                {
                    break;
                }

                Assert.IsType<TaxiingPhase>(ac.Phases?.CurrentPhase);
                Assert.True(ac.IndicatedAirspeed <= previousIas, $"t={t}: ias rose to {ac.IndicatedAirspeed:F1} from {previousIas:F1} while braking");
                previousIas = ac.IndicatedAirspeed;
                brakingTicks++;
            }

            Assert.True(brakingTicks > 0, $"held on the first tick from {restoredIas:F1} kt: no braking before the hold");
            Assert.NotNull(ac);
            Assert.IsType<HoldingInPositionPhase>(ac.Phases?.CurrentPhase);
            Assert.True(ac.IndicatedAirspeed <= HoldArmSpeedKts, $"held at ias={ac.IndicatedAirspeed:F1}, above the {HoldArmSpeedKts} kt arm speed");

            // Along the occupied W, a node reference names where the taxi ends: W's end ahead, from the nearest W node ahead.
            AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(groundData.GetLayout("OAK"));
            GroundNode wAhead = layout
                .Nodes.Values.Where(n =>
                    n.Edges.Any(e => (e is GroundEdge) && e.MatchesTaxiway("W"))
                    && (GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, GeoMath.BearingTo(ac.Position, n.Position)) < 90.0)
                )
                .MinBy(n => GeoMath.DistanceNm(ac.Position, n.Position))!;
            GroundNode wEnd = TaxiStartsOnOccupiedTaxiwayTests.NodesAhead(wAhead, ac.TrueHeading.Degrees, "W").Last();
            string command = $"TAXI W #{wEnd.Id}";
            CommandResult taxi = engine.SendCommand(Callsign, command);
            output.WriteLine($"{command}: {taxi.Success} {taxi.Message}");
            Assert.True(taxi.Success, $"{command} refused after the restore: {taxi.Message}");
            AircraftState taxiing = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
            Assert.IsType<TaxiingPhase>(taxiing.Phases?.CurrentPhase);
            Assert.NotNull(taxiing.Ground.AssignedTaxiRoute);
        }
    }
}
