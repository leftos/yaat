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
        var ac = MakeGroundAircraft();
        var cmd = new TaxiCommand(["A"], [], DestinationRunway: "28R");

        var result = GroundCommandHandler.TryTaxi(ac, cmd, null);

        Assert.False(result.Success);
        Assert.Contains("No airport ground layout", result.Message!);
    }

    [Fact]
    public void TryTaxi_UnknownTaxiway_Fails()
    {
        var ac = MakeGroundAircraft();
        var layout = MakeSimpleLayout();
        var cmd = new TaxiCommand(["ZZZZZ"], [], DestinationRunway: "28R");

        var result = GroundCommandHandler.TryTaxi(ac, cmd, layout);

        Assert.False(result.Success);
    }

    [Fact]
    public void TryTaxi_ValidPath_Succeeds()
    {
        var ac = MakeGroundAircraft();
        var layout = MakeSimpleLayout();
        var cmd = new TaxiCommand(["A"], [], DestinationRunway: "28R");

        var result = GroundCommandHandler.TryTaxi(ac, cmd, layout);

        Assert.True(result.Success);
        Assert.NotNull(ac.Ground.AssignedTaxiRoute);
        Assert.True(ac.Ground.AssignedTaxiRoute!.Segments.Count > 0);
    }

    [Fact]
    public void TryTaxi_AutoCrossRunway_ClearsHoldShorts()
    {
        var ac = MakeGroundAircraft();
        var layout = MakeSimpleLayout();
        var cmd = new TaxiCommand(["A"], [], DestinationRunway: "28R");

        var result = GroundCommandHandler.TryTaxi(ac, cmd, layout, autoCrossRunway: true);

        Assert.True(result.Success);
        // All RunwayCrossing hold-shorts should be pre-cleared
        foreach (var hs in ac.Ground.AssignedTaxiRoute!.HoldShortPoints)
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
        var cmd = new PushbackCommand(null, null, null, null, null);

        var result = GroundCommandHandler.TryPushback(ac, cmd, null);

        Assert.False(result.Success);
        Assert.Contains("at parking", result.Message!);
    }

    [Fact]
    public void TryPushback_AtParking_NoArgs_Succeeds()
    {
        var ac = MakeAircraftAtParking();
        var cmd = new PushbackCommand(null, null, null, null, null);

        var result = GroundCommandHandler.TryPushback(ac, cmd, null);

        Assert.True(result.Success);
        Assert.Contains("Pushing back", result.Message!);
    }

    [Fact]
    public void TryPushback_WithTaxiway_ResolvesTarget()
    {
        var ac = MakeAircraftAtParking();
        var layout = MakeSimpleLayout();
        var cmd = new PushbackCommand(null, "A", null, null, null);

        var result = GroundCommandHandler.TryPushback(ac, cmd, layout);

        Assert.True(result.Success);
        Assert.Contains("onto A", result.Message!);
    }

    [Fact]
    public void TryPushback_WithHeading_IncludesInMessage()
    {
        var ac = MakeAircraftAtParking();
        var cmd = new PushbackCommand(new MagneticHeading(180), null, null, null, null);

        var result = GroundCommandHandler.TryPushback(ac, cmd, null);

        Assert.True(result.Success);
        Assert.Contains("180", result.Message!);
    }

    // Layout for cardinal-hint snap tests: aircraft sits at the central intersection of a
    // straight north-south taxiway "A" with edges in both directions.
    private static AirportGroundLayout MakeCardinalSnapLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var node1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.727, -122.218),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.728, -122.218),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node3 = new GroundNode
        {
            Id = 3,
            Position = new LatLon(37.729, -122.218),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;
        layout.Nodes[3] = node3;
        var edge12 = new GroundEdge
        {
            Nodes = [node1, node2],
            TaxiwayName = "A",
            DistanceNm = GeoMath.DistanceNm(node1.Position, node2.Position),
        };
        var edge23 = new GroundEdge
        {
            Nodes = [node2, node3],
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

    [Fact]
    public void TryPushback_TaxiwayWithFaceN_SnapsNorthEdge()
    {
        var ac = MakeAircraftAtParking(); // sits at (37.728, -122.218) == node2
        var layout = MakeCardinalSnapLayout();
        // FACE N → cardinal hint = 360° magnetic; closer of (180, 360) is 360.
        var cmd = new PushbackCommand(new MagneticHeading(360), "A", null, null, null);

        var result = GroundCommandHandler.TryPushback(ac, cmd, layout);

        Assert.True(result.Success);
        Assert.Contains("face heading 360", result.Message!);
    }

    [Fact]
    public void TryPushback_TaxiwayWithFaceS_SnapsSouthEdge()
    {
        var ac = MakeAircraftAtParking();
        var layout = MakeCardinalSnapLayout();
        // FACE S → cardinal hint = 180°; closer of (180, 360) is 180.
        var cmd = new PushbackCommand(new MagneticHeading(180), "A", null, null, null);

        var result = GroundCommandHandler.TryPushback(ac, cmd, layout);

        Assert.True(result.Success);
        Assert.Contains("face heading 180", result.Message!);
    }

    [Fact]
    public void TryPushback_CardinalAlone_UsesAbsoluteFacing()
    {
        // Without a taxiway, the cardinal is the absolute target facing (no edge snap).
        var ac = MakeAircraftAtParking();
        var cmd = new PushbackCommand(new MagneticHeading(45), null, null, null, null);

        var result = GroundCommandHandler.TryPushback(ac, cmd, null);

        Assert.True(result.Success);
        Assert.Contains("face heading 045", result.Message!);
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
    public void TryCrossRunway_FromHoldingShort_DestinationRunway_Fails()
    {
        var ac = MakeGroundAircraft();
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
        var ac = MakeGroundAircraft();
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

        var cmd = new CrossRunwayCommand("01L");
        var result = GroundCommandHandler.TryCrossRunway(ac, cmd);

        Assert.False(result.Success);
        Assert.Contains("01L", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_FromHoldingShort_ExplicitHoldShort_Succeeds()
    {
        var ac = MakeGroundAircraft();
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

        var cmd = new CrossRunwayCommand("28R");
        var result = GroundCommandHandler.TryCrossRunway(ac, cmd);

        Assert.True(result.Success);
        Assert.Contains("Cross 28R", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_PreClearInRoute_MarksHoldShortCleared()
    {
        var ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = MakeRouteWithHoldShort("28R/10L");

        // Not currently at a hold-short phase — pre-clear mode
        var cmd = new CrossRunwayCommand("28R");
        var result = GroundCommandHandler.TryCrossRunway(ac, cmd);

        Assert.True(result.Success);
        Assert.True(ac.Ground.AssignedTaxiRoute.HoldShortPoints[0].IsCleared);
    }

    [Fact]
    public void TryCrossRunway_NoMatchingHoldShort_Fails()
    {
        var ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = MakeRouteWithHoldShort("15/33");

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

    // --- Named CROSS for taxiway/intersection holds ---

    [Fact]
    public void TryCrossRunway_NamedTaxiway_FromHoldingShort_Succeeds()
    {
        var ac = MakeGroundAircraft();
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

        var result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand("B"));

        Assert.True(result.Success);
        Assert.Contains("B", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_NamedTaxiway_PreClearInRoute_MarksHoldShortCleared()
    {
        var ac = MakeGroundAircraft();
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

        var result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand("B"));

        Assert.True(result.Success);
        Assert.True(ac.Ground.AssignedTaxiRoute.HoldShortPoints[0].IsCleared);
    }

    // --- Bare CROSS (no runway argument) ---

    [Fact]
    public void TryCrossRunway_Bare_FromHoldingShort_RunwayCrossing_Succeeds()
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

        var result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand(null));

        Assert.True(result.Success);
        Assert.Contains("28R/10L", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_Bare_FromHoldingShort_TaxiwayExplicit_Succeeds()
    {
        var ac = MakeGroundAircraft();
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

        var result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand(null));

        Assert.True(result.Success);
        Assert.Contains("B", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_Bare_FromHoldingShort_DestinationRunway_Fails()
    {
        var ac = MakeGroundAircraft();
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
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0,
            Logger = NullLogger.Instance,
        };
        ac.Phases.Start(ctx);

        var result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand(null));

        Assert.False(result.Success);
        Assert.Contains("LUAW", result.Message!);
        Assert.Contains("CTO", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_Bare_FromTaxi_ClearsFirstUnclearedHoldShortOnly()
    {
        var ac = MakeGroundAircraft();
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

        var result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand(null));

        Assert.True(result.Success);
        Assert.Contains("28L/10R", result.Message!);
        Assert.True(ac.Ground.AssignedTaxiRoute.HoldShortPoints[0].IsCleared);
        Assert.False(ac.Ground.AssignedTaxiRoute.HoldShortPoints[1].IsCleared);
    }

    [Fact]
    public void TryCrossRunway_Bare_FromTaxi_NextIsDestinationRunway_Fails()
    {
        var ac = MakeGroundAircraft();
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

        var result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand(null));

        Assert.False(result.Success);
        Assert.Contains("LUAW", result.Message!);
        Assert.Contains("CTO", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_Bare_FromTaxi_NoUnclearedHoldShorts_Fails()
    {
        var ac = MakeGroundAircraft();
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

        var result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand(null));

        Assert.False(result.Success);
        Assert.Contains("No upcoming hold-short", result.Message!);
    }

    [Fact]
    public void TryCrossRunway_Bare_FromTaxi_NoRoute_Fails()
    {
        var ac = MakeGroundAircraft();
        // No route assigned, not holding short

        var result = GroundCommandHandler.TryCrossRunway(ac, new CrossRunwayCommand(null));

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
        ac.Ground.AssignedTaxiRoute = MakeRouteWithHoldShort("28R/10L");
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
        var cmd = new FollowGroundCommand("UAL123");

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
        var cmd = new FollowGroundCommand("UAL123");

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
        Assert.True(ac.Ground.IsImmobile);
        Assert.Equal(HoldDirective.HoldPosition, ac.Ground.Hold);
    }

    [Fact]
    public void TryHoldPosition_TaxiingPhase_MentionsTaxiway()
    {
        // RPO-visible feedback should describe what state the sim now believes.
        var ac = MakeGroundAircraft();
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

        var result = GroundCommandHandler.TryHoldPosition(ac);

        Assert.True(result.Success);
        Assert.Contains("taxiway A", result.Message!);
    }

    [Fact]
    public void TryHoldPosition_LineUpPhase_MentionsRunway()
    {
        var ac = MakeGroundAircraft();
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

        var result = GroundCommandHandler.TryHoldPosition(ac);

        Assert.True(result.Success);
        Assert.Contains("runway 28R", result.Message!);
    }

    [Fact]
    public void TryHoldPosition_ClearsExpeditingTaxi()
    {
        var ac = MakeGroundAircraft();
        ac.Ground.IsExpeditingTaxi = true;

        GroundCommandHandler.TryHoldPosition(ac);

        Assert.False(ac.Ground.IsExpeditingTaxi);
    }

    [Fact]
    public void TryResumeTaxi_ClearsExpeditingTaxi()
    {
        var ac = MakeGroundAircraft();
        ac.Ground.Hold = HoldDirective.HoldPosition;
        ac.Ground.IsExpeditingTaxi = true;

        GroundCommandHandler.TryResumeTaxi(ac);

        Assert.False(ac.Ground.IsExpeditingTaxi);
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
        ac.Ground.Hold = HoldDirective.HoldPosition;

        var result = GroundCommandHandler.TryResumeTaxi(ac);

        Assert.True(result.Success);
        Assert.False(ac.Ground.IsImmobile);
        Assert.Null(ac.Ground.Hold);
    }

    [Fact]
    public void TryResumeTaxi_NotHeld_Fails()
    {
        var ac = MakeGroundAircraft();
        ac.Ground.Hold = null;

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

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand([], [])])]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
    }

    [Fact]
    public void Resume_ClearsRunwayCrossingHoldShortPhase()
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

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand([], [])])]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
    }

    [Fact]
    public void Resume_DoesNotClearDestinationRunwayHoldShortPhase()
    {
        var ac = MakeGroundAircraft();
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
        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

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
        var ac = MakeGroundAircraft();
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
        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.True(route.HoldShortPoints[1].IsCleared, "Upcoming 28L hold-short should be pre-cleared");
    }

    [Fact]
    public void ResCross_RunwayNotOnRoute_FailsEntireCommand()
    {
        // Route only contains 28R. RES CROSS 09L lists a runway with no matching
        // hold-short — the entire command must fail (strict mode, matching CROSS).
        var ac = MakeGroundAircraft();
        ac.IsOnGround = true;

        var route = MakeRouteWithHoldShort("28R");
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
        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

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
        var ac = MakeGroundAircraft();
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
        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

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
        var ac = MakeGroundAircraft();
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

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand([], ["28L"])])]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Equal(HoldShortReason.ExplicitHoldShort, route.HoldShortPoints[1].Reason);
        Assert.Equal("28L", route.HoldShortPoints[1].TargetName);
        // Promotion does not pre-clear — the aircraft should still stop there.
        Assert.False(route.HoldShortPoints[1].IsCleared);
    }

    [Fact]
    public void ResHs_TargetNotOnRoute_FailsEntireCommand()
    {
        var ac = MakeGroundAircraft();
        ac.IsOnGround = true;

        var route = MakeRouteWithHoldShort("28R");
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

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand([], ["09L"])])]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

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
        var ac = MakeGroundAircraft();
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

        var compound = new CompoundCommand([new ParsedBlock(null, [new ResumeCommand(["28R"], ["28L"])])]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.True(route.HoldShortPoints[0].IsCleared, "28R should be pre-cleared via CROSS");
        Assert.Equal(HoldShortReason.ExplicitHoldShort, route.HoldShortPoints[1].Reason);
        Assert.False(route.HoldShortPoints[1].IsCleared, "28L should remain a stop (promoted, not cleared)");
    }

    // -------------------------------------------------------------------------
    // TryAssignRunway
    // -------------------------------------------------------------------------

    [Fact]
    public void TryAssignRunway_ValidRunway_SetsAssignedRunway()
    {
        var ac = MakeGroundAircraft();
        var navDb = TestNavDbFactory.WithRunways(TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 280));
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var result = GroundCommandHandler.TryAssignRunway(ac, "28R");

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

        var navDb = TestVnasData.NavigationDb;
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
        };
        ac.Phases = new PhaseList();

        var result = GroundCommandHandler.TryAssignRunway(ac, "30");

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
        };
        ac.Phases = new PhaseList();
        var rwy12 = TestRunwayFactory.Make(designator: "12", airportId: "OAK", heading: 120, thresholdLat: 37.73, thresholdLon: -122.22);
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

        var result = GroundCommandHandler.TryAssignRunway(ac, "30");

        Assert.True(result.Success);
        Assert.Null(ac.Approach.PendingClearance);
    }

    [Fact]
    public void TryAssignRunway_InvalidRunway_Fails()
    {
        var ac = MakeGroundAircraft();
        var navDb = TestNavDbFactory.WithRunways(TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 280));
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var result = GroundCommandHandler.TryAssignRunway(ac, "99X");

        Assert.False(result.Success);
        Assert.Contains("Unknown runway", result.Message!);
    }

    [Fact]
    public void TryAssignRunway_NoRunwayLookup_Fails()
    {
        var ac = MakeGroundAircraft();
        using var _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());

        var result = GroundCommandHandler.TryAssignRunway(ac, "28R");

        Assert.False(result.Success);
    }

    [Fact]
    public void TryAssignRunway_NullPhases_CreatesPhaseList()
    {
        var ac = MakeGroundAircraft();
        ac.Phases = null;
        var navDb = TestNavDbFactory.WithRunways(TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 280));
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var result = GroundCommandHandler.TryAssignRunway(ac, "28R");

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
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new TaxiCommand(["A"], []);

        var result = GroundCommandHandler.TryTaxi(ac, cmd, layout);

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
        var result = GroundCommandHandler.TryTaxi(ac, cmd2, minLayout);

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
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new TaxiCommand(["A"], [], DestinationRunway: "28R");

        var result = GroundCommandHandler.TryTaxi(ac, cmd, layout);

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
        Assert.Equal(15.0, ac.Ground.ConflictBreakRemainingSeconds, precision: 9);
    }

    [Fact]
    public void TryBreakConflict_Airborne_Fails()
    {
        var ac = MakeGroundAircraft();
        ac.IsOnGround = false;

        var result = GroundCommandHandler.TryBreakConflict(ac);

        Assert.False(result.Success);
        Assert.Equal(0.0, ac.Ground.ConflictBreakRemainingSeconds);
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

        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42), groundLayout: layout));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.NotNull(ac.Ground.AssignedTaxiRoute);
        // The CROSS 28R should have pre-cleared the hold-short
        var allHs = ac.Ground.AssignedTaxiRoute!.HoldShortPoints;
        var hs = allHs.FirstOrDefault(h => h.TargetName is not null && RunwayIdentifier.Parse(h.TargetName).Contains("28R"));
        Assert.NotNull(hs);
        Assert.True(hs.IsCleared);
    }

    [Fact]
    public void DispatchCompound_TaxiWithTrailingGiveWay_AssignsRouteAndYields()
    {
        var ac = MakeAircraftAtParking();
        var layout = MakeSimpleLayout();

        // Issue #279: "TAXI A GIVEWAY UAL999" (no comma) splits into a TAXI + standalone GIVEWAY
        // sharing one block. Applied in source order, GIVEWAY sees the just-assigned taxi route.
        var parsed = CommandParser.ParseCompound("TAXI A GIVEWAY UAL999");
        Assert.True(parsed.IsSuccess, parsed.Reason);

        var result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(new SerializableRandom(42), groundLayout: layout));

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
        var ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = MakeRouteWithHoldShort("28R");

        var result = GroundCommandHandler.TryGiveWay(ac, "UAL999");

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
        var ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = MakeRouteWithHoldShort("28R");
        ac.Ground.IsExpeditingTaxi = true;

        GroundCommandHandler.TryGiveWay(ac, "UAL999");

        Assert.False(ac.Ground.IsExpeditingTaxi);
    }

    [Fact]
    public void TryGiveWay_NoTaxiRoute_Fails()
    {
        // GIVEWAY requires an assigned taxi route — without one there's no taxi to defer.
        var ac = MakeGroundAircraft();
        ac.Ground.AssignedTaxiRoute = null;

        var result = GroundCommandHandler.TryGiveWay(ac, "UAL999");

        Assert.False(result.Success);
        Assert.Contains("must have a taxi route assigned", result.Message!);
    }

    [Fact]
    public void TryGiveWay_Airborne_Fails()
    {
        // GIVEWAY is a ground-only command.
        var ac = MakeGroundAircraft();
        ac.IsOnGround = false;

        var result = GroundCommandHandler.TryGiveWay(ac, "UAL999");

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
        var ac = MakeGroundAircraft();
        ac.IsOnGround = false;
        ac.Phases = new PhaseList();

        var result = GroundCommandHandler.TryExitCommand(ac, new ExitPreference { Side = ExitSide.Right }, noDelete: false, expedite: false);

        Assert.False(result.Success);
        Assert.Null(ac.Phases.RequestedExit);
    }

    [Fact]
    public void TryExitCommand_PendingLandingPhase_Succeeds()
    {
        // ER/EL is normally issued on short final, before LandingPhase becomes
        // active — the LandingPhase is pending in the list. Recording-based
        // tests (ExitRightTaxiwaySelectionTests) exercise this path.
        var ac = MakeGroundAircraft();
        ac.IsOnGround = false;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new LandingPhase());

        var result = GroundCommandHandler.TryExitCommand(
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
        var ac = MakeGroundAircraft();
        ac.Phases = new PhaseList();
        ac.Phases.Add(new RunwayExitPhase());

        var result = GroundCommandHandler.TryExitCommand(ac, new ExitPreference { Side = ExitSide.Left }, noDelete: false, expedite: false);

        Assert.True(result.Success);
        Assert.Equal(ExitSide.Left, ac.Phases.RequestedExit?.Side);
    }

    [Fact]
    public void TryExitCommand_TaxiwayOnlyAfterSide_InheritsStandingSide()
    {
        // Issue #276: preset "ER ; EXIT D" is two separate exit commands. The first
        // (ER) sets Side=Right; the second (EXIT D) is taxiway-only (Side=null) and
        // must NOT drop the standing Right — it should exit right AT D.
        var ac = MakeGroundAircraft();
        ac.IsOnGround = false;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new LandingPhase());

        var er = GroundCommandHandler.TryExitCommand(ac, new ExitPreference { Side = ExitSide.Right }, noDelete: false, expedite: false);
        Assert.True(er.Success);

        var exitD = GroundCommandHandler.TryExitCommand(ac, new ExitPreference { Taxiway = "D" }, noDelete: false, expedite: false);
        Assert.True(exitD.Success);

        Assert.Equal(ExitSide.Right, ac.Phases.RequestedExit?.Side);
        Assert.Equal("D", ac.Phases.RequestedExit?.Taxiway);
    }

    [Fact]
    public void TryExitCommand_ExplicitSideAfterSide_Overrides()
    {
        // A later command that carries its own explicit side (EL D after ER) wins —
        // the standing side is only inherited by taxiway-only commands.
        var ac = MakeGroundAircraft();
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
        var ac = MakeGroundAircraft();
        ac.IsOnGround = false;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new LandingPhase());

        GroundCommandHandler.TryExitCommand(ac, new ExitPreference { Taxiway = "D" }, noDelete: false, expedite: false);

        Assert.Null(ac.Phases.RequestedExit?.Side);
        Assert.Equal("D", ac.Phases.RequestedExit?.Taxiway);
    }
}
