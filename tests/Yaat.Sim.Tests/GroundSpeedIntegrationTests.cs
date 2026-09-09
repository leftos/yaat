using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests;

/// <summary>
/// Physics owns ground speed: there is exactly one integrator. Ground phases and
/// <see cref="GroundNavigator"/> publish <c>Targets.TargetSpeed</c> (and <c>DesiredDecelRate</c> when they
/// want a specific braking rate); <see cref="FlightPhysics"/> closes the gap at the category's TAXI rates
/// while <c>IsOnGround</c>, and at the type's flight-envelope rates once airborne.
/// </summary>
public sealed class GroundSpeedIntegrationTests
{
    /// <summary>The shipping sim runs four physics sub-ticks per second.</summary>
    private const double SubTick = 0.25;

    public GroundSpeedIntegrationTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeB738(bool onGround)
    {
        return new AircraftState
        {
            Callsign = "TEST001",
            AircraftType = "B738",
            Position = new LatLon(37.620, -122.380),
            TrueHeading = new TrueHeading(0),
            TrueTrack = new TrueHeading(0),
            Altitude = onGround ? 0 : 3000,
            IsOnGround = onGround,
        };
    }

    private static void TickOneSecond(AircraftState aircraft)
    {
        for (int k = 1; k <= 4; k++)
        {
            FlightPhysics.Update(aircraft, SubTick, null, null, simTimeSeconds: k * SubTick);
        }
    }

    /// <summary>
    /// On the ground the rate is the category's breakaway-thrust taxi rate (1.0 kt/s for a jet), not the
    /// airborne profile rate (2.0 kt/s for a B738) — the same aircraft accelerating airborne proves the split.
    /// </summary>
    [Fact]
    public void UpdateSpeed_OnGround_UsesTaxiAccelRate()
    {
        var ground = MakeB738(onGround: true);
        ground.IndicatedAirspeed = 0;
        ground.Targets.TargetSpeed = 30;

        TickOneSecond(ground);

        Assert.Equal(1.0, CategoryPerformance.TaxiAccelRate(AircraftCategory.Jet), 1e-9);
        Assert.Equal(1.0, ground.IndicatedAirspeed, 1e-9);

        var airborne = MakeB738(onGround: false);
        airborne.IndicatedAirspeed = 200;
        airborne.Targets.TargetSpeed = 250;

        TickOneSecond(airborne);

        Assert.Equal(202.0, airborne.IndicatedAirspeed, 1e-9);
    }

    /// <summary>
    /// Braking on the ground runs at the category's taxi decel rate (5 kt/s for a jet) unless a phase
    /// published a rate of its own — an expedited exit or a firm rollout stop — which physics then honors.
    /// </summary>
    [Fact]
    public void UpdateSpeed_OnGround_UsesTaxiDecelRate_UnlessDesiredDecelRateSet()
    {
        var aircraft = MakeB738(onGround: true);
        aircraft.IndicatedAirspeed = 30;
        aircraft.Targets.TargetSpeed = 0;

        TickOneSecond(aircraft);

        Assert.Equal(5.0, CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet), 1e-9);
        Assert.Equal(25.0, aircraft.IndicatedAirspeed, 1e-9);

        var overridden = MakeB738(onGround: true);
        overridden.IndicatedAirspeed = 30;
        overridden.Targets.TargetSpeed = 0;
        overridden.Targets.DesiredDecelRate = 3.0;

        TickOneSecond(overridden);

        Assert.Equal(27.0, overridden.IndicatedAirspeed, 1e-9);
    }

    /// <summary>
    /// The snap window on the ground is one sub-tick of change. Physics may close the last sliver of a gap
    /// in a single step, but never more than the <c>rate x deltaSeconds</c> it would have integrated anyway:
    /// a fixed 2 kt window is two whole seconds of jet taxi acceleration delivered as a jump.
    /// </summary>
    [Fact]
    public void UpdateSpeed_OnGround_NeverJumpsMoreThanOneSubTick()
    {
        var aircraft = MakeB738(onGround: true);
        aircraft.IndicatedAirspeed = 0;

        double oneSubTickOfAccel = CategoryPerformance.TaxiAccelRate(AircraftCategory.Jet) * SubTick;
        Assert.Equal(0.25, oneSubTickOfAccel, 1e-9);

        // 140 sub-ticks = 35 s: long enough for the 1.0 kt/s taxi accel to close on the 30 kt target,
        // which is where a fixed 2 kt snap window fires.
        double iasAtTenSeconds = double.NaN;
        for (int k = 1; k <= 140; k++)
        {
            // The ground phases re-publish their target every tick; physics nulls it on the snap.
            aircraft.Targets.TargetSpeed = 30;
            double before = aircraft.IndicatedAirspeed;

            FlightPhysics.Update(aircraft, SubTick, null, null, simTimeSeconds: k * SubTick);

            double delta = aircraft.IndicatedAirspeed - before;
            Assert.True(
                Math.Abs(delta) <= oneSubTickOfAccel + 1e-9,
                $"sub-tick {k}: IAS jumped {delta:F4} kt ({before:F4} -> {aircraft.IndicatedAirspeed:F4}); one sub-tick of taxi accel is {oneSubTickOfAccel:F4} kt"
            );

            if (k == 40)
            {
                iasAtTenSeconds = aircraft.IndicatedAirspeed;
            }
        }

        Assert.Equal(10.0, iasAtTenSeconds, 1e-9);
        Assert.Equal(30.0, aircraft.IndicatedAirspeed, 1e-9);
    }

    /// <summary>
    /// Airborne the snap window is unchanged: the fixed 2 kt rounding nicety, so a B738 at 249 kt with a
    /// 250 kt target lands exactly on 250 in one sub-tick and the transient target self-nulls.
    /// </summary>
    [Fact]
    public void UpdateSpeed_Airborne_SnapsInsideTwoKts()
    {
        var aircraft = MakeB738(onGround: false);
        aircraft.IndicatedAirspeed = 249;
        aircraft.Targets.TargetSpeed = 250;

        FlightPhysics.Update(aircraft, SubTick, null, null, simTimeSeconds: SubTick);

        Assert.Equal(250.0, aircraft.IndicatedAirspeed, 1e-9);
        Assert.Null(aircraft.Targets.TargetSpeed);
    }

    /// <summary>
    /// A held taxiing aircraft decelerates once, at the taxi decel rate: the phase pins TargetSpeed to 0 and
    /// physics brakes toward it. It used to also decrement IAS itself on top of physics, so a held jet shed
    /// 10 kt/s (two integrators) instead of 5.
    /// </summary>
    [Fact]
    public void TaxiingPhase_WhenHeld_DeceleratesAtTaxiDecelRateOnly()
    {
        var layout = BuildLayout();
        var aircraft = new AircraftState
        {
            Callsign = "TEST002",
            AircraftType = "B738",
            Position = new LatLon(37.620, -122.380),
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KSFO" },
        };
        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[0].Directed(layout.Nodes[0], layout.Nodes[1]) }],
            HoldShortPoints = [],
        };
        aircraft.Ground.AssignedTaxiRoute = route;

        var phase = new TaxiingPhase();
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = SubTick,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };
        aircraft.Phases.Start(ctx);

        aircraft.IndicatedAirspeed = 20;
        aircraft.Ground.Hold = HoldDirective.HoldPosition;

        for (int k = 1; k <= 4; k++)
        {
            PhaseRunner.Tick(aircraft, ctx);
            FlightPhysics.Update(aircraft, SubTick, null, null, simTimeSeconds: k * SubTick);
        }

        Assert.Equal(15.0, aircraft.IndicatedAirspeed, 1e-9);
    }

    /// <summary>Node0 --[A]--> Node1, the minimum a <see cref="TaxiingPhase"/> needs to initialize.</summary>
    private static AirportGroundLayout BuildLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "KSFO" };

        var node0 = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.620, -122.380),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.621, -122.380),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        var edge01 = new GroundEdge
        {
            Nodes = [node0, node1],
            TaxiwayName = "A",
            DistanceNm = 0.06,
        };

        node0.Edges.Add(edge01);
        node1.Edges.Add(edge01);

        layout.Nodes[0] = node0;
        layout.Nodes[1] = node1;
        layout.Edges.Add(edge01);
        layout.RebuildAdjacencyLists();

        return layout;
    }
}
