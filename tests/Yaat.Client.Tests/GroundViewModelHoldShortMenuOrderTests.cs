using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests;

/// <summary>
/// The ground context menu's "Hold short of…" rows on the real KOAK layout: one row per bar along the remaining route,
/// nearest first, each with its distance from the aircraft rounded to 50 ft; one row per runway, named by the room's
/// active runways (the active end; else the end on the side of a full-length crossing; else both ends); and never a
/// junction arc's joined name. The layout is parsed under the server's lower-case id (<c>oak</c>) while the room keys
/// its active runways by the FAA id (<c>OAK</c>), as in production.
///
/// Routes: the mock's <c>S T V W4</c> to runway 30 (ends at the W4 bar, mid-way along 12/30); <c>W1</c> and <c>W7</c>
/// to their 12/30 bars (the full-length crossings at the 30 and 12 ends); <c>G</c> south across 28R to 28L; <c>K</c>
/// across 15/33; <c>W W3</c> round the fillet from W onto W3 to runway 30.
/// </summary>
public class GroundViewModelHoldShortMenuOrderTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoActiveRunways = new Dictionary<string, IReadOnlyList<string>>();

    public GroundViewModelHoldShortMenuOrderTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void MockRoute_RowsComeNearestFirst_EachRoundedTo50Ft()
    {
        (GroundViewModel vm, AircraftModel ac) = MockRoute(NoActiveRunways);

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortTargets(ac);

        string dump = Dump(rows);
        // B and W are each met on two route taxiways, on their own edges and on the junction fillets the route rounds.
        Assert.Equal("HS S1@S, HS S1@T, HS B@T, HS B@V, HS W@V, HS W@W4, HS 12", string.Join(", ", rows.Select(r => r.Command)));
        Assert.All(rows, r => Assert.True(r.Label.DistanceFt > 0, dump));
        Assert.All(rows, r => Assert.Equal(0, r.Label.DistanceFt % TugMovePlanner.NoteRoundingFt));
        Assert.Equal(rows.Select(r => r.Label.DistanceFt).Order(), rows.Select(r => r.Label.DistanceFt));

        HoldShortChoice first = rows[0];
        Assert.Equal((HoldShortChoice.TaxiwayBadge, "S1", "crossing on S"), (first.Label.Badge, first.Label.Name, first.Label.Where));

        HoldShortChoice last = rows[^1];
        Assert.Equal(HoldShortChoice.RunwayBadge, last.Label.Badge);
        Assert.Equal("at W4, end of route", last.Label.Where);
    }

    [Fact]
    public void MockRoute_NoRowNamesAJunctionArc()
    {
        (GroundViewModel vm, AircraftModel ac) = MockRoute(NoActiveRunways);

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortTargets(ac);

        Assert.NotEmpty(rows);
        Assert.DoesNotContain(rows, r => r.Label.Name.Contains(" - ") || r.Command.Contains(" - "));
    }

    [Fact]
    public void MockRoute_LocatedRowsKeepTheirPositionOrder()
    {
        (GroundViewModel vm, AircraftModel ac) = MockRoute(NoActiveRunways);

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortTargets(ac);

        // S1 joins S and T, so the route meets it on both: on S first, then on T.
        List<HoldShortChoice> s1 = [.. rows.Where(r => r.Label.Name == "S1")];
        Assert.Equal(["HS S1@S", "HS S1@T"], s1.Select(r => r.Command));
        Assert.Equal(["crossing on S", "crossing on T"], s1.Select(r => r.Label.Where));
        Assert.True(s1[0].Label.DistanceFt < s1[1].Label.DistanceFt, Dump(rows));
    }

    [Fact]
    public void RouteAcrossTwoRunways_OneRowPerRunway_InRouteOrder()
    {
        AirportGroundLayout layout = LoadOak();
        (GroundViewModel vm, AircraftModel ac) = OnTheWayTo(layout, NorthBar28ROnG(layout), "G", "28L", NoActiveRunways);

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortTargets(ac);

        List<HoldShortChoice> runways = [.. rows.Where(r => r.Label.Badge == HoldShortChoice.RunwayBadge)];
        Assert.True(runways.Count == 2, Dump(rows));
        Assert.Equal(["Runway 10L/28R", "Runway 10R/28L"], runways.Select(r => r.Label.Name));
        Assert.Equal(["HS 10L", "HS 10R"], runways.Select(r => r.Command));
    }

    [Fact]
    public void RunwayCrossedPastBothItsBars_OneRow_AtTheFirstBar()
    {
        AirportGroundLayout layout = LoadOak();
        GroundNode northBar = NorthBar28ROnG(layout);
        (GroundViewModel vm, AircraftModel ac) = OnTheWayTo(layout, northBar, "G", "28L", NoActiveRunways);
        TaxiRoute route = vm.ResolveRemainingRoute(ac)!;
        List<int> bars28R = [.. route.Segments.Select(s => layout.Nodes[s.ToNodeId]).Where(n => IsBar(n, "28R", "G")).Select(n => n.Id)];
        Assert.True(bars28R.Count == 2, $"the route passes {bars28R.Count} of 28R's bars on G");

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortTargets(ac);

        HoldShortChoice runway = Assert.Single(rows, r => r.Label.Name == "Runway 10L/28R");
        Assert.Equal(northBar.Id, runway.Preview.Segments[^1].ToNodeId);
    }

    [Fact]
    public void LowerCaseLayoutId_TheRoomsActiveEndNamesTheRow()
    {
        AirportGroundLayout layout = LoadOak();
        Assert.Equal("oak", layout.AirportId);
        (GroundViewModel vm, AircraftModel ac) = OnTheWayTo(layout, NorthBar28ROnG(layout), "G", "28L", Active("28R"));

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortTargets(ac);

        HoldShortChoice runway = rows.First(r => r.Label.Badge == HoldShortChoice.RunwayBadge);
        Assert.Equal(("Runway 28R", "HS 28R"), (runway.Label.Name, runway.Command));
    }

    /// <summary>
    /// Partway along K toward its 15/33 bar the route starts at the bar itself (no free-space leg is laid to a holding
    /// position), so the bar is the route's start node, not the end of any segment.
    /// </summary>
    [Fact]
    public void ShortOfTheBarTheRouteStartsAt_TheBarIsTheFirstRow()
    {
        AirportGroundLayout layout = LoadOak();
        (GroundNode bar, GroundNode behind) = layout
            .Nodes.Values.Where(n => IsBar(n, "33", "K"))
            .SelectMany(b => b.Edges.Where(e => e.TaxiwayName == "K").Select(e => (Bar: b, Behind: e.OtherNode(b))))
            .Where(pair => !NearRunway(pair.Behind))
            .MaxBy(pair => GeoMath.DistanceNm(pair.Bar.Position, pair.Behind.Position));
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
        Assert.Equal(bar.Id, vm.ResolveRemainingRoute(ac)?.Segments[0].FromNodeId);

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortTargets(ac);

        Assert.NotEmpty(rows);
        HoldShortChoice first = rows[0];
        Assert.Equal((HoldShortChoice.RunwayBadge, 200d), (first.Label.Badge, first.Label.DistanceFt));
        Assert.True(bar.RunwayId!.Value.Contains(first.Command["HS ".Length..]), Dump(rows));
        Assert.Empty(first.Preview.Segments);
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

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortTargets(ac);

        HoldShortChoice u = Assert.Single(rows, r => r.Command == "HS U");
        Assert.Equal((HoldShortChoice.TaxiwayBadge, "U"), (u.Label.Badge, u.Label.Name));
        Assert.Equal(tangent.Id, u.Preview.Segments[^1].ToNodeId);
    }

    /// <summary>
    /// A B738 lined up mid-way along C west of H, cleared <c>C J</c> to 28R (behind it) and sent a turn about in place: its
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
            AssignedRunway = "28R",
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

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortTargets(ac);

        string dump = Dump(rows);
        Assert.True(rows.Count > 0, "no rows");
        // A row at the start node would preview no segments.
        Assert.All(rows, r => Assert.True(r.Preview.Segments.Count > 0, dump));
        // The expected distance is rounded to 50 ft like the row's, so it may land up to 25 ft away, plus 10 ft of slack.
        Assert.All(
            rows,
            r => Assert.True(Math.Abs(r.Label.DistanceFt - (route.PrefixDistanceFt(r.Preview.Segments.Count) - pastStartFt)) <= 35, dump)
        );
    }

    [Theory]
    [InlineData("30", "Runway 30", "HS 30")]
    [InlineData("D12", "Runway 12", "HS 12")]
    [InlineData("A30,28L", "Runway 30", "HS 30")]
    public void OneEndActive_TheActiveEndNamesTheRow(string active, string name, string command)
    {
        (GroundViewModel vm, AircraftModel ac) = MockRoute(Active(active));

        HoldShortChoice runway = RunwayRow(vm.GetHoldShortTargets(ac));

        Assert.Equal((name, command), (runway.Label.Name, runway.Command));
    }

    [Theory]
    [InlineData("W1", "30", "Runway 30", "HS 30")]
    [InlineData("W7", "12", "Runway 12", "HS 12")]
    public void BothEndsActive_FullLengthCrossing_TheEndOnThatSideNamesTheRow(string taxiway, string barEnd, string name, string command)
    {
        AirportGroundLayout layout = LoadOak();
        GroundNode bar = layout.Nodes.Values.Single(n => IsBar(n, barEnd, taxiway));
        (GroundViewModel vm, AircraftModel ac) = OnTheWayTo(layout, bar, taxiway, "30", Active("12,30"));

        HoldShortChoice runway = RunwayRow(vm.GetHoldShortTargets(ac));

        Assert.Equal((name, command), (runway.Label.Name, runway.Command));
    }

    [Fact]
    public void BothEndsActive_MidRunwayCrossing_NamesBothEnds()
    {
        (GroundViewModel vm, AircraftModel ac) = MockRoute(Active("12,30"));

        HoldShortChoice runway = RunwayRow(vm.GetHoldShortTargets(ac));

        Assert.Equal(("Runway 12/30", "HS 12"), (runway.Label.Name, runway.Command));
    }

    [Fact]
    public void NoEndActive_FullLengthCrossingNamesItsSide_MidRunwayCrossingNamesBothEnds()
    {
        AirportGroundLayout layout = LoadOak();
        GroundNode w1Bar = layout.Nodes.Values.Single(n => IsBar(n, "30", "W1"));
        (GroundViewModel w1Vm, AircraftModel w1Ac) = OnTheWayTo(layout, w1Bar, "W1", "30", NoActiveRunways);
        (GroundViewModel mockVm, AircraftModel mockAc) = MockRoute(NoActiveRunways);

        HoldShortChoice fullLength = RunwayRow(w1Vm.GetHoldShortTargets(w1Ac));
        HoldShortChoice midRunway = RunwayRow(mockVm.GetHoldShortTargets(mockAc));

        Assert.Equal(("Runway 30", "HS 30"), (fullLength.Label.Name, fullLength.Command));
        Assert.Equal(("Runway 12/30", "HS 12"), (midRunway.Label.Name, midRunway.Command));
    }

    [Fact]
    public void NoRunwayGeometry_FullLengthCrossingNamesBothEnds()
    {
        AirportGroundLayout layout = LoadOak();
        layout.Runways.Clear();
        GroundNode w1Bar = layout.Nodes.Values.Single(n => IsBar(n, "30", "W1"));
        (GroundViewModel vm, AircraftModel ac) = OnTheWayTo(layout, w1Bar, "W1", "30", NoActiveRunways);

        HoldShortChoice runway = RunwayRow(vm.GetHoldShortTargets(ac));

        Assert.Equal(("Runway 12/30", "HS 12"), (runway.Label.Name, runway.Command));
    }

    // --- Fixtures ---------------------------------------------------------------------------

    /// <summary>KOAK parsed under the lower-case id the server gives a layout (<c>AirportGroundDataService</c>).</summary>
    private static AirportGroundLayout LoadOak()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        Assert.True(File.Exists(path), $"{path} is missing from the test output");
        return GeoJsonParser.Parse("oak", File.ReadAllText(path), null, FilletMode.Standard);
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
