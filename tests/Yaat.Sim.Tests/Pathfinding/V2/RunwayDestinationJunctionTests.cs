using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding.V2;

/// <summary>
/// When the final named taxiway in an explicit clearance leads to a runway destination, the
/// transition INTO that taxiway must pick the junction whose taxiway side reaches the runway's
/// own hold-short — not merely the cheapest (nearest-along-the-previous-taxiway) junction.
///
/// <para>
/// Regression for the V2 departure-routing bug (OAK <c>TAXI D J C 33</c>, S2-OAK-4 N342T): C
/// intersects both A (east) and runway 33 (west). The cheapest J/C junction commits the terminus
/// walk eastward toward A, after which the westward turn toward 33 fails the U-turn admissibility
/// check, so the route detoured the long way round (C → A → B → cross 28L/10R → P → J → back to 33,
/// 131 segments) and the aircraft reached its departure runway ~340 s late, missing its takeoff
/// window. The fix anchors the final-transition junction selection toward the destination runway's
/// hold-short on the taxiway, so J→C picks the junction from which C leads straight to 33.
/// </para>
/// </summary>
public class RunwayDestinationJunctionTests
{
    private readonly ITestOutputHelper _output;

    public RunwayDestinationJunctionTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Oak_TaxiDJC_To33_RoutesStraightToRunway_NoADetour()
    {
        var layout = new TestAirportGroundData(FilletMode.V2).GetLayout("OAK");
        if (layout is null || TestVnasData.NavigationDb is null)
        {
            _output.WriteLine("oak layout / navdata unavailable — skipping");
            return;
        }

        // N342T's position when re-taxied "D J C 33" at t=324 (recorded snapshot), mid taxiway D.
        const double StartLat = 37.736298725227435;
        const double StartLon = -122.21898354608565;
        var startNode = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway("D")))
            .OrderBy(n => GeoMath.DistanceNm(StartLat, StartLon, n.Position.Lat, n.Position.Lon))
            .First();
        _output.WriteLine($"start node = {startNode.Id}");

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startNode.Id,
            ["D", "J", "C"],
            out string? failReason,
            new ExplicitPathOptions
            {
                DestinationRunway = "33",
                AirportId = "OAK",
                DiagnosticLog = msg => _output.WriteLine(msg),
            },
            AircraftCategory.Piston
        );

        Assert.NotNull(route);
        Assert.Null(failReason);

        _output.WriteLine($"Route: {route.Segments.Count} segments");
        foreach (var seg in route.Segments)
        {
            _output.WriteLine($"  {seg.TaxiwayName, -12} #{seg.FromNodeId} -> #{seg.ToNodeId}");
        }

        // The route must end at a hold-short for the destination runway 33.
        var destHs = route.HoldShortPoints.LastOrDefault(h => h.Reason == HoldShortReason.DestinationRunway);
        Assert.NotNull(destHs);
        Assert.Contains("33", destHs.TargetName ?? "");

        // It must go D -> J -> C -> 33 directly: never onto taxiway A (the wrong-direction detour
        // signature), and never across runway 28L/10R (which the detour crossed).
        Assert.DoesNotContain(route.Segments, s => s.TaxiwayName == "A");
        Assert.DoesNotContain(route.Segments, s => s.TaxiwayName.Contains("28L", StringComparison.Ordinal));

        // And it must be short — the detour was 131 segments; the direct route is ~36.
        Assert.True(
            route.Segments.Count < 60,
            $"TAXI D J C 33 resolved {route.Segments.Count} segments — expected the direct D-J-C-33 route (~36), "
                + "not the wrong-direction detour via A/B/28L-10R/P."
        );
    }
}
