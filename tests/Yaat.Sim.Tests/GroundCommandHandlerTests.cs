using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

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
            GroundSpeed = 0,
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

        var result = GroundCommandHandler.TryTaxi(ac, cmd, null, null, Logger);

        Assert.False(result.Success);
        Assert.Contains("No airport ground layout", result.Message!);
    }

    [Fact]
    public void TryTaxi_UnknownTaxiway_Fails()
    {
        var ac = MakeGroundAircraft();
        var layout = MakeSimpleLayout();
        var cmd = new TaxiCommand(["ZZZZZ"], [], DestinationRunway: "28R");

        var result = GroundCommandHandler.TryTaxi(ac, cmd, layout, null, Logger);

        Assert.False(result.Success);
    }

    [Fact]
    public void TryTaxi_ValidPath_Succeeds()
    {
        var ac = MakeGroundAircraft();
        var layout = MakeSimpleLayout();
        var cmd = new TaxiCommand(["A"], [], DestinationRunway: "28R");

        var result = GroundCommandHandler.TryTaxi(ac, cmd, layout, null, Logger);

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

        var result = GroundCommandHandler.TryTaxi(ac, cmd, layout, null, Logger, autoCrossRunway: true);

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

        var result = GroundCommandHandler.TryPushback(ac, cmd, null, Logger);

        Assert.False(result.Success);
        Assert.Contains("at parking", result.Message!);
    }

    [Fact]
    public void TryPushback_AtParking_NoArgs_Succeeds()
    {
        var ac = MakeAircraftAtParking();
        var cmd = new PushbackCommand();

        var result = GroundCommandHandler.TryPushback(ac, cmd, null, Logger);

        Assert.True(result.Success);
        Assert.Contains("Pushing back", result.Message!);
    }

    [Fact]
    public void TryPushback_WithTaxiway_ResolvesTarget()
    {
        var ac = MakeAircraftAtParking();
        var layout = MakeSimpleLayout();
        var cmd = new PushbackCommand(Taxiway: "A");

        var result = GroundCommandHandler.TryPushback(ac, cmd, layout, Logger);

        Assert.True(result.Success);
        Assert.Contains("onto A", result.Message!);
    }

    [Fact]
    public void TryPushback_WithHeading_IncludesInMessage()
    {
        var ac = MakeAircraftAtParking();
        var cmd = new PushbackCommand(Heading: 180);

        var result = GroundCommandHandler.TryPushback(ac, cmd, null, Logger);

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
        var holdPhase = new HoldingShortPhase(new HoldShortPoint
        {
            NodeId = 3,
            Reason = HoldShortReason.RunwayCrossing,
            TargetName = "28R/10L",
        });
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

        var result = GroundCommandHandler.TryHoldShort(ac, cmd, null, Logger);

        Assert.False(result.Success);
        Assert.Contains("on the ground", result.Message!);
    }

    [Fact]
    public void TryHoldShort_NoRoute_Fails()
    {
        var ac = MakeGroundAircraft();
        var cmd = new HoldShortCommand("28R");

        var result = GroundCommandHandler.TryHoldShort(ac, cmd, null, Logger);

        Assert.False(result.Success);
        Assert.Contains("No taxi route", result.Message!);
    }

    [Fact]
    public void TryHoldShort_NoLayout_Fails()
    {
        var ac = MakeGroundAircraft();
        ac.AssignedTaxiRoute = MakeRouteWithHoldShort("28R/10L");
        var cmd = new HoldShortCommand("B");

        var result = GroundCommandHandler.TryHoldShort(ac, cmd, null, Logger);

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

        var result = GroundCommandHandler.TryFollow(ac, cmd, null, Logger);

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
        ac.Phases.Start(new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0,
            Logger = NullLogger.Instance,
        });
        var cmd = new FollowCommand("UAL123");

        var result = GroundCommandHandler.TryFollow(ac, cmd, null, Logger);

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
}
