using System.Diagnostics;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Phases;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// Which hold-short bar a departure to a runway is sent to, on real layouts and nav data. A full-length entrance usually
/// has a bar on each side of the runway (SFO 28R's C: 52 ft and 53 ft along the runway from the threshold, 284 ft south
/// and 275 ft north of the centreline); the aircraft is sent to the bar on its own side, never across the runway it is
/// about to depart (AIM 4-3-18.a.6). With no own-side bar among the full-length ones, the own-side bar nearest the
/// threshold stands in as the departure point.
/// </summary>
public class LineupBarOrderTests(ITestOutputHelper output)
{
    /// <summary>Right of a 28 runway's heading (292° to 298° true): north of the runway at SFO and OAK.</summary>
    private const int North = 1;

    /// <summary>Left of a 28 runway's heading: south of the runway at SFO and OAK.</summary>
    private const int South = -1;

    /// <summary>Right of runway 33's heading (344° true): east of the runway at OAK.</summary>
    private const int East = 1;

    /// <summary>Left of runway 33's heading: west of the runway at OAK.</summary>
    private const int West = -1;

    /// <summary>The category the full-length pick models: a B738-class jet.</summary>
    private const AircraftCategory Jet = AircraftCategory.Jet;

    /// <summary>The wake class the full-length pick models.</summary>
    private const WakeTurbulenceData.WakeClass Large = WakeTurbulenceData.WakeClass.Large;

    private static AirportGroundLayout Layout(string airport)
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airport);
        Assert.NotNull(layout);
        return layout;
    }

    private static (LatLon Threshold, TrueHeading Heading) Axis(string airport, string runway)
    {
        RunwayInfo? info = NavigationDatabase.Instance.GetRunway(airport, runway);
        Assert.NotNull(info);
        var threshold = new LatLon(info.ThresholdLatitude, info.ThresholdLongitude);
        var far = new LatLon(info.EndLatitude, info.EndLongitude);
        return (threshold, new TrueHeading(GeoMath.BearingTo(threshold, far)));
    }

    private static double CrossFt(LatLon point, (LatLon Threshold, TrueHeading Heading) axis) =>
        GeoMath.SignedCrossTrackDistanceNm(point, axis.Threshold, axis.Heading) * GeoMath.FeetPerNm;

    private static double AlongFt(LatLon point, (LatLon Threshold, TrueHeading Heading) axis) =>
        GeoMath.AlongTrackDistanceNm(point, axis.Threshold, axis.Heading) * GeoMath.FeetPerNm;

    /// <summary>The bar of <paramref name="runway"/> on <paramref name="taxiway"/>, on <paramref name="side"/>, nearest the threshold.</summary>
    private static GroundNode Bar(AirportGroundLayout layout, string airport, string runway, string taxiway, int side)
    {
        (LatLon Threshold, TrueHeading Heading) axis = Axis(airport, runway);
        GroundNode? bar = layout
            .GetRunwayHoldShortNodes(runway)
            .Where(node => (node.Edges.Any(e => e.TaxiwayName == taxiway)) && (Math.Sign(CrossFt(node.Position, axis)) == side))
            .MinBy(node => AlongFt(node.Position, axis));
        Assert.NotNull(bar);
        return bar;
    }

    /// <summary>
    /// The node beside <paramref name="bar"/> farther from the centreline: where an aircraft taxiing up to that bar comes
    /// from, on the bar's side of the runway and between the runway's ends. Between two parallel runways it is the other
    /// runway's bar on the same taxiway.
    /// </summary>
    private static GroundNode Outboard(GroundNode bar, string airport, string runway)
    {
        (LatLon Threshold, TrueHeading Heading) axis = Axis(airport, runway);
        GroundNode? node = bar
            .Edges.Select(e => e.OtherNode(bar))
            .Where(n => !((n.RunwayId is { } id) && id.Contains(runway)))
            .MaxBy(n => Math.Abs(CrossFt(n.Position, axis)));
        Assert.NotNull(node);
        Assert.True(
            Math.Abs(CrossFt(node.Position, axis)) > Math.Abs(CrossFt(bar.Position, axis)),
            $"node {node.Id} is not outboard of bar {bar.Id}"
        );
        Assert.True(AlongFt(node.Position, axis) > 0, $"node {node.Id} lies before the {runway} threshold");
        return node;
    }

    [Fact]
    public void LineupBar_Sfo28R_C_StartNorth_PicksNorthBar()
    {
        AirportGroundLayout layout = Layout("SFO");
        GroundNode start = Outboard(Bar(layout, "SFO", "28R", "N", North), "SFO", "28R");

        GroundNode result = RouteMaterialiser.FindFullLengthLineupHoldShort(layout, start, "28R", layout.GetRunwayHoldShortNodes("28R"), Jet, Large);

        Assert.Equal(Bar(layout, "SFO", "28R", "C", North).Id, result.Id);
    }

    [Fact]
    public void LineupBar_Sfo28R_C_StartSouth_PicksSouthBar()
    {
        AirportGroundLayout layout = Layout("SFO");
        GroundNode start = Outboard(Bar(layout, "SFO", "28R", "N", South), "SFO", "28R");

        GroundNode result = RouteMaterialiser.FindFullLengthLineupHoldShort(layout, start, "28R", layout.GetRunwayHoldShortNodes("28R"), Jet, Large);

        Assert.Equal(Bar(layout, "SFO", "28R", "C", South).Id, result.Id);
    }

    /// <summary>
    /// OAK 33's C crosses the runway at a slant: its east bar is 435 ft before the threshold, its west bar 144 ft before,
    /// 291 ft apart along the runway, so only the east bar is full length. A start west of the runway is still sent to
    /// the west bar, the departure point on its own side.
    /// </summary>
    [Fact]
    public void LineupBar_Oak33_C_StartWest_PicksWestBarOverFarSideFullLengthBar()
    {
        AirportGroundLayout layout = Layout("OAK");
        GroundNode start = Outboard(Bar(layout, "OAK", "33", "L", West), "OAK", "33");

        GroundNode result = RouteMaterialiser.FindFullLengthLineupHoldShort(layout, start, "33", layout.GetRunwayHoldShortNodes("33"), Jet, Large);

        Assert.Equal(Bar(layout, "OAK", "33", "C", West).Id, result.Id);
        Assert.NotEqual(Bar(layout, "OAK", "33", "C", East).Id, result.Id);
    }

    /// <summary>
    /// TAXIAUTO to OAK 33 from east of the runway at K holds at C's east bar, the full-length bar on its side, rather
    /// than heading for C's west bar across the runway.
    /// </summary>
    [Fact]
    public void TaxiAuto_Oak33_StartEast_EndsAtCEastBar()
    {
        AirportGroundLayout layout = Layout("OAK");
        GroundNode start = Outboard(Bar(layout, "OAK", "33", "K", East), "OAK", "33");

        TaxiRoute? route = TaxiPathfinder.FindRunwayRoute(layout, start, "33", AircraftCategory.Jet, WakeTurbulenceData.WakeClass.Large);

        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);
        Assert.Equal(Bar(layout, "OAK", "33", "C", East).Id, route.Segments[^1].ToNodeId);
    }

    /// <summary>An air taxi to 28R from south of the runway ends at C's south bar, never across the runway.</summary>
    [Fact]
    public void AirTaxi_Sfo28R_StartSouth_PicksCSouthBar()
    {
        AirportGroundLayout layout = Layout("SFO");
        GroundNode start = Outboard(Bar(layout, "SFO", "28R", "N", South), "SFO", "28R");

        GroundNode? result = GroundCommandHandler.ResolveRunwayHoldShortNode(layout, new HoldShortTarget("28R", null, false), start.Position);

        Assert.NotNull(result);
        Assert.Equal(Bar(layout, "SFO", "28R", "C", South).Id, result.Id);
    }

    // ---------------------------------------------------------------------------
    // OAK 33: starts before the threshold, where the start has no side
    // ---------------------------------------------------------------------------

    /// <summary>A B738's length, for the explicit-path options.</summary>
    private const double B738LengthFt = 129.5;

    /// <summary>The point <paramref name="alongFt"/> along and <paramref name="crossFt"/> right of the runway axis.</summary>
    private static LatLon AxisPoint((LatLon Threshold, TrueHeading Heading) axis, double alongFt, double crossFt)
    {
        LatLon onAxis = GeoMath.ProjectPoint(axis.Threshold, axis.Heading, alongFt / GeoMath.FeetPerNm);
        double sideDeg = crossFt >= 0 ? 90.0 : 270.0;
        return GeoMath.ProjectPoint(onAxis, new TrueHeading((axis.Heading.Degrees + sideDeg) % 360.0), Math.Abs(crossFt) / GeoMath.FeetPerNm);
    }

    /// <summary>The node on <paramref name="taxiway"/> nearest the axis point, which must lie within <paramref name="maxFt"/> of it.</summary>
    private static GroundNode NodeOnTaxiwayNear(AirportGroundLayout layout, string taxiway, LatLon point, double maxFt)
    {
        GroundNode? node = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.TaxiwayName == taxiway))
            .MinBy(n => GeoMath.DistanceNm(n.Position, point));
        Assert.NotNull(node);
        Assert.True(GeoMath.DistanceNm(node.Position, point) * GeoMath.FeetPerNm <= maxFt, $"no node on {taxiway} within {maxFt} ft");
        return node;
    }

    /// <summary>
    /// The OAK 33 starts west of the runway before its threshold: on C about 100 ft before the threshold, outboard of C's west
    /// bar, and at that bar itself.
    /// </summary>
    private static GroundNode Oak33WestStart(AirportGroundLayout layout, string start)
    {
        GroundNode westBar = Bar(layout, "OAK", "33", "C", West);
        if (start == "bar")
        {
            return westBar;
        }

        (LatLon Threshold, TrueHeading Heading) axis = Axis("OAK", "33");
        GroundNode? node = layout
            .Nodes.Values.Where(n => (n.Type != GroundNodeType.RunwayHoldShort) && n.Edges.Any(e => e.TaxiwayName == "C"))
            .Where(n => CrossFt(n.Position, axis) < CrossFt(westBar.Position, axis))
            .MinBy(n => GeoMath.DistanceNm(n.Position, AxisPoint(axis, -100.0, CrossFt(westBar.Position, axis) - 60.0)));
        Assert.NotNull(node);
        double along = AlongFt(node.Position, axis);
        Assert.True((along < 0) && (along > -250), $"node {node.Id} on C is {along:F0} ft along 33, not about -100 ft");
        return node;
    }

    private static int FinalNodeId(TaxiRoute route, GroundNode start) => route.Segments.Count == 0 ? start.Id : route.Segments[^1].ToNodeId;

    [Theory]
    [InlineData("taxiway")]
    [InlineData("bar")]
    public void TaxiAuto_Oak33_StartOnCWestBeforeThreshold_EndsAtCWestBar(string startKind)
    {
        AirportGroundLayout layout = Layout("OAK");
        GroundNode start = Oak33WestStart(layout, startKind);

        TaxiRoute? route = TaxiPathfinder.FindRunwayRoute(layout, start, "33", AircraftCategory.Jet, WakeTurbulenceData.WakeClass.Large);

        Assert.NotNull(route);
        Assert.Equal(Bar(layout, "OAK", "33", "C", West).Id, FinalNodeId(route, start));
    }

    /// <summary>
    /// OAK's west C bar is 144 ft before the 33 threshold, so for runway 15 it lies past the far end: the start has no side
    /// and the shadow test does not apply. TAXIAUTO 15 and an air taxi to 15 head for a bar at the 15 end, never holding at
    /// the 33-end bar the start stands at.
    /// </summary>
    [Fact]
    public void Oak15_StartAtCWestBarPastFarEnd_PicksA15EndBar()
    {
        AirportGroundLayout layout = Layout("OAK");
        GroundNode start = Oak33WestStart(layout, "bar");
        RunwayInfo? runway15 = NavigationDatabase.Instance.GetRunway("OAK", "15");
        Assert.NotNull(runway15);
        HashSet<int> bars15 = [.. layout.GetRunwayHoldShortNodes("15").Select(b => b.Id)];

        TaxiRoute? route = TaxiPathfinder.FindRunwayRoute(layout, start, "15", AircraftCategory.Jet, WakeTurbulenceData.WakeClass.Large);
        GroundNode? airTaxi = GroundCommandHandler.ResolveRunwayHoldShortNode(layout, new HoldShortTarget("15", null, false), start.Position);

        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);
        int end = route.Segments[^1].ToNodeId;
        Assert.Contains(end, bars15);
        double endAlongFt = AlongFt(layout.Nodes[end].Position, Axis("OAK", "15"));
        Assert.True(endAlongFt < runway15.PavementLengthFt / 2, $"TAXIAUTO 15 ends at #{end}, {endAlongFt:F0} ft along 15");
        Assert.NotNull(airTaxi);
        Assert.NotEqual(start.Id, airTaxi.Id);
    }

    [Theory]
    [InlineData("taxiway")]
    public void TaxiC33_Oak_StartOnCWestBeforeThreshold_EndsAtCWestBar(string startKind) => AssertTaxiC33EndsAtCWestBar(startKind);

    [Fact(Skip = "YAAT-1287: TAXI C 33 from OAK's west C bar walks past the runway end (explicit-path resolver; red on the base too)")]
    public void TaxiC33_Oak_StartAtCWestBar_EndsAtCWestBar() => AssertTaxiC33EndsAtCWestBar("bar");

    private static void AssertTaxiC33EndsAtCWestBar(string startKind)
    {
        AirportGroundLayout layout = Layout("OAK");
        GroundNode start = Oak33WestStart(layout, startKind);
        var options = new ExplicitPathOptions
        {
            StartPose = null,
            OccupiedTaxiway = null,
            ImpliedLeadTaxiway = null,
            AircraftLengthFt = B738LengthFt,
            DestinationRunway = "33",
        };

        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["C"],
            out string? failReason,
            options,
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );

        Assert.True(route is not null, failReason);
        Assert.Equal(Bar(layout, "OAK", "33", "C", West).Id, FinalNodeId(route, start));
    }

    [Theory]
    [InlineData("taxiway")]
    [InlineData("bar")]
    public void AirTaxi_Oak33_StartOnCWestBeforeThreshold_PicksCWestBar(string startKind)
    {
        AirportGroundLayout layout = Layout("OAK");
        GroundNode start = Oak33WestStart(layout, startKind);

        GroundNode? result = GroundCommandHandler.ResolveRunwayHoldShortNode(layout, new HoldShortTarget("33", null, false), start.Position);

        Assert.NotNull(result);
        Assert.Equal(Bar(layout, "OAK", "33", "C", West).Id, result.Id);
    }

    /// <summary>
    /// From the south end of K, 111 ft before the 33 threshold and 1,680 ft west of the runway, the start has no side, and
    /// the full-length pick is C's east bar, the bar nearest the threshold along the runway.
    /// </summary>
    [Fact]
    public void LineupBar_Oak33_StartAtSouthEndOfK_PicksCEastBar()
    {
        AirportGroundLayout layout = Layout("OAK");
        GroundNode start = NodeOnTaxiwayNear(layout, "K", AxisPoint(Axis("OAK", "33"), -111.0, -1680.0), 150.0);

        GroundNode lineup = RouteMaterialiser.FindFullLengthLineupHoldShort(layout, start, "33", layout.GetRunwayHoldShortNodes("33"), Jet, Large);
        GroundNode? airTaxi = GroundCommandHandler.ResolveRunwayHoldShortNode(layout, new HoldShortTarget("33", null, false), start.Position);

        int eastBar = Bar(layout, "OAK", "33", "C", East).Id;
        Assert.Equal(eastBar, lineup.Id);
        Assert.Equal(eastBar, airTaxi?.Id);
    }

    // ---------------------------------------------------------------------------
    // OrderFullLengthLineupBars branches, on SFO 28R's real bars
    // ---------------------------------------------------------------------------

    private static List<int> Ids(List<GroundNode>? bars)
    {
        Assert.NotNull(bars);
        return [.. bars.Select(b => b.Id)];
    }

    /// <summary>SFO 28R's bars by along-track, then node id: the order with no start side.</summary>
    private static List<int> Sfo28RByAlong(List<GroundNode> bars)
    {
        (LatLon Threshold, TrueHeading Heading) axis = Axis("SFO", "28R");
        return [.. bars.OrderBy(b => AlongFt(b.Position, axis)).ThenBy(b => b.Id).Select(b => b.Id)];
    }

    [Theory]
    [InlineData(3000.0, 0.0)] // on the runway
    [InlineData(3000.0, 140.0)] // within half the 200 ft width plus 50 ft of the centreline
    [InlineData(-500.0, -500.0)] // before the threshold
    [InlineData(12353.0, -500.0)] // past the far end (pavement 11,853 ft)
    public void Order_Sfo28R_StartWithNoSide_IsAlongTrackThenNodeId(double alongFt, double crossFt)
    {
        AirportGroundLayout layout = Layout("SFO");
        List<GroundNode> bars = layout.GetRunwayHoldShortNodes("28R");
        LatLon start = AxisPoint(Axis("SFO", "28R"), alongFt, crossFt);

        List<GroundNode>? ordered = RouteMaterialiser.OrderFullLengthLineupBars("SFO", "28R", start, bars, null);

        Assert.Equal(Sfo28RByAlong(bars), Ids(ordered));
    }

    [Fact]
    public void Order_Sfo28R_NullStart_IsAlongTrackThenNodeId()
    {
        AirportGroundLayout layout = Layout("SFO");
        List<GroundNode> bars = layout.GetRunwayHoldShortNodes("28R");

        List<GroundNode>? ordered = RouteMaterialiser.OrderFullLengthLineupBars("SFO", "28R", null, bars, null);

        Assert.Equal(Sfo28RByAlong(bars), Ids(ordered));
    }

    [Fact]
    public void Order_Sfo28R_NoCandidateOnStartSide_IsDistanceFromThresholdThenNodeId()
    {
        AirportGroundLayout layout = Layout("SFO");
        (LatLon Threshold, TrueHeading Heading) axis = Axis("SFO", "28R");
        List<GroundNode> northBars = [.. layout.GetRunwayHoldShortNodes("28R").Where(b => CrossFt(b.Position, axis) > 0)];

        List<GroundNode>? ordered = RouteMaterialiser.OrderFullLengthLineupBars("SFO", "28R", AxisPoint(axis, 3000.0, -500.0), northBars, null);

        List<int> expected = [.. northBars.OrderBy(b => GeoMath.DistanceNm(axis.Threshold, b.Position)).ThenBy(b => b.Id).Select(b => b.Id)];
        Assert.Equal(expected, Ids(ordered));
    }

    /// <summary>
    /// The order TAXIAUTO tries 28R's bars in from south of the runway: C's south bar, then the rest of the full-length band
    /// (C's north bar), then every other bar by along-track, then node id.
    /// </summary>
    [Fact]
    public void Order_Sfo28R_StartSouth_IsOwnSideBandThenBandThenRestByAlongTrack()
    {
        AirportGroundLayout layout = Layout("SFO");
        List<GroundNode> bars = layout.GetRunwayHoldShortNodes("28R");
        int southC = Bar(layout, "SFO", "28R", "C", South).Id;
        int northC = Bar(layout, "SFO", "28R", "C", North).Id;

        List<GroundNode>? ordered = RouteMaterialiser.OrderFullLengthLineupBars(
            "SFO",
            "28R",
            AxisPoint(Axis("SFO", "28R"), 3000.0, -500.0),
            bars,
            null
        );

        List<int> rest = [.. Sfo28RByAlong(bars).Where(id => (id != southC) && (id != northC))];
        Assert.Equal([southC, northC, .. rest], Ids(ordered));
    }

    // ---------------------------------------------------------------------------
    // Cost of the shadow test on the Air taxi flyout's path
    // ---------------------------------------------------------------------------

    /// <summary>OAK's runway ends, every one the Air taxi flyout lists a row for.</summary>
    private static readonly string[] OakRunwayEnds = ["10L", "28R", "10R", "28L", "12", "30", "15", "33"];

    /// <summary>A helicopter on <paramref name="end"/>'s centreline 1,000 ft past its threshold: no side, so the shadow test applies.</summary>
    private static LatLon NoSideStart(string end) => AxisPoint(Axis("OAK", end), 1000.0, 0.0);

    /// <summary>
    /// The flyout's refill: ten passes of the bare air taxi resolution for every OAK runway end from a helicopter with no
    /// side of that runway, on a layout no earlier test has warmed. Reports the total time; asserts only that each end
    /// resolves.
    /// </summary>
    [Fact]
    public void AirTaxi_Oak_FlyoutLikeLoop_ReportsCost()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = TestAirportGroundData.LoadUncached("OAK");
        Assert.NotNull(layout);
        List<(string End, LatLon Start)> rows = [.. OakRunwayEnds.Select(end => (end, NoSideStart(end)))];

        var watch = Stopwatch.StartNew();
        for (int pass = 0; pass < 10; pass++)
        {
            foreach ((string end, LatLon start) in rows)
            {
                Assert.NotNull(GroundCommandHandler.ResolveRunwayHoldShortNode(layout, new HoldShortTarget(end, null, false), start));
            }
        }

        output.WriteLine($"10 passes x {rows.Count} OAK runway ends: {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// A second identical air taxi resolution reads every bar's shadow result from the per-layout cache and runs no taxi
    /// route search, on a layout no earlier test has warmed.
    /// </summary>
    [Fact]
    public void AirTaxi_Oak33_SecondIdenticalCall_RunsNoRouteSearch()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = TestAirportGroundData.LoadUncached("OAK");
        Assert.NotNull(layout);
        var target = new HoldShortTarget("33", null, false);
        LatLon start = NoSideStart("33");

        int before = TaxiPathfinder.LineupBarShadowCache.RouteSearchesOnThisThread;
        GroundNode? first = GroundCommandHandler.ResolveRunwayHoldShortNode(layout, target, start);
        int afterFirst = TaxiPathfinder.LineupBarShadowCache.RouteSearchesOnThisThread;
        GroundNode? second = GroundCommandHandler.ResolveRunwayHoldShortNode(layout, target, start);
        int afterSecond = TaxiPathfinder.LineupBarShadowCache.RouteSearchesOnThisThread;

        Assert.NotNull(first);
        Assert.True(afterFirst > before, "the first call ran no route search, so the shadow test never ran");
        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(first.Id, second?.Id);
    }

    /// <summary>
    /// A bar the aircraft cannot taxi to ranks with the shadowed bars for a ground taxi, after every reachable bar, and as
    /// clear for an air taxi. No real OAK or SFO case of a first-ranked bar unreachable for a category is known, so a
    /// delegate stands in for reachability: from before the SFO 28R threshold, the first bar by along-track is unreachable.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Order_Sfo28R_StartBeforeThreshold_UnreachableFirstBar_RanksLastOnlyForGround(bool unreachableIsShadowed)
    {
        AirportGroundLayout layout = Layout("SFO");
        List<GroundNode> bars = layout.GetRunwayHoldShortNodes("28R");
        List<int> byAlong = Sfo28RByAlong(bars);
        int unreachable = byAlong[0];

        TaxiPathfinder.LineupBarReach Reach(GroundNode bar) =>
            bar.Id == unreachable ? TaxiPathfinder.LineupBarReach.Unreachable : TaxiPathfinder.LineupBarReach.Clear;
        List<GroundNode>? ordered = RouteMaterialiser.OrderFullLengthLineupBars(
            "SFO",
            "28R",
            AxisPoint(Axis("SFO", "28R"), -500.0, -500.0),
            bars,
            () => bar => TaxiPathfinder.RanksAsShadowed(Reach(bar), unreachableIsShadowed)
        );

        List<int> expected = unreachableIsShadowed ? [.. byAlong.Skip(1), unreachable] : byAlong;
        Assert.Equal(expected, Ids(ordered));
    }
}
