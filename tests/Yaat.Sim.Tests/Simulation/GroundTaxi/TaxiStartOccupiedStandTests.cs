using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-576 (OAK S1-OAK-5 bundle): SWA919's <c>PUSH TE</c> off stand 20 stopped beside SWA2436, parked at stand 23, and the
/// later <c>TAXI $E</c> started at the nearest node, stand 23's own parking node (531), so its free-space leg drove onto the
/// parked aircraft and the conflict detector held it there for good. A taxi never starts through an occupied stand: its
/// parking node is never the start, nor is a node whose free-space leg passes a parked aircraft.
/// </summary>
public class TaxiStartOccupiedStandTests
{
    /// <summary>
    /// How far SWA919 stood from stand 23's parking node when the bundle's <c>TAXI $E</c> came (t=815): its route's free-space
    /// leg ran 50 ft to the node on a bearing of 145.9°.
    /// </summary>
    private const double StalledPushFromStandFt = 50.2;

    private const double StalledPushBearingFromStandDeg = 325.9;

    /// <summary>SWA919's nose at the end of its push (t=810).</summary>
    private const double StalledPushHeadingDeg = 338.4;

    private const string OccupiedStand = "23";

    public TaxiStartOccupiedStandTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TaxiAfterStalledPush_FromBesideOccupiedStand_RouteAvoidsItsParkingNode()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        GroundNode stand = Stand(layout, OccupiedStand);
        AircraftState taxiing = StalledPusher(layout);
        AircraftState parked = ParkedAt(layout, "SWA2436", "B732", OccupiedStand, 47.0);

        TaxiRoute route = TaxiE(layout, taxiing, [taxiing, parked]);

        Assert.DoesNotContain(route.Segments, s => (s.FromNodeId == stand.Id) || (s.ToNodeId == stand.Id));
    }

    [Fact]
    public void TaxiAfterStalledPush_FromBesideOccupiedStand_RouteAvoidsTheStandLeadIn()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        GroundNode stand = Stand(layout, OccupiedStand);
        HashSet<int> standAndLeadIn = [stand.Id, .. LeadIn(stand).Select(n => n.Id)];
        AircraftState taxiing = StalledPusher(layout);
        AircraftState parked = ParkedAt(layout, "SWA2436", "B732", OccupiedStand, 47.0);

        TaxiRoute route = TaxiE(layout, taxiing, [taxiing, parked]);

        Assert.DoesNotContain(route.Segments, s => standAndLeadIn.Contains(s.FromNodeId) || standAndLeadIn.Contains(s.ToNodeId));
    }

    [Fact]
    public void TaxiFromBesideAnEmptyStand_StillMayStartAtItsParkingNode()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        GroundNode stand = Stand(layout, OccupiedStand);
        AircraftState taxiing = StalledPusher(layout);

        TaxiRoute route = TaxiE(layout, taxiing, [taxiing]);

        Assert.Contains(route.Segments.Take(2), s => (s.FromNodeId == stand.Id) || (s.ToNodeId == stand.Id));
    }

    [Fact]
    public void TaxiFromOwnStand_StartsAtOwnParkingNode()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        GroundNode stand = Stand(layout, OccupiedStand);
        AircraftState own = ParkedAt(layout, "SWA2436", "B732", OccupiedStand, 47.0);
        AircraftState neighbour = ParkedAt(layout, "SWA1118", "B737", "22", 21.0);

        TaxiRoute route = TaxiE(layout, own, [own, neighbour]);

        double startFt = GeoMath.DistanceNm(route.Segments[0].Edge.FromNode.Position, stand.Position) * GeoMath.FeetPerNm;
        Assert.True(
            startFt <= AirportGroundLayout.AtNodeToleranceFt,
            $"the route starts {startFt:F0} ft from its own stand {OccupiedStand}: {route.ToSummary()}"
        );
    }

    [Fact]
    public void FindTaxiStartNode_OccupiedStandSet_SkipsItsParkingNode()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        GroundNode stand = Stand(layout, OccupiedStand);
        AircraftState taxiing = StalledPusher(layout);
        AircraftState parked = ParkedAt(layout, "SWA2436", "B732", OccupiedStand, 47.0);
        var heading = new TrueHeading(StalledPushHeadingDeg);

        GroundNode? alone = layout.FindTaxiStartNode(taxiing.Position, heading, TaxiStartTraffic.Of(taxiing, []));
        GroundNode? beside = layout.FindTaxiStartNode(taxiing.Position, heading, TaxiStartTraffic.Of(taxiing, [NeighbourCandidate.From(parked)]));

        Assert.Equal(stand.Id, alone?.Id);
        Assert.NotNull(beside);
        Assert.NotEqual(stand.Id, beside.Id);
    }

    [Fact]
    public void FindTaxiStartNode_OccupiedStand_FallsBackToATaxiNodeAheadNeverAnEmptyStand()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState taxiing = StalledPusher(layout);
        AircraftState parked = ParkedAt(layout, "SWA2436", "B732", OccupiedStand, 47.0);
        var heading = new TrueHeading(StalledPushHeadingDeg);

        GroundNode? beside = layout.FindTaxiStartNode(taxiing.Position, heading, TaxiStartTraffic.Of(taxiing, [NeighbourCandidate.From(parked)]));

        Assert.NotNull(beside);
        Assert.False(
            beside.Type is GroundNodeType.Parking or GroundNodeType.Helipad,
            $"the fallback start is stand node {beside.Id} ({beside.Name})"
        );
        double offNoseDeg = GeoMath.AbsBearingDifference(GeoMath.BearingTo(taxiing.Position, beside.Position), StalledPushHeadingDeg);
        Assert.True(offNoseDeg < 90.0, $"the fallback start {beside.Id} is {offNoseDeg:F0} deg off the nose, behind the aircraft");
    }

    [Fact]
    public void FindTaxiStartNode_EveryNodeAheadBlocked_FallsBackBehind()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState taxiing = StalledPusher(layout);
        AircraftState parked = ParkedAt(layout, "SWA2436", "B732", OccupiedStand, 47.0);
        List<AircraftState> blockersAhead = HeldOnTaxiNodesWithin(layout, taxiing, AirportGroundLayout.OccupiedStandStartSearchFt, aheadOnly: true);
        List<NeighbourCandidate> traffic = [NeighbourCandidate.From(parked), .. blockersAhead.Select(NeighbourCandidate.From)];

        GroundNode? start = layout.FindTaxiStartNode(taxiing.Position, new TrueHeading(StalledPushHeadingDeg), TaxiStartTraffic.Of(taxiing, traffic));

        Assert.NotNull(start);
        double offNoseDeg = GeoMath.AbsBearingDifference(GeoMath.BearingTo(taxiing.Position, start.Position), StalledPushHeadingDeg);
        Assert.True(offNoseDeg >= 90.0, $"with every node ahead held, the start {start.Id} is still {offNoseDeg:F0} deg off the nose");
    }

    [Fact]
    public void FindTaxiStartNode_HolderBesideItsEmptyStand_LeavesTheStandFree()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        // SWA2436 pushed off stand 23 and holds 250 ft clear of it, still naming the stand; the stand is empty.
        GroundNode stand = Stand(layout, OccupiedStand);
        AircraftState taxiing = StalledPusher(layout);
        LatLon heldAt = GeoMath.ProjectPoint(stand.Position, new TrueHeading(StalledPushBearingFromStandDeg + 90.0), 250.0 / GeoMath.FeetPerNm);
        AircraftState holder = OnGround(layout, "SWA2436", "B732", heldAt, 47.0);
        holder.Ground.ParkingSpot = OccupiedStand;
        Start(holder, new HoldingAfterPushbackPhase());

        GroundNode? start = layout.FindTaxiStartNode(
            taxiing.Position,
            new TrueHeading(StalledPushHeadingDeg),
            TaxiStartTraffic.Of(taxiing, [NeighbourCandidate.From(holder)])
        );

        Assert.Equal(stand.Id, start?.Id);
    }

    [Fact]
    public void TaxiFromBesideOccupiedStand_NoClearStart_ResponseNamesTheBlocker()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState taxiing = StalledPusher(layout);
        AircraftState parked = ParkedAt(layout, "SWA2436", "B732", OccupiedStand, 47.0);
        List<AircraftState> world =
        [
            taxiing,
            parked,
            .. HeldOnTaxiNodesWithin(layout, taxiing, AirportGroundLayout.OccupiedStandStartSearchFt, aheadOnly: false),
        ];

        CommandResult result = DispatchTaxiE(layout, taxiing, world);

        Assert.True(result.Success, $"TAXI $E was refused: {result.Message}");
        Assert.EndsWith($"(blocked by stand {OccupiedStand}, SWA2436)", result.Message);
    }

    [Fact]
    public void FollowRouteStartOf_OccupiedStandInTheWorld_DoesNotStartAtIt()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        GroundNode stand = Stand(layout, OccupiedStand);
        AircraftState taxiing = StalledPusher(layout);
        AircraftState parked = ParkedAt(layout, "SWA2436", "B732", OccupiedStand, 47.0);

        FollowRoutePlanner.RouteStart? start = FollowRoutePlanner.StartOf(
            layout,
            taxiing,
            requireAhead: true,
            () => [NeighbourCandidate.From(parked)]
        );

        Assert.NotNull(start);
        Assert.NotEqual(stand.Id, start.Value.Node.Id);
    }

    /// <summary>
    /// <c>FOLLOWG</c> through the command dispatcher plans with the world's aircraft: the joinability probe reads the
    /// dispatcher's aircraft list, and the follow route SWA919 drives after one tick, behind a lead taxiing B, does not start
    /// through stand 23, which SWA2436 occupies.
    /// </summary>
    [Fact]
    public void FollowGThroughTheDispatcher_OccupiedStandInTheWorld_FollowRouteAvoidsIt()
    {
        if (FollowCornerGeometry.StartTaxiingLead(TestContext.Current.TestOutputHelper!) is not { } run)
        {
            return;
        }

        GroundNode stand = Stand(run.Layout, OccupiedStand);
        AircraftState follower = StalledPusher(run.Layout);
        AircraftState parked = ParkedAt(run.Layout, "SWA2436", "B732", OccupiedStand, 47.0);
        run.Engine.World.AddAircraft(follower);
        run.Engine.World.AddAircraft(parked);
        int listReads = 0;

        CommandResult result = DispatchFollowG(
            run.Layout,
            follower,
            run.Lead.Callsign,
            run.Engine.FindAircraft,
            () =>
            {
                listReads++;
                return run.Engine.World.GetSnapshot();
            }
        );

        Assert.True(result.Success, $"FOLLOWG was refused: {result.Message}");
        Assert.True(listReads > 0, "the FOLLOWG joinability probe never read the dispatcher's aircraft list");

        run.Engine.TickOneSecond();

        FollowingPhase following = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        TaxiRoute route = Assert.IsType<TaxiRoute>(following.FollowRoute);
        Assert.DoesNotContain(route.Segments, s => (s.FromNodeId == stand.Id) || (s.ToNodeId == stand.Id));
    }

    /// <summary>
    /// An aircraft held at every taxi node within <paramref name="withinFt"/> of <paramref name="aircraft"/> (only those ahead
    /// of its nose when <paramref name="aheadOnly"/>), so no free-space leg to one of them is clear.
    /// </summary>
    private static List<AircraftState> HeldOnTaxiNodesWithin(AirportGroundLayout layout, AircraftState aircraft, double withinFt, bool aheadOnly)
    {
        List<AircraftState> held = [];
        foreach (GroundNode node in layout.Nodes.Values)
        {
            bool taxiNode = (node.Edges.Count > 0) && (node.Type is not (GroundNodeType.Parking or GroundNodeType.Helipad));
            double distFt = GeoMath.DistanceNm(aircraft.Position, node.Position) * GeoMath.FeetPerNm;
            bool ahead = GeoMath.AbsBearingDifference(GeoMath.BearingTo(aircraft.Position, node.Position), aircraft.TrueHeading.Degrees) < 90.0;
            if (taxiNode && (distFt <= withinFt + 1.0) && (ahead || !aheadOnly))
            {
                AircraftState blocker = OnGround(layout, $"HLD{node.Id}", "C172", node.Position, 0.0);
                Start(blocker, new HoldingInPositionPhase());
                held.Add(blocker);
            }
        }

        return held;
    }

    private static AirportGroundLayout? OakLayout() => new TestAirportGroundData().GetLayout("OAK");

    private static GroundNode Stand(AirportGroundLayout layout, string name) =>
        layout.FindParkingByName(name) ?? throw new InvalidOperationException($"OAK has no stand {name}");

    /// <summary>The stand's lead-in: the taxi nodes its parking node is joined to.</summary>
    private static IEnumerable<GroundNode> LeadIn(GroundNode stand) =>
        stand.Edges.Select(e => e.OtherNode(stand)).Where(n => n.Type is not (GroundNodeType.Parking or GroundNodeType.Helipad));

    /// <summary>SWA919 at rest where its push stopped, still carrying the stand it left.</summary>
    private static AircraftState StalledPusher(AirportGroundLayout layout)
    {
        LatLon position = GeoMath.ProjectPoint(
            Stand(layout, OccupiedStand).Position,
            new TrueHeading(StalledPushBearingFromStandDeg),
            StalledPushFromStandFt / GeoMath.FeetPerNm
        );
        AircraftState aircraft = OnGround(layout, "SWA919", "B737", position, StalledPushHeadingDeg);
        aircraft.Ground.ParkingSpot = "20";
        Start(aircraft, new HoldingAfterPushbackPhase());
        return aircraft;
    }

    private static AircraftState ParkedAt(AirportGroundLayout layout, string callsign, string type, string standName, double headingDeg)
    {
        AircraftState aircraft = OnGround(layout, callsign, type, Stand(layout, standName).Position, headingDeg);
        aircraft.Ground.ParkingSpot = standName;
        Start(aircraft, new AtParkingPhase());
        return aircraft;
    }

    private static AircraftState OnGround(AirportGroundLayout layout, string callsign, string type, LatLon position, double headingDeg)
    {
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = position,
            TrueHeading = new TrueHeading(headingDeg),
            TrueTrack = new TrueHeading(headingDeg),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    private static void Start(AircraftState aircraft, Phase phase)
    {
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty));
    }

    /// <summary>Dispatches <c>TAXI $E</c> to <paramref name="aircraft"/> in a world holding <paramref name="world"/>; its route.</summary>
    private static TaxiRoute TaxiE(AirportGroundLayout layout, AircraftState aircraft, List<AircraftState> world)
    {
        CommandResult result = DispatchTaxiE(layout, aircraft, world);

        Assert.True(result.Success, $"TAXI $E was refused: {result.Message}");
        return Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>Dispatches <c>TAXI $E</c> to <paramref name="aircraft"/> in a world holding <paramref name="world"/>; the result.</summary>
    private static CommandResult DispatchTaxiE(AirportGroundLayout layout, AircraftState aircraft, List<AircraftState> world)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse("TAXI $E");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);
        DispatchContext ctx = TestDispatch.Context(new Random(42), validateDctFixes: false, groundLayout: layout, listAircraft: () => world);

        return CommandDispatcher.DispatchCompound(compound, aircraft, ctx);
    }

    /// <summary>
    /// Dispatches <c>FOLLOWG <paramref name="leadCallsign"/></c> to <paramref name="aircraft"/> with the given aircraft lookup
    /// and aircraft list; the result.
    /// </summary>
    private static CommandResult DispatchFollowG(
        AirportGroundLayout layout,
        AircraftState aircraft,
        string leadCallsign,
        Func<string, AircraftState?> findAircraft,
        Func<IReadOnlyList<AircraftState>> listAircraft
    )
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse($"FOLLOWG {leadCallsign}");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);
        DispatchContext ctx = TestDispatch.Context(
            new Random(42),
            validateDctFixes: false,
            groundLayout: layout,
            findAircraft: findAircraft,
            listAircraft: listAircraft
        );

        return CommandDispatcher.DispatchCompound(compound, aircraft, ctx);
    }
}
