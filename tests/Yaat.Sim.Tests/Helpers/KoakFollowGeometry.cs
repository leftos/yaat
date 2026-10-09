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

    internal static GroundEdge EdgeBetween(GroundNode a, GroundNode b) =>
        a.Edges.OfType<GroundEdge>().FirstOrDefault(edge => edge.OtherNode(a).Id == b.Id)
        ?? throw new InvalidOperationException(
            $"No straight edge joins node #{a.Id} and node #{b.Id} (nodes joined only by a fillet arc, or not adjacent); "
                + $"use the GroundArc from node #{a.Id}'s edges for arc pairs"
        );

    /// <summary>
    /// The sharpest turn of more than 90° at a KOAK junction from a straight taxiway edge at least 60 ft long onto another at least
    /// 40 ft long (the lowest junction id among equals): the far end of the edge in, the junction, the far end of the edge out,
    /// the two edges and the turn (degrees).
    /// </summary>
    internal static (GroundNode FarIn, GroundNode Junction, GroundNode FarOut, GroundEdge In, GroundEdge Out, double TurnDeg) SharpTurn(
        AirportGroundLayout layout
    )
    {
        (GroundNode FarIn, GroundNode Junction, GroundNode FarOut, GroundEdge In, GroundEdge Out, double TurnDeg)? sharpest = null;
        foreach (GroundNode junction in layout.Nodes.Values.OrderBy(node => node.Id))
        {
            List<GroundEdge> edges = [.. junction.Edges.OfType<GroundEdge>().Where(edge => !edge.IsRunwayCenterline && !edge.IsRamp)];
            foreach (GroundEdge inEdge in edges.Where(edge => edge.DistanceNm * GeoMath.FeetPerNm >= 60.0))
            {
                GroundNode farIn = inEdge.OtherNode(junction);
                double inDeg = GeoMath.BearingTo(farIn.Position, junction.Position);
                foreach (GroundEdge outEdge in edges.Where(edge => !ReferenceEquals(edge, inEdge) && (edge.DistanceNm * GeoMath.FeetPerNm >= 40.0)))
                {
                    GroundNode farOut = outEdge.OtherNode(junction);
                    double turnDeg = Math.Abs(GeoMath.SignedBearingDifference(inDeg, GeoMath.BearingTo(junction.Position, farOut.Position)));
                    if ((turnDeg > 90.0) && (turnDeg < 175.0) && (turnDeg > (sharpest?.TurnDeg ?? 0.0)))
                    {
                        sharpest = (farIn, junction, farOut, inEdge, outEdge, turnDeg);
                    }
                }
            }
        }

        Assert.True(sharpest is not null, "KOAK has no straight taxiway edge turning more than 90° onto another");
        return sharpest.Value;
    }

    /// <summary>
    /// A C-to-J fillet corner whose straight way round the junction is longer than the arc joining its tangent nodes: the
    /// corner a lead rounding the arc leaves straight stub edges either side of the junction in its trail for, never the arc
    /// it drove. <see cref="ThroughRun"/> is the straight path round the junction, C tangent node first.
    /// </summary>
    internal sealed record CToJCorner(
        GroundArc Arc,
        GroundNode TangentOnC,
        GroundNode TangentOnJ,
        GroundEdge StubOnC,
        GroundEdge StubOnJ,
        IReadOnlyList<GroundNode> ThroughRun
    );

    /// <summary>
    /// The KOAK C/J corner whose straight way round the junction exceeds the arc joining its tangent nodes by the most —
    /// at node 352 the 130 ft arc against 300 ft round the apex, where the shallow corners either side differ by 2 ft.
    /// </summary>
    internal static CToJCorner FindCToJCorner(AirportGroundLayout layout)
    {
        CToJCorner? widest = null;
        double widestFt = 0.0;
        foreach (GroundNode junction in layout.Nodes.Values.OrderBy(node => node.Id))
        {
            foreach (CToJCorner candidate in CornerCandidates(junction))
            {
                double excessFt = ThroughFt(candidate) - (candidate.Arc.DistanceNm * GeoMath.FeetPerNm);
                if (excessFt > widestFt)
                {
                    widestFt = excessFt;
                    widest = candidate;
                }
            }
        }

        Assert.NotNull(widest);
        return widest;
    }

    /// <summary>
    /// The corners at <paramref name="junction"/>: each C edge and J edge pair with a straight stub off each to a tangent
    /// node, those two tangent nodes joined by a fillet arc.
    /// </summary>
    private static IEnumerable<CToJCorner> CornerCandidates(GroundNode junction)
    {
        List<GroundEdge> straight = [.. junction.Edges.OfType<GroundEdge>()];
        foreach (GroundEdge cEdge in straight.Where(edge => edge.MatchesTaxiway("C")))
        {
            foreach (GroundEdge jEdge in straight.Where(edge => edge.MatchesTaxiway("J")))
            {
                GroundNode cNear = cEdge.OtherNode(junction);
                GroundNode jNear = jEdge.OtherNode(junction);
                foreach (GroundEdge stubC in Stubs(cNear, junction, "C"))
                {
                    foreach (GroundEdge stubJ in Stubs(jNear, junction, "J"))
                    {
                        GroundNode t1 = stubC.OtherNode(cNear);
                        GroundNode t2 = stubJ.OtherNode(jNear);
                        if (t1.Edges.OfType<GroundArc>().FirstOrDefault(arc => arc.OtherNode(t1).Id == t2.Id) is not { } arc)
                        {
                            continue;
                        }

                        yield return new CToJCorner(arc, t1, t2, stubC, stubJ, [t1, cNear, junction, jNear, t2]);
                    }
                }
            }
        }
    }

    private static IEnumerable<GroundEdge> Stubs(GroundNode node, GroundNode junction, string taxiway) =>
        node.Edges.OfType<GroundEdge>().Where(edge => edge.MatchesTaxiway(taxiway) && (edge.OtherNode(node).Id != junction.Id));

    private static double ThroughFt(CToJCorner corner)
    {
        double nm = 0.0;
        for (int i = 0; i + 1 < corner.ThroughRun.Count; i++)
        {
            nm += EdgeBetween(corner.ThroughRun[i], corner.ThroughRun[i + 1]).DistanceNm;
        }

        return nm * GeoMath.FeetPerNm;
    }

    /// <summary>
    /// A C-to-J corner and <see cref="Loop"/>, the way round the block from its C tangent node back to its J tangent node that
    /// never uses the junction, as a node chain over straight edges, C tangent node first.
    /// </summary>
    internal sealed record CornerLoop(CToJCorner Corner, IReadOnlyList<GroundNode> Loop);

    /// <summary>
    /// The corner at <see cref="FindCToJCorner"/>'s junction whose C side leads round the block back to its J side without
    /// crossing a runway: on C away from the junction to H, H to D, D to J, and J back down to the J tangent node.
    /// </summary>
    internal static CornerLoop FindBlockBackToTheCorner(AirportGroundLayout layout)
    {
        GroundNode junction = FindCToJCorner(layout).ThroughRun[2];
        foreach (CToJCorner corner in CornerCandidates(junction))
        {
            if (BlockBack(corner) is { } loop)
            {
                return new CornerLoop(corner, loop);
            }
        }

        throw new InvalidOperationException($"No C-to-J corner at KOAK node #{junction.Id} leads round the block by C, H, D and J");
    }

    private static List<GroundNode>? BlockBack(CToJCorner corner)
    {
        (string Taxiway, Func<GroundNode, bool> IsGoal)[] legs =
        [
            ("C", node => HasStraight(node, "H")),
            ("H", node => HasStraight(node, "D")),
            ("D", node => HasStraight(node, "J")),
            ("J", node => node.Id == corner.TangentOnJ.Id),
        ];
        List<GroundNode> chain = [corner.ThroughRun[1], corner.TangentOnC];
        foreach ((string taxiway, Func<GroundNode, bool> isGoal) in legs)
        {
            if (AlongTaxiway(chain[^1], chain[^2], taxiway, isGoal) is not { } leg)
            {
                return null;
            }

            chain.AddRange(leg.Skip(1));
        }

        return chain[1..];
    }

    /// <summary>
    /// The fewest straight <paramref name="taxiway"/> edges from <paramref name="from"/> to the nearest node
    /// <paramref name="isGoal"/> accepts, never entering <paramref name="behind"/>, as a node chain from <paramref name="from"/>;
    /// null when no such node is reachable.
    /// </summary>
    private static List<GroundNode>? AlongTaxiway(GroundNode from, GroundNode behind, string taxiway, Func<GroundNode, bool> isGoal)
    {
        Dictionary<int, GroundNode?> cameFrom = new() { [from.Id] = null, [behind.Id] = null };
        Queue<GroundNode> frontier = new([from]);
        while (frontier.TryDequeue(out GroundNode? node))
        {
            if ((node.Id != from.Id) && isGoal(node))
            {
                List<GroundNode> chain = [];
                for (GroundNode? at = node; at is not null; at = cameFrom[at.Id])
                {
                    chain.Insert(0, at);
                }

                return chain;
            }

            foreach (GroundEdge edge in node.Edges.OfType<GroundEdge>().Where(edge => edge.MatchesTaxiway(taxiway)))
            {
                GroundNode next = edge.OtherNode(node);
                if (cameFrom.TryAdd(next.Id, node))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        return null;
    }

    private static bool HasStraight(GroundNode node, string taxiway) => node.Edges.OfType<GroundEdge>().Any(edge => edge.MatchesTaxiway(taxiway));

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
