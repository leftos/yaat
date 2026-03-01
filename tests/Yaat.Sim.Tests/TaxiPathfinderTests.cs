using System.IO;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
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

        var node0 = new GroundNode { Id = 0, Latitude = 37.700, Longitude = -122.200, Type = GroundNodeType.TaxiwayIntersection };
        var node1 = new GroundNode { Id = 1, Latitude = 37.701, Longitude = -122.200, Type = GroundNodeType.TaxiwayIntersection };
        var node2 = new GroundNode { Id = 2, Latitude = 37.702, Longitude = -122.200, Type = GroundNodeType.TaxiwayIntersection };
        var node3 = new GroundNode { Id = 3, Latitude = 37.703, Longitude = -122.200, Type = GroundNodeType.TaxiwayIntersection };
        // HS1 near runway 30 threshold (further north)
        var hs1 = new GroundNode { Id = 10, Latitude = 37.704, Longitude = -122.200, Type = GroundNodeType.RunwayHoldShort, RunwayId = "12/30" };
        // HS2 near runway 12 threshold (further south)
        var hs2 = new GroundNode { Id = 11, Latitude = 37.700, Longitude = -122.201, Type = GroundNodeType.RunwayHoldShort, RunwayId = "12/30" };

        layout.Nodes[0] = node0;
        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;
        layout.Nodes[3] = node3;
        layout.Nodes[10] = hs1;
        layout.Nodes[11] = hs2;

        var edgeA01 = new GroundEdge { FromNodeId = 0, ToNodeId = 1, TaxiwayName = "A", DistanceNm = 0.06 };
        var edgeW12 = new GroundEdge { FromNodeId = 1, ToNodeId = 2, TaxiwayName = "W", DistanceNm = 0.06 };
        var edgeW23 = new GroundEdge { FromNodeId = 2, ToNodeId = 3, TaxiwayName = "W", DistanceNm = 0.06 };
        var edgeW1_2_hs1 = new GroundEdge { FromNodeId = 2, ToNodeId = 10, TaxiwayName = "W1", DistanceNm = 0.04 };
        var edgeW2_3_hs2 = new GroundEdge { FromNodeId = 3, ToNodeId = 11, TaxiwayName = "W2", DistanceNm = 0.04 };

        layout.Edges.AddRange([edgeA01, edgeW12, edgeW23, edgeW1_2_hs1, edgeW2_3_hs2]);

        node0.Edges.Add(edgeA01);
        node1.Edges.AddRange([edgeA01, edgeW12]);
        node2.Edges.AddRange([edgeW12, edgeW23, edgeW1_2_hs1]);
        node3.Edges.AddRange([edgeW23, edgeW2_3_hs2]);
        hs1.Edges.Add(edgeW1_2_hs1);
        hs2.Edges.Add(edgeW2_3_hs2);

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

        var node0 = new GroundNode { Id = 0, Latitude = 37.700, Longitude = -122.200, Type = GroundNodeType.TaxiwayIntersection };
        var node1 = new GroundNode { Id = 1, Latitude = 37.701, Longitude = -122.200, Type = GroundNodeType.TaxiwayIntersection };
        var node2 = new GroundNode { Id = 2, Latitude = 37.702, Longitude = -122.200, Type = GroundNodeType.TaxiwayIntersection };
        var hs = new GroundNode { Id = 10, Latitude = 37.703, Longitude = -122.200, Type = GroundNodeType.RunwayHoldShort, RunwayId = "12/30" };

        layout.Nodes[0] = node0;
        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;
        layout.Nodes[10] = hs;

        var edgeA01 = new GroundEdge { FromNodeId = 0, ToNodeId = 1, TaxiwayName = "A", DistanceNm = 0.06 };
        var edgeW12 = new GroundEdge { FromNodeId = 1, ToNodeId = 2, TaxiwayName = "W", DistanceNm = 0.06 };
        var edgeZ2hs = new GroundEdge { FromNodeId = 2, ToNodeId = 10, TaxiwayName = "Z", DistanceNm = 0.04 };

        layout.Edges.AddRange([edgeA01, edgeW12, edgeZ2hs]);

        node0.Edges.Add(edgeA01);
        node1.Edges.AddRange([edgeA01, edgeW12]);
        node2.Edges.AddRange([edgeW12, edgeZ2hs]);
        hs.Edges.Add(edgeZ2hs);

        return layout;
    }

    private sealed class TestRunwayLookup : IRunwayLookup
    {
        private readonly Dictionary<(string Airport, string Runway), RunwayInfo> _runways = new(
            new AirportRunwayComparer());

        public void Add(RunwayInfo info)
        {
            _runways[(info.AirportId, info.RunwayId)] = info;
        }

        public RunwayInfo? GetRunway(string airportCode, string runwayId)
        {
            return _runways.GetValueOrDefault((airportCode, runwayId));
        }

        public IReadOnlyList<RunwayInfo> GetRunways(string airportCode)
        {
            var result = new List<RunwayInfo>();
            foreach (var (key, info) in _runways)
            {
                if (string.Equals(key.Airport, airportCode, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(info);
                }
            }
            return result;
        }

        private sealed class AirportRunwayComparer
            : IEqualityComparer<(string Airport, string Runway)>
        {
            public bool Equals((string Airport, string Runway) x, (string Airport, string Runway) y)
            {
                return string.Equals(x.Airport, y.Airport, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.Runway, y.Runway, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode((string Airport, string Runway) obj)
            {
                return HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Airport),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Runway));
            }
        }
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
            layout, fromNodeId: 0, taxiwayNames: ["A"], out _);

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
        Assert.Equal(expected, TaxiPathfinder.IsNumberedVariant(candidate, baseName));
    }

    // --- RunwayIdMatches helper tests ---

    [Theory]
    [InlineData("12/30", "30", true)]
    [InlineData("12/30", "12", true)]
    [InlineData("12/30", "6", false)]
    [InlineData("30", "30", true)]
    [InlineData("30L", "30L", true)]
    [InlineData("12L/30R", "30R", true)]
    public void RunwayIdMatches_ReturnsExpected(string stored, string target, bool expected)
    {
        Assert.Equal(expected, TaxiPathfinder.RunwayIdMatches(stored, target));
    }

    // --- Variant auto-inference tests ---

    [Fact]
    public void VariantInference_SingleVariant_AutoExtends()
    {
        // Build layout where only W1 connects to a hold-short for runway 30
        var layout = new AirportGroundLayout { AirportId = "KTEST" };

        var node0 = new GroundNode { Id = 0, Latitude = 37.700, Longitude = -122.200, Type = GroundNodeType.TaxiwayIntersection };
        var node1 = new GroundNode { Id = 1, Latitude = 37.701, Longitude = -122.200, Type = GroundNodeType.TaxiwayIntersection };
        var node2 = new GroundNode { Id = 2, Latitude = 37.702, Longitude = -122.200, Type = GroundNodeType.TaxiwayIntersection };
        var hs = new GroundNode { Id = 10, Latitude = 37.703, Longitude = -122.200, Type = GroundNodeType.RunwayHoldShort, RunwayId = "12/30" };

        layout.Nodes[0] = node0;
        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;
        layout.Nodes[10] = hs;

        var edgeA = new GroundEdge { FromNodeId = 0, ToNodeId = 1, TaxiwayName = "A", DistanceNm = 0.06 };
        var edgeW = new GroundEdge { FromNodeId = 1, ToNodeId = 2, TaxiwayName = "W", DistanceNm = 0.06 };
        var edgeW1 = new GroundEdge { FromNodeId = 2, ToNodeId = 10, TaxiwayName = "W1", DistanceNm = 0.04 };

        layout.Edges.AddRange([edgeA, edgeW, edgeW1]);
        node0.Edges.Add(edgeA);
        node1.Edges.AddRange([edgeA, edgeW]);
        node2.Edges.AddRange([edgeW, edgeW1]);
        hs.Edges.Add(edgeW1);

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout, 0, ["A", "W"], out string? failReason,
            destinationRunway: "30");

        Assert.NotNull(route);
        Assert.Null(failReason);
        // Route should end at HS node (10)
        Assert.Equal(10, route.Segments[^1].ToNodeId);
        // Should include a W1 segment
        Assert.Contains(route.Segments, s =>
            string.Equals(s.TaxiwayName, "W1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VariantInference_MultipleVariants_PicksClosestToThreshold()
    {
        var layout = BuildVariantLayout();

        // Runway 30 threshold is further north (37.710)
        var runways = new TestRunwayLookup();
        runways.Add(new RunwayInfo
        {
            AirportId = "KTEST",
            RunwayId = "30",
            ThresholdLatitude = 37.710,
            ThresholdLongitude = -122.200,
            TrueHeading = 300,
            ElevationFt = 0,
            LengthFt = 5000,
            WidthFt = 150,
            EndLatitude = 37.690,
            EndLongitude = -122.200,
        });

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout, 0, ["A", "W"], out string? failReason,
            destinationRunway: "30", runways: runways, airportId: "KTEST");

        Assert.NotNull(route);
        Assert.Null(failReason);
        // HS1 (id=10, lat 37.704) is closer to 37.710 threshold than HS2 (id=11, lat 37.700)
        // So W1 should be chosen
        Assert.Equal(10, route.Segments[^1].ToNodeId);
        Assert.Contains(route.Segments, s =>
            string.Equals(s.TaxiwayName, "W1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VariantInference_AlreadyReachesHoldShort_NoInference()
    {
        var layout = BuildVariantLayout();

        // Explicitly specify W1 — route should walk W then W1 normally
        var route = TaxiPathfinder.ResolveExplicitPath(
            layout, 0, ["A", "W", "W1"], out string? failReason,
            destinationRunway: "30");

        Assert.NotNull(route);
        Assert.Null(failReason);
        // Should end at HS1 (node 10)
        Assert.Equal(10, route.Segments[^1].ToNodeId);
    }

    [Fact]
    public void VariantInference_NoDestinationRunway_NoInference()
    {
        var layout = BuildVariantLayout();

        // No destination runway — walk A then W to the end of W
        var route = TaxiPathfinder.ResolveExplicitPath(
            layout, 0, ["A", "W"], out string? failReason);

        Assert.NotNull(route);
        Assert.Null(failReason);
        // Should end at node 3 (end of taxiway W), NOT at a hold-short
        Assert.Equal(3, route.Segments[^1].ToNodeId);
    }

    [Fact]
    public void VariantInference_AmbiguousConnector_ReturnsNullWithReason()
    {
        var layout = BuildAmbiguousLayout();

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout, 0, ["A", "W"], out string? failReason,
            destinationRunway: "30");

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

        var node0 = new GroundNode { Id = 0, Latitude = 37.700, Longitude = -122.200, Type = GroundNodeType.TaxiwayIntersection };
        var node1 = new GroundNode { Id = 1, Latitude = 37.701, Longitude = -122.200, Type = GroundNodeType.TaxiwayIntersection };
        var node2 = new GroundNode { Id = 2, Latitude = 37.702, Longitude = -122.200, Type = GroundNodeType.TaxiwayIntersection };

        layout.Nodes[0] = node0;
        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;

        var edgeA = new GroundEdge { FromNodeId = 0, ToNodeId = 1, TaxiwayName = "A", DistanceNm = 0.06 };
        var edgeW = new GroundEdge { FromNodeId = 1, ToNodeId = 2, TaxiwayName = "W", DistanceNm = 0.06 };

        layout.Edges.AddRange([edgeA, edgeW]);
        node0.Edges.Add(edgeA);
        node1.Edges.AddRange([edgeA, edgeW]);
        node2.Edges.Add(edgeW);

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout, 0, ["A", "W"], out string? failReason,
            destinationRunway: "30");

        Assert.NotNull(route);
        Assert.Null(failReason);
        // Proceeds with existing behavior — ends at node 2 with destination hold-short
        Assert.Equal(2, route.Segments[^1].ToNodeId);
    }

    // --- Integration tests using real airport GeoJSON ---

    private const string VzoaGeoJsonDir =
        @"X:\dev\vzoa\training-files\atctrainer-airport-files";

    private static AirportGroundLayout? LoadAirportLayout(string airportId, string subdir)
    {
        // Try per-feature files first (more accurate), fall back to monolithic
        string dir = Path.Combine(VzoaGeoJsonDir, subdir);
        if (Directory.Exists(dir))
        {
            var files = Directory.GetFiles(dir, "*.geojson")
                .Select(File.ReadAllText)
                .ToList();

            if (files.Count > 0)
            {
                return GeoJsonParser.ParseMultiple(airportId, files);
            }
        }

        string monolithic = Path.Combine(VzoaGeoJsonDir, $"{subdir}.geojson");
        if (File.Exists(monolithic))
        {
            return GeoJsonParser.Parse(airportId, File.ReadAllText(monolithic));
        }

        return null;
    }

    private static GroundNode? FindParkingOrSpot(AirportGroundLayout layout, string name)
    {
        foreach (var node in layout.Nodes.Values)
        {
            if ((node.Type == GroundNodeType.Parking || node.Type == GroundNodeType.Spot)
                && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }
        return null;
    }

    private static int? FindNodeOnTaxiway(AirportGroundLayout layout, string taxiwayName)
    {
        foreach (var edge in layout.Edges)
        {
            if (string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                return edge.FromNodeId;
            }
        }
        return null;
    }

    private static int? FindIntersectionNode(
        AirportGroundLayout layout, string tw1, string tw2)
    {
        foreach (var node in layout.Nodes.Values)
        {
            bool hasTw1 = false;
            bool hasTw2 = false;
            foreach (var edge in node.Edges)
            {
                if (string.Equals(edge.TaxiwayName, tw1, StringComparison.OrdinalIgnoreCase))
                {
                    hasTw1 = true;
                }
                if (string.Equals(edge.TaxiwayName, tw2, StringComparison.OrdinalIgnoreCase))
                {
                    hasTw2 = true;
                }
            }
            if (hasTw1 && hasTw2)
            {
                return node.Id;
            }
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
        var holdShorts = layout.Nodes.Values
            .Where(n => n.Type == GroundNodeType.RunwayHoldShort
                && n.RunwayId is not null
                && TaxiPathfinder.RunwayIdMatches(n.RunwayId, "30"))
            .ToList();
        Assert.True(holdShorts.Count > 0, "Should have hold-shorts for runway 30");

        // Hold-short nodes must be connected to the graph (edge split fix)
        foreach (var hs in holdShorts)
        {
            Assert.True(hs.Edges.Count > 0,
                $"Hold-short node {hs.Id} ({hs.RunwayId}) should have edges but has 0");
        }

        // Check that B's FromNodeId is actually in the graph
        var bEdge = layout.Edges.First(e =>
            string.Equals(e.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase));
        Assert.True(layout.Nodes.ContainsKey(bEdge.FromNodeId),
            $"B edge FromNodeId {bEdge.FromNodeId} should exist in nodes");

        // Check we can walk B from its first node
        var bStartNode = layout.Nodes[bEdge.FromNodeId];
        bool hasBEdge = bStartNode.Edges.Any(e =>
            string.Equals(e.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasBEdge, $"Node {bEdge.FromNodeId} should have B edges");
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
        var w3Edges = layout.Edges
            .Where(e => string.Equals(e.TaxiwayName, "W3", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(w3Edges.Count > 0, "Should have W3 edges");

        // Try walking from each W3 endpoint
        foreach (var edge in w3Edges)
        {
            var route1 = TaxiPathfinder.ResolveExplicitPath(
                layout, edge.FromNodeId, ["W3"], out _);
            var route2 = TaxiPathfinder.ResolveExplicitPath(
                layout, edge.ToNodeId, ["W3"], out _);

            // At least one direction should work
            if (route1 is not null || route2 is not null)
            {
                int startId = route1 is not null ? edge.FromNodeId : edge.ToNodeId;
                // Now try W3 then W
                var combined = TaxiPathfinder.ResolveExplicitPath(
                    layout, startId, ["W3", "W"], out string? fr);
                if (combined is not null)
                {
                    return; // Success
                }
            }
        }

        // If we get here, diagnose WHY walking fails
        var firstW3 = w3Edges[0];
        var node = layout.Nodes[firstW3.FromNodeId];
        var edgeNames = string.Join(", ", node.Edges.Select(e => e.TaxiwayName));
        Assert.Fail($"Could not walk W3. Node {firstW3.FromNodeId} edges: [{edgeNames}]. " +
            $"W3 edges count: {w3Edges.Count}");
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

        var w3Edges = layout.Edges
            .Where(e => string.Equals(e.TaxiwayName, "W3", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(w3Edges.Count > 0, "Should have W3 edges");

        var triedNodes = new HashSet<int>();
        foreach (var edge in w3Edges)
        {
            foreach (int nodeId in new[] { edge.FromNodeId, edge.ToNodeId })
            {
                if (!triedNodes.Add(nodeId))
                {
                    continue;
                }

                route = TaxiPathfinder.ResolveExplicitPath(
                    layout, nodeId, ["W3", "W"], out failReason,
                    destinationRunway: "30");

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
        bool hasVariant = route.Segments.Any(s =>
            TaxiPathfinder.IsNumberedVariant(s.TaxiwayName, "W"));
        Assert.True(hasVariant,
            $"Expected W-variant segment, got: {string.Join(" -> ", route.Segments.Select(s => s.TaxiwayName))}");

        // Route should pass through a hold-short for runway 30 (the variant walk
        // may continue past the hold-short to W1's end node)
        bool passesHoldShort = route.Segments.Any(s =>
            layout.Nodes.TryGetValue(s.ToNodeId, out var n)
            && n.Type == GroundNodeType.RunwayHoldShort
            && n.RunwayId is not null
            && TaxiPathfinder.RunwayIdMatches(n.RunwayId, "30"));
        Assert.True(passesHoldShort,
            "Route should pass through a hold-short node for runway 30");
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

        var w3Edges = layout.Edges
            .Where(e => string.Equals(e.TaxiwayName, "W3", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(w3Edges.Count > 0, "Should have W3 edges");

        var triedNodes = new HashSet<int>();
        foreach (var edge in w3Edges)
        {
            foreach (int nodeId in new[] { edge.FromNodeId, edge.ToNodeId })
            {
                if (!triedNodes.Add(nodeId))
                {
                    continue;
                }

                route = TaxiPathfinder.ResolveExplicitPath(
                    layout, nodeId, ["W3", "W", "W1"], out failReason,
                    destinationRunway: "30");

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

        var bEdges = layout.Edges
            .Where(e => string.Equals(e.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(bEdges.Count > 0, "Should have B edges");

        var triedNodes = new HashSet<int>();
        foreach (var edge in bEdges)
        {
            foreach (int nodeId in new[] { edge.FromNodeId, edge.ToNodeId })
            {
                if (!triedNodes.Add(nodeId))
                {
                    continue;
                }

                route = TaxiPathfinder.ResolveExplicitPath(
                    layout, nodeId, ["B", "W"], out failReason,
                    destinationRunway: "30");

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
            var hs30 = layout.Nodes.Values
                .Where(n => n.Type == GroundNodeType.RunwayHoldShort
                    && n.RunwayId is not null
                    && TaxiPathfinder.RunwayIdMatches(n.RunwayId, "30"))
                .ToList();
            var hsInfo = string.Join("; ", hs30.Select(n =>
                $"HS {n.Id} edges=[{string.Join(",", n.Edges.Select(e => e.TaxiwayName))}]"));

            Assert.Fail(
                $"Route B W to RWY 30 returned null. " +
                $"failReason={failReason ?? "null"}, " +
                $"hold-shorts for 30: {hs30.Count} ({hsInfo})");
        }

        // Route should contain B and W segments
        Assert.Contains(route.Segments, s =>
            string.Equals(s.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(route.Segments, s =>
            string.Equals(s.TaxiwayName, "W", StringComparison.OrdinalIgnoreCase));

        // Route should reach a hold-short for runway 30 — either via variant
        // inference (W1-W7) or by W itself crossing the runway
        bool passesRwy30HoldShort = route.Segments.Any(s =>
            layout.Nodes.TryGetValue(s.ToNodeId, out var n)
            && n.Type == GroundNodeType.RunwayHoldShort
            && n.RunwayId is not null
            && TaxiPathfinder.RunwayIdMatches(n.RunwayId, "30"));

        var segSummary = string.Join(" ", route.Segments
            .Select(s => s.TaxiwayName)
            .Aggregate(new List<string>(), (acc, name) =>
            {
                if (acc.Count == 0 || !string.Equals(acc[^1], name, StringComparison.OrdinalIgnoreCase))
                {
                    acc.Add(name);
                }
                return acc;
            }));
        Assert.True(passesRwy30HoldShort,
            $"Route should pass through runway 30 hold-short. " +
            $"Taxiways: {segSummary}, " +
            $"start={usedStartNode}, " +
            $"end={route.Segments[^1].ToNodeId}");
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

        bool hasHoldShorts = layout.Nodes.Values.Any(
            n => n.Type == GroundNodeType.RunwayHoldShort);
        Assert.True(hasHoldShorts, "SFO layout should have hold-short nodes");
    }

}
