using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// SFO's five alley carries two sub-lanes, T5 and T5A, that run side by side down the ramp and meet nowhere
/// on it — the graph joins them only out at the T5 / Alpha junction. <c>TAXI $5A</c> from gate D2 therefore
/// resolved to a 998 ft route down T5, out to Alpha and back up T5A for a move whose straight line is 529 ft
/// and whose two spots are 71 ft apart. The apron between the sub-lanes is aircraft-usable pavement the pilot
/// routes across at his own discretion (7110.65 §3-7-2 NOTE 2, AIM 4-3-20.g.7, AIM 2-3-4.c.2), so the
/// clearance is honoured by leaving the painted line where the lanes come abreast and driving straight in.
/// </summary>
public class SfoFiveAlleySpotCutTests
{
    /// <summary>The straight line from D2 to spot 5A is 529 ft; a route that still loops out to Alpha is 998 ft.</summary>
    private const double MaxRouteFt = 750.0;

    /// <summary>The cut from spot 5 crosses 71 ft; the min-total alternative crosses a 345 ft diagonal of open apron.</summary>
    private const double MaxCrossingFt = 150.0;

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
    public void GateD2_TaxiToSpot5A_CutsAcrossTheAlleyInsteadOfLoopingOutToAlpha()
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
        Assert.DoesNotContain(route.Segments, TouchesAlpha);

        TaxiRouteSegment crossing = route.Segments[^1];
        double crossingFt = crossing.Edge.DistanceNm * GeoMath.FeetPerNm;
        GroundNode? spot5 = ground.Layout.FindSpotNodeByName("5");
        Assert.True(spot5 is not null, "SFO layout has no spot named '5'");
        _output.WriteLine($"crossing: #{crossing.FromNodeId}-#{crossing.ToNodeId}({crossing.TaxiwayName}) {crossingFt:F0} ft, spot 5 = #{spot5.Id}");
        // These two pin the smallest-crossing objective, which the total-length bound below cannot: minimising
        // prefix + crossing instead cuts out at the far end of the lane across a 345 ft diagonal of open apron for a
        // 625 ft total, which passes that bound. A 345 ft diagonal fails both of these.
        Assert.True(
            crossingFt < MaxCrossingFt,
            $"the free-space crossing is {crossingFt:F0} ft; expected under {MaxCrossingFt:F0} ft (the cut from spot 5 measures 71 ft)"
        );
        Assert.Equal(spot5.Id, crossing.FromNodeId);
        // Measured baselines: the uncut graph route is 998 ft (down T5, out to the T5 / Alpha junction, back up T5A),
        // the straight line D2 -> spot 5A is 529 ft, and the cut route is 689 ft — down the lane to spot 5, then a
        // 71 ft crossing. The bound is a regression guard with headroom for fillet-geometry drift, not a derived limit.
        Assert.True(
            route.TotalDistanceFt < MaxRouteFt,
            $"route is {route.TotalDistanceFt:F0} ft for a {straightFt:F0} ft move; expected under {MaxRouteFt:F0} ft"
        );
    }
}
