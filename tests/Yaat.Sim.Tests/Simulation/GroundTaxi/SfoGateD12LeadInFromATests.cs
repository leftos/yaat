using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-719: SFO gate D12 is taxied to straight off taxiway A. The vNAS map draws no lead-in line for D12, so the layout
/// build hung it off T6B (239 ft away, A is 287 ft) and <c>TAXI K A @D12</c> became <c>… A T6B @D12</c> through spot 6B. The
/// SFO sidecar's <c>standLeadIns</c> entry connects D12 to A's vertex at the A/T6B junction instead; its neighbour D14 still
/// hangs off T6B.
/// </summary>
public class SfoGateD12LeadInFromATests(ITestOutputHelper output)
{
    /// <summary>SKW1222 holding short of K on B, nosed 297.9° true.</summary>
    private static readonly (LatLon Position, TrueHeading Heading) HeldShortOfKOnB = (new LatLon(37.621827, -122.385547), new TrueHeading(297.9));

    [Fact]
    public void TaxiKA_ToD12_RollsOffA_WithoutT6BOrSpot6B()
    {
        if (Taxi("K A", "D12") is not { } taxied)
        {
            return;
        }

        (TaxiRoute route, AirportGroundLayout layout) = taxied;
        GroundNode d12 = Assert.IsType<GroundNode>(layout.FindParkingByName("D12"));
        GroundNode spot6B = Assert.IsType<GroundNode>(layout.FindSpotNodeByName("6B"));

        Assert.Equal(d12.Id, route.Segments[^1].ToNodeId);
        Assert.DoesNotContain(route.Segments, s => (s.Edge.Edge is not GroundArc) && s.Edge.Edge.MatchesTaxiway("T6B"));
        Assert.DoesNotContain(route.Segments, s => s.ToNodeId == spot6B.Id);
    }

    [Fact]
    public void TaxiKA_ToD14_StillRollsInOffT6B()
    {
        if (Taxi("K A", "D14") is not { } taxied)
        {
            return;
        }

        (TaxiRoute route, AirportGroundLayout layout) = taxied;
        GroundNode d14 = Assert.IsType<GroundNode>(layout.FindParkingByName("D14"));

        Assert.Equal(d14.Id, route.Segments[^1].ToNodeId);
        Assert.Contains(route.Segments, s => (s.Edge.Edge is not GroundArc) && s.Edge.Edge.MatchesTaxiway("T6B"));
    }

    /// <summary>SKW1222 (E75L) held short of K on B is cleared <c>TAXI {route} @{stand}</c>; null when SFO data is missing.</summary>
    private (TaxiRoute Route, AirportGroundLayout Layout)? Taxi(string taxiways, string stand)
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return null;
        }

        GroundNode holdNode = ground.Layout.Nodes.Values.MinBy(n => GeoMath.DistanceNm(n.Position, HeldShortOfKOnB.Position))!;
        var hold = new HoldShortPoint
        {
            NodeId = holdNode.Id,
            Reason = HoldShortReason.ExplicitHoldShort,
            TargetName = "K",
        };
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            "SKW1222",
            "E75L",
            (holdNode, HeldShortOfKOnB.Heading),
            new HoldingShortPhase(hold)
        );
        aircraft.Position = HeldShortOfKOnB.Position;
        aircraft.Ground.CurrentTaxiway = "B";

        string command = $"TAXI {taxiways} @{stand}";
        CommandResult result = ground.Engine.SendCommand("SKW1222", command);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()}");
        SfoGroundHarness.DumpRoute(output, route);
        return (route, ground.Layout);
    }
}
