using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests;

/// <summary>
/// The ground context menu's "Hold short of…" rows on the real KOAK layout: one row per bar along the remaining route,
/// nearest first, each with its distance from the aircraft rounded to 50 ft; one row per runway but the destination
/// runway the route ends at, named by the room's active runways (the active end; else the end on the side of a
/// full-length crossing; else both ends); and never a junction arc's joined name. The layout is parsed under the
/// server's lower-case id (<c>oak</c>) while the room keys its active runways by the FAA id (<c>OAK</c>), as in production.
///
/// Routes: the mock's <c>S T V W4</c> to runway 30 (ends at the W4 bar, mid-way along 12/30, so 12/30 is no row);
/// <c>W1</c> and <c>W7</c> with no runway assigned, across their 12/30 bars (the full-length crossings at the 30 and 12
/// ends); <c>G</c> south across 28R (mid-runway) to 28L; <c>K</c> across 15/33; <c>W W3</c> round the fillet from W
/// onto W3 to runway 30.
/// </summary>
public class GroundViewModelHoldShortMenuOrderTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoActiveRunways = new Dictionary<string, IReadOnlyList<string>>();

    public GroundViewModelHoldShortMenuOrderTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void MockRoute_RowsComeNearestFirst_EachRoundedTo50Ft()
    {
        (GroundViewModel vm, AircraftModel ac) = MockRoute(NoActiveRunways);

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortMenu(ac).Rows;

        string dump = Dump(rows);
        // B and W are each met on two route taxiways, on their own edges and on the junction fillets the route rounds.
        Assert.Equal("HS S1@S, HS T, HS S1@T, HS B@T, HS B@V, HS V, HS W@V, HS W@W4, HS W4", string.Join(", ", rows.Select(r => r.Command)));
        Assert.All(rows, r => Assert.True(r.Label.DistanceFt > 0, dump));
        Assert.All(rows, r => Assert.Equal(0, r.Label.DistanceFt % TugMovePlanner.NoteRoundingFt));
        Assert.Equal(rows.Select(r => r.Label.DistanceFt).Order(), rows.Select(r => r.Label.DistanceFt));

        HoldShortChoice first = rows[0];
        Assert.Equal((HoldShortChoice.TaxiwayBadge, "S1", "crossing on S"), (first.Label.Badge, first.Label.Name, first.Label.Where));

        HoldShortChoice last = rows[^1];
        Assert.Equal((HoldShortChoice.TaxiwayBadge, "W4", "before turning onto W4"), (last.Label.Badge, last.Label.Name, last.Label.Where));
    }

    /// <summary>
    /// The mock route ends at the W4 bar of 12/30, its destination runway: a taxi to a runway already ends holding short
    /// of it and the server takes <c>HS 12</c> there as a no-op, so the runway is no stop; the ladder ends with it as the
    /// dimmed destination row, the whole route away.
    /// </summary>
    [Fact]
    public void MockRoute_DestinationRunwayIsTheDestinationRow()
    {
        (GroundViewModel vm, AircraftModel ac) = MockRoute(NoActiveRunways);
        TaxiRoute route = vm.ResolveRemainingRoute(ac)!;

        HoldShortMenu menu = vm.GetHoldShortMenu(ac);

        string dump = Dump(menu.Rows);
        Assert.NotEmpty(menu.Rows);
        Assert.DoesNotContain(menu.Rows, r => (r.Command == "HS 12") || (r.Command == "HS 30"));
        Assert.DoesNotContain(menu.Rows, r => (r.Label.Badge == HoldShortChoice.RunwayBadge) && (r.Preview.Segments.Count == route.Segments.Count));
        Assert.True(menu.Rows.All(r => r.Label.Badge == HoldShortChoice.TaxiwayBadge), dump);
        HoldShortDestination destination = Assert.IsType<HoldShortDestination>(menu.Destination);
        Assert.Equal(("Runway 30", true), (destination.Name, destination.IsRunway));
        Assert.Equal("Runway 30 · route ends here", destination.ToString());
        Assert.True(destination.DistanceFt > menu.Rows[^1].Label.DistanceFt, dump);
        Assert.Equal("route S T V W4 · RWY 30", menu.RouteLine);
    }

    /// <summary>
    /// The destination row and the route line name the stand the broadcast taxi destination names, a <c>$</c> spot or an
    /// <c>@</c> gate (KOAK lists neither name, so the mock route stays as it resolves), and the assigned runway when there
    /// is no stand. No test layout has a helipad, so the <c>Helipad H1</c> wording is not covered here.
    /// </summary>
    [Theory]
    [InlineData("$Z5", "", "Spot Z5", false, "route S T V W4 · spot Z5")]
    [InlineData("@B22", "", "Gate B22", false, "route S T V W4 · gate B22")]
    [InlineData("", "30", "Runway 30", true, "route S T V W4 · RWY 30")]
    public void DestinationRow_Spot_Gate_Runway(string taxiDestination, string runway, string name, bool isRunway, string routeLine)
    {
        (GroundViewModel vm, AircraftModel ac) = MockRoute(NoActiveRunways);
        ac.AssignedRunway = runway;
        ac.TaxiDestination = taxiDestination;

        HoldShortMenu menu = vm.GetHoldShortMenu(ac);

        Assert.NotEmpty(menu.Rows);
        HoldShortDestination destination = Assert.IsType<HoldShortDestination>(menu.Destination);
        Assert.Equal((name, isRunway), (destination.Name, destination.IsRunway));
        Assert.Equal($"{name} · route ends here", destination.ToString());
        Assert.Equal(routeLine, menu.RouteLine);
    }

    /// <summary>A route to runway 30 along W1 from two nodes back crosses no bar: no stop, so no destination row either.</summary>
    [Fact]
    public void NoStops_DestinationOnly_CommandHidden()
    {
        AirportGroundLayout layout = LoadOak();
        GroundNode w1Bar = layout.Nodes.Values.Single(n => IsBar(n, "30", "W1"));
        (GroundViewModel vm, AircraftModel ac) = OnTheWayTo(layout, w1Bar, "W1", "30", NoActiveRunways);

        HoldShortMenu menu = vm.GetHoldShortMenu(ac);

        Assert.True(menu.Rows.Count == 0, Dump(menu.Rows));
        Assert.Null(menu.Destination);
    }

    [Theory]
    [InlineData("D", "now, taxiing on D")]
    [InlineData("", "now, taxiing")]
    public void MeRow_Taxiing_SaysTaxiingOnTaxiway(string taxiway, string expected) =>
        Assert.Equal(expected, GroundViewModel.HoldShortMeLine(Phase("Taxiing", taxiway)));

    [Theory]
    [InlineData("Holding Short 28R/10L", "B", "now, holding short of 28R on B")]
    [InlineData("Holding Short 28R", "", "now, holding short of 28R")]
    public void MeRow_HoldingShortOfRunway_SaysHoldingShortOfRunwayOnTaxiway(string phase, string taxiway, string expected) =>
        Assert.Equal(expected, GroundViewModel.HoldShortMeLine(Phase(phase, taxiway)));

    [Theory]
    [InlineData("Holding Short C", "now, holding short of C on D")]
    [InlineData("Holding Short spot 17", "now, holding short of spot 17 on D")]
    public void MeRow_HoldingShortOfTaxiway_SaysHoldingShortOfCOnD(string phase, string expected) =>
        Assert.Equal(expected, GroundViewModel.HoldShortMeLine(Phase(phase, "D")));

    [Fact]
    public void MeRow_OtherPhase_IsNone() => Assert.Null(GroundViewModel.HoldShortMeLine(Phase("At Parking", "")));

    /// <summary>
    /// An aircraft standing on the 15/33 bar on K the route starts at, taxiing, gets no stop for that bar: the bar it is
    /// at is behind the ladder, not on it.
    /// </summary>
    [Fact]
    public void AtTheBarTheRouteStartsAt_NoStopForIt()
    {
        AirportGroundLayout layout = LoadOak();
        (GroundNode bar, GroundNode behind) = KBarAndTheNodeBehind(layout);
        var ac = new AircraftModel
        {
            Callsign = "UAL100",
            AircraftType = "B738",
            Position = bar.Position,
            Heading = new TrueHeading(GeoMath.BearingTo(behind.Position, bar.Position)),
            CurrentPhase = "Taxiing",
            CurrentTaxiway = "K",
            TaxiRoute = "K",
            AssignedRunway = "",
        };
        GroundViewModel vm = ViewModel(layout, NoActiveRunways);
        Assert.Equal(bar.Id, vm.ResolveRemainingRoute(ac)?.Segments[0].FromNodeId);

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortMenu(ac).Rows;

        Assert.DoesNotContain(rows, r => r.BarPoint == bar.Position);
    }

    [Fact]
    public void MockRoute_NoRowNamesAJunctionArc()
    {
        (GroundViewModel vm, AircraftModel ac) = MockRoute(NoActiveRunways);

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortMenu(ac).Rows;

        Assert.NotEmpty(rows);
        Assert.DoesNotContain(rows, r => r.Label.Name.Contains(" - ") || r.Command.Contains(" - "));
    }

    [Fact]
    public void MockRoute_LocatedRowsKeepTheirPositionOrder()
    {
        (GroundViewModel vm, AircraftModel ac) = MockRoute(NoActiveRunways);

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortMenu(ac).Rows;

        // S1 joins S and T, so the route meets it on both: on S first, then on T.
        List<HoldShortChoice> s1 = [.. rows.Where(r => r.Label.Name == "S1")];
        Assert.Equal(["HS S1@S", "HS S1@T"], s1.Select(r => r.Command));
        Assert.Equal(["crossing on S", "crossing on T"], s1.Select(r => r.Label.Where));
        Assert.True(s1[0].Label.DistanceFt < s1[1].Label.DistanceFt, Dump(rows));
    }

    [Fact]
    public void RouteAcrossOneRunwayToAnother_OnlyTheCrossedRunwayIsARow()
    {
        (GroundViewModel vm, AircraftModel ac) = AcrossMidRunway(NoActiveRunways);

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortMenu(ac).Rows;

        HoldShortChoice runway = RunwayRow(rows);
        Assert.Equal(("10L/28R", "HS 10L"), (runway.Label.Name, runway.Command));
        Assert.DoesNotContain(rows, r => (r.Command == "HS 10R") || (r.Command == "HS 28L"));
    }

    [Fact]
    public void RunwayCrossedPastBothItsBars_OneRow_AtTheFirstBar()
    {
        AirportGroundLayout layout = LoadOak();
        GroundNode northBar = NorthBar28ROnG(layout);
        // The bar now sits on the C/G arc tangent, where the first G edge out of it in edge order leads toward the runway: the
        // two nodes back along G are the ones further from 28R's centreline.
        GroundNode start = WalkBackAwayFrom28R(layout, northBar, "G", 2);
        (GroundViewModel vm, AircraftModel ac) = ClearedFrom(layout, start, northBar, "G", "28L", NoActiveRunways);
        TaxiRoute route = vm.ResolveRemainingRoute(ac)!;
        List<int> bars28R = [.. route.Segments.Select(s => layout.Nodes[s.ToNodeId]).Where(n => IsBar(n, "28R", "G")).Select(n => n.Id)];
        Assert.True(bars28R.Count == 2, $"the route passes {bars28R.Count} of 28R's bars on G");

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortMenu(ac).Rows;

        HoldShortChoice runway = Assert.Single(rows, r => r.Label.Name == "10L/28R");
        Assert.Equal(northBar.Id, runway.Preview.Segments[^1].ToNodeId);
    }

    /// <summary>
    /// Held short of 10L/28R by a phase naming one end (<c>Holding Short 10L</c>, as the sim names a runway the room lists an
    /// end of) on G, two nodes back of the north 28R bar, the route running on past 28R's south bar to 28L, with 28R active:
    /// the south bar, which the active end would name <c>HS 28R</c>, is no stop, since the held runway is the layout's pair.
    /// A pin, green before and after the ladder resolved the pair from the layout: here the pair is the one-end name's
    /// computed reciprocal, which the ladder matched before. <c>HeldTarget.Of</c>'s layout branch is defensive: a normal runway
    /// pair is always the computed reciprocal, so no test reaches a pair only the layout knows.
    /// </summary>
    [Fact]
    public void HeldUnderOneEndsName_TheOtherEndIsNoStop()
    {
        AirportGroundLayout layout = LoadOak();
        GroundNode northBar = NorthBar28ROnG(layout);
        GroundNode start = WalkBackAwayFrom28R(layout, northBar, "G", 2);
        (GroundViewModel vm, AircraftModel ac) = ClearedFrom(layout, start, northBar, "G", "28L", Active("28R"));
        ac.CurrentPhase = "Holding Short 10L";

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortMenu(ac).Rows;

        Assert.DoesNotContain(rows, r => r.Label.Badge == HoldShortChoice.RunwayBadge);
    }

    [Fact]
    public void LowerCaseLayoutId_TheRoomsActiveEndNamesTheRow()
    {
        AirportGroundLayout layout = LoadOak();
        Assert.Equal("oak", layout.AirportId);
        (GroundViewModel vm, AircraftModel ac) = OnTheWayTo(layout, NorthBar28ROnG(layout), "G", "28L", Active("28R"));

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortMenu(ac).Rows;

        HoldShortChoice runway = rows.First(r => r.Label.Badge == HoldShortChoice.RunwayBadge);
        Assert.Equal(("28R", "HS 28R"), (runway.Label.Name, runway.Command));
    }

    /// <summary>
    /// Partway along K toward its 15/33 bar, behind the bar's painted stop, the route leads to the bar by the free-space
    /// leg along K, so the bar ends the route's first segment and is the menu's first row.
    /// </summary>
    [Fact]
    public void ShortOfTheBarTheRouteLeadsTo_TheBarIsTheFirstRow()
    {
        AirportGroundLayout layout = LoadOak();
        (GroundNode bar, GroundNode behind) = KBarAndTheNodeBehind(layout);
        double edgeFt = GeoMath.DistanceNm(bar.Position, behind.Position) * GeoMath.FeetPerNm;
        Assert.True(edgeFt > 300, $"K behind the 15/33 bar is {edgeFt:F0} ft");
        LatLon position = GeoMath.ProjectPoint(
            bar.Position,
            new TrueHeading(GeoMath.BearingTo(bar.Position, behind.Position)),
            200 / GeoMath.FeetPerNm
        );
        var ac = new AircraftModel
        {
            Callsign = "UAL100",
            AircraftType = "B738",
            Position = position,
            Heading = new TrueHeading(GeoMath.BearingTo(position, bar.Position)),
            CurrentTaxiway = "K",
            TaxiRoute = "K",
            AssignedRunway = "",
        };
        GroundViewModel vm = ViewModel(layout, NoActiveRunways);
        Assert.Equal(bar.Id, vm.ResolveRemainingRoute(ac)?.Segments[0].ToNodeId);

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortMenu(ac).Rows;

        Assert.NotEmpty(rows);
        HoldShortChoice first = rows[0];
        Assert.Equal((HoldShortChoice.RunwayBadge, 200d), (first.Label.Badge, first.Label.DistanceFt));
        Assert.True(bar.RunwayId!.Value.Contains(first.Command["HS ".Length..]), Dump(rows));
        TaxiRouteSegment leg = Assert.Single(first.Preview.Segments);
        Assert.True(leg.FromNodeId < 0, $"the preview's one segment starts at layout node {leg.FromNodeId}, not the approach leg");
        Assert.Equal(bar.Id, leg.ToNodeId);
        Assert.DoesNotContain(rows, r => r.Command == "HS K");
        Assert.All(rows.Skip(1), r => Assert.True(r.Label.DistanceFt > 200, Dump(rows)));
    }

    /// <summary>
    /// W meets W3 and U at one intersection: a route turning from W onto W3 rounds the fillet and never reaches a node on
    /// U's own edges, but the W node the fillet leaves from also carries the "W - U" arc, where the server binds HS U.
    /// </summary>
    [Fact]
    public void TaxiwayMetOnlyByAJunctionArc_IsARow_AtThatArcsNode()
    {
        AirportGroundLayout layout = LoadOak();
        GroundNode tangent = layout.Nodes.Values.Where(n => HasArc(n, "W", "W3") && HasArc(n, "W", "U")).MinBy(n => n.Id)!;
        // One W edge back, away from the intersection node that U's and W3's own edges reach.
        GroundNode start = WalkBack(tangent, "W", 1, n => n.Edges.OfType<GroundEdge>().Any(e => (e.TaxiwayName == "U") || (e.TaxiwayName == "W3")));
        var ac = new AircraftModel
        {
            Callsign = "UAL100",
            AircraftType = "B738",
            Position = start.Position,
            Heading = new TrueHeading(GeoMath.BearingTo(start.Position, tangent.Position)),
            CurrentTaxiway = "W",
            TaxiRoute = "W W3",
            AssignedRunway = "30",
        };
        GroundViewModel vm = ViewModel(layout, NoActiveRunways);
        TaxiRoute route = vm.ResolveRemainingRoute(ac)!;
        List<GroundNode> routeNodes = [.. route.Segments.Select(s => layout.Nodes[s.ToNodeId])];
        Assert.Contains(routeNodes, n => n.Id == tangent.Id);
        Assert.DoesNotContain(routeNodes, n => n.Edges.OfType<GroundEdge>().Any(e => e.TaxiwayName == "U"));

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortMenu(ac).Rows;

        HoldShortChoice u = Assert.Single(rows, r => r.Command == "HS U");
        Assert.Equal((HoldShortChoice.TaxiwayBadge, "U"), (u.Label.Badge, u.Label.Name));
        Assert.Equal(tangent.Id, u.Preview.Segments[^1].ToNodeId);
    }

    /// <summary>
    /// A B738 lined up mid-way along C west of H, cleared <c>C J</c> to 28L (so 28R, crossed on the way, is a row; the
    /// destination runway is none) and sent a turn about in place: its
    /// route starts at the C node ahead of its nose and segment 0 runs back over the aircraft to the far node (the shape
    /// <c>GroundViewModelMidEdgeStartOverlayTests</c> pins). The aircraft is past that start node, so the node is no bar
    /// of its own (the server walks from the end of segment 0), and every distance is the route's less the stretch of
    /// segment 0 already behind the aircraft.
    /// </summary>
    [Fact]
    public void PastTheRouteStartNode_ItIsNoRow_AndDistancesStartAtTheAircraft()
    {
        AirportGroundLayout layout = LoadOak();
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        LatLon position = new(
            tangentCut.Position.Lat + ((farEnd.Position.Lat - tangentCut.Position.Lat) * 0.35),
            tangentCut.Position.Lon + ((farEnd.Position.Lon - tangentCut.Position.Lon) * 0.35)
        );
        var ac = new AircraftModel
        {
            Callsign = "DAL880",
            AircraftType = "B738",
            Position = position,
            Heading = new TrueHeading(GeoMath.BearingTo(farEnd.Position, tangentCut.Position)),
            CurrentTaxiway = "C",
            TaxiRoute = "C J",
            AssignedRunway = "28L",
            HasActiveTaxiRoute = true,
            TaxiTurnAboutShape = TaxiTurnAboutShape.InPlace,
            TaxiTurnAboutTargetNodeId = farEnd.Id,
            IsOnGround = true,
        };
        GroundViewModel vm = ViewModel(layout, NoActiveRunways);
        TaxiRoute route = vm.ResolveRemainingRoute(ac)!;
        Assert.Equal((tangentCut.Id, farEnd.Id), (route.Segments[0].FromNodeId, route.Segments[0].ToNodeId));
        double pastStartFt =
            GeoMath.AlongTrackDistanceNm(position, tangentCut.Position, new TrueHeading(route.Segments[0].Edge.DepartureBearing)) * GeoMath.FeetPerNm;
        Assert.True(pastStartFt > 100, $"the aircraft is {pastStartFt:F0} ft past the start node");

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortMenu(ac).Rows;

        string dump = Dump(rows);
        Assert.True(rows.Count > 0, "no rows");
        Assert.True(rows.Any(r => r.Command == "HS 10L"), dump);
        // A row at the start node would preview no segments.
        Assert.All(rows, r => Assert.True(r.Preview.Segments.Count > 0, dump));
        // The expected distance is rounded to 50 ft like the row's, so it may land up to 25 ft away, plus 10 ft of slack.
        Assert.All(
            rows,
            r => Assert.True(Math.Abs(r.Label.DistanceFt - (route.PrefixDistanceFt(r.Preview.Segments.Count) - pastStartFt)) <= 35, dump)
        );
    }

    [Theory]
    [InlineData("28R", "28R", "HS 28R")]
    [InlineData("D10L", "10L", "HS 10L")]
    [InlineData("A28R,30", "28R", "HS 28R")]
    public void OneEndActive_TheActiveEndNamesTheRow(string active, string name, string command)
    {
        (GroundViewModel vm, AircraftModel ac) = AcrossMidRunway(Active(active));

        HoldShortChoice runway = RunwayRow(vm.GetHoldShortMenu(ac).Rows);

        Assert.Equal((name, command), (runway.Label.Name, runway.Command));
    }

    [Theory]
    [InlineData("W1", "30", "30", "HS 30")]
    [InlineData("W7", "12", "12", "HS 12")]
    public void BothEndsActive_FullLengthCrossing_TheEndOnThatSideNamesTheRow(string taxiway, string barEnd, string name, string command)
    {
        AirportGroundLayout layout = LoadOak();
        GroundNode bar = layout.Nodes.Values.Single(n => IsBar(n, barEnd, taxiway));
        (GroundViewModel vm, AircraftModel ac) = OnTheWayTo(layout, bar, taxiway, "", Active("12,30"));

        HoldShortChoice runway = RunwayRow(vm.GetHoldShortMenu(ac).Rows);

        Assert.Equal((name, command), (runway.Label.Name, runway.Command));
    }

    [Fact]
    public void BothEndsActive_MidRunwayCrossing_NamesBothEnds()
    {
        (GroundViewModel vm, AircraftModel ac) = AcrossMidRunway(Active("10L,28R"));

        HoldShortChoice runway = RunwayRow(vm.GetHoldShortMenu(ac).Rows);

        Assert.Equal(("10L/28R", "HS 10L"), (runway.Label.Name, runway.Command));
    }

    [Fact]
    public void NoEndActive_FullLengthCrossingNamesItsSide_MidRunwayCrossingNamesBothEnds()
    {
        AirportGroundLayout layout = LoadOak();
        GroundNode w1Bar = layout.Nodes.Values.Single(n => IsBar(n, "30", "W1"));
        (GroundViewModel w1Vm, AircraftModel w1Ac) = OnTheWayTo(layout, w1Bar, "W1", "", NoActiveRunways);
        (GroundViewModel gVm, AircraftModel gAc) = AcrossMidRunway(NoActiveRunways);

        HoldShortChoice fullLength = RunwayRow(w1Vm.GetHoldShortMenu(w1Ac).Rows);
        HoldShortChoice midRunway = RunwayRow(gVm.GetHoldShortMenu(gAc).Rows);

        Assert.Equal(("30", "HS 30"), (fullLength.Label.Name, fullLength.Command));
        Assert.Equal(("10L/28R", "HS 10L"), (midRunway.Label.Name, midRunway.Command));
    }

    [Fact]
    public void NoRunwayGeometry_FullLengthCrossingNamesBothEnds()
    {
        AirportGroundLayout layout = LoadOak();
        layout.Runways.Clear();
        GroundNode w1Bar = layout.Nodes.Values.Single(n => IsBar(n, "30", "W1"));
        (GroundViewModel vm, AircraftModel ac) = OnTheWayTo(layout, w1Bar, "W1", "", NoActiveRunways);

        HoldShortChoice runway = RunwayRow(vm.GetHoldShortMenu(ac).Rows);

        Assert.Equal(("12/30", "HS 12"), (runway.Label.Name, runway.Command));
    }

    /// <summary>
    /// YAAT-760: SCX8607 on S where the bundle has it 18 s after <c>TAXI S T U W W1 30</c>. The route turns from S onto
    /// T, and the instructor had to type <c>HS T</c>, which the sim armed at the first route node meeting T ("holding short
    /// of T at S"). The ladder offers that stop, bound where the server binds it, before the turn.
    /// </summary>
    [Fact]
    public void RouteTurningOntoATaxiway_OffersHoldShortOfIt_BeforeTheTurn()
    {
        AirportGroundLayout layout = LoadOak();
        var ac = new AircraftModel
        {
            Callsign = "SCX8607",
            AircraftType = "B738",
            Position = new LatLon(37.712205, -122.216953),
            Heading = new TrueHeading(199.6),
            CurrentPhase = "Taxiing",
            CurrentTaxiway = "S",
            TaxiRoute = "S T U W W1",
            AssignedRunway = "30",
            HasActiveTaxiRoute = true,
            IsOnGround = true,
        };
        GroundViewModel vm = ViewModel(layout, NoActiveRunways);
        TaxiRoute route = vm.ResolveRemainingRoute(ac)!;
        int firstMeetingT = route.Segments.FindIndex(s => layout.Nodes[s.ToNodeId].Edges.Any(e => e.MatchesTaxiway("T")));
        Assert.True(firstMeetingT >= 0, "the route never meets T");

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortMenu(ac).Rows;

        string dump = Dump(rows);
        HoldShortChoice t = Assert.Single(rows, r => r.Command == "HS T");
        Assert.Equal((HoldShortChoice.TaxiwayBadge, "T", "before turning onto T"), (t.Label.Badge, t.Label.Name, t.Label.Where));
        Assert.Equal(route.Segments[firstMeetingT].ToNodeId, t.Preview.Segments[^1].ToNodeId);
        Assert.DoesNotContain(rows, r => r.Command == "HS S");
        Assert.True(rows.Any(r => r.Command == "HS U"), dump);
    }

    /// <summary>
    /// UAL194 on T9 at SFO two nodes short of A, cleared <c>TAXI A B1 B K A F</c> to 28L (the clearance of bundle S1-SFO-2):
    /// the route turns onto A from T9 and again from K. <c>HS A</c> binds the first route node meeting A, so A is one stop, there,
    /// never a pair of located <c>HS A@…</c> stops.
    /// </summary>
    [Fact]
    public void RouteUsingATaxiwayTwice_OffersOneStop_AtTheFirstTurn()
    {
        AirportGroundLayout layout = LoadSfo();
        GroundNode? junction = layout.FindIntersectionNode("T9", "A");
        Assert.NotNull(junction);
        GroundNode start = WalkBack(junction, "T9", 2, n => n.Edges.Any(e => e.MatchesTaxiway("A")));
        var ac = new AircraftModel
        {
            Callsign = "UAL194",
            AircraftType = "B738",
            Position = start.Position,
            Heading = new TrueHeading(GeoMath.BearingTo(start.Position, junction.Position)),
            CurrentPhase = "Taxiing",
            CurrentTaxiway = "T9",
            TaxiRoute = "A B1 B K A F",
            AssignedRunway = "28L",
            HasActiveTaxiRoute = true,
            IsOnGround = true,
        };
        GroundViewModel vm = ViewModel(layout, NoActiveRunways);
        TaxiRoute? route = vm.ResolveRemainingRoute(ac);
        Assert.NotNull(route);
        string routeLine = string.Join(" ", route.Segments.Select(s => s.TaxiwayName));
        int firstOnA = route.Segments.FindIndex(s => s.TaxiwayName == "A");
        Assert.True(firstOnA > 0, $"the route does not turn onto A: {routeLine}");
        int offA = route.Segments.FindIndex(firstOnA, s => s.TaxiwayName != "A");
        Assert.True((offA > 0) && (route.Segments.FindIndex(offA, s => s.TaxiwayName == "A") > 0), $"the route uses A once: {routeLine}");
        int firstMeetingA = route.Segments.FindIndex(s => layout.Nodes[s.ToNodeId].Edges.Any(e => e.MatchesTaxiway("A")));

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortMenu(ac).Rows;

        string dump = Dump(rows);
        List<HoldShortChoice> a = [.. rows.Where(r => (r.Command == "HS A") || r.Command.StartsWith("HS A@", StringComparison.Ordinal))];
        Assert.True(a.Count == 1, $"{routeLine} → {dump}");
        Assert.Equal("HS A", a[0].Command);
        Assert.Equal(route.Segments[firstMeetingA].ToNodeId, a[0].Preview.Segments[^1].ToNodeId);
    }

    /// <summary>
    /// OAK <c>D F</c>, the chord over 15/33: the ladder still stops at the crossing's near bar, a real node on D, though the chord
    /// touches no centreline.
    /// </summary>
    [Fact]
    public void HoldShortMenu_ChordCrossing_ListsTheCrossedRunwayBar()
    {
        (GroundViewModel vm, AircraftModel ac, _, GroundNode dBar) = OnDBoundOverTheChord();
        Assert.NotEmpty(ChordOf(vm.ResolveRemainingRoute(ac)!));

        HoldShortChoice runway = RunwayRow(vm.GetHoldShortMenu(ac).Rows);

        Assert.Equal(dBar.Id, runway.Preview.Segments[^1].ToNodeId);
    }

    /// <summary>A chord over 15/33 counts as going onto that runway and no other, by the segment: it reaches no centreline node.</summary>
    [Fact]
    public void TravelsOnRunway_ChordCrossing_CountsTheCrossing()
    {
        (GroundViewModel vm, AircraftModel ac, AirportGroundLayout layout, GroundNode dBar) = OnDBoundOverTheChord();
        TaxiRoute route = vm.ResolveRemainingRoute(ac)!;
        Assert.DoesNotContain(route.Segments, s => s.Edge.ToNode.Edges.Exists(e => e.IsRunwayCenterline));

        Assert.True(GroundViewModel.TravelsOnRunway(layout, route, dBar.RunwayId!.Value));
        Assert.False(GroundViewModel.TravelsOnRunway(layout, route, RunwayIdentifier.Parse("10L/28R")));
    }

    /// <summary>The overlay rebuilds the straight-over leg the sim flies: the same virtual points, the taxiways' names, no centreline.</summary>
    [Fact]
    public void GroundViewModel_StraightOverLeg_OverlayDrawsTheServerLeg()
    {
        (GroundViewModel vm, AircraftModel ac, AirportGroundLayout layout, _) = OnDBoundOverTheChord();

        TaxiRoute drawn = vm.ResolveRemainingRoute(ac)!;
        TaxiRoute? server = TaxiPathfinder.ResolveExplicitPath(
            layout,
            drawn.Segments[0].FromNodeId,
            ["D", "F"],
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = "D",
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt(ac.AircraftType),
                StartHeadingTrue = ac.Heading.Degrees,
            },
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );
        Assert.True(server is not null, failReason);

        List<TaxiRouteSegment> drawnLeg = ChordOf(drawn);
        Assert.Equal(["D", "D", "F"], drawnLeg.Take(3).Select(s => s.TaxiwayName));
        Assert.Equal(ChordOf(server).Select(s => s.ToNodeId), drawnLeg.Select(s => s.ToNodeId));
        Assert.Equal(ChordOf(server).Select(s => s.Edge.ToNode.Position), drawnLeg.Select(s => s.Edge.ToNode.Position));
        Assert.DoesNotContain(drawn.Segments, s => s.Edge.Edge.IsRunwayCenterline);
    }

    private static List<TaxiRouteSegment> ChordOf(TaxiRoute route) => [.. route.Segments.Where(s => VirtualNode.IsVirtualEdge(s.Edge.Edge))];

    /// <summary>
    /// A B738 two nodes back along D from its 15/33 bar, facing it, cleared <c>D F</c>, which crosses by a chord. Parsed with the real
    /// airport code, as the server parses it: the chord is capped at the 75 ft runway's edge, a width only navdata gives.
    /// </summary>
    private static (GroundViewModel Vm, AircraftModel Ac, AirportGroundLayout Layout, GroundNode DBar) OnDBoundOverTheChord()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        AirportGroundLayout layout = GeoJsonParser.Parse(
            "oak",
            File.ReadAllText(path),
            "OAK",
            new LayoutInputs([], VideoMap: null),
            FilletMode.Standard
        );
        GroundNode dBar = layout.Nodes.Values.Single(n => IsBar(n, "33", "D"));
        (GroundViewModel vm, AircraftModel ac) = OnTheWayTo(layout, dBar, "D", "", NoActiveRunways);
        ac.TaxiRoute = "D F";
        return (vm, ac, layout, dBar);
    }

    // --- Fixtures ---------------------------------------------------------------------------

    /// <summary>KOAK parsed under the lower-case id the server gives a layout (<c>AirportGroundDataService</c>).</summary>
    private static AirportGroundLayout LoadOak()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        Assert.True(File.Exists(path), $"{path} is missing from the test output");
        return GeoJsonParser.Parse("oak", File.ReadAllText(path), null, new LayoutInputs([], VideoMap: null), FilletMode.Standard);
    }

    private static AirportGroundLayout LoadSfo()
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        Assert.True(File.Exists(path), $"{path} is missing from the test output");
        return GeoJsonParser.Parse("SFO", File.ReadAllText(path), "SFO", new LayoutInputs([], VideoMap: null), FilletMode.Standard);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Active(string ends) =>
        new Dictionary<string, IReadOnlyList<string>> { ["OAK"] = ends.Split(',') };

    private static GroundViewModel ViewModel(AirportGroundLayout layout, IReadOnlyDictionary<string, IReadOnlyList<string>> active)
    {
        var vm = new GroundViewModel(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask) { RoomActiveRunways = () => active };
        vm.SetDomainLayoutForTesting(layout);
        return vm;
    }

    /// <summary>The mock's NKS743: on S north of S1, facing south, cleared <c>S T V W4</c> to runway 30.</summary>
    private static (GroundViewModel Vm, AircraftModel Ac) MockRoute(IReadOnlyDictionary<string, IReadOnlyList<string>> active)
    {
        AirportGroundLayout layout = LoadOak();
        GroundNode start = layout.FindNearestNode(37.715225, -122.217621)!;
        Assert.Contains(start.Edges, e => e.TaxiwayName == "S");
        GroundNode s1 = layout.FindIntersectionNode("S", "S1")!;
        var ac = new AircraftModel
        {
            Callsign = "NKS743",
            AircraftType = "A320",
            Position = start.Position,
            Heading = new TrueHeading(GeoMath.BearingTo(start.Position, s1.Position)),
            CurrentTaxiway = "S",
            TaxiRoute = "S T V W4",
            AssignedRunway = "30",
        };
        return (ViewModel(layout, active), ac);
    }

    /// <summary>
    /// A southbound G taxi to 28L, crossing 10L/28R mid-runway on the way: the route's one runway row, since 28L is its
    /// destination.
    /// </summary>
    private static (GroundViewModel Vm, AircraftModel Ac) AcrossMidRunway(IReadOnlyDictionary<string, IReadOnlyList<string>> active)
    {
        AirportGroundLayout layout = LoadOak();
        return OnTheWayTo(layout, NorthBar28ROnG(layout), "G", "28L", active);
    }

    /// <summary>The 28R bar on G north of the runway: the first a southbound G taxi meets.</summary>
    private static GroundNode NorthBar28ROnG(AirportGroundLayout layout) =>
        layout.Nodes.Values.Where(n => IsBar(n, "28R", "G")).MaxBy(n => n.Position.Lat)!;

    /// <summary>An aircraft two nodes back along <paramref name="taxiway"/> from <paramref name="bar"/>, facing it, cleared along it.</summary>
    private static (GroundViewModel Vm, AircraftModel Ac) OnTheWayTo(
        AirportGroundLayout layout,
        GroundNode bar,
        string taxiway,
        string runway,
        IReadOnlyDictionary<string, IReadOnlyList<string>> active
    )
    {
        GroundNode start = WalkBack(bar, taxiway, 2, NearRunway);
        return ClearedFrom(layout, start, bar, taxiway, runway, active);
    }

    /// <summary>An aircraft at <paramref name="start"/>, facing <paramref name="bar"/>, cleared along <paramref name="taxiway"/>.</summary>
    private static (GroundViewModel Vm, AircraftModel Ac) ClearedFrom(
        AirportGroundLayout layout,
        GroundNode start,
        GroundNode bar,
        string taxiway,
        string runway,
        IReadOnlyDictionary<string, IReadOnlyList<string>> active
    )
    {
        var ac = new AircraftModel
        {
            Callsign = "UAL100",
            AircraftType = "B738",
            Position = start.Position,
            Heading = new TrueHeading(GeoMath.BearingTo(start.Position, bar.Position)),
            CurrentTaxiway = taxiway,
            TaxiRoute = taxiway,
            AssignedRunway = runway,
        };
        return (ViewModel(layout, active), ac);
    }

    /// <summary>
    /// The node <paramref name="hops"/> straight <paramref name="taxiway"/> edges back from <paramref name="from"/>, never
    /// onto a bar or a node <paramref name="avoid"/> rejects; the first such walk in edge order, backing out of a branch
    /// that dead-ends.
    /// </summary>
    private static GroundNode WalkBack(GroundNode from, string taxiway, int hops, Func<GroundNode, bool> avoid)
    {
        GroundNode? start = TryWalkBack(from, from, taxiway, hops, avoid);
        Assert.True(start is not null, $"no {hops}-hop walk along {taxiway} from node {from.Id}");
        return start;
    }

    private static GroundNode? TryWalkBack(GroundNode current, GroundNode previous, string taxiway, int hops, Func<GroundNode, bool> avoid)
    {
        if (hops == 0)
        {
            return current;
        }

        IEnumerable<GroundNode> candidates = current
            .Edges.OfType<GroundEdge>()
            .Where(e => e.TaxiwayName == taxiway)
            .Select(e => e.OtherNode(current))
            .Where(n => (n.Id != previous.Id) && (n.Type != GroundNodeType.RunwayHoldShort) && !avoid(n));
        return candidates.Select(n => TryWalkBack(n, current, taxiway, hops - 1, avoid)).FirstOrDefault(found => found is not null);
    }

    /// <summary>
    /// The node <paramref name="hops"/> straight <paramref name="taxiway"/> edges back from <paramref name="bar"/>, walking away
    /// from 28R: only a neighbour further from its centreline than the current node counts, never a bar or a runway link.
    /// </summary>
    private static GroundNode WalkBackAwayFrom28R(AirportGroundLayout layout, GroundNode bar, string taxiway, int hops)
    {
        RunwayInfo runway = Assert.Single(RunwayOccupancy.AirportRunways(layout.AirportId), r => (r.Id.End1 == "28R") || (r.Id.End2 == "28R"));
        GroundNode? start = TryWalkAwayFrom(runway, bar, taxiway, hops);
        Assert.True(start is not null, $"no {hops}-hop walk along {taxiway} away from 28R from node {bar.Id}");
        return start!;
    }

    private static GroundNode? TryWalkAwayFrom(RunwayInfo runway, GroundNode current, string taxiway, int hops)
    {
        if (hops == 0)
        {
            return current;
        }

        double hereFt = RunwayHoldBars.CentrelineFt(runway, current.Position);
        return current
            .Edges.OfType<GroundEdge>()
            .Where(e => e.TaxiwayName == taxiway)
            .Select(e => e.OtherNode(current))
            .Where(n => (n.Type != GroundNodeType.RunwayHoldShort) && !NearRunway(n))
            .Where(n => RunwayHoldBars.CentrelineFt(runway, n.Position) > hereFt)
            .Select(n => TryWalkAwayFrom(runway, n, taxiway, hops - 1))
            .FirstOrDefault(found => found is not null);
    }

    /// <summary>A 15/33 bar on K and the K node behind it, away from the runway, the pair a longest K edge apart.</summary>
    private static (GroundNode Bar, GroundNode Behind) KBarAndTheNodeBehind(AirportGroundLayout layout) =>
        layout
            .Nodes.Values.Where(n => IsBar(n, "33", "K"))
            .SelectMany(b => b.Edges.Where(e => e.TaxiwayName == "K").Select(e => (Bar: b, Behind: e.OtherNode(b))))
            .Where(pair => !NearRunway(pair.Behind))
            .MaxBy(pair => GeoMath.DistanceNm(pair.Bar.Position, pair.Behind.Position));

    private static AircraftModel Phase(string phase, string taxiway) =>
        new()
        {
            Callsign = "UAL455",
            AircraftType = "A320",
            CurrentPhase = phase,
            CurrentTaxiway = taxiway,
        };

    /// <summary>A node on or at a runway: any link (centerline or junction arc) names one.</summary>
    private static bool NearRunway(GroundNode node) => node.Edges.Any(e => e.TaxiwayName.Contains("RWY", StringComparison.OrdinalIgnoreCase));

    /// <summary>The long straight C edge west of the C/H junction, away from C/B: its node nearer H, then its far node.</summary>
    private static (GroundNode TangentCut, GroundNode FarEnd) LongCEdgeWestOfH(AirportGroundLayout layout)
    {
        GroundNode junctionCH = layout.FindIntersectionNode("C", "H")!;
        GroundNode junctionCB = layout.FindIntersectionNode("C", "B")!;
        double awayFromB = (GeoMath.BearingTo(junctionCH.Position, junctionCB.Position) + 180.0) % 360.0;
        GroundNode tangentCut = NextAlongC(junctionCH, awayFromB);
        GroundNode farEnd = NextAlongC(tangentCut, GeoMath.BearingTo(junctionCH.Position, tangentCut.Position));
        return (tangentCut, farEnd);
    }

    private static GroundNode NextAlongC(GroundNode node, double bearingDeg) =>
        node
            .Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("C"))
            .Select(e => e.OtherNode(node))
            .MinBy(other => GeoMath.AbsBearingDifference(GeoMath.BearingTo(node.Position, other.Position), bearingDeg))!;

    private static bool HasArc(GroundNode node, string a, string b) =>
        node.Edges.OfType<GroundArc>().Any(arc => arc.MatchesTaxiway(a) && arc.MatchesTaxiway(b) && (arc.TaxiwayNames.Length == 2));

    private static bool IsBar(GroundNode node, string runwayEnd, string taxiway) =>
        (node.Type == GroundNodeType.RunwayHoldShort)
        && (node.RunwayId is { } id)
        && id.Contains(runwayEnd)
        && node.Edges.Any(e => e.TaxiwayName == taxiway);

    private static HoldShortChoice RunwayRow(IReadOnlyList<HoldShortChoice> rows)
    {
        List<HoldShortChoice> runways = [.. rows.Where(r => r.Label.Badge == HoldShortChoice.RunwayBadge)];
        Assert.True(runways.Count == 1, Dump(rows));
        return runways[0];
    }

    private static string Dump(IReadOnlyList<HoldShortChoice> rows) =>
        string.Join(" | ", rows.Select(r => $"{r.Label.Badge} {r.Label.Name} · {r.Label.Where} · ~{r.Label.DistanceFt} ft — {r.Command}"));
}
