using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// OAK, B738 at Spot "E" on TE facing junction TE/T (TE ends; T, U, TC begin). <c>TAXI TE T U HS T</c> holds short of T
/// at the junction, whose painted stop sits several segments back from the junction node. The aircraft must stop at that
/// painted stop, not run on to the junction, and drive on through it when released (YAAT-207). A runway bar whose
/// half-length setback is longer than the route's last segment takes the same path.
/// </summary>
public sealed class OakTaxiwayHoldShortSetbackTests
{
    private const string Callsign = "SWA3470";

    private readonly ITestOutputHelper _output;

    public OakTaxiwayHoldShortSetbackTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TaxiTeTUHsT_FromSpotE_StopsAtThePaintedStopNotTheJunction()
    {
        if (SpawnAtSpotE() is not { } world)
        {
            return;
        }

        (SimulationEngine engine, AirportGroundLayout layout, AircraftState ac) = world;
        CommandResult result = engine.SendCommand(Callsign, "TAXI TE T U HS T");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;
        HoldShortPoint bar = Assert.Single(route.HoldShortPoints);
        Assert.Equal(("T", HoldShortReason.ExplicitHoldShort), (bar.TargetName, bar.Reason));
        var stop = new LatLon(bar.Latitude!.Value, bar.Longitude!.Value);
        double stopToNodeFt = GeoMath.DistanceNm(stop, layout.Nodes[bar.NodeId].Position) * GeoMath.FeetPerNm;
        double barSegFt = route.Segments.First(s => s.ToNodeId == bar.NodeId).Edge.DistanceNm * GeoMath.FeetPerNm;
        Assert.True(stopToNodeFt > barSegFt + 50.0, "not the multi-segment shape");

        int settled = SfoGroundHarness.TickUntil(engine, () => IsHoldingShortAtRest(ac), 180, null);
        Assert.True(settled > 0, "never came to rest holding short of T");
        double offStopFt = GeoMath.DistanceNm(ac.Position, stop) * GeoMath.FeetPerNm;
        Assert.True(
            offStopFt <= 10.0,
            $"{Callsign} held {offStopFt:F0} ft from the painted HS T stop ({stopToNodeFt:F0} ft back from junction {bar.NodeId})"
        );
    }

    [Fact]
    public void TaxiTeTUHsT_Released_DrivesFromTheStopToTheJunction()
    {
        if (SpawnAtSpotE() is not { } world)
        {
            return;
        }

        (SimulationEngine engine, AirportGroundLayout layout, AircraftState ac) = world;
        Assert.True(engine.SendCommand(Callsign, "TAXI TE T U HS T").Success);
        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;
        HoldShortPoint bar = Assert.Single(route.HoldShortPoints);
        int barSegmentIndex = route.Segments.FindIndex(s => s.ToNodeId == bar.NodeId);
        Assert.True(SfoGroundHarness.TickUntil(engine, () => IsHoldingShortAtRest(ac), 180, null) > 0, "never came to rest holding short of T");
        double heldFromJunctionFt = GeoMath.DistanceNm(ac.Position, layout.Nodes[bar.NodeId].Position) * GeoMath.FeetPerNm;
        double barSegFt = route.Segments[barSegmentIndex].Edge.DistanceNm * GeoMath.FeetPerNm;
        Assert.True(
            heldFromJunctionFt > barSegFt + 50.0,
            $"held {heldFromJunctionFt:F0} ft from junction {bar.NodeId}, not short of its own segment"
        );

        // The issued route ends at the junction ("route ends at U, no destination given"), so the release drives the
        // segments left between the stop and the junction and the taxi ends there.
        Assert.Equal(route.Segments.Count - 1, barSegmentIndex);
        CommandResult res = engine.SendCommand(Callsign, "RES");
        Assert.True(res.Success, res.Message);
        int arrived = SfoGroundHarness.TickUntil(
            engine,
            () => route.IsComplete && (ac.GroundSpeed < SfoGroundHarness.StationarySpeedKts) && (ac.Phases?.CurrentPhase is HoldingInPositionPhase),
            120,
            null
        );
        Assert.True(
            arrived > 0,
            $"never drove on to junction {bar.NodeId} after RES (segment {route.CurrentSegmentIndex}, phase {ac.Phases?.CurrentPhase?.Name})"
        );
        Assert.True(bar.IsCleared);
        double endFromJunctionFt = GeoMath.DistanceNm(ac.Position, layout.Nodes[bar.NodeId].Position) * GeoMath.FeetPerNm;
        Assert.True(endFromJunctionFt <= 10.0, $"released taxi ended {endFromJunctionFt:F0} ft from junction {bar.NodeId}");
    }

    [Fact]
    public void TaxiW1To30_RunwayBarBehindAShortLastSegment_HoldsWithTheNoseAtTheBar()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        SimulationEngine engine = BuildEngine(groundData);
        GroundNode junction = layout.FindIntersectionNode("W", "W1")!;
        GroundNode toward = junction.Edges.First(e => e.TaxiwayName == "W1").OtherNode(junction);
        AircraftState ac = SfoGroundHarness.SpawnAt(
            new SfoGround(engine, layout),
            Callsign,
            "B738",
            (junction, new TrueHeading(GeoMath.BearingTo(junction.Position, toward.Position))),
            new HoldingInPositionPhase()
        );

        CommandResult result = engine.SendCommand(Callsign, "TAXI W1 30");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;
        HoldShortPoint bar = route.HoldShortPoints[^1];
        Assert.Equal(HoldShortReason.DestinationRunway, bar.Reason);
        var stop = new LatLon(bar.Latitude!.Value, bar.Longitude!.Value);
        double stopToNodeFt = GeoMath.DistanceNm(stop, layout.Nodes[bar.NodeId].Position) * GeoMath.FeetPerNm;
        double lastSegFt = route.Segments[^1].Edge.DistanceNm * GeoMath.FeetPerNm;
        Assert.True(
            stopToNodeFt > lastSegFt + 20.0,
            $"not the short-last-segment shape: stop {stopToNodeFt:F0} ft back, last segment {lastSegFt:F0} ft"
        );

        int settled = SfoGroundHarness.TickUntil(engine, () => IsHoldingShortAtRest(ac), 180, null);
        Assert.True(settled > 0, "never came to rest holding short of runway 30");
        double offStopFt = GeoMath.DistanceNm(ac.Position, stop) * GeoMath.FeetPerNm;
        Assert.True(
            offStopFt <= 10.0,
            $"{Callsign} held {offStopFt:F0} ft from the runway 30 stop ({stopToNodeFt:F0} ft back from bar node {bar.NodeId})"
        );

        // The stop puts the nose on the bar: at rest the aircraft is at or short of it, never past (AIM 2-3-5.b.3).
        double restShortOfStopFt = StationFt(route, stop) - StationFt(route, ac.Position);
        Assert.True(
            restShortOfStopFt >= -0.5,
            $"{Callsign} came to rest {-restShortOfStopFt:F1} ft past the runway 30 stop: the nose is over the bar"
        );
    }

    [Fact]
    public void TaxiTeTUHsT_TargetContinuesStraightAhead_FloorsAgainstTheOtherBranchesNotT()
    {
        if (SpawnAtSpotE() is not { } world)
        {
            return;
        }

        (SimulationEngine engine, AirportGroundLayout layout, AircraftState ac) = world;
        Assert.True(engine.SendCommand(Callsign, "TAXI TE T U HS T").Success);
        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;
        HoldShortPoint bar = Assert.Single(route.HoldShortPoints);
        foreach (string w in route.Warnings)
        {
            _output.WriteLine($"warning: {w}");
        }

        Assert.DoesNotContain(route.Warnings, w => w.StartsWith("holding short of TWY T — wingtip clearance from T ", StringComparison.Ordinal));

        // The floor is held against the junction's branches that are not straight ahead: U and TC.
        double lengthFt = FaaAircraftDatabase.Get("B738")!.LengthFt!.Value;
        LatLon nose = AlongRouteFrom(route, new LatLon(bar.Latitude!.Value, bar.Longitude!.Value), lengthFt / 2.0);
        double noseToBranchesFt = Math.Min(
            SfoGroundHarness.DistanceToTaxiwayFt(layout, "U", nose),
            SfoGroundHarness.DistanceToTaxiwayFt(layout, "TC", nose)
        );
        double floorFt = HoldShortAnnotator.WingtipClearanceFloorFt(layout.Runways.Max(r => r.WidthFt));
        const string prefix = "holding short of TWY T — wingtip clearance from TC/U not assured (";
        string? warning = route.Warnings.SingleOrDefault(w =>
            w.StartsWith("holding short of TWY T — wingtip clearance from ", StringComparison.Ordinal)
        );
        _output.WriteLine($"nose {noseToBranchesFt:F1} ft from U/TC, floor {floorFt:F0} ft");

        // OAK's widest runway is 150 ft (ADG V): the floor is 132 ft nose to U/TC. The walk-back clamps at the previous
        // junction on TE before reaching it, so the stop falls short of the floor and the route warns, naming U and TC.
        Assert.Equal(132.0, floorFt);
        Assert.NotNull(warning);
        Assert.StartsWith(prefix, warning);
        int reportedFt = int.Parse(warning[prefix.Length..^" ft)".Length], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(
            Math.Abs(reportedFt - noseToBranchesFt) <= 1.0,
            $"warning reports {reportedFt} ft; the nose is {noseToBranchesFt:F1} ft from U/TC"
        );
        Assert.True(
            noseToBranchesFt < floorFt,
            $"nose is {noseToBranchesFt:F0} ft from U/TC, clear of the {floorFt:F0} ft floor, yet the route warns"
        );
    }

    [Fact]
    public void TaxiBZHsZ_BranchNamedLikeTheArrivingTaxiway_IsMeasuredByItsOwnEdgesNotTheAircraftsLine()
    {
        // SFO: B runs straight on into Z at a node where a second B edge forks off. Looked up by name, B's centreline
        // would include the line the aircraft arrives on and measure the nose at 0 ft from it.
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is not { } layout)
        {
            return;
        }

        (GroundNode fork, GroundEdge arriving) = layout
            .Nodes.Values.SelectMany(n => n.Edges.OfType<GroundEdge>().Where(e => e.TaxiwayName == "B").Select(e => (Node: n, Edge: e)))
            .First(c =>
                (c.Node.Edges.OfType<GroundEdge>().Count(e => e.TaxiwayName == "B") >= 2)
                && c.Node.Edges.OfType<GroundEdge>()
                    .Any(z =>
                        (z.TaxiwayName == "Z")
                        && (
                            GeoMath.AbsBearingDifference(
                                GeoMath.BearingTo(c.Node.Position, z.OtherNode(c.Node).Position),
                                GeoMath.BearingTo(c.Node.Position, c.Edge.OtherNode(c.Node).Position)
                            ) >= 160.0
                        )
                    )
            );
        GroundNode onB = arriving.OtherNode(fork);
        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "SFO",
                AutoCrossRunway = true,
            },
        };
        AircraftState ac = SfoGroundHarness.SpawnAt(
            new SfoGround(engine, layout),
            Callsign,
            "B738",
            (onB, new TrueHeading(GeoMath.BearingTo(onB.Position, fork.Position))),
            new HoldingInPositionPhase()
        );

        CommandResult result = engine.SendCommand(Callsign, "TAXI B Z HS Z");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;
        foreach (string w in route.Warnings)
        {
            _output.WriteLine($"node {fork.Id}: warning: {w}");
        }

        HoldShortPoint bar = Assert.Single(route.HoldShortPoints, h => h.TargetName == "Z");
        Assert.Equal(fork.Id, bar.NodeId);
        // The floor is held against the B fork and Q, measured from their own edges: the nose is well clear of them, not
        // 0 ft from the B line it arrives on.
        const string prefix = "holding short of TWY Z — wingtip clearance from B/Q not assured (";
        string warning = Assert.Single(route.Warnings, w => w.StartsWith(prefix, StringComparison.Ordinal));
        int reportedFt = int.Parse(warning[prefix.Length..^" ft)".Length], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(reportedFt >= 50, $"nose reported {reportedFt} ft from B/Q: measured against the aircraft's own line");
    }

    [Fact]
    public void TaxiCAHsA_PureNameChange_HoldsALengthPlus30BackWithNoWarning()
    {
        // OAK's C runs straight on into A at a node with no other branch (C ends, A begins): the nearest real
        // pure name-change junction at OAK.
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        GroundNode change = layout.Nodes.Values.Single(n =>
            (n.Edges.Count == 2) && n.Edges.Any(e => e.TaxiwayName == "A") && n.Edges.Any(e => e.TaxiwayName == "C")
        );
        GroundNode onC = change.Edges.First(e => e.TaxiwayName == "C").OtherNode(change);
        SimulationEngine engine = BuildEngine(groundData);
        AircraftState ac = SfoGroundHarness.SpawnAt(
            new SfoGround(engine, layout),
            Callsign,
            "B738",
            (onC, new TrueHeading(GeoMath.BearingTo(onC.Position, change.Position))),
            new HoldingInPositionPhase()
        );

        CommandResult result = engine.SendCommand(Callsign, "TAXI C A HS A");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;
        HoldShortPoint bar = Assert.Single(route.HoldShortPoints, h => h.TargetName == "A");
        Assert.Equal(change.Id, bar.NodeId);
        double stopToNodeFt = GeoMath.DistanceNm(new LatLon(bar.Latitude!.Value, bar.Longitude!.Value), change.Position) * GeoMath.FeetPerNm;
        double lengthFt = FaaAircraftDatabase.Get("B738")!.LengthFt!.Value;
        Assert.True(
            Math.Abs(stopToNodeFt - (lengthFt + 30.0)) <= 1.0,
            $"stop {stopToNodeFt:F1} ft back from the C/A node; expected length + 30 = {lengthFt + 30.0:F1}"
        );
        Assert.DoesNotContain(route.Warnings, w => w.Contains("wingtip clearance", StringComparison.Ordinal));
    }

    [Fact]
    public void TaxiTeTUHsT_ReadsBackTheRouteAsIssued()
    {
        if (SpawnAtSpotE() is not { } world)
        {
            return;
        }

        (SimulationEngine engine, AirportGroundLayout _, AircraftState ac) = world;
        CommandResult result = engine.SendCommand(Callsign, "TAXI TE T U HS T");
        Assert.True(result.Success, result.Message);
        string message = Assert.IsType<string>(result.Message);
        _output.WriteLine(message);
        Assert.StartsWith("Taxi via TE T U HS T [", message);
        Assert.Contains("route ends at U, no destination given", message);
        Assert.All(ac.Ground.AssignedTaxiRoute!.Segments, s => Assert.Equal("TE", s.TaxiwayName));
    }

    /// <summary>
    /// Distance (ft) along the route's node-to-node chords from the route's start to <paramref name="point"/>, projected
    /// onto the nearest chord.
    /// </summary>
    private static double StationFt(TaxiRoute route, LatLon point)
    {
        int onIndex = Enumerable
            .Range(0, route.Segments.Count)
            .MinBy(i => GeoMath.DistanceToSegmentFt(point, route.Segments[i].Edge.FromNode.Position, route.Segments[i].Edge.ToNode.Position));
        double stationFt = 0.0;
        for (int i = 0; i < onIndex; i++)
        {
            stationFt += GeoMath.DistanceNm(route.Segments[i].Edge.FromNode.Position, route.Segments[i].Edge.ToNode.Position) * GeoMath.FeetPerNm;
        }

        LatLon from = route.Segments[onIndex].Edge.FromNode.Position;
        double chordBearing = GeoMath.BearingTo(from, route.Segments[onIndex].Edge.ToNode.Position);
        double offDeg = GeoMath.AbsBearingDifference(chordBearing, GeoMath.BearingTo(from, point));
        return stationFt + (GeoMath.DistanceNm(from, point) * GeoMath.FeetPerNm * Math.Cos(offDeg * Math.PI / 180.0));
    }

    /// <summary>
    /// The point <paramref name="aheadFt"/> further along the route's node-to-node chords from <paramref name="start"/>,
    /// which lies on one of them.
    /// </summary>
    private static LatLon AlongRouteFrom(TaxiRoute route, LatLon start, double aheadFt)
    {
        int onIndex = Enumerable
            .Range(0, route.Segments.Count)
            .MinBy(i => GeoMath.DistanceToSegmentFt(start, route.Segments[i].Edge.FromNode.Position, route.Segments[i].Edge.ToNode.Position));
        LatLon from = start;
        double remainingFt = aheadFt;
        for (int i = onIndex; i < route.Segments.Count; i++)
        {
            LatLon to = route.Segments[i].Edge.ToNode.Position;
            double legFt = GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;
            if (remainingFt <= legFt)
            {
                return GeoMath.ProjectPoint(from, new TrueHeading(GeoMath.BearingTo(from, to)), remainingFt / GeoMath.FeetPerNm);
            }

            remainingFt -= legFt;
            from = to;
        }

        return from;
    }

    private static bool IsHoldingShortAtRest(AircraftState ac) =>
        (ac.GroundSpeed < SfoGroundHarness.StationarySpeedKts) && (ac.Phases?.CurrentPhase is HoldingShortPhase);

    private static SimulationEngine BuildEngine(TestAirportGroundData groundData) =>
        new(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunway = true,
            },
        };

    private (SimulationEngine Engine, AirportGroundLayout Layout, AircraftState Aircraft)? SpawnAtSpotE()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        SimulationEngine engine = BuildEngine(groundData);
        GroundNode spot = layout.FindSpotNodeByName("E")!;
        GroundNode junction = layout.FindIntersectionNode("TE", "T")!;
        GroundNode toward = spot.Edges.Select(e => e.OtherNode(spot)).MinBy(n => GeoMath.DistanceNm(n.Position, junction.Position))!;
        AircraftState ac = SfoGroundHarness.SpawnAt(
            new SfoGround(engine, layout),
            Callsign,
            "B738",
            (spot, new TrueHeading(GeoMath.BearingTo(spot.Position, toward.Position))),
            new HoldingInPositionPhase()
        );
        return (engine, layout, ac);
    }
}
