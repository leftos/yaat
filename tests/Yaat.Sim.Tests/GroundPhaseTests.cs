using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
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
        };

    private static (PushbackPhase Phase, PhaseContext Ctx) StartPush(AircraftState aircraft, TugMove move)
    {
        var phase = PushOf(aircraft, move);
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);
        return (phase, ctx);
    }

    /// <summary>One engine-order second: what the aircraft moved, how far the nose turned, and how far the push heading is off the nose's reciprocal (null on a pull).</summary>
    private readonly record struct PushTick(double MovedFt, double NoseTurnDeg, double? PushGapDeg);

    /// <summary>Ticks the phase then physics, one second at a time, until the phase completes or the budget runs out.</summary>
    private static (bool Completed, List<PushTick> Ticks) RunPush(AircraftState aircraft, PushbackPhase phase, PhaseContext ctx, int maxTicks)
    {
        var ticks = new List<PushTick>();
        for (int i = 0; i < maxTicks; i++)
        {
            var from = aircraft.Position;
            var nose = aircraft.TrueHeading;
            if (phase.OnTick(ctx))
            {
                return (true, ticks);
            }

            FlightPhysics.Update(aircraft, 1.0);
            double? gap = aircraft.Ground.PushbackTrueHeading is { } push ? push.ToReciprocal().AbsAngleTo(aircraft.TrueHeading) : null;
            ticks.Add(new PushTick(GeoMath.DistanceNm(from, aircraft.Position) * GeoMath.FeetPerNm, nose.AbsAngleTo(aircraft.TrueHeading), gap));
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
        var aircraft = MakeGroundAircraft(heading: 0);
        var (phase, ctx) = StartPush(aircraft, move);
        var run = RunPush(aircraft, phase, ctx, 20);
        var turning = run.Ticks.Skip(2).ToList();
        return (turning.Sum(t => t.NoseTurnDeg) * Math.PI / 180.0) / turning.Sum(t => t.MovedFt);
    }

    // --- FIX 1: TryHoldPosition uses IsOnGround ---

    [Fact]
    public void TryHoldPosition_OnGround_SetsHeld()
    {
        var aircraft = MakeGroundAircraft();
        aircraft.IsOnGround = true;

        var result = GroundCommandHandler.TryHoldPosition(aircraft);

        Assert.True(result.Success);
        Assert.True(aircraft.Ground.IsImmobile);
    }

    [Fact]
    public void TryHoldPosition_Airborne_Fails()
    {
        var aircraft = MakeGroundAircraft();
        aircraft.IsOnGround = false;

        var result = GroundCommandHandler.TryHoldPosition(aircraft);

        Assert.False(result.Success);
        Assert.False(aircraft.Ground.IsImmobile);
    }

    [Fact]
    public void TryHoldPosition_FollowingPhase_Succeeds()
    {
        var aircraft = MakeGroundAircraft();
        aircraft.IsOnGround = true;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new FollowingPhase("LEAD01"));
        aircraft.Phases.Start(MakeContext(aircraft));

        var result = GroundCommandHandler.TryHoldPosition(aircraft);

        Assert.True(result.Success);
        Assert.True(aircraft.Ground.IsImmobile);
    }

    // --- FIX 2: PushbackPhase respects IsHeld ---

    [Fact]
    public void PushbackPhase_WhenHeld_StopsMoving()
    {
        var aircraft = MakeGroundAircraft();
        aircraft.Phases = new PhaseList();
        var phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        // PushbackTrueHeading should be set (opposite of aircraft heading)
        Assert.NotNull(aircraft.Ground.PushbackTrueHeading);

        // Tick once — phase sets TargetSpeed, FlightPhysics moves
        FlightPhysics.Update(aircraft, 1.0);
        phase.OnTick(ctx);
        Assert.True(aircraft.GroundSpeed > 0);

        // Hold and tick again
        aircraft.Ground.Hold = HoldDirective.HoldPosition;
        phase.OnTick(ctx);
        Assert.Equal(0, aircraft.GroundSpeed);
    }

    [Fact]
    public void PushbackPhase_WhenResumed_ContinuesMoving()
    {
        var aircraft = MakeGroundAircraft();
        aircraft.Phases = new PhaseList();
        var phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        // Let physics accelerate once
        FlightPhysics.Update(aircraft, 1.0);

        // Hold
        aircraft.Ground.Hold = HoldDirective.HoldPosition;
        phase.OnTick(ctx);
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

    // --- FIX 3: Hold-short → taxi resume ---

    [Fact]
    public void TaxiingPhase_RunwayCrossing_InsertsHoldCrossingResume()
    {
        var layout = BuildCrossingLayout();
        var aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

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
        var ctx = MakeContext(aircraft, layout);
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
        var phases = aircraft.Phases.Phases;
        Assert.True(phases.Count >= 4, $"Expected at least 4 phases, got {phases.Count}");
        Assert.IsType<TaxiingPhase>(phases[0]);
        Assert.IsType<HoldingShortPhase>(phases[1]);
        Assert.IsType<CrossingRunwayPhase>(phases[2]);
        Assert.IsType<TaxiingPhase>(phases[3]);
    }

    [Fact]
    public void TaxiingPhase_ExplicitHoldShort_InsertsHoldAndResume()
    {
        var layout = BuildCrossingLayout();
        var aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

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
        var ctx = MakeContext(aircraft, layout);
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
        var phases = aircraft.Phases.Phases;
        Assert.True(phases.Count >= 4, $"Expected at least 4 phases (Taxi + Hold + Cross + Taxi), got {phases.Count}");
        Assert.IsType<TaxiingPhase>(phases[0]);
        Assert.IsType<HoldingShortPhase>(phases[1]);
        Assert.IsType<CrossingRunwayPhase>(phases[2]);
        Assert.IsType<TaxiingPhase>(phases[3]);
    }

    [Fact]
    public void TaxiingPhase_DestinationRunway_InsertsHoldOnly()
    {
        var layout = BuildCrossingLayout();
        var aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

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
        var ctx = MakeContext(aircraft, layout);
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
        var phases = aircraft.Phases.Phases;
        Assert.Equal(3, phases.Count);
        Assert.IsType<TaxiingPhase>(phases[0]);
        Assert.IsType<HoldingShortPhase>(phases[1]);
        Assert.IsType<HoldingInPositionPhase>(phases[2]);
    }

    // --- FIX 4: FollowingPhase hold-short awareness ---

    [Fact]
    public void FollowingPhase_ApproachingHoldShort_AutoHolds()
    {
        var layout = BuildCrossingLayout();
        var target = MakeGroundAircraft(37.623, -122.380, heading: 0);
        target.Callsign = "LEAD01";

        // Place follower just before the hold-short node (heading toward it)
        var aircraft = MakeGroundAircraft(37.6208, -122.380, heading: 0);
        aircraft.Phases = new PhaseList();
        var followPhase = new FollowingPhase("LEAD01");
        aircraft.Phases.Add(followPhase);
        var ctx = MakeContext(aircraft, layout, cs => cs == "LEAD01" ? target : null);
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
        var phases = aircraft.Phases.Phases;
        Assert.True(phases.Count >= 3, $"Expected at least 3 phases, got {phases.Count}");
        Assert.IsType<FollowingPhase>(phases[0]);
        Assert.IsType<HoldingShortPhase>(phases[1]);
        Assert.IsType<FollowingPhase>(phases[2]);
    }

    [Fact]
    public void FollowingPhase_HeadingAway_DoesNotHold()
    {
        var layout = BuildCrossingLayout();
        var target = MakeGroundAircraft(37.619, -122.380, heading: 180);
        target.Callsign = "LEAD01";

        // Place follower near node 1 but heading AWAY from it (south)
        var aircraft = MakeGroundAircraft(37.6208, -122.380, heading: 180);
        aircraft.Phases = new PhaseList();
        var followPhase = new FollowingPhase("LEAD01");
        aircraft.Phases.Add(followPhase);
        var ctx = MakeContext(aircraft, layout, cs => cs == "LEAD01" ? target : null);
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
        var target = MakeGroundAircraft(37.623, -122.380, heading: 0);
        target.Callsign = "LEAD01";

        var aircraft = MakeGroundAircraft(37.6208, -122.380, heading: 0);
        aircraft.Phases = new PhaseList();
        var followPhase = new FollowingPhase("LEAD01");
        aircraft.Phases.Add(followPhase);
        // No ground layout
        var ctx = MakeContext(aircraft, null, cs => cs == "LEAD01" ? target : null);
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
        var lead = MakeGroundAircraft(37.623, -122.380, heading: 0);
        lead.Callsign = "LEAD01";
        lead.IndicatedAirspeed = 0;

        // Follower directly behind the lead, just inside the follow distance, still rolling at 5 kt.
        double startGapNm = FollowingPhase.FollowDistanceNm - 0.002;
        var follower = MakeGroundAircraft(37.623 - (startGapNm / 60.0), -122.380, heading: 0);
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
        var aircraft = MakeGroundAircraft(heading: 90);
        aircraft.Phases = new PhaseList();
        var phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
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
        var aircraft = MakeGroundAircraft();
        aircraft.Targets.TargetTrueHeading = new TrueHeading(270);
        aircraft.Phases = new PhaseList();
        var phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        Assert.Null(aircraft.Targets.TargetTrueHeading);
    }

    // --- Tug moves: curvature-limited steering ---

    /// <summary>
    /// Pins: a push turning onto a facing starts moving at once, turns the nose only as it moves and never tighter
    /// than the routine radius, keeps the tail leading (push heading = the nose's reciprocal), and ends on the facing.
    /// </summary>
    [Fact]
    public void PushbackPhase_PushTurnTo_TurnsOnlyAsItMovesAndEndsOnTheFacing()
    {
        var aircraft = MakeGroundAircraft(heading: 0);
        var (phase, ctx) = StartPush(aircraft, TugMove.TurnTo(PushbackLegKind.Push, 90));
        double radiusFt = TugKinematics.TurnRadiusFt(aircraft.AircraftType, tight: false);

        var run = RunPush(aircraft, phase, ctx, 300);

        Assert.True(run.Completed, "the turn never completed");
        Assert.All(run.Ticks, t => AssertWithinCurvature(t, radiusFt));
        Assert.All(run.Ticks, t => Assert.True(t.PushGapDeg is <= 0.01, $"push heading {t.PushGapDeg:F2}° off the nose's reciprocal"));
        Assert.True(new TrueHeading(90).AbsAngleTo(aircraft.TrueHeading) <= 0.5, $"ended with the nose on {aircraft.TrueHeading.Degrees:F1}°");
    }

    /// <summary>Pins: a straight move completes only once it has covered its distance, and overshoots it by under a step.</summary>
    [Fact]
    public void PushbackPhase_Straight_CompletesOnlyAfterItsDistance()
    {
        var aircraft = MakeGroundAircraft(heading: 90);
        var start = aircraft.Position;
        var (phase, ctx) = StartPush(aircraft, TugMove.Straight(PushbackLegKind.Push, 50.0));

        Assert.False(phase.OnTick(ctx), "completed before moving");
        FlightPhysics.Update(aircraft, 1.0);
        var run = RunPush(aircraft, phase, ctx, 300);

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
        var aircraft = MakeGroundAircraft(lat: 37.620, lon: -122.380, heading: 90);
        var target = GeoMath.ProjectPoint(aircraft.Position, new TrueHeading(225), 400.0 / GeoMath.FeetPerNm);
        var (phase, ctx) = StartPush(aircraft, TugMove.ToPoint(PushbackLegKind.Push, target));
        double radiusFt = TugKinematics.TurnRadiusFt(aircraft.AircraftType, tight: false);

        var run = RunPush(aircraft, phase, ctx, 600);

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
        var aircraft = MakeGroundAircraft(heading: 180);
        aircraft.Phases = new PhaseList();
        var phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        // Pushback heading should be opposite of nose: 180 + 180 = 360 → 0
        double expected = (180.0 + 180.0) % 360.0;
        Assert.Equal(expected, aircraft.Ground.PushbackTrueHeading!.Value.Degrees, 1.0);
    }

    [Fact]
    public void PushbackPhase_FacingWest_PushesEast()
    {
        var aircraft = MakeGroundAircraft(heading: 270);
        aircraft.Phases = new PhaseList();
        var phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        Assert.Equal(90, aircraft.Ground.PushbackTrueHeading!.Value.Degrees, 1.0);
    }

    [Fact]
    public void PushbackPhase_FacingNorth_PushesSouth()
    {
        var aircraft = MakeGroundAircraft(heading: 360);
        aircraft.Phases = new PhaseList();
        var phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        Assert.Equal(180, aircraft.Ground.PushbackTrueHeading!.Value.Degrees, 1.0);
    }

    // --- TaxiingPhase with pre-cleared hold-short ---

    [Fact]
    public void TaxiingPhase_PreClearedHoldShort_SkipsHoldingShortPhase()
    {
        var layout = BuildCrossingLayout();
        var aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

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
        var ctx = MakeContext(aircraft, layout);
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
        var layout = BuildCrossingLayout();
        var aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

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
        var ctx = MakeContext(aircraft, layout);
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

        var restoredAircraft = MakeGroundAircraft(aircraft.Position.Lat, aircraft.Position.Lon, heading: 0);
        restoredAircraft.IndicatedAirspeed = aircraft.IndicatedAirspeed;
        restoredAircraft.Ground.AssignedTaxiRoute = route;
        restoredAircraft.Phases = new PhaseList();
        restoredAircraft.Phases.Add(restored);
        var restoredCtx = MakeContext(restoredAircraft, layout);

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
        var aircraft = MakeGroundAircraft();
        aircraft.Phases = new PhaseList();
        var phase = SimplePush(aircraft);
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        // Tick once to get moving
        FlightPhysics.Update(aircraft, 1.0);
        phase.OnTick(ctx);

        // Hold
        aircraft.Ground.Hold = HoldDirective.HoldPosition;
        phase.OnTick(ctx);

        Assert.Equal(0, ctx.Targets.TargetSpeed);
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
        var aircraft = MakeGroundAircraft(heading: 0);
        var (phase, ctx) = StartPush(aircraft, TugMove.TurnTo(PushbackLegKind.Push, 180) with { Tight = true });

        Assert.Equal(180.0, aircraft.Ground.PushbackTrueHeading!.Value.Degrees, 6);
        Assert.True(ctx.Targets.TargetSpeed > 0, "the push did not ask for speed");

        var run = RunPush(aircraft, phase, ctx, 5);

        Assert.True(run.Ticks.Sum(t => t.MovedFt) > 1.0, "the aircraft did not move");
        double radiusFt = TugKinematics.TurnRadiusFt(aircraft.AircraftType, tight: true);
        Assert.All(run.Ticks, t => AssertWithinCurvature(t, radiusFt));
    }

    /// <summary>Pins: a pull leaves the push heading null and moves the aircraft nose-first.</summary>
    [Fact]
    public void PushbackPhase_Pull_LeadsWithTheNose()
    {
        var aircraft = MakeGroundAircraft(heading: 30);
        var start = aircraft.Position;
        var target = GeoMath.ProjectPoint(start, new TrueHeading(30), 200.0 / GeoMath.FeetPerNm);
        var (phase, ctx) = StartPush(aircraft, TugMove.ToPoint(PushbackLegKind.Pull, target));

        Assert.Null(aircraft.Ground.PushbackTrueHeading);
        var run = RunPush(aircraft, phase, ctx, 10);

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
        var aircraft = MakeGroundAircraft(heading: 0);
        var start = aircraft.Position;
        var (phase, ctx) = StartPush(aircraft, TugMove.Straight(PushbackLegKind.Push, 50.0) with { DwellBefore = true });
        int dwellTicks = (int)PushbackPhase.DwellSeconds;

        var dwell = RunPush(aircraft, phase, ctx, dwellTicks);

        Assert.All(dwell.Ticks, t => Assert.Equal(0.0, t.MovedFt, 9));
        Assert.All(dwell.Ticks, t => Assert.Equal(0.0, t.NoseTurnDeg, 9));
        Assert.All(dwell.Ticks, t => Assert.Equal(0.0, t.PushGapDeg!.Value, 6));
        Assert.Equal(0, ctx.Targets.TargetSpeed ?? 0);
        Assert.Equal(start, aircraft.Position);

        var moving = RunPush(aircraft, phase, ctx, 1);
        Assert.True(moving.Ticks[0].MovedFt > 0.0, "the push did not move once the dwell was over");
    }

    // -------------------------------------------------------------------------
    // Issue #167 — adjust pushback face direction mid-pushback
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pins: the stand push-off of a single-goal push can be amended while it runs and not once it has completed;
    /// it is on the stand until it has moved half a fuselage; and it reports its planned end until it completes.
    /// </summary>
    [Fact]
    public void PushbackPhase_StandPushOff_AmendableAndOnTheStandUntilItHasMoved()
    {
        var aircraft = MakeGroundAircraft(heading: 0);
        var standPose = new TugPose(aircraft.Position, 0);
        var move = TugMove.Straight(PushbackLegKind.Push, 100.0);
        var phase = new PushbackPhase
        {
            Move = move,
            PlannedEnd = TugKinematics.Simulate(standPose, [move], aircraft.AircraftType, 1.0).End.Position,
            StartsAtStand = true,
            ContinuesIntoNextMove = false,
            Amendment = TugAmendment.For(TugGoal.Facing(90), standPose),
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        Assert.True(phase.CanAmend(aircraft));
        Assert.False(phase.HasLeftTheStand(aircraft));
        Assert.True(phase.TryGetPushLegEnd(aircraft, out var end));
        Assert.Equal(phase.PlannedEnd, end);

        var run = RunPush(aircraft, phase, ctx, 300);

        Assert.True(run.Completed);
        Assert.True(phase.HasLeftTheStand(aircraft), "a 100 ft push-off left a B738 on its stand");
        Assert.False(phase.CanAmend(aircraft), "a completed push-off was still amendable");
        Assert.False(phase.TryGetPushLegEnd(aircraft, out _), "a completed move still reported an end");
    }

    /// <summary>Pins: a move with no amendment is never amendable, and a move that is not the push-off has left the stand.</summary>
    [Fact]
    public void PushbackPhase_LaterMove_NotAmendableAndOffTheStand()
    {
        var aircraft = MakeGroundAircraft(heading: 0);
        var (phase, _) = StartPush(aircraft, TugMove.TurnTo(PushbackLegKind.Push, 90));

        Assert.False(phase.CanAmend(aircraft));
        Assert.True(phase.HasLeftTheStand(aircraft));
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
        var aircraft = MakeGroundAircraft();
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
        var layout = BuildCrossingLayout();
        var aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[0].Directed(layout.Nodes[0], layout.Nodes[1]) }],
            HoldShortPoints = [],
        };
        aircraft.Ground.AssignedTaxiRoute = route;
        aircraft.Phases = new PhaseList();
        var taxi = new TaxiingPhase();
        aircraft.Phases.Add(taxi);
        var ctx = MakeContext(aircraft, layout);
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
        var aircraft = MakeGroundAircraft();
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
        var layout = BuildCrossingLayout();
        var aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = "A", Edge = layout.Edges[0].Directed(layout.Nodes[0], layout.Nodes[1]) }],
            HoldShortPoints = [],
        };
        aircraft.Ground.AssignedTaxiRoute = route;
        aircraft.Phases = new PhaseList();
        var taxi = new TaxiingPhase();
        aircraft.Phases.Add(taxi);
        var ctx = MakeContext(aircraft, layout);
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
        var layout = BuildCrossingLayout();
        var aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);
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
        var ctx = MakeContext(aircraft, layout);
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
        var layout = BuildCrossingLayout();
        var aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);
        var phase = new RunwayHoldingPhase("28L/10R");

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        // A stale target from the phase that ran before the hold.
        aircraft.Targets.TargetSpeed = 30;
        phase.OnTick(ctx);

        Assert.Equal(0, aircraft.Targets.TargetSpeed);
        Assert.Equal(0, aircraft.IndicatedAirspeed);
    }
}
