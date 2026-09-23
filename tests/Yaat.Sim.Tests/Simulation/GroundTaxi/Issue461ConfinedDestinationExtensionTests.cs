using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Acceptance;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Issue #461: a TAXI whose named route does not reach its destination is still accepted when the destination needs only
/// one more movement-area taxiway. The aircraft taxis the route as issued, the destination picks the way along the last
/// cleared taxiway, and it holds short of the taxiway it would need (<see cref="HoldShortReason.RouteIncomplete"/>),
/// with the controller told which taxiway is missing. The extension from the last cleared taxiway to a gate stays on
/// the cleared taxiways, ramp taxilanes and the apron; it never threads an uncleared taxiway.
/// </summary>
[Collection("Acceptance")]
public class Issue461ConfinedDestinationExtensionTests(ITestOutputHelper output)
{
    private const string OakPostLandingBundlePath = "TestData/s2-oak3-follow-runaway-ias-recording.yaat-bug-report-bundle.zip";

    /// <summary>OAK 28R hold-short on G, north side (node 508): where a 28R arrival exiting on G holds.</summary>
    private const int OakGNorthOf28RHoldShortNode = 508;

    /// <summary>North along G, towards SIG1.</summary>
    private const double OakNorthAlongGHeadingDeg = 0.0;

    /// <summary>OAK C/C1 junction (node 356) on C, west of the runway 33 crossing.</summary>
    private const int OakCC1JunctionNode = 356;

    /// <summary>West along C, towards the C/F junction.</summary>
    private const double OakWestAlongCHeadingDeg = 292.0;

    /// <summary>SFO UAL2627 holding after exiting runway 28L (see <c>SfoTaxiBToF1RampConnectorTests</c>).</summary>
    private static readonly (LatLon Position, TrueHeading Heading) SfoAfterExit28LPose = (
        new LatLon(37.61935192788897, -122.3796147778547),
        new TrueHeading(182.16)
    );

    /// <summary>SKW3398 at bundle t≈878: holding short of K on B, nosed 297.9° true.</summary>
    private static readonly (LatLon Position, TrueHeading Heading) Skw3398HeldShortOfKPose = (
        new LatLon(37.621827131692896, -122.38554745714856),
        new TrueHeading(297.9)
    );

    private const int SfoKOnBJunctionNode = 135;

    /// <summary>
    /// The issue's example: <c>RWY 28R TAXI F C HS 33</c> from OAK gate OLD1. F and C never reach runway 28R's
    /// departure end; B does, from the east end of C. The aircraft taxis F, C (holding short of 33 on the way) and holds
    /// short of B.
    /// </summary>
    [Fact]
    public void RunwayDestination_MissingTaxiway_HoldsShortWithNote()
    {
        AirportGroundLayout? layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        GroundNode gate = Assert.IsType<GroundNode>(layout.FindParkingByName("OLD1"));
        AircraftState aircraft = MakeAircraft(layout, gate.Position, gate.TrueHeading ?? new TrueHeading(7), new AtParkingPhase());
        aircraft.Ground.ParkingSpot = "OLD1";
        var taxi = new TaxiCommand(Path: ["F", "C"], HoldShorts: [HoldShortTarget.Parse("33")], DestinationRunway: "28R");

        TaxiRoute route = AssertHoldsShortOfMissingTaxiway(
            layout,
            aircraft,
            taxi,
            "B",
            "Holding short of B: route to RWY 28R needs B, not in clearance"
        );
        Assert.Contains(route.HoldShortPoints, h => (h.Reason == HoldShortReason.ExplicitHoldShort) && (h.TargetName?.Contains("33") == true));
    }

    /// <summary>
    /// A gate: <c>TAXI C @OLD1</c> from the C/C1 junction. OLD1 hangs off F, which the clearance does not name, and the
    /// run along F to the stand (about 2,900 ft) is far longer than an implied lead-in, so the aircraft taxis C to the
    /// C/F junction and holds short of F.
    /// </summary>
    [Fact]
    public void LongLeadIn_Holds_OLD1ViaF()
    {
        AirportGroundLayout? layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        GroundNode start = layout.Nodes[OakCC1JunctionNode];
        AircraftState aircraft = MakeAircraft(layout, start.Position, new TrueHeading(OakWestAlongCHeadingDeg), new HoldingInPositionPhase());
        var taxi = new TaxiCommand(Path: ["C"], HoldShorts: [], DestinationParking: "OLD1");

        AssertHoldsShortOfMissingTaxiway(layout, aircraft, taxi, "F", "Holding short of F: route to @OLD1 needs F, not in clearance");
    }

    /// <summary>
    /// A short implied lead-in: OAK <c>TAXI G @SIG1</c> from the 28R hold-short on G (node 508). SIG1 hangs off D, which the
    /// clearance does not name, but after D the route drives only apron to SIG1 and the run on D is well under
    /// <see cref="Yaat.Sim.Data.Airport.Pathfinding.SegmentExpander.MaxImpliedLeadInFt"/>, so D is driven silently: the
    /// route reaches SIG1, nothing is warned, and the readback is the clearance as issued.
    /// </summary>
    [Fact]
    public void ShortLeadIn_Implied_SIG1ViaD()
    {
        AirportGroundLayout? layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        GroundNode start = layout.Nodes[OakGNorthOf28RHoldShortNode];
        AircraftState aircraft = MakeAircraft(layout, start.Position, new TrueHeading(OakNorthAlongGHeadingDeg), new HoldingInPositionPhase());
        var taxi = new TaxiCommand(Path: ["G"], HoldShorts: [], DestinationParking: "SIG1");

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()} ({route.Segments.Count} segments)");
        GroundNode sig1 = Assert.IsType<GroundNode>(layout.FindHelipadByName("SIG1") ?? layout.FindParkingByName("SIG1"));
        Assert.Equal(sig1.Id, route.Segments[^1].ToNodeId);
        Assert.Contains(route.Segments, s => (s.Edge.Edge is not GroundArc) && s.Edge.Edge.MatchesTaxiway("D"));
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason == HoldShortReason.RouteIncomplete);
        Assert.Empty(route.Warnings);
        Assert.Equal("Taxi via G @SIG1", result.Message);
    }

    /// <summary>
    /// A through-taxiway missing at the start: N9225L <c>TAXI D @NEW1</c> from the E exit of 28R. E does not meet D; C
    /// joins them, and the clearance resolves once C is added, so the aircraft holds short of C on E — not of a taxiway
    /// further along the route.
    /// </summary>
    [Fact]
    public void ThroughTaxiway_HoldsAtRealMissingLink_N9225L()
    {
        if (ReplayN9225LToEExit() is not { } engine)
        {
            return;
        }

        CommandResult result = engine.SendCommand("N9225L", "TAXI D @NEW1");
        output.WriteLine($"TAXI D @NEW1: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft("N9225L"));
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()} ({route.Segments.Count} segments)");
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(aircraft.Ground.Layout);

        const string Note = "Holding short of C: route to @NEW1 needs C, not in clearance";
        HoldShortPoint incomplete = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.RouteIncomplete);
        Assert.Equal("C", incomplete.TargetName);
        Assert.Equal(route.Segments[^1].ToNodeId, incomplete.NodeId);
        Assert.Contains(layout.Nodes[incomplete.NodeId].Edges, e => e.MatchesTaxiway("C"));
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.MatchesTaxiway("C"));
        Assert.All(
            route.Segments.Where(s => (s.Edge.Edge is not GroundArc) && !VirtualNode.IsVirtualEdge(s.Edge.Edge)),
            s => Assert.True(s.Edge.Edge.MatchesTaxiway("E"), $"segment {s.FromNodeId}->{s.ToNodeId} is on {s.TaxiwayName}, not E")
        );
        Assert.Single(route.Warnings, w => w == Note);
        Assert.Contains(Note, result.Message);
    }

    /// <summary>
    /// The missing link at the start cannot be held short of: N9225L <c>TAXI G @OLD1</c> from the E exit. E does not meet
    /// G (C joins them), and even with C added the clearance never reaches OLD1 (it hangs off F, far beyond an implied
    /// lead-in), so the TAXI is refused, naming the link the start needs.
    /// </summary>
    [Fact]
    public void OffRoute_RefusalNamesMissingLink()
    {
        if (ReplayN9225LToEExit() is not { } engine)
        {
            return;
        }

        CommandResult result = engine.SendCommand("N9225L", "TAXI G @OLD1");
        output.WriteLine($"TAXI G @OLD1: {result.Success} — {result.Message}");
        Assert.False(result.Success, result.Message);
        Assert.StartsWith("Unable, route to G from E needs C, not in clearance (", result.Message);
    }

    /// <summary>
    /// SFO <c>TAXI B @F1</c>: F1 is reached from B along the T9 ramp taxilane and the apron, none of it movement area
    /// the clearance left out, so the route resolves all the way to the stand with nothing to warn about.
    /// </summary>
    [Fact]
    public void Destination_ViaRampLanesOnly_ResolvesWithoutNote()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode start = ground
            .Layout.Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway("B")))
            .OrderBy(n => GeoMath.DistanceNm(n.Position, SfoAfterExit28LPose.Position))
            .First();
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            "UAL2627",
            "A320",
            (start, SfoAfterExit28LPose.Heading),
            new HoldingInPositionPhase()
        );
        aircraft.Position = SfoAfterExit28LPose.Position;

        CommandResult result = ground.Engine.SendCommand("UAL2627", "TAXI B @F1");
        output.WriteLine($"TAXI B @F1: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);

        GroundNode f1 = Assert.IsType<GroundNode>(ground.Layout.FindParkingByName("F1"));
        Assert.Equal(f1.Id, route.Segments[^1].ToNodeId);
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason == HoldShortReason.RouteIncomplete);
        Assert.Empty(route.Warnings);
    }

    /// <summary>
    /// Issue #454's destination cut still wins over the fallback: SKW3398 <c>TAXI B K A T5A @D1</c> drives T5A and cuts
    /// across the apron straight to D1, with no route-incomplete hold.
    /// </summary>
    [Fact]
    public void Issue454Cut_StillTakenAhead_OfFallback()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var hold = new HoldShortPoint
        {
            NodeId = SfoKOnBJunctionNode,
            Reason = HoldShortReason.ExplicitHoldShort,
            TargetName = "K",
        };
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            "SKW3398",
            "E75L",
            (ground.Layout.Nodes[SfoKOnBJunctionNode], Skw3398HeldShortOfKPose.Heading),
            new HoldingShortPhase(hold)
        );
        aircraft.Position = Skw3398HeldShortOfKPose.Position;
        aircraft.Ground.CurrentTaxiway = "B";

        CommandResult result = ground.Engine.SendCommand("SKW3398", "TAXI B K A T5A @D1");
        output.WriteLine($"TAXI B K A T5A @D1: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);

        GroundNode d1 = Assert.IsType<GroundNode>(ground.Layout.FindParkingByName("D1"));
        Assert.Equal(d1.Id, route.Segments[^1].ToNodeId);
        Assert.True(VirtualNode.IsVirtualEdge(route.Segments[^1].Edge.Edge), "the route should end on the direct apron cut to D1");
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason == HoldShortReason.RouteIncomplete);
        Assert.DoesNotContain(route.Warnings, w => w.StartsWith("Holding short of", StringComparison.Ordinal));
    }

    /// <summary>
    /// Issue #454: an aircraft standing on T with no reported current taxiway (UAL733's pose — the setup of
    /// <c>HoldShortAnnotatorTests.TaxiwayHoldShort_CrosserOnT_IsNotPinnedByTheHolder</c>). <c>TAXI A @E8</c> starts on the
    /// bit of T the aircraft stands on, so the taxiway under it counts as the one it occupies: the clearance resolves and
    /// the readback names T rather than treating it as pavement the clearance left out.
    /// </summary>
    [Fact]
    public void NullCurrentTaxiway_OnTEdge_CountsAsOccupyingT()
    {
        if (SfoGroundHarness.Build(output, autoCross: true) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            "UAL733",
            "A320",
            (VirtualNode.Create(37.62094280885291, -122.38247885498703), new TrueHeading(208.98)),
            new HoldingInPositionPhase()
        );
        Assert.Null(aircraft.Ground.CurrentTaxiway);

        // The premise: the aircraft's position projects onto a T edge, close enough to count as standing on it.
        AirportGroundLayout.NearestTaxiEdge nearest = Assert.IsType<AirportGroundLayout.NearestTaxiEdge>(
            ground.Layout.FindNearestTaxiEdge(aircraft.Position)
        );
        output.WriteLine($"nearest edge: {nearest.Edge.TaxiwayName} at {nearest.DistNm * GeoMath.FeetPerNm:F2} ft");
        Assert.Equal("T", nearest.Edge.TaxiwayName);

        CommandResult result = ground.Engine.SendCommand("UAL733", "TAXI A @E8");
        output.WriteLine($"TAXI A @E8: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        SfoGroundHarness.DumpRoute(output, Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute));
        Assert.StartsWith("Taxi via T A", result.Message);
    }

    /// <summary>
    /// A clearance with no destination through the start-link check: N9225L <c>TAXI D</c> from the E exit. E does not
    /// meet D (C joins them), and <c>TAXI C D</c> reaches D, so the aircraft holds short of C on E; the note names the
    /// clearance's last taxiway as where the route goes.
    /// </summary>
    [Fact]
    public void NoDestination_StartLink_HoldsShortOfC_N9225L()
    {
        if (ReplayN9225LToEExit() is not { } engine)
        {
            return;
        }

        CommandResult result = engine.SendCommand("N9225L", "TAXI D");
        output.WriteLine($"TAXI D: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(engine.FindAircraft("N9225L")?.Ground.AssignedTaxiRoute);
        HoldShortPoint incomplete = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.RouteIncomplete);
        Assert.Equal("C", incomplete.TargetName);
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.MatchesTaxiway("C"));
        Assert.Contains("Holding short of C: route to D needs C, not in clearance", route.Warnings);
    }

    /// <summary>
    /// At the end of a route that could not reach its destination, RES and CROSS do not apply: the controller is told to
    /// issue a TAXI that includes the taxiway the route needs. (A CROSS of a runway the route has no bar for is refused
    /// by the dispatcher before the phase is asked, so CROSS is checked at the phase.)
    /// </summary>
    [Fact]
    public void AtRouteIncompleteHold_ResAndCross_Rejected_N9225L()
    {
        if (HoldN9225LShortOfC() is not { } engine)
        {
            return;
        }

        const string Hint = "holding short of C where the route issued ends — issue a TAXI that includes C";
        CommandResult result = engine.SendCommand("N9225L", "RES");
        output.WriteLine($"RES: {result.Success} — {result.Message}");
        Assert.False(result.Success, result.Message);
        Assert.Contains(Hint, result.Message);
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(engine.FindAircraft("N9225L")?.Phases?.CurrentPhase);
        CommandAcceptance cross = hold.CanAcceptCommand(CanonicalCommandType.CrossRunway);
        Assert.True(cross.IsRejected);
        Assert.Equal(Hint, cross.Reason);
    }

    /// <summary>
    /// <c>HS C</c> at the end of an incomplete route keeps the aircraft there: RES then releases the hold, but the route
    /// goes no further, so it does not move. A new TAXI that names C takes it on.
    /// </summary>
    [Fact]
    public void AtRouteIncompleteHold_HsOfMissingTaxiway_KeepsAircraftUntilNewTaxi_N9225L()
    {
        if (HoldN9225LShortOfC() is not { } engine)
        {
            return;
        }

        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft("N9225L"));
        LatLon heldAt = aircraft.Position;
        CommandResult hs = engine.SendCommand("N9225L", "HS C");
        output.WriteLine($"HS C: {hs.Success} — {hs.Message}");
        Assert.True(hs.Success, hs.Message);
        CommandResult res = engine.SendCommand("N9225L", "RES");
        output.WriteLine($"RES: {res.Success} — {res.Message}");
        Assert.True(res.Success, res.Message);
        for (int t = 0; t < 30; t++)
        {
            engine.TickOneSecond();
        }

        double driftFt = GeoMath.DistanceNm(heldAt, aircraft.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"after RES: {aircraft.Phases?.CurrentPhase?.Name}, moved {driftFt:F1} ft");
        Assert.True(driftFt < 5.0, $"the route ends at the hold, so RES moves nothing; moved {driftFt:F1} ft");

        CommandResult taxi = engine.SendCommand("N9225L", "TAXI C D @NEW1");
        output.WriteLine($"TAXI C D @NEW1: {taxi.Success} — {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);
        for (int t = 0; t < 60; t++)
        {
            engine.TickOneSecond();
        }

        double movedFt = GeoMath.DistanceNm(heldAt, aircraft.Position) * GeoMath.FeetPerNm;
        Assert.True(movedFt > 50.0, $"TAXI C D @NEW1 should take it on; moved {movedFt:F1} ft");
    }

    /// <summary>N9225L after <c>TAXI D @NEW1</c> from the E exit, stopped at the end of its route holding short of C.</summary>
    private SimulationEngine? HoldN9225LShortOfC()
    {
        if (ReplayN9225LToEExit() is not { } engine)
        {
            return null;
        }

        CommandResult result = engine.SendCommand("N9225L", "TAXI D @NEW1");
        Assert.True(result.Success, result.Message);
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft("N9225L"));
        for (int t = 0; t < 180; t++)
        {
            engine.TickOneSecond();
            if (
                (aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.RouteIncomplete })
                && (aircraft.GroundSpeed < 1.0)
            )
            {
                return engine;
            }
        }

        throw new Xunit.Sdk.XunitException($"N9225L never held short of C: {aircraft.Phases?.CurrentPhase?.Name}");
    }

    /// <summary>
    /// The S2-OAK-3 bundle replayed to just before N9225L's recorded <c>TAXI D @NEW1</c> (t=423), then ticked until it
    /// settles holding after its exit onto E — the setup of <c>OakPostLandingReversalsTests</c>. Null without navdata or
    /// the bundle.
    /// </summary>
    private SimulationEngine? ReplayN9225LToEExit()
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(OakPostLandingBundlePath);
        TestVnasData.EnsureInitialized();
        if ((archive is null) || (TestVnasData.NavigationDb is null))
        {
            archive?.Dispose();
            return null;
        }

        using (archive)
        {
            SimLogBuilder.CreateForTest(output).InitializeSimLog();
            var engine = new SimulationEngine(new TestAirportGroundData(FilletMode.Standard));
            engine.Replay(archive.ToBaseSessionRecording(), 423);
            for (int t = 0; (t < 120) && (engine.FindAircraft("N9225L")?.Phases?.CurrentPhase?.Name != "Holding After Exit"); t++)
            {
                engine.TickOneSecond();
            }

            AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft("N9225L"));
            Assert.Equal("Holding After Exit", aircraft.Phases?.CurrentPhase?.Name);
            Assert.Equal("E", aircraft.Ground.CurrentTaxiway);
            return engine;
        }
    }

    private AirportGroundLayout? LoadOak()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new TestAirportGroundData().GetLayout("OAK");
    }

    private static AircraftState MakeAircraft(AirportGroundLayout layout, LatLon position, TrueHeading heading, Phase startPhase)
    {
        var aircraft = new AircraftState
        {
            Callsign = "N461TX",
            AircraftType = "B738",
            Position = position,
            TrueHeading = heading,
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(startPhase);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    /// <summary>
    /// The TAXI is accepted; the route ends at a <see cref="HoldShortReason.RouteIncomplete"/> hold short of
    /// <paramref name="missing"/>, drives no edge of it, and carries <paramref name="warning"/> exactly once.
    /// </summary>
    private TaxiRoute AssertHoldsShortOfMissingTaxiway(
        AirportGroundLayout layout,
        AircraftState aircraft,
        TaxiCommand taxi,
        string missing,
        string warning
    )
    {
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()} ({route.Segments.Count} segments)");
        foreach (HoldShortPoint h in route.HoldShortPoints)
        {
            output.WriteLine($"hold-short: {h.TargetName} at {h.NodeId} ({h.Reason})");
        }

        HoldShortPoint incomplete = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.RouteIncomplete);
        Assert.Equal(missing, incomplete.TargetName);
        Assert.Equal(route.Segments[^1].ToNodeId, incomplete.NodeId);
        Assert.Contains(layout.Nodes[incomplete.NodeId].Edges, e => e.MatchesTaxiway(missing));
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.MatchesTaxiway(missing));
        Assert.Single(route.Warnings, w => w == warning);
        Assert.Contains(warning, result.Message);
        return route;
    }
}
