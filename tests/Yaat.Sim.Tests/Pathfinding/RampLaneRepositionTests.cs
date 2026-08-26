using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// Issue #396: SFO ramp lanes M3 / M4 / M5 are parallel taxilanes covering one ramp with no painted connectors
/// between them. A <c>TAXI M4 …</c> whose first lane the graph cannot reach from the aircraft's lane is honoured
/// by a free-space cut across the apron onto the named lane (a virtual first segment), then the normal graph
/// route from there. Guarded to numbered sibling lanes (same letter prefix), a short crossing, and open apron
/// (no runway centerline, no foreign taxiway between).
/// </summary>
public class RampLaneRepositionTests
{
    private const string Sfo = "SFO";
    private readonly ITestOutputHelper _output;

    public RampLaneRepositionTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static AirportGroundLayout? Layout() => TestVnasData.NavigationDb is null ? null : new TestAirportGroundData().GetLayout(Sfo);

    private static AirportGroundLayout? OakLayout() => TestVnasData.NavigationDb is null ? null : new TestAirportGroundData().GetLayout("OAK");

    private static ExplicitPathOptions Options(AirportGroundLayout layout, string? destinationRunway) =>
        new() { AirportId = layout.AirportId, DestinationRunway = destinationRunway };

    /// <summary>Resolve from the graph node nearest <paramref name="position"/> the way TryTaxi does, returning the structured failure.</summary>
    private static PathfindingFailure? FailureFor(
        AirportGroundLayout layout,
        LatLon position,
        TrueHeading heading,
        List<string> path,
        ExplicitPathOptions options
    )
    {
        var start = layout.FindNearestNodeForTaxi(position, heading) ?? layout.FindNearestNode(position);
        Assert.NotNull(start);
        var route = TaxiPathfinder.ResolveExplicitPathDetailed(layout, start.Id, path, out var failure, options, AircraftCategory.Jet);
        Assert.Null(route);
        Assert.NotNull(failure);
        return failure;
    }

    private static bool Traverses(TaxiRoute route, string twy) =>
        route.Segments.Any(s =>
            s.TaxiwayName.Split([' ', '-', '/', ','], StringSplitOptions.RemoveEmptyEntries)
                .Any(tok => string.Equals(tok, twy, StringComparison.OrdinalIgnoreCase))
        );

    [Theory]
    [InlineData("M3", true)]
    [InlineData("M4", true)]
    [InlineData("M5", true)]
    [InlineData("A", false)]
    [InlineData("A1", false)]
    [InlineData("GL", false)]
    [InlineData("28L", false)]
    [InlineData("#12", false)]
    public void IsRampTaxilane_Sfo(string name, bool expected)
    {
        var layout = Layout();
        if (layout is null)
        {
            return;
        }

        Assert.Equal(expected, RampLaneReposition.IsRampTaxilane(layout, name));
    }

    /// <summary>
    /// The name form alone also matches OAK's W1–W7 runway connectors; a lane carrying a runway holding position is
    /// never a ramp lane. B1 is the GA hangar-row lane (RAMP along its length, no hold-short) and qualifies.
    /// </summary>
    [Theory]
    [InlineData("TE", true)]
    [InlineData("TC", true)]
    [InlineData("T", false)]
    [InlineData("V", false)]
    [InlineData("W3", false)]
    [InlineData("B1", true)]
    public void IsRampTaxilane_Oak(string name, bool expected)
    {
        var layout = OakLayout();
        if (layout is null)
        {
            return;
        }

        Assert.Equal(expected, RampLaneReposition.IsRampTaxilane(layout, name));
    }

    [Theory]
    [InlineData("TE", "TC", true)]
    [InlineData("M3", "M5", true)]
    [InlineData("m4", "M5", true)]
    [InlineData("TE", "M4", false)]
    [InlineData("TE", "TE", false)]
    public void AreSiblingLanes(string a, string b, bool expected)
    {
        Assert.Equal(expected, RampLaneReposition.AreSiblingLanes(a, b));
    }

    [Fact]
    public void GateB20S_TaxiM4_CutsAcrossOntoM4ThenFollowsTheGraph()
    {
        var layout = Layout();
        if (layout is null)
        {
            return;
        }

        var gate = layout.FindParkingByName("B20S")!;
        var path = new List<string> { "M4", "M1", "A", "H", "GL", "L", "LF", "F" };
        var options = Options(layout, "28L");
        var failure = FailureFor(layout, gate.Position, gate.TrueHeading!.Value, path, options)!;
        Assert.Equal(FailureKind.TaxiwayNotConnected, failure.Kind);

        var plan = RampLaneReposition.TryPlan(layout, gate.Position, gate.TrueHeading!.Value, null, path, failure, options, AircraftCategory.Jet);
        Assert.NotNull(plan);
        _output.WriteLine($"plan: lane {plan.Lane} target #{plan.TargetNode.Id} crossing {plan.CrossingFt:F0} ft; {plan.Route.ToSummary()}");

        Assert.Equal("M4", plan.Lane);
        Assert.True(plan.CrossingFt <= RampLaneReposition.MaxCrossingFt, $"crossing {plan.CrossingFt:F0} ft exceeds the cap");
        Assert.True(plan.TargetNode.Edges.Any(e => e is GroundEdge && e.MatchesTaxiway("M4")), "target must sit on a straight M4 edge");

        var first = plan.Route.Segments[0];
        Assert.True(first.FromNodeId < 0, "the crossing is a free-space (virtual) leg from the aircraft's position");
        Assert.Equal(plan.TargetNode.Id, first.ToNodeId);
        Assert.Equal("M4", first.TaxiwayName);
        Assert.True(Traverses(plan.Route, "M1"), "route must continue onto M1");
        Assert.True(Traverses(plan.Route, "A"), "route must reach A");
        Assert.Contains(plan.Route.HoldShortPoints, h => h.Reason == HoldShortReason.DestinationRunway);
    }

    [Fact]
    public void GateB20S_TaxiM5_CutsStraightAcrossM4OntoM5()
    {
        var layout = Layout();
        if (layout is null)
        {
            return;
        }

        var gate = layout.FindParkingByName("B20S")!;
        var path = new List<string> { "M5", "M1", "A", "A1" };
        var options = Options(layout, "1R");
        var failure = FailureFor(layout, gate.Position, gate.TrueHeading!.Value, path, options)!;

        var plan = RampLaneReposition.TryPlan(layout, gate.Position, gate.TrueHeading!.Value, null, path, failure, options, AircraftCategory.Jet);
        Assert.NotNull(plan);
        _output.WriteLine($"plan: lane {plan.Lane} target #{plan.TargetNode.Id} crossing {plan.CrossingFt:F0} ft; {plan.Route.ToSummary()}");
        Assert.Equal("M5", plan.Lane);
        Assert.Equal("M5", plan.Route.Segments[0].TaxiwayName);
        Assert.True(Traverses(plan.Route, "M1"), "route must continue onto M1");
        Assert.False(Traverses(plan.Route, "M4"), "M4 is crossed in free space, never taxied along");
    }

    [Fact]
    public void MidLaneOnM3_TaxiM4_CutsAcrossAndHeadsTowardM1()
    {
        var layout = Layout();
        if (layout is null)
        {
            return;
        }

        // Node 469 is mid-M3 on the Terminal 1 ramp; M3 runs ~027°/207° here and M1 lies to the south-west.
        var onM3 = layout.Nodes[469];
        var heading = new TrueHeading(207);
        var path = new List<string> { "M4", "M1", "A", "A1" };
        var options = Options(layout, "1R");
        // Mid-lane the resolver first tries a connector detour around the missing M4 leg, so it reports the
        // dead end as an unreachable destination rather than blaming M4 outright.
        var failure = FailureFor(layout, onM3.Position, heading, path, options)!;

        var plan = RampLaneReposition.TryPlan(layout, onM3.Position, heading, "M3", path, failure, options, AircraftCategory.Jet);
        Assert.NotNull(plan);
        _output.WriteLine($"plan: lane {plan.Lane} target #{plan.TargetNode.Id} crossing {plan.CrossingFt:F0} ft; {plan.Route.ToSummary()}");

        Assert.True(plan.CrossingFt <= 300, $"a mid-lane switch is a short cut across the alley, not {plan.CrossingFt:F0} ft");
        Assert.True(Traverses(plan.Route, "M1"), "route must continue onto M1");

        // The first on-lane segment must head south-west toward M1, not double back north-east.
        var onLane = plan.Route.Segments[1];
        double bearing = GeoMath.BearingTo(onLane.Edge.FromNode.Position, onLane.Edge.ToNode.Position);
        Assert.True(GeoMath.AbsBearingDifference(bearing, 207) < 60, $"first M4 segment bears {bearing:F0}°, expected ~207° toward M1");
    }

    [Fact]
    public void Plan_RoundTripsThroughASnapshot()
    {
        var layout = Layout();
        if (layout is null)
        {
            return;
        }

        var gate = layout.FindParkingByName("B20S")!;
        var path = new List<string> { "M4", "M1", "A", "A1" };
        var options = Options(layout, "1R");
        var failure = FailureFor(layout, gate.Position, gate.TrueHeading!.Value, path, options)!;
        var plan = RampLaneReposition.TryPlan(layout, gate.Position, gate.TrueHeading!.Value, null, path, failure, options, AircraftCategory.Jet)!;

        // The free-space leg's virtual start is not a layout node; restore rebuilds it from the recorded position.
        var dto = plan.Route.ToSnapshot();
        Assert.True(dto.Segments[0].FromNodeId < 0);
        Assert.Equal(gate.Position.Lat, dto.Segments[0].FromLatitude);
        Assert.Equal(gate.Position.Lon, dto.Segments[0].FromLongitude);
        Assert.Null(dto.Segments[0].ToLatitude);
        Assert.Null(dto.Segments[1].FromLatitude);

        var restored = TaxiRoute.FromSnapshot(dto, layout);
        Assert.NotNull(restored);
        Assert.Equal(plan.Route.Segments.Count, restored.Segments.Count);
        Assert.Equal(gate.Position, restored.Segments[0].Edge.FromNode.Position);
        Assert.Equal(plan.TargetNode.Id, restored.Segments[0].ToNodeId);
        Assert.Equal("M4", restored.Segments[0].TaxiwayName);
        Assert.Equal(plan.Route.Segments[^1].ToNodeId, restored.Segments[^1].ToNodeId);
        Assert.Equal(plan.Route.HoldShortPoints.Count, restored.HoldShortPoints.Count);
    }

    [Fact]
    public void LaterTaxiwayDoesNotReachTheRunway_NoPlan()
    {
        var layout = Layout();
        if (layout is null)
        {
            return;
        }

        // M4 itself would be reachable by a cut, but the clearance dies further along: A never reaches 28L. The
        // resolver blames A, not the lane, so no cut is attempted and the controller gets the real error.
        var onM3 = layout.Nodes[469];
        var heading = new TrueHeading(207);
        var path = new List<string> { "M4", "M1", "A" };
        var options = Options(layout, "28L");
        var failure = FailureFor(layout, onM3.Position, heading, path, options)!;
        Assert.Equal(FailureKind.DestinationUnreachable, failure.Kind);
        Assert.Equal("A", failure.InfeasibleTaxiway);

        var plan = RampLaneReposition.TryPlan(layout, onM3.Position, heading, "M3", path, failure, options, AircraftCategory.Jet);
        Assert.Null(plan);
    }

    [Fact]
    public void Gate4115_TaxiA_IsNotASiblingLane_NoPlan()
    {
        var layout = Layout();
        if (layout is null)
        {
            return;
        }

        var gate = layout.FindParkingByName("41-15")!;
        var path = new List<string> { "A", "E" };
        var options = Options(layout, "28R");
        var failure = FailureFor(layout, gate.Position, gate.TrueHeading!.Value, path, options)!;

        var plan = RampLaneReposition.TryPlan(layout, gate.Position, gate.TrueHeading!.Value, null, path, failure, options, AircraftCategory.Piston);
        Assert.Null(plan);
    }

    [Fact]
    public void GateB20S_TaxiM2_BeyondCrossingRange_NoPlan()
    {
        var layout = Layout();
        if (layout is null)
        {
            return;
        }

        // M2 is a sibling lane but ~840 ft away across M4/M5 — far beyond a lane switch.
        var gate = layout.FindParkingByName("B20S")!;
        var path = new List<string> { "M2", "A" };
        var options = Options(layout, "28L");
        var failure = FailureFor(layout, gate.Position, gate.TrueHeading!.Value, path, options)!;

        var plan = RampLaneReposition.TryPlan(layout, gate.Position, gate.TrueHeading!.Value, null, path, failure, options, AircraftCategory.Jet);
        Assert.Null(plan);
    }

    [Fact]
    public void ForeignTaxiwayBetweenTheLanes_NoPlan()
    {
        // Synthetic mini-airport: gate G leads onto lane M3; lane M9 runs parallel 200 ft away, but a lettered
        // taxiway K lies between them. The cut would cross K — a movement-area taxiway, not open apron.
        var layout = GeoJsonParser.Parse("TST", MiniRampGeoJson(withTaxiwayBetween: true), "TST");
        var gate = layout.FindParkingByName("G")!;
        var path = new List<string> { "M9" };
        var options = Options(layout, null);
        var failure = FailureFor(layout, gate.Position, gate.TrueHeading!.Value, path, options)!;

        var plan = RampLaneReposition.TryPlan(layout, gate.Position, gate.TrueHeading!.Value, null, path, failure, options, AircraftCategory.Jet);
        Assert.Null(plan);
    }

    [Fact]
    public void OpenApronBetweenTheLanes_Plans()
    {
        var layout = GeoJsonParser.Parse("TST", MiniRampGeoJson(withTaxiwayBetween: false), "TST");
        var gate = layout.FindParkingByName("G")!;
        var path = new List<string> { "M9" };
        var options = Options(layout, null);
        var failure = FailureFor(layout, gate.Position, gate.TrueHeading!.Value, path, options)!;

        var plan = RampLaneReposition.TryPlan(layout, gate.Position, gate.TrueHeading!.Value, null, path, failure, options, AircraftCategory.Jet);
        Assert.NotNull(plan);
        Assert.Equal("M9", plan.Lane);
    }

    /// <summary>
    /// Two parallel north–south lanes 150 ft apart (M3 at lon 0, M9 at lon +150 ft), a gate 250 ft west of M3 with
    /// a lead-out onto it (an SFO-like alley, long enough that the lead-out's fillet stays clear of the gate), a gate
    /// H 250 ft east of M9 so both lanes are ramp-attached, and optionally a lettered taxiway K running between the lanes.
    /// </summary>
    private static string MiniRampGeoJson(bool withTaxiwayBetween)
    {
        const double lat0 = 37.60;
        const double lon0 = -122.38;
        const double degPerFtLat = 1.0 / 364000.0;
        double degPerFtLon = degPerFtLat / Math.Cos(lat0 * Math.PI / 180.0);
        string Lon(double ft) => (lon0 + (ft * degPerFtLon)).ToString("F7", System.Globalization.CultureInfo.InvariantCulture);
        string Lat(double ft) => (lat0 + (ft * degPerFtLat)).ToString("F7", System.Globalization.CultureInfo.InvariantCulture);
        string Line(string name, double lonFt) =>
            $$"""
                { "type": "Feature", "properties": { "type": "taxiway", "name": "{{name}}" },
                  "geometry": { "type": "LineString",
                    "coordinates": [[{{Lon(lonFt)}}, {{Lat(-600)}}], [{{Lon(lonFt)}}, {{Lat(0)}}], [{{Lon(lonFt)}}, {{Lat(600)}}]] } }
                """;
        string between = withTaxiwayBetween ? "," + Line("K", 75) : "";
        return $$"""
            { "type": "FeatureCollection", "features": [
              { "type": "Feature", "properties": { "type": "parking", "name": "G", "heading": 270 },
                "geometry": { "type": "Point", "coordinates": [{{Lon(-250)}}, {{Lat(0)}}] } },
              { "type": "Feature", "properties": { "type": "parking", "name": "H", "heading": 90 },
                "geometry": { "type": "Point", "coordinates": [{{Lon(400)}}, {{Lat(0)}}] } },
              {{Line("M3", 0)}},
              {{Line("M9", 150)}}{{between}}
            ] }
            """;
    }
}
