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
        GroundNode ramp = Node(0, 37.700, -122.200);
        GroundNode jD = Node(1, 37.701, -122.200);
        GroundNode d1 = Node(2, 37.702, -122.200);
        GroundNode d2 = Node(3, 37.703, -122.200);
        GroundNode c = Node(4, 37.704, -122.200);
        GroundNode b = Node(5, 37.705, -122.200);

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
        GroundNode e1 = Node(0, 37.700, -122.200);
        GroundNode e2 = Node(1, 37.701, -122.200);
        GroundNode e3 = Node(2, 37.702, -122.200);
        GroundNode e4 = Node(3, 37.703, -122.200);

        var route = new TaxiRoute
        {
            Segments = [Seg(Straight(e1, e2, "E"), e1, e2), Seg(Arc(e2, e3, "A", "E"), e2, e3), Seg(Straight(e3, e4, "E"), e3, e4)],
            HoldShortPoints = [],
        };

        Assert.Equal("E", route.FormatTaxiwaySequence());
        Assert.Equal(["E"], TaxiRouteFormatter.CleanTaxiwaySequence(route));
    }

    /// <summary>
    /// A route whose last segment is the "C - G" junction arc ends on G (a runway bar on the arc's G tangent): the arc's one
    /// other taxiway is its last leg, so the route reads "C G", not "C".
    /// </summary>
    [Fact]
    public void FinalJunctionArc_AppendsItsOtherTaxiway()
    {
        GroundNode c1 = Node(0, 37.700, -122.200);
        GroundNode c2 = Node(1, 37.701, -122.200);
        GroundNode g1 = Node(2, 37.702, -122.199);

        var route = new TaxiRoute { Segments = [Seg(Straight(c1, c2, "C"), c1, c2), Seg(Arc(c2, g1, "C", "G"), c2, g1)], HoldShortPoints = [] };

        Assert.Equal("C G", route.FormatTaxiwaySequence());
        Assert.Equal(["C", "G"], TaxiRouteFormatter.CleanTaxiwaySequence(route));
    }

    /// <summary>A "C - G" junction arc in the middle of a route along C is a corner clipped, not a leg: the route reads "C".</summary>
    [Fact]
    public void MidRouteJunctionArc_AddsNoLeg()
    {
        GroundNode c1 = Node(0, 37.700, -122.200);
        GroundNode c2 = Node(1, 37.701, -122.200);
        GroundNode c3 = Node(2, 37.702, -122.200);
        GroundNode c4 = Node(3, 37.703, -122.200);

        var route = new TaxiRoute
        {
            Segments = [Seg(Straight(c1, c2, "C"), c1, c2), Seg(Arc(c2, c3, "C", "G"), c2, c3), Seg(Straight(c3, c4, "C"), c3, c4)],
            HoldShortPoints = [],
        };

        Assert.Equal(["C"], TaxiRouteFormatter.CleanTaxiwaySequence(route));
    }

    /// <summary>A final arc whose other name is the current leg's own ("C - C") names no other taxiway, so it adds nothing.</summary>
    [Fact]
    public void FinalArcNamingOnlyTheCurrentLeg_AddsNothing()
    {
        GroundNode c1 = Node(0, 37.700, -122.200);
        GroundNode c2 = Node(1, 37.701, -122.200);
        GroundNode c3 = Node(2, 37.702, -122.199);

        var route = new TaxiRoute { Segments = [Seg(Straight(c1, c2, "C"), c1, c2), Seg(Arc(c2, c3, "C", "C"), c2, c3)], HoldShortPoints = [] };

        Assert.Equal(["C"], TaxiRouteFormatter.CleanTaxiwaySequence(route));
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
        GroundNode east = Node(0, 37.720, -122.200);
        GroundNode west = Node(1, 37.720, -122.220);

        // The "RWY" prefix is what makes GroundEdge.IsRunwayCenterline true.
        GroundEdge centerline = Straight(east, west, "RWY28R/10L");

        var westbound = new TaxiRoute { Segments = [Seg(centerline, east, west)], HoldShortPoints = [] };
        Assert.Equal("on 28R", westbound.ToSummary());

        var eastbound = new TaxiRoute { Segments = [Seg(centerline, west, east)], HoldShortPoints = [] };
        Assert.Equal("on 10L", eastbound.ToSummary());
    }

    private static GroundNode Bar(int id, double lat, double lon, string runway) =>
        new()
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = RunwayIdentifier.Parse(runway),
        };

    /// <summary>
    /// A runway cleared to taxi along keeps an explicit hold short at its entry bar (<c>TAXI A 28L B HS 28L</c>), and its
    /// exit bar stands on the far side: a 2,000 ft back-taxi is far longer than any crossing, so it still reads "on 28L".
    /// </summary>
    [Fact]
    public void LongRunwayLegWithAnEntryHold_StillReadsOnTheRunway()
    {
        GroundNode a0 = Node(0, 37.7190, -122.2000);
        GroundNode barSouth = Bar(1, 37.7195, -122.2000, "28L/10R");
        GroundNode j1 = Node(2, 37.7200, -122.2000);
        GroundNode j2 = Node(3, 37.7200, -122.2075);
        GroundNode barNorth = Bar(4, 37.7205, -122.2075, "28L/10R");
        GroundNode b1 = Node(5, 37.7210, -122.2075);

        var route = new TaxiRoute
        {
            Segments =
            [
                Seg(Straight(a0, barSouth, "A"), a0, barSouth),
                Seg(Straight(barSouth, j1, "A"), barSouth, j1),
                Seg(Straight(j1, j2, "RWY28L/10R"), j1, j2),
                Seg(Straight(j2, barNorth, "B"), j2, barNorth),
                Seg(Straight(barNorth, b1, "B"), barNorth, b1),
            ],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = barSouth.Id,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "28L",
                },
            ],
        };

        Assert.Equal("A 28L B", route.FormatTaxiwaySequence());
        Assert.Contains("on 28L", route.ToSummary());
    }

    /// <summary>
    /// An earlier hold-short bar of the same runway, two legs back, is not the entry bar of a later runway leg the route
    /// enters with no bar of its own: that leg is taxied along, not crossed, and keeps reading as the runway.
    /// </summary>
    [Fact]
    public void EarlierCrossingBar_DoesNotMakeALaterHoldlessRunwayEntryACrossing()
    {
        GroundNode a0 = Node(0, 37.7180, -122.2000);
        GroundNode earlierBar = Bar(1, 37.7185, -122.2000, "28L/10R");
        GroundNode a1 = Node(2, 37.7190, -122.2000);
        GroundNode a2 = Node(3, 37.7190, -122.2020);
        GroundNode b1 = Node(4, 37.7200, -122.2020);
        GroundNode r2 = Node(5, 37.7200, -122.2025);
        GroundNode barNorth = Bar(6, 37.7205, -122.2025, "28L/10R");
        GroundNode c1 = Node(7, 37.7210, -122.2025);

        var route = new TaxiRoute
        {
            Segments =
            [
                Seg(Straight(a0, earlierBar, "A"), a0, earlierBar),
                Seg(Straight(earlierBar, a1, "A"), earlierBar, a1),
                Seg(Straight(a1, a2, "A"), a1, a2),
                Seg(Straight(a2, b1, "B"), a2, b1),
                Seg(Straight(b1, r2, "RWY28L/10R"), b1, r2),
                Seg(Straight(r2, barNorth, "C"), r2, barNorth),
                Seg(Straight(barNorth, c1, "C"), barNorth, c1),
            ],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = earlierBar.Id,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28L",
                },
            ],
        };

        Assert.Equal("A B 28L C", route.FormatTaxiwaySequence());
    }

    /// <summary>
    /// A crossing whose centreline run is just under the 300 ft bound, leaving onto B through a far-side fillet arc that the
    /// leg walk keeps on the runway's name: only the centreline counts toward the bound, the same measure the pathfinder
    /// uses, so the leg still reads as a crossing and never as "on 28L".
    /// </summary>
    [Fact]
    public void CrossingNearTheBound_CountsOnlyCenterline_NotTheFarSideFillet()
    {
        GroundNode a0 = Node(0, 37.7190, -122.2000);
        GroundNode barSouth = Bar(1, 37.7195, -122.2000, "28L/10R");
        GroundNode j1 = Node(2, 37.7200, -122.2000);
        GroundNode j2 = Node(3, 37.7200, -122.200971);
        GroundNode k = Node(4, 37.72012, -122.20110);
        GroundNode barNorth = Bar(5, 37.7203, -122.20110, "28L/10R");
        GroundNode b1 = Node(6, 37.7208, -122.20110);

        var route = new TaxiRoute
        {
            Segments =
            [
                Seg(Straight(a0, barSouth, "A"), a0, barSouth),
                Seg(Straight(barSouth, j1, "A"), barSouth, j1),
                Seg(Straight(j1, j2, "RWY28L/10R"), j1, j2),
                Seg(Arc(j2, k, "B", "RWY28L/10R"), j2, k),
                Seg(Straight(k, barNorth, "B"), k, barNorth),
                Seg(Straight(barNorth, b1, "B"), barNorth, b1),
            ],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = barSouth.Id,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28L",
                },
            ],
        };

        double centerlineFt = GeoMath.DistanceNm(j1.Position, j2.Position) * GeoMath.FeetPerNm;
        double filletFt = GeoMath.DistanceNm(j2.Position, k.Position) * GeoMath.FeetPerNm;
        Assert.InRange(centerlineFt, 250.0, 300.0);
        Assert.True(centerlineFt + filletFt > 300.0, $"centreline {centerlineFt:F0} ft + fillet {filletFt:F0} ft does not pass the bound");

        Assert.Equal("A B", route.FormatTaxiwaySequence());
    }

    [Fact]
    public void KeepsSingleNameArcs()
    {
        GroundNode a = Node(0, 37.700, -122.200);
        GroundNode b = Node(1, 37.701, -122.200);
        GroundNode c = Node(2, 37.702, -122.200);

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

    /// <summary>
    /// "Without delay" with no taxiway shown before it (every taxiway filtered out, as the rollout echo drops the leading
    /// implied-exit taxiway) follows the destination: "@A1, without delay", never "without delay, @A1".
    /// </summary>
    [Fact]
    public void ToSummary_WithoutDelay_AndNoTaxiwayShown_FollowsTheDestination()
    {
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.701, -122.200);
        var route = new TaxiRoute
        {
            Segments = [Seg(Straight(n0, n1, "A"), n0, n1)],
            HoldShortPoints = [],
            DestinationParking = "A1",
        };

        Assert.Equal("@A1, without delay", route.ToSummary(null, [], static _ => false, [], withoutDelay: true));
        Assert.Equal("A, without delay, @A1", route.ToSummary(null, [], static _ => true, [], withoutDelay: true));
    }

    [Fact]
    public void ToSummary_SkipsMembershipArcs()
    {
        GroundNode c1 = Node(0, 37.700, -122.200);
        GroundNode cj = Node(1, 37.701, -122.200);
        GroundNode e1 = Node(2, 37.702, -122.200);
        GroundNode e2 = Node(3, 37.703, -122.200);

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
