using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class GroundCommandHandlerTests
{
    private static readonly ILogger Logger = new NullLogger<GroundCommandHandlerTests>();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AircraftState MakeGroundAircraft(double lat = 37.728, double lon = -122.218)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            Heading = 280,
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            Departure = "OAK",
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static AircraftState MakeAircraftAtParking()
    {
        var ac = MakeGroundAircraft();
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0,
            Logger = NullLogger.Instance,
        };
        ac.Phases.Start(ctx);
        return ac;
    }

    private static AirportGroundLayout MakeSimpleLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        // Simple graph: nodes 1-2-3 on taxiway A
        var node1 = new GroundNode
        {
            Id = 1,
            Latitude = 37.728,
            Longitude = -122.218,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node2 = new GroundNode
        {
            Id = 2,
            Latitude = 37.729,
            Longitude = -122.218,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node3 = new GroundNode
        {
            Id = 3,
            Latitude = 37.730,
            Longitude = -122.218,
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = new RunwayIdentifier("28R"),
        };

        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;
        layout.Nodes[3] = node3;

        var edge12 = new GroundEdge
        {
            FromNodeId = 1,
            ToNodeId = 2,
            TaxiwayName = "A",
            DistanceNm = GeoMath.DistanceNm(node1.Latitude, node1.Longitude, node2.Latitude, node2.Longitude),
        };
        var edge23 = new GroundEdge
        {
            FromNodeId = 2,
            ToNodeId = 3,
            TaxiwayName = "A",
            DistanceNm = GeoMath.DistanceNm(node2.Latitude, node2.Longitude, node3.Latitude, node3.Longitude),
        };

        layout.Edges.Add(edge12);
        layout.Edges.Add(edge23);
        node1.Edges.Add(edge12);
        node2.Edges.Add(edge12);
        node2.Edges.Add(edge23);
        node3.Edges.Add(edge23);

        return layout;
    }

    private static TaxiRoute MakeRouteWithHoldShort(string runwayId)
    {
        return new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment
                {
                    FromNodeId = 1,
                    ToNodeId = 2,
                    TaxiwayName = "A",
                    Edge = new GroundEdge
                    {
                        FromNodeId = 1,
                        ToNodeId = 2,
                        TaxiwayName = "A",
                        DistanceNm = 0.1,
                    },
                },
            ],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = runwayId,
                },
            ],
        };
    }

    // -------------------------------------------------------------------------
    // TryTaxi
    // -------------------------------------------------------------------------

    [Fact]
    public void TryTaxi_NoLayout_Fails()
    {
        var ac = MakeGroundAircraft();
        var cmd = new TaxiCommand(["A"], [], DestinationRunway: "28R");

        var result = GroundCommandHandler.TryTaxi(ac, cmd, null, null);

        Assert.False(result.Success);
        Assert.Contains("No airport ground layout", result.Message!);
    }

    [Fact]
    public void TryTaxi_UnknownTaxiway_Fails()
    {
        var ac = MakeGroundAircraft();
        var layout = MakeSimpleLayout();
        var cmd = new TaxiCommand(["ZZZZZ"], [], DestinationRunway: "28R");

        var result = GroundCommandHandler.TryTaxi(ac, cmd, layout, null);

        Assert.False(result.Success);
    }

    [Fact]
    public void TryTaxi_ValidPath_Succeeds()
    {
        var ac = MakeGroundAircraft();
        var layout = MakeSimpleLayout();
        var cmd = new TaxiCommand(["A"], [], DestinationRunway: "28R");

        var result = GroundCommandHandler.TryTaxi(ac, cmd, layout, null);

        Assert.True(result.Success);
        Assert.NotNull(ac.AssignedTaxiRoute);
        Assert.True(ac.AssignedTaxiRoute!.Segments.Count > 0);
    }

    [Fact]
    public void TryTaxi_AutoCrossRunway_ClearsHoldShorts()
    {
        var ac = MakeGroundAircraft();
        var layout = MakeSimpleLayout();
        var cmd = new TaxiCommand(["A"], [], DestinationRunway: "28R");

        var result = GroundCommandHandler.TryTaxi(ac, cmd, layout, null, autoCrossRunway: true);

        Assert.True(result.Success);
        // All RunwayCrossing hold-shorts should be pre-cleared
        foreach (var hs in ac.AssignedTaxiRoute!.HoldShortPoints)
        {
            if (hs.Reason == HoldShortReason.RunwayCrossing)
            {
                Assert.True(hs.IsCleared);
            }
        }
    }

    // -------------------------------------------------------------------------
    // TryPushback
    // -------------------------------------------------------------------------

    [Fact]
    public void TryPushback_NotAtParking_Fails()
    {
        var ac = MakeGroundAircraft();
        // Phases empty (no AtParkingPhase)
        var cmd = new PushbackCommand();

        var result = GroundCommandHandler.TryPushback(ac, cmd, null);

        Assert.False(result.Success);
        Assert.Contains("at parking", result.Message!);
    }

    [Fact]
    public void TryPushback_AtParking_NoArgs_Succeeds()
    {
        var ac = MakeAircraftAtParking();
        var cmd = new PushbackCommand();

        var result = GroundCommandHandler.TryPushback(ac, cmd, null);

        Assert.True(result.Success);
        Assert.Contains("Pushing back", result.Message!);
    }

    [Fact]
    public void TryPushback_WithTaxiway_ResolvesTarget()
    {
        var ac = MakeAircraftAtParking();
        var layout = MakeSimpleLayout();
        var cmd = new PushbackCommand(Taxiway: "A");

        var result = GroundCommandHandler.TryPushback(ac, cmd, layout);

        Assert.True(result.Success);
        Assert.Contains("onto A", result.Message!);
    }

    [Fact]
    public void TryPushback_WithHeading_IncludesInMessage()
    {
        var ac = MakeAircraftAtParking();
        var cmd = new PushbackCommand(Heading: 180);

        var result = GroundCommandHandler.TryPushback(ac, cmd, null);

        Assert.True(result.Success);
        Assert.Contains("180", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryCrossRunway
    // -------------------------------------------------------------------------

    [Fact]
    public void TryCrossRunway_FromHoldingShort_SatisfiesClearance()
    {
        var ac = MakeGroundAircraft();
        ac.Phases = new PhaseList();
        var holdPhase = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = 3,
                Reason = HoldShortReason.RunwayCrossing,
                TargetName = "28R/10L",
            }
        );
        ac.Phases.Add(holdPhase);
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0,
            Logger = NullLogger.Instance,
        };
        ac.Phases.Start(ctx);

        var cmd = new CrossRunwayCommand("28R");
        var result = GroundCommandHandler.TryCrossRunway(ac, cmd);

        Assert.True(result.Success);
        Assert.Contains("Cross 28R", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_PreClearInRoute_MarksHoldShortCleared()
    {
        var ac = MakeGroundAircraft();
        ac.AssignedTaxiRoute = MakeRouteWithHoldShort("28R/10L");

        // Not currently at a hold-short phase — pre-clear mode
        var cmd = new CrossRunwayCommand("28R");
        var result = GroundCommandHandler.TryCrossRunway(ac, cmd);

        Assert.True(result.Success);
        Assert.True(ac.AssignedTaxiRoute.HoldShortPoints[0].IsCleared);
    }

    [Fact]
    public void TryCrossRunway_NoMatchingHoldShort_Fails()
    {
        var ac = MakeGroundAircraft();
        ac.AssignedTaxiRoute = MakeRouteWithHoldShort("15/33");

        var cmd = new CrossRunwayCommand("28R");
        var result = GroundCommandHandler.TryCrossRunway(ac, cmd);

        Assert.False(result.Success);
        Assert.Contains("No hold-short", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_NoRoute_Fails()
    {
        var ac = MakeGroundAircraft();
        // No route assigned

        var cmd = new CrossRunwayCommand("28R");
        var result = GroundCommandHandler.TryCrossRunway(ac, cmd);

        Assert.False(result.Success);
    }

    // -------------------------------------------------------------------------
    // TryHoldShort
    // -------------------------------------------------------------------------

    [Fact]
    public void TryHoldShort_NotOnGround_Fails()
    {
        var ac = MakeGroundAircraft();
        ac.IsOnGround = false;
        var cmd = new HoldShortCommand("28R");

        var result = GroundCommandHandler.TryHoldShort(ac, cmd, null);

        Assert.False(result.Success);
        Assert.Contains("on the ground", result.Message!);
    }

    [Fact]
    public void TryHoldShort_NoRoute_Fails()
    {
        var ac = MakeGroundAircraft();
        var cmd = new HoldShortCommand("28R");

        var result = GroundCommandHandler.TryHoldShort(ac, cmd, null);

        Assert.False(result.Success);
        Assert.Contains("No taxi route", result.Message!);
    }

    [Fact]
    public void TryHoldShort_NoLayout_Fails()
    {
        var ac = MakeGroundAircraft();
        ac.AssignedTaxiRoute = MakeRouteWithHoldShort("28R/10L");
        var cmd = new HoldShortCommand("B");

        var result = GroundCommandHandler.TryHoldShort(ac, cmd, null);

        Assert.False(result.Success);
        Assert.Contains("No ground layout", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryFollow
    // -------------------------------------------------------------------------

    [Fact]
    public void TryFollow_NoActivePhase_Fails()
    {
        var ac = MakeGroundAircraft();
        ac.Phases = null;
        var cmd = new FollowCommand("UAL123");

        var result = GroundCommandHandler.TryFollow(ac, cmd, null);

        Assert.False(result.Success);
        Assert.Contains("no active phase", result.Message!);
    }

    [Fact]
    public void TryFollow_NotOnGround_Fails()
    {
        var ac = MakeGroundAircraft();
        ac.IsOnGround = false;
        // Use a TaxiingPhase which accepts Follow, so the ground check is reached
        ac.Phases = new PhaseList();
        ac.Phases.Add(new TaxiingPhase());
        ac.Phases.Start(
            new PhaseContext
            {
                Aircraft = ac,
                Targets = ac.Targets,
                Category = AircraftCategory.Jet,
                DeltaSeconds = 0,
                Logger = NullLogger.Instance,
            }
        );
        var cmd = new FollowCommand("UAL123");

        var result = GroundCommandHandler.TryFollow(ac, cmd, null);

        Assert.False(result.Success);
        Assert.Contains("on the ground", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryHoldPosition / TryResumeTaxi
    // -------------------------------------------------------------------------

    [Fact]
    public void TryHoldPosition_OnGround_SetsIsHeld()
    {
        var ac = MakeGroundAircraft();

        var result = GroundCommandHandler.TryHoldPosition(ac);

        Assert.True(result.Success);
        Assert.True(ac.IsHeld);
    }

    [Fact]
    public void TryHoldPosition_NotOnGround_Fails()
    {
        var ac = MakeGroundAircraft();
        ac.IsOnGround = false;

        var result = GroundCommandHandler.TryHoldPosition(ac);

        Assert.False(result.Success);
    }

    [Fact]
    public void TryResumeTaxi_WhenHeld_ClearsIsHeld()
    {
        var ac = MakeGroundAircraft();
        ac.IsHeld = true;

        var result = GroundCommandHandler.TryResumeTaxi(ac);

        Assert.True(result.Success);
        Assert.False(ac.IsHeld);
    }

    [Fact]
    public void TryResumeTaxi_NotHeld_Fails()
    {
        var ac = MakeGroundAircraft();
        ac.IsHeld = false;

        var result = GroundCommandHandler.TryResumeTaxi(ac);

        Assert.False(result.Success);
    }

    [Fact]
    public void Resume_ClearsExplicitHoldShortPhase()
    {
        var ac = MakeGroundAircraft();
        ac.IsOnGround = true;

        var holdShort = new HoldShortPoint
        {
            NodeId = 1,
            Reason = HoldShortReason.ExplicitHoldShort,
            TargetName = "E",
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingShortPhase(holdShort));
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Logger = Logger,
        };
        ac.Phases.Start(ctx);

        Assert.IsType<HoldingShortPhase>(ac.Phases.CurrentPhase);

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand()])]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, null, null, new Random(42), true);

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
    }

    [Fact]
    public void Resume_DoesNotClearRunwayCrossingHoldShortPhase()
    {
        var ac = MakeGroundAircraft();
        ac.IsOnGround = true;

        var holdShort = new HoldShortPoint
        {
            NodeId = 1,
            Reason = HoldShortReason.RunwayCrossing,
            TargetName = "28R",
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingShortPhase(holdShort));
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Logger = Logger,
        };
        ac.Phases.Start(ctx);

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand()])]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, null, null, new Random(42), true);

        Assert.False(result.Success);
    }

    // -------------------------------------------------------------------------
    // TryAssignRunway
    // -------------------------------------------------------------------------

    [Fact]
    public void TryAssignRunway_ValidRunway_SetsAssignedRunway()
    {
        var ac = MakeGroundAircraft();
        var navDb = TestNavDbFactory.WithRunways(TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 280));

        var result = GroundCommandHandler.TryAssignRunway(ac, "28R", navDb);

        Assert.True(result.Success);
        Assert.Contains("Runway 28R", result.Message!);
        Assert.NotNull(ac.Phases);
        Assert.NotNull(ac.Phases!.AssignedRunway);
        Assert.Equal("28R", ac.Phases.AssignedRunway!.Designator);
    }

    [Fact]
    public void TryAssignRunway_InvalidRunway_Fails()
    {
        var ac = MakeGroundAircraft();
        var navDb = TestNavDbFactory.WithRunways(TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 280));

        var result = GroundCommandHandler.TryAssignRunway(ac, "99X", navDb);

        Assert.False(result.Success);
        Assert.Contains("Unknown runway", result.Message!);
    }

    [Fact]
    public void TryAssignRunway_NoRunwayLookup_Fails()
    {
        var ac = MakeGroundAircraft();

        var result = GroundCommandHandler.TryAssignRunway(ac, "28R", null);

        Assert.False(result.Success);
    }

    [Fact]
    public void TryAssignRunway_NullPhases_CreatesPhaseList()
    {
        var ac = MakeGroundAircraft();
        ac.Phases = null;
        var navDb = TestNavDbFactory.WithRunways(TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 280));

        var result = GroundCommandHandler.TryAssignRunway(ac, "28R", navDb);

        Assert.True(result.Success);
        Assert.NotNull(ac.Phases);
    }

    // -------------------------------------------------------------------------
    // TryTaxi auto-detect runway
    // -------------------------------------------------------------------------

    [Fact]
    public void TryTaxi_EndsAtHoldShort_AutoDetectsRunway()
    {
        var ac = MakeGroundAircraft();
        var layout = MakeSimpleLayout();
        // Threshold (Lat1) at node3 position so auto-detect resolves runway
        var navDb = TestNavDbFactory.WithRunways(
            TestRunwayFactory.Make(designator: "28R", airportId: "OAK", thresholdLat: 37.730, thresholdLon: -122.218, heading: 280)
        );
        var cmd = new TaxiCommand(["A"], []);

        var result = GroundCommandHandler.TryTaxi(ac, cmd, layout, navDb);

        Assert.True(result.Success);
        Assert.NotNull(ac.Phases?.AssignedRunway);
        // Node 3 is closer to End1 threshold (37.730), so should resolve to End1 designator
        Assert.Equal("28R", ac.Phases!.AssignedRunway!.Designator);
    }

    [Fact]
    public void TryTaxi_EndsAtNonHoldShort_NoAutoDetect()
    {
        var ac = MakeGroundAircraft();
        // Place aircraft right at node 1, but path only goes A (to node 2 which is intersection)
        // Actually node 2 also has edges to node 3 (A). The path "A" goes from node 1 through all A-edges.
        // Let me create a minimal layout with only 2 non-HS nodes.
        var minLayout = new AirportGroundLayout { AirportId = "TEST" };
        var n1 = new GroundNode
        {
            Id = 1,
            Latitude = 37.728,
            Longitude = -122.218,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n2 = new GroundNode
        {
            Id = 2,
            Latitude = 37.729,
            Longitude = -122.218,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        minLayout.Nodes[1] = n1;
        minLayout.Nodes[2] = n2;
        var edge = new GroundEdge
        {
            FromNodeId = 1,
            ToNodeId = 2,
            TaxiwayName = "B",
            DistanceNm = GeoMath.DistanceNm(n1.Latitude, n1.Longitude, n2.Latitude, n2.Longitude),
        };
        minLayout.Edges.Add(edge);
        n1.Edges.Add(edge);
        n2.Edges.Add(edge);

        var cmd2 = new TaxiCommand(["B"], []);
        var result = GroundCommandHandler.TryTaxi(ac, cmd2, minLayout, null);

        Assert.True(result.Success);
        Assert.Null(ac.Phases?.AssignedRunway);
    }

    [Fact]
    public void TryTaxi_ExplicitDestRunway_SetsAssignedRunway()
    {
        var ac = MakeGroundAircraft();
        var layout = MakeSimpleLayout();
        var navDb = TestNavDbFactory.WithRunways(
            TestRunwayFactory.Make(designator: "28R", airportId: "OAK", thresholdLat: 37.730, thresholdLon: -122.218, heading: 280)
        );
        var cmd = new TaxiCommand(["A"], [], DestinationRunway: "28R");

        var result = GroundCommandHandler.TryTaxi(ac, cmd, layout, navDb);

        Assert.True(result.Success);
        Assert.NotNull(ac.Phases?.AssignedRunway);
    }

    // -------------------------------------------------------------------------
    // TryBreakConflict
    // -------------------------------------------------------------------------

    [Fact]
    public void TryBreakConflict_OnGround_SetsTimer()
    {
        var ac = MakeGroundAircraft();

        var result = GroundCommandHandler.TryBreakConflict(ac);

        Assert.True(result.Success);
        Assert.Equal(15.0, ac.ConflictBreakRemainingSeconds, precision: 9);
    }

    [Fact]
    public void TryBreakConflict_Airborne_Fails()
    {
        var ac = MakeGroundAircraft();
        ac.IsOnGround = false;

        var result = GroundCommandHandler.TryBreakConflict(ac);

        Assert.False(result.Success);
        Assert.Equal(0.0, ac.ConflictBreakRemainingSeconds);
    }

    // -------------------------------------------------------------------------
    // TryGo
    // -------------------------------------------------------------------------

    [Fact]
    public void TryGo_InStopAndGoPhase_Succeeds()
    {
        var ac = MakeGroundAircraft();
        ac.Phases = new PhaseList();
        var stopAndGo = new StopAndGoPhase();
        ac.Phases.Add(stopAndGo);
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0,
            Logger = NullLogger.Instance,
        };
        ac.Phases.Start(ctx);

        var result = GroundCommandHandler.TryGo(ac);

        Assert.True(result.Success);
    }

    [Fact]
    public void TryGo_NotInStopAndGoPhase_Fails()
    {
        var ac = MakeGroundAircraft();
        // No StopAndGoPhase — just a plain ground aircraft with TaxiingPhase
        ac.Phases = new PhaseList();
        ac.Phases.Add(new TaxiingPhase());
        ac.Phases.Start(
            new PhaseContext
            {
                Aircraft = ac,
                Targets = ac.Targets,
                Category = AircraftCategory.Jet,
                DeltaSeconds = 0,
                Logger = NullLogger.Instance,
            }
        );

        var result = GroundCommandHandler.TryGo(ac);

        Assert.False(result.Success);
        Assert.Contains("stop-and-go", result.Message!);
    }

    // -------------------------------------------------------------------------
    // Parser: BREAK, GO, TAXIALL
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_Break_ReturnsBreakConflictCommand()
    {
        var cmd = CommandParser.Parse("BREAK");

        Assert.IsType<BreakConflictCommand>(cmd.Value);
    }

    [Fact]
    public void Parse_Go_ReturnsGoCommand()
    {
        var cmd = CommandParser.Parse("GO");

        Assert.IsType<GoCommand>(cmd.Value);
    }

    [Fact]
    public void Parse_TaxiAll_ReturnsCommandWithDestinationRunway()
    {
        var cmd = CommandParser.Parse("TAXIALL 30");

        var taxiAll = Assert.IsType<TaxiAllCommand>(cmd.Value);
        Assert.Equal("30", taxiAll.DestinationRunway);
    }

    // -------------------------------------------------------------------------
    // DispatchCompound: TAXI + CROSS in same block
    // -------------------------------------------------------------------------

    [Fact]
    public void DispatchCompound_TaxiWithCross_PreClearsHoldShort()
    {
        var ac = MakeAircraftAtParking();
        var layout = MakeSimpleLayout();

        // Compound: TAXI A, CROSS 28R — one block, two parallel commands
        var compound = new CompoundCommand([new ParsedBlock(null, [new TaxiCommand(["A"], []), new CrossRunwayCommand("28R")])]);

        var result = CommandDispatcher.DispatchCompound(compound, ac, null, layout, new Random(42), true);

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.NotNull(ac.AssignedTaxiRoute);
        // The CROSS 28R should have pre-cleared the hold-short
        var allHs = ac.AssignedTaxiRoute!.HoldShortPoints;
        var hs = allHs.FirstOrDefault(h => h.TargetName is not null && RunwayIdentifier.Parse(h.TargetName).Contains("28R"));
        Assert.NotNull(hs);
        Assert.True(hs.IsCleared);
    }
}
