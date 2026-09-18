using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// In-trail compression bug (QXE831 / SWA8154 too close on final): a faster arrival-generator
/// follower behind a slower leader on the same final closed the spawn spacing with no in-trail
/// speed management, collapsing separation below the 3 NM radar floor (the reporter saw 1.3 NM).
///
/// Generator arrivals spawn OnFinal at a fixed distance-based speed (farther out = faster), so a
/// follower is always faster than the closer-in, decelerating leader. The fix makes the arrival
/// generator act as a naive approach controller (TRACON), capping the follower's speed so the
/// stream holds its spacing down the final.
///
/// This test constructs the failing geometry live (a slow turboprop leader and a faster jet
/// follower on the OAK rwy 30 final, ~5 NM apart, both generator arrivals) and ticks the real
/// engine. Without the spacing manager the follower overruns the leader and busts 3 NM; with it
/// the gap holds. A live construction (not recording replay) is used deliberately: replaying a
/// recording re-injects spawns at their recorded positions, which the speed-managed stream no
/// longer matches, so replay cannot fairly exercise the manager.
/// </summary>
public class ArrivalGeneratorInTrailSpacingTests(ITestOutputHelper output)
{
    private const string ScenarioPath = "TestData/issue153-s2-oak-5-2-scenario.json";

    /// <summary>7110.65 §5-5-4 terminal radar separation floor (nm).</summary>
    private const double RadarFloorNm = 3.0;

    [Fact]
    public void FasterFollower_HoldsSpacing_BehindSlowerLeader()
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return;
        }

        var rwy30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30").Runway;
        var threshold = new LatLon(rwy30.ThresholdLatitude, rwy30.ThresholdLongitude);

        // Slow turboprop leader at 20 NM; faster jet follower 5 NM behind. Both generator
        // arrivals on the rwy 30 final.
        InjectArrival(engine, rwy30, "DAL1", "DH8D", 20.0, isGeneratorArrival: true);
        InjectArrival(engine, rwy30, "SWA2", "B739", 25.0, isGeneratorArrival: true);

        double minGapWhileBothOut = double.MaxValue;
        int minT = -1;

        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();

            var leader = engine.FindAircraft("DAL1");
            var follower = engine.FindAircraft("SWA2");
            if (leader is null || follower is null)
            {
                break;
            }

            double leaderDist = GeoMath.DistanceNm(leader.Position, threshold);
            double followerDist = GeoMath.DistanceNm(follower.Position, threshold);

            // Assert the radar floor only while both are still well out on the descent — the
            // last ~mile Vref-mismatch residual (a faster-Vref jet cannot fly below its own
            // Vref behind a slower one) is physically unavoidable and excluded.
            if (leaderDist < 5.0 || followerDist < 5.0)
            {
                continue;
            }

            double gap = GeoMath.DistanceNm(leader.Position, follower.Position);
            if (gap < minGapWhileBothOut)
            {
                minGapWhileBothOut = gap;
                minT = t;
            }
        }

        output.WriteLine($"Minimum in-trail separation (both >= 5 NM out): {minGapWhileBothOut:F2} NM at t={minT}s");

        Assert.True(
            minGapWhileBothOut >= RadarFloorNm,
            $"Faster follower busted the {RadarFloorNm:F0} NM radar floor behind a slower leader: {minGapWhileBothOut:F2} NM at t={minT}s"
        );
    }

    [Fact]
    public void ManualSpeedCommand_ReleasesAutoSpacing()
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return;
        }
        var rwy30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30").Runway;

        InjectArrival(engine, rwy30, "DAL1", "DH8D", 10.0, isGeneratorArrival: true);
        InjectArrival(engine, rwy30, "SWA2", "B739", 15.0, isGeneratorArrival: true);

        for (int t = 1; t <= 5; t++)
        {
            engine.TickOneSecond();
        }

        var follower = engine.FindAircraft("SWA2");
        Assert.NotNull(follower);
        Assert.NotNull(follower.Targets.SpeedCeiling); // manager engaged

        var result = engine.SendCommand("SWA2", "SPD 200");
        Assert.True(result.Success, result.Message);

        engine.TickOneSecond();
        follower = engine.FindAircraft("SWA2");
        Assert.NotNull(follower);

        Assert.True(follower.Approach.AutoSpacingReleased, "manual speed command should release auto-spacing");
        // The manager must not re-impose a ceiling once released; SPD cleared it.
        Assert.Null(follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// The student taking the track is a handoff, not a release: the receiving controller inherits the restrictions
    /// the aircraft is flying (§5-4-5.h.3, §5-4-6.c), so the simulated TRACON lets go of the follower's speed without
    /// giving it back and the arrival does not accelerate on the tick the handoff is accepted. The ceiling left
    /// standing is then an ordinary assigned speed with nobody re-stamping it — the manager would otherwise re-derive
    /// a different figure every tick as the gap changes, which is what the three quiet ticks here assert it does not
    /// — and it lapses the way any other does: the student's own speed command, or the §5-7-1.d / AIM 4-4-12.a.7
    /// window.
    /// </summary>
    [Fact]
    public void StudentTrackOwnership_LeavesTheSpacingCeilingStanding()
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return;
        }
        var rwy30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30").Runway;

        InjectArrival(engine, rwy30, "DAL1", "DH8D", 10.0, isGeneratorArrival: true);
        InjectArrival(engine, rwy30, "SWA2", "B739", 15.0, isGeneratorArrival: true);

        engine.TickOneSecond();
        var follower = engine.FindAircraft("SWA2");
        Assert.NotNull(follower);
        Assert.NotNull(follower.Targets.SpeedCeiling); // manager engaged
        double standing = follower.Targets.SpeedCeiling!.Value;

        // The student controller takes the track — the assigned speed goes with it.
        var student = TrackOwner.CreateNonNas("OAK_TWR");
        engine.Scenario.StudentPosition = student;
        follower.Track.Owner = TrackOwner.CreateNonNas("OAK_TWR");

        engine.TickOneSecond();
        follower = engine.FindAircraft("SWA2");
        Assert.NotNull(follower);

        Assert.True(follower.Approach.AutoSpacingReleased, "student ownership should end the manager's speed authority");
        Assert.Equal(standing, follower.Targets.SpeedCeiling!.Value);

        // Nobody re-derives the figure as the gap changes — it is the student's now, exactly as handed over.
        for (int t = 0; t < 3; t++)
        {
            engine.TickOneSecond();
        }
        follower = engine.FindAircraft("SWA2");
        Assert.NotNull(follower);
        Assert.Equal(standing, follower.Targets.SpeedCeiling!.Value);

        // The student's own speed command is what lapses it.
        var result = engine.SendCommand("SWA2", "SPD 200");
        Assert.True(result.Success, result.Message);

        engine.TickOneSecond();
        follower = engine.FindAircraft("SWA2");
        Assert.NotNull(follower);

        Assert.Null(follower.Targets.SpeedCeiling);
        Assert.True(follower.Approach.AutoSpacingReleased, "the latch is one-way");
    }

    /// <summary>
    /// The generator in-trail manager's own scope: it manages generator arrivals and nothing else. The stream is laid
    /// out with every pair far enough apart in time that the same-runway arrival protection — which does manage a
    /// non-generator follower, see
    /// <c>SameRunwayProtection_ManagesANonGeneratorFollower_WhenTheArrivalAheadWillNotClearInTime</c> — predicts no
    /// conflict anywhere, so a null ceiling on the manual aircraft genuinely means the generator manager kept its
    /// hands off rather than that some other pass happened not to fire. The surviving half of the original intent is
    /// still here: a non-generator aircraft serves as a leader for a generator follower.
    /// </summary>
    [Fact]
    public void GeneratorManager_ManagesOnlyGeneratorArrivals_BehindANonGeneratorLeader()
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return;
        }
        var rwy30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30").Runway;
        engine.Scenario.AutoArrivalSpacingOnOccupiedRunway = true;

        InjectArrival(engine, rwy30, "MANUAL1", "DH8D", 6.0, isGeneratorArrival: false);
        InjectArrival(engine, rwy30, "GEN2", "B739", 35.0, isGeneratorArrival: true);
        InjectArrival(engine, rwy30, "MANUAL3", "B739", 48.0, isGeneratorArrival: false);

        for (int t = 1; t <= 5; t++)
        {
            engine.TickOneSecond();
        }

        var manualLeader = engine.FindAircraft("MANUAL1");
        var genFollower = engine.FindAircraft("GEN2");
        var manualFollower = engine.FindAircraft("MANUAL3");
        Assert.NotNull(manualLeader);
        Assert.NotNull(genFollower);
        Assert.NotNull(manualFollower);

        foreach (var ac in new[] { manualLeader, genFollower, manualFollower })
        {
            output.WriteLine(
                $"{ac.Callsign}: {RunwayOccupancy.DistanceToAssignedThresholdNm(ac, rwy30, engine.World.GroundLayout):F1} nm, "
                    + $"eta {RunwayOccupancy.SecondsToAssignedThreshold(ac, rwy30, engine.World.GroundLayout):F0}s, "
                    + $"ceiling {ac.Targets.SpeedCeiling?.ToString("F0") ?? "(none)"}, "
                    + $"protection {ac.Approach.SameRunwayProtectionCeilingKts?.ToString("F0") ?? "(off)"}"
            );
        }

        // The layout is conflict-free by construction; if this trips, the spacing below has drifted and the null
        // assertions underneath it no longer prove anything about the generator manager.
        Assert.Null(genFollower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(manualFollower.Approach.SameRunwayProtectionCeilingKts);

        Assert.NotNull(genFollower.Targets.SpeedCeiling); // generator follower managed behind a manual leader
        Assert.Null(manualLeader.Targets.SpeedCeiling); // front of the stream, and not a generator arrival
        Assert.Null(manualFollower.Targets.SpeedCeiling); // the generator manager never manages a non-generator arrival
    }

    /// <summary>
    /// The other half of the contract the generator manager's scope leaves open: a non-generator follower delivered
    /// inside the leader's runway occupancy time <em>is</em> managed — by the same-runway arrival protection, which
    /// exists precisely because <see cref="SimulationEngine"/>'s generator spacing never touches a scenario-scripted
    /// arrival (§3-10-3.a.1, the interval that must exist between the two threshold crossings). Two manual arrivals
    /// 3 nm apart with the faster one behind: nothing in the generator path would touch either of them.
    /// </summary>
    [Fact]
    public void SameRunwayProtection_ManagesANonGeneratorFollower_WhenTheArrivalAheadWillNotClearInTime()
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return;
        }
        var rwy30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30").Runway;
        engine.Scenario.AutoArrivalSpacingOnOccupiedRunway = true;

        InjectArrival(engine, rwy30, "MANUAL1", "DH8D", 8.0, isGeneratorArrival: false);
        InjectArrival(engine, rwy30, "MANUAL3", "B739", 11.0, isGeneratorArrival: false);

        for (int t = 1; t <= 5; t++)
        {
            engine.TickOneSecond();
        }

        var manualLeader = engine.FindAircraft("MANUAL1");
        var manualFollower = engine.FindAircraft("MANUAL3");
        Assert.NotNull(manualLeader);
        Assert.NotNull(manualFollower);

        output.WriteLine(
            $"leader eta {RunwayOccupancy.SecondsToAssignedThreshold(manualLeader, rwy30, engine.World.GroundLayout):F0}s, "
                + $"follower eta {RunwayOccupancy.SecondsToAssignedThreshold(manualFollower, rwy30, engine.World.GroundLayout):F0}s, "
                + $"follower ceiling {manualFollower.Targets.SpeedCeiling?.ToString("F0") ?? "(none)"}"
        );

        Assert.NotNull(manualFollower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(manualFollower.Approach.SameRunwayProtectionCeilingKts, manualFollower.Targets.SpeedCeiling);
        Assert.Null(manualLeader.Targets.SpeedCeiling); // front of the stream — nobody ahead of it to be spaced from
    }

    [Fact]
    public void NewMarkerFields_SurviveSnapshotRoundTrip()
    {
        var aircraft = new AircraftState
        {
            Callsign = "SWA2",
            AircraftType = "B739",
            IsGeneratorArrival = true,
        };
        aircraft.Approach.AutoSpacingReleased = true;

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), groundLayout: null);

        Assert.True(restored.IsGeneratorArrival);
        Assert.True(restored.Approach.AutoSpacingReleased);
    }

    /// <summary>
    /// FDX7106 (B763, OAK 30) was capped behind a leader on short final and, once that leader landed, flew the ~25 nm
    /// left at the capped speed: <see cref="FinalApproachPhase"/> writes no speed target between spawn and its own
    /// deceleration stages, so nothing raised it again. The simulated approach controller, still owning the arrival,
    /// now gives it its normal speed back once the adjustment is no longer needed (§5-7-4.a, "resume normal speed"),
    /// and the phase's stages still run afterwards: configuration speed by the configuration reach gate, then the
    /// final-approach-speed stage.
    /// </summary>
    [Fact]
    public void LeaderLeavesTheStream_TheSlowedFollowerIsRestored_AndStillFliesTheStagedSlowdown()
    {
        var setup = SlowFollowerBehindLeader(leaderNm: 20.0, followerNm: 23.5);
        if (setup is null)
        {
            return;
        }
        var (engine, rwy30) = setup.Value;

        engine.World.RemoveAircraft(LeaderCallsign);

        int restoredAt = -1;
        for (int t = 1; (t <= 60) && (restoredAt < 0); t++)
        {
            engine.TickOneSecond();
            var follower = engine.FindAircraft(FollowerCallsign)!;
            if (Math.Abs(follower.IndicatedAirspeed - ScheduledKts(follower, rwy30)) <= SimulationEngine.SpeedRestoreDeadbandKts)
            {
                restoredAt = t;
            }
        }

        var restoredFollower = engine.FindAircraft(FollowerCallsign)!;
        output.WriteLine(
            $"restored at t={restoredAt}s: IAS {restoredFollower.IndicatedAirspeed:F0} kt, scheduled {ScheduledKts(restoredFollower, rwy30):F0} kt, "
                + $"{AlongFinalNm(restoredFollower, rwy30):F1} nm"
        );
        Assert.True(restoredAt > 0, "the follower was not given its scheduled speed back within 60 s of becoming the lead");

        var category = AircraftCategorization.Categorize(restoredFollower.AircraftType);
        double fas =
            AircraftPerformance.ApproachSpeed(restoredFollower.AircraftType, category)
            + AircraftPerformance.WindApproachAdditive(engine.World.Weather, rwy30.TrueHeading.Degrees);
        double configSpeed = fas * FinalApproachPhase.ConfigSpeedMultiplier;

        double? iasAtConfigGate = null;
        double? iasAtFasGate = null;
        double configGate = double.NaN;
        double fasGate = double.NaN;
        for (int t = 1; t <= 900; t++)
        {
            engine.TickOneSecond();
            var follower = engine.FindAircraft(FollowerCallsign);
            if (follower?.Phases?.CurrentPhase is not FinalApproachPhase phase)
            {
                break;
            }

            fasGate = follower.Approach.FinalApproachFasReachGateNm ?? FinalApproachPhase.FasReachGateNm;
            configGate = FinalApproachPhase.ConfigurationReachGateNm(fasGate);
            if ((iasAtConfigGate is null) && (phase.DistanceToThresholdNm <= configGate))
            {
                iasAtConfigGate = follower.IndicatedAirspeed;
            }

            if ((iasAtFasGate is null) && (phase.DistanceToThresholdNm <= fasGate))
            {
                iasAtFasGate = follower.IndicatedAirspeed;
                break;
            }
        }

        output.WriteLine(
            $"config gate {configGate:F1} nm: IAS {iasAtConfigGate?.ToString("F0") ?? "(never reached)"} kt vs config speed {configSpeed:F0} kt; "
                + $"FAS gate {fasGate:F1} nm: IAS {iasAtFasGate?.ToString("F0") ?? "(never reached)"} kt vs FAS {fas:F0} kt"
        );
        Assert.NotNull(iasAtConfigGate);
        Assert.True(
            iasAtConfigGate!.Value <= configSpeed + 2.0,
            $"restored follower reached the configuration reach gate at {iasAtConfigGate:F0} kt, above configuration speed {configSpeed:F0} kt"
        );
        Assert.NotNull(iasAtFasGate);
        Assert.True(
            iasAtFasGate!.Value <= fas + 2.0,
            $"restored follower reached the final-approach-speed reach gate at {iasAtFasGate:F0} kt, above FAS {fas:F0} kt"
        );
    }

    /// <summary>
    /// The follower half of the restore: an arrival the manager slowed behind its leader, whose ceiling then rises
    /// because the leader pulls away, is given that raised ceiling back while it still has a leader — it is not left at
    /// the slowed speed just because a ceiling only ever lowers the speed.
    /// </summary>
    [Fact]
    public void LeaderPullsAway_TheSlowedFollowerIsRestoredToItsRaisedCeiling()
    {
        var setup = SlowFollowerBehindLeader(leaderNm: 20.0, followerNm: 23.5);
        if (setup is null)
        {
            return;
        }
        var (engine, rwy30) = setup.Value;

        // Let the ceiling finish slowing the follower behind the close leader, so the raised ceiling below sits well
        // outside the restore deadband.
        for (int t = 1; t <= 20; t++)
        {
            engine.TickOneSecond();
        }

        PullLeaderAhead(engine, rwy30, nm: 8.0);
        double iasBefore = engine.FindAircraft(FollowerCallsign)!.IndicatedAirspeed;

        int restoredAt = -1;
        AircraftState follower = engine.FindAircraft(FollowerCallsign)!;
        for (int t = 1; (t <= 60) && (restoredAt < 0); t++)
        {
            engine.TickOneSecond();
            Assert.NotNull(engine.FindAircraft(LeaderCallsign)); // still a follower throughout
            follower = engine.FindAircraft(FollowerCallsign)!;
            if ((follower.Targets.SpeedCeiling is { } ceiling) && (follower.IndicatedAirspeed >= ceiling - SimulationEngine.SpeedRestoreDeadbandKts))
            {
                restoredAt = t;
            }
        }

        output.WriteLine(
            $"restored at t={restoredAt}s: IAS {iasBefore:F0} → {follower.IndicatedAirspeed:F0} kt, "
                + $"ceiling {follower.Targets.SpeedCeiling?.ToString("F0") ?? "(none)"} kt"
        );
        Assert.True(restoredAt > 0, "the follower was not given its raised ceiling back within 60 s");
        Assert.True(
            follower.IndicatedAirspeed > iasBefore + SimulationEngine.SpeedRestoreDeadbandKts,
            $"the follower did not accelerate: {iasBefore:F0} → {follower.IndicatedAirspeed:F0} kt"
        );
    }

    /// <summary>
    /// A handoff to the student in progress suppresses the restore for a follower too, not only for the lead of the
    /// stream: the ceiling rises when the leader pulls away, but the transferring controller changes no speed once the
    /// handoff is initiated (§5-4-5.b).
    /// </summary>
    [Fact]
    public void HandoffToStudentInProgress_OnAFollower_DoesNotRestore()
    {
        var setup = SlowFollowerBehindLeader(leaderNm: 20.0, followerNm: 23.5);
        if (setup is null)
        {
            return;
        }
        var (engine, rwy30) = setup.Value;

        var follower = engine.FindAircraft(FollowerCallsign)!;
        engine.Scenario!.StudentPosition = TrackOwner.CreateNonNas("OAK_TWR");
        follower.Track.Owner = TrackOwner.CreateNonNas("NCT_APP");
        var handoff = TrackEngine.ApplyHandoff(follower, engine.Scenario, identity: null, tcpCode: null, redirect: null);
        Assert.True(handoff.Success, handoff.Message);

        PullLeaderAhead(engine, rwy30, nm: 8.0);
        double iasBefore = follower.IndicatedAirspeed;

        double maxIas = iasBefore;
        double maxShortfall = 0;
        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
            Assert.NotNull(engine.FindAircraft(LeaderCallsign)); // still a follower throughout
            follower = engine.FindAircraft(FollowerCallsign)!;
            maxIas = Math.Max(maxIas, follower.IndicatedAirspeed);
            maxShortfall = Math.Max(maxShortfall, (follower.Targets.SpeedCeiling ?? 0) - follower.IndicatedAirspeed);
        }

        output.WriteLine($"IAS {iasBefore:F0} kt, max IAS after {maxIas:F0} kt, largest shortfall below the ceiling {maxShortfall:F0} kt");
        Assert.True(
            maxShortfall > SimulationEngine.SpeedRestoreDeadbandKts,
            "premise: the raised ceiling must leave the follower slow enough that a restore would otherwise be issued"
        );
        Assert.True(maxIas <= iasBefore + 1.0, $"the follower was re-accelerated during a pending handoff: {iasBefore:F0} → {maxIas:F0} kt");
    }

    /// <summary>
    /// The time-based allowance of <see cref="ArrivalSpacingManager.InTrailCeilingKts"/> assumes the leader closes on the
    /// threshold at no less than its Vref-bounded ground speed, which only holds for a leader on final. A leader that is in
    /// the stream only through its landing intent — here a C172 on a downwind inside the corridor, cleared to land on 30
    /// and flying away from the threshold — gives the follower the proportional ceiling alone.
    /// </summary>
    [Fact]
    public void LeaderOnADownwindInsideTheCorridor_FollowerGetsTheProportionalCeiling()
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return;
        }
        var gen30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30");
        var rwy30 = gen30.Runway;

        var threshold = new LatLon(rwy30.ThresholdLatitude, rwy30.ThresholdLongitude);
        var downwind = new TrueHeading((rwy30.TrueHeading.Degrees + 180.0) % 360.0);
        var leader = new AircraftState
        {
            Callsign = LeaderCallsign,
            AircraftType = "C172",
            Position = GeoMath.ProjectPoint(threshold, downwind, 2.0),
            TrueHeading = downwind,
            TrueTrack = downwind,
            Altitude = rwy30.ElevationFt + 1000,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
            Phases = new PhaseList { AssignedRunway = rwy30, LandingClearance = ClearanceType.ClearedToLand },
        };
        engine.World.AddAircraft(leader);
        var follower = InjectArrival(engine, rwy30, FollowerCallsign, "B739", 12.0, isGeneratorArrival: true);

        var leaderCategory = AircraftCategorization.Categorize(leader.AircraftType);
        var followerCategory = AircraftCategorization.Categorize(follower.AircraftType);
        double wake = WakeTurbulenceData.OnApproachWakeSeparationNm(leader.AircraftType, leaderCategory, follower.AircraftType, followerCategory);
        double target = Math.Max(gen30.Config.IntervalDistance, Math.Max(RadarFloorNm, wake));
        double expected = ArrivalSpacingManager.SpacingCeilingKts(
            leader.IndicatedAirspeed,
            AlongFinalNm(follower, rwy30) - AlongFinalNm(leader, rwy30),
            target,
            AircraftPerformance.ApproachSpeed(follower.AircraftType, followerCategory),
            ScheduledKts(follower, rwy30)
        );

        engine.TickOneSecond();

        follower = engine.FindAircraft(FollowerCallsign)!;
        output.WriteLine($"follower ceiling {follower.Targets.SpeedCeiling?.ToString("F1") ?? "(none)"} kt, proportional {expected:F1} kt");
        Assert.NotNull(follower.Targets.SpeedCeiling);
        Assert.Equal(expected, follower.Targets.SpeedCeiling!.Value, 3);
    }

    /// <summary>
    /// A leader on final through its track alone — 40° off the RWY 30 course inside the corridor band, not in
    /// <see cref="FinalApproachPhase"/> — closes on the threshold at its ground speed times the cosine of that offset, not
    /// at its ground speed. With the time-based allowance binding, the follower's ceiling is
    /// <see cref="ArrivalSpacingManager.InTrailCeilingKts"/> computed with that along-course closure, lower than with the
    /// raw ground speed.
    /// </summary>
    [Fact]
    public void LeaderInterceptingTheFinal_TheAllowanceUsesItsAlongCourseClosure()
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return;
        }
        var gen30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30");
        var rwy30 = gen30.Runway;

        var leaderCategory = AircraftCategorization.Categorize("B739");
        var (onCourse, leaderAltitudeFt) = AircraftInitializer.FinalApproachPoint(rwy30, leaderCategory, 6.0);
        var rightOfCourse = new TrueHeading((rwy30.TrueHeading.Degrees + 90.0) % 360.0);
        var intercept = new TrueHeading((rwy30.TrueHeading.Degrees + 320.0) % 360.0);
        engine.World.AddAircraft(
            new AircraftState
            {
                Callsign = LeaderCallsign,
                AircraftType = "B739",
                Position = GeoMath.ProjectPoint(onCourse, rightOfCourse, 1.0),
                TrueHeading = intercept,
                TrueTrack = intercept,
                Altitude = leaderAltitudeFt,
                IndicatedAirspeed = 150,
                IsOnGround = false,
                FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
                Phases = new PhaseList { AssignedRunway = rwy30 },
            }
        );
        InjectArrival(engine, rwy30, FollowerCallsign, "B739", 15.0, isGeneratorArrival: true);

        // The manager stamps the ceiling before physics, so the second tick's ceiling reads the state the first tick left.
        engine.TickOneSecond();
        var leader = engine.FindAircraft(LeaderCallsign)!;
        var follower = engine.FindAircraft(FollowerCallsign)!;
        double offsetDeg = leader.TrueTrack.AbsAngleTo(rwy30.TrueHeading);
        Assert.InRange(offsetDeg, 30.0, 45.0); // premise: on final through the track branch, well off the course

        var followerCategory = AircraftCategorization.Categorize(follower.AircraftType);
        double wake = WakeTurbulenceData.OnApproachWakeSeparationNm(leader.AircraftType, leaderCategory, follower.AircraftType, followerCategory);
        var alongCourse = new InTrailPair
        {
            LeaderIasKts = leader.IndicatedAirspeed,
            LeaderGsKts = leader.GroundSpeed * Math.Cos(offsetDeg * Math.PI / 180.0),
            LeaderVrefKts = AircraftPerformance.ApproachSpeed(leader.AircraftType, leaderCategory),
            LeaderDistanceNm = AlongFinalNm(leader, rwy30),
            FollowerDistanceNm = AlongFinalNm(follower, rwy30),
            FollowerIasKts = follower.IndicatedAirspeed,
            FollowerGsKts = follower.GroundSpeed,
            FollowerVrefKts = AircraftPerformance.ApproachSpeed(follower.AircraftType, followerCategory),
            FollowerScheduledKts = ScheduledKts(follower, rwy30),
            TargetNm = Math.Max(gen30.Config.IntervalDistance, Math.Max(RadarFloorNm, wake)),
        };
        double expected = ArrivalSpacingManager.InTrailCeilingKts(alongCourse);
        double withRawGs = ArrivalSpacingManager.InTrailCeilingKts(alongCourse with { LeaderGsKts = leader.GroundSpeed });
        output.WriteLine(
            $"leader {offsetDeg:F1}° off course, GS {leader.GroundSpeed:F0} kt: ceiling {expected:F1} kt along-course, {withRawGs:F1} kt raw"
        );
        Assert.True(expected < withRawGs - 1.0, $"premise: the allowance must bind ({expected:F1} kt vs {withRawGs:F1} kt)");

        engine.TickOneSecond();

        follower = engine.FindAircraft(FollowerCallsign)!;
        Assert.NotNull(follower.Targets.SpeedCeiling);
        Assert.Equal(expected, follower.Targets.SpeedCeiling!.Value, 3);
    }

    /// <summary>
    /// A handoff to the student in progress suppresses the restore from the moment it is initiated, not only once it is
    /// accepted (§5-4-5.b), and the ceiling standing on the new lead of the stream stays for the student to inherit
    /// (§5-4-6.c) instead of being released.
    /// </summary>
    [Fact]
    public void HandoffToStudentInProgress_HoldsTheStandingCeiling_AndDoesNotRestore()
    {
        var setup = SlowFollowerBehindLeader(leaderNm: 20.0, followerNm: 23.5);
        if (setup is null)
        {
            return;
        }
        var (engine, _) = setup.Value;

        var follower = engine.FindAircraft(FollowerCallsign)!;
        engine.Scenario!.StudentPosition = TrackOwner.CreateNonNas("OAK_TWR");
        follower.Track.Owner = TrackOwner.CreateNonNas("NCT_APP");
        var handoff = TrackEngine.ApplyHandoff(follower, engine.Scenario, identity: null, tcpCode: null, redirect: null);
        Assert.True(handoff.Success, handoff.Message);

        engine.TickOneSecond();
        follower = engine.FindAircraft(FollowerCallsign)!;
        Assert.NotNull(follower.Targets.SpeedCeiling); // still a follower: the manager keeps its ceiling while the handoff is pending
        double standing = follower.Targets.SpeedCeiling!.Value;
        double iasAtRemoval = follower.IndicatedAirspeed;

        engine.World.RemoveAircraft(LeaderCallsign);

        double maxIas = iasAtRemoval;
        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
            follower = engine.FindAircraft(FollowerCallsign)!;
            Assert.Equal((double?)standing, follower.Targets.SpeedCeiling);
            maxIas = Math.Max(maxIas, follower.IndicatedAirspeed);
        }

        output.WriteLine($"standing ceiling {standing:F0} kt, IAS at removal {iasAtRemoval:F0} kt, max IAS after {maxIas:F0} kt");
        Assert.True(maxIas <= iasAtRemoval + 1.0, $"the follower was re-accelerated during a pending handoff: {iasAtRemoval:F0} → {maxIas:F0} kt");
    }

    /// <summary>
    /// Inside the restore gate the phase is about to start its own deceleration stages, so an arrival that becomes the
    /// lead of the stream there is not sped up.
    /// </summary>
    [Fact]
    public void LeaderLeavesTheStream_InsideTheRestoreGate_NoRestore()
    {
        var setup = SlowFollowerBehindLeader(leaderNm: 10.0, followerNm: 13.5);
        if (setup is null)
        {
            return;
        }
        var (engine, rwy30) = setup.Value;

        var follower = engine.FindAircraft(FollowerCallsign)!;
        double gate = SimulationEngine.SpeedRestoreGateNm(AircraftCategorization.Categorize(follower.AircraftType), follower.Callsign);
        double distance = AlongFinalNm(follower, rwy30);
        double shortfall = ScheduledKts(follower, rwy30) - follower.IndicatedAirspeed;
        output.WriteLine($"follower at {distance:F1} nm (restore gate {gate:F1} nm), {shortfall:F0} kt below scheduled");
        Assert.True(distance < gate, $"premise: the follower must be inside the restore gate ({distance:F1} nm vs {gate:F1} nm)");
        Assert.True(shortfall > SimulationEngine.SpeedRestoreDeadbandKts, "premise: outside the gate this shortfall would be restored");

        double iasAtRemoval = follower.IndicatedAirspeed;
        engine.World.RemoveAircraft(LeaderCallsign);

        double maxIas = iasAtRemoval;
        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
            maxIas = Math.Max(maxIas, engine.FindAircraft(FollowerCallsign)!.IndicatedAirspeed);
        }

        Assert.True(maxIas <= iasAtRemoval + 1.0, $"the follower was re-accelerated inside the restore gate: {iasAtRemoval:F0} → {maxIas:F0} kt");
    }

    /// <summary>
    /// The same-runway protection pass owning the ceiling (<see cref="AircraftApproachState.SameRunwayProtectionCeilingKts"/>
    /// set) blocks the restore. That pass releases a front-of-stream arrival outside the 10-nm tower boundary, so it
    /// cannot own this one for real; the test re-applies the state the pass produces (its ownership marker and the
    /// matching <see cref="ControlTargets.SpeedCeiling"/>) before every tick, which is what the pass does each tick it owns
    /// an arrival.
    /// </summary>
    [Fact]
    public void LeaderLeavesTheStream_WhileSameRunwayProtectionOwnsTheCeiling_NoRestore()
    {
        var setup = SlowFollowerBehindLeader(leaderNm: 20.0, followerNm: 23.5);
        if (setup is null)
        {
            return;
        }
        var (engine, _) = setup.Value;

        var follower = engine.FindAircraft(FollowerCallsign)!;
        double protectionCeiling = follower.Targets.SpeedCeiling ?? follower.IndicatedAirspeed;
        double iasAtRemoval = follower.IndicatedAirspeed;
        engine.World.RemoveAircraft(LeaderCallsign);

        double maxIas = iasAtRemoval;
        for (int t = 1; t <= 60; t++)
        {
            follower = engine.FindAircraft(FollowerCallsign)!;
            follower.Approach.SameRunwayProtectionCeilingKts = protectionCeiling;
            follower.Targets.SpeedCeiling = protectionCeiling;
            engine.TickOneSecond();
            maxIas = Math.Max(maxIas, engine.FindAircraft(FollowerCallsign)!.IndicatedAirspeed);
        }

        Assert.True(
            maxIas <= iasAtRemoval + 1.0,
            $"the follower was re-accelerated while protection owned its ceiling: {iasAtRemoval:F0} → {maxIas:F0} kt"
        );
    }

    /// <summary>
    /// The far-gap ceiling (<see cref="ArrivalSpacingManager.InTrailCeilingKts"/>) wired through the engine: a follower
    /// 26 nm behind a leader on short final at ~144 kt is capped at its own scheduled speed, not at the leader's speed plus
    /// the proportional correction (164 kt).
    /// </summary>
    [Fact]
    public void FarBehindALeaderOnShortFinal_TheCeilingIsTheFollowersScheduledSpeed()
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return;
        }
        var rwy30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30").Runway;

        var leader = InjectArrival(engine, rwy30, LeaderCallsign, "B739", 2.3, isGeneratorArrival: true);
        leader.IndicatedAirspeed = 144.0;
        var follower = InjectArrival(engine, rwy30, FollowerCallsign, "B763", 2.3 + 26.0, isGeneratorArrival: true);
        double followerDistance = AlongFinalNm(follower, rwy30);
        double scheduled = ScheduledKts(follower, rwy30);

        engine.TickOneSecond();

        follower = engine.FindAircraft(FollowerCallsign)!;
        output.WriteLine(
            $"follower at {followerDistance:F1} nm: ceiling {follower.Targets.SpeedCeiling?.ToString("F1") ?? "(none)"} kt, "
                + $"scheduled {scheduled:F1} kt"
        );
        Assert.NotNull(follower.Targets.SpeedCeiling);
        Assert.Equal(scheduled, follower.Targets.SpeedCeiling!.Value, 3);
        Assert.True(follower.Targets.SpeedCeiling.Value > 144.0 + AirborneFollowHelper.MaxSpeedAdjustKts);
    }

    /// <summary>
    /// The simulated approach controller exists only while the student works a position below approach. With the
    /// student on APP the student is the one spacing the final, so a follower closer than the target gets no ceiling
    /// from a controller that is not there.
    /// </summary>
    [Fact]
    public void StudentOnApproach_CloseFollowerGetsNoCeiling()
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return;
        }
        var rwy30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30").Runway;
        engine.Scenario.StudentPositionType = "APP";

        InjectArrival(engine, rwy30, "DAL1", "DH8D", 10.0, isGeneratorArrival: true);
        InjectArrival(engine, rwy30, "SWA2", "B739", 13.0, isGeneratorArrival: true);

        for (int t = 1; t <= 5; t++)
        {
            engine.TickOneSecond();
        }

        var follower = engine.FindAircraft("SWA2");
        Assert.NotNull(follower);
        Assert.Null(follower.Targets.SpeedCeiling);
        Assert.False(follower.Approach.AutoSpacingReleased, "a closed gate is not a release: nothing latches");
    }

    /// <summary>
    /// A ceiling the simulated approach controller stamped while the student was on TWR is handed back on the tick the
    /// student becomes APP, without latching the one-way release, so the pass picks the arrival up again if the student
    /// goes back below approach.
    /// </summary>
    [Fact]
    public void StudentBecomesApproach_TheManagedCeilingIsReleasedOnThatTick()
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return;
        }
        var rwy30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30").Runway;
        engine.Scenario.StudentPositionType = "TWR";

        InjectArrival(engine, rwy30, "DAL1", "DH8D", 10.0, isGeneratorArrival: true);
        InjectArrival(engine, rwy30, "SWA2", "B739", 13.0, isGeneratorArrival: true);

        engine.TickOneSecond();
        var follower = engine.FindAircraft("SWA2")!;
        Assert.NotNull(follower.Targets.SpeedCeiling); // premise: the manager engaged while the student was on TWR

        engine.Scenario.StudentPositionType = "APP";
        engine.TickOneSecond();
        follower = engine.FindAircraft("SWA2")!;
        Assert.Null(follower.Targets.SpeedCeiling);
        Assert.False(follower.Approach.AutoSpacingReleased, "a closed gate is not a release: nothing latches");

        engine.Scenario.StudentPositionType = "TWR";
        engine.TickOneSecond();
        Assert.NotNull(engine.FindAircraft("SWA2")!.Targets.SpeedCeiling);
    }

    private const string LeaderCallsign = "DAL1";
    private const string FollowerCallsign = "FDX7106";

    /// <summary>
    /// A slow turboprop generator arrival ahead of a B763 generator arrival on the OAK 30 final, closer than the 5-nm
    /// target, ticked until the spacing manager has slowed the follower at least 15 kt below its scheduled speed.
    /// </summary>
    private (SimulationEngine Engine, RunwayInfo Runway)? SlowFollowerBehindLeader(double leaderNm, double followerNm)
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return null;
        }
        var rwy30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30").Runway;

        InjectArrival(engine, rwy30, LeaderCallsign, "DH8D", leaderNm, isGeneratorArrival: true);
        InjectArrival(engine, rwy30, FollowerCallsign, "B763", followerNm, isGeneratorArrival: true);

        bool slowed = false;
        for (int t = 1; (t <= 60) && !slowed; t++)
        {
            engine.TickOneSecond();
            var follower = engine.FindAircraft(FollowerCallsign)!;
            double shortfall = ScheduledKts(follower, rwy30) - follower.IndicatedAirspeed;
            if (shortfall >= 15.0)
            {
                slowed = true;
                output.WriteLine(
                    $"t={t}s: follower {follower.IndicatedAirspeed:F0} kt at {AlongFinalNm(follower, rwy30):F1} nm, "
                        + $"{shortfall:F0} kt below scheduled, ceiling {follower.Targets.SpeedCeiling?.ToString("F0") ?? "(none)"}"
                );
            }
        }

        Assert.True(slowed, "premise: the spacing manager never slowed the follower 15 kt below its scheduled speed");
        return (engine, rwy30);
    }

    /// <summary>
    /// The leader pulls away from the follower: moved <paramref name="nm"/> closer to the threshold, onto the glidepath
    /// there, so the gap opens past the in-trail target and the follower's ceiling rises.
    /// </summary>
    private static void PullLeaderAhead(SimulationEngine engine, RunwayInfo runway, double nm)
    {
        var leader = engine.FindAircraft(LeaderCallsign)!;
        var category = AircraftCategorization.Categorize(leader.AircraftType);
        var (position, altitudeFt) = AircraftInitializer.FinalApproachPoint(runway, category, AlongFinalNm(leader, runway) - nm);
        leader.Position = position;
        leader.Altitude = altitudeFt;
    }

    private static double ScheduledKts(AircraftState aircraft, RunwayInfo runway)
    {
        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        double vref = AircraftPerformance.ApproachSpeed(aircraft.AircraftType, category);
        return ArrivalSpacingManager.ScheduledFinalSpeedKts(aircraft.AircraftType, category, vref, aircraft.Callsign, AlongFinalNm(aircraft, runway));
    }

    private static double AlongFinalNm(AircraftState aircraft, RunwayInfo runway)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var outbound = new TrueHeading((runway.TrueHeading.Degrees + 180.0) % 360.0);
        return GeoMath.AlongTrackDistanceNm(aircraft.Position, threshold, outbound);
    }

    private SimulationEngine? LoadOakEngine()
    {
        if (!File.Exists(ScenarioPath))
        {
            return null;
        }
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return null;
        }

        var engine = new SimulationEngine(groundData);
        engine.LoadScenario(File.ReadAllText(ScenarioPath), rngSeed: 1, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        return engine.Scenario is null ? null : engine;
    }

    private static AircraftState InjectArrival(
        SimulationEngine engine,
        RunwayInfo runway,
        string callsign,
        string type,
        double distanceNm,
        bool isGeneratorArrival
    )
    {
        var category = AircraftCategorization.Categorize(type);
        var init = AircraftInitializer.InitializeOnFinal(runway, category, callsign, requestedDistanceNm: distanceNm, aircraftType: type);

        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = init.Position,
            TrueHeading = init.TrueHeading,
            Altitude = init.Altitude,
            IndicatedAirspeed = init.Speed,
            IsOnGround = false,
            IsGeneratorArrival = isGeneratorArrival,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
            Phases = init.Phases,
        };

        engine.World.AddAircraft(aircraft);
        return aircraft;
    }
}
