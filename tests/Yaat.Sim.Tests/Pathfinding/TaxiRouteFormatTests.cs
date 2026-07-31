using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// Unit tests for <see cref="TaxiRoute.FormatTaxiwaySequence"/> — the operator-facing route string
/// (Aircraft List Info column + DTO TaxiRoute field) and TaxiRoute.ToSummary. Multi-name junction arcs
/// (<c>"D - RAMP"</c>) are transitions between taxiways, not legs, so they must never appear.
/// </summary>
public class TaxiRouteFormatTests
{
    private static GroundNode Node(int id, double lat, double lon) =>
        new()
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = GroundNodeType.TaxiwayIntersection,
        };

    private static GroundEdge Straight(GroundNode a, GroundNode b, string twy) =>
        new()
        {
            Nodes = [a, b],
            TaxiwayName = twy,
            DistanceNm = GeoMath.DistanceNm(a.Position, b.Position),
        };

    private static GroundArc Arc(GroundNode a, GroundNode b, params string[] names) =>
        new()
        {
            Nodes = [a, b],
            P1Lat = a.Position.Lat,
            P1Lon = a.Position.Lon,
            P2Lat = b.Position.Lat,
            P2Lon = b.Position.Lon,
            MinRadiusOfCurvatureFt = 100,
            DistanceNm = GeoMath.DistanceNm(a.Position, b.Position),
            TaxiwayNames = names,
        };

    private static TaxiRouteSegment Seg(IGroundEdge e, GroundNode from, GroundNode to) =>
        new() { Edge = e.Directed(from, to), TaxiwayName = e is GroundArc arc ? arc.TaxiwayName : ((GroundEdge)e).TaxiwayName };

    [Fact]
    public void SkipsMembershipArcs_AndCollapsesRepeats()
    {
        var ramp = Node(0, 37.700, -122.200);
        var jD = Node(1, 37.701, -122.200);
        var d1 = Node(2, 37.702, -122.200);
        var d2 = Node(3, 37.703, -122.200);
        var c = Node(4, 37.704, -122.200);
        var b = Node(5, 37.705, -122.200);

        var route = new TaxiRoute
        {
            Segments =
            [
                Seg(Straight(ramp, jD, "RAMP"), ramp, jD),
                Seg(Arc(jD, d1, "D", "RAMP"), jD, d1), // "D - RAMP" junction arc — must be skipped
                Seg(Straight(d1, d2, "D"), d1, d2),
                Seg(Straight(d2, c, "C"), d2, c),
                Seg(Straight(c, b, "B"), c, b),
            ],
            HoldShortPoints = [],
        };

        // RAMP is dropped: a route into or out of a stand names the stand, not the pavement class.
        Assert.Equal("D C B", route.FormatTaxiwaySequence());
    }

    /// <summary>
    /// A composite arc must resolve to the taxiway being followed, not to its first member — the walk
    /// stays on the current name whenever the edge belongs to it. Without that stickiness a route
    /// along E that clips an "A - E" corner reads "E A E".
    /// </summary>
    [Fact]
    public void CompositeArc_KeepsTheTaxiwayBeingFollowed()
    {
        var e1 = Node(0, 37.700, -122.200);
        var e2 = Node(1, 37.701, -122.200);
        var e3 = Node(2, 37.702, -122.200);
        var e4 = Node(3, 37.703, -122.200);

        var route = new TaxiRoute
        {
            Segments = [Seg(Straight(e1, e2, "E"), e1, e2), Seg(Arc(e2, e3, "A", "E"), e2, e3), Seg(Straight(e3, e4, "E"), e3, e4)],
            HoldShortPoints = [],
        };

        Assert.Equal("E", route.FormatTaxiwaySequence());
        Assert.Equal(["E"], TaxiRouteFormatter.CleanTaxiwaySequence(route));
    }

    /// <summary>
    /// A runway taxied ALONG reads as the end the aircraft is travelling toward. A drawn route names
    /// no runway in its path (every token is a node reference), so there is no cleared designator to
    /// match and the direction of travel is the only signal — it must not fall back to the internal
    /// combined id "28R/10L".
    /// </summary>
    [Fact]
    public void RunwayTaxiedAlong_NamesTheEndTravelledToward()
    {
        // Westbound down the 28R/10L centerline.
        var east = Node(0, 37.720, -122.200);
        var west = Node(1, 37.720, -122.220);

        // The "RWY" prefix is what makes GroundEdge.IsRunwayCenterline true.
        var centerline = Straight(east, west, "RWY28R/10L");

        var westbound = new TaxiRoute { Segments = [Seg(centerline, east, west)], HoldShortPoints = [] };
        Assert.Equal("on 28R", westbound.ToSummary());

        var eastbound = new TaxiRoute { Segments = [Seg(centerline, west, east)], HoldShortPoints = [] };
        Assert.Equal("on 10L", eastbound.ToSummary());
    }

    [Fact]
    public void KeepsSingleNameArcs()
    {
        var a = Node(0, 37.700, -122.200);
        var b = Node(1, 37.701, -122.200);
        var c = Node(2, 37.702, -122.200);

        var route = new TaxiRoute
        {
            Segments =
            [
                Seg(Straight(a, b, "A"), a, b),
                Seg(Arc(b, c, "A"), b, c), // single-name arc on A — kept, collapses as a repeat of A
            ],
            HoldShortPoints = [],
        };

        Assert.Equal("A", route.FormatTaxiwaySequence());
    }

    [Fact]
    public void EmptyRoute_IsEmptyString()
    {
        var route = new TaxiRoute { Segments = [], HoldShortPoints = [] };
        Assert.Equal("", route.FormatTaxiwaySequence());
    }

    [Fact]
    public void ToSummary_SkipsMembershipArcs()
    {
        var c1 = Node(0, 37.700, -122.200);
        var cj = Node(1, 37.701, -122.200);
        var e1 = Node(2, 37.702, -122.200);
        var e2 = Node(3, 37.703, -122.200);

        var route = new TaxiRoute
        {
            Segments =
            [
                Seg(Straight(c1, cj, "C"), c1, cj),
                Seg(Arc(cj, e1, "C", "E"), cj, e1), // "C - E" membership arc — must not appear as a token
                Seg(Straight(e1, e2, "E"), e1, e2),
            ],
            HoldShortPoints = [],
        };

        Assert.Equal("C E", route.ToSummary());
    }
}
