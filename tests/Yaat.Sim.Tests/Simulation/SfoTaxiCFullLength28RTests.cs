using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-1261 (S1-SFO-3 Ground Control 28RT): VJT751 (GL7T), holding short of C on the ramp taxilane T421, was cleared
/// <c>TAXI C 28R</c> at t=635. C runs straight to its own full-length 28R bar on the north side, but the route left C
/// for C2 and held at C2's bar, 582 ft up the runway where C's own bar is 59 ft. The start node is four hops from the first C node,
/// past the bridge's three-hop reach, so the walk along C never started and the numbered-variant step auto-routed the
/// whole clearance from the start node to the variant's bar. The test restores the snapshot at or before t=634 (t=630) and issues
/// the same clearance: the route must hold at C's own 28R bar on the aircraft's side of the runway.
/// </summary>
public class SfoTaxiCFullLength28RTests(ITestOutputHelper output)
{
    private const string BundlePath = "TestData/sfo-gc-28rt-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "VJT751";

    [Fact]
    public void TaxiC28R_FromHoldingShortOfCOnT421_HoldsAtCsOwnNearSideBar()
    {
        // Pinned to the recorded layout: the restored snapshot's taxi route and hold-short name its node ids.
        var groundData = RecordingGroundData.VersionPinned(BundlePath, "TAXI C 28R holding at C's own full-length 28R bar, not C2's");
        TestVnasData.EnsureInitialized();
        RecordingArchive? archive = RecordingLoader.OpenArchive(BundlePath);
        Assert.NotNull(archive);

        using (archive)
        {
            var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
            engine.Replay(archive.ToBaseSessionRecording(), 0);
            TimedSnapshot snapshot = Assert.IsType<TimedSnapshot>(archive.ReadSnapshotAt(634));
            engine.RestoreFromSnapshot(snapshot.State);

            AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
            Assert.Equal("C", GroundCommandHandler.HeldShortTaxiway(aircraft));
            AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(aircraft.Ground.Layout);

            CommandResult result = engine.SendCommand(Callsign, "TAXI C 28R");
            Assert.True(result.Success, $"TAXI C 28R must be accepted: {result.Message}");

            TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
            output.WriteLine($"route: {route.ToSummary()}");
            HoldShortPoint lineup = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.DestinationRunway);
            GroundNode lineupNode = layout.Nodes[lineup.NodeId];
            output.WriteLine($"lineup bar #{lineup.NodeId} on {string.Join(", ", lineupNode.Edges.Select(e => e.TaxiwayName).Distinct())}");

            // C's own 28R bars: the north one, on the side VJT751 comes from, and the south one across the runway.
            List<GroundNode> cBars =
            [
                .. layout.Nodes.Values.Where(n =>
                    (n.Type == GroundNodeType.RunwayHoldShort)
                    && (n.RunwayId is { } runway)
                    && runway.Contains("28R")
                    && n.Edges.Any(e => e.TaxiwayName.Equals("C", StringComparison.OrdinalIgnoreCase))
                ),
            ];
            Assert.Equal(2, cBars.Count);
            GroundNode nearSideBar = cBars.MinBy(n => GeoMath.DistanceNm(n.Position, aircraft.Position))!;

            Assert.True(
                lineup.NodeId == nearSideBar.Id,
                $"TAXI C 28R held at #{lineup.NodeId} ({route.ToSummary()}), not at C's own near-side 28R bar #{nearSideBar.Id}"
            );
            Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.MatchesTaxiway("C2"));
        }
    }
}
