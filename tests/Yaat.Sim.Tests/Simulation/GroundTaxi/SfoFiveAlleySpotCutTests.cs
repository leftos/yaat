using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// SFO's five alley carries two sub-lanes, T5 and T5A, that run side by side down the ramp and meet nowhere on
/// it — the graph joins them only out at the T5 / Alpha junction. <c>TAXI $5A</c> from gate D2 used to be
/// honoured by leaving the painted line at spot 5 and driving the 71 ft straight across to spot 5A, which left
/// the aircraft stopped on the marking at the crossing's own heading — 45° across a lane that runs 118 / 298.
/// A spot is entered along the lane it sits on, so the clearance takes the alley's 998 ft route out to Alpha
/// and back up T5A instead, and the apron cut is not offered for a spot destination.
/// </summary>
public class SfoFiveAlleySpotCutTests
{
    /// <summary>The lane route down T5, out to Alpha and back up T5A measures 998 ft; the bound is a drift guard.</summary>
    private const double MaxRouteFt = 1200.0;

    private readonly ITestOutputHelper _output;

    public SfoFiveAlleySpotCutTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static bool OnTaxiway(GroundNode node, string twy) => node.Edges.Any(e => e.MatchesTaxiway(twy));

    /// <summary>The T5 / Alpha junction the detour turns around at is the only node on this route that touches Alpha.</summary>
    private static bool TouchesAlpha(TaxiRouteSegment seg) => OnTaxiway(seg.Edge.FromNode, "A") || OnTaxiway(seg.Edge.ToNode, "A");

    /// <summary>
    /// Every node of the uncut graph route with how far along it lies, how far the stand is from it, and what a cut
    /// there would cost — the table that says which cut point is the shortest and by how much, so a failure of the
    /// length assertion is read off the output instead of re-derived.
    /// </summary>
    private void LogCutCandidates(AirportGroundLayout layout, GroundNode gate, GroundNode stand)
    {
        TaxiRoute? graph = TaxiPathfinder.FindRoute(layout, gate.Id, stand.Id, AircraftCategory.Jet);
        if (graph is null)
        {
            return;
        }

        var rows = new List<string>();
        for (int i = 0; i <= graph.Segments.Count; i++)
        {
            GroundNode node = i == 0 ? graph.Segments[0].Edge.FromNode : graph.Segments[i - 1].Edge.ToNode;
            double alongFt = graph.PrefixDistanceFt(i);
            double cutFt = GeoMath.DistanceNm(node.Position, stand.Position) * GeoMath.FeetPerNm;
            double remainingFt = graph.TotalDistanceFt - alongFt;
            rows.Add(
                $"#{node.Id} along {alongFt:F0} cut {cutFt:F0} left {remainingFt:F0} "
                    + $"ratio {(cutFt > 0 ? remainingFt / cutFt : 0):F2} result {alongFt + cutFt:F0}"
            );
        }

        _output.WriteLine($"uncut graph route {graph.TotalDistanceFt:F0} ft; candidates: {string.Join(" | ", rows)}");
    }

    [Fact]
    public void GateD2_TaxiToSpot5A_ArrivesUpT5AInsteadOfCuttingAcrossTheAlley()
    {
        SfoGround? built = SfoGroundHarness.Build(_output, autoCross: false);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot5A = ground.Layout.FindSpotNodeByName("5A");
        Assert.True(spot5A is not null, "SFO layout has no spot named '5A'");
        GroundNode? gate = ground.Layout.FindParkingByName("D2");
        Assert.True(gate is not null, "SFO layout has no parking named 'D2'");

        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "UAL1234", "B738", "D2");
        CommandResult result = ground.Engine.SendCommand("UAL1234", "TAXI $5A");
        _output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        double straightFt = GeoMath.DistanceNm(gate.Position, spot5A.Position) * GeoMath.FeetPerNm;
        _output.WriteLine($"gate D2 = #{gate.Id}, spot 5A = #{spot5A.Id}, straight line {straightFt:F0} ft");
        _output.WriteLine("route: " + string.Join(" ", route.Segments.Select(s => $"{s.FromNodeId}-{s.ToNodeId}({s.TaxiwayName})")));
        _output.WriteLine($"route length: {route.TotalDistanceFt:F0} ft over {route.Segments.Count} segments");

        LogCutCandidates(ground.Layout, gate, spot5A);

        Assert.Equal("5A", route.DestinationSpot);
        Assert.Equal(spot5A.Id, route.Segments[^1].ToNodeId);

        // The lanes meet only at Alpha, so honouring the spot's own lane means going round by it.
        Assert.Contains(route.Segments, TouchesAlpha);

        TaxiRouteSegment arrival = route.Segments[^1];
        _output.WriteLine($"arrival leg: #{arrival.FromNodeId}-#{arrival.ToNodeId}({arrival.TaxiwayName})");
        Assert.True(arrival.Edge.Edge.MatchesTaxiway("T5A"), $"the last leg arrives on {arrival.TaxiwayName}, not up spot 5A's own lane");
        // The shape the rule forbids: a free-space apron leg that ends on the spot marking, which stops the
        // aircraft across its lane. The 71 ft cut from spot 5 is exactly that.
        Assert.DoesNotContain(route.Segments, s => (s.ToNodeId == spot5A.Id) && s.TaxiwayName.Equals("RAMP", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            route.TotalDistanceFt < MaxRouteFt,
            $"route is {route.TotalDistanceFt:F0} ft for a {straightFt:F0} ft move; expected under {MaxRouteFt:F0} ft"
        );
    }
}
