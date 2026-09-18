using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

public class GroundPhaseTests
{
    /// <summary>
    /// Builds a minimal crossing layout: Node0 --[A]--> Node1 (HS 28L/10R) --[RWY28L]--> Node2 (HS 28L/10R) --[A]--> Node3
    /// </summary>
    private static AirportGroundLayout BuildCrossingLayout()
    {
        var rwyId = RunwayIdentifier.Parse("28L/10R");
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
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rwyId,
        };
        var node2 = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.622, -122.380),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rwyId,
        };
        var node3 = new GroundNode
        {
            Id = 3,
            Position = new LatLon(37.623, -122.380),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        var edge01 = new GroundEdge
        {
            Nodes = [node0, node1],
            TaxiwayName = "A",
            DistanceNm = 0.06,
        };
        var edge12 = new GroundEdge
        {
            Nodes = [node1, node2],
            TaxiwayName = "RWY28L",
            DistanceNm = 0.06,
        };
        var edge23 = new GroundEdge
        {
            Nodes = [node2, node3],
            TaxiwayName = "A",
            DistanceNm = 0.06,
        };

        node0.Edges.Add(edge01);
        node1.Edges.AddRange([edge01, edge12]);
        node2.Edges.AddRange([edge12, edge23]);
        node3.Edges.Add(edge23);

        layout.Nodes[0] = node0;
        layout.Nodes[1] = node1;
        layout.Nodes[2] = node2;
        layout.Nodes[3] = node3;
        layout.Edges.AddRange([edge01, edge12, edge23]);
        layout.RebuildAdjacencyLists();

        return layout;
    }

    private static AircraftState MakeGroundAircraft(double lat = 37.620, double lon = -122.380, double heading = 0)
    {
        return new AircraftState
        {
            Callsign = "TEST001",
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(heading),
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KSFO" },
        };
    }

    private static PhaseContext MakeContext(AircraftState aircraft, AirportGroundLayout? layout = null, Func<string, AircraftState?>? lookup = null)
    {
        return new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            GroundLayout = layout,
            AircraftLookup = lookup,
            Logger = NullLogger.Instance,
        };
    }

    public GroundPhaseTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>A plain pushback: a straight push of the type's simple pushback distance, off no stand.</summary>
    private static PushbackPhase SimplePush(AircraftState aircraft) =>
        PushOf(aircraft, TugMove.Straight(PushbackLegKind.Push, TugMovePlanner.SimplePushbackFt(aircraft.AircraftType)));

    private static PushbackPhase PushOf(AircraftState aircraft, TugMove move) =>
        new()
        {
            Move = move,
            PlannedEnd = TugKinematics
                .Simulate(new TugPose(aircraft.Position, aircraft.TrueHeading.Degrees), [move], aircraft.AircraftType, 1.0)
                .End.Position,
            ContinuesIntoNextMove = false,
            ContinuesStandPushOff = false,
        };

    private static (PushbackPhase Phase, PhaseContext Ctx) StartPush(AircraftState aircraft, TugMove move)
    {
        PushbackPhase phase = PushOf(aircraft, move);
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        PhaseContext ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);
        return (phase, ctx);
    }

    /// <summary>
    /// A started move off no stand, with the leg-1 flag set as given: a move flown through from the stand push-off, or
    /// one the plan reversed into.
    /// </summary>
    private static (PushbackPhase Phase, PhaseContext Ctx) StartMoveOffTheStand(AircraftState aircraft, TugMove move, bool continuesStandPushOff)
    {
        var phase = new PushbackPhase
        {
            Move = move,
            PlannedEnd = TugKinematics
                .Simulate(new TugPose(aircraft.Position, aircraft.TrueHeading.Degrees), [move], aircraft.AircraftType, 1.0)
                .End.Position,
            ContinuesIntoNextMove = false,
            ContinuesStandPushOff = continuesStandPushOff,
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        PhaseContext ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);
        return (phase, ctx);
    }

    /// <summary>
    /// A started move off no stand with the flow-through flag set as given: one the plan follows with another move, or
    /// one that ends at a standstill.
    /// </summary>
    private static (PushbackPhase Phase, PhaseContext Ctx) StartMoveContinuing(AircraftState aircraft, TugMove move, bool continuesIntoNextMove)
    {
        var phase = new PushbackPhase
        {
            Move = move,
            PlannedEnd = TugKinematics
                .Simulate(new TugPose(aircraft.Position, aircraft.TrueHeading.Degrees), [move], aircraft.AircraftType, 1.0)
                .End.Position,
            ContinuesIntoNextMove = continuesIntoNextMove,
            ContinuesStandPushOff = false,
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        PhaseContext ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);
        return (phase, ctx);
    }

    /// <summary>The type's FAA wheelbase, the bicycle model's axle spacing; every type these tests tow carries one.</summary>
    private static double WheelbaseFt(string aircraftType) => FaaAircraftDatabase.Get(aircraftType)!.WheelbaseFt!.Value;

    /// <summary>
    /// The ticks whose step steered the direction of travel at the full rate the radius allows — the steady part of a
    /// turn, where the nose gear sits at atan(L / R). Leaves out the accelerating steps (which move farther than the
    /// step they were steered for) and the rate-limited last one.
    /// </summary>
    private static List<PushTick> FullRateTurns(List<PushTick> ticks, double radiusFt) =>
        [.. ticks.Where(t => (t.MovedFt > 0.1) && (t.NoseTurnDeg >= (0.98 * (t.MovedFt / radiusFt) * (180.0 / Math.PI))))];

    /// <summary>
    /// One engine-order second: what the aircraft moved, how far the nose turned, how far the push heading is off the
    /// nose's reciprocal (null on a pull), and where the towbar points relative to the nose — signed, positive to the
    /// right, null when no tug is attached.
    /// </summary>
    private readonly record struct PushTick(double MovedFt, double NoseTurnDeg, double? PushGapDeg, double? TowbarOffsetDeg);

    /// <summary>Ticks the phase then physics, one second at a time, until the phase completes or the budget runs out.</summary>
    private static (bool Completed, List<PushTick> Ticks) RunPush(AircraftState aircraft, PushbackPhase phase, PhaseContext ctx, int maxTicks)
    {
        var ticks = new List<PushTick>();
        for (int i = 0; i < maxTicks; i++)
        {
            LatLon from = aircraft.Position;
            TrueHeading nose = aircraft.TrueHeading;
            if (phase.OnTick(ctx))
            {
                return (true, ticks);
            }

            FlightPhysics.Update(aircraft, 1.0);
            double? gap = aircraft.Ground.PushbackTrueHeading is { } push ? push.ToReciprocal().AbsAngleTo(aircraft.TrueHeading) : null;
            double? towbar = aircraft.Ground.TowbarTrueHeading is { } bar ? aircraft.TrueHeading.SignedAngleTo(bar) : null;
            ticks.Add(
                new PushTick(GeoMath.DistanceNm(from, aircraft.Position) * GeoMath.FeetPerNm, nose.AbsAngleTo(aircraft.TrueHeading), gap, towbar)
            );
        }

        return (false, ticks);
    }

    private static void AssertWithinCurvature(PushTick tick, double radiusFt)
    {
        double turnRad = tick.NoseTurnDeg * Math.PI / 180.0;
        double boundRad = ((tick.MovedFt / radiusFt) * 1.05) + 0.002;
        Assert.True(turnRad <= boundRad, $"the nose turned {tick.NoseTurnDeg:F2}° over {tick.MovedFt:F2} ft, past 1/R = 1/{radiusFt:F1} ft");
    }

    /// <summary>Flies a turn from a standstill heading north and returns the total nose turn per foot moved, radians.</summary>
    private static double TurnPerFoot(TugMove move)
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 0);
        (PushbackPhase? phase, PhaseContext? ctx) = StartPush(aircraft, move);
        (bool Completed, List<PushTick> Ticks) run = RunPush(aircraft, phase, ctx, 20);
        var turning = run.Ticks.Skip(2).ToList();
        return (turning.Sum(t => t.NoseTurnDeg) * Math.PI / 180.0) / turning.Sum(t => t.MovedFt);
    }

    // --- FIX 1: TryHoldPosition uses IsOnGround ---

    [Fact]
    public void TryHoldPosition_OnGround_SetsHeld()
    {
        AircraftState aircraft = MakeGroundAircraft();
        aircraft.IsOnGround = true;

        CommandResult result = GroundCommandHandler.TryHoldPosition(aircraft);

        Assert.True(result.Success);
        Assert.True(aircraft.Ground.IsImmobile);
    }

    [Fact]
    public void TryHoldPosition_Airborne_Fails()
    {
        AircraftState aircraft = MakeGroundAircraft();
        aircraft.IsOnGround = false;

        CommandResult result = GroundCommandHandler.TryHoldPosition(aircraft);

        Assert.False(result.Success);
        Assert.False(aircraft.Ground.IsImmobile);
    }

    [Fact]
    public void TryHoldPosition_FollowingPhase_Succeeds()
    {
        AircraftState aircraft = MakeGroundAircraft();
        aircraft.IsOnGround = true;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new FollowingPhase("LEAD01"));
        aircraft.Phases.Start(MakeContext(aircraft));

        CommandResult result = GroundCommandHandler.TryHoldPosition(aircraft);

        Assert.True(result.Success);
        Assert.True(aircraft.Ground.IsImmobile);
    }

    // --- FIX 2: PushbackPhase respects IsHeld ---

    [Fact]
    public void PushbackPhase_WhenHeld_StopsMoving()
    {
        AircraftState aircraft = MakeGroundAircraft();
        aircraft.Phases = new PhaseList();
        PushbackPhase phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        PhaseContext ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        // PushbackTrueHeading should be set (opposite of aircraft heading)
        Assert.NotNull(aircraft.Ground.PushbackTrueHeading);

        // Tick once — phase sets TargetSpeed, FlightPhysics moves
        FlightPhysics.Update(aircraft, 1.0);
        phase.OnTick(ctx);
        Assert.True(aircraft.GroundSpeed > 0);

        // Hold and tick again: the tug brakes the tow at the towbar rate rather than freezing it where it stands,
        // so the aircraft is still rolling the instant the hold lands and comes to rest over the seconds after.
        aircraft.Ground.Hold = HoldDirective.HoldPosition;
        phase.OnTick(ctx);
        Assert.True(aircraft.GroundSpeed > 0, "the hold stopped the tug dead instead of braking it to a stop");

        for (int i = 0; i < 5; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            phase.OnTick(ctx);
        }

        Assert.Equal(0, aircraft.GroundSpeed);
    }

    [Fact]
    public void PushbackPhase_WhenResumed_ContinuesMoving()
    {
        AircraftState aircraft = MakeGroundAircraft();
        aircraft.Phases = new PhaseList();
        PushbackPhase phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        PhaseContext ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        // Let physics accelerate once
        FlightPhysics.Update(aircraft, 1.0);

        // Hold, and let the tow brake to rest at the towbar rate
        aircraft.Ground.Hold = HoldDirective.HoldPosition;
        for (int i = 0; i < 5; i++)
        {
            phase.OnTick(ctx);
            FlightPhysics.Update(aircraft, 1.0);
        }

        Assert.Equal(0, aircraft.GroundSpeed);

        // Resume — phase OnTick reasserts TargetSpeed automatically
        aircraft.Ground.Hold = null;
        for (int i = 0; i < 3; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            phase.OnTick(ctx);
        }

        Assert.True(aircraft.GroundSpeed > 0);
    }

    [Fact]
    public void PushbackPhase_HeldOverTheEndOfTheMove_StillCompletes()
    {
        // A hold that lands inside the braking distance still brakes the tow over the last feet of the move, and
        // those feet belong to the move: once the distance is covered the move is over, and the phase has to hand
        // over instead of sitting on a finished move until RES lifts the hold.
        AircraftState aircraft = MakeGroundAircraft(heading: 0);
        var move = TugMove.Straight(PushbackLegKind.Push, TugMovePlanner.SimplePushbackFt(aircraft.AircraftType));
        (PushbackPhase? phase, PhaseContext? ctx) = StartPush(aircraft, move);

        LatLon start = aircraft.Position;
        double movedFt = 0;
        bool completedEarly = false;
        for (int i = 0; (i < 120) && (movedFt < move.StraightDistanceFt) && !completedEarly; i++)
        {
            completedEarly = phase.OnTick(ctx);
            FlightPhysics.Update(aircraft, 1.0);
            movedFt = GeoMath.DistanceNm(start, aircraft.Position) * GeoMath.FeetPerNm;
        }

        Assert.False(completedEarly, "the move completed before the hold could land on its last step");
        Assert.True(movedFt >= move.StraightDistanceFt, $"the push stopped {move.StraightDistanceFt - movedFt:F1} ft short of its end");

        // The hold lands on the step that carried the tow through the planned end.
        aircraft.Ground.Hold = HoldDirective.HoldPosition;
        bool completed = false;
        for (int i = 0; (i < 10) && !completed; i++)
        {
            completed = phase.OnTick(ctx);
            FlightPhysics.Update(aircraft, 1.0);
        }

        Assert.True(completed, "a hold over the end of the move left the finished push running until RES");
        Assert.Equal(0, aircraft.GroundSpeed);
    }

    // --- FIX 3: Hold-short → taxi resume ---

    [Fact]
    public void TaxiingPhase_RunwayCrossing_InsertsHoldCrossingResume()
    {
        AirportGroundLayout layout = BuildCrossingLayout();
        AircraftState aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

        var route = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[0].Directed(layout.Nodes[0], layout.Nodes[1]) },
                new TaxiRouteSegment { TaxiwayName = "RWY28L", Edge = layout.Edges[1].Directed(layout.Nodes[1], layout.Nodes[2]) },
                new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[2].Directed(layout.Nodes[2], layout.Nodes[3]) },
            ],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28L/10R",
                },
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28L/10R",
                },
            ],
        };
        aircraft.Ground.AssignedTaxiRoute = route;

        aircraft.Phases = new PhaseList();
        var taxiPhase = new TaxiingPhase();
        aircraft.Phases.Add(taxiPhase);
        PhaseContext ctx = MakeContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        // Simulate arriving at node 1 (hold-short) by placing aircraft there
        aircraft.Position = layout.Nodes[1].Position;

        // Tick until the phase completes (arrives at hold-short)
        bool completed = false;
        for (int i = 0; i < 300; i++)
        {
            if (taxiPhase.OnTick(ctx))
            {
                completed = true;
                break;
            }
        }

        Assert.True(completed);

        // Verify inserted phases: HoldingShortPhase, CrossingRunwayPhase, TaxiingPhase
        List<Phase> phases = aircraft.Phases.Phases;
        Assert.True(phases.Count >= 4, $"Expected at least 4 phases, got {phases.Count}");
        Assert.IsType<TaxiingPhase>(phases[0]);
        Assert.IsType<HoldingShortPhase>(phases[1]);
        Assert.IsType<CrossingRunwayPhase>(phases[2]);
        Assert.IsType<TaxiingPhase>(phases[3]);
    }

    [Fact]
    public void TaxiingPhase_ExplicitHoldShort_InsertsHoldAndResume()
    {
        AirportGroundLayout layout = BuildCrossingLayout();
        AircraftState aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

        var route = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[0].Directed(layout.Nodes[0], layout.Nodes[1]) },
                new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[1].Directed(layout.Nodes[1], layout.Nodes[2]) },
                new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[2].Directed(layout.Nodes[2], layout.Nodes[3]) },
            ],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "B",
                },
            ],
        };
        aircraft.Ground.AssignedTaxiRoute = route;

        aircraft.Phases = new PhaseList();
        var taxiPhase = new TaxiingPhase();
        aircraft.Phases.Add(taxiPhase);
        PhaseContext ctx = MakeContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        // Place at hold-short node
        aircraft.Position = layout.Nodes[1].Position;

        bool completed = false;
        for (int i = 0; i < 300; i++)
        {
            if (taxiPhase.OnTick(ctx))
            {
                completed = true;
                break;
            }
        }

        Assert.True(completed);

        // ExplicitHoldShort at a RunwayHoldShort node (HoldShortAnnotator promotes the
        // entry-side runway HS when "HS <rwy>" is in the command) is treated like a
        // RunwayCrossing on resume: HoldingShort → CrossingRunwayPhase → TaxiingPhase.
        // Without this, the aircraft would just taxi across at 15 kt taxi speed.
        List<Phase> phases = aircraft.Phases.Phases;
        Assert.True(phases.Count >= 4, $"Expected at least 4 phases (Taxi + Hold + Cross + Taxi), got {phases.Count}");
        Assert.IsType<TaxiingPhase>(phases[0]);
        Assert.IsType<HoldingShortPhase>(phases[1]);
        Assert.IsType<CrossingRunwayPhase>(phases[2]);
        Assert.IsType<TaxiingPhase>(phases[3]);
    }

    [Fact]
    public void TaxiingPhase_DestinationRunway_InsertsHoldOnly()
    {
        AirportGroundLayout layout = BuildCrossingLayout();
        AircraftState aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

        // Route ends at hold-short node 1 (destination runway)
        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[0].Directed(layout.Nodes[0], layout.Nodes[1]) }],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "28L",
                },
            ],
        };
        aircraft.Ground.AssignedTaxiRoute = route;

        aircraft.Phases = new PhaseList();
        var taxiPhase = new TaxiingPhase();
        aircraft.Phases.Add(taxiPhase);
        PhaseContext ctx = MakeContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        // Place at hold-short node
        aircraft.Position = layout.Nodes[1].Position;

        bool completed = false;
        for (int i = 0; i < 300; i++)
        {
            if (taxiPhase.OnTick(ctx))
            {
                completed = true;
                break;
            }
        }

        Assert.True(completed);

        // HoldingShortPhase + HoldingInPositionPhase (no departure clearance)
        List<Phase> phases = aircraft.Phases.Phases;
        Assert.Equal(3, phases.Count);
        Assert.IsType<TaxiingPhase>(phases[0]);
        Assert.IsType<HoldingShortPhase>(phases[1]);
        Assert.IsType<HoldingInPositionPhase>(phases[2]);
    }

    // --- FIX 4: FollowingPhase hold-short awareness ---

    [Fact]
    public void FollowingPhase_ApproachingHoldShort_AutoHolds()
    {
        AirportGroundLayout layout = BuildCrossingLayout();
        AircraftState target = MakeGroundAircraft(37.623, -122.380, heading: 0);
        target.Callsign = "LEAD01";

        // Place follower just before the hold-short node (heading toward it)
        AircraftState aircraft = MakeGroundAircraft(37.6208, -122.380, heading: 0);
        aircraft.Phases = new PhaseList();
        var followPhase = new FollowingPhase("LEAD01");
        aircraft.Phases.Add(followPhase);
        PhaseContext ctx = MakeContext(aircraft, layout, cs => cs == "LEAD01" ? target : null);
        aircraft.Phases.Start(ctx);

        bool completed = false;
        for (int i = 0; i < 50; i++)
        {
            if (followPhase.OnTick(ctx))
            {
                completed = true;
                break;
            }

            // Physics owns ground speed: the phase publishes the follow target and physics integrates it.
            // The hold-short check only arms on a moving aircraft, so the tick loop has to run both halves.
            FlightPhysics.Update(aircraft, 1.0, cs => cs == "LEAD01" ? target : null, null, simTimeSeconds: i);
        }

        Assert.True(completed, "FollowingPhase should complete when hold-short is detected");

        // Verify inserted phases: HoldingShortPhase + new FollowingPhase
        List<Phase> phases = aircraft.Phases.Phases;
        Assert.True(phases.Count >= 3, $"Expected at least 3 phases, got {phases.Count}");
        Assert.IsType<FollowingPhase>(phases[0]);
        Assert.IsType<HoldingShortPhase>(phases[1]);
        Assert.IsType<FollowingPhase>(phases[2]);
    }

    [Fact]
    public void FollowingPhase_HeadingAway_DoesNotHold()
    {
        AirportGroundLayout layout = BuildCrossingLayout();
        AircraftState target = MakeGroundAircraft(37.619, -122.380, heading: 180);
        target.Callsign = "LEAD01";

        // Place follower near node 1 but heading AWAY from it (south)
        AircraftState aircraft = MakeGroundAircraft(37.6208, -122.380, heading: 180);
        aircraft.Phases = new PhaseList();
        var followPhase = new FollowingPhase("LEAD01");
        aircraft.Phases.Add(followPhase);
        PhaseContext ctx = MakeContext(aircraft, layout, cs => cs == "LEAD01" ? target : null);
        aircraft.Phases.Start(ctx);

        // Should not complete due to hold-short detection
        bool completed = followPhase.OnTick(ctx);
        Assert.False(completed, "FollowingPhase should not trigger hold-short when heading away");

        // No inserted phases
        Assert.Single(aircraft.Phases.Phases);
    }

    [Fact]
    public void FollowingPhase_NoLayout_SkipsCheck()
    {
        AircraftState target = MakeGroundAircraft(37.623, -122.380, heading: 0);
        target.Callsign = "LEAD01";

        AircraftState aircraft = MakeGroundAircraft(37.6208, -122.380, heading: 0);
        aircraft.Phases = new PhaseList();
        var followPhase = new FollowingPhase("LEAD01");
        aircraft.Phases.Add(followPhase);
        // No ground layout
        PhaseContext ctx = MakeContext(aircraft, null, cs => cs == "LEAD01" ? target : null);
        aircraft.Phases.Start(ctx);

        bool completed = followPhase.OnTick(ctx);
        Assert.False(completed, "FollowingPhase should continue normally without a layout");

        // No inserted phases
        Assert.Single(aircraft.Phases.Phases);
    }

    /// <summary>
    /// A follower that catches a stopped leader closes up to the stop gap rather than freezing at the
    /// follow-distance boundary. Matching the leader's speed verbatim inside FollowDistanceNm publishes
    /// TargetSpeed = 0 behind a parked lead, and physics — the only integrator of ground speed — holds the
    /// follower wherever it happened to be, up to ~180 ft short. Real traffic rolls up to the stop gap.
    /// </summary>
    [Fact]
    public void FollowingPhase_BehindStoppedLead_ClosesUpToStopDistance()
    {
        // Lead parked, engines running, not moving.
        AircraftState lead = MakeGroundAircraft(37.623, -122.380, heading: 0);
        lead.Callsign = "LEAD01";
        lead.IndicatedAirspeed = 0;

        // Follower directly behind the lead, just inside the follow distance, still rolling at 5 kt.
        double startGapNm = FollowingPhase.FollowDistanceNm - 0.002;
        AircraftState follower = MakeGroundAircraft(37.623 - (startGapNm / 60.0), -122.380, heading: 0);
        follower.IndicatedAirspeed = 5;
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase("LEAD01"));

        Func<string, AircraftState?> lookup = cs => cs == "LEAD01" ? lead : null;
        var ctx = new PhaseContext
        {
            Aircraft = follower,
            Targets = follower.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.25,
            GroundLayout = null,
            AircraftLookup = lookup,
            Logger = NullLogger.Instance,
        };
        follower.Phases.Start(ctx);

        // 60 s at the sub-tick rate physics runs at.
        bool everStopped = false;
        double speedAfterFirstStop = 0;
        for (int i = 0; i < 240; i++)
        {
            PhaseRunner.Tick(follower, ctx);
            FlightPhysics.Update(follower, ctx.DeltaSeconds, lookup, null, simTimeSeconds: i * ctx.DeltaSeconds);

            if (follower.GroundSpeed <= 0)
            {
                everStopped = true;
            }
            else if (everStopped)
            {
                speedAfterFirstStop = Math.Max(speedAfterFirstStop, follower.GroundSpeed);
            }
        }

        double gapNm = GeoMath.DistanceNm(follower.Position, lead.Position);

        // Ten feet of slack over the stop gap covers the brake-out from the close-up speed.
        double allowedGapNm = FollowingPhase.StopDistanceNm + (10.0 / GeoMath.FeetPerNm);
        Assert.True(
            gapNm <= allowedGapNm,
            $"follower settled {gapNm * GeoMath.FeetPerNm:F0} ft behind the stopped lead, expected at most {allowedGapNm * GeoMath.FeetPerNm:F0} ft"
        );
        Assert.Equal(0, follower.GroundSpeed);

        // No chatter: once it first reaches 0 inside the stop gap it stays there. A flat close-up speed
        // that only zeroes at the StopDistanceNm branch creeps forward, trips the branch, brakes, drifts
        // back out and creeps again; the brake curve decays to 0 at the gap so the stop is terminal.
        Assert.True(everStopped, "follower never reached a stop behind the parked lead within 60 s");
        Assert.Equal(0.0, speedAfterFirstStop, 1e-9);
    }

    // --- Pushback speed recovery after conflict ---

    [Fact]
    public void PushbackPhase_SpeedRecoversAfterConflictClears()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 90);
        aircraft.Phases = new PhaseList();
        PushbackPhase phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        PhaseContext ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        // Let physics ramp up to pushback speed
        for (int i = 0; i < 5; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            phase.OnTick(ctx);
        }

        Assert.True(aircraft.GroundSpeed > 0, "Should be moving before conflict");

        // Simulate conflict: GroundSpeedLimit clamps to 0
        aircraft.Ground.SpeedLimit = 0;
        FlightPhysics.Update(aircraft, 1.0);
        phase.OnTick(ctx);
        Assert.Equal(0, aircraft.GroundSpeed);

        // Conflict clears
        aircraft.Ground.SpeedLimit = null;
        for (int i = 0; i < 5; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            phase.OnTick(ctx);
        }

        Assert.True(aircraft.GroundSpeed > 0, "Speed should recover after conflict clears");
    }

    // --- Pushback OnStart clears TargetHeading ---

    [Fact]
    public void PushbackPhase_OnStart_ClearsTargetHeading()
    {
        AircraftState aircraft = MakeGroundAircraft();
        aircraft.Targets.TargetTrueHeading = new TrueHeading(270);
        aircraft.Phases = new PhaseList();
        PushbackPhase phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        PhaseContext ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        Assert.Null(aircraft.Targets.TargetTrueHeading);
    }

    // --- Pushback publishes the towbar rates and takes them away again ---

    /// <summary>
    /// A tug move is integrated at the towbar rates, not the aircraft's own: the phase publishes
    /// <see cref="CategoryPerformance.TugAccelRate"/> and <see cref="CategoryPerformance.TugDecelRate"/> as it
    /// starts, and hands the aircraft back on its category rates however the move ends — the tug is gone, and
    /// whatever taxis next accelerates and brakes on its own terms.
    /// </summary>
    [Fact]
    public void PushbackPhase_PublishesTowbarRates_AndClearsThemOnEveryEnd()
    {
        double accel = CategoryPerformance.TugAccelRate(AircraftCategory.Jet);
        double decel = CategoryPerformance.TugDecelRate(AircraftCategory.Jet);

        AircraftState completing = MakeGroundAircraft();
        (PushbackPhase? completingPhase, PhaseContext? completingCtx) = StartPush(completing, TugMove.Straight(PushbackLegKind.Push, 50.0));

        Assert.Equal(accel, completingCtx.Targets.DesiredAccelRate!.Value, 1e-9);
        Assert.Equal(decel, completingCtx.Targets.DesiredDecelRate!.Value, 1e-9);

        completingPhase.OnEnd(completingCtx, PhaseStatus.Completed);

        Assert.Null(completingCtx.Targets.DesiredAccelRate);
        Assert.Null(completingCtx.Targets.DesiredDecelRate);

        AircraftState skipping = MakeGroundAircraft();
        (PushbackPhase? skippingPhase, PhaseContext? skippingCtx) = StartPush(skipping, TugMove.Straight(PushbackLegKind.Push, 50.0));

        Assert.Equal(accel, skippingCtx.Targets.DesiredAccelRate!.Value, 1e-9);
        Assert.Equal(decel, skippingCtx.Targets.DesiredDecelRate!.Value, 1e-9);

        skippingPhase.OnEnd(skippingCtx, PhaseStatus.Skipped);

        Assert.Null(skippingCtx.Targets.DesiredAccelRate);
        Assert.Null(skippingCtx.Targets.DesiredDecelRate);
    }

    // --- Tug moves: curvature-limited steering ---

    /// <summary>
    /// Pins: a push turning onto a facing starts moving at once, turns the nose only as it moves and never tighter
    /// than the routine radius, keeps the tail leading (push heading = the nose's reciprocal), and ends on the facing.
    /// </summary>
    [Fact]
    public void PushbackPhase_PushTurnTo_TurnsOnlyAsItMovesAndEndsOnTheFacing()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 0);
        (PushbackPhase? phase, PhaseContext? ctx) = StartPush(aircraft, TugMove.TurnTo(PushbackLegKind.Push, 90));
        double radiusFt = TugKinematics.TurnRadiusFt(aircraft.AircraftType, tight: false);

        (bool Completed, List<PushTick> Ticks) run = RunPush(aircraft, phase, ctx, 300);

        Assert.True(run.Completed, "the turn never completed");
        Assert.All(run.Ticks, t => AssertWithinCurvature(t, radiusFt));
        Assert.All(run.Ticks, t => Assert.True(t.PushGapDeg is <= 0.01, $"push heading {t.PushGapDeg:F2}° off the nose's reciprocal"));
        Assert.True(new TrueHeading(90).AbsAngleTo(aircraft.TrueHeading) <= 0.5, $"ended with the nose on {aircraft.TrueHeading.Degrees:F1}°");
    }

    /// <summary>
    /// Pins the tug's pose on a move that steers nothing: a straight push never turns the direction of travel, so the
    /// nose gear stays straight and the towbar lies along the fuselage axis for the whole move.
    /// </summary>
    [Fact]
    public void PushbackPhase_StraightPush_KeepsTheTowbarOnTheNoseAxis()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 90);
        (PushbackPhase? phase, PhaseContext? ctx) = StartPush(aircraft, TugMove.Straight(PushbackLegKind.Push, 50.0));

        (bool Completed, List<PushTick> Ticks) run = RunPush(aircraft, phase, ctx, 300);

        Assert.True(run.Completed, "the straight push never completed");
        Assert.All(
            run.Ticks,
            t => Assert.True((t.TowbarOffsetDeg is { } offset) && (Math.Abs(offset) < 0.5), $"the towbar sat {t.TowbarOffsetDeg:F2}° off the nose")
        );
    }

    /// <summary>
    /// Pins which side the tug is on when a push turns: pushing while the nose yaws right, the nose gear's velocity in
    /// the fuselage frame is (−v, ωL), so the wheel line — and the towbar out along it — sits atan(ωL / v) = atan(L / R)
    /// to the <em>left</em> of the axis, the opposite side from the yaw.
    /// </summary>
    [Fact]
    public void PushbackPhase_PushTurningRight_PutsTheTowbarLeftOfTheNose()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 0);
        (PushbackPhase? phase, PhaseContext? ctx) = StartPush(aircraft, TugMove.TurnTo(PushbackLegKind.Push, 90));
        double radiusFt = TugKinematics.TurnRadiusFt(aircraft.AircraftType, tight: false);
        double expectedDeg = Math.Atan(WheelbaseFt(aircraft.AircraftType) / radiusFt) * (180.0 / Math.PI);

        (bool Completed, List<PushTick> Ticks) run = RunPush(aircraft, phase, ctx, 300);
        List<PushTick> steering = FullRateTurns(run.Ticks, radiusFt);

        Assert.True(run.Completed, "the turn never completed");
        Assert.True(steering.Count >= 5, $"only {steering.Count} steps steered at the full rate, so the towbar angle proved nothing");
        Assert.All(steering, t => Assert.Equal(-expectedDeg, t.TowbarOffsetDeg!.Value, 3.0));
    }

    /// <summary>
    /// Pins the other kind: pulling and turning right the nose gear is steered right, so the tug sits ahead and to the
    /// <em>right</em> of the nose by that same atan(L / R).
    /// </summary>
    [Fact]
    public void PushbackPhase_PullTurningRight_PutsTheTowbarRightOfTheNose()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 0);
        (PushbackPhase? phase, PhaseContext? ctx) = StartPush(aircraft, TugMove.TurnTo(PushbackLegKind.Pull, 90));
        double radiusFt = TugKinematics.TurnRadiusFt(aircraft.AircraftType, tight: false);
        double expectedDeg = Math.Atan(WheelbaseFt(aircraft.AircraftType) / radiusFt) * (180.0 / Math.PI);

        (bool Completed, List<PushTick> Ticks) run = RunPush(aircraft, phase, ctx, 300);
        List<PushTick> steering = FullRateTurns(run.Ticks, radiusFt);

        Assert.True(run.Completed, "the turn never completed");
        Assert.True(steering.Count >= 5, $"only {steering.Count} steps steered at the full rate, so the towbar angle proved nothing");
        Assert.All(steering, t => Assert.Equal(expectedDeg, t.TowbarOffsetDeg!.Value, 3.0));
    }

    /// <summary>Pins: the move starts with the towbar on the fuselage axis, and a tug taken off the move lets go of it.</summary>
    [Fact]
    public void PushbackPhase_OnStart_AttachesTheTowbar_AndASkippedMoveDetachesIt()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 90);
        (PushbackPhase? phase, PhaseContext? ctx) = StartPush(aircraft, TugMove.Straight(PushbackLegKind.Push, 50.0));

        Assert.NotNull(aircraft.Ground.TowbarTrueHeading);
        Assert.Equal(90.0, aircraft.Ground.TowbarTrueHeading!.Value.Degrees, 9);

        phase.OnEnd(ctx, PhaseStatus.Skipped);

        Assert.Null(aircraft.Ground.TowbarTrueHeading);
    }

    /// <summary>
    /// Pins the boundary: a move that completes into the next one leaves the towbar standing, so a continuous tow does
    /// not snap the tug straight at the boundary; one that completes at a standstill is the end of the tow and lets go.
    /// </summary>
    [Fact]
    public void PushbackPhase_CompletedMove_KeepsTheTowbarOnlyWhenItFlowsIntoTheNext()
    {
        AircraftState continuing = MakeGroundAircraft(heading: 90);
        (PushbackPhase? continuingPhase, PhaseContext? continuingCtx) = StartMoveContinuing(
            continuing,
            TugMove.Straight(PushbackLegKind.Push, 50.0),
            continuesIntoNextMove: true
        );

        continuingPhase.OnEnd(continuingCtx, PhaseStatus.Completed);

        Assert.NotNull(continuing.Ground.TowbarTrueHeading);

        AircraftState stopping = MakeGroundAircraft(heading: 90);
        (PushbackPhase? stoppingPhase, PhaseContext? stoppingCtx) = StartMoveContinuing(
            stopping,
            TugMove.Straight(PushbackLegKind.Push, 50.0),
            continuesIntoNextMove: false
        );

        stoppingPhase.OnEnd(stoppingCtx, PhaseStatus.Completed);

        Assert.Null(stopping.Ground.TowbarTrueHeading);
    }

    /// <summary>Pins: a straight move completes only once it has covered its distance, and overshoots it by under a step.</summary>
    [Fact]
    public void PushbackPhase_Straight_CompletesOnlyAfterItsDistance()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 90);
        LatLon start = aircraft.Position;
        (PushbackPhase? phase, PhaseContext? ctx) = StartPush(aircraft, TugMove.Straight(PushbackLegKind.Push, 50.0));

        Assert.False(phase.OnTick(ctx), "completed before moving");
        FlightPhysics.Update(aircraft, 1.0);
        (bool Completed, List<PushTick> Ticks) run = RunPush(aircraft, phase, ctx, 300);

        double pushedFt = GeoMath.DistanceNm(start, aircraft.Position) * GeoMath.FeetPerNm;
        Assert.True(run.Completed, "the straight push never completed");
        Assert.InRange(pushedFt, 50.0, 52.0);
        Assert.Equal(270.0, GeoMath.BearingTo(start, aircraft.Position), 1.0);
    }

    /// <summary>
    /// Pins: a push toward a point off its tail steers by curvature — a stopped aircraft does not rotate, and a moving
    /// one turns no tighter than the routine radius — and ends on the point.
    /// </summary>
    [Fact]
    public void PushbackPhase_ToPoint_TurnsNoTighterThanTheRadius()
    {
        AircraftState aircraft = MakeGroundAircraft(lat: 37.620, lon: -122.380, heading: 90);
        LatLon target = GeoMath.ProjectPoint(aircraft.Position, new TrueHeading(225), 400.0 / GeoMath.FeetPerNm);
        (PushbackPhase? phase, PhaseContext? ctx) = StartPush(aircraft, TugMove.ToPoint(PushbackLegKind.Push, target));
        double radiusFt = TugKinematics.TurnRadiusFt(aircraft.AircraftType, tight: false);

        (bool Completed, List<PushTick> Ticks) run = RunPush(aircraft, phase, ctx, 600);

        Assert.True(run.Completed, "the push never reached its point");
        Assert.Equal(0.0, run.Ticks[0].NoseTurnDeg, 6);
        Assert.All(run.Ticks, t => AssertWithinCurvature(t, radiusFt));
        Assert.True(run.Ticks.Max(t => t.NoseTurnDeg) > 0.5, "the push never turned, so the bound proved nothing");
        Assert.True(GeoMath.DistanceNm(aircraft.Position, target) * GeoMath.FeetPerNm <= 3.0, "ended off the point");
    }

    /// <summary>Pins: a tight move turns on the tight radius — more heading per foot than the same move flown routinely.</summary>
    [Fact]
    public void PushbackPhase_TightTurnTo_TurnsMorePerFootThanARoutineOne()
    {
        double routine = TurnPerFoot(TugMove.TurnTo(PushbackLegKind.Push, 180));
        double tight = TurnPerFoot(TugMove.TurnTo(PushbackLegKind.Push, 180) with { Tight = true });

        double expected = TugKinematics.TurnRadiusFt("B738", tight: false) / TugKinematics.TurnRadiusFt("B738", tight: true);
        Assert.True(tight > routine * 1.5, $"tight {tight:F4} rad/ft vs routine {routine:F4} rad/ft");
        Assert.InRange(tight / routine, expected * 0.9, expected * 1.1);
    }

    // --- Pushback other directions ---

    [Fact]
    public void PushbackPhase_FacingSouth_PushesNorth()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 180);
        aircraft.Phases = new PhaseList();
        PushbackPhase phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        PhaseContext ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        // Pushback heading should be opposite of nose: 180 + 180 = 360 → 0
        double expected = (180.0 + 180.0) % 360.0;
        Assert.Equal(expected, aircraft.Ground.PushbackTrueHeading!.Value.Degrees, 1.0);
    }

    [Fact]
    public void PushbackPhase_FacingWest_PushesEast()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 270);
        aircraft.Phases = new PhaseList();
        PushbackPhase phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        PhaseContext ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        Assert.Equal(90, aircraft.Ground.PushbackTrueHeading!.Value.Degrees, 1.0);
    }

    [Fact]
    public void PushbackPhase_FacingNorth_PushesSouth()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 360);
        aircraft.Phases = new PhaseList();
        PushbackPhase phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        PhaseContext ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        Assert.Equal(180, aircraft.Ground.PushbackTrueHeading!.Value.Degrees, 1.0);
    }

    // --- TaxiingPhase with pre-cleared hold-short ---

    [Fact]
    public void TaxiingPhase_PreClearedHoldShort_SkipsHoldingShortPhase()
    {
        AirportGroundLayout layout = BuildCrossingLayout();
        AircraftState aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

        var route = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[0].Directed(layout.Nodes[0], layout.Nodes[1]) },
                new TaxiRouteSegment { TaxiwayName = "RWY28L", Edge = layout.Edges[1].Directed(layout.Nodes[1], layout.Nodes[2]) },
                new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[2].Directed(layout.Nodes[2], layout.Nodes[3]) },
            ],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28L/10R",
                    IsCleared = true, // pre-cleared
                },
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = "28L/10R",
                    IsCleared = true, // pre-cleared
                },
            ],
        };
        aircraft.Ground.AssignedTaxiRoute = route;

        aircraft.Phases = new PhaseList();
        var taxiPhase = new TaxiingPhase();
        aircraft.Phases.Add(taxiPhase);
        PhaseContext ctx = MakeContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        // Place at hold-short node
        aircraft.Position = layout.Nodes[1].Position;

        // Tick: should NOT insert HoldingShortPhase since it's pre-cleared
        for (int i = 0; i < 300; i++)
        {
            if (taxiPhase.OnTick(ctx))
            {
                break;
            }
        }

        // Pre-cleared: phase should insert crossing but no hold
        // Verify no HoldingShortPhase in the upcoming phases
        Assert.DoesNotContain(aircraft.Phases.Phases.Skip(1), p => p is HoldingShortPhase);
    }

    // --- Snapshot/restore mid-segment behavior ---

    [Fact]
    public void TaxiingPhase_RestoreMidSegment_DoesNotSkipSegment()
    {
        // Mid-segment snapshot/restore: TaxiingPhase persisted Initialized=true,
        // but GroundNavigatorDto does not carry the active PathPrimitive. After
        // FromSnapshot the navigator's _currentPrimitive is null, so Tick falls
        // into its default branch and returns ArrivedAtNode immediately. That
        // signals ArriveAtNode, which advances route.CurrentSegmentIndex —
        // skipping the segment the aircraft was actually traversing.
        //
        // The fix: FromSnapshot leaves _initialized=false so the next OnTick
        // re-runs SetupCurrentSegment from the route's current segment.
        AirportGroundLayout layout = BuildCrossingLayout();
        AircraftState aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

        // 3-segment route: 0→1, 1→2, 2→3. No hold-shorts so progression is uninterrupted.
        var route = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[0].Directed(layout.Nodes[0], layout.Nodes[1]) },
                new TaxiRouteSegment { TaxiwayName = "RWY28L", Edge = layout.Edges[1].Directed(layout.Nodes[1], layout.Nodes[2]) },
                new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[2].Directed(layout.Nodes[2], layout.Nodes[3]) },
            ],
            HoldShortPoints = [],
        };
        aircraft.Ground.AssignedTaxiRoute = route;

        aircraft.Phases = new PhaseList();
        var taxiPhase = new TaxiingPhase();
        aircraft.Phases.Add(taxiPhase);
        PhaseContext ctx = MakeContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        // Tick a few times so the phase initialises and the aircraft moves a bit
        // along segment 0 — but stays well short of node 1.
        for (int i = 0; i < 3; i++)
        {
            taxiPhase.OnTick(ctx);
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
        }

        Assert.Equal(0, route.CurrentSegmentIndex);

        // Snapshot mid-segment, then restore into a fresh phase via the real
        // PhaseList.FromSnapshot path — which adds restored phases WITHOUT
        // calling OnStart (PhaseList.cs:316-320). Calling Start() afterwards
        // would call OnStart and mask the bug because OnStart re-runs
        // SetupCurrentSegment.
        var dto = (TaxiingPhaseDto)taxiPhase.ToSnapshot();
        var restored = TaxiingPhase.FromSnapshot(dto);

        AircraftState restoredAircraft = MakeGroundAircraft(aircraft.Position.Lat, aircraft.Position.Lon, heading: 0);
        restoredAircraft.IndicatedAirspeed = aircraft.IndicatedAirspeed;
        restoredAircraft.Ground.AssignedTaxiRoute = route;
        restoredAircraft.Phases = new PhaseList();
        restoredAircraft.Phases.Add(restored);
        PhaseContext restoredCtx = MakeContext(restoredAircraft, layout);

        int idxBefore = route.CurrentSegmentIndex;
        restored.OnTick(restoredCtx);

        Assert.Equal(idxBefore, route.CurrentSegmentIndex);
    }

    // --- Pushback distance scales with aircraft length ---

    [Fact]
    public void SimplePushbackDistance_LargerJet_PushesFartherThanLightSingle()
    {
        // Needs FaaAircraftDatabase + WakeTurbulenceData populated; otherwise every
        // type falls back to the 0.015 nm baseline and the test sees no spread.
        // xUnit runs collections in parallel, so we can't rely on another class
        // having initialized first.
        TestVnasData.EnsureInitialized();

        double b738 = CategoryPerformance.SimplePushbackDistanceNm("B738");
        double c172 = CategoryPerformance.SimplePushbackDistanceNm("C172");
        double a388 = CategoryPerformance.SimplePushbackDistanceNm("A388");

        Assert.True(b738 > c172, $"B738 {b738} should exceed C172 {c172}");
        Assert.True(a388 > b738, $"A388 {a388} should exceed B738 {b738}");
    }

    // --- Pushback held state sets TargetSpeed to 0 ---

    [Fact]
    public void PushbackPhase_WhenHeld_SetsTargetSpeedZero()
    {
        AircraftState aircraft = MakeGroundAircraft();
        aircraft.Phases = new PhaseList();
        PushbackPhase phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        PhaseContext ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        // Tick once to get moving
        FlightPhysics.Update(aircraft, 1.0);
        phase.OnTick(ctx);

        // Hold: the target goes to zero at once, and physics brakes the tow onto it at the towbar rate
        aircraft.Ground.Hold = HoldDirective.HoldPosition;
        phase.OnTick(ctx);

        Assert.Equal(0, ctx.Targets.TargetSpeed);

        for (int i = 0; i < 5; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            phase.OnTick(ctx);
        }

        Assert.Equal(0, aircraft.GroundSpeed);
    }

    // --- Tug moves: no alignment stage, pulls, dwell ---

    /// <summary>
    /// Pins: there is no rotate-in-place stage — a push whose facing is 180° away is under way from its first tick,
    /// tail-first, and its nose has not moved before the aircraft has.
    /// </summary>
    [Fact]
    public void PushbackPhase_LargeTurn_StartsMovingAtOnceWithNoPivot()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 0);
        (PushbackPhase? phase, PhaseContext? ctx) = StartPush(aircraft, TugMove.TurnTo(PushbackLegKind.Push, 180) with { Tight = true });

        Assert.Equal(180.0, aircraft.Ground.PushbackTrueHeading!.Value.Degrees, 6);
        Assert.True(ctx.Targets.TargetSpeed > 0, "the push did not ask for speed");

        (bool Completed, List<PushTick> Ticks) run = RunPush(aircraft, phase, ctx, 5);

        Assert.True(run.Ticks.Sum(t => t.MovedFt) > 1.0, "the aircraft did not move");
        double radiusFt = TugKinematics.TurnRadiusFt(aircraft.AircraftType, tight: true);
        Assert.All(run.Ticks, t => AssertWithinCurvature(t, radiusFt));
    }

    /// <summary>Pins: a pull leaves the push heading null and moves the aircraft nose-first.</summary>
    [Fact]
    public void PushbackPhase_Pull_LeadsWithTheNose()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 30);
        LatLon start = aircraft.Position;
        LatLon target = GeoMath.ProjectPoint(start, new TrueHeading(30), 200.0 / GeoMath.FeetPerNm);
        (PushbackPhase? phase, PhaseContext? ctx) = StartPush(aircraft, TugMove.ToPoint(PushbackLegKind.Pull, target));

        Assert.Null(aircraft.Ground.PushbackTrueHeading);
        (bool Completed, List<PushTick> Ticks) run = RunPush(aircraft, phase, ctx, 10);

        Assert.All(run.Ticks, t => Assert.Null(t.PushGapDeg));
        Assert.True(run.Ticks.Sum(t => t.MovedFt) > 10.0, "the pull did not move the aircraft");
        Assert.Equal(30.0, GeoMath.BearingTo(start, aircraft.Position), 1.0);
    }

    /// <summary>
    /// Pins: a reversal dwells stopped for <see cref="PushbackPhase.DwellSeconds"/> — no movement, no rotation, no speed
    /// asked for, the push heading already set — and then moves.
    /// </summary>
    [Fact]
    public void PushbackPhase_DwellBefore_HoldsStillForTheDwellThenMoves()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 0);
        LatLon start = aircraft.Position;
        (PushbackPhase? phase, PhaseContext? ctx) = StartPush(aircraft, TugMove.Straight(PushbackLegKind.Push, 50.0) with { DwellBefore = true });
        int dwellTicks = (int)PushbackPhase.DwellSeconds;

        (bool Completed, List<PushTick> Ticks) dwell = RunPush(aircraft, phase, ctx, dwellTicks);

        Assert.All(dwell.Ticks, t => Assert.Equal(0.0, t.MovedFt, 9));
        Assert.All(dwell.Ticks, t => Assert.Equal(0.0, t.NoseTurnDeg, 9));
        Assert.All(dwell.Ticks, t => Assert.Equal(0.0, t.PushGapDeg!.Value, 6));
        Assert.Equal(0, ctx.Targets.TargetSpeed ?? 0);
        Assert.Equal(start, aircraft.Position);

        (bool Completed, List<PushTick> Ticks) moving = RunPush(aircraft, phase, ctx, 1);
        Assert.True(moving.Ticks[0].MovedFt > 0.0, "the push did not move once the dwell was over");
    }

    // -------------------------------------------------------------------------
    // Issue #167 — adjust pushback face direction mid-pushback
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pins: the stand push-off of a single-goal push can be amended while it runs and not once it has completed;
    /// it has no ramp priority until it has moved half a fuselage off the stand; and it reports its planned end
    /// until it completes.
    /// </summary>
    [Fact]
    public void PushbackPhase_StandPushOff_AmendableAndNoRampPriorityUntilItHasMoved()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 0);
        var standPose = new TugPose(aircraft.Position, 0);
        var move = TugMove.Straight(PushbackLegKind.Push, 100.0);
        var phase = new PushbackPhase
        {
            Move = move,
            PlannedEnd = TugKinematics.Simulate(standPose, [move], aircraft.AircraftType, 1.0).End.Position,
            StartsAtStand = true,
            ContinuesIntoNextMove = false,
            ContinuesStandPushOff = false,
            Amendment = TugAmendment.For(TugGoal.Facing(90), standPose),
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        PhaseContext ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        Assert.True(phase.CanAmend(aircraft));
        Assert.False(phase.HasRampPriority(aircraft));
        Assert.True(phase.TryGetPushLegEnd(aircraft, out LatLon end));
        Assert.Equal(phase.PlannedEnd, end);

        (bool Completed, List<PushTick> Ticks) run = RunPush(aircraft, phase, ctx, 300);

        Assert.True(run.Completed);
        Assert.True(phase.HasRampPriority(aircraft), "a 100 ft push-off left a B738 on its stand");
        Assert.False(phase.CanAmend(aircraft), "a completed push-off was still amendable");
        Assert.False(phase.TryGetPushLegEnd(aircraft, out _), "a completed move still reported an end");
    }

    /// <summary>
    /// Pins: a move with no amendment is never amendable, and a move flown through from the stand push-off with no
    /// reversal between keeps the push-off's ramp priority — it is still leg 1 of the push off the stand.
    /// </summary>
    [Fact]
    public void PushbackPhase_MoveContinuingThePushOff_NotAmendableAndKeepsRampPriority()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 0);
        (PushbackPhase? phase, PhaseContext _) = StartMoveOffTheStand(
            aircraft,
            TugMove.TurnTo(PushbackLegKind.Push, 90),
            continuesStandPushOff: true
        );

        Assert.False(phase.CanAmend(aircraft));
        Assert.True(phase.HasRampPriority(aircraft), "a move flown through from the push-off lost the push's ramp priority");
    }

    /// <summary>
    /// Pins: a move the plan reversed into — the second leg of a tug move, or the push half of a three-point turn —
    /// is a repositioning tow, so it has no ramp priority and yields to taxiing traffic like anything else.
    /// </summary>
    [Fact]
    public void PushbackPhase_MoveAfterAReversal_HasNoRampPriority()
    {
        AircraftState aircraft = MakeGroundAircraft(heading: 0);
        (PushbackPhase? phase, PhaseContext _) = StartMoveOffTheStand(
            aircraft,
            TugMove.TurnTo(PushbackLegKind.Push, 90),
            continuesStandPushOff: false
        );

        Assert.False(phase.CanAmend(aircraft));
        Assert.False(phase.HasRampPriority(aircraft), "a move after a reversal claimed the push-off's ramp priority");
    }

    // -------------------------------------------------------------------------
    // CrossingRunwayPhase OnEnd must preserve momentum for following TaxiingPhase
    // -------------------------------------------------------------------------

    [Fact]
    public void CrossingRunwayPhase_OnEnd_Completed_PreservesMomentumForFollowingTaxi()
    {
        // After a runway crossing, the typical next phase is TaxiingPhase
        // (BuildResumePhases at TaxiingPhase.cs:341). Real-world: aircraft
        // cross runways at ~10 kts and continue into taxi without stopping.
        // OnEnd must NOT zero IndicatedAirspeed, or the aircraft loses its
        // crossing momentum and has to re-accelerate from zero.
        AircraftState aircraft = MakeGroundAircraft();
        aircraft.IndicatedAirspeed = 10; // mid-crossing speed
        var phase = new CrossingRunwayPhase(approachNodeId: 1, targetNodeId: 2, runwayId: "28L");

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Logger = NullLogger.Instance,
        };

        phase.OnEnd(ctx, PhaseStatus.Completed);

        Assert.Equal(10, aircraft.IndicatedAirspeed);
    }

    // -------------------------------------------------------------------------
    // RunwayExitPhase respects Ground.IsImmobile
    // -------------------------------------------------------------------------

    [Fact]
    public void TaxiingPhase_WhenExpediting_RaisesMaxSpeed()
    {
        // EXPEDITE on the ground bumps the taxi cap by TaxiExpediteMultiplier
        // (jet 30 kts → 39 kts). Verified by the navigator's MaxSpeedKts after
        // a tick of TaxiingPhase with the flag set.
        AirportGroundLayout layout = BuildCrossingLayout();
        AircraftState aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[0].Directed(layout.Nodes[0], layout.Nodes[1]) }],
            HoldShortPoints = [],
        };
        aircraft.Ground.AssignedTaxiRoute = route;
        aircraft.Phases = new PhaseList();
        var taxi = new TaxiingPhase();
        aircraft.Phases.Add(taxi);
        PhaseContext ctx = MakeContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        Assert.Equal(CategoryPerformance.TaxiSpeed(AircraftCategory.Jet), taxi.NavMaxSpeedKts, precision: 3);

        aircraft.Ground.IsExpeditingTaxi = true;
        taxi.OnTick(ctx);

        double expected = CategoryPerformance.TaxiSpeed(AircraftCategory.Jet) * CategoryPerformance.TaxiExpediteMultiplier;
        Assert.Equal(expected, taxi.NavMaxSpeedKts, precision: 3);

        aircraft.Ground.IsExpeditingTaxi = false;
        taxi.OnTick(ctx);

        Assert.Equal(CategoryPerformance.TaxiSpeed(AircraftCategory.Jet), taxi.NavMaxSpeedKts, precision: 3);
    }

    [Fact]
    public void RunwayExitPhase_WhenHeld_StopsRolling()
    {
        // Concrete silent-failure case: HOLD POSITION sets Ground.Hold,
        // but RunwayExitPhase used to keep setting TargetSpeed = coastSpeed each
        // tick — the controller saw "Hold position" success but the aircraft kept
        // rolling. The other ground-movement phases (CrossingRunwayPhase,
        // PushbackPhase, TaxiingPhase, etc.) all honor IsImmobile; RunwayExitPhase
        // must too.
        AircraftState aircraft = MakeGroundAircraft();
        aircraft.Phases = new PhaseList();
        var phase = new RunwayExitPhase();
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Logger = NullLogger.Instance,
        };
        phase.OnStart(ctx);

        aircraft.Ground.Hold = HoldDirective.HoldPosition;
        phase.OnTick(ctx);

        Assert.Equal(0, aircraft.Targets.TargetSpeed);
    }

    [Fact]
    public void TaxiingPhase_WhenHeld_StopsRolling()
    {
        // Issue #407: HOLD POSITION on a taxiing aircraft set Ground.Hold and the
        // controller saw "Hold position" success, but TaxiingPhase only nudged
        // IndicatedAirspeed down without pinning Targets.TargetSpeed — generic
        // physics kept re-accelerating toward the stale taxi target every sub-tick,
        // so the aircraft never stopped (two held aircraft met head-on at OAK).
        AirportGroundLayout layout = BuildCrossingLayout();
        AircraftState aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[0].Directed(layout.Nodes[0], layout.Nodes[1]) }],
            HoldShortPoints = [],
        };
        aircraft.Ground.AssignedTaxiRoute = route;
        aircraft.Phases = new PhaseList();
        var taxi = new TaxiingPhase();
        aircraft.Phases.Add(taxi);
        PhaseContext ctx = MakeContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        aircraft.IndicatedAirspeed = 15;
        aircraft.Targets.TargetSpeed = 12.5;
        aircraft.Ground.Hold = HoldDirective.HoldPosition;
        taxi.OnTick(ctx);

        Assert.Equal(0, aircraft.Targets.TargetSpeed);

        // Physics owns ground speed: the phase pins the target to 0 and physics brakes toward it at the
        // category ground decel rate. From 15 kt at 5 kt/s that is three whole-second ticks.
        for (int i = 0; (i < 30) && (aircraft.IndicatedAirspeed > 0); i++)
        {
            taxi.OnTick(ctx);
            FlightPhysics.Update(aircraft, 1.0, null, null, simTimeSeconds: i);
        }

        Assert.Equal(0, aircraft.IndicatedAirspeed);
        Assert.Equal(0, aircraft.Targets.TargetSpeed);

        // ControlTargets persist across phases: no braking-rate override may leak out of the taxi phase
        // into a later airborne deceleration.
        taxi.OnEnd(ctx, PhaseStatus.Completed);
        Assert.Null(aircraft.Targets.DesiredDecelRate);
    }

    // -------------------------------------------------------------------------
    // Held-stop contract across every ground motion phase
    // -------------------------------------------------------------------------

    /// <summary>
    /// The hold contract for every ground motion phase: while Ground.Hold is set, the phase must
    /// leave Targets.TargetSpeed pinned at 0 after its tick — a stale nonzero target lets
    /// FlightPhysics.UpdateSpeed re-accelerate toward it every sub-tick, fighting the phase's own
    /// stop (the issue-407 failure mode in TaxiingPhase, previously fixed one-off in
    /// RunwayExitPhase). Behavioral companion to <see cref="GroundPhaseConvention"/>'s source-text
    /// IsImmobile check, which TaxiingPhase passed while still broken.
    /// </summary>
    [Theory]
    [InlineData("Taxiing")]
    [InlineData("RunwayExit")]
    [InlineData("Pushback")]
    [InlineData("CrossingRunway")]
    [InlineData("ClearRunway")]
    [InlineData("AirTaxi")]
    [InlineData("Following")]
    public void GroundMotionPhase_WhenHeld_LeavesNoSpeedTarget(string phaseName)
    {
        AirportGroundLayout layout = BuildCrossingLayout();
        AircraftState aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);
        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[0].Directed(layout.Nodes[0], layout.Nodes[1]) }],
            HoldShortPoints = [],
        };

        Phase phase = phaseName switch
        {
            "Taxiing" => new TaxiingPhase(),
            "RunwayExit" => new RunwayExitPhase(),
            "Pushback" => SimplePush(aircraft),
            "CrossingRunway" => new CrossingRunwayPhase(layout.Nodes[0].Id, layout.Nodes[1].Id, "28R"),
            "ClearRunway" => new ClearRunwayPhase(layout.Nodes[0].Id, layout.Nodes[1].Id),
            "AirTaxi" => new AirTaxiPhase(37.621, -122.380, null),
            "Following" => new FollowingPhase("OTHER"),
            _ => throw new System.ArgumentOutOfRangeException(nameof(phaseName)),
        };

        if (phase is TaxiingPhase)
        {
            aircraft.Ground.AssignedTaxiRoute = route;
        }

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        PhaseContext ctx = MakeContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        aircraft.IndicatedAirspeed = 15;
        aircraft.Targets.TargetSpeed = 12.5;
        aircraft.Ground.Hold = HoldDirective.HoldPosition;
        phase.OnTick(ctx);
        phase.OnTick(ctx);

        Assert.True(
            aircraft.Targets.TargetSpeed is null or 0,
            $"{phase.GetType().Name} left Targets.TargetSpeed={aircraft.Targets.TargetSpeed} while held — "
                + "physics will re-accelerate toward the stale target every sub-tick."
        );
    }

    /// <summary>
    /// A LAHSO hold on the runway must re-publish TargetSpeed = 0 on every tick, not only in OnStart:
    /// ground speed is integrated by physics, so a nonzero target surviving from the landing roll would
    /// be accelerated toward every sub-tick while the phase pins IndicatedAirspeed to 0.
    /// </summary>
    [Fact]
    public void RunwayHoldingPhase_WhenHolding_RepublishesZeroSpeedTargetEveryTick()
    {
        AirportGroundLayout layout = BuildCrossingLayout();
        AircraftState aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);
        var phase = new RunwayHoldingPhase("28L/10R");

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        PhaseContext ctx = MakeContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        // A stale target from the phase that ran before the hold.
        aircraft.Targets.TargetSpeed = 30;
        phase.OnTick(ctx);

        Assert.Equal(0, aircraft.Targets.TargetSpeed);
        Assert.Equal(0, aircraft.IndicatedAirspeed);
    }
}
