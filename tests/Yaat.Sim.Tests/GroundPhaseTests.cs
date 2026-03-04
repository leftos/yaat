using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

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
            Latitude = 37.620,
            Longitude = -122.380,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node1 = new GroundNode
        {
            Id = 1,
            Latitude = 37.621,
            Longitude = -122.380,
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rwyId,
        };
        var node2 = new GroundNode
        {
            Id = 2,
            Latitude = 37.622,
            Longitude = -122.380,
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rwyId,
        };
        var node3 = new GroundNode
        {
            Id = 3,
            Latitude = 37.623,
            Longitude = -122.380,
            Type = GroundNodeType.TaxiwayIntersection,
        };

        var edge01 = new GroundEdge
        {
            FromNodeId = 0,
            ToNodeId = 1,
            TaxiwayName = "A",
            DistanceNm = 0.06,
        };
        var edge12 = new GroundEdge
        {
            FromNodeId = 1,
            ToNodeId = 2,
            TaxiwayName = "RWY28L",
            DistanceNm = 0.06,
        };
        var edge23 = new GroundEdge
        {
            FromNodeId = 2,
            ToNodeId = 3,
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

        return layout;
    }

    private static AircraftState MakeGroundAircraft(double lat = 37.620, double lon = -122.380, double heading = 0)
    {
        return new AircraftState
        {
            Callsign = "TEST001",
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            Heading = heading,
            IsOnGround = true,
            Departure = "KSFO",
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
        Assert.True(aircraft.IsHeld);
    }

    [Fact]
    public void TryHoldPosition_Airborne_Fails()
    {
        var aircraft = MakeGroundAircraft();
        aircraft.IsOnGround = false;

        var result = GroundCommandHandler.TryHoldPosition(aircraft);

        Assert.False(result.Success);
        Assert.False(aircraft.IsHeld);
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
        Assert.True(aircraft.IsHeld);
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

        // PushbackHeading should be set (opposite of aircraft heading)
        Assert.NotNull(aircraft.PushbackHeading);

        // Tick once — phase sets TargetSpeed, FlightPhysics moves
        FlightPhysics.Update(aircraft, 1.0);
        phase.OnTick(ctx);
        Assert.True(aircraft.GroundSpeed > 0);

        // Hold and tick again
        aircraft.IsHeld = true;
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
        aircraft.IsHeld = true;
        phase.OnTick(ctx);
        Assert.Equal(0, aircraft.GroundSpeed);

        // Resume — phase OnTick reasserts TargetSpeed automatically
        aircraft.IsHeld = false;
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
                new TaxiRouteSegment
                {
                    FromNodeId = 0,
                    ToNodeId = 1,
                    TaxiwayName = "A",
                    Edge = layout.Edges[0],
                },
                new TaxiRouteSegment
                {
                    FromNodeId = 1,
                    ToNodeId = 2,
                    TaxiwayName = "RWY28L",
                    Edge = layout.Edges[1],
                },
                new TaxiRouteSegment
                {
                    FromNodeId = 2,
                    ToNodeId = 3,
                    TaxiwayName = "A",
                    Edge = layout.Edges[2],
                },
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
        aircraft.AssignedTaxiRoute = route;

        aircraft.Phases = new PhaseList();
        var taxiPhase = new TaxiingPhase();
        aircraft.Phases.Add(taxiPhase);
        var ctx = MakeContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        // Simulate arriving at node 1 (hold-short) by placing aircraft there
        aircraft.Latitude = layout.Nodes[1].Latitude;
        aircraft.Longitude = layout.Nodes[1].Longitude;

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
                new TaxiRouteSegment
                {
                    FromNodeId = 0,
                    ToNodeId = 1,
                    TaxiwayName = "A",
                    Edge = layout.Edges[0],
                },
                new TaxiRouteSegment
                {
                    FromNodeId = 1,
                    ToNodeId = 2,
                    TaxiwayName = "A",
                    Edge = layout.Edges[1],
                },
                new TaxiRouteSegment
                {
                    FromNodeId = 2,
                    ToNodeId = 3,
                    TaxiwayName = "A",
                    Edge = layout.Edges[2],
                },
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
        aircraft.AssignedTaxiRoute = route;

        aircraft.Phases = new PhaseList();
        var taxiPhase = new TaxiingPhase();
        aircraft.Phases.Add(taxiPhase);
        var ctx = MakeContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        // Place at hold-short node
        aircraft.Latitude = layout.Nodes[1].Latitude;
        aircraft.Longitude = layout.Nodes[1].Longitude;

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

        // Verify: HoldingShortPhase + TaxiingPhase (no CrossingRunwayPhase)
        var phases = aircraft.Phases.Phases;
        Assert.True(phases.Count >= 3, $"Expected at least 3 phases, got {phases.Count}");
        Assert.IsType<TaxiingPhase>(phases[0]);
        Assert.IsType<HoldingShortPhase>(phases[1]);
        Assert.IsType<TaxiingPhase>(phases[2]);

        // Ensure no CrossingRunwayPhase was inserted
        Assert.DoesNotContain(phases, p => p is CrossingRunwayPhase);
    }

    [Fact]
    public void TaxiingPhase_DestinationRunway_InsertsHoldOnly()
    {
        var layout = BuildCrossingLayout();
        var aircraft = MakeGroundAircraft(37.620, -122.380, heading: 0);

        // Route ends at hold-short node 1 (destination runway)
        var route = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment
                {
                    FromNodeId = 0,
                    ToNodeId = 1,
                    TaxiwayName = "A",
                    Edge = layout.Edges[0],
                },
            ],
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
        aircraft.AssignedTaxiRoute = route;

        aircraft.Phases = new PhaseList();
        var taxiPhase = new TaxiingPhase();
        aircraft.Phases.Add(taxiPhase);
        var ctx = MakeContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        // Place at hold-short node
        aircraft.Latitude = layout.Nodes[1].Latitude;
        aircraft.Longitude = layout.Nodes[1].Longitude;

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

        // Only HoldingShortPhase — no CrossingRunwayPhase, no TaxiingPhase
        var phases = aircraft.Phases.Phases;
        Assert.Equal(2, phases.Count);
        Assert.IsType<TaxiingPhase>(phases[0]);
        Assert.IsType<HoldingShortPhase>(phases[1]);
    }

    // --- FIX 4: FollowingPhase hold-short awareness ---

    [Fact]
    public void FollowingPhase_ApproachingHoldShort_AutoHolds()
    {
        var layout = BuildCrossingLayout();
        var target = MakeGroundAircraft(37.623, -122.380, heading: 0);
        target.Callsign = "LEAD01";
        target.GroundSpeed = 10;

        // Place follower just before the hold-short node (heading toward it)
        var aircraft = MakeGroundAircraft(37.6208, -122.380, heading: 0);
        aircraft.GroundSpeed = 10;
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
        target.GroundSpeed = 10;

        // Place follower near node 1 but heading AWAY from it (south)
        var aircraft = MakeGroundAircraft(37.6208, -122.380, heading: 180);
        aircraft.GroundSpeed = 10;
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
        target.GroundSpeed = 10;

        var aircraft = MakeGroundAircraft(37.6208, -122.380, heading: 0);
        aircraft.GroundSpeed = 10;
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
        double speedBefore = aircraft.GroundSpeed;

        // Simulate conflict: GroundSpeedLimit clamps to 0
        aircraft.GroundSpeedLimit = 0;
        FlightPhysics.Update(aircraft, 1.0);
        phase.OnTick(ctx);
        Assert.Equal(0, aircraft.GroundSpeed);

        // Conflict clears
        aircraft.GroundSpeedLimit = null;
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
        aircraft.Targets.TargetHeading = 270;
        aircraft.Phases = new PhaseList();
        var phase = new PushbackPhase();
        aircraft.Phases.Add(phase);
        var ctx = MakeContext(aircraft);
        aircraft.Phases.Start(ctx);

        Assert.Null(aircraft.Targets.TargetHeading);
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

        // Initial pushback heading is opposite of nose (= 180)
        Assert.Equal(180, aircraft.PushbackHeading!.Value, 1.0);

        // After a few ticks, nose starts turning right, pushback heading should follow
        for (int i = 0; i < 5; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            phase.OnTick(ctx);
        }

        // Nose should have turned toward 90 (from 0)
        Assert.True(aircraft.Heading > 0, "Nose should have started rotating");

        // PushbackHeading should be ~180° offset from current nose, not stuck at 180
        double expectedPush = (aircraft.Heading + 180.0) % 360.0;
        Assert.Equal(expectedPush, aircraft.PushbackHeading!.Value, 1.0);
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
        // Aircraft facing east, target is to the south
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

        // Initial PushbackHeading = 270 (opposite of 90)
        double initialPushHdg = aircraft.PushbackHeading!.Value;
        Assert.Equal(270, initialPushHdg, 1.0);

        // Bearing to target is ~180 (due south)
        double bearingToTarget = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, targetLat, targetLon);

        // After one tick, PushbackHeading should NOT have snapped to bearingToTarget.
        // It should have moved at most PushbackTurnRate degrees (5 deg/s × 1s = 5°).
        FlightPhysics.Update(aircraft, 1.0);
        phase.OnTick(ctx);

        double newPushHdg = aircraft.PushbackHeading!.Value;
        double changeDeg = Math.Abs(FlightPhysics.NormalizeAngle(newPushHdg - initialPushHdg));
        double maxAllowed = CategoryPerformance.PushbackTurnRate(AircraftCategory.Jet) * 1.0 + 1.0;

        Assert.True(changeDeg <= maxAllowed, $"PushbackHeading changed {changeDeg:F1}° in 1s, max expected ~{maxAllowed:F0}°");
        Assert.NotEqual(bearingToTarget, newPushHdg, 5.0);
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
        aircraft.IsHeld = true;
        phase.OnTick(ctx);

        Assert.Equal(0, ctx.Targets.TargetSpeed);
        Assert.Equal(0, aircraft.GroundSpeed);
    }
}
