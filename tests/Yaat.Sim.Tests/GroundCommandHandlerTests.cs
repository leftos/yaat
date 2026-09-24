using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
public class GroundCommandHandlerTests
{
    private static readonly ILogger Logger = new NullLogger<GroundCommandHandlerTests>();

    public GroundCommandHandlerTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AircraftState MakeGroundAircraft(double lat = 37.728, double lon = -122.218)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(280),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
            Phases = new PhaseList(),
        };
        return ac;
    }

    private static AircraftState MakeAircraftAtParking()
    {
        AircraftState ac = MakeGroundAircraft();
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
            Position = new LatLon(37.728, -122.218),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.729, -122.218),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node3 = new GroundNode
        {
            Id = 3,
            Position = new LatLon(37.730, -122.218),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = new RunwayIdentifier("28R"),
        };

        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;
        layout.Nodes[3] = node3;

        var edge12 = new GroundEdge
        {
            Nodes = [layout.Nodes[1], layout.Nodes[2]],
            TaxiwayName = "A",
            DistanceNm = GeoMath.DistanceNm(node1.Position, node2.Position),
        };
        var edge23 = new GroundEdge
        {
            Nodes = [layout.Nodes[2], layout.Nodes[3]],
            TaxiwayName = "A",
            DistanceNm = GeoMath.DistanceNm(node2.Position, node3.Position),
        };

        layout.Edges.Add(edge12);
        layout.Edges.Add(edge23);
        node1.Edges.Add(edge12);
        node2.Edges.Add(edge12);
        node2.Edges.Add(edge23);
        node3.Edges.Add(edge23);

        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static TaxiRoute MakeRouteWithHoldShort(string runwayId)
    {
        return new TaxiRoute
        {
            Segments = [MakeSegment(1, 2, "A", 0.1)],
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

    private static TaxiRoute MakeRouteWithTwoHoldShorts(string firstRunwayId, string secondRunwayId)
    {
        return new TaxiRoute
        {
            Segments = [MakeSegment(1, 2, "A", 0.1), MakeSegment(2, 3, "A", 0.1)],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = firstRunwayId,
                },
                new HoldShortPoint
                {
                    NodeId = 3,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = secondRunwayId,
                },
            ],
        };
    }

    /// <summary>
    /// A KOAK-shaped taxi route out of a ramp: hold short of taxiway C at node 1, then taxiway B
    /// across runway 28R — entry bar node 2, runway centerline node 3, exit bar node 4 — and on to
    /// node 5. <paramref name="autoCleared"/> reproduces what <c>TaxiRouteAutoCross.Apply</c> leaves
    /// behind when the AutoCrossRunway toggle is on: the crossing is pre-cleared before the
    /// controller ever gets to issue <c>HS 28R</c>.
    /// </summary>
    private static (AirportGroundLayout Layout, TaxiRoute Route) MakeCrossingRoute(bool autoCleared)
    {
        var rwy = new RunwayIdentifier("28R", "10L");
        var layout = new AirportGroundLayout { AirportId = "OAK" };
        int[] taxiwayNodeIds = [0, 1, 3, 5];
        int[] holdShortNodeIds = [2, 4];
        foreach (int id in taxiwayNodeIds)
        {
            layout.Nodes[id] = new GroundNode
            {
                Id = id,
                Position = new LatLon(0, 0),
                Type = GroundNodeType.TaxiwayIntersection,
            };
        }
        foreach (int id in holdShortNodeIds)
        {
            layout.Nodes[id] = new GroundNode
            {
                Id = id,
                Position = new LatLon(0, 0),
                Type = GroundNodeType.RunwayHoldShort,
                RunwayId = rwy,
            };
        }

        var route = new TaxiRoute
        {
            Segments =
            [
                MakeSegment(0, 1, "C", 0.1),
                MakeSegment(1, 2, "B", 0.1),
                MakeSegment(2, 3, "B", 0.1),
                MakeSegment(3, 4, "B", 0.1),
                MakeSegment(4, 5, "B", 0.1),
            ],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "C",
                },
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28R/10L",
                    IsCleared = autoCleared,
                    ClearedByAutoCross = autoCleared,
                },
            ],
            // BuildResumePhases bumps past the segment that ended at the hold-short node the
            // aircraft is stopped at, so an aircraft holding short of C sits at index 1.
            CurrentSegmentIndex = 1,
        };

        return (layout, route);
    }

    private static AircraftState MakeAircraftHoldingShortOfTaxiwayC(TaxiRoute route)
    {
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = true;
        ac.Ground.AssignedTaxiRoute = route;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingShortPhase(route.HoldShortPoints[0]));
        ac.Phases.Start(
            new PhaseContext
            {
                Aircraft = ac,
                Targets = ac.Targets,
                Category = AircraftCategory.Jet,
                DeltaSeconds = 1.0,
                Logger = Logger,
            }
        );
        return ac;
    }

    // -------------------------------------------------------------------------
    // TryTaxi
    // -------------------------------------------------------------------------

    private static TaxiRouteSegment MakeSegment(int fromId, int toId, string taxiwayName, double distanceNm = 0.1)
    {
        var fromNode = new GroundNode
        {
            Id = fromId,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var toNode = new GroundNode
        {
            Id = toId,
            Position = new LatLon(0, 0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var edge = new GroundEdge
        {
            Nodes = [fromNode, toNode],
            TaxiwayName = taxiwayName,
            DistanceNm = distanceNm,
        };
        return new TaxiRouteSegment { TaxiwayName = taxiwayName, Edge = edge.Directed(fromNode, toNode) };
    }

    [Fact]
    public void TryTaxi_NoLayout_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        var cmd = new TaxiCommand(["A"], [], DestinationRunway: "28R");

        CommandResult result = GroundCommandHandler.TryTaxi(ac, cmd, null);

        Assert.False(result.Success);
        Assert.Contains("No airport ground layout", result.Message!);
    }

    [Fact]
    public void TryTaxi_UnknownTaxiway_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        AirportGroundLayout layout = MakeSimpleLayout();
        var cmd = new TaxiCommand(["ZZZZZ"], [], DestinationRunway: "28R");

        CommandResult result = GroundCommandHandler.TryTaxi(ac, cmd, layout);

        Assert.False(result.Success);
    }

    [Fact]
    public void TryTaxi_ValidPath_Succeeds()
    {
        AircraftState ac = MakeGroundAircraft();
        AirportGroundLayout layout = MakeSimpleLayout();
        var cmd = new TaxiCommand(["A"], [], DestinationRunway: "28R");

        CommandResult result = GroundCommandHandler.TryTaxi(ac, cmd, layout);

        Assert.True(result.Success);
        Assert.NotNull(ac.Ground.AssignedTaxiRoute);
        Assert.True(ac.Ground.AssignedTaxiRoute!.Segments.Count > 0);
    }

    [Fact]
    public void TryTaxi_AutoCrossRunway_ClearsHoldShorts()
    {
        AircraftState ac = MakeGroundAircraft();
        AirportGroundLayout layout = MakeSimpleLayout();
        var cmd = new TaxiCommand(["A"], [], DestinationRunway: "28R");

        CommandResult result = GroundCommandHandler.TryTaxi(ac, cmd, layout, autoCrossRunway: true);

        Assert.True(result.Success);
        // All RunwayCrossing hold-shorts should be pre-cleared
        foreach (HoldShortPoint hs in ac.Ground.AssignedTaxiRoute!.HoldShortPoints)
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
        AircraftState ac = MakeGroundAircraft();
        // Phases empty (no AtParkingPhase)
        var cmd = new PushbackCommand(null, null, null, null);

        CommandResult result = GroundCommandHandler.TryPushback(ac, cmd, null, null);

        Assert.False(result.Success);
        Assert.Contains("at parking", result.Message!);
    }

    [Fact]
    public void TryPushback_AtParking_NoArgs_Succeeds()
    {
        AircraftState ac = MakeAircraftAtParking();
        var cmd = new PushbackCommand(null, null, null, null);

        CommandResult result = GroundCommandHandler.TryPushback(ac, cmd, null, null);

        Assert.True(result.Success);
        Assert.StartsWith("Push straight back (nose ", result.Message!);
    }

    /// <summary>SFO gate B12 backs onto taxiway Y: a bare <c>PUSH Y</c> pushes straight back to it.</summary>
    [Fact]
    public void TryPushback_WithTaxiway_ResolvesTarget()
    {
        if (ParkedOnSfoB12() is not (var ac, var layout))
        {
            return;
        }

        var cmd = new PushbackCommand(null, "Y", null, null);

        CommandResult result = GroundCommandHandler.TryPushback(ac, cmd, layout, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Push straight back to taxiway Y", result.Message!);
    }

    /// <summary>
    /// <c>PUSH #node</c> off SFO gate B12, to the node where taxiway Y meets the push-back line: accepted, and the tug
    /// move it installs ends with the aircraft on that node, holding there.
    /// </summary>
    [Fact]
    public void TryPushback_ToNode_InstallsTugMoveEndingAtTheNode()
    {
        if (ParkedOnSfoB12() is not (var ac, var layout))
        {
            return;
        }

        GroundNode target = layout.FindExitByTaxiway(ac.Position, "Y") ?? throw new InvalidOperationException("no Y node behind SFO B12");
        ParseResult<ParsedCommand> parsed = CommandParser.Parse($"PUSH #{target.Id}");
        PushbackCommand cmd = Assert.IsType<PushbackCommand>(parsed.Value);

        CommandResult result = GroundCommandHandler.TryPushback(ac, cmd, layout, null);

        Assert.True(result.Success, result.Message);
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases!.Phases[^1]);
        PushbackPhase lastMove = ac.Phases.Phases.OfType<PushbackPhase>().Last();
        double endToNodeFt = GeoMath.DistanceNm(lastMove.PlannedEnd, target.Position) * GeoMath.FeetPerNm;
        Assert.True(endToNodeFt < 1.0, $"the tug move ends {endToNodeFt:F1} ft from node #{target.Id}");
    }

    /// <summary><c>PUSH #node</c> naming a node the layout does not carry is refused as <c>PUSHM</c> refuses it.</summary>
    [Fact]
    public void TryPushback_ToUnknownNode_RefusedAsPushmRefusesIt()
    {
        if (ParkedOnSfoB12() is not (var ac, var layout))
        {
            return;
        }

        var cmd = new PushbackCommand(null, null, null, PushDestination.AtNode(99999999));

        CommandResult result = GroundCommandHandler.TryPushback(ac, cmd, layout, null);

        Assert.False(result.Success, result.Message);
        Assert.Equal("Cannot find node '#99999999'", result.Message);
        Assert.IsType<AtParkingPhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary><c>PUSH #node</c> naming a stand's node takes no facing, a heading or a facing taxiway alike.</summary>
    [Theory]
    [InlineData("FACE E")]
    [InlineData("F1")]
    public void TryPushback_ToStandNodeWithAFacing_RefusedWithTheStandFacingWording(string facing)
    {
        if (ParkedOnSfo("D2") is not (var ac, var layout))
        {
            return;
        }

        GroundNode d1 = layout.FindParkingByName("D1") ?? throw new InvalidOperationException("SFO gate D1 missing");
        PushbackCommand cmd = Assert.IsType<PushbackCommand>(CommandParser.Parse($"PUSH #{d1.Id} {facing}").Value);

        CommandResult result = GroundCommandHandler.TryPushback(ac, cmd, layout, null);

        Assert.False(result.Success, result.Message);
        Assert.Equal($"PUSH #{d1.Id} does not take a facing — the aircraft parks on the stand's own heading", result.Message);
        Assert.IsType<AtParkingPhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary><c>PUSH #node</c> naming a stand's node parks the aircraft on that stand.</summary>
    [Fact]
    public void TryPushback_ToStandNode_ParksOnTheStand()
    {
        if (ParkedOnSfo("D2") is not (var ac, var layout))
        {
            return;
        }

        GroundNode d1 = layout.FindParkingByName("D1") ?? throw new InvalidOperationException("SFO gate D1 missing");
        PushbackCommand cmd = Assert.IsType<PushbackCommand>(CommandParser.Parse($"PUSH #{d1.Id}").Value);

        CommandResult result = GroundCommandHandler.TryPushback(ac, cmd, layout, null);

        Assert.True(result.Success, result.Message);
        Assert.IsType<AtParkingPhase>(ac.Phases!.Phases[^1]);
        Assert.Equal("D1", ac.Ground.ParkingSpot);
    }

    /// <summary><c>PUSH #node</c> naming a spot's node ends holding on the spot, the stand left behind.</summary>
    [Fact]
    public void TryPushback_ToSpotNode_HoldsOnTheSpot()
    {
        if (ParkedOnSfo("D2") is not (var ac, var layout))
        {
            return;
        }

        GroundNode spot = layout.FindSpotNodeByName("5A") ?? throw new InvalidOperationException("SFO spot 5A missing");
        ac.Ground.ParkingSpot = "D2";
        PushbackCommand cmd = Assert.IsType<PushbackCommand>(CommandParser.Parse($"PUSH #{spot.Id}").Value);

        CommandResult result = GroundCommandHandler.TryPushback(ac, cmd, layout, null);

        Assert.True(result.Success, result.Message);
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases!.Phases[^1]);
        Assert.Null(ac.Ground.ParkingSpot);
    }

    /// <summary>
    /// A stand destination takes no facing. The parser never builds one; a hand-built <c>PUSH @B13</c> carrying a
    /// facing heading or a facing taxiway is refused with the parser's own words, and the aircraft stays parked.
    /// </summary>
    [Theory]
    [InlineData("FACE S", 180, null)]
    [InlineData("Y", null, "Y")]
    public void TryPushback_StandWithAFacing_RefusedWithTheParsersWords(string parsedFacing, int? faceHeading, string? facingTaxiway)
    {
        if (ParkedOnSfoB12() is not (var ac, var layout))
        {
            return;
        }

        ParseResult<ParsedCommand> parsed = CommandParser.Parse($"PUSH @B13 {parsedFacing}");
        Assert.False(parsed.IsSuccess, $"'PUSH @B13 {parsedFacing}' parsed as {parsed.Value}");
        var cmd = new PushbackCommand(
            faceHeading is { } heading ? new MagneticHeading(heading) : null,
            null,
            facingTaxiway,
            PushDestination.AtParking("B13")
        );

        CommandResult result = GroundCommandHandler.TryPushback(ac, cmd, layout, null);

        Assert.False(result.Success, $"a stand push with a facing was accepted: {result.Message}");
        Assert.Equal("PUSH @B13 does not take a facing — the aircraft parks on the stand's own heading", result.Message);
        Assert.Contains(result.Message!, parsed.Reason, StringComparison.Ordinal);
        Assert.IsType<AtParkingPhase>(ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void TryPushback_WithHeading_IncludesInMessage()
    {
        AircraftState ac = MakeAircraftAtParking();
        var cmd = new PushbackCommand(new MagneticHeading(180), null, null, null);

        CommandResult result = GroundCommandHandler.TryPushback(ac, cmd, null, null);

        Assert.True(result.Success);
        Assert.Equal("Push back, face south", result.Message);
    }

    /// <summary>
    /// A B738 parked on SFO gate B12, nose on the stand heading (284° true). Taxiway Y runs behind the stand, its
    /// edge at the exit node lying along 028° / 208° true. Null when the SFO layout is missing.
    /// </summary>
    private static (AircraftState Aircraft, AirportGroundLayout Layout)? ParkedOnSfoB12() => ParkedOnSfo("B12");

    /// <summary>A B738 parked on an SFO gate, nose on the stand heading. Null when the SFO layout is missing.</summary>
    private static (AircraftState Aircraft, AirportGroundLayout Layout)? ParkedOnSfo(string gate)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        GroundNode stand = layout.FindParkingByName(gate) ?? throw new InvalidOperationException($"the SFO layout has no gate {gate}");
        AircraftState ac = MakeAircraftAtParking();
        ac.Position = stand.Position;
        ac.TrueHeading = stand.TrueHeading ?? throw new InvalidOperationException($"SFO gate {gate} has no heading");
        return (ac, layout);
    }

    /// <summary>
    /// FACE N (013° true at SFO) snaps to Y's edge direction nearest it, 028° true: the installed tug move ends on it,
    /// and the readback words the cardinal the controller named.
    /// </summary>
    [Fact]
    public void TryPushback_TaxiwayWithFaceN_SnapsNorthEdge()
    {
        if (ParkedOnSfoB12() is not (var ac, var layout))
        {
            return;
        }

        var cmd = new PushbackCommand(new MagneticHeading(360), "Y", null, null);

        CommandResult result = GroundCommandHandler.TryPushback(ac, cmd, layout, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Push onto Y, face north", result.Message);
        AssertTugMoveEndsFacing(ac, 28.0);
    }

    /// <summary>FACE S (193° true at SFO) snaps to Y's other edge direction, 208° true.</summary>
    [Fact]
    public void TryPushback_TaxiwayWithFaceS_SnapsSouthEdge()
    {
        if (ParkedOnSfoB12() is not (var ac, var layout))
        {
            return;
        }

        var cmd = new PushbackCommand(new MagneticHeading(180), "Y", null, null);

        CommandResult result = GroundCommandHandler.TryPushback(ac, cmd, layout, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Push onto Y, face south", result.Message);
        AssertTugMoveEndsFacing(ac, 208.0);
    }

    [Fact]
    public void TryPushback_CardinalAlone_UsesAbsoluteFacing()
    {
        // Without a taxiway, the cardinal is the absolute target facing (no edge snap).
        AircraftState ac = MakeAircraftAtParking();
        var cmd = new PushbackCommand(new MagneticHeading(45), null, null, null);

        CommandResult result = GroundCommandHandler.TryPushback(ac, cmd, null, null);

        Assert.True(result.Success);
        Assert.Equal("Push back, face northeast", result.Message);
        AssertTugMoveEndsFacing(ac, MagneticDeclination.MagneticToTrue(45.0, ac.Position));
    }

    /// <summary>
    /// The nose heading the installed tug move ends on, flown from where the aircraft stands through every queued
    /// <see cref="PushbackPhase"/>'s move, is within 2° of <paramref name="expectedTrueDeg"/>.
    /// </summary>
    private static void AssertTugMoveEndsFacing(AircraftState ac, double expectedTrueDeg)
    {
        List<TugMove> moves = [.. ac.Phases!.Phases.OfType<PushbackPhase>().Select(p => p.Move)];
        TugPose end = TugKinematics.Simulate(new TugPose(ac.Position, ac.TrueHeading.Degrees), moves, ac.AircraftType, 1.0).End;
        double offDeg = GeoMath.AbsBearingDifference(end.NoseTrueDeg, expectedTrueDeg);
        Assert.True(offDeg < 2.0, $"the tug move ends facing {end.NoseTrueDeg:F1}° true, {offDeg:F1}° off {expectedTrueDeg:F1}°");
    }

    // -------------------------------------------------------------------------
    // TryCrossRunway
    // -------------------------------------------------------------------------

    [Fact]
    public void TryCrossRunway_FromHoldingShort_SatisfiesClearance()
    {
        AircraftState ac = MakeGroundAircraft();
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

        var cmd = new CrossRunwayCommand(["28R"], []);
        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, cmd, ac.Ground.Layout);

        Assert.True(result.Success);
        Assert.Contains("Cross 28R", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_FromHoldingShort_DestinationRunway_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Phases = new PhaseList();
        var holdPhase = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = 3,
                Reason = HoldShortReason.DestinationRunway,
                TargetName = "28R/10L",
            }
        );
        ac.Phases.Add(holdPhase);

        // The refusal is about an aircraft still taxiing TO its departure runway, so it needs the in-progress
        // route that says so. A bar reached with no route at all — an ATXI to the runway — takes CROSS instead
        // (AirTaxiRunwayTerminusTests.HoldingShortAfterAnAirTaxi_TakesCross).
        ac.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [MakeSegment(1, 2, "A", 0.1), MakeSegment(2, 3, "A", 0.1)],
            HoldShortPoints = [holdPhase.HoldShort],
            CurrentSegmentIndex = 0,
        };

        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0,
            Logger = NullLogger.Instance,
        };
        ac.Phases.Start(ctx);

        var cmd = new CrossRunwayCommand(["28R"], []);
        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, cmd, ac.Ground.Layout);

        Assert.False(result.Success);
        Assert.Contains("LUAW", result.Message!);
        Assert.Contains("CTO", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_FromHoldingShort_RunwayMismatch_NotInRoute_Fails()
    {
        // When in HoldingShortPhase at 28R/10L but the requested runway (01L)
        // doesn't match and is not an upcoming hold-short in the taxi route,
        // CROSS falls through to the route pre-clear path and reports that
        // there's no hold-short for 01L. (When 01L *is* an upcoming hold-short,
        // the comma form RES, CROSS 01L pre-clears it — see
        // N7ljResCrossCommaFormTests for that scenario.)
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = MakeRouteWithHoldShort("28R/10L");
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

        var cmd = new CrossRunwayCommand(["01L"], []);
        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, cmd, ac.Ground.Layout);

        Assert.False(result.Success);
        Assert.Contains("01L", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_FromHoldingShort_ExplicitHoldShort_Succeeds()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Phases = new PhaseList();
        var holdPhase = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = 3,
                Reason = HoldShortReason.ExplicitHoldShort,
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

        var cmd = new CrossRunwayCommand(["28R"], []);
        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, cmd, ac.Ground.Layout);

        Assert.True(result.Success);
        Assert.Contains("Cross 28R", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_PreClearInRoute_MarksHoldShortCleared()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = MakeRouteWithHoldShort("28R/10L");

        // Not currently at a hold-short phase — pre-clear mode
        var cmd = new CrossRunwayCommand(["28R"], []);
        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, cmd, ac.Ground.Layout);

        Assert.True(result.Success);
        Assert.True(ac.Ground.AssignedTaxiRoute.HoldShortPoints[0].IsCleared);
    }

    [Fact]
    public void TryCrossRunway_NoMatchingHoldShort_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = MakeRouteWithHoldShort("15/33");

        var cmd = new CrossRunwayCommand(["28R"], []);
        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, cmd, ac.Ground.Layout);

        Assert.False(result.Success);
        Assert.Contains("No hold-short", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_NoRoute_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        // No route assigned

        var cmd = new CrossRunwayCommand(["28R"], []);
        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, cmd, ac.Ground.Layout);

        Assert.False(result.Success);
    }

    [Fact]
    public void TryCrossRunway_MultipleRunways_PreClearsAllMatchingRouteHoldShorts()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = MakeRouteWithTwoHoldShorts("28R/10L", "28L/10R");

        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand(["28R", "28L"], []), ac.Ground.Layout);

        Assert.True(result.Success, result.Message);
        Assert.All(ac.Ground.AssignedTaxiRoute.HoldShortPoints, hs => Assert.True(hs.IsCleared));
    }

    [Fact]
    public void TryCrossRunway_MultipleRunways_OneNotInRoute_FailsAtomically()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = MakeRouteWithTwoHoldShorts("28R/10L", "28L/10R");

        // 01L is not an upcoming crossing — the whole command is rejected and nothing is cleared.
        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand(["28R", "01L"], []), ac.Ground.Layout);

        Assert.False(result.Success);
        Assert.Contains("01L", result.Message!);
        Assert.All(ac.Ground.AssignedTaxiRoute.HoldShortPoints, hs => Assert.False(hs.IsCleared));
    }

    // --- Named CROSS for taxiway/intersection holds ---

    [Fact]
    public void TryCrossRunway_NamedTaxiway_FromHoldingShort_Succeeds()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Phases = new PhaseList();
        var holdPhase = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = 3,
                Reason = HoldShortReason.ExplicitHoldShort,
                TargetName = "B",
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

        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand(["B"], []), ac.Ground.Layout);

        Assert.True(result.Success);
        Assert.Contains("B", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_NamedTaxiway_PreClearInRoute_MarksHoldShortCleared()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [MakeSegment(1, 2, "A", 0.1)],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "B",
                },
            ],
        };

        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand(["B"], []), ac.Ground.Layout);

        Assert.True(result.Success);
        Assert.True(ac.Ground.AssignedTaxiRoute.HoldShortPoints[0].IsCleared);
    }

    // --- Bare CROSS (no runway argument) ---

    [Fact]
    public void TryCrossRunway_Bare_FromHoldingShort_RunwayCrossing_Succeeds()
    {
        AircraftState ac = MakeGroundAircraft();
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

        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand([], []), ac.Ground.Layout);

        Assert.True(result.Success);
        Assert.Contains("28R/10L", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_Bare_FromHoldingShort_TaxiwayExplicit_Succeeds()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Phases = new PhaseList();
        var holdPhase = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = 3,
                Reason = HoldShortReason.ExplicitHoldShort,
                TargetName = "B",
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

        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand([], []), ac.Ground.Layout);

        Assert.True(result.Success);
        Assert.Contains("B", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_Bare_FromHoldingShort_DestinationRunway_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Phases = new PhaseList();
        var holdPhase = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = 3,
                Reason = HoldShortReason.DestinationRunway,
                TargetName = "28R/10L",
            }
        );
        ac.Phases.Add(holdPhase);

        // As above: mid-route to the departure runway is what makes bare CROSS a mistake.
        ac.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [MakeSegment(1, 2, "A", 0.1), MakeSegment(2, 3, "A", 0.1)],
            HoldShortPoints = [holdPhase.HoldShort],
            CurrentSegmentIndex = 0,
        };

        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0,
            Logger = NullLogger.Instance,
        };
        ac.Phases.Start(ctx);

        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand([], []), ac.Ground.Layout);

        Assert.False(result.Success);
        Assert.Contains("LUAW", result.Message!);
        Assert.Contains("CTO", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_Bare_FromTaxi_ClearsFirstUnclearedHoldShortOnly()
    {
        AircraftState ac = MakeGroundAircraft();
        // Two runway crossings ahead — bare CROSS should clear only the first.
        ac.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [MakeSegment(1, 2, "A", 0.1), MakeSegment(2, 3, "A", 0.1)],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28L/10R",
                },
                new HoldShortPoint
                {
                    NodeId = 3,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28R/10L",
                },
            ],
        };

        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand([], []), ac.Ground.Layout);

        Assert.True(result.Success);
        Assert.Contains("28L/10R", result.Message!);
        Assert.True(ac.Ground.AssignedTaxiRoute.HoldShortPoints[0].IsCleared);
        Assert.False(ac.Ground.AssignedTaxiRoute.HoldShortPoints[1].IsCleared);
    }

    [Fact]
    public void TryCrossRunway_Bare_FromTaxi_NextIsDestinationRunway_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [MakeSegment(1, 2, "A", 0.1)],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "28R/10L",
                },
            ],
        };

        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand([], []), ac.Ground.Layout);

        Assert.False(result.Success);
        Assert.Contains("LUAW", result.Message!);
        Assert.Contains("CTO", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_Bare_FromTaxi_NoUnclearedHoldShorts_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [MakeSegment(1, 2, "A", 0.1)],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28R/10L",
                    IsCleared = true,
                },
            ],
        };

        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand([], []), ac.Ground.Layout);

        Assert.False(result.Success);
        Assert.Contains("No upcoming hold-short", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_Bare_FromTaxi_NoRoute_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        // No route assigned, not holding short

        CommandResult result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand([], []), ac.Ground.Layout);

        Assert.False(result.Success);
    }

    // -------------------------------------------------------------------------
    // TryHoldShort
    // -------------------------------------------------------------------------

    [Fact]
    public void TryHoldShort_NotOnGround_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = false;
        var cmd = new HoldShortCommand(HoldShortTarget.Parse("28R"));

        CommandResult result = GroundCommandHandler.TryHoldShort(ac, cmd, null);

        Assert.False(result.Success);
        Assert.Contains("on the ground", result.Message!);
    }

    [Fact]
    public void TryHoldShort_NoRoute_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        var cmd = new HoldShortCommand(HoldShortTarget.Parse("28R"));

        CommandResult result = GroundCommandHandler.TryHoldShort(ac, cmd, null);

        Assert.False(result.Success);
        Assert.Contains("No taxi route", result.Message!);
    }

    [Fact]
    public void TryHoldShort_NoLayout_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = MakeRouteWithHoldShort("28R/10L");
        var cmd = new HoldShortCommand(HoldShortTarget.Parse("B"));

        CommandResult result = GroundCommandHandler.TryHoldShort(ac, cmd, null);

        Assert.False(result.Success);
        Assert.Contains("No ground layout", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryFollow
    // -------------------------------------------------------------------------

    [Fact]
    public void TryFollow_NoActivePhase_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Phases = null;
        var cmd = new FollowGroundCommand("UAL123");

        CommandResult result = GroundCommandHandler.TryFollow(ac, cmd, null, null);

        Assert.False(result.Success);
        Assert.Contains("no active phase", result.Message!);
    }

    [Fact]
    public void TryFollow_NotOnGround_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
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
        var cmd = new FollowGroundCommand("UAL123");

        CommandResult result = GroundCommandHandler.TryFollow(ac, cmd, null, null);

        Assert.False(result.Success);
        Assert.Contains("on the ground", result.Message!);
    }

    private static Func<string, AircraftState?> LookupWithGroundLeader(string callsign)
    {
        AircraftState leader = MakeGroundAircraft(37.729, -122.219);
        leader.Callsign = callsign;
        return cs => string.Equals(cs, callsign, StringComparison.OrdinalIgnoreCase) ? leader : null;
    }

    [Fact]
    public void TryFollow_FromAtParking_Succeeds()
    {
        AircraftState ac = MakeAircraftAtParking();
        var cmd = new FollowGroundCommand("UAL123");

        CommandResult result = GroundCommandHandler.TryFollow(ac, cmd, null, LookupWithGroundLeader("UAL123"));

        Assert.True(result.Success, result.Message);
        Assert.IsType<FollowingPhase>(ac.Phases!.CurrentPhase);
    }

    [Fact]
    public void TryFollow_FromTaxiing_Succeeds()
    {
        AircraftState ac = MakeGroundAircraft();
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
        var cmd = new FollowGroundCommand("UAL123");

        CommandResult result = GroundCommandHandler.TryFollow(ac, cmd, null, LookupWithGroundLeader("UAL123"));

        Assert.True(result.Success, result.Message);
        Assert.IsType<FollowingPhase>(ac.Phases!.CurrentPhase);
    }

    [Theory]
    [InlineData(typeof(HoldingAfterPushbackPhase))]
    [InlineData(typeof(HoldingAfterExitPhase))]
    public void TryFollow_FromIdleHoldingPhases_Succeeds(Type phaseType)
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Phases = new PhaseList();
        ac.Phases.Add((Phase)Activator.CreateInstance(phaseType)!);
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
        var cmd = new FollowGroundCommand("UAL123");

        CommandResult result = GroundCommandHandler.TryFollow(ac, cmd, null, LookupWithGroundLeader("UAL123"));

        Assert.True(result.Success, result.Message);
        Assert.IsType<FollowingPhase>(ac.Phases!.CurrentPhase);
    }

    [Fact]
    public void TryFollow_UnknownTarget_Fails()
    {
        AircraftState ac = MakeAircraftAtParking();
        var cmd = new FollowGroundCommand("UAL123");

        CommandResult result = GroundCommandHandler.TryFollow(ac, cmd, null, _ => null);

        Assert.False(result.Success);
        Assert.Contains("No aircraft UAL123", result.Message!);
        Assert.IsType<AtParkingPhase>(ac.Phases!.CurrentPhase);
    }

    [Fact]
    public void TryFollow_TargetAirborne_Fails()
    {
        AircraftState ac = MakeAircraftAtParking();
        var cmd = new FollowGroundCommand("UAL123");
        Func<string, AircraftState?> lookup = LookupWithGroundLeader("UAL123");
        lookup("UAL123")!.IsOnGround = false;

        CommandResult result = GroundCommandHandler.TryFollow(ac, cmd, null, lookup);

        Assert.False(result.Success);
        Assert.Contains("not on the ground", result.Message!);
        Assert.IsType<AtParkingPhase>(ac.Phases!.CurrentPhase);
    }

    [Fact]
    public void TryFollow_Self_Fails()
    {
        AircraftState ac = MakeAircraftAtParking();
        var cmd = new FollowGroundCommand(ac.Callsign);

        CommandResult result = GroundCommandHandler.TryFollow(ac, cmd, null, LookupWithGroundLeader(ac.Callsign));

        Assert.False(result.Success);
        Assert.Contains("cannot follow itself", result.Message!);
        Assert.IsType<AtParkingPhase>(ac.Phases!.CurrentPhase);
    }

    // -------------------------------------------------------------------------
    // TryHoldPosition / TryResumeTaxi
    // -------------------------------------------------------------------------

    [Fact]
    public void TryHoldPosition_OnGround_SetsIsHeld()
    {
        AircraftState ac = MakeGroundAircraft();

        CommandResult result = GroundCommandHandler.TryHoldPosition(ac);

        Assert.True(result.Success);
        Assert.True(ac.Ground.IsImmobile);
        Assert.Equal(HoldDirective.HoldPosition, ac.Ground.Hold);
    }

    [Fact]
    public void TryHoldPosition_TaxiingPhase_MentionsTaxiway()
    {
        // RPO-visible feedback should describe what state the sim now believes.
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.CurrentTaxiway = "A";
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

        CommandResult result = GroundCommandHandler.TryHoldPosition(ac);

        Assert.True(result.Success);
        Assert.Contains("taxiway A", result.Message!);
    }

    [Fact]
    public void TryHoldPosition_LineUpPhase_MentionsRunway()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(designator: "28R", heading: 280, elevationFt: 6) };
        ac.Phases.Add(new LinedUpAndWaitingPhase());
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

        CommandResult result = GroundCommandHandler.TryHoldPosition(ac);

        Assert.True(result.Success);
        Assert.Contains("runway 28R", result.Message!);
    }

    [Fact]
    public void TryHoldPosition_ClearsExpeditingTaxi()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.IsExpeditingTaxi = true;

        GroundCommandHandler.TryHoldPosition(ac);

        Assert.False(ac.Ground.IsExpeditingTaxi);
    }

    [Fact]
    public void TryResumeTaxi_ClearsExpeditingTaxi()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.Hold = HoldDirective.HoldPosition;
        ac.Ground.IsExpeditingTaxi = true;

        GroundCommandHandler.TryResumeTaxi(ac);

        Assert.False(ac.Ground.IsExpeditingTaxi);
    }

    [Fact]
    public void TryHoldPosition_NotOnGround_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = false;

        CommandResult result = GroundCommandHandler.TryHoldPosition(ac);

        Assert.False(result.Success);
    }

    [Fact]
    public void TryResumeTaxi_WhenHeld_ClearsIsHeld()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.Hold = HoldDirective.HoldPosition;

        CommandResult result = GroundCommandHandler.TryResumeTaxi(ac);

        Assert.True(result.Success);
        Assert.False(ac.Ground.IsImmobile);
        Assert.Null(ac.Ground.Hold);
    }

    [Fact]
    public void TryResumeTaxi_NotHeld_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.Hold = null;

        CommandResult result = GroundCommandHandler.TryResumeTaxi(ac);

        Assert.False(result.Success);
    }

    /// <summary>
    /// RES to an aircraft the ground conflict detector has stopped — the "why is it crawling and RES says it
    /// isn't held" stall — does what the controller meant: it breaks the conflict, exactly as BREAK does.
    /// </summary>
    [Fact]
    public void Res_OnDetectorStall_BreaksConflict()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.Hold = null;
        ac.Ground.SpeedLimit = 0;

        CommandResult result = GroundCommandHandler.TryResumeTaxi(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(GroundCommandHandler.BreakDurationSeconds, ac.Ground.ConflictBreakRemainingSeconds);
        Assert.Null(ac.Ground.SpeedLimit);
    }

    /// <summary>
    /// The detector's crawl floor itself counts as held: an aircraft pinned at 5 kt behind an obstacle is the
    /// same stall the controller is looking at, and RES breaks it.
    /// </summary>
    [Fact]
    public void Res_OnDetectorCrawl_BreaksConflict()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.Hold = null;
        ac.Ground.SpeedLimit = GroundConflictDetector.SlowTaxiSpeedKts;

        CommandResult result = GroundCommandHandler.TryResumeTaxi(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(GroundCommandHandler.BreakDurationSeconds, ac.Ground.ConflictBreakRemainingSeconds);
        Assert.Null(ac.Ground.SpeedLimit);
    }

    /// <summary>
    /// A trail-speed cap behind moving traffic is not a stall: the aircraft is taxiing, just slower, and RES
    /// must not switch off its collision protection for the next 15 s on the strength of it.
    /// </summary>
    [Fact]
    public void Res_OnDetectorTrailAtSpeed_StillRefused()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.Hold = null;
        ac.Ground.SpeedLimit = 15.0;

        CommandResult result = GroundCommandHandler.TryResumeTaxi(ac);

        Assert.False(result.Success);
        Assert.Equal("Aircraft is not held", result.Message);
        Assert.Equal(0.0, ac.Ground.ConflictBreakRemainingSeconds);
        Assert.Equal(15.0, ac.Ground.SpeedLimit);
    }

    /// <summary>An aircraft that is neither held nor capped has nothing to resume, and RES still says so.</summary>
    [Fact]
    public void Res_NotHeldNotStalled_StillRefused()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.Hold = null;
        ac.Ground.SpeedLimit = null;

        CommandResult result = GroundCommandHandler.TryResumeTaxi(ac);

        Assert.False(result.Success);
        Assert.Equal("Aircraft is not held", result.Message);
        Assert.Equal(0.0, ac.Ground.ConflictBreakRemainingSeconds);
    }

    [Fact]
    public void Resume_ClearsExplicitHoldShortPhase()
    {
        AircraftState ac = MakeGroundAircraft();
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

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand([], [])])]);
        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
    }

    [Fact]
    public void Resume_ClearsRunwayCrossingHoldShortPhase()
    {
        AircraftState ac = MakeGroundAircraft();
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

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand([], [])])]);
        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
    }

    [Fact]
    public void Resume_DoesNotClearDestinationRunwayHoldShortPhase()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = true;

        var holdShort = new HoldShortPoint
        {
            NodeId = 1,
            Reason = HoldShortReason.DestinationRunway,
            TargetName = "30",
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

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand([], [])])]);
        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.False(result.Success);
        Assert.False(holdShort.IsCleared);
        Assert.NotNull(result.Message);
        Assert.Contains("CTO", result.Message);
        Assert.Contains("LUAW", result.Message);
        Assert.DoesNotContain("not held", result.Message);
    }

    // -------------------------------------------------------------------------
    // RES CROSS — bundles RES with explicit pre-clearance(s) for upcoming runway
    // crossings further down the taxi route. Each listed runway must match an
    // upcoming RunwayCrossing hold-short; otherwise the entire command fails.
    // -------------------------------------------------------------------------

    [Fact]
    public void ResCross_PreClearsMatchingRouteHoldShort()
    {
        // HoldingShortPhase at 28R (explicit). Route also contains an upcoming
        // 28L crossing. RES CROSS 28L should clear the current phase AND mark
        // the 28L hold-short cleared so the aircraft does not stop at it.
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = true;

        var route = new TaxiRoute
        {
            Segments = [MakeSegment(1, 2, "W", 0.1), MakeSegment(2, 3, "W", 0.1), MakeSegment(3, 4, "W1", 0.1)],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "28R",
                },
                new HoldShortPoint
                {
                    NodeId = 3,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28L",
                },
            ],
        };
        ac.Ground.AssignedTaxiRoute = route;

        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingShortPhase(route.HoldShortPoints[0]));
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Logger = Logger,
        };
        ac.Phases.Start(ctx);

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand(["28L"], [])])]);
        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.True(route.HoldShortPoints[1].IsCleared, "Upcoming 28L hold-short should be pre-cleared");
    }

    [Fact]
    public void ResCross_RunwayNotOnRoute_FailsEntireCommand()
    {
        // Route only contains 28R. RES CROSS 09L lists a runway with no matching
        // hold-short — the entire command must fail (strict mode, matching CROSS).
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = true;

        TaxiRoute route = MakeRouteWithHoldShort("28R");
        route.HoldShortPoints[0].Reason = HoldShortReason.ExplicitHoldShort;
        ac.Ground.AssignedTaxiRoute = route;

        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingShortPhase(route.HoldShortPoints[0]));
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Logger = Logger,
        };
        ac.Phases.Start(ctx);

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand(["09L"], [])])]);
        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("09L", result.Message);
        // The current hold-short must remain uncleared since the command failed.
        Assert.False(route.HoldShortPoints[0].IsCleared);
    }

    [Fact]
    public void ResCross_ListedRunwayIsDestination_FailsEntireCommand()
    {
        // Destination runway in the cross list — CROSS already rejects this
        // ("cannot cross destination runway"); RES CROSS inherits the same gate.
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = true;

        var route = new TaxiRoute
        {
            Segments = [MakeSegment(1, 2, "W", 0.1), MakeSegment(2, 3, "W1", 0.1)],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "28R",
                },
                new HoldShortPoint
                {
                    NodeId = 3,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "30",
                },
            ],
        };
        ac.Ground.AssignedTaxiRoute = route;

        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingShortPhase(route.HoldShortPoints[0]));
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Logger = Logger,
        };
        ac.Phases.Start(ctx);

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand(["30"], [])])]);
        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        // Destination hold-short must not have been silently cleared.
        Assert.False(route.HoldShortPoints[1].IsCleared);
    }

    [Fact]
    public void ResHs_PromotesUpcomingRunwayCrossingToExplicit()
    {
        // Route has an upcoming RunwayCrossing for 28L. RES HS 28L should promote
        // it to ExplicitHoldShort so it survives AutoCross.
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = true;

        var route = new TaxiRoute
        {
            Segments = [MakeSegment(1, 2, "W", 0.1), MakeSegment(2, 3, "W", 0.1)],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "28R",
                },
                new HoldShortPoint
                {
                    NodeId = 3,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28L",
                },
            ],
        };
        ac.Ground.AssignedTaxiRoute = route;

        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingShortPhase(route.HoldShortPoints[0]));
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Logger = Logger,
        };
        ac.Phases.Start(ctx);

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand([], [HoldShortTarget.Parse("28L")])])]);
        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Equal(HoldShortReason.ExplicitHoldShort, route.HoldShortPoints[1].Reason);
        Assert.Equal("28L", route.HoldShortPoints[1].TargetName);
        // Promotion does not pre-clear — the aircraft should still stop there.
        Assert.False(route.HoldShortPoints[1].IsCleared);
    }

    [Fact]
    public void ResHs_TargetNotOnRoute_FailsEntireCommand()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = true;

        TaxiRoute route = MakeRouteWithHoldShort("28R");
        route.HoldShortPoints[0].Reason = HoldShortReason.ExplicitHoldShort;
        ac.Ground.AssignedTaxiRoute = route;

        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingShortPhase(route.HoldShortPoints[0]));
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Logger = Logger,
        };
        ac.Phases.Start(ctx);

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand([], [HoldShortTarget.Parse("09L")])])]);
        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("09L", result.Message);
        // The current hold-short must remain uncleared since the command failed.
        Assert.False(route.HoldShortPoints[0].IsCleared);
    }

    [Fact]
    public void ResCrossPlusHs_BothApplied()
    {
        // Compound: RES CROSS 28R HS 28L — pre-clear 28R, promote 28L's crossing
        // to ExplicitHoldShort. Two separate runway hold-shorts on the route.
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = true;

        var route = new TaxiRoute
        {
            Segments = [MakeSegment(1, 2, "W", 0.1), MakeSegment(2, 3, "W", 0.1)],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28R",
                },
                new HoldShortPoint
                {
                    NodeId = 3,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28L",
                },
            ],
        };
        ac.Ground.AssignedTaxiRoute = route;

        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingShortPhase(route.HoldShortPoints[0]));
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Logger = Logger,
        };
        ac.Phases.Start(ctx);

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand(["28R"], [HoldShortTarget.Parse("28L")])])]);
        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.True(route.HoldShortPoints[0].IsCleared, "28R should be pre-cleared via CROSS");
        Assert.Equal(HoldShortReason.ExplicitHoldShort, route.HoldShortPoints[1].Reason);
        Assert.False(route.HoldShortPoints[1].IsCleared, "28L should remain a stop (promoted, not cleared)");
    }

    // -------------------------------------------------------------------------
    // HS / RES HS against an already-cleared runway crossing
    // -------------------------------------------------------------------------

    [Fact]
    public void ResHs_AutoClearedCrossing_ReArmsHoldShort()
    {
        (AirportGroundLayout? layout, TaxiRoute? route) = MakeCrossingRoute(autoCleared: true);
        AircraftState ac = MakeAircraftHoldingShortOfTaxiwayC(route);

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand([], [HoldShortTarget.Parse("28R")])])]);
        CommandResult result = CommandDispatcher.DispatchCompound(
            compound,
            ac,
            TestDispatch.Context(new SerializableRandom(42), groundLayout: layout)
        );

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        HoldShortPoint hs28R = Assert.Single(route.HoldShortPoints, h => h.TargetName == "28R/10L");
        Assert.Equal(2, hs28R.NodeId);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, hs28R.Reason);
        Assert.False(hs28R.IsCleared, "HS 28R must revoke the AutoCross clearance, else the aircraft taxis straight across");
        Assert.False(hs28R.ClearedByAutoCross);
    }

    [Fact]
    public void Hs_AutoClearedCrossing_ReArmsNearSideBar_DoesNotAddFarSide()
    {
        (AirportGroundLayout? layout, TaxiRoute? route) = MakeCrossingRoute(autoCleared: true);
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = route;

        CommandResult result = GroundCommandHandler.TryHoldShort(ac, new HoldShortCommand(HoldShortTarget.Parse("28R")), layout);

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        HoldShortPoint hs28R = Assert.Single(route.HoldShortPoints, h => h.TargetName == "28R/10L");
        Assert.Equal(2, hs28R.NodeId);
        Assert.False(hs28R.IsCleared);
        Assert.DoesNotContain(route.HoldShortPoints, h => h.NodeId == 4);
    }

    [Fact]
    public void Hs_AfterEnteringRunway_IsRejected()
    {
        (AirportGroundLayout? layout, TaxiRoute? route) = MakeCrossingRoute(autoCleared: true);
        // Aircraft is on segment 2→3: past the entry bar (node 2), between the hold-short bars.
        route.CurrentSegmentIndex = 2;
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = route;

        CommandResult result = GroundCommandHandler.TryHoldShort(ac, new HoldShortCommand(HoldShortTarget.Parse("28R")), layout);

        Assert.False(result.Success);
        Assert.Contains("28R", result.Message!);
        Assert.DoesNotContain(route.HoldShortPoints, h => h.NodeId == 4);
    }

    [Fact]
    public void Hs_WhileHoldingShortOfSameRunway_IsNoOpSuccess()
    {
        // The aircraft is stopped AT the 28R bar, so BuildResumePhases has already bumped
        // CurrentSegmentIndex past it. That must not read as "already entered the runway".
        (AirportGroundLayout? layout, TaxiRoute? route) = MakeCrossingRoute(autoCleared: false);
        route.CurrentSegmentIndex = 2;
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = route;

        CommandResult result = GroundCommandHandler.TryHoldShort(ac, new HoldShortCommand(HoldShortTarget.Parse("28R")), layout);

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        HoldShortPoint hs28R = Assert.Single(route.HoldShortPoints, h => h.TargetName == "28R/10L");
        Assert.Equal(2, hs28R.NodeId);
        Assert.False(hs28R.IsCleared);
    }

    [Fact]
    public void CrossThenHs_SameRunway_ReArmsHoldShort()
    {
        // Latest instruction wins: HS revokes a clearance from any source, including an
        // explicit CROSS the controller issued moments earlier.
        (AirportGroundLayout? layout, TaxiRoute? route) = MakeCrossingRoute(autoCleared: false);
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = route;

        Assert.True(GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand(["28R"], []), ac.Ground.Layout).Success);
        Assert.True(route.HoldShortPoints[1].IsCleared);

        CommandResult result = GroundCommandHandler.TryHoldShort(ac, new HoldShortCommand(HoldShortTarget.Parse("28R")), layout);

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.False(route.HoldShortPoints[1].IsCleared);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, route.HoldShortPoints[1].Reason);
    }

    [Fact]
    public void Hs_DestinationRunway_IsNoOpSuccess()
    {
        (AirportGroundLayout? layout, TaxiRoute? route) = MakeCrossingRoute(autoCleared: false);
        route.HoldShortPoints[1].Reason = HoldShortReason.DestinationRunway;
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = route;

        CommandResult result = GroundCommandHandler.TryHoldShort(ac, new HoldShortCommand(HoldShortTarget.Parse("28R")), layout);

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Equal(HoldShortReason.DestinationRunway, route.HoldShortPoints[1].Reason);
        Assert.Equal(2, route.HoldShortPoints.Count);
    }

    [Fact]
    public void ResHs_MultiTarget_OneUnreachable_AppliesNeither()
    {
        (AirportGroundLayout? layout, TaxiRoute? route) = MakeCrossingRoute(autoCleared: true);
        AircraftState ac = MakeAircraftHoldingShortOfTaxiwayC(route);

        var compound = new CompoundCommand([
            new ParsedBlock(null, [new ResumeCommand([], [HoldShortTarget.Parse("28R"), HoldShortTarget.Parse("09L")])]),
        ]);
        CommandResult result = CommandDispatcher.DispatchCompound(
            compound,
            ac,
            TestDispatch.Context(new SerializableRandom(42), groundLayout: layout)
        );

        Assert.False(result.Success);
        Assert.Contains("09L", result.Message!);
        HoldShortPoint hs28R = Assert.Single(route.HoldShortPoints, h => h.TargetName == "28R/10L");
        Assert.True(hs28R.IsCleared, "a failed compound must not half-apply the reachable target");
        Assert.Equal(HoldShortReason.RunwayCrossing, hs28R.Reason);
    }

    // -------------------------------------------------------------------------
    // TryAssignRunway
    // -------------------------------------------------------------------------

    [Fact]
    public void TryAssignRunway_ValidRunway_SetsAssignedRunway()
    {
        AircraftState ac = MakeGroundAircraft();
        NavigationDatabase navDb = TestNavDbFactory.WithRunways(TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 280));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        CommandResult result = GroundCommandHandler.TryAssignRunway(ac, "28R");

        Assert.True(result.Success);
        Assert.Contains("Runway 28R", result.Message!);
        Assert.NotNull(ac.Phases);
        Assert.NotNull(ac.Phases!.AssignedRunway);
        Assert.Equal("28R", ac.Phases.AssignedRunway!.Designator);
        Assert.Equal("28R", ac.Procedure.DepartureRunway);
        Assert.Null(ac.Procedure.DestinationRunway);
    }

    [Fact]
    public void TryAssignRunway_AirborneArrival_SetsDestinationRunwayNotDeparture()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        NavigationDatabase navDb = TestVnasData.NavigationDb;
        NavigationDatabase.SetInstance(navDb);

        var ac = new AircraftState
        {
            Callsign = "N456",
            AircraftType = "B738",
            Position = new LatLon(37.75, -122.35),
            TrueHeading = new TrueHeading(280),
            Altitude = 5000,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
            Procedure = new AircraftProcedure { ActiveStarId = "WNDSR2" },
            Phases = new PhaseList(),
        };

        CommandResult result = GroundCommandHandler.TryAssignRunway(ac, "30");

        Assert.True(result.Success);
        Assert.Equal("30", ac.Procedure.DestinationRunway);
        Assert.Null(ac.Procedure.DepartureRunway);

        var names = ac.Targets.NavigationRoute.Select(t => t.Name).ToList();
        Assert.Contains("HOPTA", names);
        Assert.Contains("ALLXX", names);
        Assert.Contains("CRSEN", names);
        Assert.Equal(names.IndexOf("HOPTA") + 1, names.IndexOf("ALLXX"));
        Assert.Equal(names.IndexOf("ALLXX") + 1, names.IndexOf("CRSEN"));
        Assert.DoesNotContain("AAAME", names);
    }

    [Fact]
    public void TryAssignRunway_AirborneArrival_ClearsPendingWhenRunwayMismatches()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        NavigationDatabase.SetInstance(TestVnasData.NavigationDb);

        var ac = new AircraftState
        {
            Callsign = "N789",
            AircraftType = "B738",
            Position = new LatLon(37.75, -122.35),
            TrueHeading = new TrueHeading(280),
            Altitude = 5000,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
            Procedure = new AircraftProcedure { ActiveStarId = "WNDSR2" },
            Phases = new PhaseList(),
        };
        RunwayInfo rwy12 = TestRunwayFactory.Make(designator: "12", airportId: "OAK", heading: 120, thresholdLat: 37.73, thresholdLon: -122.22);
        ac.Approach.PendingClearance = new PendingApproachInfo
        {
            Clearance = new ApproachClearance
            {
                ApproachId = "H12-Z",
                AirportCode = "KOAK",
                RunwayId = "12",
                FinalApproachCourse = rwy12.TrueHeading,
            },
            AssignedRunway = rwy12,
        };

        CommandResult result = GroundCommandHandler.TryAssignRunway(ac, "30");

        Assert.True(result.Success);
        Assert.Null(ac.Approach.PendingClearance);
    }

    [Fact]
    public void TryAssignRunway_InvalidRunway_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        NavigationDatabase navDb = TestNavDbFactory.WithRunways(TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 280));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        CommandResult result = GroundCommandHandler.TryAssignRunway(ac, "99X");

        Assert.False(result.Success);
        Assert.Contains("Unknown runway", result.Message!);
    }

    [Fact]
    public void TryAssignRunway_NoRunwayLookup_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        using IDisposable _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());

        CommandResult result = GroundCommandHandler.TryAssignRunway(ac, "28R");

        Assert.False(result.Success);
    }

    [Fact]
    public void TryAssignRunway_NullPhases_CreatesPhaseList()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.Phases = null;
        NavigationDatabase navDb = TestNavDbFactory.WithRunways(TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 280));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        CommandResult result = GroundCommandHandler.TryAssignRunway(ac, "28R");

        Assert.True(result.Success);
        Assert.NotNull(ac.Phases);
    }

    // -------------------------------------------------------------------------
    // TryTaxi auto-detect runway
    // -------------------------------------------------------------------------

    [Fact]
    public void TryTaxi_EndsAtHoldShort_AutoDetectsRunway()
    {
        AircraftState ac = MakeGroundAircraft();
        AirportGroundLayout layout = MakeSimpleLayout();
        // Threshold (Lat1) at node3 position so auto-detect resolves runway
        NavigationDatabase navDb = TestNavDbFactory.WithRunways(
            TestRunwayFactory.Make(designator: "28R", airportId: "OAK", thresholdLat: 37.730, thresholdLon: -122.218, heading: 280)
        );
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new TaxiCommand(["A"], []);

        CommandResult result = GroundCommandHandler.TryTaxi(ac, cmd, layout);

        Assert.True(result.Success);
        Assert.NotNull(ac.Phases?.AssignedRunway);
        // Node 3 is closer to End1 threshold (37.730), so should resolve to End1 designator
        Assert.Equal("28R", ac.Phases!.AssignedRunway!.Designator);
    }

    [Fact]
    public void TryTaxi_EndsAtNonHoldShort_NoAutoDetect()
    {
        AircraftState ac = MakeGroundAircraft();
        // Place aircraft right at node 1, but path only goes A (to node 2 which is intersection)
        // Actually node 2 also has edges to node 3 (A). The path "A" goes from node 1 through all A-edges.
        // Let me create a minimal layout with only 2 non-HS nodes.
        var minLayout = new AirportGroundLayout { AirportId = "TEST" };
        var n1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.728, -122.218),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.729, -122.218),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        minLayout.Nodes[1] = n1;
        minLayout.Nodes[2] = n2;
        var edge = new GroundEdge
        {
            Nodes = [n1, n2],
            TaxiwayName = "B",
            DistanceNm = GeoMath.DistanceNm(n1.Position, n2.Position),
        };
        minLayout.Edges.Add(edge);
        minLayout.RebuildAdjacencyLists();

        var cmd2 = new TaxiCommand(["B"], []);
        CommandResult result = GroundCommandHandler.TryTaxi(ac, cmd2, minLayout);

        Assert.True(result.Success);
        Assert.Null(ac.Phases?.AssignedRunway);
    }

    [Fact]
    public void TryTaxi_ExplicitDestRunway_SetsAssignedRunway()
    {
        AircraftState ac = MakeGroundAircraft();
        AirportGroundLayout layout = MakeSimpleLayout();
        NavigationDatabase navDb = TestNavDbFactory.WithRunways(
            TestRunwayFactory.Make(designator: "28R", airportId: "OAK", thresholdLat: 37.730, thresholdLon: -122.218, heading: 280)
        );
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new TaxiCommand(["A"], [], DestinationRunway: "28R");

        CommandResult result = GroundCommandHandler.TryTaxi(ac, cmd, layout);

        Assert.True(result.Success);
        Assert.NotNull(ac.Phases?.AssignedRunway);
    }

    // -------------------------------------------------------------------------
    // TryBreakConflict
    // -------------------------------------------------------------------------

    [Fact]
    public void TryBreakConflict_OnGround_SetsTimer()
    {
        AircraftState ac = MakeGroundAircraft();

        CommandResult result = GroundCommandHandler.TryBreakConflict(ac);

        Assert.True(result.Success);
        Assert.Equal(15.0, ac.Ground.ConflictBreakRemainingSeconds, precision: 9);
    }

    [Fact]
    public void TryBreakConflict_Airborne_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = false;

        CommandResult result = GroundCommandHandler.TryBreakConflict(ac);

        Assert.False(result.Success);
        Assert.Equal(0.0, ac.Ground.ConflictBreakRemainingSeconds);
    }

    // -------------------------------------------------------------------------
    // TryGo
    // -------------------------------------------------------------------------

    [Fact]
    public void TryGo_InStopAndGoPhase_Succeeds()
    {
        AircraftState ac = MakeGroundAircraft();
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

        CommandResult result = GroundCommandHandler.TryGo(ac);

        Assert.True(result.Success);
    }

    [Fact]
    public void TryGo_NotInStopAndGoPhase_Fails()
    {
        AircraftState ac = MakeGroundAircraft();
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

        CommandResult result = GroundCommandHandler.TryGo(ac);

        Assert.False(result.Success);
        Assert.Contains("stop-and-go", result.Message!);
    }

    // -------------------------------------------------------------------------
    // Parser: BREAK, GO, TAXIALL
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_Break_ReturnsBreakConflictCommand()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("BREAK");

        Assert.IsType<BreakConflictCommand>(cmd.Value);
    }

    [Fact]
    public void Parse_Go_ReturnsGoCommand()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("GO");

        Assert.IsType<GoCommand>(cmd.Value);
    }

    [Fact]
    public void Parse_TaxiAll_ReturnsCommandWithDestinationRunway()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("TAXIALL 30");

        TaxiAllCommand taxiAll = Assert.IsType<TaxiAllCommand>(cmd.Value);
        Assert.Equal("30", taxiAll.DestinationRunway);
    }

    // -------------------------------------------------------------------------
    // DispatchCompound: TAXI + CROSS in same block
    // -------------------------------------------------------------------------

    [Fact]
    public void DispatchCompound_TaxiWithCross_PreClearsHoldShort()
    {
        AircraftState ac = MakeAircraftAtParking();
        AirportGroundLayout layout = MakeSimpleLayout();

        // Compound: TAXI A, CROSS 28R — one block, two parallel commands
        var compound = new CompoundCommand([new ParsedBlock(null, [new TaxiCommand(["A"], []), new CrossRunwayCommand(["28R"], [])])]);

        CommandResult result = CommandDispatcher.DispatchCompound(
            compound,
            ac,
            TestDispatch.Context(new SerializableRandom(42), groundLayout: layout)
        );

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.NotNull(ac.Ground.AssignedTaxiRoute);
        // The CROSS 28R should have pre-cleared the hold-short
        List<HoldShortPoint> allHs = ac.Ground.AssignedTaxiRoute!.HoldShortPoints;
        HoldShortPoint? hs = allHs.FirstOrDefault(h => h.TargetName is not null && RunwayIdentifier.Parse(h.TargetName).Contains("28R"));
        Assert.NotNull(hs);
        Assert.True(hs.IsCleared);
    }

    [Fact]
    public void DispatchCompound_TaxiWithTrailingGiveWay_AssignsRouteAndYields()
    {
        AircraftState ac = MakeAircraftAtParking();
        AirportGroundLayout layout = MakeSimpleLayout();

        // Issue #279: "TAXI A GIVEWAY UAL999" (no comma) splits into a TAXI + standalone GIVEWAY
        // sharing one block. Applied in source order, GIVEWAY sees the just-assigned taxi route.
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("TAXI A GIVEWAY UAL999");
        Assert.True(parsed.IsSuccess, parsed.Reason);

        CommandResult result = CommandDispatcher.DispatchCompound(
            parsed.Value!,
            ac,
            TestDispatch.Context(new SerializableRandom(42), groundLayout: layout)
        );

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.NotNull(ac.Ground.AssignedTaxiRoute);
        Assert.NotNull(ac.Ground.Hold);
        Assert.Equal(HoldKind.GiveWay, ac.Ground.Hold!.Kind);
        Assert.Equal("UAL999", ac.Ground.Hold.YieldTarget);
    }

    // -------------------------------------------------------------------------
    // TryGiveWay — regression guards for invalidated prior-session claims (review §7 "Closed")
    // -------------------------------------------------------------------------

    [Fact]
    public void TryGiveWay_OnGroundWithRoute_HoldsAircraft()
    {
        // Locks in: standalone GIVEWAY holds the aircraft until the target passes.
        // The plan claim "GIVEWAY only fires inside LV/AT conditional dispatch" was wrong.
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = MakeRouteWithHoldShort("28R");

        CommandResult result = GroundCommandHandler.TryGiveWay(ac, "UAL999");

        Assert.True(result.Success);
        Assert.Contains("Give way to UAL999", result.Message!);
        Assert.True(ac.Ground.IsImmobile);
        Assert.NotNull(ac.Ground.Hold);
        Assert.Equal(HoldKind.GiveWay, ac.Ground.Hold!.Kind);
        Assert.Equal("UAL999", ac.Ground.Hold.YieldTarget);
    }

    [Fact]
    public void TryGiveWay_ClearsExpeditingTaxi()
    {
        // GIVEWAY is a hold-class command: telling the aircraft to wait for another
        // implicitly cancels an earlier EXPEDITE, so it resumes at normal taxi speed.
        // Matches TryHoldPosition / TryResumeTaxi / TryHoldShort.
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = MakeRouteWithHoldShort("28R");
        ac.Ground.IsExpeditingTaxi = true;

        GroundCommandHandler.TryGiveWay(ac, "UAL999");

        Assert.False(ac.Ground.IsExpeditingTaxi);
    }

    [Fact]
    public void TryGiveWay_NoTaxiRoute_Fails()
    {
        // GIVEWAY requires an assigned taxi route — without one there's no taxi to defer.
        AircraftState ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = null;

        CommandResult result = GroundCommandHandler.TryGiveWay(ac, "UAL999");

        Assert.False(result.Success);
        Assert.Contains("must have a taxi route assigned", result.Message!);
    }

    [Fact]
    public void TryGiveWay_Airborne_Fails()
    {
        // GIVEWAY is a ground-only command.
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = false;

        CommandResult result = GroundCommandHandler.TryGiveWay(ac, "UAL999");

        Assert.False(result.Success);
        Assert.Contains("on the ground", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryExitCommand — landing/exit phase gate
    // -------------------------------------------------------------------------

    [Fact]
    public void TryExitCommand_NoLandingOrExitPhase_Fails()
    {
        // Silent-failure case: EXIT issued during cruise/enroute used to silently
        // store RequestedExit for a landing that may never happen. Now requires a
        // pending or active LandingPhase / HelicopterLandingPhase / RunwayExitPhase.
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = false;
        ac.Phases = new PhaseList();

        CommandResult result = GroundCommandHandler.TryExitCommand(
            ac,
            new ExitPreference { Side = ExitSide.Right },
            noDelete: false,
            expedite: false
        );

        Assert.False(result.Success);
        Assert.Null(ac.Phases.RequestedExit);
    }

    [Fact]
    public void TryExitCommand_PendingLandingPhase_Succeeds()
    {
        // ER/EL is normally issued on short final, before LandingPhase becomes
        // active — the LandingPhase is pending in the list. Recording-based
        // tests (ExitRightTaxiwaySelectionTests) exercise this path.
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = false;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new LandingPhase());

        CommandResult result = GroundCommandHandler.TryExitCommand(
            ac,
            new ExitPreference { Side = ExitSide.Right, Taxiway = "D" },
            noDelete: false,
            expedite: false
        );

        Assert.True(result.Success);
        Assert.NotNull(ac.Phases.RequestedExit);
        Assert.Equal("D", ac.Phases.RequestedExit.Taxiway);
    }

    [Fact]
    public void TryExitCommand_ActiveRunwayExitPhase_Succeeds()
    {
        // Updating exit preference mid-rollout should still work.
        AircraftState ac = MakeGroundAircraft();
        ac.Phases = new PhaseList();
        ac.Phases.Add(new RunwayExitPhase());

        CommandResult result = GroundCommandHandler.TryExitCommand(ac, new ExitPreference { Side = ExitSide.Left }, noDelete: false, expedite: false);

        Assert.True(result.Success);
        Assert.Equal(ExitSide.Left, ac.Phases.RequestedExit?.Side);
    }

    [Fact]
    public void TryExitCommand_TaxiwayOnlyAfterSide_InheritsStandingSide()
    {
        // Issue #276: preset "ER ; EXIT D" is two separate exit commands. The first
        // (ER) sets Side=Right; the second (EXIT D) is taxiway-only (Side=null) and
        // must NOT drop the standing Right — it should exit right AT D.
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = false;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new LandingPhase());

        CommandResult er = GroundCommandHandler.TryExitCommand(ac, new ExitPreference { Side = ExitSide.Right }, noDelete: false, expedite: false);
        Assert.True(er.Success);

        CommandResult exitD = GroundCommandHandler.TryExitCommand(ac, new ExitPreference { Taxiway = "D" }, noDelete: false, expedite: false);
        Assert.True(exitD.Success);

        Assert.Equal(ExitSide.Right, ac.Phases.RequestedExit?.Side);
        Assert.Equal("D", ac.Phases.RequestedExit?.Taxiway);
    }

    [Fact]
    public void TryExitCommand_ExplicitSideAfterSide_Overrides()
    {
        // A later command that carries its own explicit side (EL D after ER) wins —
        // the standing side is only inherited by taxiway-only commands.
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = false;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new LandingPhase());

        GroundCommandHandler.TryExitCommand(ac, new ExitPreference { Side = ExitSide.Right }, noDelete: false, expedite: false);
        GroundCommandHandler.TryExitCommand(ac, new ExitPreference { Side = ExitSide.Left, Taxiway = "D" }, noDelete: false, expedite: false);

        Assert.Equal(ExitSide.Left, ac.Phases.RequestedExit?.Side);
        Assert.Equal("D", ac.Phases.RequestedExit?.Taxiway);
    }

    [Fact]
    public void TryExitCommand_TaxiwayOnlyWithNoStandingSide_KeepsNullSide()
    {
        // A bare EXIT D with no prior side stays side-less (inferred side is applied
        // later during exit resolution, not here).
        AircraftState ac = MakeGroundAircraft();
        ac.IsOnGround = false;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new LandingPhase());

        GroundCommandHandler.TryExitCommand(ac, new ExitPreference { Taxiway = "D" }, noDelete: false, expedite: false);

        Assert.Null(ac.Phases.RequestedExit?.Side);
        Assert.Equal("D", ac.Phases.RequestedExit?.Taxiway);
    }
}
