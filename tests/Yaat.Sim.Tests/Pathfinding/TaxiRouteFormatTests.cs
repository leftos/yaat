using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// Unit tests for <see cref="TaxiRoute.FormatTaxiwaySequence"/> — the operator-facing route string
/// (Aircraft List Info column + DTO TaxiRoute field). Multi-name junction/membership arcs
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

        Assert.Equal("RAMP D C B", route.FormatTaxiwaySequence());
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
}
