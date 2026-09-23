using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Issue #460 part B: <c>PUSH A F1</c> off SFO gate D7 was refused "the move to taxiway A would put the aircraft on
/// taxiway F1" while <c>PUSH A</c> from the same gate went. F1 meets A at A's exit node there, so an aircraft lined up
/// on A facing toward F1 lies across F1's junction pavement without ever going onto F1. The pavement of every taxiway
/// meeting A within a fuselage length of the exit node, measured along the walk from it, is exempt from the
/// movement-area check; pavement beyond that walk, an arc onto a taxiway that does not meet A there, and any edge
/// touching a runway holding position are not.
/// </summary>
public class PushTaxiwayFacingJunctionTests(ITestOutputHelper output)
{
    private const string AircraftType = "B738";

    [Fact]
    public void PushOntoTaxiwayFacingJunction_IsNotRefusedForTheFacingTaxiwaysJunctionPavement()
    {
        if (SfoLayout() is not { } layout)
        {
            return;
        }

        GroundNode d7 = Gate(layout, "D7");
        CommandResult bare = Push(layout, d7, "PUSH A");
        Assert.True(bare.Success, $"PUSH A off D7 is the premise: {bare.Message}");

        CommandResult faced = Push(layout, d7, "PUSH A F1");

        Assert.True(faced.Success, $"PUSH A F1 off D7 was refused: {faced.Message}");
        Assert.Equal("Pushing back onto A facing F1", faced.Message);
    }

    /// <summary>
    /// F1 meets A at D7's exit node, so the bearing from the exit node to F1's nearest node says nothing (it read 0°, and
    /// the push took A's direction nearest north by accident). The aircraft ends lined up on A in the direction along A
    /// nearest the way F1's own centreline leaves the junction; were F1 to leave square to A (both of A's directions
    /// within 5° of the same angle to it), on A's direction nearest its nose at the gate, the least swing.
    /// </summary>
    [Fact]
    public void PushOntoTaxiwayFacingJunction_EndsFacingAlongTheTaxiwayTheWayTheFacingTaxiwayLeads()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode d7 = Gate(ground.Layout, "D7");
        GroundNode exit = ground.Layout.FindExitByTaxiway(d7.Position, "A") ?? throw new InvalidOperationException("no A exit");
        GroundEdge f1 = exit.Edges.OfType<GroundEdge>().Single(e => e.MatchesTaxiway("F1"));
        double leadDeg = GeoMath.BearingTo(exit.Position, f1.OtherNode(exit).Position);
        double towardDeg = ground.Layout.GetEdgeBearingForTaxiway(exit, "A", leadDeg) ?? throw new InvalidOperationException("no A edge");
        double awayDeg =
            ground.Layout.GetEdgeBearingForTaxiway(exit, "A", new TrueHeading(towardDeg).ToReciprocal().Degrees)
            ?? throw new InvalidOperationException("no A edge");
        double squareDeg = Math.Abs(GeoMath.AbsBearingDifference(towardDeg, leadDeg) - GeoMath.AbsBearingDifference(awayDeg, leadDeg));
        double noseDeg = d7.TrueHeading!.Value.Degrees;
        double leastSwingDeg =
            GeoMath.AbsBearingDifference(towardDeg, noseDeg) <= GeoMath.AbsBearingDifference(awayDeg, noseDeg) ? towardDeg : awayDeg;
        double expectedDeg = squareDeg <= 5.0 ? leastSwingDeg : towardDeg;
        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL460", AircraftType, "D7");

        CommandResult result = ground.Engine.SendCommand(ac.Callsign, "PUSH A F1");
        Assert.True(result.Success, result.Message);
        SfoGroundHarness.TickUntil(ground.Engine, () => ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase, 600, null);

        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
        double offDeg = GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, expectedDeg);
        output.WriteLine(
            $"F1 leads off on {leadDeg:F1}; A runs {towardDeg:F1}/{awayDeg:F1} ({squareDeg:F1}° from square); expected {expectedDeg:F1}; ended on {ac.TrueHeading.Degrees:F1}"
        );
        Assert.True(offDeg < 10.0, $"ended on {ac.TrueHeading.Degrees:F1}°, {offDeg:F0}° from the expected direction along A ({expectedDeg:F1}°)");
    }

    /// <summary>
    /// T meets A at E11U's exit node: its stub there is A's own intersection pavement, and <c>PUSH A F1</c> no longer
    /// refuses for lying across it.
    /// </summary>
    [Fact]
    public void PushOntoTaxiway_IsNotRefusedForAnotherTaxiwayMeetingItWithinTheLimit()
    {
        if (SfoLayout() is not { } layout)
        {
            return;
        }

        CommandResult result = Push(layout, Gate(layout, "E11U"), "PUSH A F1");

        Assert.True(result.Success, $"PUSH A F1 off E11U was refused: {result.Message}");
    }

    /// <summary>
    /// K meets A within the limit off F3, but the K stub the tow crosses runs from 82 to 150 ft along the walk from the
    /// exit node, beyond a B738's fuselage length: that is K itself, not A's intersection, and the refusal stands.
    /// </summary>
    [Fact]
    public void PushOntoTaxiway_StillRefusesAnotherTaxiwaysPavementBeyondTheLimit()
    {
        if (SfoLayout() is not { } layout)
        {
            return;
        }

        CommandResult result = Push(layout, Gate(layout, "F3"), "PUSH A F1");

        Assert.False(result.Success, $"PUSH A F1 off F3 was accepted: {result.Message}");
        Assert.Equal("Unable, the move to taxiway A would put the aircraft on taxiway K", result.Message);
    }

    /// <summary>
    /// The exemption built for D7's push is bounded: every edge in it lies within a fuselage length of A's exit node,
    /// measured along the walk the builder takes (A's centreline, then the exempt edges), and some F1 pavement beyond it
    /// is left out, so F1 pavement past the junction still refuses.
    /// </summary>
    [Fact]
    public void PushOntoTaxiway_StillRefusesFacingTaxiwayPavementBeyondTheJunction()
    {
        if (SfoLayout() is not { } layout)
        {
            return;
        }

        GroundNode exit = Exit(layout, "D7");

        IReadOnlySet<int> junction = TugPathCheck.FacingJunctionEdgeIndices(layout, AircraftType, exit, "A", "F1");

        Assert.NotEmpty(junction);
        AssertWithinTheWalkBound(layout, exit, junction);
        List<IGroundEdge> edges = [.. layout.AllEdges];
        Assert.Contains(
            Enumerable.Range(0, edges.Count),
            i => edges[i].MatchesTaxiway("F1") && !junction.Contains(i) && Touches(edges[i], junction, edges)
        );
    }

    /// <summary>
    /// K meets A 75 ft along A from F3's exit node; the bound is measured from the exit node along the whole walk, not
    /// afresh from the K junction, so K's stub from 82 to 150 ft is outside a B738's 130 ft.
    /// </summary>
    [Fact]
    public void FacingJunctionEdges_MeasureTheBoundAlongTheWalkFromTheExitNode()
    {
        if (SfoLayout() is not { } layout)
        {
            return;
        }

        GroundNode exit = Exit(layout, "F3");

        IReadOnlySet<int> junction = TugPathCheck.FacingJunctionEdgeIndices(layout, AircraftType, exit, "A", "K");

        AssertWithinTheWalkBound(layout, exit, junction);
    }

    /// <summary>
    /// Off D10, E meets A at the exit node and F branches off E 11 ft from it. The E/F corner fillet there lies wholly
    /// inside the walk bound and is exempt; the E/F fillets at E's next junction, about 114 ft along the walk and
    /// 127–133 ft long, reach past the bound and stay out, though they carry E's name.
    /// </summary>
    [Fact]
    public void FacingJunctionEdges_LeaveOutAnArcOntoATaxiwayThatDoesNotMeetTheGoalTaxiway()
    {
        if (SfoLayout() is not { } layout)
        {
            return;
        }

        GroundNode exit = Exit(layout, "D10");
        double boundFt = TugMovePlanner.FuselageLengthFt(AircraftType);
        List<IGroundEdge> edges = [.. layout.AllEdges];
        List<int> farFillets =
        [
            .. Enumerable
                .Range(0, edges.Count)
                .Where(i => (edges[i] is GroundArc) && edges[i].MatchesTaxiway("E") && edges[i].MatchesTaxiway("F"))
                .Where(i => ((NearestEndFt(edges[i], exit) + LengthFt(edges[i])) > boundFt) && (NearestEndFt(edges[i], exit) <= (2 * boundFt))),
        ];
        Assert.NotEmpty(farFillets);

        IReadOnlySet<int> junction = TugPathCheck.FacingJunctionEdgeIndices(layout, AircraftType, exit, "A", "E");

        Assert.Contains(junction, i => (edges[i] is GroundArc) && edges[i].MatchesTaxiway("F"));
        Assert.DoesNotContain(farFillets, junction.Contains);
        AssertWithinTheWalkBound(layout, exit, junction);
    }

    /// <summary>
    /// SFO D10: E meets A at the exit node, and the E/F corner fillet 11–29 ft from it is A's own intersection pavement,
    /// in the junction set. The tow goes on across the next E/F fillet, which starts about 114 ft along the walk and is
    /// 127 ft long — beyond a B738's fuselage length — so <c>PUSH A F1</c> is still refused, naming E.
    /// </summary>
    [Fact]
    public void PushOntoTaxiway_D10_StillRefusesTheEfFilletBeyondTheBound()
    {
        if (SfoLayout() is not { } layout)
        {
            return;
        }

        GroundNode exit = Exit(layout, "D10");
        List<IGroundEdge> edges = [.. layout.AllEdges];
        List<int> efFillets =
        [
            .. Enumerable.Range(0, edges.Count).Where(i => (edges[i] is GroundArc) && edges[i].MatchesTaxiway("E") && edges[i].MatchesTaxiway("F")),
        ];
        int cornerFillet = efFillets.Where(i => edges[i].Nodes.Select(n => n.Id).Order().SequenceEqual([1601, 1604])).DefaultIfEmpty(-1).First();
        if (cornerFillet < 0)
        {
            cornerFillet = efFillets.MinBy(i => NearestEndFt(edges[i], exit));
        }

        IReadOnlySet<int> junction = TugPathCheck.FacingJunctionEdgeIndices(layout, AircraftType, exit, "A", "F1");
        CommandResult result = Push(layout, Gate(layout, "D10"), "PUSH A F1");

        Assert.Contains(cornerFillet, junction);
        Assert.False(result.Success, $"PUSH A F1 off D10 was accepted: {result.Message}");
        Assert.Equal("Unable, the move to taxiway A would put the aircraft on taxiway E", result.Message);
    }

    /// <summary>
    /// SFO taxiway F reaches a runway holding position about 100 ft from its junction with AF, inside a B738's
    /// fuselage length: the exemption for a push onto AF facing F leaves out every edge touching a holding position.
    /// </summary>
    [Fact]
    public void FacingJunctionEdges_LeaveOutAnEdgeTouchingARunwayHoldShort()
    {
        if (SfoLayout() is not { } layout)
        {
            return;
        }

        double boundFt = TugMovePlanner.FuselageLengthFt(AircraftType);
        List<GroundNode> holds = [.. layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && HasStraightEdge(n, "F"))];
        List<GroundNode> junctions = [.. layout.Nodes.Values.Where(n => HasStraightEdge(n, "F") && HasStraightEdge(n, "AF"))];
        (GroundNode hold, GroundNode junctionNode, double apartFt) = holds
            .SelectMany(h => junctions.Select(j => (Hold: h, Junction: j, ApartFt: Ft(h.Position, j.Position))))
            .MinBy(p => p.ApartFt);
        Assert.True(apartFt < boundFt, $"the premise: F's holding position #{hold.Id} lies {apartFt:F0} ft from AF, beyond {boundFt:F0} ft");
        List<IGroundEdge> edges = [.. layout.AllEdges];

        IReadOnlySet<int> junction = TugPathCheck.FacingJunctionEdgeIndices(layout, AircraftType, junctionNode, "AF", "F");

        Assert.DoesNotContain(junction, i => edges[i].Nodes.Any(n => n.Type == GroundNodeType.RunwayHoldShort));
    }

    /// <summary>
    /// <c>PUSH A FACE &lt;dir&gt;</c> names no facing taxiway, so its taxiway-line goal carries no junction exemption;
    /// the same goal lined up facing F1 does.
    /// </summary>
    [Fact]
    public void PushOntoTaxiwayWithAHeading_GetsNoJunctionExemption()
    {
        if (SfoLayout() is not { } layout)
        {
            return;
        }

        GroundNode d7 = Gate(layout, "D7");
        GroundNode exit = Exit(layout, "D7");
        double facingDeg = layout.GetEdgeBearingForTaxiway(exit, "A", 0.0) ?? throw new InvalidOperationException("no A edge");
        var headingGoal = TugGoal.TaxiwayLine(exit, "A", facingDeg);

        ResolvedTugGoal byHeading = TugGoalResolver.Resolve(layout, Request(d7, headingGoal), 0);
        ResolvedTugGoal byFacingTaxiway = TugGoalResolver.Resolve(layout, Request(d7, headingGoal with { FacingTaxiwayName = "F1" }), 0);

        Assert.Empty(byHeading.JunctionEdgeIndices);
        Assert.NotEmpty(byFacingTaxiway.JunctionEdgeIndices);
    }

    /// <summary>
    /// Every exempt edge lies within a fuselage length of the exit node along the builder's own walk: out along the goal
    /// taxiway's centreline, then through exempt edges only.
    /// </summary>
    private static void AssertWithinTheWalkBound(AirportGroundLayout layout, GroundNode exit, IReadOnlySet<int> junction)
    {
        double boundFt = TugMovePlanner.FuselageLengthFt(AircraftType);
        List<IGroundEdge> edges = [.. layout.AllEdges];
        var exempt = new HashSet<IGroundEdge>(junction.Select(i => edges[i]));
        var reached = new Dictionary<int, double> { [exit.Id] = 0.0 };
        var queue = new Queue<GroundNode>([exit]);
        while (queue.Count > 0)
        {
            GroundNode node = queue.Dequeue();
            foreach (IGroundEdge edge in node.Edges.Where(e => exempt.Contains(e) || ((e is GroundEdge) && (e.TaxiwayName == "A"))))
            {
                GroundNode far = edge.Nodes[0].Id == node.Id ? edge.Nodes[1] : edge.Nodes[0];
                double farFt = reached[node.Id] + LengthFt(edge);
                if ((farFt <= boundFt) && (!reached.TryGetValue(far.Id, out double held) || (farFt < held)))
                {
                    reached[far.Id] = farFt;
                    queue.Enqueue(far);
                }
            }
        }

        foreach (int index in junction)
        {
            IGroundEdge edge = edges[index];
            double nearFt = edge.Nodes.Where(n => reached.ContainsKey(n.Id)).Select(n => reached[n.Id]).DefaultIfEmpty(double.PositiveInfinity).Min();
            Assert.True(
                (nearFt + LengthFt(edge)) <= boundFt,
                $"edge [{index}] {edge.TaxiwayName} {edge.Nodes[0].Id}-{edge.Nodes[1].Id} runs to {nearFt + LengthFt(edge):F0} ft along the walk, beyond {boundFt:F0} ft"
            );
        }
    }

    private static TugRequest Request(GroundNode stand, TugGoal goal) =>
        new()
        {
            Start = new TugPose(stand.Position, stand.TrueHeading!.Value.Degrees),
            StartsAtStand = true,
            AircraftType = AircraftType,
            Goals = [goal],
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
        };

    private static CommandResult Push(AirportGroundLayout layout, GroundNode stand, string command)
    {
        var ac = new AircraftState
        {
            Callsign = "UAL460",
            AircraftType = AircraftType,
            Position = stand.Position,
            TrueHeading = stand.TrueHeading ?? throw new InvalidOperationException($"SFO gate {stand.Name} has no heading"),
            TrueTrack = stand.TrueHeading.Value,
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };
        ac.Ground.Layout = layout;
        ac.Ground.ParkingSpot = stand.Name;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);
        DispatchContext ctx = TestDispatch.Context(new Random(42), validateDctFixes: false, groundLayout: layout);
        return CommandDispatcher.DispatchCompound(compound, ac, ctx);
    }

    private static AirportGroundLayout? SfoLayout()
    {
        TestVnasData.EnsureInitialized();
        return new TestAirportGroundData().GetLayout("SFO");
    }

    private static GroundNode Gate(AirportGroundLayout layout, string name) =>
        layout.FindParkingByName(name) ?? throw new InvalidOperationException($"SFO gate {name} missing");

    private static GroundNode Exit(AirportGroundLayout layout, string gate) =>
        layout.FindExitByTaxiway(Gate(layout, gate).Position, "A") ?? throw new InvalidOperationException($"no A exit off {gate}");

    private static bool Touches(IGroundEdge edge, IReadOnlySet<int> junction, List<IGroundEdge> edges) =>
        junction.Any(i => edges[i].Nodes.Any(n => edge.Nodes.Contains(n)));

    private static bool HasStraightEdge(GroundNode node, string taxiway) =>
        node.Edges.OfType<GroundEdge>().Any(e => string.Equals(e.TaxiwayName, taxiway, StringComparison.OrdinalIgnoreCase));

    private static double NearestEndFt(IGroundEdge edge, GroundNode from) => edge.Nodes.Min(n => Ft(n.Position, from.Position));

    private static double LengthFt(IGroundEdge edge) => edge.DistanceNm * GeoMath.FeetPerNm;

    private static double Ft(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;
}
