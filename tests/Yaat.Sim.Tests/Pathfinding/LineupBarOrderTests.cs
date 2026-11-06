using System.Diagnostics;
using System.Globalization;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
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

        GroundNode result = RouteMaterialiser.FindFullLengthLineupHoldShort(
            layout,
            start,
            "28R",
            layout.GetRunwayHoldShortNodes("28R"),
            new TaxiClass(Jet, Large)
        );

        Assert.Equal(Bar(layout, "SFO", "28R", "C", North).Id, result.Id);
    }

    [Fact]
    public void LineupBar_Sfo28R_C_StartSouth_PicksSouthBar()
    {
        AirportGroundLayout layout = Layout("SFO");
        GroundNode start = Outboard(Bar(layout, "SFO", "28R", "N", South), "SFO", "28R");

        GroundNode result = RouteMaterialiser.FindFullLengthLineupHoldShort(
            layout,
            start,
            "28R",
            layout.GetRunwayHoldShortNodes("28R"),
            new TaxiClass(Jet, Large)
        );

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

        GroundNode result = RouteMaterialiser.FindFullLengthLineupHoldShort(
            layout,
            start,
            "33",
            layout.GetRunwayHoldShortNodes("33"),
            new TaxiClass(Jet, Large)
        );

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

    /// <summary>
    /// A search to OAK 33 that resumes mid-route judges the bar pick from its head, not from where the taxi began. The taxi
    /// began on C west of the runway, whose pick is C's west bar; the head has reached C's east bar, the full-length bar on
    /// its own side, so the search has nothing left to taxi and costs nothing, rather than searching across the runway to the
    /// west bar (a cost the caller would score this tail by).
    /// </summary>
    [Fact]
    public void AutoRouter_Oak33_ResumedAtCEastBar_ForATaxiBegunWest_HoldsThere()
    {
        AirportGroundLayout layout = Layout("OAK");
        List<GroundNode> bars = layout.GetRunwayHoldShortNodes("33");
        GroundNode originalStart = Oak33WestStart(layout, "taxiway");
        GroundNode head = Bar(layout, "OAK", "33", "C", East);
        Assert.Equal(head.Id, RouteMaterialiser.FindFullLengthLineupHoldShort(layout, head, "33", bars, new TaxiClass(Jet, Large)).Id);
        Assert.Equal(
            Bar(layout, "OAK", "33", "C", West).Id,
            RouteMaterialiser.FindFullLengthLineupHoldShort(layout, originalStart, "33", bars, new TaxiClass(Jet, Large)).Id
        );
        var ctx = SearchContext.Compile(
            layout,
            originalStart.Id,
            waypointSequence: [],
            destinationRunway: "33",
            destinationParking: null,
            destinationSpot: null,
            destinationNodeId: null,
            explicitHoldShorts: null,
            category: Jet,
            wakeClass: Large,
            preference: RoutePreference.FewestTurns,
            diagnosticLog: null,
            waypointTurnHints: null,
            startHeadingTrue: null,
            startPose: null,
            aircraftLengthFt: null
        );

        (TaxiRoute? route, PathfindingFailure? failure, double cost) = AutoRouter.RunWithCost(
            ctx,
            PartialRoute.StartAt(head.Id),
            maxExpansions: 200_000
        );

        Assert.True(route is not null, failure?.HumanMessage);
        Assert.True(route.Segments.Count == 0, $"the resumed search taxis on to #{route.Segments.LastOrDefault()?.ToNodeId}");
        Assert.True(cost == 0.0, $"the resumed search costs {cost:F3}: it searched for a route to another bar");
    }

    /// <summary>
    /// A resumed search whose head node is not on the layout fails naming that head, rather than silently searching from
    /// the aircraft's original start as if the head had been found.
    /// </summary>
    [Fact]
    public void AutoRouter_ResumedRouteHeadMissingFromLayout_FailsNamingTheHead()
    {
        AirportGroundLayout layout = Layout("OAK");
        GroundNode start = Oak33WestStart(layout, "taxiway");
        var ctx = SearchContext.Compile(
            layout,
            start.Id,
            waypointSequence: [],
            destinationRunway: "33",
            destinationParking: null,
            destinationSpot: null,
            destinationNodeId: null,
            explicitHoldShorts: null,
            category: Jet,
            wakeClass: Large,
            preference: RoutePreference.FewestTurns,
            diagnosticLog: null,
            waypointTurnHints: null,
            startHeadingTrue: null,
            startPose: null,
            aircraftLengthFt: null
        );
        int missingHead = layout.Nodes.Keys.Max() + 1;

        (TaxiRoute? route, PathfindingFailure? failure, _) = AutoRouter.RunWithCost(ctx, PartialRoute.StartAt(missingHead), maxExpansions: 200_000);

        Assert.Null(route);
        Assert.NotNull(failure);
        Assert.Equal(FailureKind.StartNodeUnreachable, failure.Kind);
        Assert.Contains(missingHead.ToString(CultureInfo.InvariantCulture), failure.HumanMessage, StringComparison.Ordinal);
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
    // OAK 33: starts near the threshold
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
    /// The OAK 33 starts west of the runway near its threshold: C's west bar itself, which lies before the threshold, and the
    /// first C node outboard of that bar. With the bar seated on its painted marking, that outboard node lies just past the
    /// threshold (about 80 ft along 33); the bar is still the one the start reaches C's west side by.
    /// </summary>
    private static GroundNode Oak33WestStart(AirportGroundLayout layout, string start)
    {
        GroundNode westBar = Bar(layout, "OAK", "33", "C", West);
        if (start == "bar")
        {
            return westBar;
        }

        GroundNode node = Outboard(westBar, "OAK", "33");
        Assert.NotEqual(GroundNodeType.RunwayHoldShort, node.Type);
        Assert.Contains(node.Edges, e => e.TaxiwayName == "C");
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

    [Fact]
    public void TaxiC33_Oak_StartAtCWestBar_EndsAtCWestBar() => AssertTaxiC33EndsAtCWestBar("bar");

    /// <summary>
    /// A B738 whose main gear sits at <paramref name="mainGear"/>, facing <paramref name="headingDeg"/> on
    /// <paramref name="taxiway"/>, in a holding-in-position phase, and the engine that drives it.
    /// </summary>
    private static (SimulationEngine Engine, AircraftState Aircraft) StageOnTaxiway(LatLon mainGear, double headingDeg, string taxiway)
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout("OAK");
        Assert.NotNull(layout);

        var aircraft = new AircraftState
        {
            Callsign = "N1287",
            AircraftType = "B738",
            Position = mainGear,
            TrueHeading = new TrueHeading(headingDeg),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = taxiway;

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-yaat-1287-hold-at-start-bar",
                ScenarioName = "OAK C west bar, TAXI C 33",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        engine.World.AddAircraft(aircraft);

        return (engine, aircraft);
    }

    /// <summary>
    /// A B738 on OAK's C west of runway 33, facing the runway, with its nose <paramref name="nosePastBarFt"/> ft past the
    /// west C bar (<c>0</c> puts the nose at the bar), in a holding-in-position phase, and the engine that drives it. The main
    /// gear sits half a B738 length back from the nose along the taxiway.
    /// </summary>
    private static (SimulationEngine Engine, AircraftState Aircraft) StageAtCWestBar(double nosePastBarFt, out GroundNode westBar)
    {
        AirportGroundLayout layout = Layout("OAK");
        westBar = Bar(layout, "OAK", "33", "C", West);
        GroundNode outboard = Outboard(westBar, "OAK", "33");
        double towardRunwayDeg = GeoMath.BearingTo(outboard.Position, westBar.Position);
        double halfLengthFt = AircraftLength.ResolveFt("B738") / 2;
        LatLon mainGear = GeoMath.ProjectPoint(
            westBar.Position,
            new TrueHeading((towardRunwayDeg + 180.0) % 360.0),
            (halfLengthFt - nosePastBarFt) / GeoMath.FeetPerNm
        );

        return StageOnTaxiway(mainGear, towardRunwayDeg, "C");
    }

    /// <summary>
    /// A B738 holding at OAK's west C bar, nose at the bar and facing the runway, is given <c>TAXI C 33</c>: it holds short of
    /// 33 where it stands, with nothing to taxi, and does not move, rather than crossing the runway's end to C's east bar.
    /// </summary>
    [Fact]
    public void TaxiC33_Oak_HoldingAtCWestBar_HoldsThereAndDoesNotMove()
    {
        (SimulationEngine engine, AircraftState aircraft) = StageAtCWestBar(0.0, out GroundNode westBar);
        LatLon mainGear = aircraft.Position;

        CommandResult taxi = engine.SendCommand("N1287", "TAXI C 33");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Empty(route.Segments);
        HoldShortPoint hold = Assert.Single(route.HoldShortPoints);
        Assert.Equal(westBar.Id, hold.NodeId);
        Assert.Equal(HoldShortReason.DestinationRunway, hold.Reason);

        for (int second = 0; second < 30; second++)
        {
            engine.TickOneSecond();
        }

        double movedFt = GeoMath.DistanceNm(mainGear, aircraft.Position) * GeoMath.FeetPerNm;
        Assert.True(movedFt < 1.0, $"moved {movedFt:F1} ft from the west C bar");
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(westBar.Id, holding.HoldShort.NodeId);
    }

    /// <summary>
    /// A B738 whose nose is already 10 ft past OAK's west C bar, facing runway 33, is given <c>TAXI C 33</c>: it is already at
    /// its runway, so it holds short of 33 where it stands, clear of the pavement, and the controller is told the nose is past
    /// the line — rather than taxiing on along C across the runway's end to C's east bar.
    /// </summary>
    [Fact]
    public void TaxiC33_Oak_NosePastCWestBar_HoldsWhereItStands()
    {
        (SimulationEngine engine, AircraftState aircraft) = StageAtCWestBar(10.0, out GroundNode westBar);
        LatLon start = aircraft.Position;

        CommandResult taxi = engine.SendCommand("N1287", "TAXI C 33");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Empty(route.Segments);
        HoldShortPoint hold = Assert.Single(route.HoldShortPoints);
        Assert.Equal(westBar.Id, hold.NodeId);
        Assert.Equal(HoldShortReason.DestinationRunway, hold.Reason);
        Assert.Contains("already past the 33 hold-short line — holding in place, clear of the runway", route.Warnings);
        Assert.Contains("holding in place", taxi.Message);
        Assert.DoesNotContain("holding short", taxi.Message);

        for (int second = 0; second < 30; second++)
        {
            engine.TickOneSecond();
        }

        double movedFt = GeoMath.DistanceNm(start, aircraft.Position) * GeoMath.FeetPerNm;
        Assert.True(movedFt < 1.0, $"moved {movedFt:F1} ft from the west C bar");
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(westBar.Id, holding.HoldShort.NodeId);
    }

    /// <summary>
    /// A B738 at OAK's west C bar is given <c>TAXI C D 33</c>: the aircraft is already holding short of its runway where it
    /// stands, so the command succeeds with a note naming the taxiway it did not taxi — never a message denying where the
    /// aircraft is.
    /// </summary>
    [Fact]
    public void TaxiCD33_Oak_HoldingAtCWestBar_HoldsThereWithANote()
    {
        (SimulationEngine engine, AircraftState aircraft) = StageAtCWestBar(0.0, out GroundNode westBar);

        CommandResult taxi = engine.SendCommand("N1287", "TAXI C D 33");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Empty(route.Segments);
        HoldShortPoint hold = Assert.Single(route.HoldShortPoints);
        Assert.Equal(westBar.Id, hold.NodeId);
        Assert.Equal(HoldShortReason.DestinationRunway, hold.Reason);
        Assert.Contains("D not taxied — already holding short of runway 33", route.Warnings);
        Assert.DoesNotContain(route.Warnings, w => w.StartsWith("already past", StringComparison.Ordinal));
        Assert.DoesNotContain("unreachable", taxi.Message);
    }

    /// <summary>
    /// A B738 at OAK's west C bar is given <c>TAXI C 33 HS C</c>: it is already on C and holding short of 33 where it stands,
    /// so the note says the hold is already in effect rather than denying the route ever reaches C.
    /// </summary>
    [Fact]
    public void TaxiC33HsC_Oak_HoldingAtCWestBar_DoesNotSayItNeverReachesC()
    {
        (SimulationEngine engine, AircraftState aircraft) = StageAtCWestBar(0.0, out _);

        CommandResult taxi = engine.SendCommand("N1287", "TAXI C 33 HS C");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Empty(route.Segments);
        Assert.DoesNotContain(route.Warnings, w => w.Contains("never reaches C", StringComparison.Ordinal));
        Assert.Contains("HS C not applied — already on C, holding short of runway 33", route.Warnings);
    }

    /// <summary>
    /// How far past OAK's west C bar, along the bar-to-runway bearing, a <paramref name="aircraftType"/>'s main gear sits with
    /// its nose on runway 33's pavement and the gear itself off it: the shortest such distance over the whole bearing. NaN when
    /// no such pose exists.
    /// </summary>
    private static double GearFtWithNoseOnPavement(AirportGroundLayout layout, GroundNode westBar, double towardRunwayDeg, string aircraftType)
    {
        var heading = new TrueHeading(towardRunwayDeg);
        double halfLengthNm = AircraftLength.ResolveFt(aircraftType) / 2 / GeoMath.FeetPerNm;
        for (double gearFt = 0; gearFt <= 500.0; gearFt += 0.5)
        {
            LatLon gear = GeoMath.ProjectPoint(westBar.Position, heading, gearFt / GeoMath.FeetPerNm);
            LatLon nose = GeoMath.ProjectPoint(gear, heading, halfLengthNm);
            if (!TaxiPathfinder.IsOnRunwayPavement(layout, gear, "33") && TaxiPathfinder.IsOnRunwayPavement(layout, nose, "33"))
            {
                return gearFt;
            }
        }

        return double.NaN;
    }

    /// <summary>
    /// The first distance past <paramref name="westBar"/>, along the bar-to-runway bearing and beyond
    /// <paramref name="minFt"/>, at which every part of a <paramref name="aircraftType"/> is off runway 33's pavement. NaN when
    /// no such pose exists.
    /// </summary>
    private static double GearFtWithAllPartsOffPavement(
        AirportGroundLayout layout,
        GroundNode westBar,
        double towardRunwayDeg,
        string aircraftType,
        double minFt
    )
    {
        for (double gearFt = minFt; gearFt <= 500.0; gearFt += 0.5)
        {
            LatLon gear = GeoMath.ProjectPoint(westBar.Position, new TrueHeading(towardRunwayDeg), gearFt / GeoMath.FeetPerNm);
            bool allOffPavement = StartPose
                .Of(gear, towardRunwayDeg, aircraftType)
                .PartPositions()
                .All(p => !TaxiPathfinder.IsOnRunwayPavement(layout, p, "33"));
            if (allOffPavement)
            {
                return gearFt;
            }
        }

        return double.NaN;
    }

    /// <summary>
    /// A B738 holding with its nose 10 ft past OAK's west C bar, every part clear of 33's pavement and the main gear at the bar,
    /// still holds there: the main gear is inside the bar's radius and no part of the aircraft is on the runway.
    /// </summary>
    [Fact]
    public void HoldsAtPassedBar_NosePastBarGearOffPavement_Holds()
    {
        AirportGroundLayout layout = Layout("OAK");
        GroundNode westBar = Bar(layout, "OAK", "33", "C", West);
        GroundNode outboard = Outboard(westBar, "OAK", "33");
        double towardRunwayDeg = GeoMath.BearingTo(outboard.Position, westBar.Position);
        double halfLengthFt = AircraftLength.ResolveFt("B738") / 2;
        LatLon mainGear = GeoMath.ProjectPoint(
            westBar.Position,
            new TrueHeading((towardRunwayDeg + 180.0) % 360.0),
            (halfLengthFt - 10.0) / GeoMath.FeetPerNm
        );

        var pose = StartPose.Of(mainGear, towardRunwayDeg, "B738");
        Assert.True(
            GeoMath.DistanceNm(pose.MainGear, westBar.Position) * GeoMath.FeetPerNm <= TaxiPathfinder.AtRunwayHoldShortRadiusFt,
            "the B738's main gear is outside the bar's hold radius"
        );
        Assert.True(
            pose.PartPositions().All(p => !TaxiPathfinder.IsOnRunwayPavement(layout, p, "33")),
            "a part of the B738 is on the 33 pavement at the bar"
        );
        Assert.True(RouteMaterialiser.HoldsAtPassedBar(layout, westBar, pose, "33"));
    }

    /// <summary>
    /// A B744 whose nose reaches runway 33's pavement from OAK's west C bar — its main gear still off it and inside the bar's
    /// hold radius — does not hold there: an aircraft is clear of the runway only when every part is past its edge (AIM
    /// 4-3-20.a.2), so the past-the-line note's "clear of the runway" would be a lie.
    /// </summary>
    [Fact]
    public void HoldsAtPassedBar_NoseOverTheRunwayPavement_DoesNotHold()
    {
        AirportGroundLayout layout = Layout("OAK");
        GroundNode westBar = Bar(layout, "OAK", "33", "C", West);
        GroundNode outboard = Outboard(westBar, "OAK", "33");
        double towardRunwayDeg = GeoMath.BearingTo(outboard.Position, westBar.Position);
        double gearFt = GearFtWithNoseOnPavement(layout, westBar, towardRunwayDeg, "B744");
        Assert.False(double.IsNaN(gearFt), "no B744 pose has its nose on the 33 pavement with its main gear off it");
        Assert.True(
            gearFt <= TaxiPathfinder.AtRunwayHoldShortRadiusFt,
            $"the closest such pose puts the B744's main gear {gearFt:F0} ft from the bar, past the "
                + $"{TaxiPathfinder.AtRunwayHoldShortRadiusFt:F0} ft hold radius"
        );

        LatLon mainGear = GeoMath.ProjectPoint(westBar.Position, new TrueHeading(towardRunwayDeg), gearFt / GeoMath.FeetPerNm);
        var pose = StartPose.Of(mainGear, towardRunwayDeg, "B744");
        Assert.False(TaxiPathfinder.IsOnRunwayPavement(layout, pose.MainGear, "33"), $"the main gear at {gearFt:F0} ft is on the 33 pavement");
        Assert.True(
            pose.PartPositions().Any(p => TaxiPathfinder.IsOnRunwayPavement(layout, p, "33")),
            $"no part of the B744 whose gear is {gearFt:F0} ft from the bar is on the 33 pavement"
        );
        Assert.False(RouteMaterialiser.HoldsAtPassedBar(layout, westBar, pose, "33"));
    }

    /// <summary>
    /// A B738 whose main gear is more than 100 ft past OAK's west C bar with every part clear of 33's pavement is beyond the
    /// bar's hold radius, so the bar does not hold it in place. Skips when this bar gives no such pose.
    /// </summary>
    [Fact]
    public void HoldsAtPassedBar_GearBeyondTheRadius_DoesNotHold()
    {
        AirportGroundLayout layout = Layout("OAK");
        GroundNode westBar = Bar(layout, "OAK", "33", "C", West);
        GroundNode outboard = Outboard(westBar, "OAK", "33");
        double towardRunwayDeg = GeoMath.BearingTo(outboard.Position, westBar.Position);
        double gearFt = GearFtWithAllPartsOffPavement(layout, westBar, towardRunwayDeg, "B738", TaxiPathfinder.AtRunwayHoldShortRadiusFt + 0.5);
        if (double.IsNaN(gearFt))
        {
            return;
        }

        LatLon mainGear = GeoMath.ProjectPoint(westBar.Position, new TrueHeading(towardRunwayDeg), gearFt / GeoMath.FeetPerNm);
        var pose = StartPose.Of(mainGear, towardRunwayDeg, "B738");
        Assert.True(
            GeoMath.DistanceNm(pose.MainGear, westBar.Position) * GeoMath.FeetPerNm > TaxiPathfinder.AtRunwayHoldShortRadiusFt,
            $"the B738's main gear at {gearFt:F0} ft is inside the bar's hold radius"
        );
        Assert.False(RouteMaterialiser.HoldsAtPassedBar(layout, westBar, pose, "33"));
    }

    /// <summary>
    /// An aircraft standing on runway 33's centerline where C crosses it is not holding at the destination runway's bar:
    /// <c>TAXI C D 33</c> keeps its refusal of the unreachable D, never the "already holding short" note.
    /// </summary>
    [Fact]
    public void TaxiCD33_Oak_StandingOnRunway33_StillRefusesD()
    {
        AirportGroundLayout layout = Layout("OAK");
        GroundNode westBar = Bar(layout, "OAK", "33", "C", West);
        GroundNode? crossing = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.IsRunwayCenterline && e.MatchesRunway("33")))
            .Where(n => n.Edges.Any(e => e.TaxiwayName == "C"))
            .MinBy(n => GeoMath.DistanceNm(n.Position, westBar.Position));
        Assert.NotNull(crossing);
        GroundNode? onRunway = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.IsRunwayCenterline && e.MatchesRunway("33")))
            .Where(n => !n.Edges.Any(e => e.TaxiwayName == "C"))
            .Where(n => GeoMath.DistanceNm(n.Position, crossing.Position) * GeoMath.FeetPerNm <= 300)
            .MinBy(n => GeoMath.DistanceNm(n.Position, westBar.Position));
        Assert.NotNull(onRunway);
        double runwayDeg = Axis("OAK", "33").Heading.Degrees;

        (SimulationEngine engine, _) = StageOnTaxiway(onRunway.Position, runwayDeg, "C");
        CommandResult taxi = engine.SendCommand("N1287", "TAXI C D 33");
        Assert.False(taxi.Success, taxi.Message);
        Assert.Contains("unreachable", taxi.Message);
    }

    /// <summary>
    /// A misspelt taxiway is never accepted: <c>TAXI C ZZ 33</c> from OAK's west C bar is refused for the unknown name, not
    /// accepted with a "ZZ not taxied" note.
    /// </summary>
    [Fact]
    public void TaxiCZz33_Oak_HoldingAtCWestBar_RefusesAnUnknownTaxiway()
    {
        (SimulationEngine engine, _) = StageAtCWestBar(0.0, out _);

        CommandResult taxi = engine.SendCommand("N1287", "TAXI C ZZ 33");
        Assert.False(taxi.Success, taxi.Message);
        Assert.Contains("ZZ", taxi.Message);
        Assert.DoesNotContain("ZZ not taxied", taxi.Message);
    }

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
            new TaxiClass(AircraftCategory.Jet, WakeTurbulenceData.WakeClass.Large)
        );

        Assert.True(route is not null, failReason);
        int westBar = Bar(layout, "OAK", "33", "C", West).Id;
        Assert.Equal(westBar, FinalNodeId(route, start));
        HoldShortPoint hold = Assert.Single(route.HoldShortPoints, h => h.NodeId == westBar);
        Assert.Equal(HoldShortReason.DestinationRunway, hold.Reason);
    }

    /// <summary>
    /// OAK's west C bar lies past runway 15's far end, so <c>TAXI C 15</c> from it is no hold at the bar: the route has
    /// segments and ends elsewhere.
    /// </summary>
    [Fact]
    public void TaxiC15_Oak_StartAtCWestBar_DoesNotHoldThere()
    {
        AirportGroundLayout layout = Layout("OAK");
        GroundNode start = Oak33WestStart(layout, "bar");
        var options = new ExplicitPathOptions
        {
            StartPose = null,
            OccupiedTaxiway = null,
            ImpliedLeadTaxiway = null,
            AircraftLengthFt = B738LengthFt,
            DestinationRunway = "15",
        };

        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["C"],
            out string? failReason,
            options,
            new TaxiClass(AircraftCategory.Jet, WakeTurbulenceData.WakeClass.Large)
        );

        Assert.True(route is not null, failReason);
        Assert.NotEmpty(route.Segments);
        Assert.NotEqual(start.Id, route.Segments[^1].ToNodeId);
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

        GroundNode lineup = RouteMaterialiser.FindFullLengthLineupHoldShort(
            layout,
            start,
            "33",
            layout.GetRunwayHoldShortNodes("33"),
            new TaxiClass(Jet, Large)
        );
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
    /// TAXIAUTO to OAK 33 from the south end of K, a start with no side, runs the shadow test's route search to each bar
    /// once and takes the route to the bar it picks from that search, rather than searching it again, on a layout no
    /// earlier test has warmed.
    /// </summary>
    [Fact]
    public void TaxiAuto_Oak33_StartAtSouthEndOfK_SearchesEachBarOnce()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = TestAirportGroundData.LoadUncached("OAK");
        Assert.NotNull(layout);
        GroundNode start = NodeOnTaxiwayNear(layout, "K", AxisPoint(Axis("OAK", "33"), -111.0, -1680.0), 150.0);
        int bars = layout.GetRunwayHoldShortNodes("33").Count(b => b.Id != start.Id);

        int before = TaxiPathfinder.LineupBarShadowCache.RouteSearchesOnThisThread;
        TaxiRoute? route = TaxiPathfinder.FindRunwayRoute(layout, start, "33", Jet, Large);
        int searches = TaxiPathfinder.LineupBarShadowCache.RouteSearchesOnThisThread - before;

        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);
        Assert.Equal(bars, searches);
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
