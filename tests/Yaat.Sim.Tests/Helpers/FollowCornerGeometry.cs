using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Ground geometry and aircraft for the <c>FOLLOWG</c> planner and command tests: KOAK's taxiway B chain and engine, and
/// fillet-corner finders that work on any layout (KOAK and KSFO).
/// </summary>
internal static class FollowCornerGeometry
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
        AircraftState aircraft = Spawn(setup.Layout.AirportId, callsign, type, pose.At.Position, Facing(pose.At, pose.Toward));
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
    /// it drove. <see cref="ThroughRun"/> is the straight path round the junction, C tangent node first: the two tangent nodes,
    /// the stub ends either side of the junction and the junction (five nodes), or, where each stub runs from its tangent node
    /// to the junction itself, the tangent nodes and the junction (three nodes).
    /// </summary>
    internal sealed record CToJCorner(
        GroundArc Arc,
        GroundNode TangentOnC,
        GroundNode TangentOnJ,
        GroundEdge StubOnC,
        GroundEdge StubOnJ,
        IReadOnlyList<GroundNode> ThroughRun
    )
    {
        /// <summary>The C stub's end away from its tangent node.</summary>
        internal GroundNode NearOnC => ThroughRun[1];

        /// <summary>The J stub's end away from its tangent node.</summary>
        internal GroundNode NearOnJ => ThroughRun[^2];
    }

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
            foreach (CToJCorner candidate in CornerCandidates(junction, "C", "J"))
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
    /// A fillet corner like <see cref="CToJCorner"/> on any two taxiways: <see cref="InTaxiway"/> takes the C side's place
    /// and <see cref="OutTaxiway"/> the J side's.
    /// </summary>
    internal sealed record TaxiwayCorner(CToJCorner Corner, string InTaxiway, string OutTaxiway);

    /// <summary>
    /// The first fillet corner on <paramref name="layout"/> (lowest junction id) whose arc's tightest radius is under
    /// <paramref name="category"/>'s main-gear turn radius but not under <paramref name="nextDown"/>'s, whose square way round
    /// the junction turns no more than <paramref name="category"/> may at a junction, and that has a straight edge on past each
    /// tangent node along the tangent's own taxiway; null when the layout has none.
    /// </summary>
    internal static TaxiwayCorner? FindArcTighterThan(AirportGroundLayout layout, AircraftCategory category, AircraftCategory nextDown)
    {
        double radiusFt = CategoryPerformance.MainGearTurnRadiusFt(category);
        double floorFt = CategoryPerformance.MainGearTurnRadiusFt(nextDown);
        double maxTurnDeg = CategoryLimits.MaxHeadingChangeDeg(category);
        return layout
            .Nodes.Values.OrderBy(node => node.Id)
            .SelectMany(TaxiwayCorners)
            .FirstOrDefault(c =>
                (c.Corner.Arc.MinRadiusOfCurvatureFt >= floorFt)
                && (c.Corner.Arc.MinRadiusOfCurvatureFt < radiusFt)
                && (ApexTurnDeg(c.Corner) <= maxTurnDeg)
                && HasStraightBeyond(c.Corner.TangentOnC, c.Corner.NearOnC, c.InTaxiway)
                && HasStraightBeyond(c.Corner.TangentOnJ, c.Corner.NearOnJ, c.OutTaxiway)
            );
    }

    /// <summary>
    /// KSFO's Q-to-B1 corner (<paramref name="layout"/> is KSFO's): the first whose arc's tightest radius is under a jet's
    /// main-gear turn radius but not a turboprop's, at a junction turn a jet may make (<see cref="FindArcTighterThan"/>) — a
    /// wide Bezier fillet whose effective radius (<see cref="FollowRoutePlanner.EffectiveArcRadiusFt"/>) a jet turns on —
    /// logged to <paramref name="output"/>.
    /// </summary>
    internal static TaxiwayCorner KsfoQToB1(AirportGroundLayout layout, ITestOutputHelper output)
    {
        TaxiwayCorner? corner = FindArcTighterThan(layout, AircraftCategory.Jet, AircraftCategory.Turboprop);
        Assert.NotNull(corner);
        LogCorner(corner, output);
        return corner;
    }

    /// <summary>
    /// The first corner on the layouts of <paramref name="airportIds"/>, in order, whose arc a C172 lead drives and a B738
    /// follower refuses (<see cref="IsJetRefusedCorner"/>), with its airport and layout; null when no layout has one. Logs to
    /// <paramref name="output"/>, for each airport searched, how many arcs are under a jet's effective radius, how many of
    /// those a piston drives, and whether a corner qualifies.
    /// </summary>
    internal static (string AirportId, AirportGroundLayout Layout, TaxiwayCorner Corner)? ArcTighterThanAJet(
        ITestOutputHelper output,
        IReadOnlyList<string> airportIds
    )
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        double jetFt = CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet);
        foreach (string airportId in airportIds)
        {
            if (groundData.GetLayout(airportId) is not { } layout)
            {
                output.WriteLine($"SKIP: {airportId} layout unavailable");
                continue;
            }

            List<GroundArc> underJet =
            [
                .. layout
                    .Nodes.Values.SelectMany(n => n.Edges.OfType<GroundArc>())
                    .Distinct()
                    .Where(a => FollowRoutePlanner.EffectiveArcRadiusFt(a) < jetFt),
            ];
            TaxiwayCorner? tight = ArcCorners(layout).FirstOrDefault(IsJetRefusedCorner);
            output.WriteLine(
                $"{airportId}: {underJet.Count} arcs under {jetFt:F0} ft effective, {underJet.Count(PistonDrives)} a piston drives; "
                    + $"corner: {tight is not null}"
            );
            if (tight is not null)
            {
                LogCorner(tight, output);
                return (airportId, layout, tight);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="corner"/>'s arc is one a C172 drives (<see cref="PistonDrives"/>) and a B738 refuses, its
    /// effective radius under a jet's main-gear turn radius, while its square way round turns no more than a jet may at a node
    /// and is no longer than the follow planner accepts for a square way (1.5 times <see cref="FollowRoutePlanner.ArcStubLengthFt"/>),
    /// with a straight edge on past each tangent node along the tangent's own taxiway.
    /// </summary>
    private static bool IsJetRefusedCorner(TaxiwayCorner corner) =>
        PistonDrives(corner.Corner.Arc)
        && (FollowRoutePlanner.EffectiveArcRadiusFt(corner.Corner.Arc) < CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet))
        && (ApexTurnDeg(corner.Corner) <= CategoryLimits.MaxHeadingChangeDeg(AircraftCategory.Jet))
        && (ThroughFt(corner.Corner) <= (1.5 * FollowRoutePlanner.ArcStubLengthFt(corner.Corner.Arc)))
        && HasStraightBeyond(corner.Corner.TangentOnC, corner.Corner.NearOnC, corner.InTaxiway)
        && HasStraightBeyond(corner.Corner.TangentOnJ, corner.Corner.NearOnJ, corner.OutTaxiway);

    /// <summary>
    /// KOAK's K-to-L corner through node 441 (<paramref name="layout"/> is KOAK's), whose fillet arc's tightest radius is under
    /// the floor no category steers under, so every follower refuses it at its main-gear turn radius and at its tight-turn floor
    /// alike; logged to <paramref name="output"/> with its square way round against the follow planner's bound for it.
    /// </summary>
    internal static TaxiwayCorner KoakKToL441(AirportGroundLayout layout, ITestOutputHelper output)
    {
        TaxiwayCorner corner = ArcCorners(layout)
            .First(c =>
                string.Equals(c.InTaxiway, "K", StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.OutTaxiway, "L", StringComparison.OrdinalIgnoreCase)
                && c.Corner.ThroughRun.Concat(c.Corner.Arc.Nodes).Any(n => n.Id == 441)
            );
        double boundFt = 1.5 * FollowRoutePlanner.ArcStubLengthFt(corner.Corner.Arc);
        output.WriteLine($"K-to-L: square way {ThroughFt(corner.Corner):F1} ft against the 1.5 x stub bound {boundFt:F1} ft");
        LogCorner(corner, output);
        return corner;
    }

    /// <summary>
    /// Whether a piston aircraft may round <paramref name="arc"/>: its effective radius not under a piston's main-gear turn
    /// radius, its tightest radius not under the floor no category steers under.
    /// </summary>
    private static bool PistonDrives(GroundArc arc) =>
        (FollowRoutePlanner.EffectiveArcRadiusFt(arc) >= CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Piston))
        && (arc.MinRadiusOfCurvatureFt >= GeometricAdmissibility.MinSteerableArcRadiusFt);

    /// <summary>
    /// Every fillet arc on <paramref name="layout"/> off the ramps and runways, each way round (arcs in order of their lowest
    /// node id), as a corner whose <see cref="CToJCorner.ThroughRun"/> is the shortest run of at most four straight taxiway
    /// edges from the arc's first tangent node to its other when that run has three or five nodes.
    /// </summary>
    private static IEnumerable<TaxiwayCorner> ArcCorners(AirportGroundLayout layout)
    {
        IEnumerable<GroundArc> arcs = layout
            .Nodes.Values.OrderBy(node => node.Id)
            .SelectMany(node => node.Edges.OfType<GroundArc>())
            .Distinct()
            .Where(arc =>
                !arc.TaxiwayNames.Any(name =>
                    name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) || name.Equals("RAMP", StringComparison.OrdinalIgnoreCase)
                )
            );
        foreach (GroundArc arc in arcs)
        {
            foreach ((GroundNode from, GroundNode to) in new[] { (arc.Nodes[0], arc.Nodes[1]), (arc.Nodes[1], arc.Nodes[0]) })
            {
                if (ShortestRun([from], 0.0, to, 4) is { Run.Count: 3 or 5 } found)
                {
                    GroundEdge stubOnC = EdgeBetween(found.Run[0], found.Run[1]);
                    GroundEdge stubOnJ = EdgeBetween(found.Run[^2], found.Run[^1]);
                    var corner = new CToJCorner(arc, from, to, stubOnC, stubOnJ, found.Run);
                    yield return new TaxiwayCorner(corner, stubOnC.TaxiwayName, stubOnJ.TaxiwayName);
                }
            }
        }
    }

    /// <summary>
    /// The shortest run on from <paramref name="run"/> (<paramref name="runFt"/> long) to <paramref name="to"/> over at most
    /// <paramref name="edgesLeft"/> more straight taxiway edges, off the ramps and runways and never back through a node it
    /// holds; null when none reaches it.
    /// </summary>
    private static (List<GroundNode> Run, double Ft)? ShortestRun(List<GroundNode> run, double runFt, GroundNode to, int edgesLeft)
    {
        GroundNode here = run[^1];
        if (here.Id == to.Id)
        {
            return (run, runFt);
        }

        (List<GroundNode> Run, double Ft)? best = null;
        IEnumerable<GroundEdge> onward = edgesLeft > 0 ? here.Edges.OfType<GroundEdge>().Where(e => !e.IsRunwayCenterline && !e.IsRamp) : [];
        foreach (GroundEdge edge in onward)
        {
            GroundNode next = edge.OtherNode(here);
            if (run.Any(node => node.Id == next.Id))
            {
                continue;
            }

            if (
                (ShortestRun([.. run, next], runFt + (edge.DistanceNm * GeoMath.FeetPerNm), to, edgesLeft - 1) is { } found)
                && (found.Ft < (best?.Ft ?? double.PositiveInfinity))
            )
            {
                best = found;
            }
        }

        return best;
    }

    /// <summary>The airports whose committed layouts the jet-refuses tests search for their corner (<see cref="ArcTighterThanAJet"/>).</summary>
    internal static readonly string[] JetRefusesSearchAirports =
    [
        "OAK",
        "SFO",
        "ATL",
        "AUS",
        "COS",
        "FAT",
        "FLL",
        "HWD",
        "IAH",
        "LAX",
        "MER",
        "MIA",
        "MSY",
        "RNO",
        "SEA",
        "SJC",
        "SMF",
        "issue172-sfo",
        "sfo-b1short",
    ];

    private static void LogCorner(TaxiwayCorner corner, ITestOutputHelper output) =>
        output.WriteLine(
            $"{corner.InTaxiway}-to-{corner.OutTaxiway} corner {string.Join(">", corner.Corner.ThroughRun.Select(n => $"#{n.Id}"))}: "
                + $"arc tightest radius {corner.Corner.Arc.MinRadiusOfCurvatureFt:F1} ft, "
                + $"effective {FollowRoutePlanner.EffectiveArcRadiusFt(corner.Corner.Arc):F1} ft, "
                + $"apex turn {ApexTurnDeg(corner.Corner):F1}°"
        );

    /// <summary>The fillet corners at <paramref name="junction"/> between every two taxiways that meet there.</summary>
    private static IEnumerable<TaxiwayCorner> TaxiwayCorners(GroundNode junction)
    {
        List<string> taxiways =
        [
            .. junction
                .Edges.OfType<GroundEdge>()
                .Where(edge => !edge.IsRunwayCenterline && !edge.IsRamp)
                .Select(edge => edge.TaxiwayName)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
        foreach (string inTaxiway in taxiways)
        {
            foreach (string outTaxiway in taxiways.Where(name => !string.Equals(name, inTaxiway, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (CToJCorner corner in CornerCandidates(junction, inTaxiway, outTaxiway))
                {
                    yield return new TaxiwayCorner(corner, inTaxiway, outTaxiway);
                }
            }
        }
    }

    /// <summary>The largest heading change (degrees) at a node of <paramref name="corner"/>'s straight way round: its junction turn.</summary>
    internal static double ApexTurnDeg(CToJCorner corner)
    {
        double sharpestDeg = 0.0;
        for (int i = 1; i + 1 < corner.ThroughRun.Count; i++)
        {
            double inDeg = GeoMath.BearingTo(corner.ThroughRun[i - 1].Position, corner.ThroughRun[i].Position);
            double outDeg = GeoMath.BearingTo(corner.ThroughRun[i].Position, corner.ThroughRun[i + 1].Position);
            sharpestDeg = Math.Max(sharpestDeg, Math.Abs(GeoMath.SignedBearingDifference(inDeg, outDeg)));
        }

        return sharpestDeg;
    }

    private static bool HasStraightBeyond(GroundNode tangent, GroundNode near, string taxiway) =>
        tangent.Edges.OfType<GroundEdge>().Any(edge => edge.MatchesTaxiway(taxiway) && (edge.OtherNode(tangent).Id != near.Id));

    /// <summary>
    /// The corners at <paramref name="junction"/>: each <paramref name="inTaxiway"/> edge and <paramref name="outTaxiway"/>
    /// edge pair with a straight stub off each to a tangent node, those two tangent nodes joined by a fillet arc.
    /// </summary>
    private static IEnumerable<CToJCorner> CornerCandidates(GroundNode junction, string inTaxiway, string outTaxiway)
    {
        List<GroundEdge> straight = [.. junction.Edges.OfType<GroundEdge>()];
        foreach (GroundEdge cEdge in straight.Where(edge => edge.MatchesTaxiway(inTaxiway)))
        {
            foreach (GroundEdge jEdge in straight.Where(edge => edge.MatchesTaxiway(outTaxiway)))
            {
                GroundNode cNear = cEdge.OtherNode(junction);
                GroundNode jNear = jEdge.OtherNode(junction);
                foreach (GroundEdge stubC in Stubs(cNear, junction, inTaxiway))
                {
                    foreach (GroundEdge stubJ in Stubs(jNear, junction, outTaxiway))
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
        foreach (CToJCorner corner in CornerCandidates(junction, "C", "J"))
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
            AirportId,
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
        AircraftState aircraft = Spawn(AirportId, callsign, "C172", stand.Position, new TrueHeading(0));
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    internal static AircraftState Spawn(string airportId, string callsign, string type, LatLon position, TrueHeading heading)
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
                Departure = airportId,
                Destination = airportId,
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
