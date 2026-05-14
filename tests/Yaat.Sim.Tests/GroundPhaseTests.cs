using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;

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
        var phase = new PushbackPhase();
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
        var phase = new PushbackPhase();
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

    // --- Pushback speed recovery after conflict ---

    [Fact]
    public void PushbackPhase_SpeedRecoversAfterConflictClears()
    {
        var aircraft = MakeGroundAircraft(heading: 90);
        aircraft.Phases = new PhaseList();
        var phase = new PushbackPhase();
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
        var phase = new PushbackPhase();
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        Assert.Null(aircraft.Targets.TargetTrueHeading);
    }

    // --- Mode 2: pushback path curves with nose rotation ---

    [Fact]
    public void PushbackPhase_HeadingMode_CurvesPushbackWithNose()
    {
        var aircraft = MakeGroundAircraft(heading: 0);
        aircraft.Phases = new PhaseList();
        var phase = new PushbackPhase { TargetHeading = 90 };
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        // 90° diff > 20° threshold → alignment stage, no PushbackHeading yet
        Assert.Null(aircraft.Ground.PushbackTrueHeading);

        // Tick through alignment until aligned (nose rotates toward 90)
        for (int i = 0; i < 100; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            phase.OnTick(ctx);
            if (aircraft.Ground.PushbackTrueHeading is not null)
            {
                break;
            }
        }

        // Should now be aligned and pushing
        Assert.NotNull(aircraft.Ground.PushbackTrueHeading);
        Assert.True(aircraft.TrueHeading.Degrees > 60, "Nose should have rotated toward 90 during alignment");

        // After more ticks, pushback heading should track nose+180
        for (int i = 0; i < 5; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            phase.OnTick(ctx);
        }

        double expectedPush = (aircraft.TrueHeading.Degrees + 180.0) % 360.0;
        Assert.Equal(expectedPush, aircraft.Ground.PushbackTrueHeading!.Value.Degrees, 1.0);
    }

    // --- Mode 2: requires minimum distance even if heading already close ---

    [Fact]
    public void PushbackPhase_HeadingMode_RequiresMinimumDistance()
    {
        // Start facing 90, push with target 91 (already nearly there)
        var aircraft = MakeGroundAircraft(heading: 90);
        aircraft.Phases = new PhaseList();
        var phase = new PushbackPhase { TargetHeading = 91 };
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        // One tick: heading reached almost immediately, but distance hasn't been pushed
        FlightPhysics.Update(aircraft, 1.0);
        bool completed = phase.OnTick(ctx);
        Assert.False(completed, "Should not complete before minimum pushback distance");
    }

    // --- Mode 3: pushback arcs gradually instead of snapping ---

    [Fact]
    public void PushbackPhase_TargetedMode_ArcsGraduallyTowardTarget()
    {
        // Aircraft facing east, target is to the south.
        // Alignment heading = (180+180)%360 = 0 (nose north, tail south).
        // Aircraft heading = 90, diff = 90° > 20° → alignment stage first.
        var aircraft = MakeGroundAircraft(lat: 37.620, lon: -122.380, heading: 90);
        double targetLat = 37.619; // south
        double targetLon = -122.380;

        aircraft.Phases = new PhaseList();
        var phase = new PushbackPhase
        {
            TargetLatitude = targetLat,
            TargetLongitude = targetLon,
            TargetHeading = 270,
        };
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        // Should be in alignment stage — no PushbackHeading yet
        Assert.Null(aircraft.Ground.PushbackTrueHeading);

        // Tick through alignment until push stage begins
        for (int i = 0; i < 100; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            phase.OnTick(ctx);
            if (aircraft.Ground.PushbackTrueHeading is not null)
            {
                break;
            }
        }

        Assert.NotNull(aircraft.Ground.PushbackTrueHeading);
        double initialPushHdg = aircraft.Ground.PushbackTrueHeading!.Value.Degrees;

        // Now in push stage — after one tick, PushbackTrueHeading arcs gradually
        FlightPhysics.Update(aircraft, 1.0);
        phase.OnTick(ctx);

        double newPushHdg = aircraft.Ground.PushbackTrueHeading!.Value.Degrees;
        double changeDeg = Math.Abs(aircraft.Ground.PushbackTrueHeading.Value.SignedAngleTo(new TrueHeading(initialPushHdg)));
        double maxAllowed = CategoryPerformance.PushbackTurnRate(AircraftCategory.Jet) * 1.0 + 1.0;

        Assert.True(changeDeg <= maxAllowed, $"PushbackHeading changed {changeDeg:F1}° in 1s, max expected ~{maxAllowed:F0}°");
    }

    // --- PushbackTurnRate is slower than GroundTurnRate ---

    [Fact]
    public void PushbackTurnRate_IsSlowerThanGroundTurnRate()
    {
        foreach (var cat in new[] { AircraftCategory.Jet, AircraftCategory.Turboprop, AircraftCategory.Piston })
        {
            Assert.True(
                CategoryPerformance.PushbackTurnRate(cat) < CategoryPerformance.GroundTurnRate(cat),
                $"PushbackTurnRate should be slower than GroundTurnRate for {cat}"
            );
        }
    }

    // --- Pushback other directions ---

    [Fact]
    public void PushbackPhase_FacingSouth_PushesNorth()
    {
        var aircraft = MakeGroundAircraft(heading: 180);
        aircraft.Phases = new PhaseList();
        var phase = new PushbackPhase();
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
        var phase = new PushbackPhase();
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
        var phase = new PushbackPhase();
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
        var phase = new PushbackPhase();
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

    // --- Pushback alignment stage ---

    [Fact]
    public void PushbackPhase_TargetedMode_RotatesBeforePushing()
    {
        // Aircraft facing east, target to the north → alignment heading = (0+180)%360 = 180
        // Current heading = 90, diff = 90° > 20° → alignment stage
        var aircraft = MakeGroundAircraft(lat: 37.620, lon: -122.380, heading: 90);
        aircraft.Phases = new PhaseList();
        var phase = new PushbackPhase { TargetLatitude = 37.621, TargetLongitude = -122.380 };
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        // Alignment stage: no PushbackHeading, speed = 0
        Assert.Null(aircraft.Ground.PushbackTrueHeading);
        Assert.Equal(0, ctx.Targets.TargetSpeed);

        // Tick a few times — heading should change, position should not
        var startPos = aircraft.Position;
        double startHeading = aircraft.TrueHeading.Degrees;

        for (int i = 0; i < 5; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            phase.OnTick(ctx);
        }

        Assert.NotEqual(startHeading, aircraft.TrueHeading.Degrees);
        Assert.Equal(startPos.Lat, aircraft.Position.Lat, 6);
        Assert.Equal(startPos.Lon, aircraft.Position.Lon, 6);

        // Eventually transitions to push stage
        for (int i = 0; i < 100; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            phase.OnTick(ctx);
            if (aircraft.Ground.PushbackTrueHeading is not null)
            {
                break;
            }
        }

        Assert.NotNull(aircraft.Ground.PushbackTrueHeading);
        Assert.True(ctx.Targets.TargetSpeed > 0);
    }

    [Fact]
    public void PushbackPhase_HeadingMode_RotatesBeforePushing()
    {
        // Heading = 0, target heading = 90 → diff = 90° > 20° → alignment
        var aircraft = MakeGroundAircraft(heading: 0);
        aircraft.Phases = new PhaseList();
        var phase = new PushbackPhase { TargetHeading = 90 };
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        Assert.Null(aircraft.Ground.PushbackTrueHeading);
        Assert.Equal(0, ctx.Targets.TargetSpeed);

        // Position unchanged during alignment
        var startPos = aircraft.Position;

        for (int i = 0; i < 5; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            phase.OnTick(ctx);
        }

        Assert.Equal(startPos.Lat, aircraft.Position.Lat, 6);
        Assert.Equal(startPos.Lon, aircraft.Position.Lon, 6);
        Assert.Null(aircraft.Ground.PushbackTrueHeading);
    }

    [Fact]
    public void PushbackPhase_AlreadyAligned_SkipsRotation()
    {
        // Heading = 10, target heading = 0 → diff = 10° < 20° → skip alignment
        var aircraft = MakeGroundAircraft(heading: 10);
        aircraft.Phases = new PhaseList();
        var phase = new PushbackPhase { TargetHeading = 0 };
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        // PushbackHeading set immediately
        Assert.NotNull(aircraft.Ground.PushbackTrueHeading);
        Assert.True(ctx.Targets.TargetSpeed > 0);
    }

    [Fact]
    public void PushbackPhase_NoMovementDuringAlignment()
    {
        // Large misalignment: heading = 0, target heading = 180 → 180° diff
        var aircraft = MakeGroundAircraft(heading: 0);
        aircraft.Phases = new PhaseList();
        var phase = new PushbackPhase { TargetHeading = 180 };
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        var startPos = aircraft.Position;

        // Tick 10 times during alignment
        for (int i = 0; i < 10; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            phase.OnTick(ctx);
        }

        // Position must not change during alignment
        Assert.Equal(startPos.Lat, aircraft.Position.Lat, 6);
        Assert.Equal(startPos.Lon, aircraft.Position.Lon, 6);
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
        var phase = new CrossingRunwayPhase(approachNodeId: 1, targetNodeId: 2);

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
}
