using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// Issue #172 W7: per-taxiway turn-direction hints (<c>&gt;A</c> right / <c>&lt;A</c> left) bias junction
/// selection toward the controller's turn. Uses OAK node 18, a clean cross where taxiway W runs through
/// (neighbour 682 bears 130°, neighbour 683 bears 310° — opposite) and B branches off. With the aircraft
/// heading 40° (across W), 682 is a right turn and 683 a left turn, so <c>&gt;W</c> and <c>&lt;W</c> must
/// start the route in opposite directions.
/// </summary>
public class Issue172TurnHintTests(ITestOutputHelper output)
{
    private const int WCrossNode = 18;
    private const int WRightNeighbor = 682; // bearing 130° from node 18 → right turn from heading 40°
    private const int WLeftNeighbor = 683; // bearing 310° from node 18 → left turn from heading 40°
    private const double AcrossWHeadingDeg = 40.0;

    private static AirportGroundLayout? OakLayout(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("TaxiPathfinder", LogLevel.Debug).InitializeSimLog();
        return new TestAirportGroundData().GetLayout("OAK");
    }

    private static TaxiRoute? ResolveWithHint(AirportGroundLayout layout, TurnDirection hint, out string? failReason)
    {
        return TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: WCrossNode,
            taxiwayNames: ["W"],
            out failReason,
            new ExplicitPathOptions
            {
                AirportId = "OAK",
                PathTurnHints = [hint],
                StartHeadingTrue = AcrossWHeadingDeg,
            },
            AircraftCategory.Jet
        );
    }

    [Fact]
    public void RightHint_StartsRouteTowardRightNeighbor()
    {
        var layout = OakLayout(output);
        if (layout is null)
        {
            return;
        }

        var route = ResolveWithHint(layout, TurnDirection.Right, out string? failReason);

        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);
        Assert.Equal(WCrossNode, route.Segments[0].FromNodeId);
        Assert.Equal(WRightNeighbor, route.Segments[0].ToNodeId);
    }

    [Fact]
    public void LeftHint_StartsRouteTowardLeftNeighbor()
    {
        var layout = OakLayout(output);
        if (layout is null)
        {
            return;
        }

        var route = ResolveWithHint(layout, TurnDirection.Left, out string? failReason);

        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);
        Assert.Equal(WCrossNode, route.Segments[0].FromNodeId);
        Assert.Equal(WLeftNeighbor, route.Segments[0].ToNodeId);
    }

    [Fact]
    public void OppositeHints_ProduceOppositeFirstSteps()
    {
        var layout = OakLayout(output);
        if (layout is null)
        {
            return;
        }

        var right = ResolveWithHint(layout, TurnDirection.Right, out _);
        var left = ResolveWithHint(layout, TurnDirection.Left, out _);

        Assert.NotNull(right);
        Assert.NotNull(left);
        Assert.NotEqual(right.Segments[0].ToNodeId, left.Segments[0].ToNodeId);
    }

    // -----------------------------------------------------------------------
    // Mid-route onto-penalty: a hint on a non-first taxiway picks which junction the route turns
    // onto it at. Real test airports collapse each taxiway pair into a single fillet cluster (so the
    // turn handedness is uniform within it), so this is proven on a synthetic graph where taxiway A
    // meets taxiway B at two separate junctions of opposite handedness:
    //
    //        BL(5)         A2(3) ── B(west, left turn) ── BL(5)
    //          \           │
    //           A2(3)      A1(2) ── B(east, right turn) ── BR(4)
    //           │          │
    //           A1(2)──BR  A0(1)  (start, taxis north along A)
    //           │
    //           A0(1)
    //
    // Heading north along A, turning east onto B at A1 is a right turn; turning west onto B at A2 is a
    // left turn. ">B" must pick A1→BR; "<B" must pick A2→BL.
    private static GroundNode Node(int id, double lat, double lon) =>
        new()
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = GroundNodeType.TaxiwayIntersection,
        };

    private static AirportGroundLayout TwoJunctionLayout()
    {
        var a0 = Node(1, 37.700, -122.200);
        var a1 = Node(2, 37.702, -122.200);
        var a2 = Node(3, 37.704, -122.200);
        var br = Node(4, 37.702, -122.197); // east of A1 → right turn from a northbound A
        var bl = Node(5, 37.704, -122.203); // west of A2 → left turn from a northbound A

        var layout = new AirportGroundLayout { AirportId = "TEST" };
        foreach (var n in new[] { a0, a1, a2, br, bl })
        {
            layout.Nodes[n.Id] = n;
        }

        void Edge(GroundNode x, GroundNode y, string twy) =>
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [x, y],
                    TaxiwayName = twy,
                    DistanceNm = GeoMath.DistanceNm(x.Position, y.Position),
                }
            );

        Edge(a0, a1, "A");
        Edge(a1, a2, "A");
        Edge(a1, br, "B");
        Edge(a2, bl, "B");
        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static TaxiRoute? ResolveMidRoute(AirportGroundLayout layout, TurnDirection bHint, out string? failReason) =>
        TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: 1,
            taxiwayNames: ["A", "B"],
            out failReason,
            new ExplicitPathOptions { AirportId = "TEST", PathTurnHints = [null, bHint] },
            AircraftCategory.Jet
        );

    [Fact]
    public void MidRoute_RightHintOntoB_TurnsAtRightHandJunction()
    {
        var route = ResolveMidRoute(TwoJunctionLayout(), TurnDirection.Right, out string? failReason);

        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.Equal(4, route.Segments[^1].ToNodeId); // BR — turned onto B at A1 (right)
    }

    [Fact]
    public void MidRoute_LeftHintOntoB_TurnsAtLeftHandJunction()
    {
        var route = ResolveMidRoute(TwoJunctionLayout(), TurnDirection.Left, out string? failReason);

        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.Equal(5, route.Segments[^1].ToNodeId); // BL — turned onto B at A2 (left)
    }

    // -----------------------------------------------------------------------
    // First-taxiway start direction through the multi-token path (RouteNamedToNamed): taxiway A runs
    // east-west through the start S and meets taxiway B at a junction on each side. With the aircraft
    // heading north, ">A" (right onto A) starts east toward the A/B junction AE→BE; "<A" starts west
    // toward AW→BW. This exercises FirstTaxiwayTurnHintPenalty (the hint on token 0 + StartHeadingTrue).
    //
    //     BE(12)            BW(14)
    //      │                 │
    //     AE(11) ──── S(10) ──── AW(13)        (A runs east-west; aircraft at S heading north)
    private static AirportGroundLayout TwoEntryLayout()
    {
        var s = Node(10, 37.700, -122.200);
        var ae = Node(11, 37.700, -122.197); // east of S → right turn from a northbound aircraft
        var aw = Node(13, 37.700, -122.203); // west of S → left turn
        var be = Node(12, 37.701, -122.197); // B north off AE
        var bw = Node(14, 37.701, -122.203); // B north off AW

        var layout = new AirportGroundLayout { AirportId = "TEST" };
        foreach (var n in new[] { s, ae, aw, be, bw })
        {
            layout.Nodes[n.Id] = n;
        }

        void Edge(GroundNode x, GroundNode y, string twy) =>
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [x, y],
                    TaxiwayName = twy,
                    DistanceNm = GeoMath.DistanceNm(x.Position, y.Position),
                }
            );

        Edge(s, ae, "A");
        Edge(s, aw, "A");
        Edge(ae, be, "B");
        Edge(aw, bw, "B");
        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static TaxiRoute? ResolveFirstTaxiway(AirportGroundLayout layout, TurnDirection aHint, out string? failReason) =>
        TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: 10,
            taxiwayNames: ["A", "B"],
            out failReason,
            new ExplicitPathOptions
            {
                AirportId = "TEST",
                PathTurnHints = [aHint, null],
                StartHeadingTrue = 0.0,
            },
            AircraftCategory.Jet
        );

    [Fact]
    public void FirstTaxiway_RightHint_StartsEastTowardRightEntry()
    {
        var route = ResolveFirstTaxiway(TwoEntryLayout(), TurnDirection.Right, out string? failReason);

        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.Equal(11, route.Segments[0].ToNodeId); // AE — went right (east) onto A
        Assert.Equal(12, route.Segments[^1].ToNodeId); // BE
    }

    [Fact]
    public void FirstTaxiway_LeftHint_StartsWestTowardLeftEntry()
    {
        var route = ResolveFirstTaxiway(TwoEntryLayout(), TurnDirection.Left, out string? failReason);

        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.Equal(13, route.Segments[0].ToNodeId); // AW — went left (west) onto A
        Assert.Equal(14, route.Segments[^1].ToNodeId); // BW
    }
}
