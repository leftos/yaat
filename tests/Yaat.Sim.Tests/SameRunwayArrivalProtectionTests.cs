using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit cover for the pure same-runway arrival-protection math: both regimes of the required threshold interval, the
/// terminal radar and wake floors under it, the constant-deceleration vacate legs and the all-parts-past-the-bar term
/// they end at, the conflict predicate either side of its boundary, and the ceiling staying inside
/// [§5-7-3 floor, scheduled] without ever producing a NaN.
/// </summary>
public class SameRunwayArrivalProtectionTests
{
    /// <summary>
    /// A landing-regime rollout: the braking leg runs down to <paramref name="exitSpeedKts"/> and the steady leg
    /// that follows is flown at the same figure, the way <c>LandingPhase</c> brakes onto its exit's turn-off speed
    /// and then taxis the exit path at it.
    /// </summary>
    private static SameRunwayArrivalProtection.LeaderRollout Rollout(
        double brakingLegNm,
        double entrySpeedKts,
        double exitSpeedKts,
        double steadyLegNm,
        double elapsedSeconds
    ) => new(brakingLegNm, entrySpeedKts, exitSpeedKts, steadyLegNm, exitSpeedKts, elapsedSeconds);

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
    public void AirborneLeader_UsesPerCategoryConstant(AircraftCategory leaderCategory, double expectedSeconds) =>
        Assert.Equal(expectedSeconds, SameRunwayArrivalProtection.AirborneLeaderIntervalSeconds(leaderCategory), 3);

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
        SameRunwayArrivalProtection.LeaderRollout rollout = Rollout(
            brakingLegNm: 0.5,
            entrySpeedKts: 60.0,
            exitSpeedKts: 18.0,
            steadyLegNm: 0.05,
            elapsedSeconds: 30.0
        );

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
        SameRunwayArrivalProtection.LeaderRollout rollout = Rollout(
            brakingLegNm: 0.05,
            entrySpeedKts: 40.0,
            exitSpeedKts: 18.0,
            steadyLegNm: 0.02,
            elapsedSeconds: 5.0
        );

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
        SameRunwayArrivalProtection.LeaderRollout rollout = Rollout(
            brakingLegNm: 0.05,
            entrySpeedKts: 40.0,
            exitSpeedKts: 18.0,
            steadyLegNm: 0.02,
            elapsedSeconds: 5.0
        );

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
        SameRunwayArrivalProtection.LeaderRollout braking = Rollout(
            brakingLegNm: 0.5,
            entrySpeedKts: 74.0,
            exitSpeedKts: 20.0,
            steadyLegNm: 0.0,
            elapsedSeconds: 0.0
        );

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
        SameRunwayArrivalProtection.LeaderRollout slow = Rollout(
            brakingLegNm: 0.1,
            entrySpeedKts: 12.0,
            exitSpeedKts: 20.0,
            steadyLegNm: 0.0,
            elapsedSeconds: 0.0
        );

        Assert.Equal(0.1 / 12.0 * 3600.0, SameRunwayArrivalProtection.SecondsToRunwayClear(slow), 3);
    }

    /// <summary>
    /// A type the FAA database does not carry is sized by its wake category, the same resolver
    /// <see cref="RunwayExitPhase"/> uses: the A225 is CWT A (super), so its tail clears the bar half of the CWT
    /// resolver's 250 ft past the hold-short node, not half of a 60 ft guess.
    /// </summary>
    [Fact]
    public void TailClearance_UnknownType_UsesCwtFallbackLength()
    {
        TestVnasData.EnsureInitialized();
        Assert.Null(FaaAircraftDatabase.Get("A225"));
        Assert.Equal("A", WakeTurbulenceData.GetCwt("A225"));

        double tailFt = SameRunwayArrivalProtection.TailClearanceNm("A225") * GeoMath.FeetPerNm;

        Assert.Equal(AircraftLength.CwtFallbackLengthFt("A225") / 2.0, tailFt, 6);
    }

    [Fact]
    public void TailClearance_PastTheBarAddsTime()
    {
        // AIM 2-3-4.a.1 / AIM 4-3-20.b: an aircraft exiting is not clear until all parts of it have crossed the
        // holding position marking, so the vacate runs half a fuselage length past the hold-short node — the same
        // virtual target RunwayExitPhase taxis to.
        double tail = SameRunwayArrivalProtection.TailClearanceNm("B738");
        Assert.True(tail > 0.0, "a B738 has a fuselage to get across the bar");

        SameRunwayArrivalProtection.LeaderRollout toTheBar = Rollout(
            brakingLegNm: 0.0,
            entrySpeedKts: 30.0,
            exitSpeedKts: 20.0,
            steadyLegNm: 0.05,
            elapsedSeconds: 0.0
        );
        SameRunwayArrivalProtection.LeaderRollout allPartsAcross = toTheBar with { SteadyLegNm = 0.05 + tail };

        double added = SameRunwayArrivalProtection.SecondsToRunwayClear(allPartsAcross) - SameRunwayArrivalProtection.SecondsToRunwayClear(toTheBar);

        Assert.Equal(tail / 20.0 * 3600.0, added, 3);
        Assert.True(added > 0.0, "getting the tail across the bar cannot take zero time");
    }

    [Fact]
    public void SecondsToRunwayClear_IsTheTwoLegsWithoutTheElapsedTime()
    {
        // The interval carries the time already spent since the threshold crossing; the time-from-now question the
        // occupied-runway go-around asks must not.
        SameRunwayArrivalProtection.LeaderRollout rollout = Rollout(
            brakingLegNm: 0.5,
            entrySpeedKts: 60.0,
            exitSpeedKts: 18.0,
            steadyLegNm: 0.05,
            elapsedSeconds: 20.0
        );

        Assert.Equal((0.5 / 39.0 * 3600.0) + 10.0, SameRunwayArrivalProtection.SecondsToRunwayClear(rollout), 3);
    }

    [Fact]
    public void SecondsToRunwayClear_IsNeverForAnAircraftWithDistanceLeftAndNoSpeed()
    {
        // An aircraft stopped on the runway does not clear it at its present speed — the go-around's fail-closed
        // case, and the unbounded required interval the spacing pass floors its ceiling on. A stopped aircraft is not
        // decelerating either, so the planned turn-off speed must not resurrect it through the mean.
        SameRunwayArrivalProtection.LeaderRollout stopped = Rollout(
            brakingLegNm: 0.3,
            entrySpeedKts: 0.0,
            exitSpeedKts: 18.0,
            steadyLegNm: 0.05,
            elapsedSeconds: 10.0
        );

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
            Phases = new Yaat.Sim.Phases.PhaseList(),
        };
        landed.Phases.Add(new Yaat.Sim.Phases.Tower.LandingPhase());

        Assert.Null(SameRunwayArrivalProtection.TryBuildRollout(landed, elapsedSinceThresholdSeconds: 0.0));
    }

    // ---- The RunwayExitPhase regime: steady at the exit route's ceiling, then the stop ----

    /// <summary>The type both exiting-leader arms fly — a jet, so 30 kt of taxi ceiling and 5 kt/s of braking.</summary>
    private const string ExitingLeaderType = "B738";

    /// <summary>Taxi ceiling (kt) <c>RunwayExitPhase</c> caps a non-expediting jet exit at.</summary>
    private static readonly double ExitCeilingKts = CategoryPerformance.TaxiSpeed(AircraftCategory.Jet);

    /// <summary>Rate (kt/s) the exit navigator brakes to the hold-short stop at.</summary>
    private static readonly double ExitDecelKtsPerSec = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet);

    /// <summary>
    /// A leader on a real OAK 28R exit path, standing <paramref name="remainderFt"/> short of being clear — the
    /// distance measured to the virtual target half a fuselage past the hold-short node, the same target
    /// <see cref="RunwayExitPhase"/> taxis to — and doing <paramref name="groundSpeedKts"/>. Built through
    /// <c>RunwayExitPhase.FromSnapshot</c> against the real layout so the hold-short node is a real one. Null
    /// when the layout fixture is unavailable (silent skip).
    /// </summary>
    private static AircraftState? ExitingLeader(double remainderFt, double groundSpeedKts) =>
        ExitingLeader(ExitingLeaderType, remainderFt, groundSpeedKts);

    /// <summary><see cref="ExitingLeader(double, double)"/> for an aircraft of <paramref name="aircraftType"/>.</summary>
    private static AircraftState? ExitingLeader(string aircraftType, double remainderFt, double groundSpeedKts)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return null;
        }

        (GroundNode Branch, GroundNode HoldShort, string Taxiway)? pair = FindExitPair(layout, "28R");
        if (pair is null)
        {
            return null;
        }

        (GroundNode? branch, GroundNode? holdShort, string? taxiway) = pair.Value;
        double tailFt = SameRunwayArrivalProtection.TailClearanceNm(aircraftType) * GeoMath.FeetPerNm;
        double toHoldShortFt = Math.Max(0.0, remainderFt - tailFt);
        double bearingToBranch = GeoMath.BearingTo(holdShort.Position, branch.Position);
        (double lat, double lon) = GeoMath.ProjectPoint(holdShort.Position, new TrueHeading(bearingToBranch), toHoldShortFt / GeoMath.FeetPerNm);

        var dto = new RunwayExitPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 4.0,
            ReachedExitNode = true,
            ExitNodeId = holdShort.Id,
            ExitTaxiway = taxiway,
            RunwayId = "28R",
            ExitSpeed = 25.0,
            TimeSinceLastLog = 0.0,
            RunwayHeadingDeg = 281.0,
            ExitStateValue = (int)RunwayExitPhase.ExitState.FollowingExitPath,
            ExitWaypointNodeIds = [branch.Id, holdShort.Id],
            ExitWaypointIndex = 1,
        };

        var aircraft = new AircraftState
        {
            Callsign = "LEAD",
            AircraftType = aircraftType,
            AirportId = "OAK",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(bearingToBranch + 180.0),
            IsOnGround = true,
            IndicatedAirspeed = groundSpeedKts,
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(RunwayExitPhase.FromSnapshot(dto, layout));
        return aircraft;
    }

    /// <summary>
    /// A hold-short node on <paramref name="runwayId"/> plus a neighbour joined by a named taxiway edge, read
    /// from the layout at runtime — fillet node ids shift whenever the fixture is regenerated.
    /// </summary>
    private static (GroundNode Branch, GroundNode HoldShort, string Taxiway)? FindExitPair(AirportGroundLayout layout, string runwayId)
    {
        foreach (GroundNode holdShort in layout.GetRunwayHoldShortNodes(runwayId))
        {
            foreach (IGroundEdge edge in holdShort.Edges)
            {
                if (string.IsNullOrEmpty(edge.TaxiwayName))
                {
                    continue;
                }

                foreach (GroundNode node in edge.Nodes)
                {
                    if (node.Id != holdShort.Id)
                    {
                        return (node, holdShort, edge.TaxiwayName);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Seconds the steady-then-brake profile takes over <paramref name="remainderFt"/> at <paramref name="steadyKts"/>.</summary>
    private static double SteadyThenBrakeSeconds(double remainderFt, double steadyKts)
    {
        double stoppingFt = steadyKts * steadyKts / (2.0 * ExitDecelKtsPerSec) / 3600.0 * GeoMath.FeetPerNm;
        return ((remainderFt - stoppingFt) / GeoMath.FeetPerNm / steadyKts * 3600.0) + (steadyKts / ExitDecelKtsPerSec);
    }

    /// <summary>
    /// The prediction and the phase must agree on where "clear" is for a type the FAA database does not carry too.
    /// An A225 (CWT A) exiting OAK 28R is ticked until <see cref="RunwayExitPhase"/> ends; it must stop past the
    /// hold-short node by the <see cref="SameRunwayArrivalProtection.TailClearanceNm"/> the protection measures to,
    /// not by half of a flat 60 ft guess.
    /// </summary>
    [Fact]
    public void UnknownType_ExitStopMatchesProtectionTailClearance()
    {
        TestVnasData.EnsureInitialized();
        Assert.Null(FaaAircraftDatabase.Get("A225"));

        AircraftState? leader = ExitingLeader("A225", remainderFt: 600.0, groundSpeedKts: 15.0);
        if (leader is null)
        {
            return;
        }

        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 0,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        GroundNode holdShort = ((RunwayExitPhase)leader.Phases!.CurrentPhase!).TargetHoldShortNode!;
        engine.World.AddAircraft(leader);

        for (int t = 0; (t < 120) && (leader.Phases?.CurrentPhase is RunwayExitPhase); t++)
        {
            engine.TickOneSecond();
        }

        Assert.False(leader.Phases?.CurrentPhase is RunwayExitPhase, "the exit never completed");
        double pastBarFt = GeoMath.DistanceNm(holdShort.Position, leader.Position) * GeoMath.FeetPerNm;
        double expectedFt = SameRunwayArrivalProtection.TailClearanceNm("A225") * GeoMath.FeetPerNm;
        Assert.True(
            Math.Abs(pastBarFt - expectedFt) <= 15.0,
            $"the exit stopped {pastBarFt:F1} ft past the hold-short node; the protection clears it at {expectedFt:F1} ft"
        );
    }

    [Fact]
    public void ExitingLeader_IsPredictedSteadyThenBraking()
    {
        // SKW3398's measured SFO 28R exit: 1,286 ft still to run to the virtual target, entering the leg at the
        // 30-kt exit ceiling. Its navigator holds that ceiling to the braking point and only then brakes at 5 kt/s
        // — ~1,134 ft steady (22.4 s) plus a 152 ft / 6 s stop — so modelling the whole remainder as one braking
        // leg from the present speed (the entry/2 mean, 51 s here) is ~23 s pessimistic, and the go-around it feeds
        // fires for a leader that will be clear.
        AircraftState? leader = ExitingLeader(remainderFt: 1286.0, groundSpeedKts: ExitCeilingKts);
        if (leader is null)
        {
            return;
        }

        SameRunwayArrivalProtection.LeaderRollout? rollout = SameRunwayArrivalProtection.TryBuildRollout(leader, elapsedSinceThresholdSeconds: 0.0);
        Assert.NotNull(rollout);

        double predicted = SameRunwayArrivalProtection.SecondsToRunwayClear(rollout.Value);
        double expected = SteadyThenBrakeSeconds(1286.0, ExitCeilingKts);
        double oneBrakingLeg = 1286.0 / GeoMath.FeetPerNm / (ExitCeilingKts / 2.0) * 3600.0;

        Assert.Equal(expected, predicted, 1.0);
        Assert.True(
            predicted < oneBrakingLeg - 15.0,
            $"steady-then-brake predicted {predicted:F1}s where the one-braking-leg model says {oneBrakingLeg:F1}s; the "
                + $"measured vacate for this remainder is ~28 s, so the new figure must be far under the old one"
        );
    }

    [Fact]
    public void ExitingLeader_SlowerThanTaxiSpeed_HoldsItsOwnSpeed()
    {
        // The ceiling is a cap, not a target: a leader already below it will not accelerate to it, so the steady
        // leg is flown at the speed it is actually doing.
        AircraftState? leader = ExitingLeader(remainderFt: 1286.0, groundSpeedKts: 18.0);
        if (leader is null)
        {
            return;
        }

        SameRunwayArrivalProtection.LeaderRollout? rollout = SameRunwayArrivalProtection.TryBuildRollout(leader, elapsedSinceThresholdSeconds: 0.0);
        Assert.NotNull(rollout);

        double predicted = SameRunwayArrivalProtection.SecondsToRunwayClear(rollout.Value);

        Assert.Equal(SteadyThenBrakeSeconds(1286.0, 18.0), predicted, 1.0);
        Assert.True(
            predicted > SteadyThenBrakeSeconds(1286.0, ExitCeilingKts),
            $"an 18-kt leader must take longer than the {ExitCeilingKts:F0}-kt one, not be sped up to the ceiling"
        );
    }

    [Fact]
    public void ExitingLeader_AboveTheCeiling_BleedsDownBeforeHoldingIt()
    {
        // On the first ticks of an exit the aircraft is still carrying its turn-off speed above the taxi ceiling.
        // It bleeds down to the ceiling, holds it, and brakes for the stop — and at one constant rate the two braking
        // pieces together take exactly the time of one brake from the entry speed to zero, whatever sits between them.
        // Reading the stopping distance from the ceiling instead would grant a steady leg the aircraft cannot hold.
        const double entryKts = 37.0;
        AircraftState? leader = ExitingLeader(remainderFt: 1286.0, groundSpeedKts: entryKts);
        if (leader is null)
        {
            return;
        }

        SameRunwayArrivalProtection.LeaderRollout? rollout = SameRunwayArrivalProtection.TryBuildRollout(leader, elapsedSinceThresholdSeconds: 0.0);
        Assert.NotNull(rollout);

        double stoppingFt = entryKts * entryKts / (2.0 * ExitDecelKtsPerSec) / 3600.0 * GeoMath.FeetPerNm;
        double expected = ((1286.0 - stoppingFt) / GeoMath.FeetPerNm / ExitCeilingKts * 3600.0) + (entryKts / ExitDecelKtsPerSec);
        double predicted = SameRunwayArrivalProtection.SecondsToRunwayClear(rollout.Value);

        Assert.Equal(expected, predicted, 1.0);

        // The bleed-down is short, so entering hot moves the answer by well under a second either way — the
        // point is that it stays exact, not that it is slower or faster than entering at the ceiling.
        Assert.Equal(SteadyThenBrakeSeconds(1286.0, ExitCeilingKts), predicted, 1.0);
    }

    [Fact]
    public void ExitingLeader_ShorterThanStoppingDistance_IsAllBraking()
    {
        // Inside the stopping distance there is no steady leg left: the whole remainder is flown braking from the
        // speed the leader is doing now, at the constant-deceleration mean of that speed and zero.
        AircraftState? leader = ExitingLeader(remainderFt: 100.0, groundSpeedKts: ExitCeilingKts);
        if (leader is null)
        {
            return;
        }

        SameRunwayArrivalProtection.LeaderRollout? rollout = SameRunwayArrivalProtection.TryBuildRollout(leader, elapsedSinceThresholdSeconds: 0.0);
        Assert.NotNull(rollout);

        double stoppingFt = ExitCeilingKts * ExitCeilingKts / (2.0 * ExitDecelKtsPerSec) / 3600.0 * GeoMath.FeetPerNm;
        Assert.True(stoppingFt > 100.0, $"the fixture only exercises the short case if 100 ft is inside the {stoppingFt:F0} ft stopping distance");

        double predicted = SameRunwayArrivalProtection.SecondsToRunwayClear(rollout.Value);
        Assert.Equal(100.0 / GeoMath.FeetPerNm / (ExitCeilingKts / 2.0) * 3600.0, predicted, 0.5);
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
    public void RegulatoryFloor_IsTheCategoryAndDistanceFigure(AircraftCategory category, double distanceNm, double expectedKts) =>
        Assert.Equal(expectedKts, SameRunwayArrivalProtection.RegulatoryFloorKts(category, distanceNm), 3);

    /// <summary>
    /// §5-7-1.g — speed adjustments are expressed in 5-knot increments — so the spoken figure is the nearest multiple
    /// of 5, and a figure exactly between two of them rounds away from zero rather than to the even one .NET's default
    /// would pick: 182.5 is said as 185, not 180, and 187.5 as 190.
    /// </summary>
    [Theory]
    [InlineData(182.4, 180.0)]
    [InlineData(182.5, 185.0)]
    [InlineData(187.5, 190.0)]
    public void SpokenSpeed_IsTheNearestFiveKnots(double speedKts, double expectedKts) =>
        Assert.Equal(expectedKts, SameRunwayArrivalProtection.SpokenSpeedKts(speedKts), 3);

    /// <summary>
    /// The release line that restates the speed rather than resuming one is §5-7-2.a.1's "MAINTAIN (specific speed)
    /// KNOTS", so the figure it names obeys the same §5-7-1.g 5-knot increments every other spoken figure here does: a
    /// 212 kt ceiling handed back is said as 210, even though the ceiling the physics flies stays at 212.
    /// </summary>
    [Fact]
    public void ReleaseRestatement_SpeaksTheHandedBackSpeedInFiveKnotIncrements() =>
        Assert.Equal(
            "NCT_APP → UAL123: maintain 210 knots (in-trail spacing, 30)",
            SameRunwayArrivalProtection.ReleaseRestatementLine("NCT_APP", "UAL123", 212.0, "30")
        );

    [Fact]
    public void Ceiling_FloorsAtTheRegulatorySpeedRatherThanVref()
    {
        // A jet 12 nm out with a huge shortfall: §5-7-3.c.1(b) stops the reduction at 170 kt, not at the 141 kt Vref,
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
        // encodes: a 110-kt piston cannot be floored at the 150 kt §5-7-3.c.2(b) figure it never flies.
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
    public void TowerAuthority_BeginsAtTenMiles(double distanceToThresholdNm, bool expectedInside) =>
        Assert.Equal(expectedInside, SameRunwayArrivalProtection.IsInsideTowerSpeedAuthority(distanceToThresholdNm));

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
