using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>KOAK taxiway B geometry and aircraft for the <c>FOLLOWG</c> planner and command tests.</summary>
internal static class KoakFollowGeometry
{
    internal const string AirportId = "OAK";

    /// <summary>A lead taxiing on B with its route and trail, on a running engine.</summary>
    internal sealed record LeadRun(SimulationEngine Engine, AirportGroundLayout Layout, List<GroundNode> Chain, AircraftState Lead);

    internal static AirportGroundLayout? LoadLayout(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout(AirportId) is not { } layout)
        {
            output.WriteLine("SKIP: KOAK layout unavailable");
            return null;
        }

        return layout;
    }

    /// <summary>
    /// A C560 spawned on B at <c>Chain[3]</c> facing the 28R bar, cleared <c>TAXI B W 30</c> (crossings pre-cleared) and ticked
    /// until its trail holds at least two edges.
    /// </summary>
    internal static LeadRun? StartTaxiingLead(ITestOutputHelper output)
    {
        if (NewEngine(output, autoCross: true) is not { } setup)
        {
            return null;
        }

        (SimulationEngine engine, AirportGroundLayout layout) = setup;
        List<GroundNode> chain = BChain(layout);
        AircraftState lead = AddTaxiing(setup, "N1LED", "C560", (chain[3], chain[2]), "TAXI B W 30");
        for (int second = 0; (second < 120) && (lead.Ground.TaxiEdgeTrail.Edges.Count < 2); second++)
        {
            engine.TickOneSecond();
        }

        Assert.True(lead.Ground.TaxiEdgeTrail.Edges.Count >= 2, "the lead's trail never reached two edges");
        output.WriteLine($"lead route {lead.Ground.AssignedTaxiRoute?.ToSummary()}, segment {lead.Ground.AssignedTaxiRoute?.CurrentSegmentIndex}");
        return new LeadRun(engine, layout, chain, lead);
    }

    /// <summary>An engine over the committed KOAK layout, or null when the layout is unavailable.</summary>
    internal static (SimulationEngine Engine, AirportGroundLayout Layout)? NewEngine(ITestOutputHelper output, bool autoCross)
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout(AirportId) is not { } layout)
        {
            output.WriteLine("SKIP: KOAK layout unavailable");
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-follow-planner",
                ScenarioName = "OAK follow planner",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = AirportId,
                AutoCrossRunway = autoCross,
            },
        };
        return (engine, layout);
    }

    /// <summary>
    /// An aircraft added to <paramref name="setup"/>'s engine at <c>pose.At</c>, facing <c>pose.Toward</c>, on its layout, and
    /// sent <paramref name="taxi"/>.
    /// </summary>
    internal static AircraftState AddTaxiing(
        (SimulationEngine Engine, AirportGroundLayout Layout) setup,
        string callsign,
        string type,
        (GroundNode At, GroundNode Toward) pose,
        string taxi
    )
    {
        AircraftState aircraft = Spawn(callsign, type, pose.At.Position, Facing(pose.At, pose.Toward));
        aircraft.Ground.Layout = setup.Layout;
        setup.Engine.World.AddAircraft(aircraft);
        CommandResult result = setup.Engine.SendCommand(callsign, taxi);
        Assert.True(result.Success, result.Message);
        return aircraft;
    }

    /// <summary>
    /// Straight B nodes going north from the near 28R holding position, one edge apart: <c>[0]</c> is the bar, each next node
    /// one B edge farther from the runway.
    /// </summary>
    internal static List<GroundNode> BChain(AirportGroundLayout layout)
    {
        List<GroundNode> bar28R = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "B");
        GroundNode bar28L = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28L", "B")[0];
        GroundNode current = bar28R.OrderByDescending(node => GeoMath.DistanceNm(node.Position, bar28L.Position)).First();
        List<GroundNode> chain = [current];
        GroundNode? previous = null;
        while (chain.Count < 7)
        {
            GroundNode here = current;
            GroundNode? next = here
                .Edges.Where(edge => edge.MatchesTaxiway("B") && !edge.IsRunwayCenterline)
                .Select(edge => edge.OtherNode(here))
                .Where(node => node.Id != previous?.Id)
                .OrderByDescending(node => GeoMath.DistanceNm(node.Position, bar28L.Position))
                .FirstOrDefault();
            if (next is null)
            {
                string edges = string.Join(", ", here.Edges.Select(e => e.TaxiwayName));
                Assert.Fail($"B ends {chain.Count} nodes north of the 28R bar at #{here.Id}: edges [{edges}]");
            }

            previous = here;
            current = next;
            chain.Add(next);
        }

        return chain;
    }

    internal static GroundEdge EdgeBetween(GroundNode a, GroundNode b) => a.Edges.OfType<GroundEdge>().First(edge => edge.OtherNode(a).Id == b.Id);

    internal static TrueHeading Facing(GroundNode from, GroundNode to) => new(GeoMath.BearingTo(from.Position, to.Position));

    internal static LatLon Between(LatLon a, LatLon b, double fraction) =>
        new(a.Lat + ((b.Lat - a.Lat) * fraction), a.Lon + ((b.Lon - a.Lon) * fraction));

    /// <summary>How far (ft) from every runway centreline a pose counts as clear of the runways' hold lines.</summary>
    internal const double ClearOfRunwayFt = 500.0;

    /// <summary>
    /// An aircraft at the midpoint of a straight segment of <paramref name="lead"/>'s route, three segments or more ahead of it,
    /// facing back along it, more than <see cref="ClearOfRunwayFt"/> from every runway centreline: clear of every runway's hold
    /// lines, where a follow with no plan would drive a clearing route off the runway instead of holding.
    /// </summary>
    internal static AircraftState SpawnOnRouteAhead(AircraftState lead, string callsign, string type)
    {
        TaxiRoute route = Assert.IsType<TaxiRoute>(lead.Ground.AssignedTaxiRoute);
        List<RunwayInfo> runways = [.. RunwayOccupancy.AirportRunways(AirportId)];
        Assert.NotEmpty(runways);
        TaxiRouteSegment ahead = route
            .Segments.Skip(route.CurrentSegmentIndex + 3)
            .First(s =>
                (s.Edge.Edge is GroundEdge)
                && !s.Edge.Edge.IsRunwayCenterline
                && (s.Edge.DistanceNm * GeoMath.FeetPerNm >= 100.0)
                && runways.All(r => ClearOf(r, Between(s.Edge.FromNode.Position, s.Edge.ToNode.Position, 0.5)))
            );
        return Spawn(
            callsign,
            type,
            Between(ahead.Edge.FromNode.Position, ahead.Edge.ToNode.Position, 0.5),
            Facing(ahead.Edge.ToNode, ahead.Edge.FromNode)
        );
    }

    private static bool ClearOf(RunwayInfo runway, LatLon point) =>
        GeoMath.DistanceToSegmentFt(point, new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2)) > ClearOfRunwayFt;

    /// <summary>A C172 at parking on a KOAK stand, in <see cref="AtParkingPhase"/>.</summary>
    internal static AircraftState SpawnAtStand(AirportGroundLayout layout, string callsign)
    {
        GroundNode stand = layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Edges.Count > 0)).OrderBy(n => n.Id).First();
        AircraftState aircraft = Spawn(callsign, "C172", stand.Position, new TrueHeading(0));
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    internal static AircraftState Spawn(string callsign, string type, LatLon position, TrueHeading heading)
    {
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = position,
            TrueHeading = heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = AirportId,
                Destination = AirportId,
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(1500),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, null));
        return aircraft;
    }
}
