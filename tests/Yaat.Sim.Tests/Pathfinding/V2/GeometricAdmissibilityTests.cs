using System.Collections.Immutable;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Tests.Pathfinding.V2;

/// <summary>
/// Unit tests for <see cref="GeometricAdmissibility"/>.
/// All tests use inline synthetic nodes/edges — no navdata required.
/// </summary>
public class GeometricAdmissibilityTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static GroundNode Node(int id, double lat, double lon) =>
        new()
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = GroundNodeType.TaxiwayIntersection,
        };

    private static GroundEdge StraightEdge(GroundNode a, GroundNode b, string twy = "A")
    {
        double dist = GeoMath.DistanceNm(a.Position, b.Position);
        var edge = new GroundEdge
        {
            Nodes = [a, b],
            TaxiwayName = twy,
            DistanceNm = dist,
        };
        a.Edges.Add(edge);
        b.Edges.Add(edge);
        return edge;
    }

    /// <summary>
    /// Constructs a minimal GroundArc between <paramref name="natural0"/> and <paramref name="natural1"/>
    /// where Nodes[0] = natural0 (forward traversal is natural0 → natural1).
    /// Control points are placed along the straight line (degenerate arc — zero curvature).
    /// The tangent at either end equals the straight bearing between the two nodes.
    /// </summary>
    private static GroundArc StraightArc(GroundNode natural0, GroundNode natural1, string twy = "A")
    {
        double dist = GeoMath.DistanceNm(natural0.Position, natural1.Position);
        double p1Lat = natural0.Position.Lat + (natural1.Position.Lat - natural0.Position.Lat) / 3.0;
        double p1Lon = natural0.Position.Lon + (natural1.Position.Lon - natural0.Position.Lon) / 3.0;
        double p2Lat = natural0.Position.Lat + 2.0 * (natural1.Position.Lat - natural0.Position.Lat) / 3.0;
        double p2Lon = natural0.Position.Lon + 2.0 * (natural1.Position.Lon - natural0.Position.Lon) / 3.0;

        var arc = new GroundArc
        {
            Nodes = [natural0, natural1],
            TaxiwayNames = [twy],
            DistanceNm = dist,
            P1Lat = p1Lat,
            P1Lon = p1Lon,
            P2Lat = p2Lat,
            P2Lon = p2Lon,
            MinRadiusOfCurvatureFt = 50000.0,
        };
        natural0.Edges.Add(arc);
        natural1.Edges.Add(arc);
        return arc;
    }

    private static PartialRoute BuildRoute(int startId, double arrivalBearing, IGroundEdge? lastEdge = null)
    {
        var visited = ImmutableHashSet<int>.Empty.Add(startId);
        return new PartialRoute(
            HeadNodeId: startId,
            ArrivalBearing: arrivalBearing,
            LastEdge: lastEdge,
            LastTaxiwayName: string.Empty,
            Previous: lastEdge is null ? null : PartialRoute.StartAt(startId),
            Depth: lastEdge is null ? 0 : 1,
            AccumulatedCost: 0.0,
            VisitedNodeIds: visited
        );
    }

    // ---------------------------------------------------------------------------
    // Straight edge admissibility
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 0.0, AircraftCategory.Jet, true)] // 0° change: always admit
    [InlineData(0.0, 90.0, AircraftCategory.Jet, true)] // 90° change: within jet 135° limit
    [InlineData(0.0, 134.9, AircraftCategory.Jet, true)] // just under jet limit
    [InlineData(0.0, 135.1, AircraftCategory.Jet, false)] // just over jet limit
    [InlineData(0.0, 154.9, AircraftCategory.Piston, true)] // just under piston limit
    [InlineData(0.0, 155.1, AircraftCategory.Piston, false)] // just over piston limit
    public void StraightEdge_HeadingDelta_DeterminesAdmissibility(
        double arrivalBearing,
        double departureOffsetDeg,
        AircraftCategory category,
        bool expected
    )
    {
        // Build two nodes so the departure bearing toward n2 equals arrivalBearing + departureOffsetDeg.
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);

        // The arrival bearing into n1 is the bearing n0→n1. We'll construct a synthetic route
        // that has the desired arrival bearing directly.
        var e01 = StraightEdge(n0, n1);

        // Construct n2 so that bearing n1→n2 = (arrivalBearing + departureOffsetDeg) % 360.
        double departureBearing = (arrivalBearing + departureOffsetDeg) % 360.0;
        // Place n2 in the direction of departureBearing from n1 at 0.01 nm.
        double deltaLat = Math.Cos(departureBearing * Math.PI / 180.0) * 0.01 / 60.0;
        double deltaLon = Math.Sin(departureBearing * Math.PI / 180.0) * 0.01 / (60.0 * Math.Cos(n1.Position.Lat * Math.PI / 180.0));
        var n2 = Node(2, n1.Position.Lat + deltaLat, n1.Position.Lon + deltaLon);
        var e12 = StraightEdge(n1, n2);

        // Build route arriving at n1 with the specified arrival bearing.
        var routeAtN1 = new PartialRoute(
            HeadNodeId: n1.Id,
            ArrivalBearing: arrivalBearing,
            LastEdge: e01,
            LastTaxiwayName: "A",
            Previous: PartialRoute.StartAt(n0.Id),
            Depth: 1,
            AccumulatedCost: 0.0,
            VisitedNodeIds: ImmutableHashSet<int>.Empty.Add(n0.Id).Add(n1.Id)
        );

        bool result = GeometricAdmissibility.IsAdmissible(routeAtN1, e12, n2, category);
        Assert.Equal(expected, result);
    }

    // ---------------------------------------------------------------------------
    // No previous edge: always admit
    // ---------------------------------------------------------------------------

    [Fact]
    public void NoLastEdge_AlwaysAdmitted()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var e = StraightEdge(n0, n1);

        var routeAtStart = PartialRoute.StartAt(n0.Id);
        bool result = GeometricAdmissibility.IsAdmissible(routeAtStart, e, n1, AircraftCategory.Jet);
        Assert.True(result);
    }

    // ---------------------------------------------------------------------------
    // Arc direction
    // ---------------------------------------------------------------------------

    [Fact]
    public void ForwardArc_WithinCategoryLimit_IsAdmitted()
    {
        // n0→n1→arc(n1→n2): forward arc aligned with heading, small turn angle.
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var n2 = Node(2, 37.701, -122.199);

        var eForward = StraightEdge(n0, n1);
        var arc = StraightArc(n1, n2);

        double arrivalBearing = GeoMath.BearingTo(n0.Position, n1.Position);

        var routeAtN1 = new PartialRoute(
            HeadNodeId: n1.Id,
            ArrivalBearing: arrivalBearing,
            LastEdge: eForward,
            LastTaxiwayName: "A",
            Previous: PartialRoute.StartAt(n0.Id),
            Depth: 1,
            AccumulatedCost: 0.0,
            VisitedNodeIds: ImmutableHashSet<int>.Empty.Add(n0.Id).Add(n1.Id)
        );

        bool result = GeometricAdmissibility.IsAdmissible(routeAtN1, arc, n2, AircraftCategory.Jet);
        Assert.True(result);
    }

    [Fact]
    public void ReverseArc_WithinHeadingLimit_IsAdmitted()
    {
        // Arc with Nodes[0]=n2, Nodes[1]=n1. Traversing from n1→n2 is reverse.
        // Heading change is ~90° which is within the Jet 135° limit.
        // Per §Decisions §3 (revised): reverse arcs are admitted when heading delta is within limit;
        // they are penalised by ReverseArcCostNm in the cost function instead.
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var n2 = Node(2, 37.701, -122.199);

        var eForward = StraightEdge(n0, n1);
        // Arc's natural direction is n2→n1 (Nodes[0]=n2). Traversing from n1→n2 is reverse.
        var arc = StraightArc(n2, n1);

        double arrivalBearing = GeoMath.BearingTo(n0.Position, n1.Position);

        var routeAtN1 = new PartialRoute(
            HeadNodeId: n1.Id,
            ArrivalBearing: arrivalBearing,
            LastEdge: eForward,
            LastTaxiwayName: "A",
            Previous: PartialRoute.StartAt(n0.Id),
            Depth: 1,
            AccumulatedCost: 0.0,
            VisitedNodeIds: ImmutableHashSet<int>.Empty.Add(n0.Id).Add(n1.Id)
        );

        // Reverse traversal but heading delta ~90° < 135° limit — admitted.
        bool result = GeometricAdmissibility.IsAdmissible(routeAtN1, arc, n2, AircraftCategory.Jet);
        Assert.True(result);
    }

    [Fact]
    public void ReverseArc_ExceedingHeadingLimit_IsRejected()
    {
        // Arc with Nodes[0]=n2, Nodes[1]=n1. Traversing from n1→n2 where n2 is almost behind n1.
        // The departure bearing of the reverse arc is ~160° from arrival bearing, which exceeds 135°.
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);

        // n2 is positioned so bearing n1→n2 ≈ 180° (south), giving a ~180° heading change.
        var n2 = Node(2, 37.699, -122.200);

        var eForward = StraightEdge(n0, n1);
        // Arc's natural direction is n2→n1 (forward: south→north). Traversing from n1→n2 is reverse.
        var arc = StraightArc(n2, n1);

        double arrivalBearing = GeoMath.BearingTo(n0.Position, n1.Position); // north ≈ 0°

        var routeAtN1 = new PartialRoute(
            HeadNodeId: n1.Id,
            ArrivalBearing: arrivalBearing,
            LastEdge: eForward,
            LastTaxiwayName: "A",
            Previous: PartialRoute.StartAt(n0.Id),
            Depth: 1,
            AccumulatedCost: 0.0,
            VisitedNodeIds: ImmutableHashSet<int>.Empty.Add(n0.Id).Add(n1.Id)
        );

        // Reverse traversal with heading delta ~180° > 135° limit — rejected.
        bool result = GeometricAdmissibility.IsAdmissible(routeAtN1, arc, n2, AircraftCategory.Jet);
        Assert.False(result);
    }

    // ---------------------------------------------------------------------------
    // Category sensitivity
    // ---------------------------------------------------------------------------

    [Fact]
    public void CategorySensitivity_SameHeadingDelta_DifferentResultForDifferentCategories()
    {
        // 140° delta: exceeds Jet limit (135°) but within Piston limit (155°).
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);

        var arrivalBearing = GeoMath.BearingTo(n0.Position, n1.Position);
        double departureBearing = (arrivalBearing + 140.0) % 360.0;

        double deltaLat = Math.Cos(departureBearing * Math.PI / 180.0) * 0.01 / 60.0;
        double deltaLon = Math.Sin(departureBearing * Math.PI / 180.0) * 0.01 / (60.0 * Math.Cos(n1.Position.Lat * Math.PI / 180.0));
        var n2 = Node(2, n1.Position.Lat + deltaLat, n1.Position.Lon + deltaLon);

        var eIn = StraightEdge(n0, n1);
        var eOut = StraightEdge(n1, n2);

        var route = new PartialRoute(
            HeadNodeId: n1.Id,
            ArrivalBearing: arrivalBearing,
            LastEdge: eIn,
            LastTaxiwayName: "A",
            Previous: PartialRoute.StartAt(n0.Id),
            Depth: 1,
            AccumulatedCost: 0.0,
            VisitedNodeIds: ImmutableHashSet<int>.Empty.Add(n0.Id).Add(n1.Id)
        );

        bool jetResult = GeometricAdmissibility.IsAdmissible(route, eOut, n2, AircraftCategory.Jet);
        bool pistonResult = GeometricAdmissibility.IsAdmissible(route, eOut, n2, AircraftCategory.Piston);

        Assert.False(jetResult, "140° should exceed jet limit (135°)");
        Assert.True(pistonResult, "140° should be within piston limit (155°)");
    }

    // ---------------------------------------------------------------------------
    // Edge case: 0° arrival, 359° departure → 1° delta, not 359°
    // ---------------------------------------------------------------------------

    [Fact]
    public void HeadingWrap_ZeroArrivalToAlmost360Departure_DeltaIsOne()
    {
        double delta = RouteCostFunction.HeadingDelta(0.0, 359.0);
        Assert.Equal(1.0, delta, precision: 6);
    }

    // ---------------------------------------------------------------------------
    // IsReverseTraversal helper
    // ---------------------------------------------------------------------------

    [Fact]
    public void IsReverseTraversal_Nodes0IsFromNode_ReturnsFalse()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var arc = StraightArc(n0, n1);

        Assert.False(GeometricAdmissibility.IsReverseTraversal(arc, n0));
    }

    [Fact]
    public void IsReverseTraversal_Nodes1IsFromNode_ReturnsTrue()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.701, -122.200);
        var arc = StraightArc(n0, n1);

        Assert.True(GeometricAdmissibility.IsReverseTraversal(arc, n1));
    }
}
