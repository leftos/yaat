using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for taxi routes to spot destinations not overshooting the spot.
///
/// Bug: TAXI M2 $2, TAXI M4 M1 $1, TAXI T9 $9 at SFO all walk the entire
/// last taxiway past the spot, then A* routes back — creating a visible
/// U-turn. For TAXI M4 M1 $1, the overshoot crosses runway 1L, inserting
/// a hold-short that shouldn't exist in the route.
///
/// Root cause: WalkTaxiway has no stop condition for the destination spot
/// node — it uses destinationHintNode only for direction, not as a stop.
/// </summary>
public class SpotOvershootTaxiRouteTests(ITestOutputHelper output)
{
    private AirportGroundLayout? LoadSfoLayout()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var groundData = new TestAirportGroundData();
        return groundData.GetLayout("SFO");
    }

    private static AircraftState MakeSfoGroundAircraft(double lat, double lon)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(280),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "SFO" },
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingAfterPushbackPhase());
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
        };
        ac.Phases.Start(ctx);
        return ac;
    }

    /// <summary>
    /// Detect if a route contains backtracking (a node visited as both
    /// FromNodeId and later ToNodeId, or vice versa — indicating a U-turn).
    /// </summary>
    private static bool RouteContainsBacktracking(TaxiRoute route)
    {
        var visited = new HashSet<int>();
        foreach (var seg in route.Segments)
        {
            visited.Add(seg.FromNodeId);
            visited.Add(seg.ToNodeId);
        }

        // Check if any segment goes "backward" to a previously-traversed node
        // by checking if the route visits the same node as both intermediate
        // and then revisits. More precisely: check if there's a segment whose
        // ToNodeId matches a FromNodeId of an EARLIER segment (not adjacent).
        for (int i = 2; i < route.Segments.Count; i++)
        {
            int toId = route.Segments[i].ToNodeId;
            for (int j = 0; j < i - 1; j++)
            {
                if (route.Segments[j].FromNodeId == toId || route.Segments[j].ToNodeId == toId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Fact]
    public void TaxiM2ToSpot2_DoesNotOvershoot()
    {
        var layout = LoadSfoLayout();
        if (layout is null)
        {
            return;
        }

        // Spot 2 is on M2 at (37.608301, -122.386348).
        // Start from a position on M2 north of spot 2 (where aircraft would be
        // after PUSH M2) — approximate position of AMX669 after pushback.
        var spot2 = layout.FindSpotNodeByName("2");
        Assert.NotNull(spot2);

        // Find a node on M2 that's north (higher lat) of spot 2 — simulating
        // the aircraft being further up M2 after pushback.
        GroundNode? startNode = null;
        double bestDist = double.MaxValue;
        foreach (var node in layout.Nodes.Values)
        {
            bool onM2 = false;
            foreach (var edge in node.Edges)
            {
                if (edge.MatchesTaxiway("M2"))
                {
                    onM2 = true;
                    break;
                }
            }

            if (!onM2 || node.Id == spot2.Id)
            {
                continue;
            }

            // Pick a node on M2 that's north (higher lat) of spot 2
            if (node.Position.Lat > spot2.Position.Lat)
            {
                double dist = GeoMath.DistanceNm(node.Position.Lat, node.Position.Lon, spot2.Position.Lat, spot2.Position.Lon);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    startNode = node;
                }
            }
        }

        Assert.NotNull(startNode);
        output.WriteLine($"Start node: {startNode.Id} at ({startNode.Position.Lat:F6}, {startNode.Position.Lon:F6})");
        output.WriteLine($"Spot 2 node: {spot2.Id} at ({spot2.Position.Lat:F6}, {spot2.Position.Lon:F6})");

        var aircraft = MakeSfoGroundAircraft(startNode.Position.Lat, startNode.Position.Lon);
        var taxi = new TaxiCommand(Path: ["M2"], HoldShorts: [], DestinationSpot: "2");

        var result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        output.WriteLine($"Route summary: {route.ToSummary()}");
        output.WriteLine($"Route segments: {route.Segments.Count}");
        foreach (var seg in route.Segments)
        {
            output.WriteLine($"  {seg.FromNodeId} -> {seg.ToNodeId} on {seg.TaxiwayName}");
        }

        // The route should end at spot 2
        int lastToNode = route.Segments[^1].ToNodeId;
        Assert.Equal(spot2.Id, lastToNode);

        // The route should NOT backtrack (no U-turn)
        Assert.False(RouteContainsBacktracking(route), "Route contains backtracking — aircraft overshoots spot then U-turns back");
    }

    [Fact]
    public void TaxiM4M1ToSpot1_DoesNotOvershoot()
    {
        var layout = LoadSfoLayout();
        if (layout is null)
        {
            return;
        }

        // Spot 1 is on M1 at (37.608952, -122.385874).
        // JAL57 preset: PUSH M4, then TAXI M4 M1 $1
        var spot1 = layout.FindSpotNodeByName("1");
        Assert.NotNull(spot1);

        // Find the M4 node furthest from spot 1 (simulating post-pushback at
        // the far end of M4, which maximizes the path through M4 → M1).
        GroundNode? startNode = null;
        double bestStartDist = 0;
        foreach (var node in layout.Nodes.Values)
        {
            bool onM4 = false;
            foreach (var edge in node.Edges)
            {
                if (edge.MatchesTaxiway("M4"))
                {
                    onM4 = true;
                    break;
                }
            }

            if (!onM4)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(node.Position.Lat, node.Position.Lon, spot1.Position.Lat, spot1.Position.Lon);
            if (dist > bestStartDist)
            {
                bestStartDist = dist;
                startNode = node;
            }
        }

        Assert.NotNull(startNode);
        output.WriteLine($"Start node: {startNode.Id} at ({startNode.Position.Lat:F6}, {startNode.Position.Lon:F6})");
        output.WriteLine($"Spot 1 node: {spot1.Id} at ({spot1.Position.Lat:F6}, {spot1.Position.Lon:F6})");

        var aircraft = MakeSfoGroundAircraft(startNode.Position.Lat, startNode.Position.Lon);
        var taxi = new TaxiCommand(Path: ["M4", "M1"], HoldShorts: [], DestinationSpot: "1");

        var result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        output.WriteLine($"Route summary: {route.ToSummary()}");
        output.WriteLine($"Route segments: {route.Segments.Count}");
        foreach (var seg in route.Segments)
        {
            output.WriteLine($"  {seg.FromNodeId} -> {seg.ToNodeId} on {seg.TaxiwayName}");
        }

        // The route should end at spot 1
        int lastToNode = route.Segments[^1].ToNodeId;
        Assert.Equal(spot1.Id, lastToNode);

        // The route should NOT backtrack (no U-turn)
        Assert.False(RouteContainsBacktracking(route), "Route contains backtracking — aircraft overshoots spot then U-turns back");

        // The route should NOT have a hold-short for runway 1L —
        // spot 1 is before 1L on M1, so the route shouldn't reach 1L at all
        bool has1LHoldShort = route.HoldShortPoints.Any(hs => hs.TargetName is not null && hs.TargetName.Contains("1L"));
        Assert.False(has1LHoldShort, "Route to spot 1 should not cross runway 1L — spot is before the runway on M1");
    }

    [Fact]
    public void TaxiT9ToSpot9_DoesNotOvershoot()
    {
        var layout = LoadSfoLayout();
        if (layout is null)
        {
            return;
        }

        // Spot 9 on T9.
        var spot9 = layout.FindSpotNodeByName("9");
        Assert.NotNull(spot9);

        // Find a node on T9 to start from
        GroundNode? startNode = null;
        double bestDist = double.MaxValue;
        foreach (var node in layout.Nodes.Values)
        {
            bool onT9 = false;
            foreach (var edge in node.Edges)
            {
                if (edge.MatchesTaxiway("T9"))
                {
                    onT9 = true;
                    break;
                }
            }

            if (!onT9 || node.Id == spot9.Id)
            {
                continue;
            }

            // Pick a node on T9 that's further from spot 9 to simulate starting from parking
            double dist = GeoMath.DistanceNm(node.Position.Lat, node.Position.Lon, spot9.Position.Lat, spot9.Position.Lon);
            if (dist < bestDist)
            {
                bestDist = dist;
                startNode = node;
            }
        }

        Assert.NotNull(startNode);
        output.WriteLine($"Start node: {startNode.Id} at ({startNode.Position.Lat:F6}, {startNode.Position.Lon:F6})");
        output.WriteLine($"Spot 9 node: {spot9.Id} at ({spot9.Position.Lat:F6}, {spot9.Position.Lon:F6})");

        var aircraft = MakeSfoGroundAircraft(startNode.Position.Lat, startNode.Position.Lon);
        var taxi = new TaxiCommand(Path: ["T9"], HoldShorts: [], DestinationSpot: "9");

        var result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        output.WriteLine($"Route summary: {route.ToSummary()}");
        output.WriteLine($"Route segments: {route.Segments.Count}");
        foreach (var seg in route.Segments)
        {
            output.WriteLine($"  {seg.FromNodeId} -> {seg.ToNodeId} on {seg.TaxiwayName}");
        }

        // The route should end at spot 9
        int lastToNode = route.Segments[^1].ToNodeId;
        Assert.Equal(spot9.Id, lastToNode);

        // The route should NOT backtrack
        Assert.False(RouteContainsBacktracking(route), "Route contains backtracking — aircraft overshoots spot then U-turns back");
    }
}
