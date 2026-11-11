using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A plain <c>TAXI $spot</c> / <c>TAXI @gate</c> from inside the ramp whose taxi starts on the destination itself while the
/// aircraft stands 50 ft or more from it. On the destination's own lane facing it, the aircraft rolls straight (or along the
/// lane's fillet arc) onto it and reads back "Taxi to spot 2" / "Taxi to gate A10". Otherwise it is re-routed from the
/// destination's nearest graph neighbour inside the ramp, or refused with its phase unchanged when no such route plans.
/// Every pose is a real one pinned from the layouts (B738, holding in position).
/// </summary>
public class RampZeroSegmentRollTests(ITestOutputHelper output)
{
    private const string Spot2Refusal = "Unable, need a route to spot 2. To auto-route it: TAXIAUTO $2";

    /// <summary>On M2 60 ft short of spot 2 from the #15 side, nose on the spot with M2's flow: one free-space roll onto it.</summary>
    [Fact]
    public void Row01_OnLaneFacingSpot2_FlowAllows_RollsStraightOntoIt()
    {
        if (BuildEngine("SFO") is not { } ground)
        {
            return;
        }

        GroundNode spot2 = Assert.IsType<GroundNode>(ground.Layout.FindSpotNodeByName("2"));
        AircraftState aircraft = Spawn(ground, (new LatLon(37.6083783, -122.3865321), new TrueHeading(117.9)));

        CommandResult result = Taxi(aircraft, Spot("2"), ground.Layout);

        Assert.Equal("Taxi to spot 2", result.Message);
        Assert.Equal("Taxi to spot 2", result.PilotReadback?.Speech.Terminal, ignoreCase: true);
        AssertStraightRoll(ground.Layout, aircraft, spot2);
        AssertDrivesOnto(ground, aircraft, spot2, typeof(HoldingInPositionPhase));
    }

    /// <summary>Row 1's roll cleared with EXP reads back "without delay", as every expedited taxi does.</summary>
    [Fact]
    public void Row01b_ExpeditedRoll_ReadsBackWithoutDelay()
    {
        if (LoadLayout("SFO") is not { } layout)
        {
            return;
        }

        GroundNode spot2 = Assert.IsType<GroundNode>(layout.FindSpotNodeByName("2"));
        AircraftState aircraft = MakeAircraft(layout, (new LatLon(37.6083783, -122.3865321), new TrueHeading(117.9)));

        CommandResult result = Taxi(aircraft, new TaxiCommand([], [], Expedite: true, DestinationSpot: "2"), layout);

        Assert.Equal("Taxi to spot 2" + CommandDescriber.WithoutDelaySuffix, result.Message);
        AssertStraightRoll(layout, aircraft, spot2);
    }

    /// <summary>On T7 60 ft short of spot 21, facing it: one roll along T7 that never drives A.</summary>
    [Fact]
    public void Row02_OnTaxilaneToSpot21_RollsStraightAndNeverDrivesA()
    {
        if (LoadLayout("SFO") is not { } layout)
        {
            return;
        }

        GroundNode spot21 = Assert.IsType<GroundNode>(layout.FindSpotNodeByName("21"));
        AircraftState aircraft = MakeAircraft(layout, (new LatLon(37.6207884, -122.3851673), new TrueHeading(27.8)));

        CommandResult result = Taxi(aircraft, Spot("21"), layout);

        Assert.Equal("Taxi to spot 21", result.Message);
        TaxiRoute route = AssertStraightRoll(layout, aircraft, spot21);
        Assert.DoesNotContain(route.Segments, s => SegmentExpander.EdgeNames(s.Edge.Edge).Contains("A", StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>On spot 5B's RAMP lead-in 60 ft out, facing away from it: re-routed from its neighbour inside the ramp.</summary>
    [Fact]
    public void Row03_OnLaneFacingAway_Spot5B_ConfinedNeighbourRoute() =>
        AssertConfinedNeighbourRoute("5B", (new LatLon(37.6153571, -122.3812477), new TrueHeading(233.1)));

    /// <summary>On spot 9's RAMP lane 60 ft past it, facing further away: re-routed from its neighbour inside the ramp.</summary>
    [Fact]
    public void Row04_OnLanePastTheSpotFacingAway_Spot9_ConfinedNeighbourRoute() =>
        AssertConfinedNeighbourRoute("9", (new LatLon(37.6194423, -122.3921393), new TrueHeading(209.1)));

    /// <summary>On M2 60 ft past spot 2 (the #1288 side), nose on it against M2's one-way flow: no route inside the ramp, refused.</summary>
    [Fact]
    public void Row05_OnLaneFacingSpot2_AgainstOneWay_Refused() => AssertSpot2Refused((new LatLon(37.6082242, -122.3861649), new TrueHeading(297.9)));

    /// <summary>40 ft abeam M2, 60 ft short of spot 2: off the lane, and no route inside the ramp, refused.</summary>
    [Fact]
    public void Row06_FortyFeetAbeamTheLane_Spot2_Refused() => AssertSpot2Refused((new LatLon(37.6082813, -122.3865969), new TrueHeading(117.9)));

    /// <summary>On M2 60 ft short of spot 2, 30° off the lane: inside the 45° tolerance, rolls.</summary>
    [Fact]
    public void Row07_ThirtyDegreesOff_Spot2_Rolls()
    {
        if (LoadLayout("SFO") is not { } layout)
        {
            return;
        }

        GroundNode spot2 = Assert.IsType<GroundNode>(layout.FindSpotNodeByName("2"));
        AircraftState aircraft = MakeAircraft(layout, (new LatLon(37.6083783, -122.3865321), new TrueHeading(147.9)));

        CommandResult result = Taxi(aircraft, Spot("2"), layout);

        Assert.Equal("Taxi to spot 2", result.Message);
        AssertStraightRoll(layout, aircraft, spot2);
    }

    /// <summary>On M2 60 ft short of spot 2, 60° off the lane: no roll, and no route inside the ramp, refused.</summary>
    [Fact]
    public void Row08_SixtyDegreesOff_Spot2_Refused() => AssertSpot2Refused((new LatLon(37.6083783, -122.3865321), new TrueHeading(177.9)));

    /// <summary>On gate A10's straight RAMP lead-in 60 ft out, facing it: one roll onto the stand, where it parks.</summary>
    [Fact]
    public void Row09_OnStraightLeadInFacingGateA10_RollsOntoTheStandAndParks()
    {
        if (BuildEngine("SFO") is not { } ground)
        {
            return;
        }

        GroundNode a10 = Assert.IsType<GroundNode>(ground.Layout.FindParkingByName("A10"));
        AircraftState aircraft = Spawn(ground, (new LatLon(37.6121902, -122.3908470), new TrueHeading(118.2)));

        CommandResult result = Taxi(aircraft, Gate("A10"), ground.Layout);

        Assert.Equal("Taxi to gate A10", result.Message);
        Assert.Equal("Taxi to gate A10", result.PilotReadback?.Speech.Terminal, ignoreCase: true);
        AssertStraightRoll(ground.Layout, aircraft, a10);
        AssertDrivesOnto(ground, aircraft, a10, typeof(AtParkingPhase));
        Assert.Equal("A10", aircraft.Ground.ParkingSpot);
    }

    /// <summary>
    /// ATL gate GA11, 60 ft out on its fillet-arc lead-in, facing along it: the roll follows the arc onto the stand and
    /// never re-routes over A.
    /// </summary>
    [Fact]
    public void Row10_OnFilletArcLeadInToGateGA11_FollowsTheArcOntoTheStand()
    {
        if (BuildEngine("ATL") is not { } ground)
        {
            return;
        }

        GroundNode ga11 = Assert.IsType<GroundNode>(ground.Layout.FindParkingByName("GA11"));
        AircraftState aircraft = Spawn(ground, (new LatLon(33.6520304, -84.4260072), new TrueHeading(73.2)));

        CommandResult result = Taxi(aircraft, Gate("GA11"), ground.Layout);

        Assert.Equal("Taxi to gate GA11", result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {Describe(route)}");
        TaxiRouteSegment last = route.Segments[^1];
        Assert.IsType<GroundArc>(last.Edge.Edge);
        Assert.Equal(ga11.Id, last.ToNodeId);
        Assert.InRange(route.Segments.Count, 1, 2);
        AssertStaysInsideTheRamp(ground.Layout, route);
        AssertDrivesOnto(ground, aircraft, ga11, typeof(AtParkingPhase));
        Assert.Equal("GA11", aircraft.Ground.ParkingSpot);
    }

    /// <summary>
    /// SFO gate SIG10, 60% of the way round its 135 ft T421/RAMP fillet-arc lead-in and 54 ft from the stand, facing along the
    /// arc: the roll is the arc alone, with no free-space leg back to its start, and the aircraft never turns away from the stand.
    /// </summary>
    [Fact]
    public void Row10b_PastTheMiddleOfAFilletArcLeadIn_GateSIG10_FollowsTheArcWithoutTurningBack()
    {
        if (BuildEngine("SFO") is not { } ground)
        {
            return;
        }

        GroundNode sig10 = Assert.IsType<GroundNode>(ground.Layout.FindParkingByName("SIG10"));
        var position = new LatLon(37.6280076, -122.3835011);
        GroundArc arc = sig10.Edges.OfType<GroundArc>().MinBy(a => a.Locate(sig10, position).OffFt)!;
        AircraftState aircraft = Spawn(ground, (position, new TrueHeading(114.3)));

        CommandResult result = Taxi(aircraft, Gate("SIG10"), ground.Layout);

        Assert.Equal("Taxi to gate SIG10", result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {Describe(route)}");
        TaxiRouteSegment along = Assert.Single(route.Segments);
        Assert.Same(arc, along.Edge.Edge);
        Assert.Equal(sig10.Id, along.ToNodeId);
        AssertDrivesOnto(ground, aircraft, sig10, typeof(AtParkingPhase));
    }

    /// <summary>Under 50 ft from spot 2 on M2: unchanged, the aircraft holds where it is with no route to drive.</summary>
    [Fact]
    public void Row11_UnderFiftyFeetFromSpot2_HoldsInPlace()
    {
        if (LoadLayout("SFO") is not { } layout)
        {
            return;
        }

        GroundNode spot2 = Assert.IsType<GroundNode>(layout.FindSpotNodeByName("2"));
        LatLon position = GeoMath.ProjectPoint(spot2.Position, new TrueHeading(297.9), 30.0 / GeoMath.FeetPerNm);
        AircraftState aircraft = MakeAircraft(layout, (position, new TrueHeading(117.9)));

        CommandResult result = Taxi(aircraft, Spot("2"), layout);

        Assert.Equal("Taxi via $2", result.Message);
        Assert.Empty(Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute).Segments);
        Assert.IsType<HoldingInPositionPhase>(aircraft.Phases?.CurrentPhase);
    }

    /// <summary>
    /// <c>TAXIAUTO $2</c> from row 1's pose is not confined to the ramp: unchanged, it takes the unconfined re-route from the
    /// spot's nearest neighbour, not the roll.
    /// </summary>
    [Fact]
    public void Row12_TaxiAutoFromRow1Pose_KeepsTheUnconfinedNeighbourRoute()
    {
        if (LoadLayout("SFO") is not { } layout)
        {
            return;
        }

        GroundNode spot2 = Assert.IsType<GroundNode>(layout.FindSpotNodeByName("2"));
        AircraftState aircraft = MakeAircraft(layout, (new LatLon(37.6083783, -122.3865321), new TrueHeading(117.9)));

        CommandResult result = GroundCommandHandler.TryTaxiAuto(aircraft, new TaxiAutoCommand(null, null, "2", Expedite: false), layout, false);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {Describe(route)}");
        Assert.True(route.Segments.Count > 2, "the unconfined neighbour route, not the roll");
        Assert.Equal(spot2.Id, route.Segments[^1].ToNodeId);
    }

    private void AssertConfinedNeighbourRoute(string spotName, (LatLon Position, TrueHeading Heading) pose)
    {
        if (LoadLayout("SFO") is not { } layout)
        {
            return;
        }

        GroundNode spot = Assert.IsType<GroundNode>(layout.FindSpotNodeByName(spotName));
        AircraftState aircraft = MakeAircraft(layout, pose);

        CommandResult result = Taxi(aircraft, Spot(spotName), layout);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("Taxi to spot", result.Message, StringComparison.Ordinal);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {Describe(route)}");
        Assert.Equal(spot.Id, route.Segments[^1].ToNodeId);
        Assert.Contains(route.Segments, s => (s.FromNodeId >= 0) && (s.ToNodeId == spot.Id));
        AssertStaysInsideTheRamp(layout, route);
        AssertNoRunway(layout, route);
    }

    private void AssertSpot2Refused((LatLon Position, TrueHeading Heading) pose)
    {
        if (LoadLayout("SFO") is not { } layout)
        {
            return;
        }

        AircraftState aircraft = MakeAircraft(layout, pose);

        CommandResult result = Taxi(aircraft, Spot("2"), layout);

        Assert.False(result.Success, "refused");
        Assert.Equal(Spot2Refusal, result.Message);
        Assert.IsType<HoldingInPositionPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>The assigned route is the one free-space segment from the aircraft onto <paramref name="destination"/>.</summary>
    private TaxiRoute AssertStraightRoll(AirportGroundLayout layout, AircraftState aircraft, GroundNode destination)
    {
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {Describe(route)}");
        TaxiRouteSegment roll = Assert.Single(route.Segments);
        Assert.True(VirtualNode.IsVirtualEdge(roll.Edge.Edge), "a free-space leg");
        Assert.Equal("RAMP", roll.TaxiwayName);
        Assert.Equal(destination.Id, roll.ToNodeId);
        AssertStaysInsideTheRamp(layout, route);
        return route;
    }

    /// <summary>Ticks until the taxi ends in <paramref name="terminal"/>, never drifting further from the destination than it started.</summary>
    private void AssertDrivesOnto(SfoGround ground, AircraftState aircraft, GroundNode destination, Type terminal)
    {
        double startFt = DistanceFt(aircraft.Position, destination.Position);
        double furthestFt = startFt;
        int endedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => aircraft.Phases?.CurrentPhase?.GetType() == terminal,
            300,
            _ => furthestFt = Math.Max(furthestFt, DistanceFt(aircraft.Position, destination.Position))
        );
        output.WriteLine(
            $"ended at {endedAt}s in {aircraft.Phases?.CurrentPhase?.GetType().Name}, {DistanceFt(aircraft.Position, destination.Position):F1} ft "
                + $"from the destination (started {startFt:F1} ft, furthest {furthestFt:F1} ft)"
        );
        Assert.True(endedAt > 0, $"the taxi never ended in {terminal.Name}");
        Assert.True(furthestFt <= startFt + 1.0, $"drifted {furthestFt:F1} ft from the destination, beyond the {startFt:F1} ft it started at");
    }

    private void AssertStaysInsideTheRamp(AirportGroundLayout layout, TaxiRoute route)
    {
        var classification = MovementAreaClassification.For(layout);
        var driven = route
            .Segments.Where(s => !s.Edge.Edge.IsRunwayCenterline)
            .SelectMany(s => SegmentExpander.EdgeNames(s.Edge.Edge))
            .Where(n => (n.Length > 0) && classification.IsMovementArea(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        output.WriteLine($"movement-area taxiways driven: [{string.Join(" ", driven)}]");
        Assert.Empty(driven);
    }

    private static void AssertNoRunway(AirportGroundLayout layout, TaxiRoute route)
    {
        Assert.DoesNotContain(
            route.Segments,
            s =>
                s.Edge.Edge.IsRunwayCenterline
                || (layout.Nodes.TryGetValue(s.ToNodeId, out GroundNode? n) && (n.Type == GroundNodeType.RunwayHoldShort))
        );
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason is HoldShortReason.RunwayCrossing or HoldShortReason.DestinationRunway);
    }

    private CommandResult Taxi(AircraftState aircraft, TaxiCommand taxi, AirportGroundLayout layout)
    {
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");
        return result;
    }

    private static TaxiCommand Spot(string name) => new([], [], Expedite: false, DestinationSpot: name);

    private static TaxiCommand Gate(string name) => new([], [], Expedite: false, DestinationParking: name);

    private static string Describe(TaxiRoute route) =>
        $"[{route.FormatTaxiwaySequence()}] "
        + string.Join(" ", route.Segments.Select(s => $"#{s.FromNodeId}->#{s.ToNodeId}({s.TaxiwayName}{(s.Edge.Edge is GroundArc ? " arc" : "")})"));

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    private static AirportGroundLayout? LoadLayout(string airport)
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb is null ? null : new TestAirportGroundData().GetLayout(airport);
    }

    private SfoGround? BuildEngine(string airport)
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is null) || (groundData.GetLayout(airport) is not { } layout))
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-ramp-zero-segment-roll",
                ScenarioName = "Ramp zero-segment roll",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = airport,
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        return new SfoGround(engine, layout);
    }

    private static AircraftState Spawn(SfoGround ground, (LatLon Position, TrueHeading Heading) pose) =>
        SfoGroundHarness.SpawnAt(
            ground,
            "WJA44",
            "B738",
            (VirtualNode.Create(pose.Position.Lat, pose.Position.Lon), pose.Heading),
            new HoldingInPositionPhase()
        );

    private static AircraftState MakeAircraft(AirportGroundLayout layout, (LatLon Position, TrueHeading Heading) pose)
    {
        var aircraft = new AircraftState
        {
            Callsign = "WJA44",
            AircraftType = "B738",
            Position = pose.Position,
            TrueHeading = pose.Heading,
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = layout.AirportId, Destination = "YYC" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }
}
