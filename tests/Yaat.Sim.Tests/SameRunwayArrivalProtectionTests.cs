using Xunit;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit cover for the pure same-runway arrival-protection math: both regimes of the required threshold interval, the
/// terminal radar and wake floors under it, the constant-deceleration vacate legs and the all-parts-past-the-bar term
/// they end at, the conflict predicate either side of its boundary, and the ceiling staying inside
/// [§5-7-3 floor, scheduled] without ever producing a NaN.
/// </summary>
public class SameRunwayArrivalProtectionTests
{
    private static SameRunwayArrivalProtection.LeaderRollout Rollout(
        double brakingLegNm,
        double entrySpeedKts,
        double exitSpeedKts,
        double steadyLegNm,
        double elapsedSeconds
    ) => new(brakingLegNm, entrySpeedKts, exitSpeedKts, steadyLegNm, elapsedSeconds);

    private static SameRunwayArrivalProtection.FollowerProfile Follower(
        AircraftCategory category,
        double groundSpeedKts,
        double distanceToThresholdNm,
        double vrefKts,
        double scheduledKts
    ) => new(category, groundSpeedKts, distanceToThresholdNm, vrefKts, scheduledKts);

    [Theory]
    [InlineData(AircraftCategory.Jet, 70.0)]
    [InlineData(AircraftCategory.Turboprop, 60.0)]
    [InlineData(AircraftCategory.Piston, 50.0)]
    [InlineData(AircraftCategory.Helicopter, 50.0)]
    public void AirborneLeader_UsesPerCategoryConstant(AircraftCategory leaderCategory, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, SameRunwayArrivalProtection.AirborneLeaderIntervalSeconds(leaderCategory), 3);
    }

    [Fact]
    public void RadarFloor_BindsOverAZeroWakeRequirement()
    {
        // WakeTurbulenceData returns 0 nm for a non-wake pair and leaves the radar minimum to its caller. §5-5-4.a.1
        // is 3 NM, which at a 144-kt Vref is 75 s — above the 70 s airborne jet constant, so the floor is what binds.
        double required = SameRunwayArrivalProtection.RequiredThresholdIntervalSeconds(
            AircraftCategory.Jet,
            rollout: null,
            wakeSeparationNm: 0.0,
            followerVrefKts: 144.0
        );

        Assert.Equal(75.0, required, 3);
        Assert.True(required > SameRunwayArrivalProtection.JetIntervalSeconds, "3 NM at 144 kt must outrun the 70 s jet constant");
    }

    [Fact]
    public void RadarFloor_WinsOverASmallerWakeRequirement()
    {
        // 1 nm of wake requirement at 140 kt is 26 s; the 3 NM radar minimum under it is 77 s.
        double required = SameRunwayArrivalProtection.RequiredThresholdIntervalSeconds(
            AircraftCategory.Turboprop,
            rollout: null,
            wakeSeparationNm: 1.0,
            followerVrefKts: 140.0
        );

        Assert.Equal(SameRunwayArrivalProtection.TerminalRadarFloorNm / 140.0 * 3600.0, required, 3);
    }

    [Fact]
    public void WakeFloor_WinsWhenLargerThanTheRadarFloor()
    {
        // 6 nm behind a super at a 120 kt Vref is 180 s — well past both the 3 NM radar floor and the jet constant.
        double required = SameRunwayArrivalProtection.RequiredThresholdIntervalSeconds(
            AircraftCategory.Jet,
            rollout: null,
            wakeSeparationNm: 6.0,
            followerVrefKts: 120.0
        );

        Assert.Equal(180.0, required, 3);
    }

    [Fact]
    public void LandedLeader_RefinesFromRolloutState()
    {
        // 0.5 nm of runway left braking from 60 kt to an 18 kt exit is flown at the 39 kt mean = 46.2 s, then 0.05 nm
        // of exit at 18 kt = 10 s, on top of 30 s already elapsed since the leader crossed the threshold.
        var rollout = Rollout(brakingLegNm: 0.5, entrySpeedKts: 60.0, exitSpeedKts: 18.0, steadyLegNm: 0.05, elapsedSeconds: 30.0);

        double required = SameRunwayArrivalProtection.RequiredThresholdIntervalSeconds(
            AircraftCategory.Jet,
            rollout,
            wakeSeparationNm: 0.0,
            followerVrefKts: 140.0
        );

        Assert.Equal(30.0 + (0.5 / 39.0 * 3600.0) + 10.0, required, 3);
    }

    [Fact]
    public void LandedLeader_OccupancyMayUndercutTheConstantButNeverTheRadarFloor()
    {
        // A jet nearly at its exit needs far less than the 70 s airborne constant — but the required interval is still
        // floored by 3 NM of radar separation, so the refinement can only ever tighten spacing down to that.
        var rollout = Rollout(brakingLegNm: 0.05, entrySpeedKts: 40.0, exitSpeedKts: 18.0, steadyLegNm: 0.02, elapsedSeconds: 5.0);

        double occupancy = 5.0 + SameRunwayArrivalProtection.SecondsToRunwayClear(rollout);
        double required = SameRunwayArrivalProtection.RequiredThresholdIntervalSeconds(
            AircraftCategory.Jet,
            rollout,
            wakeSeparationNm: 0.0,
            followerVrefKts: 140.0
        );

        Assert.True(occupancy < SameRunwayArrivalProtection.JetIntervalSeconds, $"occupancy {occupancy:F1}s should undercut the 70 s constant");
        Assert.Equal(SameRunwayArrivalProtection.TerminalRadarFloorNm / 140.0 * 3600.0, required, 3);
    }

    [Fact]
    public void WakeFloor_AlsoFloorsTheLandedRegime()
    {
        var rollout = Rollout(brakingLegNm: 0.05, entrySpeedKts: 40.0, exitSpeedKts: 18.0, steadyLegNm: 0.02, elapsedSeconds: 5.0);

        double required = SameRunwayArrivalProtection.RequiredThresholdIntervalSeconds(
            AircraftCategory.Jet,
            rollout,
            wakeSeparationNm: 5.0,
            followerVrefKts: 150.0
        );

        Assert.Equal(120.0, required, 3);
    }

    [Fact]
    public void RolloutLeg_IsFlownAtTheConstantDecelerationMean()
    {
        // LandingPhase brakes the rollout to the exit's turn-off speed by the branch point, so the leg is covered at
        // the mean of the two, not at the speed the leader is doing now: 0.5 nm at 74 kt braking to a 20 kt exit is
        // 38.3 s, where holding 74 kt the whole way reports 24.3 s — the ~1.6x optimism that read a blocker clear.
        var braking = Rollout(brakingLegNm: 0.5, entrySpeedKts: 74.0, exitSpeedKts: 20.0, steadyLegNm: 0.0, elapsedSeconds: 0.0);

        double atTheMean = SameRunwayArrivalProtection.SecondsToRunwayClear(braking);
        double atTheCurrentSpeed = 0.5 / 74.0 * 3600.0;

        Assert.Equal(0.5 / 47.0 * 3600.0, atTheMean, 3);
        Assert.True(atTheMean > 1.5 * atTheCurrentSpeed, $"{atTheMean:F1}s should be ~1.6x the {atTheCurrentSpeed:F1}s constant-speed model");
    }

    [Fact]
    public void RolloutLeg_NeverAcceleratesToMakeATurnOffSpeedAboveThePresentGroundSpeed()
    {
        // A leader already slower than its exit's turn-off speed will not speed up for it; taking a mean against the
        // higher figure would report it clear early.
        var slow = Rollout(brakingLegNm: 0.1, entrySpeedKts: 12.0, exitSpeedKts: 20.0, steadyLegNm: 0.0, elapsedSeconds: 0.0);

        Assert.Equal(0.1 / 12.0 * 3600.0, SameRunwayArrivalProtection.SecondsToRunwayClear(slow), 3);
    }

    [Fact]
    public void TailClearance_PastTheBarAddsTime()
    {
        // AIM 2-3-4.a.1 / AIM 4-3-20.b: an aircraft exiting is not clear until all parts of it have crossed the
        // holding position marking, so the vacate runs half a fuselage length past the hold-short node — the same
        // virtual target RunwayExitPhase taxis to.
        double tail = SameRunwayArrivalProtection.TailClearanceNm("B738");
        Assert.True(tail > 0.0, "a B738 has a fuselage to get across the bar");

        var toTheBar = Rollout(brakingLegNm: 0.0, entrySpeedKts: 30.0, exitSpeedKts: 20.0, steadyLegNm: 0.05, elapsedSeconds: 0.0);
        var allPartsAcross = toTheBar with { SteadyLegNm = 0.05 + tail };

        double added = SameRunwayArrivalProtection.SecondsToRunwayClear(allPartsAcross) - SameRunwayArrivalProtection.SecondsToRunwayClear(toTheBar);

        Assert.Equal(tail / 20.0 * 3600.0, added, 3);
        Assert.True(added > 0.0, "getting the tail across the bar cannot take zero time");
    }

    [Fact]
    public void SecondsToRunwayClear_IsTheTwoLegsWithoutTheElapsedTime()
    {
        // The interval carries the time already spent since the threshold crossing; the time-from-now question the
        // occupied-runway go-around asks must not.
        var rollout = Rollout(brakingLegNm: 0.5, entrySpeedKts: 60.0, exitSpeedKts: 18.0, steadyLegNm: 0.05, elapsedSeconds: 20.0);

        Assert.Equal((0.5 / 39.0 * 3600.0) + 10.0, SameRunwayArrivalProtection.SecondsToRunwayClear(rollout), 3);
    }

    [Fact]
    public void SecondsToRunwayClear_IsNeverForAnAircraftWithDistanceLeftAndNoSpeed()
    {
        // An aircraft stopped on the runway does not clear it at its present speed — the go-around's fail-closed
        // case, and the unbounded required interval the spacing pass floors its ceiling on. A stopped aircraft is not
        // decelerating either, so the planned turn-off speed must not resurrect it through the mean.
        var stopped = Rollout(brakingLegNm: 0.3, entrySpeedKts: 0.0, exitSpeedKts: 18.0, steadyLegNm: 0.05, elapsedSeconds: 10.0);

        Assert.Equal(double.PositiveInfinity, SameRunwayArrivalProtection.SecondsToRunwayClear(stopped));
    }

    [Fact]
    public void TryBuildRollout_IsNullForAnAirborneAircraft()
    {
        // Airborne, or landed with no exit resolved: an unknown, which both consumers must read as "not clear".
        var airborne = new AircraftState
        {
            Callsign = "TST1",
            AircraftType = "B738",
            IsOnGround = false,
        };

        Assert.Null(SameRunwayArrivalProtection.TryBuildRollout(airborne, elapsedSinceThresholdSeconds: 0.0));
    }

    [Fact]
    public void TryBuildRollout_IsNullForALandedAircraftWithNoResolvedExit()
    {
        var landed = new AircraftState
        {
            Callsign = "TST2",
            AircraftType = "B738",
            IsOnGround = true,
            IndicatedAirspeed = 60.0,
            Phases = new Phases.PhaseList(),
        };
        landed.Phases.Add(new Phases.Tower.LandingPhase());

        Assert.Null(SameRunwayArrivalProtection.TryBuildRollout(landed, elapsedSinceThresholdSeconds: 0.0));
    }

    [Theory]
    // §5-7-3.c.1: turbojet 210 kt, 170 within 20 flying miles of the threshold.
    [InlineData(AircraftCategory.Jet, 25.0, 210.0)]
    [InlineData(AircraftCategory.Jet, 20.0, 170.0)]
    [InlineData(AircraftCategory.Jet, 12.0, 170.0)]
    // §5-7-3.c.2: reciprocating and turboprop 200 kt, 150 within 20 miles.
    [InlineData(AircraftCategory.Turboprop, 25.0, 200.0)]
    [InlineData(AircraftCategory.Turboprop, 12.0, 150.0)]
    [InlineData(AircraftCategory.Piston, 25.0, 200.0)]
    [InlineData(AircraftCategory.Piston, 12.0, 150.0)]
    // §5-7-3.e: helicopters 60 kt, at any distance.
    [InlineData(AircraftCategory.Helicopter, 25.0, 60.0)]
    [InlineData(AircraftCategory.Helicopter, 4.0, 60.0)]
    public void RegulatoryFloor_IsTheCategoryAndDistanceFigure(AircraftCategory category, double distanceNm, double expectedKts)
    {
        Assert.Equal(expectedKts, SameRunwayArrivalProtection.RegulatoryFloorKts(category, distanceNm), 3);
    }

    [Fact]
    public void Ceiling_FloorsAtTheRegulatorySpeedRatherThanVref()
    {
        // A jet 12 nm out with a huge shortfall: §5-7-3.c.1.b stops the reduction at 170 kt, not at the 141 kt Vref,
        // which is only a valid speed with gear and landing flaps out.
        double ceiling = SameRunwayArrivalProtection.ProtectionCeilingKts(
            leaderIasKts: 150.0,
            shortfallSeconds: 120.0,
            Follower(AircraftCategory.Jet, groundSpeedKts: 210.0, distanceToThresholdNm: 12.0, vrefKts: 141.0, scheduledKts: 210.0)
        );

        Assert.Equal(170.0, ceiling, 3);
    }

    [Fact]
    public void Ceiling_UsesTheHigherFloorBeyondTwentyMiles()
    {
        // The same jet at 25 nm: §5-7-3.c.1.a is 210 kt, which here equals its scheduled speed, so there is no
        // reduction left to give at that range.
        double ceiling = SameRunwayArrivalProtection.ProtectionCeilingKts(
            leaderIasKts: 150.0,
            shortfallSeconds: 120.0,
            Follower(AircraftCategory.Jet, groundSpeedKts: 210.0, distanceToThresholdNm: 25.0, vrefKts: 141.0, scheduledKts: 210.0)
        );

        Assert.Equal(210.0, ceiling, 3);
    }

    [Fact]
    public void Ceiling_FloorsAHelicopterAtSixtyKnots()
    {
        double ceiling = SameRunwayArrivalProtection.ProtectionCeilingKts(
            leaderIasKts: 70.0,
            shortfallSeconds: 120.0,
            Follower(AircraftCategory.Helicopter, groundSpeedKts: 100.0, distanceToThresholdNm: 8.0, vrefKts: 45.0, scheduledKts: 100.0)
        );

        Assert.Equal(SameRunwayArrivalProtection.HelicopterFloorKts, ceiling, 3);
    }

    [Fact]
    public void Ceiling_DoesNotFloorASlowPistonAtTheRecipFigure()
    {
        // §5-7-3.f — "lower speeds may be assigned when operationally advantageous" — is what the Min(scheduled, …)
        // encodes: a 110-kt piston cannot be floored at the 150 kt §5-7-3.c.2.b figure it never flies.
        double ceiling = SameRunwayArrivalProtection.ProtectionCeilingKts(
            leaderIasKts: 95.0,
            shortfallSeconds: 60.0,
            Follower(AircraftCategory.Piston, groundSpeedKts: 110.0, distanceToThresholdNm: 12.0, vrefKts: 80.0, scheduledKts: 110.0)
        );

        Assert.Equal(110.0, ceiling, 3);
        Assert.True(ceiling < SameRunwayArrivalProtection.RecipFloorWithin20Kts, "a 110-kt piston must not be floored at 150 kt");
    }

    [Fact]
    public void Ceiling_NeverRisesAboveTheScheduledProfileSpeed()
    {
        // No shortfall and a fast leader: the follower is still capped at its own scheduled profile, never sped up.
        double ceiling = SameRunwayArrivalProtection.ProtectionCeilingKts(
            leaderIasKts: 280.0,
            shortfallSeconds: 0.0,
            Follower(AircraftCategory.Jet, groundSpeedKts: 210.0, distanceToThresholdNm: 12.0, vrefKts: 141.0, scheduledKts: 190.0)
        );

        Assert.Equal(190.0, ceiling, 3);
    }

    [Fact]
    public void Ceiling_ReducesBelowTheLeaderSpeedForAShortfall()
    {
        // 20 s of shortfall at 200 kt ground speed is 1.11 nm of gap error; the shared 25 kt/nm gain saturates at the
        // 20 kt maximum adjustment, so the ceiling sits 20 kt under the leader and clear of the 170 kt floor.
        double ceiling = SameRunwayArrivalProtection.ProtectionCeilingKts(
            leaderIasKts: 210.0,
            shortfallSeconds: 20.0,
            Follower(AircraftCategory.Jet, groundSpeedKts: 200.0, distanceToThresholdNm: 12.0, vrefKts: 141.0, scheduledKts: 230.0)
        );

        Assert.Equal(190.0, ceiling, 3);
    }

    [Fact]
    public void Ceiling_StaysInsideTheBandForAnUnboundedShortfall()
    {
        // A leader stopped on the runway yields an unbounded required interval; the ceiling must still be a finite
        // speed inside [floor, scheduled] rather than a NaN.
        double ceiling = SameRunwayArrivalProtection.ProtectionCeilingKts(
            leaderIasKts: 150.0,
            shortfallSeconds: double.PositiveInfinity,
            Follower(AircraftCategory.Jet, groundSpeedKts: 200.0, distanceToThresholdNm: 12.0, vrefKts: 141.0, scheduledKts: 190.0)
        );

        Assert.Equal(170.0, ceiling, 3);
    }

    [Fact]
    public void Ceiling_IsFiniteForAStoppedFollowerWithAnUnboundedShortfall()
    {
        // ∞ seconds of shortfall times a zero ground speed is NaN, which would propagate through the clamp into
        // Targets.SpeedCeiling and the speed integrator. A follower covering no distance contributes no gap error.
        double ceiling = SameRunwayArrivalProtection.ProtectionCeilingKts(
            leaderIasKts: 150.0,
            shortfallSeconds: double.PositiveInfinity,
            Follower(AircraftCategory.Jet, groundSpeedKts: 0.0, distanceToThresholdNm: 12.0, vrefKts: 141.0, scheduledKts: 190.0)
        );

        Assert.False(double.IsNaN(ceiling), "a zero-ground-speed follower must not produce a NaN ceiling");
        Assert.Equal(170.0, ceiling, 3);
    }

    [Theory]
    // §5-7-3.f: inside 10 nm the arrival is on the local controller's frequency, so the simulated tower may assign
    // final approach speed. The boundary is inclusive — 10.0 is inside, 10.1 is not.
    [InlineData(10.0, true)]
    [InlineData(10.1, false)]
    [InlineData(4.0, true)]
    public void TowerAuthority_BeginsAtTenMiles(double distanceToThresholdNm, bool expectedInside)
    {
        Assert.Equal(expectedInside, SameRunwayArrivalProtection.IsInsideTowerSpeedAuthority(distanceToThresholdNm));
    }

    [Fact]
    public void FinalApproachSpeed_IsVrefPlusTheWindAdditive_NeverBareVref()
    {
        // "Reduce to final approach speed" means Vapp — Vref plus the wind/gust additive, the same formula
        // FinalApproachPhase flies. Commanding bare Vref into a gust would put the aircraft below its own target.
        Assert.Equal(151.0, SameRunwayArrivalProtection.FinalApproachSpeedKts(vrefKts: 144.0, windAdditiveKts: 7.0), 3);
        Assert.Equal(144.0, SameRunwayArrivalProtection.FinalApproachSpeedKts(vrefKts: 144.0, windAdditiveKts: 0.0), 3);
    }

    [Fact]
    public void Ceiling_StillFloorsAtTheRegulatorySpeedOutsideTowerAuthority()
    {
        // The pure ceiling arithmetic is unchanged by the tower-authority constant: a jet at 12 nm — inside the
        // §5-7-3.c 20-mile boundary but outside the 10 nm the tower speaks at — still floors at the 170 kt figure and
        // not at its 144 kt Vref. Dropping to final approach speed is the engine's decision, not this function's.
        double ceiling = SameRunwayArrivalProtection.ProtectionCeilingKts(
            leaderIasKts: 126.0,
            shortfallSeconds: 120.0,
            Follower(AircraftCategory.Jet, groundSpeedKts: 200.0, distanceToThresholdNm: 12.0, vrefKts: 144.0, scheduledKts: 187.0)
        );

        Assert.Equal(170.0, ceiling, 3);
    }

    [Theory]
    // Leader 10 s out, 70 s required: the follower must not be inside t=80.
    [InlineData(79.0, true)]
    [InlineData(80.0, false)]
    [InlineData(81.0, false)]
    public void ConflictPredicate_TripsOnlyInsideTheRequiredInterval(double followerEtaSeconds, bool expectedConflict)
    {
        bool conflict = SameRunwayArrivalProtection.IsConflictPredicted(
            leaderThresholdEtaSeconds: 10.0,
            followerThresholdEtaSeconds: followerEtaSeconds,
            requiredIntervalSeconds: 70.0
        );

        Assert.Equal(expectedConflict, conflict);
    }

    [Fact]
    public void ConflictPredicate_HandlesALeaderAlreadyPastTheThreshold()
    {
        // Leader crossed 40 s ago and needs 70 s to clear: it is off in 30 s, so a follower 20 s out conflicts and
        // one 35 s out does not.
        Assert.True(SameRunwayArrivalProtection.IsConflictPredicted(-40.0, 20.0, 70.0));
        Assert.False(SameRunwayArrivalProtection.IsConflictPredicted(-40.0, 35.0, 70.0));
    }
}
