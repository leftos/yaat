using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for adjusting a taxiing aircraft's taxi speed via <c>SPD &lt;n&gt;</c>.
/// The commanded value becomes the taxi-speed cap (<see cref="GroundNavigator.MaxSpeedKts"/>),
/// slower or faster than the category default, clamped to [min, 1.3× default] and mutually
/// exclusive with expedite taxi. Corner/arc/braking/conflict caps still win downstream.
/// </summary>
public class TaxiSpeedCommandTests
{
    private const double JetTaxiDefault = 30.0; // CategoryPerformance.TaxiSpeed(Jet)

    private static double JetMax() =>
        Math.Round(CategoryPerformance.TaxiSpeed(AircraftCategory.Jet) * CategoryPerformance.TaxiExpediteMultiplier, MidpointRounding.AwayFromZero);

    private static double MinCap() => CategoryPerformance.MinCommandedTaxiSpeedKts;

    /// <summary>Collinear straight: node0 --A--> node1 --A--> node2 --A--> node3, all taxiway intersections.</summary>
    private static AirportGroundLayout BuildStraightLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        for (int i = 0; i < 4; i++)
        {
            layout.Nodes[i] = new GroundNode
            {
                Id = i,
                Position = new LatLon(37.620 + (i * 0.002), -122.380),
                Type = GroundNodeType.TaxiwayIntersection,
            };
        }

        for (int i = 0; i < 3; i++)
        {
            var edge = new GroundEdge
            {
                Nodes = [layout.Nodes[i], layout.Nodes[i + 1]],
                TaxiwayName = "A",
                DistanceNm = GeoMath.DistanceNm(layout.Nodes[i].Position, layout.Nodes[i + 1].Position),
            };
            layout.Nodes[i].Edges.Add(edge);
            layout.Nodes[i + 1].Edges.Add(edge);
            layout.Edges.Add(edge);
        }

        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static (AircraftState Aircraft, TaxiingPhase Phase, PhaseContext Ctx) MakeTaxiing(AirportGroundLayout layout)
    {
        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = layout.Nodes[0].Position,
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
        };
        aircraft.Ground.Layout = layout;
        aircraft.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[0].Directed(layout.Nodes[0], layout.Nodes[1]) },
                new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[1].Directed(layout.Nodes[1], layout.Nodes[2]) },
                new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[2].Directed(layout.Nodes[2], layout.Nodes[3]) },
            ],
            HoldShortPoints = [],
        };

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };

        aircraft.Phases = new PhaseList();
        var phase = new TaxiingPhase();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Start(ctx);
        return (aircraft, phase, ctx);
    }

    private static CommandResult Dispatch(ParsedCommand cmd, AircraftState ac, AirportGroundLayout layout) =>
        CommandDispatcher.DispatchCompound(
            new CompoundCommand([new ParsedBlock(null, [cmd])]),
            ac,
            TestDispatch.Context(Random.Shared, groundLayout: layout)
        );

    [Fact]
    public void Spd_WhileTaxiing_IsAccepted_AndSetsCommandedSpeed()
    {
        var layout = BuildStraightLayout();
        var (ac, _, _) = MakeTaxiing(layout);

        var result = Dispatch(new SpeedCommand(10), ac, layout);

        Assert.True(result.Success, result.Message);
        Assert.Equal(10, ac.Ground.CommandedTaxiSpeedKts);
    }

    [Fact]
    public void Spd_WhileTaxiing_AppliesCapToNavigator()
    {
        var layout = BuildStraightLayout();
        var (ac, phase, ctx) = MakeTaxiing(layout);

        Dispatch(new SpeedCommand(10), ac, layout);
        phase.OnTick(ctx);

        Assert.Equal(10, phase.NavMaxSpeedKts);
    }

    [Fact]
    public void Spd_AboveDefault_RaisesCapAboveCategoryDefault()
    {
        var layout = BuildStraightLayout();
        var (ac, phase, ctx) = MakeTaxiing(layout);

        Dispatch(new SpeedCommand(38), ac, layout);
        phase.OnTick(ctx);

        Assert.Equal(38, ac.Ground.CommandedTaxiSpeedKts);
        Assert.True(phase.NavMaxSpeedKts > JetTaxiDefault, $"cap {phase.NavMaxSpeedKts} should exceed default {JetTaxiDefault}");
    }

    [Fact]
    public void Spd_BelowMinimum_ClampsToFloor()
    {
        var layout = BuildStraightLayout();
        var (ac, _, _) = MakeTaxiing(layout);

        Dispatch(new SpeedCommand(3), ac, layout);

        Assert.Equal(MinCap(), ac.Ground.CommandedTaxiSpeedKts);
    }

    [Fact]
    public void Spd_AboveMaximum_ClampsToExpediteCeiling()
    {
        var layout = BuildStraightLayout();
        var (ac, _, _) = MakeTaxiing(layout);

        Dispatch(new SpeedCommand(100), ac, layout);

        Assert.Equal(JetMax(), ac.Ground.CommandedTaxiSpeedKts);
    }

    [Fact]
    public void Spd0_ResumesNormalTaxiSpeed()
    {
        var layout = BuildStraightLayout();
        var (ac, phase, ctx) = MakeTaxiing(layout);

        Dispatch(new SpeedCommand(10), ac, layout);
        Assert.Equal(10, ac.Ground.CommandedTaxiSpeedKts);

        var result = Dispatch(new ResumeNormalSpeedCommand(), ac, layout);
        phase.OnTick(ctx);

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Ground.CommandedTaxiSpeedKts);
        Assert.Equal(JetTaxiDefault, phase.NavMaxSpeedKts);
    }

    [Fact]
    public void Spd_ClearsExpediteTaxi()
    {
        var layout = BuildStraightLayout();
        var (ac, _, _) = MakeTaxiing(layout);
        ac.Ground.IsExpeditingTaxi = true;

        Dispatch(new SpeedCommand(10), ac, layout);

        Assert.Equal(10, ac.Ground.CommandedTaxiSpeedKts);
        Assert.False(ac.Ground.IsExpeditingTaxi);
    }

    [Fact]
    public void ExpediteTaxi_ClearsCommandedSpeed()
    {
        var layout = BuildStraightLayout();
        var (ac, _, _) = MakeTaxiing(layout);
        ac.Ground.CommandedTaxiSpeedKts = 10;

        var result = Dispatch(new ExpediteCommand(), ac, layout);

        Assert.True(result.Success, result.Message);
        Assert.True(ac.Ground.IsExpeditingTaxi);
        Assert.Null(ac.Ground.CommandedTaxiSpeedKts);
    }

    [Fact]
    public void CommandedSpeed_PersistsAcrossHoldAndResume()
    {
        var layout = BuildStraightLayout();
        var (ac, _, _) = MakeTaxiing(layout);

        Dispatch(new SpeedCommand(10), ac, layout);
        Dispatch(new HoldPositionCommand(), ac, layout);
        Assert.Equal(10, ac.Ground.CommandedTaxiSpeedKts);

        Dispatch(new ResumeCommand([], []), ac, layout);

        Assert.Equal(10, ac.Ground.CommandedTaxiSpeedKts);
    }

    [Fact]
    public void NewTaxi_ClearsCommandedSpeed()
    {
        var layout = GroundCommandHandlerTestLayout();
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = layout.Nodes[1].Position,
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
        };
        ac.Ground.CommandedTaxiSpeedKts = 10;

        var result = GroundCommandHandler.TryTaxi(ac, new TaxiCommand(["A"], [], DestinationRunway: "28R"), layout);

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Ground.CommandedTaxiSpeedKts);
    }

    [Fact]
    public void Snapshot_RoundTripsCommandedSpeed()
    {
        var ground = new AircraftGroundOps { CommandedTaxiSpeedKts = 12 };

        var restored = AircraftGroundOps.FromSnapshot(ground.ToSnapshot(), layout: null);

        Assert.Equal(12, restored.CommandedTaxiSpeedKts);
    }

    [Fact]
    public void TaxiingAircraft_SlowsToCommandedSpeed_OnStraight()
    {
        var layout = BuildStraightLayout();
        var (ac, phase, ctx) = MakeTaxiing(layout);

        Dispatch(new SpeedCommand(10), ac, layout);

        // Tick the phase + physics along the straight; the aircraft accelerates from 0
        // toward the 10-kt cap (3 kt/s), reaching it in ~4 s and never exceeding it.
        for (int i = 0; i < 15; i++)
        {
            phase.OnTick(ctx);
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
        }

        Assert.True(ac.IndicatedAirspeed > 0, "aircraft should be moving");
        Assert.True(ac.IndicatedAirspeed <= 11, $"IAS {ac.IndicatedAirspeed} should not exceed the 10-kt cap");
        Assert.True(ac.IndicatedAirspeed < JetTaxiDefault, $"IAS {ac.IndicatedAirspeed} should be well below the {JetTaxiDefault}-kt default");
    }

    /// <summary>Routable layout for TryTaxi: nodes 1-2 on taxiway A, node 3 is the 28R hold short.</summary>
    private static AirportGroundLayout GroundCommandHandlerTestLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        layout.Nodes[1] = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.728, -122.218),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[2] = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.729, -122.218),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[3] = new GroundNode
        {
            Id = 3,
            Position = new LatLon(37.730, -122.218),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = new RunwayIdentifier("28R"),
        };

        var edge12 = new GroundEdge
        {
            Nodes = [layout.Nodes[1], layout.Nodes[2]],
            TaxiwayName = "A",
            DistanceNm = GeoMath.DistanceNm(layout.Nodes[1].Position, layout.Nodes[2].Position),
        };
        var edge23 = new GroundEdge
        {
            Nodes = [layout.Nodes[2], layout.Nodes[3]],
            TaxiwayName = "A",
            DistanceNm = GeoMath.DistanceNm(layout.Nodes[2].Position, layout.Nodes[3].Position),
        };
        layout.Edges.Add(edge12);
        layout.Edges.Add(edge23);
        layout.Nodes[1].Edges.Add(edge12);
        layout.Nodes[2].Edges.Add(edge12);
        layout.Nodes[2].Edges.Add(edge23);
        layout.Nodes[3].Edges.Add(edge23);
        layout.RebuildAdjacencyLists();
        return layout;
    }
}
