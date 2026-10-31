using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Free-space set-up belongs to the route's first segment alone: a virtual node further along is an ordinary route point,
/// whose corner is rounded onto the next segment's line and whose line is anchored at that node.
/// </summary>
public class MidRouteVirtualNodeTests(ITestOutputHelper output)
{
    private const string Callsign = "SKW3398";
    private const int ParkBudgetSeconds = 400;
    private const int SnapshotCompareSeconds = 30;

    /// <summary>
    /// A stand cut's roll-in runs from its virtual approach point to the stand. Every straight tick of that leg steers on
    /// the line anchored at the approach point, never on one laid from wherever the corner left the aircraft.
    /// </summary>
    [Fact]
    public void MidRouteVirtualNode_IsSetUpAsAPaintedPoint()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        (AircraftState aircraft, int rollInIndex, TaxiRouteSegment rollIn) = StartRollIn(ground);
        LatLon lineFrom = rollIn.Edge.FromNode.Position;
        var recorder = new TickRecorder(aircraft);
        int straightTicks = 0;
        int parkedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => aircraft.Phases?.CurrentPhase is AtParkingPhase,
            ParkBudgetSeconds,
            second =>
            {
                recorder.Record(second);
                if (
                    (aircraft.Ground.AssignedTaxiRoute?.CurrentSegmentIndex == rollInIndex)
                    && (aircraft.Ground.LastNavDiag is { OnArc: false } diag)
                    && (diag.TargetNodeId == rollIn.ToNodeId)
                )
                {
                    Assert.Equal(lineFrom, new LatLon(diag.SegFromLat, diag.SegFromLon));
                    straightTicks++;
                }
            }
        );

        recorder.WriteJson(Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "mid-route-virtual-node-ticks.json"));
        output.WriteLine($"{straightTicks} straight ticks on the roll-in; parked after {parkedAt} s");
        Assert.True(parkedAt > 0, $"{Callsign} never parked at D1: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.True(straightTicks > 0, "the roll-in played no straight");
    }

    /// <summary>
    /// A snapshot taken on the corner arc into the roll-in, restored into a second engine, keeps the restored aircraft on
    /// the original's positions every second: the restored navigator treats the leg as the original did, once the arc ends.
    /// </summary>
    [Fact]
    public void MidRouteVirtualNode_SnapshotOnItsCornerArc_RestoresTheSamePath()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        (AircraftState aircraft, int rollInIndex, _) = StartRollIn(ground);
        int onArc = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (aircraft.Ground.AssignedTaxiRoute?.CurrentSegmentIndex == rollInIndex) && aircraft.Ground.LastNavDiag is { OnArc: true },
            ParkBudgetSeconds,
            null
        );
        Assert.True(onArc > 0, "no entry arc played on the roll-in");

        SfoGround restoredGround = Assert.IsType<SfoGround>(SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off));
        restoredGround.Engine.RestoreFromSnapshot(RoundTrip(ground.Engine.CaptureSnapshot()));
        AircraftState restored = Assert.IsType<AircraftState>(restoredGround.Engine.FindAircraft(Callsign));

        for (int second = 1; second <= SnapshotCompareSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            restoredGround.Engine.TickOneSecond();
            Assert.Equal(aircraft.Phases?.CurrentPhase?.Name, restored.Phases?.CurrentPhase?.Name);
            Assert.Equal(aircraft.Position, restored.Position);
        }
    }

    /// <summary>Clears SKW3398 from D3 to D1, a route ending in the cut's roll-in, and returns that last segment.</summary>
    private (AircraftState Aircraft, int RollInIndex, TaxiRouteSegment RollIn) StartRollIn(SfoGround ground)
    {
        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, Callsign, "E75L", "D3");
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @D1");
        Assert.True(result.Success, result.Message);

        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        int rollInIndex = route.Segments.Count - 1;
        TaxiRouteSegment rollIn = route.Segments[rollInIndex];
        Assert.True(rollInIndex >= 1, "the roll-in is the route's first segment");
        Assert.True(rollIn.FromNodeId < 0, $"the roll-in starts at real node {rollIn.FromNodeId}, not the cut's virtual approach point");
        return (aircraft, rollInIndex, rollIn);
    }

    private static StateSnapshotDto RoundTrip(StateSnapshotDto snapshot) =>
        Assert.IsType<StateSnapshotDto>(
            JsonSerializer.Deserialize<StateSnapshotDto>(
                JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default),
                RecordingJsonOptions.Default
            )
        );
}
