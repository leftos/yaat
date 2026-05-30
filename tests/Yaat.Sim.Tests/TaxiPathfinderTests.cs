using System.IO;
using System.Linq;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// V1-only regression pins for the static <see cref="TaxiPathfinder"/> implementation. Every test here
/// calls the V1 static API directly (e.g. the 3-arg <c>FindRoute(layout, from, to)</c> with no aircraft
/// category) on synthetic graphs and on real OAK/SFO layouts built with the default (Legacy) fillet mode,
/// and asserts V1's exact route shapes. They are intentionally NOT routed through
/// <see cref="TaxiPathfinderRouter"/>: V2 legitimately produces different (correct) routes on this
/// geometry, so migrating these to <c>TaxiPathfinderRouter.Current</c> would make them fail the moment the
/// joint flip makes V2 the default. They are deleted together with V1 at that flip. Router-tracking /
/// V2 behaviour coverage for the same scenarios lives in the all-V2 suites
/// (<c>FilletV2TaxiCoverageTests</c>, the <c>*_OnV2</c> tests, the V2 Acceptance collection) and in the
/// explicit-path comparison harness (<c>PathfinderComparison</c>).
/// </summary>
[Collection("NavDbMutator")]
public class TaxiPathfinderTests
{
    public TaxiPathfinderTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// Build a simple layout with a linear taxiway: A -> B -> C -> D
    /// and a branch: B -> E
    /// </summary>
    private static AirportGroundLayout BuildSimpleLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        var nodeA = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.7, -122.2),
            Type = GroundNodeType.TaxiwayIntersection,
            Name = "A",
        };
        var nodeB = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.701, -122.2),
            Type = GroundNodeType.TaxiwayIntersection,
            Name = "B",
        };
        var nodeC = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.702, -122.2),
            Type = GroundNodeType.TaxiwayIntersection,
            Name = "C",
        };
        var nodeD = new GroundNode
        {
            Id = 3,
            Position = new LatLon(37.703, -122.2),
            Type = GroundNodeType.TaxiwayIntersection,
            Name = "D",
        };
        var nodeE = new GroundNode
        {
            Id = 4,
            Position = new LatLon(37.701, -122.201),
            Type = GroundNodeType.TaxiwayIntersection,
            Name = "E",
        };
        var parking = new GroundNode
        {
            Id = 5,
            Position = new LatLon(37.7005, -122.201),
            Type = GroundNodeType.Parking,
            Name = "P1",
            TrueHeading = new TrueHeading(90),
        };

        layout.Nodes[0] = nodeA;
        layout.Nodes[1] = nodeB;
        layout.Nodes[2] = nodeC;
        layout.Nodes[3] = nodeD;
        layout.Nodes[4] = nodeE;
        layout.Nodes[5] = parking;

        var edgeAB = new GroundEdge
        {
            Nodes = [nodeA, nodeB],
            TaxiwayName = "A",
            DistanceNm = 0.06,
        };
        var edgeBC = new GroundEdge
        {
            Nodes = [nodeB, nodeC],
            TaxiwayName = "A",
            DistanceNm = 0.06,
        };
        var edgeCD = new GroundEdge
        {
            Nodes = [nodeC, nodeD],
            TaxiwayName = "A",
            DistanceNm = 0.06,
        };
        var edgeBE = new GroundEdge
        {
            Nodes = [nodeB, nodeE],
            TaxiwayName = "B",
            DistanceNm = 0.05,
        };
        var edgePB = new GroundEdge
        {
            Nodes = [parking, nodeB],
            TaxiwayName = "RAMP",
            DistanceNm = 0.04,
        };

        layout.Edges.AddRange([edgeAB, edgeBC, edgeCD, edgeBE, edgePB]);

        // Wire adjacency
        nodeA.Edges.Add(edgeAB);
        nodeB.Edges.AddRange([edgeAB, edgeBC, edgeBE, edgePB]);
        nodeC.Edges.AddRange([edgeBC, edgeCD]);
        nodeD.Edges.Add(edgeCD);
        nodeE.Edges.Add(edgeBE);
        parking.Edges.Add(edgePB);

        layout.RebuildAdjacencyLists();
        return layout;
    }

    /// <summary>
    /// Build a layout with numbered taxiway variants connecting to runway hold-shorts.
    /// Node 0 --[A]--> Node 1 --[W]--> Node 2 --[W]--> Node 3
    ///                                   |                |
    ///                                 [W1]             [W2]
    ///                                   |                |
    ///                                 HS1 (12/30)      HS2 (12/30)
    ///
    /// HS1 is closer to runway 30 threshold, HS2 is closer to runway 12 threshold.
    /// </summary>
    private static AirportGroundLayout BuildVariantLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "KTEST" };

        var node0 = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.700, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.701, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.702, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node3 = new GroundNode
        {
            Id = 3,
            Position = new LatLon(37.703, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        // HS1 near runway 30 threshold (further north)
        var hs1 = new GroundNode
        {
            Id = 10,
            Position = new LatLon(37.704, -122.200),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = new RunwayIdentifier("12", "30"),
        };
        // HS2 near runway 12 threshold (further south)
        var hs2 = new GroundNode
        {
            Id = 11,
            Position = new LatLon(37.700, -122.201),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = new RunwayIdentifier("12", "30"),
        };

        layout.Nodes[0] = node0;
        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;
        layout.Nodes[3] = node3;
        layout.Nodes[10] = hs1;
        layout.Nodes[11] = hs2;

        var edgeA01 = new GroundEdge
        {
            Nodes = [node0, node1],
            TaxiwayName = "A",
            DistanceNm = 0.06,
        };
        var edgeW12 = new GroundEdge
        {
            Nodes = [node1, node2],
            TaxiwayName = "W",
            DistanceNm = 0.06,
        };
        var edgeW23 = new GroundEdge
        {
            Nodes = [node2, node3],
            TaxiwayName = "W",
            DistanceNm = 0.06,
        };
        var edgeW1_2_hs1 = new GroundEdge
        {
            Nodes = [node2, hs1],
            TaxiwayName = "W1",
            DistanceNm = 0.04,
        };
        var edgeW2_3_hs2 = new GroundEdge
        {
            Nodes = [node3, hs2],
            TaxiwayName = "W2",
            DistanceNm = 0.04,
        };

        layout.Edges.AddRange([edgeA01, edgeW12, edgeW23, edgeW1_2_hs1, edgeW2_3_hs2]);

        node0.Edges.Add(edgeA01);
        node1.Edges.AddRange([edgeA01, edgeW12]);
        node2.Edges.AddRange([edgeW12, edgeW23, edgeW1_2_hs1]);
        node3.Edges.AddRange([edgeW23, edgeW2_3_hs2]);
        hs1.Edges.Add(edgeW1_2_hs1);
        hs2.Edges.Add(edgeW2_3_hs2);

        layout.RebuildAdjacencyLists();
        return layout;
    }

    /// <summary>
    /// Build a layout where the hold-short is reachable only via a different-letter taxiway.
    /// Node 0 --[A]--> Node 1 --[W]--> Node 2
    ///                                   |
    ///                                  [Z]
    ///                                   |
    ///                                 HS (12/30)
    /// </summary>
    private static AirportGroundLayout BuildAmbiguousLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "KTEST" };

        var node0 = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.700, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.701, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.702, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var hs = new GroundNode
        {
            Id = 10,
            Position = new LatLon(37.703, -122.200),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = new RunwayIdentifier("12", "30"),
        };

        layout.Nodes[0] = node0;
        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;
        layout.Nodes[10] = hs;

        var edgeA01 = new GroundEdge
        {
            Nodes = [node0, node1],
            TaxiwayName = "A",
            DistanceNm = 0.06,
        };
        var edgeW12 = new GroundEdge
        {
            Nodes = [node1, node2],
            TaxiwayName = "W",
            DistanceNm = 0.06,
        };
        var edgeZ2hs = new GroundEdge
        {
            Nodes = [node2, hs],
            TaxiwayName = "Z",
            DistanceNm = 0.04,
        };

        layout.Edges.AddRange([edgeA01, edgeW12, edgeZ2hs]);

        node0.Edges.Add(edgeA01);
        node1.Edges.AddRange([edgeA01, edgeW12]);
        node2.Edges.AddRange([edgeW12, edgeZ2hs]);
        hs.Edges.Add(edgeZ2hs);

        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static TaxiRouteSegment MakeSegment(int fromId, int toId, string taxiwayName, double distanceNm = 0.1)
    {
        var fromNode = new GroundNode
        {
            Id = fromId,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var toNode = new GroundNode
        {
            Id = toId,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var edge = new GroundEdge
        {
            Nodes = [fromNode, toNode],
            TaxiwayName = taxiwayName,
            DistanceNm = distanceNm,
        };
        return new TaxiRouteSegment { TaxiwayName = taxiwayName, Edge = edge.Directed(fromNode, toNode) };
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
        layout.Nodes[0] = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.7, -122.2),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[1] = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.8, -122.3),
            Type = GroundNodeType.TaxiwayIntersection,
        };
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
        var route = TaxiPathfinder.ResolveExplicitPath(layout, fromNodeId: 0, taxiwayNames: ["A"], out _, new ExplicitPathOptions());

        Assert.NotNull(route);
        Assert.True(route.Segments.Count > 0);
        Assert.All(route.Segments, s => Assert.Equal("A", s.TaxiwayName));
    }

    [Fact]
    public void TaxiRoute_ToSummary_FormatsCorrectly()
    {
        var route = new TaxiRoute
        {
            Segments = [MakeSegment(0, 1, "S", 0.1), MakeSegment(1, 2, "T", 0.1)],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "28L",
                },
            ],
        };

        Assert.Equal("S T HS 28L", route.ToSummary());
    }

    // --- IsNumberedVariant helper tests ---

    [Theory]
    [InlineData("W1", "W", true)]
    [InlineData("W10", "W", true)]
    [InlineData("WA", "W", false)]
    [InlineData("W", "W", false)]
    [InlineData("w1", "W", true)]
    [InlineData("AB1", "AB", true)]
    [InlineData("AB", "AB", false)]
    [InlineData("A", "AB", false)]
    public void IsNumberedVariant_ReturnsExpected(string candidate, string baseName, bool expected)
    {
        Assert.Equal(expected, TaxiVariantResolver.IsNumberedVariant(candidate, baseName));
    }

    // --- RunwayIdentifier.Contains tests (used by pathfinder for hold-short matching) ---

    [Theory]
    [InlineData("12", "30", "30", true)]
    [InlineData("12", "30", "12", true)]
    [InlineData("12", "30", "6", false)]
    [InlineData("30", "30", "30", true)]
    [InlineData("30L", "30L", "30L", true)]
    [InlineData("12L", "30R", "30R", true)]
    public void RunwayIdentifierContains_ReturnsExpected(string end1, string end2, string target, bool expected)
    {
        Assert.Equal(expected, new RunwayIdentifier(end1, end2).Contains(target));
    }

    // --- Variant auto-inference tests ---

    [Fact]
    public void VariantInference_SingleVariant_AutoExtends()
    {
        // Build layout where only W1 connects to a hold-short for runway 30
        var layout = new AirportGroundLayout { AirportId = "KTEST" };

        var node0 = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.700, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.701, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.702, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var hs = new GroundNode
        {
            Id = 10,
            Position = new LatLon(37.703, -122.200),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = new RunwayIdentifier("12", "30"),
        };

        layout.Nodes[0] = node0;
        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;
        layout.Nodes[10] = hs;

        var edgeA = new GroundEdge
        {
            Nodes = [layout.Nodes[0], layout.Nodes[1]],
            TaxiwayName = "A",
            DistanceNm = 0.06,
        };
        var edgeW = new GroundEdge
        {
            Nodes = [layout.Nodes[1], layout.Nodes[2]],
            TaxiwayName = "W",
            DistanceNm = 0.06,
        };
        var edgeW1 = new GroundEdge
        {
            Nodes = [layout.Nodes[2], layout.Nodes[10]],
            TaxiwayName = "W1",
            DistanceNm = 0.04,
        };

        layout.Edges.AddRange([edgeA, edgeW, edgeW1]);
        node0.Edges.Add(edgeA);
        node1.Edges.AddRange([edgeA, edgeW]);
        node2.Edges.AddRange([edgeW, edgeW1]);
        hs.Edges.Add(edgeW1);
        layout.RebuildAdjacencyLists();

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            0,
            ["A", "W"],
            out string? failReason,
            new ExplicitPathOptions { DestinationRunway = "30" }
        );

        Assert.NotNull(route);
        Assert.Null(failReason);
        // Route should end at HS node (10)
        Assert.Equal(10, route.Segments[^1].ToNodeId);
        // Should include a W1 segment
        Assert.Contains(route.Segments, s => string.Equals(s.TaxiwayName, "W1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VariantInference_MultipleVariants_PicksClosestToThreshold()
    {
        var layout = BuildVariantLayout();

        // Runway 30 threshold is further north (37.710)
        using var _ = NavigationDatabase.ScopedOverride(
            TestNavDbFactory.WithRunways(
                new RunwayInfo
                {
                    AirportId = "KTEST",
                    Id = new RunwayIdentifier("30", "12"),
                    Designator = "30",
                    Lat1 = 37.710,
                    Lon1 = -122.200,
                    TrueHeading1 = new TrueHeading(300),
                    Elevation1Ft = 0,
                    Lat2 = 37.690,
                    Lon2 = -122.200,
                    TrueHeading2 = new TrueHeading(120),
                    Elevation2Ft = 0,
                    LengthFt = 5000,
                    WidthFt = 150,
                }
            )
        );

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            0,
            ["A", "W"],
            out string? failReason,
            new ExplicitPathOptions { DestinationRunway = "30", AirportId = "KTEST" }
        );

        Assert.NotNull(route);
        Assert.Null(failReason);
        // HS1 (id=10, lat 37.704) is closer to 37.710 threshold than HS2 (id=11, lat 37.700)
        // So W1 should be chosen
        Assert.Equal(10, route.Segments[^1].ToNodeId);
        Assert.Contains(route.Segments, s => string.Equals(s.TaxiwayName, "W1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VariantInference_AlreadyReachesHoldShort_NoInference()
    {
        var layout = BuildVariantLayout();

        // Explicitly specify W1 — route should walk W then W1 normally
        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            0,
            ["A", "W", "W1"],
            out string? failReason,
            new ExplicitPathOptions { DestinationRunway = "30" }
        );

        Assert.NotNull(route);
        Assert.Null(failReason);
        // Should end at HS1 (node 10)
        Assert.Equal(10, route.Segments[^1].ToNodeId);
    }

    [Fact]
    public void VariantInference_NoDestinationRunway_NoInference()
    {
        var layout = BuildVariantLayout();

        // No destination runway — walk A, then stop on W as soon as the aircraft enters
        // it. The no-destination truncation rule: a TAXI command ending in a taxiway
        // (no parking, spot, or destination runway) holds once it transitions onto the
        // last named taxiway, instead of walking it to the dead-end.
        var route = TaxiPathfinder.ResolveExplicitPath(layout, 0, ["A", "W"], out string? failReason, new ExplicitPathOptions());

        Assert.NotNull(route);
        Assert.Null(failReason);
        // Last segment ends at node 2 (first W segment past the A/W transition), not at
        // node 3 (end of W).
        Assert.Equal(2, route.Segments[^1].ToNodeId);
        Assert.Equal("W", route.Segments[^1].TaxiwayName);
    }

    [Fact]
    public void VariantInference_AmbiguousConnector_ReturnsNullWithReason()
    {
        var layout = BuildAmbiguousLayout();

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            0,
            ["A", "W"],
            out string? failReason,
            new ExplicitPathOptions { DestinationRunway = "30" }
        );

        Assert.Null(route);
        Assert.NotNull(failReason);
        Assert.Contains("Z", failReason);
        Assert.Contains("30", failReason);
    }

    [Fact]
    public void VariantInference_NoConnectors_ProceedsNormally()
    {
        // Layout with a hold-short for a different runway — no connectors for "30"
        var layout = new AirportGroundLayout { AirportId = "KTEST" };

        var node0 = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.700, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.701, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.702, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        layout.Nodes[0] = node0;
        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;

        var edgeA = new GroundEdge
        {
            Nodes = [layout.Nodes[0], layout.Nodes[1]],
            TaxiwayName = "A",
            DistanceNm = 0.06,
        };
        var edgeW = new GroundEdge
        {
            Nodes = [layout.Nodes[1], layout.Nodes[2]],
            TaxiwayName = "W",
            DistanceNm = 0.06,
        };

        layout.Edges.AddRange([edgeA, edgeW]);
        node0.Edges.Add(edgeA);
        node1.Edges.AddRange([edgeA, edgeW]);
        node2.Edges.Add(edgeW);
        layout.RebuildAdjacencyLists();

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            0,
            ["A", "W"],
            out string? failReason,
            new ExplicitPathOptions { DestinationRunway = "30" }
        );

        Assert.NotNull(route);
        Assert.Null(failReason);
        // Proceeds with existing behavior — ends at node 2 with destination hold-short
        Assert.Equal(2, route.Segments[^1].ToNodeId);
    }

    // --- FindRoutes (K-shortest paths) tests ---

    [Fact]
    public void FindRoutes_LinearLayout_ReturnsSingleRoute()
    {
        // A linear A→B→C→D layout has only one path
        var layout = BuildSimpleLayout();
        var routes = TaxiPathfinder.FindRoutes(layout, 0, 3);

        Assert.Single(routes);
        Assert.Equal(3, routes[0].Segments.Count);
    }

    [Fact]
    public void FindRoutes_DiamondLayout_ReturnsTwoRoutes()
    {
        // Diamond: 0→1 via T, 0→2 via U, 1→3 via V, 2→3 via W
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        var n0 = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.700, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.701, -122.201),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.701, -122.199),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n3 = new GroundNode
        {
            Id = 3,
            Position = new LatLon(37.702, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        layout.Nodes[0] = n0;
        layout.Nodes[1] = n1;
        layout.Nodes[2] = n2;
        layout.Nodes[3] = n3;

        var e01 = new GroundEdge
        {
            Nodes = [layout.Nodes[0], layout.Nodes[1]],
            TaxiwayName = "T",
            DistanceNm = 0.06,
        };
        var e02 = new GroundEdge
        {
            Nodes = [layout.Nodes[0], layout.Nodes[2]],
            TaxiwayName = "U",
            DistanceNm = 0.06,
        };
        var e13 = new GroundEdge
        {
            Nodes = [layout.Nodes[1], layout.Nodes[3]],
            TaxiwayName = "V",
            DistanceNm = 0.06,
        };
        var e23 = new GroundEdge
        {
            Nodes = [layout.Nodes[2], layout.Nodes[3]],
            TaxiwayName = "W",
            DistanceNm = 0.06,
        };

        layout.Edges.AddRange([e01, e02, e13, e23]);
        n0.Edges.AddRange([e01, e02]);
        n1.Edges.AddRange([e01, e13]);
        n2.Edges.AddRange([e02, e23]);
        n3.Edges.AddRange([e13, e23]);
        layout.RebuildAdjacencyLists();

        var routes = TaxiPathfinder.FindRoutes(layout, 0, 3);

        Assert.Equal(2, routes.Count);
        // Routes should use different taxiway sequences
        var key0 = TaxiPathfinder.BuildTaxiwayKey(routes[0]);
        var key1 = TaxiPathfinder.BuildTaxiwayKey(routes[1]);
        Assert.NotEqual(key0, key1);
    }

    [Fact]
    public void FindRoutes_Unreachable_ReturnsEmptyList()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        layout.Nodes[0] = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.7, -122.2),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[1] = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.8, -122.3),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        var routes = TaxiPathfinder.FindRoutes(layout, 0, 1);

        Assert.Empty(routes);
    }

    [Fact]
    public void FindRoutes_DeduplicatesSameTaxiwaySequence()
    {
        // Layout where two paths share the same taxiway sequence
        // 0 --[A]--> 1 --[B]--> 3
        // 0 --[A]--> 2 --[B]--> 3
        // Both are A|B — should deduplicate to one
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        var n0 = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.700, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.701, -122.201),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.701, -122.199),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n3 = new GroundNode
        {
            Id = 3,
            Position = new LatLon(37.702, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        layout.Nodes[0] = n0;
        layout.Nodes[1] = n1;
        layout.Nodes[2] = n2;
        layout.Nodes[3] = n3;

        var e01 = new GroundEdge
        {
            Nodes = [layout.Nodes[0], layout.Nodes[1]],
            TaxiwayName = "A",
            DistanceNm = 0.06,
        };
        var e02 = new GroundEdge
        {
            Nodes = [layout.Nodes[0], layout.Nodes[2]],
            TaxiwayName = "A",
            DistanceNm = 0.07,
        };
        var e13 = new GroundEdge
        {
            Nodes = [layout.Nodes[1], layout.Nodes[3]],
            TaxiwayName = "B",
            DistanceNm = 0.06,
        };
        var e23 = new GroundEdge
        {
            Nodes = [layout.Nodes[2], layout.Nodes[3]],
            TaxiwayName = "B",
            DistanceNm = 0.05,
        };

        layout.Edges.AddRange([e01, e02, e13, e23]);
        n0.Edges.AddRange([e01, e02]);
        n1.Edges.AddRange([e01, e13]);
        n2.Edges.AddRange([e02, e23]);
        n3.Edges.AddRange([e13, e23]);
        layout.RebuildAdjacencyLists();

        var routes = TaxiPathfinder.FindRoutes(layout, 0, 3);

        // Both routes are A|B — deduplication should keep only one
        Assert.Single(routes);
    }

    [Fact]
    public void FindRoutes_CappedAtMaxRoutes()
    {
        // Build a grid with many possible paths, request only 2
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        // 5-node grid: 0→1→4, 0→2→4, 0→3→4 (3 distinct paths)
        var n0 = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.700, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.701, -122.202),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.701, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n3 = new GroundNode
        {
            Id = 3,
            Position = new LatLon(37.701, -122.198),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n4 = new GroundNode
        {
            Id = 4,
            Position = new LatLon(37.702, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        layout.Nodes[0] = n0;
        layout.Nodes[1] = n1;
        layout.Nodes[2] = n2;
        layout.Nodes[3] = n3;
        layout.Nodes[4] = n4;

        var e01 = new GroundEdge
        {
            Nodes = [layout.Nodes[0], layout.Nodes[1]],
            TaxiwayName = "T",
            DistanceNm = 0.06,
        };
        var e02 = new GroundEdge
        {
            Nodes = [layout.Nodes[0], layout.Nodes[2]],
            TaxiwayName = "U",
            DistanceNm = 0.06,
        };
        var e03 = new GroundEdge
        {
            Nodes = [layout.Nodes[0], layout.Nodes[3]],
            TaxiwayName = "V",
            DistanceNm = 0.06,
        };
        var e14 = new GroundEdge
        {
            Nodes = [layout.Nodes[1], layout.Nodes[4]],
            TaxiwayName = "W",
            DistanceNm = 0.06,
        };
        var e24 = new GroundEdge
        {
            Nodes = [layout.Nodes[2], layout.Nodes[4]],
            TaxiwayName = "X",
            DistanceNm = 0.06,
        };
        var e34 = new GroundEdge
        {
            Nodes = [layout.Nodes[3], layout.Nodes[4]],
            TaxiwayName = "Y",
            DistanceNm = 0.06,
        };

        layout.Edges.AddRange([e01, e02, e03, e14, e24, e34]);
        n0.Edges.AddRange([e01, e02, e03]);
        n1.Edges.AddRange([e01, e14]);
        n2.Edges.AddRange([e02, e24]);
        n3.Edges.AddRange([e03, e34]);
        n4.Edges.AddRange([e14, e24, e34]);
        layout.RebuildAdjacencyLists();

        var routes = TaxiPathfinder.FindRoutes(layout, 0, 4, maxRoutes: 2);

        Assert.True(routes.Count <= 2);
        Assert.True(routes.Count >= 1);
    }

    [Fact]
    public void BuildTaxiwayKey_SkipsRwyAndRamp()
    {
        var route = new TaxiRoute
        {
            Segments =
            [
                MakeSegment(0, 1, "RAMP", 0.01),
                MakeSegment(1, 2, "T", 0.05),
                MakeSegment(2, 3, "RWY28L", 0.1),
                MakeSegment(3, 4, "U", 0.05),
            ],
            HoldShortPoints = [],
        };

        Assert.Equal("T|U", TaxiPathfinder.BuildTaxiwayKey(route));
    }

    [Fact]
    public void TotalDistanceNm_SumsSegments()
    {
        var route = new TaxiRoute { Segments = [MakeSegment(0, 1, "A", 0.1), MakeSegment(1, 2, "B", 0.2)], HoldShortPoints = [] };

        Assert.Equal(0.3, route.TotalDistanceNm, precision: 10);
    }

    // --- Integration tests using real airport GeoJSON ---

    private const string TestDataDir = "TestData";

    private static AirportGroundLayout? LoadAirportLayout(string airportId, string subdir)
    {
        string path = Path.Combine(TestDataDir, $"{subdir}.geojson");
        if (File.Exists(path))
        {
            return GeoJsonParser.Parse(airportId, File.ReadAllText(path), null);
        }

        return null;
    }

    [Fact]
    public void OAK_LayoutLoads_HasWVariants()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.Count > 0, "Should have nodes");
        Assert.True(layout.Edges.Count > 0, "Should have edges");

        // Verify W and W1 taxiway edges exist
        var twNames = layout.Edges.Select(e => e.TaxiwayName).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("W", twNames);
        Assert.Contains("W1", twNames);
        Assert.Contains("W2", twNames);

        // Verify hold-short nodes exist for runway 30/12
        var holdShorts = layout
            .Nodes.Values.Where(n =>
                n.Type == GroundNodeType.RunwayHoldShort && n.RunwayId is not null && n.RunwayId is { } rId && rId.Contains("30")
            )
            .ToList();
        Assert.True(holdShorts.Count > 0, "Should have hold-shorts for runway 30");

        // Hold-short nodes must be connected to the graph (edge split fix)
        foreach (var hs in holdShorts)
        {
            Assert.True(hs.Edges.Count > 0, $"Hold-short node {hs.Id} ({hs.RunwayId}) should have edges but has 0");
        }

        // Check that B's FromNodeId is actually in the graph
        var bEdge = layout.Edges.First(e => e.MatchesTaxiway("B"));
        Assert.True(layout.Nodes.ContainsKey(bEdge.Nodes[0].Id), $"B edge Nodes[0].Id {bEdge.Nodes[0].Id} should exist in nodes");

        // Check we can walk B from its first node
        var bStartNode = layout.Nodes[bEdge.Nodes[0].Id];
        bool hasBEdge = bStartNode.Edges.Any(e => e.MatchesTaxiway("B"));
        Assert.True(hasBEdge, $"Node {bEdge.Nodes[0].Id} should have B edges");
    }

    [Fact]
    public void OAK_WalkW3_Succeeds()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // Find all W3 edges
        var w3Edges = layout.Edges.Where(e => e.MatchesTaxiway("W3")).ToList();
        Assert.True(w3Edges.Count > 0, "Should have W3 edges");

        // Try walking from each W3 endpoint
        foreach (var edge in w3Edges)
        {
            var route1 = TaxiPathfinder.ResolveExplicitPath(layout, edge.Nodes[0].Id, ["W3"], out _, new ExplicitPathOptions());
            var route2 = TaxiPathfinder.ResolveExplicitPath(layout, edge.Nodes[1].Id, ["W3"], out _, new ExplicitPathOptions());

            // At least one direction should work
            if (route1 is not null || route2 is not null)
            {
                int startId = route1 is not null ? edge.Nodes[0].Id : edge.Nodes[1].Id;
                // Now try W3 then W
                var combined = TaxiPathfinder.ResolveExplicitPath(layout, startId, ["W3", "W"], out _, new ExplicitPathOptions());
                if (combined is not null)
                {
                    return; // Success
                }
            }
        }

        // If we get here, diagnose WHY walking fails
        var firstW3 = w3Edges[0];
        var node = layout.Nodes[firstW3.Nodes[0].Id];
        var edgeNames = string.Join(", ", node.Edges.Select(e => e.TaxiwayName));
        Assert.Fail($"Could not walk W3. Node {firstW3.Nodes[0].Id} edges: [{edgeNames}]. " + $"W3 edges count: {w3Edges.Count}");
    }

    [Fact]
    public void OAK_TaxiW3W_ToRunway30_InfersWVariant()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return; // Skip if vzoa files not available
        }

        // WalkTaxiway walks one direction; try multiple W3 starting points to
        // find one where the walk reaches the W junction and then continues.
        TaxiRoute? route = null;
        string? failReason = null;

        var w3Edges = layout.Edges.Where(e => e.MatchesTaxiway("W3")).ToList();
        Assert.True(w3Edges.Count > 0, "Should have W3 edges");

        var triedNodes = new HashSet<int>();
        foreach (var edge in w3Edges)
        {
            foreach (int nodeId in new[] { edge.Nodes[0].Id, edge.Nodes[1].Id })
            {
                if (!triedNodes.Add(nodeId))
                {
                    continue;
                }

                route = TaxiPathfinder.ResolveExplicitPath(
                    layout,
                    nodeId,
                    ["W3", "W"],
                    out failReason,
                    new ExplicitPathOptions { DestinationRunway = "30" }
                );

                if (route is not null)
                {
                    break;
                }
            }

            if (route is not null)
            {
                break;
            }
        }

        Assert.NotNull(route);
        Assert.Null(failReason);

        // Route should have auto-inferred a W-variant (W1 or W2)
        bool hasVariant = route.Segments.Any(s => TaxiVariantResolver.IsNumberedVariant(s.TaxiwayName, "W"));
        Assert.True(hasVariant, $"Expected W-variant segment, got: {string.Join(" -> ", route.Segments.Select(s => s.TaxiwayName))}");

        // Route should pass through a hold-short for runway 30 (the variant walk
        // may continue past the hold-short to W1's end node)
        bool passesHoldShort = route.Segments.Any(s =>
            layout.Nodes.TryGetValue(s.ToNodeId, out var n)
            && n.Type == GroundNodeType.RunwayHoldShort
            && n.RunwayId is not null
            && n.RunwayId is { } rId
            && rId.Contains("30")
        );
        Assert.True(passesHoldShort, "Route should pass through a hold-short node for runway 30");
    }

    [Fact]
    public void OAK_TaxiW3WW1_ToRunway30_NoInferenceNeeded()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // Try multiple W3 starting points (walk direction is sensitive to start)
        TaxiRoute? route = null;
        string? failReason = null;

        var w3Edges = layout.Edges.Where(e => e.MatchesTaxiway("W3")).ToList();
        Assert.True(w3Edges.Count > 0, "Should have W3 edges");

        var triedNodes = new HashSet<int>();
        foreach (var edge in w3Edges)
        {
            foreach (int nodeId in new[] { edge.Nodes[0].Id, edge.Nodes[1].Id })
            {
                if (!triedNodes.Add(nodeId))
                {
                    continue;
                }

                route = TaxiPathfinder.ResolveExplicitPath(
                    layout,
                    nodeId,
                    ["W3", "W", "W1"],
                    out failReason,
                    new ExplicitPathOptions { DestinationRunway = "30" }
                );

                if (route is not null)
                {
                    break;
                }
            }

            if (route is not null)
            {
                break;
            }
        }

        Assert.NotNull(route);
        Assert.Null(failReason);
    }

    [Fact]
    public void OAK_TaxiBW_ToRunway30_E2E()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // Simulate "RWY 30 TAXI B W": find a node on B and route to runway 30.
        // Try multiple B starting points since WalkTaxiway is direction-sensitive.
        TaxiRoute? route = null;
        string? failReason = null;
        int usedStartNode = -1;

        var bEdges = layout.Edges.Where(e => e.MatchesTaxiway("B")).ToList();
        Assert.True(bEdges.Count > 0, "Should have B edges");

        var triedNodes = new HashSet<int>();
        foreach (var edge in bEdges)
        {
            foreach (int nodeId in new[] { edge.Nodes[0].Id, edge.Nodes[1].Id })
            {
                if (!triedNodes.Add(nodeId))
                {
                    continue;
                }

                route = TaxiPathfinder.ResolveExplicitPath(
                    layout,
                    nodeId,
                    ["B", "W"],
                    out failReason,
                    new ExplicitPathOptions { DestinationRunway = "30" }
                );

                if (route is not null)
                {
                    usedStartNode = nodeId;
                    break;
                }
            }

            if (route is not null)
            {
                break;
            }
        }

        // Collect diagnostics on failure
        if (route is null)
        {
            // Check what hold-shorts exist for runway 30 and their edge details
            var hs30 = layout
                .Nodes.Values.Where(n =>
                    n.Type == GroundNodeType.RunwayHoldShort && n.RunwayId is not null && n.RunwayId is { } rId && rId.Contains("30")
                )
                .ToList();
            var hsInfo = string.Join("; ", hs30.Select(n => $"HS {n.Id} edges=[{string.Join(",", n.Edges.Select(e => e.TaxiwayName))}]"));

            Assert.Fail(
                $"Route B W to RWY 30 returned null. " + $"failReason={failReason ?? "null"}, " + $"hold-shorts for 30: {hs30.Count} ({hsInfo})"
            );
        }

        // Route should contain B and W segments
        Assert.Contains(route.Segments, s => string.Equals(s.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(route.Segments, s => string.Equals(s.TaxiwayName, "W", StringComparison.OrdinalIgnoreCase));

        // Route should reach a hold-short for runway 30 — either via variant
        // inference (W1-W7) or by W itself crossing the runway
        bool passesRwy30HoldShort = route.Segments.Any(s =>
            layout.Nodes.TryGetValue(s.ToNodeId, out var n)
            && n.Type == GroundNodeType.RunwayHoldShort
            && n.RunwayId is not null
            && n.RunwayId is { } rId
            && rId.Contains("30")
        );

        var segSummary = string.Join(
            " ",
            route
                .Segments.Select(s => s.TaxiwayName)
                .Aggregate(
                    new List<string>(),
                    (acc, name) =>
                    {
                        if (acc.Count == 0 || !string.Equals(acc[^1], name, StringComparison.OrdinalIgnoreCase))
                        {
                            acc.Add(name);
                        }
                        return acc;
                    }
                )
        );
        Assert.True(
            passesRwy30HoldShort,
            $"Route should pass through runway 30 hold-short. "
                + $"Taxiways: {segSummary}, "
                + $"start={usedStartNode}, "
                + $"end={route.Segments[^1].ToNodeId}"
        );
    }

    /// <summary>
    /// Regression: TAXI ... RWY 30 via W → W1 must terminate at the runway
    /// hold-short, not on the runway centerline. Previously,
    /// TaxiVariantResolver.AutoExtendVariant called WalkTaxiway without
    /// StopAtRunwayId, so the W1 walk ran past node 41 (RunwayHoldShort 30/12)
    /// to node 42 (TaxiwayIntersection on the runway centerline). Aircraft
    /// reaching that pose got stuck because LineUpGeometry can't plan a path
    /// from on-centerline / divergent-heading.
    /// </summary>
    [Fact]
    public void OAK_TaxiBW_ToRunway30_TerminatesAtHoldShort()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        TaxiRoute? route = null;
        string? failReason = null;

        var bEdges = layout.Edges.Where(e => e.MatchesTaxiway("B")).ToList();
        Assert.True(bEdges.Count > 0, "Should have B edges");

        var triedNodes = new HashSet<int>();
        foreach (var edge in bEdges)
        {
            foreach (int nodeId in new[] { edge.Nodes[0].Id, edge.Nodes[1].Id })
            {
                if (!triedNodes.Add(nodeId))
                {
                    continue;
                }

                route = TaxiPathfinder.ResolveExplicitPath(
                    layout,
                    nodeId,
                    ["B", "W"],
                    out failReason,
                    new ExplicitPathOptions { DestinationRunway = "30" }
                );

                if (route is not null)
                {
                    break;
                }
            }

            if (route is not null)
            {
                break;
            }
        }

        Assert.NotNull(route);

        int terminalId = route.Segments[^1].ToNodeId;
        Assert.True(layout.Nodes.TryGetValue(terminalId, out var terminalNode), $"Terminal node #{terminalId} missing from layout");

        Assert.True(
            terminalNode.Type == GroundNodeType.RunwayHoldShort,
            $"Route must terminate at a RunwayHoldShort node, not on the runway. "
                + $"Terminal node #{terminalId} is {terminalNode.Type} "
                + $"at ({terminalNode.Position.Lat:F6}, {terminalNode.Position.Lon:F6})."
        );

        Assert.True(
            terminalNode.RunwayId is { } terminalRwy && terminalRwy.Contains("30"),
            $"Terminal hold-short #{terminalId} must be for runway 30, but RunwayId={terminalNode.RunwayId?.ToString() ?? "(null)"}"
        );
    }

    [Fact]
    public void OAK_TaxiDF_CrossesRunway15_33()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // Find a starting node on taxiway D (before the 15/33 crossing)
        var dEdges = layout.Edges.Where(e => e.MatchesTaxiway("D")).ToList();
        Assert.True(dEdges.Count > 0, "Should have D edges");

        // Try resolving D → F from multiple starting nodes until one succeeds
        TaxiRoute? route = null;
        int usedStartNode = -1;
        var triedNodes = new HashSet<int>();

        foreach (var edge in dEdges)
        {
            foreach (int nodeId in new[] { edge.Nodes[0].Id, edge.Nodes[1].Id })
            {
                if (!triedNodes.Add(nodeId))
                {
                    continue;
                }

                var candidate = TaxiPathfinder.ResolveExplicitPath(layout, nodeId, ["D", "F"], out _, new ExplicitPathOptions());
                if (candidate is not null)
                {
                    route = candidate;
                    usedStartNode = nodeId;
                    break;
                }
            }

            if (route is not null)
            {
                break;
            }
        }

        Assert.NotNull(route);

        // Check hold-short points for 15/33
        var hs1533 = route.HoldShortPoints.Where(hs => hs.TargetName is not null && hs.TargetName.Contains("15")).ToList();

        // Identify which taxiway segment each hold-short falls on
        var hsDetails = new List<string>();
        foreach (var hs in route.HoldShortPoints)
        {
            string twName = "?";
            foreach (var seg in route.Segments)
            {
                if (seg.ToNodeId == hs.NodeId)
                {
                    twName = seg.TaxiwayName;
                    break;
                }
            }

            hsDetails.Add($"node={hs.NodeId} target={hs.TargetName} reason={hs.Reason} taxiway={twName}");
        }

        var segSummary = string.Join(
            " ",
            route
                .Segments.Select(s => s.TaxiwayName)
                .Aggregate(
                    new List<string>(),
                    (acc, name) =>
                    {
                        if (acc.Count == 0 || !string.Equals(acc[^1], name, StringComparison.OrdinalIgnoreCase))
                        {
                            acc.Add(name);
                        }
                        return acc;
                    }
                )
        );

        // Collect distinct 15/33 HS nodes the route passes through
        var seenHsNodeIds = new HashSet<int>();
        var allRwy15NodesOnRoute = new List<string>();
        foreach (var seg in route.Segments)
        {
            if (
                layout.Nodes.TryGetValue(seg.ToNodeId, out var node)
                && node.Type == GroundNodeType.RunwayHoldShort
                && node.RunwayId is { } rId
                && rId.Contains("15")
                && seenHsNodeIds.Add(node.Id)
            )
            {
                allRwy15NodesOnRoute.Add($"node={node.Id} taxiway={seg.TaxiwayName}");
            }
        }

        Assert.True(
            hs1533.Count > 0,
            $"Route D → F should have hold-short(s) for runway 15/33. "
                + $"Taxiways: {segSummary}, start={usedStartNode}, "
                + $"hold-shorts: [{string.Join("; ", hsDetails)}], "
                + $"all 15/33 HS nodes on route: [{string.Join("; ", allRwy15NodesOnRoute)}]"
        );

        // Verify the CROSS 15 command would clear the hold-short(s)
        foreach (var hs in hs1533)
        {
            Assert.True(RunwayIdentifier.Parse(hs.TargetName!).Contains("15"), $"Hold-short target '{hs.TargetName}' should match '15'");
        }

        // Route crosses runway 15/33 via D → F. With the no-destination truncation rule,
        // the route stops at the first F segment past the crossing — controllers who don't
        // give a destination get "hold once you enter the next taxiway" semantics. The
        // entry-side HS on D is still annotated; the exit-side HS on F is past the truncation.
        var segDetail = string.Join(
            " → ",
            route.Segments.Select(s =>
            {
                var n = layout.Nodes.TryGetValue(s.ToNodeId, out var nd) ? nd : null;
                string typ = n?.Type == GroundNodeType.RunwayHoldShort ? $"HS({n.RunwayId})" : "";
                return $"{s.ToNodeId}:{s.TaxiwayName}{typ}";
            })
        );

        // Route includes the entry-side 15/33 HS on D, plus the runway centerline crossing,
        // plus at least one F segment past the runway.
        Assert.Contains(allRwy15NodesOnRoute, n => n.Contains("taxiway=D"));

        // Entry/exit pairing produces exactly one hold-short (entry side on D)
        Assert.True(
            hs1533.Count == 1,
            $"Expected 1 hold-short for 15/33 (entry only), got {hs1533.Count}: "
                + $"[{string.Join("; ", hs1533.Select(h => $"node={h.NodeId} reason={h.Reason}"))}]. "
                + $"Segments: {segDetail}"
        );
        Assert.Equal(HoldShortReason.RunwayCrossing, hs1533[0].Reason);

        // Truncation lands the aircraft on F just past the crossing (last segment is F).
        Assert.Equal("F", route.Segments[^1].TaxiwayName);
    }

    /// <summary>
    /// Regression test for issue #53: AAL2839 "TAXI B M1 1L" at SFO produced a 47-segment route
    /// due to WalkTaxiway picking the wrong direction on M1 at the B/M1 junction. The walk should
    /// stop at the 1L hold-short on M1 in a small number of segments, not overshoot to the dead end
    /// and require TaxiVariantResolver to stitch a return path.
    /// </summary>
    [Fact]
    public void SFO_TaxiBM1_To1L_ProducesShortRoute()
    {
        var layout = LoadAirportLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        // Start near AAL2839's position on taxiway B close to the B/M1 junction
        const double StartLat = 37.609046;
        const double StartLon = -122.383669;
        var startNode = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway("B")))
            .OrderBy(n => Math.Abs(n.Position.Lat - StartLat) + Math.Abs(n.Position.Lon - StartLon))
            .First();

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startNode.Id,
            ["B", "M1"],
            out string? failReason,
            new ExplicitPathOptions { DestinationRunway = "1L" }
        );

        Assert.NotNull(route);
        Assert.Null(failReason);

        // Should end at the 1L hold-short node
        var endNode = layout.Nodes[route.Segments[^1].ToNodeId];
        Assert.Equal(GroundNodeType.RunwayHoldShort, endNode.Type);
        Assert.NotNull(endNode.RunwayId);
        Assert.True(endNode.RunwayId!.Value.Contains("1L"), $"Expected end at 1L hold-short, got runwayId={endNode.RunwayId}");

        // Should be a compact route — previously produced 47 segments; correct is ~3-6
        Assert.True(
            route.Segments.Count <= 10,
            $"Route too long: {route.Segments.Count} segments (expected ≤10). Taxiways: [{string.Join(",", route.Segments.Select(s => s.TaxiwayName).Distinct())}]"
        );
    }

    /// <summary>
    /// Issue #53 follow-up: "TAXI Y H B M1 HS 01L" (explicit hold-short, no destination runway)
    /// must produce the same compact route as "TAXI Y H B M1 1L" (destination runway).
    /// Without the fix, M1 walk has no direction guidance and walks the wrong way.
    /// </summary>
    [Fact]
    public void SFO_TaxiYHBM1_WithExplicitHS_ProducesShortRoute()
    {
        var layout = LoadAirportLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        // Start from a parking node connected to Y (SWA7348 is at parking B12 → node on Y)
        var parkingNode = layout.Nodes.Values.FirstOrDefault(n =>
            n.Type == GroundNodeType.Parking
            && n.Edges.Any(e => e.IsRamp)
            && layout.Nodes.Values.Any(adj => adj.Edges.Any(ae => ae.MatchesTaxiway("Y")) && n.Edges.Any(pe => pe.HasNode(adj.Id)))
        );
        if (parkingNode is null)
        {
            return;
        }

        // With explicit hold-short (HS keyword), no destination runway
        var routeHs = TaxiPathfinder.ResolveExplicitPath(
            layout,
            parkingNode.Id,
            ["Y", "H", "B", "M1"],
            out string? failReasonHs,
            new ExplicitPathOptions { ExplicitHoldShorts = ["1L"] }
        );

        // With destination runway (trailing runway)
        var routeRwy = TaxiPathfinder.ResolveExplicitPath(
            layout,
            parkingNode.Id,
            ["Y", "H", "B", "M1"],
            out string? failReasonRwy,
            new ExplicitPathOptions { DestinationRunway = "1L" }
        );

        Assert.NotNull(routeHs);
        Assert.Null(failReasonHs);
        Assert.NotNull(routeRwy);
        Assert.Null(failReasonRwy);

        // Both routes should have similar segment counts (HS route may differ by hold-short annotations,
        // but the underlying path segments must be the same)
        Assert.Equal(routeRwy.Segments.Count, routeHs.Segments.Count);

        // HS route must stop at the 1L hold-short, not walk the entire M1 taxiway
        var endNode = layout.Nodes[routeHs.Segments[^1].ToNodeId];
        Assert.Equal(GroundNodeType.RunwayHoldShort, endNode.Type);
        Assert.True(endNode.RunwayId!.Value.Contains("1L"), $"Expected end at 1L hold-short, got runwayId={endNode.RunwayId}");
    }

    [Fact]
    public void SFO_LayoutLoads_WithHoldShorts()
    {
        var layout = LoadAirportLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Edges.Count > 0, "SFO layout should have edges");
        Assert.True(layout.Nodes.Count > 0, "SFO layout should have nodes");

        bool hasHoldShorts = layout.Nodes.Values.Any(n => n.Type == GroundNodeType.RunwayHoldShort);
        Assert.True(hasHoldShorts, "SFO layout should have hold-short nodes");
    }

    // --- Taxiway hold-short via HS keyword in TAXI command (real OAK layout) ---

    [Fact]
    public void OAK_TaxiDHoldShortD_FromParking_HoldsBeforeEnteringD()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // NEW7 is a parking spot north of taxiway D
        var parkingNode = layout.Nodes.Values.FirstOrDefault(n => n.Type == GroundNodeType.Parking && n.Name == "NEW7");
        Assert.NotNull(parkingNode);

        // TAXI D HS D
        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: parkingNode.Id,
            taxiwayNames: ["D"],
            out string? failReason,
            new ExplicitPathOptions { ExplicitHoldShorts = ["D"] }
        );

        Assert.NotNull(route);
        Assert.Null(failReason);

        // Route should have segments (RAMP to reach D, then D segments)
        Assert.True(route.Segments.Count >= 2, $"Expected RAMP + D segments, got {route.Segments.Count}");

        // Should have D segments in the route
        Assert.Contains(route.Segments, s => string.Equals(s.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase));

        // Should have an explicit hold-short for taxiway D
        var hsD = route
            .HoldShortPoints.Where(h =>
                h.Reason == HoldShortReason.ExplicitHoldShort && string.Equals(h.TargetName, "D", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        Assert.True(hsD.Count > 0, "Should have an explicit hold-short for taxiway D");

        // The hold-short should be BEFORE the first D segment (aircraft holds at RAMP→D junction)
        int hsNodeId = hsD[0].NodeId;
        int firstDSegFromNode = route.Segments.First(s => string.Equals(s.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase)).FromNodeId;
        Assert.Equal(firstDSegFromNode, hsNodeId);
    }

    [Fact]
    public void OAK_TaxiDHoldShortD_RouteContinuesBeyondHoldShort()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parkingNode = layout.Nodes.Values.FirstOrDefault(n => n.Type == GroundNodeType.Parking && n.Name == "NEW7");
        Assert.NotNull(parkingNode);

        // TAXI D HS D — route should extend along D past the hold-short
        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: parkingNode.Id,
            taxiwayNames: ["D"],
            out _,
            new ExplicitPathOptions { ExplicitHoldShorts = ["D"] }
        );

        Assert.NotNull(route);

        var hsD = route.HoldShortPoints.First(h =>
            h.Reason == HoldShortReason.ExplicitHoldShort && string.Equals(h.TargetName, "D", StringComparison.OrdinalIgnoreCase)
        );

        // There should be D segments past the hold-short node (aircraft continues along D when cleared)
        bool hasDSegmentsPastHs = route.Segments.Any(s =>
            s.FromNodeId == hsD.NodeId && string.Equals(s.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase)
        );
        Assert.True(hasDSegmentsPastHs, "Route should have D segments past the hold-short (aircraft continues when cleared)");
    }

    [Fact]
    public void OAK_TaxiDHoldShortD_HoldShortSummaryIncludesHSD()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parkingNode = layout.Nodes.Values.FirstOrDefault(n => n.Type == GroundNodeType.Parking && n.Name == "NEW7");
        Assert.NotNull(parkingNode);

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: parkingNode.Id,
            taxiwayNames: ["D"],
            out _,
            new ExplicitPathOptions { ExplicitHoldShorts = ["D"] }
        );

        Assert.NotNull(route);

        // ToSummary should include "HS D"
        string summary = route.ToSummary();
        Assert.Contains("HS", summary);
        Assert.Contains("D", summary);
    }

    // --- Runway exit E2E tests ---

    [Fact]
    public void OAK_FindNearestExit_28R_Midpoint_ExitIsOffRunway()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // N569SX rollout position: approximately 60% down runway 28R
        // Runway heading ~292 degrees
        double acLat = 37.726374;
        double acLon = -122.209536;
        double rwyHeading = 292.0;

        var exitNode = layout.FindNearestExit(acLat, acLon, new TrueHeading(rwyHeading), null);
        Assert.NotNull(exitNode);

        double distToExit = GeoMath.DistanceNm(new LatLon(acLat, acLon), exitNode.Position);
        bool hasRunwayEdge = exitNode.Edges.Any(e => e.IsRunwayCenterline);
        string exitTaxiway = layout.GetExitTaxiwayName(exitNode) ?? "?";
        var edgeNames = string.Join(", ", exitNode.Edges.Select(e => e.TaxiwayName));

        // Diagnostic output
        var output =
            $"Exit node {exitNode.Id}: type={exitNode.Type}, taxiway={exitTaxiway}, "
            + $"pos=({exitNode.Position.Lat:F6},{exitNode.Position.Lon:F6}), "
            + $"dist={distToExit:F4}nm ({distToExit * 6076:F0}ft), "
            + $"hasRwyEdge={hasRunwayEdge}, edges=[{edgeNames}]";

        // Verify: exit node must have taxiway edges so the aircraft can leave
        bool hasTaxiwayEdge = exitNode.Edges.Any(e => !e.IsRunwayCenterline);
        Assert.True(hasTaxiwayEdge, $"Exit node has no taxiway edges — aircraft is stuck on runway. {output}");

        // Verify: exit node must be geometrically OFF the runway rectangle
        var rwy28R = layout.Runways.First(r => r.Name.Contains("28R") || r.Name.Contains("10L"));
        var rwyFeat = new GeoJsonParser.RunwayFeature(rwy28R.Name, rwy28R.Coordinates.ToList());
        var rwyId = RunwayIdentifier.Parse(rwy28R.Name);
        var rect = RunwayCrossingDetector.BuildRunwayRectangle(rwyFeat, rwy28R.WidthFt, rwyId);
        bool isOnRunway = RunwayCrossingDetector.IsOnRunway(exitNode.Position, rect);
        Assert.False(
            isOnRunway,
            $"Exit node is geometrically ON the runway rectangle. RunwayExitPhase would stop the aircraft ON the runway. {output}"
        );
    }

    [Fact]
    public void OAK_FindClearNode_28R_ExitClearsRunway()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // Same position as N569SX rollout
        double acLat = 37.726374;
        double acLon = -122.209536;
        double rwyHeading = 292.0;

        var exitNode = layout.FindNearestExit(acLat, acLon, new TrueHeading(rwyHeading), null);
        Assert.NotNull(exitNode);

        string exitTaxiway = layout.GetExitTaxiwayName(exitNode) ?? "?";
        var clearNode = layout.FindClearNode(exitNode, exitTaxiway, new TrueHeading(rwyHeading));
        Assert.NotNull(clearNode);

        // The clear node is at hold-short distance — it should NOT have
        // runway centerline edges (those only connect on-runway nodes).
        bool clearHasRunwayEdge = clearNode.Edges.Any(e => e.IsRunwayCenterline);
        var clearEdges = string.Join(", ", clearNode.Edges.Select(e => e.TaxiwayName));

        Assert.False(clearHasRunwayEdge, $"Clear node {clearNode.Id} should not have runway edges but has: [{clearEdges}]");
    }

    [Fact]
    public void OAK_ExitW5_TaxiWVTTE_UsesGraphNotRamp()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // Find a node on W5 but NOT on W — simulates aircraft that landed
        // runway 30 and exited onto W5, hasn't reached W yet.
        int? w5OnlyNodeId = null;
        foreach (var node in layout.Nodes.Values)
        {
            bool hasW5 = false;
            bool hasW = false;
            bool hasRwy = false;
            foreach (var edge in node.Edges)
            {
                if (edge.MatchesTaxiway("W5"))
                {
                    hasW5 = true;
                }
                if (edge.MatchesTaxiway("W"))
                {
                    hasW = true;
                }
                if (edge.IsRunwayCenterline)
                {
                    hasRwy = true;
                }
            }

            if (hasW5 && !hasW && !hasRwy)
            {
                w5OnlyNodeId = node.Id;
                break;
            }
        }

        Assert.True(w5OnlyNodeId.HasValue, "Should find a W5-only node (not on W or runway)");

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            w5OnlyNodeId.Value,
            ["W", "V", "T", "TE"],
            out string? failReason,
            new ExplicitPathOptions { DestinationRunway = "26" }
        );

        Assert.NotNull(route);
        Assert.Null(failReason);

        var segSummary = string.Join(
            " -> ",
            route
                .Segments.Select(s => s.TaxiwayName)
                .Aggregate(
                    new List<string>(),
                    (acc, name) =>
                    {
                        if (acc.Count == 0 || !string.Equals(acc[^1], name, StringComparison.OrdinalIgnoreCase))
                        {
                            acc.Add(name);
                        }
                        return acc;
                    }
                )
        );

        // Must NOT contain RAMP segments (that means grass-cutting)
        var rampSegments = route.Segments.Where(s => string.Equals(s.TaxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(rampSegments.Count == 0, $"Route should use graph edges (W5->W), not RAMP segments. Route: {segSummary}");

        // Should include W5 connecting segments (walked current taxiway to reach W)
        Assert.Contains(route.Segments, s => string.Equals(s.TaxiwayName, "W5", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(route.Segments, s => string.Equals(s.TaxiwayName, "W", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(route.Segments, s => string.Equals(s.TaxiwayName, "V", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(route.Segments, s => string.Equals(s.TaxiwayName, "TE", StringComparison.OrdinalIgnoreCase));

        // Should produce a warning about taxiing via W5 to reach W
        Assert.Single(route.Warnings);
        Assert.Contains("W5", route.Warnings[0]);
        Assert.Contains("W", route.Warnings[0]);
    }

    [Fact]
    public void OAK_TaxiDB_MissingC_FailsBecauseNoRunwayBridge()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // Find a node on D that is NOT on B (so reaching B requires taxiway C)
        int? dOnlyNodeId = null;
        foreach (var node in layout.Nodes.Values)
        {
            bool hasD = false;
            bool hasB = false;
            foreach (var edge in node.Edges)
            {
                if (edge.MatchesTaxiway("D"))
                {
                    hasD = true;
                }
                if (edge.MatchesTaxiway("B"))
                {
                    hasB = true;
                }
            }

            if (hasD && !hasB)
            {
                dOnlyNodeId = node.Id;
                break;
            }
        }

        Assert.True(dOnlyNodeId.HasValue, "Should find a D-only node (not on B)");

        // D→B with C omitted should fail: only runway centerline edges are
        // allowed for implicit bridging, and there's no runway between D and B.
        // The user must specify TAXI D C B.
        var route = TaxiPathfinder.ResolveExplicitPath(layout, dOnlyNodeId.Value, ["D", "B"], out _, new ExplicitPathOptions());

        Assert.Null(route);
    }

    // --- Node reference (#nodeId) tests ---

    [Fact]
    public void IsNodeReference_ValidToken()
    {
        Assert.True(NodeRefToken.IsNodeReference("#42"));
        Assert.True(NodeRefToken.IsNodeReference("#0"));
        Assert.True(NodeRefToken.IsNodeReference("#999"));
    }

    [Fact]
    public void IsNodeReference_InvalidToken()
    {
        Assert.False(NodeRefToken.IsNodeReference("#"));
        Assert.False(NodeRefToken.IsNodeReference("42"));
        Assert.False(NodeRefToken.IsNodeReference("A"));
        Assert.False(NodeRefToken.IsNodeReference("@42"));
        Assert.False(NodeRefToken.IsNodeReference("#abc"));
        Assert.False(NodeRefToken.IsNodeReference("#12x"));
    }

    [Fact]
    public void ParseNodeId_ExtractsId()
    {
        Assert.Equal(42, NodeRefToken.ParseNodeId("#42"));
        Assert.Equal(0, NodeRefToken.ParseNodeId("#0"));
        Assert.Equal(999, NodeRefToken.ParseNodeId("#999"));
    }

    [Fact]
    public void ResolveExplicitPath_SingleNodeRef_AStarToNode()
    {
        // Layout: 0 --[A]--> 1 --[A]--> 2 --[A]--> 3
        //                    1 --[B]--> 4
        var layout = BuildSimpleLayout();

        // From node 0, A* to node 3 (should find path through A taxiway)
        var route = TaxiPathfinder.ResolveExplicitPath(layout, 0, ["#3"], out string? failReason, new ExplicitPathOptions());

        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.True(route.Segments.Count >= 1);
        Assert.Equal(0, route.Segments[0].FromNodeId);
        Assert.Equal(3, route.Segments[^1].ToNodeId);
    }

    [Fact]
    public void ResolveExplicitPath_ConsecutiveNodeRefs_AStarBetweenThem()
    {
        var layout = BuildSimpleLayout();

        // A* from node 0 → node 1, then node 1 → node 4
        var route = TaxiPathfinder.ResolveExplicitPath(layout, 0, ["#1", "#4"], out string? failReason, new ExplicitPathOptions());

        Assert.Null(failReason);
        Assert.NotNull(route);

        // Should end at node 4
        Assert.Equal(4, route.Segments[^1].ToNodeId);
    }

    [Fact]
    public void ResolveExplicitPath_MixedTaxiwayAndNodeRef()
    {
        var layout = BuildSimpleLayout();

        // Walk A taxiway, then A* to node 4
        var route = TaxiPathfinder.ResolveExplicitPath(layout, 0, ["A", "#4"], out string? failReason, new ExplicitPathOptions());

        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.Equal(4, route.Segments[^1].ToNodeId);
    }

    [Fact]
    public void ResolveExplicitPath_InvalidNodeRef_ReturnsNull()
    {
        var layout = BuildSimpleLayout();

        var route = TaxiPathfinder.ResolveExplicitPath(layout, 0, ["#99999"], out string? failReason, new ExplicitPathOptions());

        Assert.Null(route);
        Assert.NotNull(failReason);
        Assert.Contains("99999", failReason);
    }

    [Fact]
    public void ResolveExplicitPath_NodeRefSameAsCurrent_NoOp()
    {
        var layout = BuildSimpleLayout();

        // A* from node 0 to node 0 should be a no-op, then walk to node 3
        var route = TaxiPathfinder.ResolveExplicitPath(layout, 0, ["#0", "#3"], out string? failReason, new ExplicitPathOptions());

        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.Equal(3, route.Segments[^1].ToNodeId);
    }

    // ----- Same-taxiway arc shortcut (Item 2) -----

    /// <summary>
    /// Build a three-node linear walk (A→B→C, single taxiway "T") plus a
    /// same-taxiway fillet arc A↔C. The arc shares the taxiway name with
    /// the walk edges, so <see cref="TaxiPathfinder.WalkTaxiway"/>'s
    /// post-pass should collapse the A→B→C straight pair into a single
    /// arc segment A→C.
    /// </summary>
    private static AirportGroundLayout BuildArcShortcutLayout(bool arcSameTaxiway)
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var nodeA = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.700, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var nodeB = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.7007, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var nodeC = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.7007, -122.2007),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[0] = nodeA;
        layout.Nodes[1] = nodeB;
        layout.Nodes[2] = nodeC;

        var edgeAB = new GroundEdge
        {
            Nodes = [nodeA, nodeB],
            TaxiwayName = "T",
            DistanceNm = 0.04,
        };
        var edgeBC = new GroundEdge
        {
            Nodes = [nodeB, nodeC],
            TaxiwayName = "T",
            DistanceNm = 0.04,
        };
        layout.Edges.AddRange([edgeAB, edgeBC]);

        var arcAC = new GroundArc
        {
            Nodes = [nodeA, nodeC],
            // Single name → same-taxiway fillet; two names → junction arc.
            TaxiwayNames = arcSameTaxiway ? ["T"] : ["T", "U"],
            DistanceNm = 0.055,
            MinRadiusOfCurvatureFt = 50.0,
            P1Lat = 37.7004,
            P1Lon = -122.200,
            P2Lat = 37.7007,
            P2Lon = -122.2003,
        };
        layout.Arcs.Add(arcAC);

        nodeA.Edges.AddRange([edgeAB, arcAC]);
        nodeB.Edges.AddRange([edgeAB, edgeBC]);
        nodeC.Edges.AddRange([edgeBC, arcAC]);

        layout.RebuildAdjacencyLists();
        return layout;
    }

    [Fact]
    public void WalkTaxiway_SameTaxiwayArcShortcutsStraightPair()
    {
        var layout = BuildArcShortcutLayout(arcSameTaxiway: true);
        var route = TaxiPathfinder.ResolveExplicitPath(layout, fromNodeId: 0, taxiwayNames: ["T"], out _, new ExplicitPathOptions());

        Assert.NotNull(route);
        // Straight walk would yield [A→B, B→C] (2 segments). The arc shortcut
        // collapses them into a single arc segment A→C.
        Assert.Single(route.Segments);
        Assert.IsType<GroundArc>(route.Segments[0].Edge.Edge);
        Assert.Equal(0, route.Segments[0].FromNodeId);
        Assert.Equal(2, route.Segments[0].ToNodeId);
    }

    [Fact]
    public void WalkTaxiway_JunctionArcNotUsedAsShortcut()
    {
        // Same topology but the arc is tagged as a two-taxiway junction arc.
        // Junction arcs connect different taxiways at transitions — using
        // one as a same-taxiway shortcut would misrepresent the pavement.
        var layout = BuildArcShortcutLayout(arcSameTaxiway: false);
        var route = TaxiPathfinder.ResolveExplicitPath(layout, fromNodeId: 0, taxiwayNames: ["T"], out _, new ExplicitPathOptions());

        Assert.NotNull(route);
        // Straight walk is preserved — 2 straight segments, no arc.
        Assert.Equal(2, route.Segments.Count);
        Assert.All(route.Segments, s => Assert.IsType<GroundEdge>(s.Edge.Edge));
    }

    [Fact]
    public void WalkTaxiway_SkipPath_ReturnsTrueWhenStartNodeAlreadyConnectsToNextTaxiway()
    {
        // Layout: node0 sits at a junction where it already has an edge on
        // both "X" and "Y". When walking "X" with NextTaxiwayName="Y", the
        // skip-path at WalkTaxiway should fire (no walking needed — we're
        // already at the connector) and report success even though segments
        // is empty. Earlier `return segments.Count > 0` reported failure for
        // an empty outer list, breaking first-taxiway clearances of the form
        // "TAXI X Y ..." when the aircraft starts at the X/Y junction.
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var node0 = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.700, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.701, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.700, -122.201),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        var edgeX = new GroundEdge
        {
            Nodes = [node0, node1],
            TaxiwayName = "X",
            DistanceNm = 0.06,
        };
        var edgeY = new GroundEdge
        {
            Nodes = [node0, node2],
            TaxiwayName = "Y",
            DistanceNm = 0.06,
        };

        layout.Nodes[0] = node0;
        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;
        layout.Edges.AddRange([edgeX, edgeY]);
        node0.Edges.AddRange([edgeX, edgeY]);
        node1.Edges.Add(edgeX);
        node2.Edges.Add(edgeY);
        layout.RebuildAdjacencyLists();

        var segments = new List<TaxiRouteSegment>();
        bool walked = TaxiPathfinder.WalkTaxiway(
            layout,
            startNodeId: 0,
            taxiwayName: "X",
            segments,
            out int endNodeId,
            new WalkOptions { NextTaxiwayName = "Y" }
        );

        Assert.True(walked, "skip-path validation must report success even with empty segments");
        Assert.Equal(0, endNodeId);
        Assert.Empty(segments);
    }

    [Fact]
    public void ResolveExplicitPath_SfoM2_UsesSameTaxiwayArcAtA1Apex()
    {
        // SFO A1 has a same-taxiway fillet arc (TaxiwayNames=["A1"])
        // connecting nodes 2186 and 2185 around the apex at node 507. The
        // M2 → A → A1 → 1R route's straight walk visits [..., 2186, 507,
        // 2185, 877]; Item 2's shortcut pass must replace the 2186→507→2185
        // straight pair with the arc so the aircraft tracks the real
        // pavement curve at natural speed rather than orbiting through the
        // apex (which Item 1's synthesis would otherwise rescue at 12 kt).
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return; // SFO geojson absent — silent skip.
        }

        // Start node matches the M2 multi-turn test's spawn area.
        const int startNode = 1529;
        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: startNode,
            taxiwayNames: ["M2", "A", "A1"],
            out string? failReason,
            new ExplicitPathOptions { DestinationRunway = "1R", AirportId = "SFO" }
        );

        Assert.Null(failReason);
        Assert.NotNull(route);

        // Identify the arc by intersection-of-origin (both tangent endpoints created by
        // the apex intersection 507) rather than hard-coded node IDs — Phase D edge
        // additions can shift tangent ID assignment without changing the geometry.
        // Both generators encode the originating junction id in the node origin, but with
        // different prefixes: Legacy "Fillet:tangent-node@507 ..." vs V2
        // "V2:tangent-cut@J507/...". The junction node (507) is removed under V2, so the
        // origin tag — not geometry — is the generator-agnostic link to "born from 507".
        bool usesArc = route.Segments.Any(s =>
            s.Edge.Edge is GroundArc arc
            && arc.TaxiwayNames.Length == 1
            && arc.TaxiwayNames[0].Equals("A1", System.StringComparison.OrdinalIgnoreCase)
            && arc.Nodes.All(n => (n.Origin?.Contains("@507") == true) || (n.Origin?.Contains("@J507") == true))
        );
        Assert.True(usesArc, "route should use a same-taxiway A1 arc at SFO A1 apex (intersection 507)");

        bool visitsApex = route.Segments.Any(s => (s.FromNodeId == 507) || (s.ToNodeId == 507));
        Assert.False(visitsApex, "route should NOT visit A1 apex node 507 — the arc skips it");
    }

    // --- Regression tests: TAXI <tw> @<parking> must not produce reversed segments ---
    // From S2-OAK-3 "VFR Sequencing": N9225L was given "TAXI D @NEW1" and N436MS was
    // given "TAXI C @JSX1". Both produced routes whose segment list contained a U-turn:
    // an (a,b) pair immediately followed by (b,a). The walk overshot the ramp branch-off
    // on the last taxiway, and the A* extension back to parking retraced the overshoot.

    private static int CountReversals(IReadOnlyList<TaxiRouteSegment> segments)
    {
        int count = 0;
        for (int i = 0; i + 1 < segments.Count; i++)
        {
            var a = segments[i];
            var b = segments[i + 1];
            if (a.FromNodeId == b.ToNodeId && a.ToNodeId == b.FromNodeId)
            {
                count++;
            }
        }

        return count;
    }

    private static List<TaxiRouteSegment> ResolveParkingRouteSegments(
        AirportGroundLayout layout,
        int startNodeId,
        List<string> taxiwayPath,
        int destNodeId
    )
    {
        var destNode = layout.Nodes[destNodeId];
        var explicitRoute = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startNodeId,
            taxiwayPath,
            out string? failReason,
            new ExplicitPathOptions { DestinationHintNode = destNode, AirportId = layout.AirportId }
        );

        Assert.Null(failReason);
        Assert.NotNull(explicitRoute);

        var combined = new List<TaxiRouteSegment>(explicitRoute.Segments);
        int endNodeId = combined.Count > 0 ? combined[^1].ToNodeId : startNodeId;
        if (endNodeId != destNodeId)
        {
            var extension = TaxiPathfinder.FindRoute(layout, endNodeId, destNodeId);
            Assert.NotNull(extension);
            combined.AddRange(extension.Segments);
        }

        return combined;
    }

    [Fact]
    public void OAK_TaxiD_ToNEW1_FromG_HasNoReversals()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // N9225L exits 28R onto G (hold-short node 1269), then `TAXI D @NEW1`.
        var parking = layout.FindParkingByName("NEW1");
        Assert.NotNull(parking);

        var combined = ResolveParkingRouteSegments(layout, startNodeId: 1269, taxiwayPath: ["G", "D"], destNodeId: parking.Id);

        int reversals = CountReversals(combined);
        Assert.True(reversals == 0, $"TAXI G D @NEW1 from node 1269 produced {reversals} reversal(s) in {combined.Count} segments");
    }

    [Fact]
    public void OAK_TaxiC_ToJSX1_FromG_HasNoReversals()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // N436MS exits 28R onto G (hold-short node 361), then `TAXI C @JSX1`.
        var parking = layout.FindParkingByName("JSX1");
        Assert.NotNull(parking);

        var combined = ResolveParkingRouteSegments(layout, startNodeId: 361, taxiwayPath: ["G", "C"], destNodeId: parking.Id);

        int reversals = CountReversals(combined);
        Assert.True(reversals == 0, $"TAXI G C @JSX1 from node 361 produced {reversals} reversal(s) in {combined.Count} segments");
    }

    // --- Look-ahead regression: at inter-taxiway and branch-off transitions, pick the
    // geometrically-best arc rather than the first one encountered. Both routes here
    // parse and produce no reversal, but naïve "first-match" transition picks a
    // wrong-way arc that creates a visibly-poor physical path.

    [Fact]
    public void OAK_TaxiD_ToNEW1_FromG_UsesNorthwardArc()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // N9225L should transition G→D at node 350 via the NW-pointing D/G arc
        // 350↔1318, not at node 1311 via the NE-pointing D/G arc 1311↔1310. Both
        // "doesn't visit 1310" and "does visit the 350→1318 arc" must hold —
        // positive and negative assertions together catch both the wrong-way entry
        // and any new arc the walker might invent.
        var parking = layout.FindParkingByName("NEW1");
        Assert.NotNull(parking);

        var combined = ResolveParkingRouteSegments(layout, startNodeId: 1269, taxiwayPath: ["G", "D"], destNodeId: parking.Id);

        Assert.DoesNotContain(combined, s => s.FromNodeId == 1310 || s.ToNodeId == 1310);
        Assert.Contains(combined, s => (s.FromNodeId == 350 && s.ToNodeId == 1318) || (s.FromNodeId == 1318 && s.ToNodeId == 350));
    }

    [Fact]
    public void OAK_TaxiD_ToNEW1_From1271_HasNoReversals()
    {
        // Reproduces the replay-test scenario exactly: N9225L's nearest node at t=424
        // is 1271 (a G/C junction), not the 28R hold-short 1269. Confirms the look-ahead
        // works from a non-exit-runway start as well.
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = layout.FindParkingByName("NEW1");
        Assert.NotNull(parking);

        var combined = ResolveParkingRouteSegments(layout, startNodeId: 1271, taxiwayPath: ["D"], destNodeId: parking.Id);
        int reversals = CountReversals(combined);
        Assert.True(
            reversals == 0,
            $"TAXI D @NEW1 from 1271 produced {reversals} reversal(s) in {combined.Count} segments: first={combined[0].FromNodeId}→{combined[0].ToNodeId}"
        );
        Assert.DoesNotContain(combined, s => s.FromNodeId == 1310 || s.ToNodeId == 1310);
        Assert.Contains(combined, s => (s.FromNodeId == 350 && s.ToNodeId == 1318) || (s.FromNodeId == 1318 && s.ToNodeId == 350));
    }

    [Fact]
    public void OAK_TaxiC_ToJSX1_FromG_UsesDirectArc()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // N436MS should leave C at node 1198 via the C/RAMP arc 1198↔1199 (short path
        // to JSX1). A first-match branch-off picks node 339 instead, which forces
        // the extension through node 1203 (near HELI1) and back via a RAMP arc —
        // a visible detour. Positive check on the 1198↔1199 arc catches the case
        // where the extension takes the long way but happens not to visit 1203.
        var parking = layout.FindParkingByName("JSX1");
        Assert.NotNull(parking);

        var combined = ResolveParkingRouteSegments(layout, startNodeId: 361, taxiwayPath: ["G", "C"], destNodeId: parking.Id);

        Assert.DoesNotContain(combined, s => s.FromNodeId == 1203 || s.ToNodeId == 1203);
        Assert.Contains(combined, s => (s.FromNodeId == 1198 && s.ToNodeId == 1199) || (s.FromNodeId == 1199 && s.ToNodeId == 1198));
    }

    [Fact]
    public void OAK_TaxiC_ToJSX1_From1271_UsesDirectArc()
    {
        // Reproduces the replay-test scenario for N436MS: path=[C] from 1271 (no
        // explicit G). The cached-extension Shortest route must traverse the 1198↔1199
        // C/RAMP arc directly to JSX1, not the long loop via 339→1207→1205→arc→1204.
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = layout.FindParkingByName("JSX1");
        Assert.NotNull(parking);

        var combined = ResolveParkingRouteSegments(layout, startNodeId: 1271, taxiwayPath: ["C"], destNodeId: parking.Id);

        Assert.DoesNotContain(combined, s => s.FromNodeId == 1203 || s.ToNodeId == 1203);
        Assert.Contains(combined, s => (s.FromNodeId == 1198 && s.ToNodeId == 1199) || (s.FromNodeId == 1199 && s.ToNodeId == 1198));
    }

    // --- FewestTurns heuristic admissibility regression ---
    // FindRoute uses RoutePreference.FewestTurns by default. Its cost is
    // distance × 0.001 + 10 per transition, but the A* heuristic in
    // FindRouteInternal is straight-line distance × 1.0 — overestimating actual
    // cost-to-goal by ~1000× for non-transitioning paths. A* loses admissibility
    // and terminates on the first goal pop, which can be a longer single-taxiway
    // detour rather than the actual minimum-cost path. This test pins the canonical
    // OAK example: from 1198 to JSX1 (604), the 4-segment direct path via the
    // 1198↔1199 C/RAMP arc is shorter than the 6-segment loop via 339→1207→1205.

    [Fact]
    public void OAK_FindRoute_1198_To_JSX1_UsesDirectArc()
    {
        var layout = LoadAirportLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var route = TaxiPathfinder.FindRoute(layout, 1198, 604);
        Assert.NotNull(route);
        Assert.Contains(route.Segments, s => (s.FromNodeId == 1198 && s.ToNodeId == 1199) || (s.FromNodeId == 1199 && s.ToNodeId == 1198));
    }
}
