using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-708 (#1193): at KOAK a B737/B738 given a bare <c>PUSH TE</c> off gate 31 or 29 is pushed straight back across TE
/// and held on it with its nose still on the stand heading, 63° (gate 31) or 77° (gate 29) off TE's bearing toward T,
/// so taxiing on toward T, U or spot E is a 117° or 104° right turn: a corner, short of the 135° at which the
/// navigator treats an entry as a reversal. The controller's <c>TAXI T U W RWY 30</c>, <c>TAXI $E</c> and
/// <c>TAXI TE $E</c> were refused "no room to turn around on TE" while <c>TAXIAUTO 30</c> flew the same corner.
/// </summary>
public class OakPushTeThenTaxiTests(ITestOutputHelper output)
{
    private const string Callsign = "SWA1391";

    /// <summary>How close to TE's bearing toward T the aircraft's heading must come once it taxis.</summary>
    private const double AlongTeToleranceDeg = 20.0;

    /// <summary>How close to TE's bearing toward T the aircraft's heading must be once it has stopped on spot E.</summary>
    private const double StoppedAlongTeToleranceDeg = 10.0;

    /// <summary>How far (ft) past the spot node, or beyond half a length short of it, the reference point may stop.</summary>
    private const double SpotToleranceFt = 3.0;

    private const int TaxiWindowSeconds = 60;

    /// <summary>The longest (s) the taxi to spot E may take to complete.</summary>
    private const int SpotRouteWindowSeconds = 120;

    /// <summary>
    /// The SWA1391 and SWA5456 clearances after their bare <c>PUSH TE</c>: each is accepted, and the aircraft turns the
    /// corner onto TE toward T and taxis.
    /// </summary>
    [Theory]
    [InlineData("31", "B737", "TAXI T U W RWY 30")]
    [InlineData("29", "B738", "TAXI $E")]
    [InlineData("31", "B737", "TAXI TE $E")]
    public void PushTeAcrossTheTaxilane_ControllerTaxiTowardT_IsAcceptedAndTurnsTheCorner(string stand, string type, string command)
    {
        if (PushedOntoTe(stand, type) is not { } pushed)
        {
            return;
        }

        (SimulationEngine engine, AirportGroundLayout layout, AircraftState aircraft) = pushed;
        double teTowardTDeg = TeTowardTDeg(layout, aircraft);
        double cornerDeg = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, teTowardTDeg);
        output.WriteLine(
            $"held on TE at ({aircraft.Position.Lat:F7},{aircraft.Position.Lon:F7}) heading {aircraft.TrueHeading.Degrees:F1}; "
                + $"TE toward T {teTowardTDeg:F1}, a {cornerDeg:F1}° turn"
        );
        Assert.InRange(cornerDeg, 90.0, GroundNavigator.ReversalEntryThresholdDeg - 1.0);

        LatLon heldAt = aircraft.Position;
        CommandResult result = engine.SendCommand(Callsign, command);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, $"'{command}' after PUSH TE off gate {stand} was refused: {result.Message}");
        Assert.NotNull(aircraft.Ground.AssignedTaxiRoute);

        double closestDeg = double.MaxValue;
        for (int t = 1; t <= TaxiWindowSeconds; t++)
        {
            engine.TickOneSecond();
            closestDeg = Math.Min(closestDeg, GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, teTowardTDeg));
        }

        double movedFt = GeoMath.DistanceNm(heldAt, aircraft.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"after {TaxiWindowSeconds} s: moved {movedFt:F1} ft, closest heading to TE toward T {closestDeg:F1}°");
        Assert.True(
            closestDeg <= AlongTeToleranceDeg,
            $"the heading never came within {AlongTeToleranceDeg}° of TE toward T (closest {closestDeg:F1}°)"
        );
        Assert.True(movedFt >= 30.0, $"the aircraft moved only {movedFt:F1} ft in {TaxiWindowSeconds} s");
    }

    /// <summary>
    /// The push off gate 31 leaves the B737's centroid within half a length of spot E, the first node of a
    /// <c>TAXI TE $E</c> route. The nose-at-spot stop must not end that route before the navigator has flown the opening
    /// corner onto TE: one second after the clearance the aircraft is still taxiing, and rolling. Run to completion, the
    /// route stops it lined up along TE toward T with its reference point short of spot E's node, within half a length
    /// of it. The push leaves the reference point less than half a length behind the spot along TE, so once lined up its
    /// nose is already past the marking: the stop cannot hold the nose short of it, only end the route as the turn ends.
    /// </summary>
    [Fact]
    public void PushTeOntoSpotE_TaxiTeE_FliesTheCornerBeforeTheSpotStop()
    {
        if (PushedOntoTe("31", "B737") is not { } pushed)
        {
            return;
        }

        (SimulationEngine engine, AirportGroundLayout layout, AircraftState aircraft) = pushed;
        double teTowardTDeg = TeTowardTDeg(layout, aircraft);
        GroundNode spotE = Assert.IsType<GroundNode>(layout.FindSpotNodeByName("E"));
        output.WriteLine(
            $"held {GeoMath.DistanceNm(aircraft.Position, spotE.Position) * GeoMath.FeetPerNm:F1} ft from spot E, "
                + $"{GeoMath.AlongTrackDistanceNm(spotE.Position, aircraft.Position, new TrueHeading(teTowardTDeg)) * GeoMath.FeetPerNm:F1} ft "
                + "of it ahead along TE toward T"
        );
        CommandResult result = engine.SendCommand(Callsign, "TAXI TE $E");
        Assert.True(result.Success, $"'TAXI TE $E' after PUSH TE off gate 31 was refused: {result.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);

        engine.TickOneSecond();

        output.WriteLine($"after 1 s: phase {aircraft.Phases?.CurrentPhase?.GetType().Name}, speed {aircraft.IndicatedAirspeed:F2} kt");
        Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(
            aircraft.IndicatedAirspeed > 0.0,
            $"the aircraft is not rolling one second after the clearance ({aircraft.IndicatedAirspeed:F2} kt)"
        );

        int completedAt = SfoGroundHarness.TickUntil(engine, () => route.IsComplete, SpotRouteWindowSeconds, null);
        Assert.True(completedAt > 0, $"the taxi to spot E did not complete within {SpotRouteWindowSeconds} s");
        double halfLengthFt = AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0;
        double spotAheadFt = GeoMath.AlongTrackDistanceNm(spotE.Position, aircraft.Position, aircraft.TrueHeading) * GeoMath.FeetPerNm;
        double offTeDeg = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, teTowardTDeg);
        output.WriteLine(
            $"complete after {completedAt} more s at ({aircraft.Position.Lat:F7},{aircraft.Position.Lon:F7}) heading "
                + $"{aircraft.TrueHeading.Degrees:F1}, {offTeDeg:F1}° off TE toward T; spot E {spotAheadFt:F1} ft ahead of the "
                + $"reference point, half-length {halfLengthFt:F1} ft"
        );
        Assert.True(spotAheadFt >= -SpotToleranceFt, $"the reference point stopped {-spotAheadFt:F1} ft past spot E's node");
        Assert.True(
            spotAheadFt <= (halfLengthFt + SpotToleranceFt),
            $"the reference point stopped {spotAheadFt:F1} ft short of spot E's node, more than half a length"
        );
        Assert.True(offTeDeg <= StoppedAlongTeToleranceDeg, $"stopped {offTeDeg:F1}° off TE toward T");
    }

    /// <summary>
    /// The bearing of TE toward T from <paramref name="aircraft"/>, held mid-way along a TE edge: toward that edge's end
    /// whose walk along TE meets T (<see cref="TeLeadsToT"/>).
    /// </summary>
    private static double TeTowardTDeg(AirportGroundLayout layout, AircraftState aircraft)
    {
        GroundEdge onTe = Assert.IsType<GroundEdge>(layout.FindMidEdgeTaxiStart(aircraft.Position));
        Assert.Equal("TE", onTe.TaxiwayName);
        GroundNode towardT = Assert.Single(onTe.Nodes, n => TeLeadsToT(n, onTe));
        return GeoMath.BearingTo(aircraft.Position, towardT.Position);
    }

    /// <summary>
    /// Whether a walk from <paramref name="end"/> over TE's edges, away from <paramref name="onTe"/>, reaches a node an edge
    /// named T leaves.
    /// </summary>
    private static bool TeLeadsToT(GroundNode end, GroundEdge onTe)
    {
        HashSet<int> visited = [end.Id, onTe.OtherNode(end).Id];
        Queue<GroundNode> frontier = new([end]);
        while (frontier.TryDequeue(out GroundNode? node))
        {
            if (node.Edges.Any(e => e.MatchesTaxiway("T")))
            {
                return true;
            }

            foreach (IGroundEdge edge in node.Edges.Where(e => !ReferenceEquals(e, onTe) && e.MatchesTaxiway("TE")))
            {
                GroundNode next = edge.OtherNode(node);
                if (visited.Add(next.Id))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// An engine over the committed KOAK layout with <paramref name="type"/> pushed off <paramref name="stand"/> by a bare
    /// <c>PUSH TE</c> and held.
    /// </summary>
    private (SimulationEngine Engine, AirportGroundLayout Layout, AircraftState Aircraft)? PushedOntoTe(string stand, string type)
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is null) || (groundData.GetLayout("OAK") is not { } layout))
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-push-te-then-taxi",
                ScenarioName = "OAK PUSH TE then TAXI",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        GroundNode parking = layout.FindParkingByName(stand) ?? throw new InvalidOperationException($"OAK layout has no parking named '{stand}'");
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = type,
            Position = parking.Position,
            TrueHeading = parking.TrueHeading ?? throw new InvalidOperationException($"OAK stand {stand} has no heading"),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.ParkingSpot = stand;
        engine.World.AddAircraft(aircraft);

        CommandResult push = engine.SendCommand(Callsign, "PUSH TE");
        output.WriteLine($"gate {stand} {type} PUSH TE: {push.Success} — {push.Message}");
        Assert.True(push.Success, push.Message);
        int heldAfter = SfoGroundHarness.TickUntil(engine, () => aircraft.Phases?.CurrentPhase is HoldingAfterPushbackPhase, 600, null);
        Assert.True(heldAfter > 0, $"gate {stand}'s PUSH TE never came to rest in HoldingAfterPushbackPhase");
        return (engine, layout, aircraft);
    }
}
