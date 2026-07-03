using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// Part B (first-hop heading bias): the first edge of a search has no prior edge, so admissibility
/// admits it in any direction and the turn-budget term is skipped — nothing otherwise stops a taxi from
/// starting with an unmotivated turn away from where the aircraft is facing. When the real heading is
/// known (<see cref="SearchContext.StartHeadingTrue"/>) and no explicit turn hint governs the first
/// taxiway, <see cref="RouteCostFunction.FirstHopHeadingBiasNmPerDeg"/> softly steers the first edge
/// toward that heading. These tests use synthetic graphs (same pattern as
/// <c>Issue172TurnHintTests</c>) so the geometry is exact and hint-free.
/// </summary>
public class FirstHopHeadingBiasTests
{
    private static GroundNode Node(int id, double lat, double lon) =>
        new()
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = GroundNodeType.TaxiwayIntersection,
        };

    private static void Edge(AirportGroundLayout layout, GroundNode x, GroundNode y, string twy) =>
        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [x, y],
                TaxiwayName = twy,
                DistanceNm = GeoMath.DistanceNm(x.Position, y.Position),
            }
        );

    // Symmetric split: taxiway A runs east-west through start S(10); A meets taxiway B at a junction on
    // each side (equal distances). With no turn hint, the first-hop direction is decided by heading alone.
    //
    //     BE(12)            BW(14)
    //      │                 │
    //     AE(11) ──── S(10) ──── AW(13)
    private static AirportGroundLayout TwoEntryLayout()
    {
        var s = Node(10, 37.700, -122.200);
        var ae = Node(11, 37.700, -122.197); // due east of S
        var aw = Node(13, 37.700, -122.203); // due west of S (symmetric)
        var be = Node(12, 37.701, -122.197); // B north off AE
        var bw = Node(14, 37.701, -122.203); // B north off AW

        var layout = new AirportGroundLayout { AirportId = "TEST" };
        foreach (var n in new[] { s, ae, aw, be, bw })
        {
            layout.Nodes[n.Id] = n;
        }

        Edge(layout, s, ae, "A");
        Edge(layout, s, aw, "A");
        Edge(layout, ae, be, "B");
        Edge(layout, aw, bw, "B");
        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static TaxiRoute? ResolveNoHint(AirportGroundLayout layout, double headingTrue, out string? failReason) =>
        TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: 10,
            taxiwayNames: ["A", "B"],
            out failReason,
            new ExplicitPathOptions { AirportId = "TEST", StartHeadingTrue = headingTrue },
            AircraftCategory.Jet
        );

    [Fact]
    public void EastHeading_StartsEast_NoHint()
    {
        var route = ResolveNoHint(TwoEntryLayout(), headingTrue: 90.0, out string? failReason);

        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.Equal(11, route.Segments[0].ToNodeId); // AE — continued east
    }

    [Fact]
    public void WestHeading_StartsWest_NoHint()
    {
        var route = ResolveNoHint(TwoEntryLayout(), headingTrue: 270.0, out string? failReason);

        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.Equal(13, route.Segments[0].ToNodeId); // AW — continued west
    }

    /// <summary>
    /// The decisive Part B guard: on a symmetric layout the first edge is direction-free without the bias,
    /// so both headings would produce the SAME (arbitrary) first step. Opposite first steps prove the
    /// aircraft's heading — via the bias — is what decides the direction. (Red without Part B.)
    /// </summary>
    [Fact]
    public void OppositeHeadings_ProduceOppositeFirstSteps_NoHint()
    {
        var east = ResolveNoHint(TwoEntryLayout(), headingTrue: 90.0, out _);
        var west = ResolveNoHint(TwoEntryLayout(), headingTrue: 270.0, out _);

        Assert.NotNull(east);
        Assert.NotNull(west);
        Assert.NotEqual(east.Segments[0].ToNodeId, west.Segments[0].ToNodeId);
    }

    /// <summary>
    /// A perpendicular heading makes both directions an equal turn, so the bias cancels — the search must
    /// still resolve to a valid direction, not deadlock on the tie.
    /// </summary>
    [Fact]
    public void PerpendicularHeading_ResolvesWithoutDeadlock_NoHint()
    {
        var route = ResolveNoHint(TwoEntryLayout(), headingTrue: 0.0, out string? failReason);

        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);
    }

    // Taxiway A leaves start S(20) only to the east — the sole route is a turn away from a westbound heading.
    private static AirportGroundLayout OneWayEastLayout()
    {
        var s = Node(20, 37.700, -122.200);
        var ae = Node(21, 37.700, -122.197); // east of S only

        var layout = new AirportGroundLayout { AirportId = "TEST" };
        layout.Nodes[s.Id] = s;
        layout.Nodes[ae.Id] = ae;
        Edge(layout, s, ae, "A");
        layout.RebuildAdjacencyLists();
        return layout;
    }

    /// <summary>
    /// A forced reversal must still resolve: the aircraft heads west but the only route goes east. The bias
    /// penalises that first edge, but it is the only option (every candidate pays it equally), so the route
    /// still takes it — the bias never blocks a required move.
    /// </summary>
    [Fact]
    public void ForcedReversal_StillReverses_DespiteHeadingBias()
    {
        var route = TaxiPathfinder.ResolveExplicitPath(
            OneWayEastLayout(),
            fromNodeId: 20,
            taxiwayNames: ["A"],
            out string? failReason,
            new ExplicitPathOptions { AirportId = "TEST", StartHeadingTrue = 270.0 },
            AircraftCategory.Jet
        );

        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);
        Assert.Equal(21, route.Segments[0].ToNodeId); // still taxis the only way (east), against the heading
    }
}
