using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A taxiing aircraft declares arrival at a node up to the loose straight-arrival threshold (~91 ft) short of
/// it. When the next segment continues on the same line, the aircraft is still on that segment's centreline, only short
/// of its start, and keeps its taxi speed through the node. At KOAK taxiway B the node between the 176→175 and 175→174
/// edges (both 248.5°) read the aircraft as ~67 ft off the new segment and capped it at the re-acquire speed.
/// </summary>
public class TaxiCollinearNodeArrivalTests(ITestOutputHelper output)
{
    /// <summary>The navigator's loose straight-arrival threshold, in feet.</summary>
    private const double ArrivalWindowFt = GroundNavigator.NodeArrivalThresholdNm * GeoMath.FeetPerNm;

    /// <summary>How far past the collinear node (ft) the speed is watched.</summary>
    private const double WatchPastNodeFt = 200.0;

    /// <summary>How far (ft) west of the 28L/10R bar the aircraft starts, clear of the bar's own node.</summary>
    private const double BarStandoffFt = 30.0;

    /// <summary>The most (kt) the ground speed may drop below the speed held entering the arrival window.</summary>
    private const double MaxSpeedDropKts = 1.0;

    /// <summary>The two B edges meeting at the node are this collinear (deg) or better.</summary>
    private const double MaxCollinearDeg = 0.5;

    /// <summary>Both B edges at the node are at least this long (ft): long straights, not a junction stub.</summary>
    private const double MinEdgeFt = 600.0;

    private const int MaxTickSeconds = 120;

    /// <summary>
    /// Started from rest 30 ft west of the 28L/10R bar on B, on the edge running to the collinear node, the C172 holds its
    /// 20 kt taxi speed well before the arrival window. The B738 is still accelerating toward its 30 kt there (about 27 kt): an
    /// accelerating aircraft only gains speed through a node on a straight line, so the speed it held entering the window still
    /// pins the dip.
    /// </summary>
    [Theory]
    [InlineData("C172")]
    [InlineData("B738")]
    public void TaxiAlongB_ThroughCollinearNode175_KeepsTaxiSpeed(string type)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        (GroundNode bar, GroundNode node, GroundNode next) = CollinearBNodesEastOfSpot9(layout);
        var lineHeading = new TrueHeading(GeoMath.BearingTo(bar.Position, node.Position));
        LatLon start = GeoMath.ProjectPoint(bar.Position, lineHeading, BarStandoffFt / GeoMath.FeetPerNm);
        output.WriteLine($"B {bar.Id} (bar) -> {node.Id} -> {next.Id}, bearing {lineHeading.Degrees:F1}");

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-b-collinear-node",
                ScenarioName = "OAK B Collinear Node",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        AircraftState aircraft = SpawnAt(layout, start, lineHeading, type);
        engine.World.AddAircraft(aircraft);

        // Along the occupied B, a node reference names where the taxi ends: B's end ahead.
        GroundNode bEnd = TaxiStartsOnOccupiedTaxiwayTests.NodesAhead(bar, lineHeading.Degrees, "B").Last();
        string command = $"TAXI B #{bEnd.Id}";
        output.WriteLine(command);
        CommandResult taxi = engine.SendCommand(aircraft.Callsign, command);
        Assert.True(taxi.Success, $"'{command}' was refused: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        AssertRouteRunsThrough(route, node, next);

        double? entrySpeedKts = null;
        double previousSpeedKts = aircraft.GroundSpeed;
        double minSpeedKts = double.MaxValue;
        double alongFt = double.NegativeInfinity;
        for (int second = 1; (second <= MaxTickSeconds) && (alongFt < WatchPastNodeFt); second++)
        {
            engine.TickOneSecond();
            double distFt = GeoMath.DistanceNm(aircraft.Position, node.Position) * GeoMath.FeetPerNm;
            alongFt = GeoMath.AlongTrackDistanceNm(aircraft.Position, node.Position, lineHeading) * GeoMath.FeetPerNm;
            if ((entrySpeedKts is null) && (alongFt < 0.0) && (distFt <= ArrivalWindowFt))
            {
                entrySpeedKts = previousSpeedKts;
            }

            if (entrySpeedKts is not null)
            {
                minSpeedKts = Math.Min(minSpeedKts, aircraft.GroundSpeed);
            }

            output.WriteLine($"t={second}s along={alongFt:F0} ft GS={aircraft.GroundSpeed:F2} kt seg={route.CurrentSegmentIndex}");
            previousSpeedKts = aircraft.GroundSpeed;
        }

        Assert.True(alongFt >= WatchPastNodeFt, $"the {type} got only {alongFt:F0} ft past node {node.Id} in {MaxTickSeconds}s");
        Assert.NotNull(entrySpeedKts);
        output.WriteLine($"entry GS {entrySpeedKts:F2} kt, minimum to {WatchPastNodeFt:F0} ft past the node {minSpeedKts:F2} kt");
        Assert.True(
            minSpeedKts >= entrySpeedKts.Value - MaxSpeedDropKts,
            $"the {type} entered node {node.Id}'s arrival window at {entrySpeedKts:F2} kt and slowed to {minSpeedKts:F2} kt "
                + $"before it was {WatchPastNodeFt:F0} ft past it, on a straight line"
        );
    }

    /// <summary>
    /// The 28L/10R hold-short bar on B, the collinear <c>Node</c> west of it and <c>Next</c> (toward spot 9): the painted-map
    /// snap seats the bar on the end node of the two long straight B edges itself.
    /// </summary>
    private static (GroundNode Bar, GroundNode Node, GroundNode Next) CollinearBNodesEastOfSpot9(AirportGroundLayout layout)
    {
        GroundNode spot9 = Assert.IsType<GroundNode>(layout.FindSpotNodeByName("9"));
        List<(GroundNode Start, GroundNode Node, GroundNode Next)> candidates =
        [
            .. from next in StraightBNeighbours(spot9)
            from node in StraightBNeighbours(next)
            where node.Id != spot9.Id
            from start in StraightBNeighbours(node)
            where (start.Id != next.Id) && IsLongCollinearPair(start, node, next)
            select (start, node, next),
        ];
        Assert.True(candidates.Count == 1, $"spot 9 leads to {candidates.Count} pairs of long collinear B edges, expected one");
        (GroundNode edgeEnd, GroundNode collinear, GroundNode toward) = candidates[0];
        GroundNode bar = GroundNodeLookup.HoldShortBar(layout, "10R", "B", edgeEnd);
        Assert.True(
            bar.Id == edgeEnd.Id,
            $"the 28L/10R bar on B is node {bar.Id}, not the long collinear edges' end node {edgeEnd.Id} where the painted-map snap seats it"
        );
        return (bar, collinear, toward);
    }

    private static bool IsLongCollinearPair(GroundNode start, GroundNode node, GroundNode next)
    {
        double inFt = GeoMath.DistanceNm(start.Position, node.Position) * GeoMath.FeetPerNm;
        double outFt = GeoMath.DistanceNm(node.Position, next.Position) * GeoMath.FeetPerNm;
        double bendDeg = GeoMath.AbsBearingDifference(
            GeoMath.BearingTo(start.Position, node.Position),
            GeoMath.BearingTo(node.Position, next.Position)
        );
        return (bendDeg <= MaxCollinearDeg) && (inFt >= MinEdgeFt) && (outFt >= MinEdgeFt);
    }

    private static IEnumerable<GroundNode> StraightBNeighbours(GroundNode node) =>
        node.Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("B")).Select(e => e.OtherNode(node));

    /// <summary>
    /// The route passes <paramref name="node"/> — starting there or arriving at it — and runs on to <paramref name="next"/> as
    /// its next segment.
    /// </summary>
    private static void AssertRouteRunsThrough(TaxiRoute route, GroundNode node, GroundNode next)
    {
        int outOf = route.Segments.FindIndex(s => (s.FromNodeId == node.Id) && (s.ToNodeId == next.Id));
        Assert.True(outOf >= 0, $"the route has no {node.Id} -> {next.Id} segment");
        Assert.True(
            (outOf == 0) || (route.Segments[outOf - 1].ToNodeId == node.Id),
            $"the route does not pass {node.Id} before running on to {next.Id}"
        );
    }

    private static AircraftState SpawnAt(AirportGroundLayout layout, LatLon position, TrueHeading heading, string type)
    {
        var aircraft = new AircraftState
        {
            Callsign = $"N{type}",
            AircraftType = type,
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = "B";
        return aircraft;
    }
}
