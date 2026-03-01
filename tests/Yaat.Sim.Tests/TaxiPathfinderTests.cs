using Yaat.Sim.Data.Airport;
using Xunit;

namespace Yaat.Sim.Tests;

public class TaxiPathfinderTests
{
    /// <summary>
    /// Build a simple layout with a linear taxiway: A -> B -> C -> D
    /// and a branch: B -> E
    /// </summary>
    private static AirportGroundLayout BuildSimpleLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        var nodeA = new GroundNode { Id = 0, Latitude = 37.7, Longitude = -122.2, Type = GroundNodeType.TaxiwayIntersection, Name = "A" };
        var nodeB = new GroundNode { Id = 1, Latitude = 37.701, Longitude = -122.2, Type = GroundNodeType.TaxiwayIntersection, Name = "B" };
        var nodeC = new GroundNode { Id = 2, Latitude = 37.702, Longitude = -122.2, Type = GroundNodeType.TaxiwayIntersection, Name = "C" };
        var nodeD = new GroundNode { Id = 3, Latitude = 37.703, Longitude = -122.2, Type = GroundNodeType.TaxiwayIntersection, Name = "D" };
        var nodeE = new GroundNode { Id = 4, Latitude = 37.701, Longitude = -122.201, Type = GroundNodeType.TaxiwayIntersection, Name = "E" };
        var parking = new GroundNode { Id = 5, Latitude = 37.7005, Longitude = -122.201, Type = GroundNodeType.Parking, Name = "P1", Heading = 90 };

        layout.Nodes[0] = nodeA;
        layout.Nodes[1] = nodeB;
        layout.Nodes[2] = nodeC;
        layout.Nodes[3] = nodeD;
        layout.Nodes[4] = nodeE;
        layout.Nodes[5] = parking;

        var edgeAB = new GroundEdge { FromNodeId = 0, ToNodeId = 1, TaxiwayName = "A", DistanceNm = 0.06 };
        var edgeBC = new GroundEdge { FromNodeId = 1, ToNodeId = 2, TaxiwayName = "A", DistanceNm = 0.06 };
        var edgeCD = new GroundEdge { FromNodeId = 2, ToNodeId = 3, TaxiwayName = "A", DistanceNm = 0.06 };
        var edgeBE = new GroundEdge { FromNodeId = 1, ToNodeId = 4, TaxiwayName = "B", DistanceNm = 0.05 };
        var edgePB = new GroundEdge { FromNodeId = 5, ToNodeId = 1, TaxiwayName = "RAMP", DistanceNm = 0.04 };

        layout.Edges.AddRange([edgeAB, edgeBC, edgeCD, edgeBE, edgePB]);

        // Wire adjacency
        nodeA.Edges.Add(edgeAB);
        nodeB.Edges.AddRange([edgeAB, edgeBC, edgeBE, edgePB]);
        nodeC.Edges.AddRange([edgeBC, edgeCD]);
        nodeD.Edges.Add(edgeCD);
        nodeE.Edges.Add(edgeBE);
        parking.Edges.Add(edgePB);

        return layout;
    }

    [Fact]
    public void FindRoute_ShortestPath_ReturnsValidRoute()
    {
        var layout = BuildSimpleLayout();

        // Route from A (0) to D (3) should go A -> B -> C -> D
        var route = TaxiPathfinder.FindRoute(layout, 0, 3);

        Assert.NotNull(route);
        Assert.Equal(3, route.Segments.Count);
        Assert.Equal(0, route.Segments[0].FromNodeId);
        Assert.Equal(1, route.Segments[0].ToNodeId);
        Assert.Equal(3, route.Segments[^1].ToNodeId);
    }

    [Fact]
    public void FindRoute_FromParking_IncludesRampSegment()
    {
        var layout = BuildSimpleLayout();

        // Route from parking P1 (5) to D (3)
        var route = TaxiPathfinder.FindRoute(layout, 5, 3);

        Assert.NotNull(route);
        Assert.True(route.Segments.Count >= 3);
        Assert.Equal(5, route.Segments[0].FromNodeId);
    }

    [Fact]
    public void FindRoute_Unreachable_ReturnsNull()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        layout.Nodes[0] = new GroundNode { Id = 0, Latitude = 37.7, Longitude = -122.2, Type = GroundNodeType.TaxiwayIntersection };
        layout.Nodes[1] = new GroundNode { Id = 1, Latitude = 37.8, Longitude = -122.3, Type = GroundNodeType.TaxiwayIntersection };
        // No edges connecting them

        var route = TaxiPathfinder.FindRoute(layout, 0, 1);
        Assert.Null(route);
    }

    [Fact]
    public void FindRoute_SameNode_ReturnsEmptyRoute()
    {
        var layout = BuildSimpleLayout();

        var route = TaxiPathfinder.FindRoute(layout, 0, 0);

        Assert.NotNull(route);
        Assert.Empty(route.Segments);
    }

    [Fact]
    public void ResolveExplicitPath_ValidPath_ReturnsRoute()
    {
        var layout = BuildSimpleLayout();

        // Explicit path: taxiway A from node 0
        var route = TaxiPathfinder.ResolveExplicitPath(
            layout, fromNodeId: 0, taxiwayNames: ["A"]);

        Assert.NotNull(route);
        Assert.True(route.Segments.Count > 0);
        Assert.All(route.Segments, s =>
            Assert.Equal("A", s.TaxiwayName));
    }

    [Fact]
    public void TaxiRoute_ToSummary_FormatsCorrectly()
    {
        var route = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment
                {
                    FromNodeId = 0, ToNodeId = 1, TaxiwayName = "S",
                    Edge = new GroundEdge { FromNodeId = 0, ToNodeId = 1, TaxiwayName = "S", DistanceNm = 0.1 },
                },
                new TaxiRouteSegment
                {
                    FromNodeId = 1, ToNodeId = 2, TaxiwayName = "T",
                    Edge = new GroundEdge { FromNodeId = 1, ToNodeId = 2, TaxiwayName = "T", DistanceNm = 0.1 },
                },
            ],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    RunwayId = "28L",
                },
            ],
        };

        Assert.Equal("S T HS 28L", route.ToSummary());
    }
}
