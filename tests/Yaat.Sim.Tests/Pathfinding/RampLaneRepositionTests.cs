using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation.Snapshots;
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

    private static ExplicitPathOptions Options(string? destinationRunway) => new() { DestinationRunway = destinationRunway, OccupiedTaxiway = null };

    /// <summary>Resolve from the graph node nearest <paramref name="position"/> the way TryTaxi does, returning the structured failure.</summary>
    private static PathfindingFailure? FailureFor(
        AirportGroundLayout layout,
        LatLon position,
        TrueHeading heading,
        List<string> path,
        ExplicitPathOptions options
    )
    {
        GroundNode? start = layout.FindNearestNodeForTaxi(position, heading) ?? layout.FindNearestNode(position);
        Assert.NotNull(start);
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPathDetailed(
            layout,
            start.Id,
            path,
            out PathfindingFailure? failure,
            options,
            AircraftCategory.Jet
        );
        Assert.Null(route);
        Assert.NotNull(failure);
        return failure;
    }

    private static bool Traverses(TaxiRoute route, string twy) =>
        route.Segments.Any(s =>
            s.TaxiwayName.Split([' ', '-', '/', ','], StringSplitOptions.RemoveEmptyEntries)
                .Any(tok => string.Equals(tok, twy, StringComparison.OrdinalIgnoreCase))
        );

    /// <summary>
    /// SFO's five/six/seven alleys carry sub-lanes named letter-digit-letter (<c>T5A</c>, <c>T6B</c>). They are ramp
    /// taxilanes just like their parent alley, so the name form has to accept a trailing letter group. Bare letters
    /// (<c>A</c>, <c>B</c>, <c>F</c>), runway connectors carrying a hold-short bar (<c>A1</c>, <c>GL</c>), runway
    /// centerlines and node references stay out.
    /// </summary>
    [Theory]
    [InlineData("M3", true)]
    [InlineData("M4", true)]
    [InlineData("M5", true)]
    [InlineData("T5", true)]
    [InlineData("T6", true)]
    [InlineData("T7", true)]
    [InlineData("T5A", true)]
    [InlineData("T5B", true)]
    [InlineData("T6A", true)]
    [InlineData("T6B", true)]
    [InlineData("T7A", true)]
    [InlineData("T7B", true)]
    [InlineData("A", false)]
    [InlineData("B", false)]
    [InlineData("F", false)]
    [InlineData("A1", false)]
    [InlineData("GL", false)]
    [InlineData("28L", false)]
    [InlineData("#12", false)]
    public void IsRampTaxilane_Sfo(string name, bool expected)
    {
        AirportGroundLayout? layout = Layout();
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
    [InlineData("W1", false)]
    [InlineData("W2", false)]
    [InlineData("W3", false)]
    [InlineData("W4", false)]
    [InlineData("W5", false)]
    [InlineData("W6", false)]
    [InlineData("W7", false)]
    [InlineData("B1", true)]
    [InlineData("B2", true)]
    public void IsRampTaxilane_Oak(string name, bool expected)
    {
        AirportGroundLayout? layout = OakLayout();
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
    public void AreSiblingLanes(string a, string b, bool expected) => Assert.Equal(expected, RampLaneReposition.AreSiblingLanes(a, b));

    [Fact]
    public void GateB20S_TaxiM4_CutsAcrossOntoM4ThenFollowsTheGraph()
    {
        AirportGroundLayout? layout = Layout();
        if (layout is null)
        {
            return;
        }

        GroundNode gate = layout.FindParkingByName("B20S")!;
        var path = new List<string> { "M4", "M1", "A", "H", "GL", "L", "LF", "F" };
        ExplicitPathOptions options = Options("28L");
        PathfindingFailure failure = FailureFor(layout, gate.Position, gate.TrueHeading!.Value, path, options)!;
        Assert.Equal(FailureKind.TaxiwayNotConnected, failure.Kind);

        RampLaneRepositionPlan? plan = RampLaneReposition.TryPlan(
            layout,
            new RampLaneRepositionRequest
            {
                Position = gate.Position,
                Heading = gate.TrueHeading!.Value,
                CurrentTaxiway = null,
                Path = path,
                Options = options,
                Category = AircraftCategory.Jet,
            },
            failure
        );
        Assert.NotNull(plan);
        _output.WriteLine($"plan: lane {plan.Lane} target #{plan.TargetNode.Id} crossing {plan.CrossingFt:F0} ft; {plan.Route.ToSummary()}");

        Assert.Equal("M4", plan.Lane);
        Assert.True(plan.CrossingFt <= RampLaneReposition.MaxCrossingFt, $"crossing {plan.CrossingFt:F0} ft exceeds the cap");
        Assert.True(plan.TargetNode.Edges.Any(e => e is GroundEdge && e.MatchesTaxiway("M4")), "target must sit on a straight M4 edge");

        TaxiRouteSegment first = plan.Route.Segments[0];
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
        AirportGroundLayout? layout = Layout();
        if (layout is null)
        {
            return;
        }

        GroundNode gate = layout.FindParkingByName("B20S")!;
        var path = new List<string> { "M5", "M1", "A", "A1" };
        ExplicitPathOptions options = Options("1R");
        PathfindingFailure failure = FailureFor(layout, gate.Position, gate.TrueHeading!.Value, path, options)!;

        RampLaneRepositionPlan? plan = RampLaneReposition.TryPlan(
            layout,
            new RampLaneRepositionRequest
            {
                Position = gate.Position,
                Heading = gate.TrueHeading!.Value,
                CurrentTaxiway = null,
                Path = path,
                Options = options,
                Category = AircraftCategory.Jet,
            },
            failure
        );
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
        AirportGroundLayout? layout = Layout();
        if (layout is null)
        {
            return;
        }

        // Node 469 is mid-M3 on the Terminal 1 ramp; M3 runs ~027°/207° here and M1 lies to the south-west.
        GroundNode onM3 = layout.Nodes[469];
        var heading = new TrueHeading(207);
        var path = new List<string> { "M4", "M1", "A", "A1" };
        ExplicitPathOptions options = Options("1R");
        // Mid-lane the resolver first tries a connector detour around the missing M4 leg, so it reports the
        // dead end as an unreachable destination rather than blaming M4 outright.
        PathfindingFailure failure = FailureFor(layout, onM3.Position, heading, path, options)!;

        RampLaneRepositionPlan? plan = RampLaneReposition.TryPlan(
            layout,
            new RampLaneRepositionRequest
            {
                Position = onM3.Position,
                Heading = heading,
                CurrentTaxiway = "M3",
                Path = path,
                Options = options,
                Category = AircraftCategory.Jet,
            },
            failure
        );
        Assert.NotNull(plan);
        _output.WriteLine($"plan: lane {plan.Lane} target #{plan.TargetNode.Id} crossing {plan.CrossingFt:F0} ft; {plan.Route.ToSummary()}");

        Assert.True(plan.CrossingFt <= 300, $"a mid-lane switch is a short cut across the alley, not {plan.CrossingFt:F0} ft");
        Assert.True(Traverses(plan.Route, "M1"), "route must continue onto M1");

        // The first on-lane segment must head south-west toward M1, not double back north-east.
        TaxiRouteSegment onLane = plan.Route.Segments[1];
        double bearing = GeoMath.BearingTo(onLane.Edge.FromNode.Position, onLane.Edge.ToNode.Position);
        Assert.True(GeoMath.AbsBearingDifference(bearing, 207) < 60, $"first M4 segment bears {bearing:F0}°, expected ~207° toward M1");
    }

    [Fact]
    public void Plan_RoundTripsThroughASnapshot()
    {
        AirportGroundLayout? layout = Layout();
        if (layout is null)
        {
            return;
        }

        GroundNode gate = layout.FindParkingByName("B20S")!;
        var path = new List<string> { "M4", "M1", "A", "A1" };
        ExplicitPathOptions options = Options("1R");
        PathfindingFailure failure = FailureFor(layout, gate.Position, gate.TrueHeading!.Value, path, options)!;
        RampLaneRepositionPlan plan = RampLaneReposition.TryPlan(
            layout,
            new RampLaneRepositionRequest
            {
                Position = gate.Position,
                Heading = gate.TrueHeading!.Value,
                CurrentTaxiway = null,
                Path = path,
                Options = options,
                Category = AircraftCategory.Jet,
            },
            failure
        )!;

        // The free-space leg's virtual start is not a layout node; restore rebuilds it from the recorded position.
        TaxiRouteDto dto = plan.Route.ToSnapshot();
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
        AirportGroundLayout? layout = Layout();
        if (layout is null)
        {
            return;
        }

        // M4 itself would be reachable by a cut, but the clearance dies further along: A never reaches 28L. The
        // resolver blames A, not the lane, so no cut is attempted and the controller gets the real error.
        GroundNode onM3 = layout.Nodes[469];
        var heading = new TrueHeading(207);
        var path = new List<string> { "M4", "M1", "A" };
        ExplicitPathOptions options = Options("28L");
        PathfindingFailure failure = FailureFor(layout, onM3.Position, heading, path, options)!;
        Assert.Equal(FailureKind.DestinationUnreachable, failure.Kind);
        Assert.Equal("A", failure.InfeasibleTaxiway);

        RampLaneRepositionPlan? plan = RampLaneReposition.TryPlan(
            layout,
            new RampLaneRepositionRequest
            {
                Position = onM3.Position,
                Heading = heading,
                CurrentTaxiway = "M3",
                Path = path,
                Options = options,
                Category = AircraftCategory.Jet,
            },
            failure
        );
        Assert.Null(plan);
    }

    [Fact]
    public void Gate4115_TaxiA_IsNotASiblingLane_NoPlan()
    {
        AirportGroundLayout? layout = Layout();
        if (layout is null)
        {
            return;
        }

        GroundNode gate = layout.FindParkingByName("41-15")!;
        var path = new List<string> { "A", "E" };
        ExplicitPathOptions options = Options("28R");
        PathfindingFailure failure = FailureFor(layout, gate.Position, gate.TrueHeading!.Value, path, options)!;

        RampLaneRepositionPlan? plan = RampLaneReposition.TryPlan(
            layout,
            new RampLaneRepositionRequest
            {
                Position = gate.Position,
                Heading = gate.TrueHeading!.Value,
                CurrentTaxiway = null,
                Path = path,
                Options = options,
                Category = AircraftCategory.Piston,
            },
            failure
        );
        Assert.Null(plan);
    }

    [Fact]
    public void GateB20S_TaxiM2_BeyondCrossingRange_NoPlan()
    {
        AirportGroundLayout? layout = Layout();
        if (layout is null)
        {
            return;
        }

        // M2 is a sibling lane but ~840 ft away across M4/M5 — far beyond a lane switch.
        GroundNode gate = layout.FindParkingByName("B20S")!;
        var path = new List<string> { "M2", "A" };
        ExplicitPathOptions options = Options("28L");
        PathfindingFailure failure = FailureFor(layout, gate.Position, gate.TrueHeading!.Value, path, options)!;

        RampLaneRepositionPlan? plan = RampLaneReposition.TryPlan(
            layout,
            new RampLaneRepositionRequest
            {
                Position = gate.Position,
                Heading = gate.TrueHeading!.Value,
                CurrentTaxiway = null,
                Path = path,
                Options = options,
                Category = AircraftCategory.Jet,
            },
            failure
        );
        Assert.Null(plan);
    }

    [Fact]
    public void ForeignTaxiwayBetweenTheLanes_NoPlan()
    {
        // Synthetic mini-airport: gate G leads onto lane M3; lane M9 runs parallel 200 ft away, but a lettered
        // taxiway K lies between them. The cut would cross K — a movement-area taxiway, not open apron.
        AirportGroundLayout layout = GeoJsonParser.Parse("TST", MiniRampGeoJson(withTaxiwayBetween: true), "TST");
        GroundNode gate = layout.FindParkingByName("G")!;
        var path = new List<string> { "M9" };
        ExplicitPathOptions options = Options(null);
        PathfindingFailure failure = FailureFor(layout, gate.Position, gate.TrueHeading!.Value, path, options)!;

        RampLaneRepositionPlan? plan = RampLaneReposition.TryPlan(
            layout,
            new RampLaneRepositionRequest
            {
                Position = gate.Position,
                Heading = gate.TrueHeading!.Value,
                CurrentTaxiway = null,
                Path = path,
                Options = options,
                Category = AircraftCategory.Jet,
            },
            failure
        );
        Assert.Null(plan);
    }

    [Fact]
    public void OpenApronBetweenTheLanes_Plans()
    {
        AirportGroundLayout layout = GeoJsonParser.Parse("TST", MiniRampGeoJson(withTaxiwayBetween: false), "TST");
        GroundNode gate = layout.FindParkingByName("G")!;
        var path = new List<string> { "M9" };
        ExplicitPathOptions options = Options(null);
        PathfindingFailure failure = FailureFor(layout, gate.Position, gate.TrueHeading!.Value, path, options)!;

        RampLaneRepositionPlan? plan = RampLaneReposition.TryPlan(
            layout,
            new RampLaneRepositionRequest
            {
                Position = gate.Position,
                Heading = gate.TrueHeading!.Value,
                CurrentTaxiway = null,
                Path = path,
                Options = options,
                Category = AircraftCategory.Jet,
            },
            failure
        );
        Assert.NotNull(plan);
        Assert.Equal("M9", plan.Lane);
    }

    /// <summary>
    /// The pressure test on the detour ratio a resolved route must beat before it is re-cut. The mini airport is a U:
    /// the only graph route from M3's north end to stand H on M9 runs the length of M3, across the K connector at the
    /// southern end and back up M9, so the lane length alone decides the ratio. Measured, not derived — the K
    /// connector lengthens the graph route and the straight drive is not a flat 400 ft, so read the two numbers as
    /// harness measurements: at a 100 ft lane the graph is 1.31× the drive and the pilot stays on the painted line;
    /// at 300 ft it is 2.29× and he crosses. The production cases sit at 1.10 and 3.75, so nothing else in the
    /// suite exercises the boundary itself.
    /// </summary>
    [Theory]
    [InlineData(100.0, false)]
    [InlineData(300.0, true)]
    public void ResolvedRouteCut_TurnsOnTheDetourRatio(double laneLengthFt, bool expectCut)
    {
        AirportGroundLayout layout = GeoJsonParser.Parse("TST", MiniLoopRampGeoJson(laneLengthFt), "TST");
        GroundNode stand = layout.FindParkingByName("H")!;
        GroundNode start = layout.GetNodesOnTaxiway("M3").OrderByDescending(n => n.Position.Lat).First();
        TaxiRoute? route = TaxiPathfinder.FindRoute(layout, start.Id, stand.Id, AircraftCategory.Jet);
        Assert.NotNull(route);

        double straightFt = GeoMath.DistanceNm(start.Position, stand.Position) * GeoMath.FeetPerNm;
        _output.WriteLine(
            $"graph route {route.TotalDistanceFt:F0} ft, straight drive {straightFt:F0} ft, ratio {route.TotalDistanceFt / straightFt:F2}"
        );
        _output.WriteLine("route: " + string.Join(" ", route.Segments.Select(s => $"{s.FromNodeId}-{s.ToNodeId}({s.TaxiwayName})")));

        RampLaneDestinationCutPlan? cut = RampLaneReposition.TryPlanResolvedRouteCut(layout, route, stand, AircraftLength.ResolveFt("B738"));
        if (!expectCut)
        {
            Assert.Null(cut);
            return;
        }

        Assert.NotNull(cut);
        _output.WriteLine($"cut from #{cut.FromNode.Id} across {cut.CrossingFt:F0} ft onto {cut.DestinationLane}: {cut.Route.TotalDistanceFt:F0} ft");
        Assert.Equal(start.Id, cut.FromNode.Id);
        Assert.Equal(stand.Id, cut.ToNode.Id);
        Assert.Equal("M9", cut.DestinationLane);
        Assert.True(cut.CrossingFt <= RampLaneReposition.MaxCrossingFt, $"crossing {cut.CrossingFt:F0} ft exceeds the cap");
        Assert.True(
            cut.Route.TotalDistanceFt < (route.TotalDistanceFt / 2.0),
            $"the cut route is {cut.Route.TotalDistanceFt:F0} ft against the graph's {route.TotalDistanceFt:F0} ft"
        );
    }

    /// <summary>
    /// Two parallel north–south lanes 150 ft apart (M3 at lon 0, M9 at lon +150 ft) that meet only at taxiway K
    /// across their southern ends, with stand H 250 ft east of M9 and abreast of the lanes' northern ends (the same
    /// 250 ft lead-out the sibling layout uses, long enough that its fillet stays clear of the gate). The lanes run
    /// 50 ft past K so the connector crosses them rather than touching their endpoints. Stand G sits 250 ft due north
    /// of M3's northern end so a gate hangs off M3 too, which makes it a ramp taxilane; its lead-in continues M3's line,
    /// so it adds no turn and leaves both the route start and the measured ratio as they were.
    /// </summary>
    private static string MiniLoopRampGeoJson(double laneLengthFt)
    {
        const double lat0 = 37.60;
        const double lon0 = -122.38;
        const double degPerFtLat = 1.0 / 364000.0;
        double degPerFtLon = degPerFtLat / Math.Cos(lat0 * Math.PI / 180.0);
        string Lon(double ft) => (lon0 + (ft * degPerFtLon)).ToString("F7", System.Globalization.CultureInfo.InvariantCulture);
        string Lat(double ft) => (lat0 + (ft * degPerFtLat)).ToString("F7", System.Globalization.CultureInfo.InvariantCulture);
        string Lane(string name, double lonFt) =>
            $$"""
                { "type": "Feature", "properties": { "type": "taxiway", "name": "{{name}}" },
                  "geometry": { "type": "LineString",
                    "coordinates": [[{{Lon(lonFt)}}, {{Lat(0)}}], [{{Lon(lonFt)}}, {{Lat(-(laneLengthFt + 50))}}]] } }
                """;
        return $$"""
            { "type": "FeatureCollection", "features": [
              { "type": "Feature", "properties": { "type": "parking", "name": "G", "heading": 360 },
                "geometry": { "type": "Point", "coordinates": [{{Lon(0)}}, {{Lat(250)}}] } },
              { "type": "Feature", "properties": { "type": "parking", "name": "H", "heading": 90 },
                "geometry": { "type": "Point", "coordinates": [{{Lon(400)}}, {{Lat(0)}}] } },
              {{Lane("M3", 0)}},
              {{Lane("M9", 150)}},
              { "type": "Feature", "properties": { "type": "taxiway", "name": "K" },
                "geometry": { "type": "LineString",
                  "coordinates": [[{{Lon(-50)}}, {{Lat(-laneLengthFt)}}], [{{Lon(200)}}, {{Lat(-laneLengthFt)}}]] } }
            ] }
            """;
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
