using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A follower already on its base with the lead on final (or on base ahead of it) to the same runway projects its
/// rollout against where the lead will be then (<see cref="BaseFollowSpacing"/>): enough spacing keeps the base, a
/// small shortfall widens the base 30° away from the field down to the widen floor, and anything else breaks off to
/// pursuit with the pattern return. Real OAK navdata, two C172s on 28R right traffic.
/// </summary>
public class BaseFollowSpacingTests(ITestOutputHelper output)
{
    private const string Leader = "LEAD1";
    private const string Follower = "FOLL1";
    private const string TurnOutCall = "turning downwind for spacing behind LEAD1, request base turn.";

    /// <summary>Pattern spacing behind a piston lead (<see cref="AirborneFollowHelper.DesiredDistanceForLeader"/>).</summary>
    private const double PistonSpacingNm = 1.0;

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("BasePhase", LogLevel.Debug)
            .EnableCategory("BaseFollowSpacing", LogLevel.Debug)
            .EnableCategory("VfrFollowPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    private static RunwayInfo Oak(string designator) =>
        NavigationDatabase.Instance.GetRunway("KOAK", designator) ?? throw new InvalidOperationException($"KOAK {designator} missing from navdata");

    private static LatLon Threshold(RunwayInfo rwy) => new(rwy.ThresholdLatitude, rwy.ThresholdLongitude);

    /// <summary>Point <paramref name="alongNm"/> out the final and <paramref name="crossNm"/> to the right of the landing direction.</summary>
    private static LatLon OffFinal(RunwayInfo rwy, double alongNm, double crossNm)
    {
        LatLon onCenterline = GeoMath.ProjectPoint(Threshold(rwy), rwy.TrueHeading.ToReciprocal(), alongNm);
        if (Math.Abs(crossNm) < 1e-9)
        {
            return onCenterline;
        }

        TrueHeading perpendicular = crossNm > 0 ? rwy.TrueHeading + 90.0 : rwy.TrueHeading - 90.0;
        return GeoMath.ProjectPoint(onCenterline, perpendicular, Math.Abs(crossNm));
    }

    private static double AlongFinalNm(RunwayInfo rwy, LatLon position) =>
        GeoMath.AlongTrackDistanceNm(position, Threshold(rwy), rwy.TrueHeading.ToReciprocal());

    private static double CrossTrackNm(RunwayInfo rwy, LatLon position) =>
        Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, Threshold(rwy), rwy.TrueHeading));

    private static double BaseSpeedKt => AircraftPerformance.BaseSpeed("C172", AircraftCategory.Piston);

    private static double ApproachSpeedKt => AircraftPerformance.ApproachSpeed("C172", AircraftCategory.Piston);

    /// <summary>A VFR aircraft of <paramref name="aircraftType"/> at the speed its caller sets.</summary>
    private static AircraftState MakeVfr(string callsign, string aircraftType, LatLon position, TrueHeading heading, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KOAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(2000),
                CruiseSpeed = 110,
            },
        };

    /// <summary>
    /// Put <paramref name="ac"/> on the right-traffic circuit for <paramref name="rwy"/> starting at <paramref name="leg"/>,
    /// cleared to land.
    /// </summary>
    private static void PutOnCircuit(AircraftState ac, RunwayInfo rwy, PatternEntryLeg leg)
    {
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategorization.Categorize(ac.AircraftType),
            ac.AircraftType,
            windSpeedKt: 0,
            PatternDirection.Right,
            leg,
            touchAndGo: false,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: null,
            NavigationDatabase.Instance.GetRunways(rwy.AirportId),
            authoredRunway: null
        );
        ac.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = PatternDirection.Right,
            LandingClearance = ClearanceType.ClearedToLand,
            ClearedRunwayId = rwy.Designator,
        };
        foreach (Phase phase in circuit)
        {
            ac.Phases.Add(phase);
        }

        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
    }

    /// <summary>The lead established on the 28R final <paramref name="alongNm"/> out, on the glidepath at its approach speed.</summary>
    private static AircraftState AddFinalLead(SimulationEngine engine, double alongNm) => AddFinalLead(engine, "C172", alongNm, ApproachSpeedKt);

    /// <summary>
    /// A lead of <paramref name="aircraftType"/> established on the 28R final <paramref name="alongNm"/> out at
    /// <paramref name="iasKt"/>.
    /// </summary>
    private static AircraftState AddFinalLead(SimulationEngine engine, string aircraftType, double alongNm, double iasKt)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, aircraftType, OffFinal(rwy, alongNm, 0.0), rwy.TrueHeading, rwy.ElevationFt + (318.0 * alongNm));
        lead.IndicatedAirspeed = iasKt;
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternEntryLeg.Final);
        Assert.IsType<FinalApproachPhase>(lead.Phases!.CurrentPhase);
        return lead;
    }

    /// <summary>
    /// The follower on the right base to 28R at <paramref name="alongNm"/> out and <paramref name="crossNm"/> right of the
    /// centerline, following the lead.
    /// </summary>
    private static AircraftState AddBaseFollower(SimulationEngine engine, double alongNm, double crossNm) =>
        AddBaseFollower(engine, alongNm, crossNm, 1000);

    /// <summary>The base follower of <see cref="AddBaseFollower(SimulationEngine, double, double)"/> at <paramref name="altitudeFt"/>.</summary>
    private static AircraftState AddBaseFollower(SimulationEngine engine, double alongNm, double crossNm, double altitudeFt)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, "C172", OffFinal(rwy, alongNm, crossNm), rwy.TrueHeading - 90.0, altitudeFt);
        follower.IndicatedAirspeed = BaseSpeedKt;
        engine.World.AddAircraft(follower);
        PutOnCircuit(follower, rwy, PatternEntryLeg.Base);
        follower.Approach.FollowingCallsign = Leader;
        follower.Approach.HasReportedTrafficInSight = true;
        Assert.IsType<BasePhase>(follower.Phases!.CurrentPhase);
        return follower;
    }

    /// <summary>The lead on the right base to 28R at <paramref name="alongNm"/> out and <paramref name="crossNm"/> right of the centerline.</summary>
    private static AircraftState AddBaseLead(SimulationEngine engine, double alongNm, double crossNm)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, "C172", OffFinal(rwy, alongNm, crossNm), rwy.TrueHeading - 90.0, 1000);
        lead.IndicatedAirspeed = BaseSpeedKt;
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternEntryLeg.Base);
        Assert.IsType<BasePhase>(lead.Phases!.CurrentPhase);
        return lead;
    }

    /// <summary>
    /// The lead on the 28R extended centerline <paramref name="alongNm"/> out, flying an instrument approach's fix sequence
    /// (<see cref="ApproachNavigationPhase"/>) toward a fix 2 nm out: on the final by geometry, not by its phase.
    /// </summary>
    private static AircraftState AddApproachNavigationLead(SimulationEngine engine, double alongNm)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, "C172", OffFinal(rwy, alongNm, 0.0), rwy.TrueHeading, rwy.ElevationFt + (318.0 * alongNm));
        lead.IndicatedAirspeed = ApproachSpeedKt;
        engine.World.AddAircraft(lead);
        LatLon fix = OffFinal(rwy, 2.0, 0.0);
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        lead.Phases.Add(new ApproachNavigationPhase { Fixes = [new ApproachFix("FAF", fix.Lat, fix.Lon)] });
        lead.Phases.Start(CommandDispatcher.BuildMinimalContext(lead));
        Assert.IsType<ApproachNavigationPhase>(lead.Phases.CurrentPhase);
        return lead;
    }

    /// <summary>Runs the production physics + phase step at the engine's four sub-ticks a second (no scenario loaded).</summary>
    private static void TickSeconds(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds * 4; i++)
        {
            engine.TickPhysics(0.25);
        }
    }

    private static TrueHeading BaseHeading(BasePhase phase) => phase.Waypoints!.BaseHeading;

    private static double TargetHeadingDeg(AircraftState ac) =>
        ac.Targets.TargetTrueHeading?.Degrees ?? throw new InvalidOperationException($"{ac.Callsign} has no target heading");

    private static int TurnOutCalls(AircraftState follower) => follower.PendingWarnings.Count(w => w.Contains(TurnOutCall, StringComparison.Ordinal));

    private static int STurnCalls(AircraftState follower) => follower.PendingWarnings.Count(w => w.Contains("S-turning", StringComparison.Ordinal));

    /// <summary>
    /// Ticks until the follower rolls out on the final (its phase is <see cref="FinalApproachPhase"/>) and returns how far the
    /// lead is ahead of it along the final then; asserts the follower got there within <paramref name="maxSeconds"/>.
    /// </summary>
    private double TickToRolloutGapNm(SimulationEngine engine, AircraftState follower, AircraftState lead, int maxSeconds, Action? eachSubTick)
    {
        RunwayInfo rwy = Oak("28R");
        for (int i = 0; i < maxSeconds * 4; i++)
        {
            eachSubTick?.Invoke();
            engine.TickPhysics(0.25);
            if (i % 40 == 0)
            {
                output.WriteLine(
                    $"t={i / 4.0:F0}s {follower.Phases?.CurrentPhase?.Name} along={AlongFinalNm(rwy, follower.Position):F2} "
                        + $"cross={GeoMath.SignedCrossTrackDistanceNm(follower.Position, Threshold(rwy), rwy.TrueHeading):F2} "
                        + $"hdg={follower.TrueHeading.Degrees:F0} gs={follower.GroundSpeed:F0} | lead {lead.Phases?.CurrentPhase?.Name} "
                        + $"along={AlongFinalNm(rwy, lead.Position):F2} gs={lead.GroundSpeed:F0}"
                );
            }

            if (follower.Phases?.CurrentPhase is FinalApproachPhase)
            {
                double followerAlong = AlongFinalNm(rwy, follower.Position);
                double leadAlong = lead.IsOnGround ? 0.0 : AlongFinalNm(rwy, lead.Position);
                output.WriteLine(
                    $"rollout at t={i / 4.0:F1}s: follower {followerAlong:F2} nm out, lead {leadAlong:F2} nm out ({lead.Phases?.CurrentPhase?.Name})"
                );
                return followerAlong - leadAlong;
            }
        }

        Assert.Fail($"{Follower} never rolled out on the final within {maxSeconds} s (phase {follower.Phases?.CurrentPhase?.Name})");
        return 0.0;
    }

    // ─── Decisions ───

    /// <summary>
    /// The break-off end to end: pursuit of the lead with the pattern return to this circuit, a turn-out to the downwind
    /// heading with one call, never within 0.5 nm and 500 ft of the lead, and a later final behind the lead, at least the
    /// required spacing back and <see cref="VfrFollowPhase.MinFinalJoinDistNm"/> out, still cleared to land. Over the whole
    /// run, down to the touchdown, the one call stays the only one: the base the turn-out ends in never breaks off again.
    /// </summary>
    private void AssertTurnsOutAndTrails(SimulationEngine engine, AircraftState follower, AircraftState lead, double requiredNm)
    {
        RunwayInfo rwy = Oak("28R");
        TrueHeading downwind = rwy.TrueHeading.ToReciprocal();
        TickSeconds(engine, 1);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        FollowPatternReturn patternReturn = Assert.IsType<FollowPatternReturn>(pursuit.PatternReturn);
        Assert.True(patternReturn.FromBase);
        Assert.Equal("28R", patternReturn.Runway.Designator);

        bool headedDownwind = false;
        bool turnedBase = false;
        bool pursuitAgain = false;
        bool reachedFinal = false;
        double closestNm = double.MaxValue;
        for (int i = 0; (i < 1200 * 4) && !(reachedFinal && follower.IsOnGround); i++)
        {
            engine.TickPhysics(0.25);
            headedDownwind |= follower.TrueHeading.AbsAngleTo(downwind) < 10.0;
            turnedBase |= follower.Phases?.CurrentPhase is BasePhase;
            pursuitAgain |= turnedBase && (follower.Phases?.CurrentPhase is VfrFollowPhase);
            if (!lead.IsOnGround && (Math.Abs(follower.Altitude - lead.Altitude) < 500.0))
            {
                closestNm = Math.Min(closestNm, GeoMath.DistanceNm(follower.Position, lead.Position));
            }

            if (i % 40 == 0)
            {
                TraceFollowAndLead(i, follower, lead);
            }

            if (!reachedFinal && (follower.Phases?.CurrentPhase is FinalApproachPhase))
            {
                reachedFinal = true;
                AssertFinalBehindLead(follower, lead, requiredNm, closestNm, i);
            }
        }

        Assert.True(reachedFinal, $"{Follower} never reached the final (phase {follower.Phases?.CurrentPhase?.Name})");
        Assert.True(headedDownwind, "the follower never turned to the downwind heading");
        Assert.False(pursuitAgain, "the follower broke off again after the turn-out's base turn");
        Assert.Equal(1, TurnOutCalls(follower));
        Assert.Equal(0, STurnCalls(follower));
    }

    private void TraceFollowAndLead(int subTick, AircraftState follower, AircraftState lead)
    {
        RunwayInfo rwy = Oak("28R");
        output.WriteLine(
            $"t={subTick / 4.0:F0}s {follower.Phases?.CurrentPhase?.Name} along={AlongFinalNm(rwy, follower.Position):F2} "
                + $"cross={GeoMath.SignedCrossTrackDistanceNm(follower.Position, Threshold(rwy), rwy.TrueHeading):F2} "
                + $"hdg={follower.TrueHeading.Degrees:F0} gs={follower.GroundSpeed:F0} alt={follower.Altitude:F0} | lead "
                + $"{lead.Phases?.CurrentPhase?.Name} along={AlongFinalNm(rwy, lead.Position):F2} gs={lead.GroundSpeed:F0} "
                + $"ground={lead.IsOnGround}"
        );
    }

    /// <summary>At the follower's rollout on final: behind the lead by <paramref name="requiredNm"/>, far enough out, cleared to land.</summary>
    private void AssertFinalBehindLead(AircraftState follower, AircraftState lead, double requiredNm, double closestNm, int subTick)
    {
        RunwayInfo rwy = Oak("28R");
        double followerAlong = AlongFinalNm(rwy, follower.Position);
        double gapNm = followerAlong - (lead.IsOnGround ? 0.0 : AlongFinalNm(rwy, lead.Position));
        output.WriteLine(
            $"final at t={subTick / 4.0:F1}s: follower {followerAlong:F2} nm out, gap {gapNm:F2} nm, closest {closestNm:F2} nm, "
                + $"lead {lead.Phases?.CurrentPhase?.Name} ground={lead.IsOnGround}"
        );
        Assert.Equal(1, TurnOutCalls(follower));
        Assert.True(closestNm >= 0.5, $"came within {closestNm:F2} nm and 500 ft of the lead");
        Assert.True(followerAlong >= VfrFollowPhase.MinFinalJoinDistNm, $"joined final {followerAlong:F2} nm out");
        Assert.True(gapNm >= requiredNm, $"joined final {gapNm:F2} nm behind the lead, want at least {requiredNm:F2}");
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases!.LandingClearance);
    }

    [Fact]
    public void RightBaseFollower_RolloutAheadOfLeadOnFinal_BreaksOffTurnsDownwindAndTrails()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState lead = AddFinalLead(engine, 5.0);
        AircraftState follower = AddBaseFollower(engine, 3.0, 2.0);

        AssertTurnsOutAndTrails(engine, follower, lead, PistonSpacingNm);
    }

    [Fact]
    public void RightBaseFollower_WidenCannotBuildGap_BreaksOffTurnsDownwindAndTrails()
    {
        SimulationEngine engine = BuildEngine();
        // Behind the lead at rollout, but too close for a widen that has under 1 nm of base left before its floor.
        AircraftState lead = AddFinalLead(engine, 3.9);
        AircraftState follower = AddBaseFollower(engine, 3.0, 1.3);

        AssertTurnsOutAndTrails(engine, follower, lead, PistonSpacingNm);
    }

    [Fact]
    public void RightBaseFollower_WidenCannotBuildGap_Be20Lead_BreaksOffTurnsDownwindAndTrails()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState lead = AddFinalLead(engine, "BE20", 4.8, 109.0);
        AircraftState follower = AddBaseFollower(engine, 3.0, 1.3);
        double requiredNm = AirborneFollowHelper.DesiredDistanceForLeader(AircraftCategory.Turboprop);

        AssertTurnsOutAndTrails(engine, follower, lead, requiredNm);
    }

    /// <summary>
    /// A lead far too slow for the follower to stay behind by speed (a structural overtake) is left to the structural
    /// go-around, as before the base spacing existed: the follower never breaks off, and goes around once it closes in.
    /// </summary>
    [Fact]
    public void StructuralOvertake_DoesNotBreakOff_GoesAroundAsBefore()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState lead = AddFinalLead(engine, 2.0);
        AircraftState follower = AddBaseFollower(engine, 2.3, 1.0);

        bool brokeOff = false;
        for (int i = 0; (i < 120 * 4) && (follower.Phases?.CurrentPhase is not GoAroundPhase); i++)
        {
            lead.IndicatedAirspeed = 40;
            engine.TickPhysics(0.25);
            brokeOff |= follower.Phases?.CurrentPhase is VfrFollowPhase;
        }

        Assert.False(brokeOff, "a structural overtake must not break off the base");
        Assert.IsType<GoAroundPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(0, TurnOutCalls(follower));
    }

    [Fact]
    public void RightBaseFollower_RolloutSlightlyTooClose_WidensAndRollsOutBehind()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState lead = AddFinalLead(engine, 5.0);
        AircraftState follower = AddBaseFollower(engine, 3.0, 3.0);
        BasePhase basePhase = Assert.IsType<BasePhase>(follower.Phases!.CurrentPhase);

        TickSeconds(engine, 1);

        Assert.Same(basePhase, follower.Phases!.CurrentPhase);
        Assert.True(basePhase.FollowWidenActive);
        // Right traffic: 30° left of the base heading turns away from the field, out along the final.
        Assert.Equal((BaseHeading(basePhase) - BaseFollowSpacing.WidenOffBaseDeg).Degrees, TargetHeadingDeg(follower), 6);

        bool leftBase = false;
        double gapNm = TickToRolloutGapNm(
            engine,
            follower,
            lead,
            400,
            () => leftBase |= (follower.Phases?.CurrentPhase is not BasePhase) && (follower.Phases?.CurrentPhase is not FinalApproachPhase)
        );

        Assert.False(leftBase, "the widen must keep the base leg, never break off");
        Assert.Equal(0, TurnOutCalls(follower));
        Assert.True(gapNm >= PistonSpacingNm, $"rolled out {gapNm:F2} nm behind the lead, want at least {PistonSpacingNm:F1}");
    }

    [Fact]
    public void RightBaseFollower_RolloutWellBehind_KeepsBaseUnchanged()
    {
        SimulationEngine engine = BuildEngine();
        AddFinalLead(engine, 1.5);
        AircraftState follower = AddBaseFollower(engine, 3.0, 2.5);
        BasePhase basePhase = Assert.IsType<BasePhase>(follower.Phases!.CurrentPhase);

        TickSeconds(engine, 5);

        Assert.Same(basePhase, follower.Phases!.CurrentPhase);
        Assert.False(basePhase.FollowWidenActive);
        Assert.Equal(BaseHeading(basePhase).Degrees, follower.TrueHeading.Degrees, 0);
        Assert.Equal(0, TurnOutCalls(follower));
    }

    [Fact]
    public void RightBaseFollower_LeadSlowsMidBase_StartsWidenLater()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState lead = AddFinalLead(engine, 4.4);
        AircraftState follower = AddBaseFollower(engine, 3.0, 3.0);
        BasePhase basePhase = Assert.IsType<BasePhase>(follower.Phases!.CurrentPhase);

        TickSeconds(engine, 5);
        Assert.Same(basePhase, follower.Phases!.CurrentPhase);
        Assert.False(basePhase.FollowWidenActive);

        // The lead slows well below its approach speed and stays there: its projected progress along the final drops.
        double slowKt = ApproachSpeedKt - 5.0;
        int widenAfterSubTicks = -1;
        for (int i = 0; (i < 30 * 4) && (widenAfterSubTicks < 0); i++)
        {
            lead.IndicatedAirspeed = slowKt;
            engine.TickPhysics(0.25);
            widenAfterSubTicks = basePhase.FollowWidenActive ? i : -1;
        }

        output.WriteLine($"widen started {widenAfterSubTicks / 4.0:F2} s after the lead slowed to {slowKt:F0} kt");
        Assert.True(widenAfterSubTicks >= 0, "the follower never widened after the lead slowed");
        Assert.Same(basePhase, follower.Phases!.CurrentPhase);
        Assert.Equal(0, TurnOutCalls(follower));
    }

    /// <summary>
    /// A widen under way runs the break-off test again every tick: a lead that falls back mid-widen, leaving the rest of the
    /// widen short of the gap though the follower is still behind it, breaks the follower off into the turn-out at once,
    /// with the one call, rather than widening on down to the floor.
    /// </summary>
    [Fact]
    public void BaseWiden_CanNoLongerBuildTheGap_BreaksOffMidWiden()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddFinalLead(engine, 3.6);
        AircraftState follower = AddBaseFollower(engine, 3.0, 1.3);
        BasePhase basePhase = Assert.IsType<BasePhase>(follower.Phases!.CurrentPhase);
        TickSeconds(engine, 1);
        Assert.True(basePhase.FollowWidenActive);
        Assert.Same(basePhase, follower.Phases!.CurrentPhase);

        lead.Position = OffFinal(rwy, AlongFinalNm(rwy, lead.Position) + 0.2, 0.0);
        TickSeconds(engine, 1);

        double floorNm = BaseFollowSpacing.WidenFloorTurnRadii * BasePhase.TurnRadiusNm(follower.GroundSpeed, AircraftCategory.Piston);
        output.WriteLine($"broke off {CrossTrackNm(rwy, follower.Position):F2} nm off the centerline, floor {floorNm:F2} nm");
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.True(pursuit.TurningOut);
        Assert.True(CrossTrackNm(rwy, follower.Position) > floorNm + 0.5, "the widen ran on toward the floor before breaking off");
        Assert.Equal(1, TurnOutCalls(follower));
    }

    [Fact]
    public void StructuralOvertakeOnBase_StillGoesAround()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState lead = AddFinalLead(engine, 2.0);
        // A much slower lead just ahead: the follower's approach speed outruns it, so speed alone cannot keep spacing.
        lead.IndicatedAirspeed = 40;
        AircraftState follower = AddBaseFollower(engine, 2.3, 0.6);

        engine.TickPhysics(0.25);

        Assert.IsType<GoAroundPhase>(follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Equal(0, TurnOutCalls(follower));
    }

    /// <summary>
    /// The default piston circuit's base, 0.75 nm off the centerline, a little short of the spacing: the widen floor is 1.5
    /// turn radii, so the base still has room to widen, and the follower widens rather than turning out.
    /// </summary>
    [Fact]
    public void BaseWiden_OnADefaultPistonCircuit_WidensRatherThanTurningOut()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddFinalLead(engine, 2.2);
        AircraftState follower = AddBaseFollower(engine, 2.5, 0.75);
        BasePhase basePhase = Assert.IsType<BasePhase>(follower.Phases!.CurrentPhase);
        PatternWaypoints waypoints = basePhase.Waypoints!;
        Assert.Equal(0.75, CrossTrackNm(rwy, new LatLon(waypoints.DownwindStartLat, waypoints.DownwindStartLon)), 1);

        TickSeconds(engine, 1);

        output.WriteLine($"lead {AlongFinalNm(rwy, lead.Position):F2} nm out, follower phase {follower.Phases?.CurrentPhase?.Name}");
        Assert.Same(basePhase, follower.Phases!.CurrentPhase);
        Assert.True(basePhase.FollowWidenActive);
        Assert.Equal(0, TurnOutCalls(follower));
    }

    /// <summary>
    /// A widened base holds its altitude while it widens, then plans the descent again from where the widen ends, so the
    /// follower rolls out on the 3° glidepath of its now longer final.
    /// </summary>
    [Fact]
    public void BaseWiden_RollsOutOnTheGlidepathAfterTheWiden()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddFinalLead(engine, 5.25);
        AircraftState follower = AddBaseFollower(engine, 3.0, 3.0, 1300);
        BasePhase basePhase = Assert.IsType<BasePhase>(follower.Phases!.CurrentPhase);
        TickSeconds(engine, 1);
        Assert.True(basePhase.FollowWidenActive);

        TickToRolloutGapNm(engine, follower, lead, 400, null);

        double alongNm = AlongFinalNm(rwy, follower.Position);
        double glidepathFt = GlideSlopeGeometry.AltitudeAtDistance(alongNm, rwy.ElevationFt, AircraftCategory.Piston);
        output.WriteLine($"rolled out {alongNm:F2} nm out at {follower.Altitude:F0} ft, glidepath {glidepathFt:F0} ft");
        Assert.InRange(follower.Altitude, glidepathFt - 100.0, glidepathFt + 100.0);
    }

    /// <summary>
    /// A base follower farther than the turn-out range from the threshold, rolling out ahead of the lead: the break-off still
    /// turns it out to the downwind heading, with the one call; the range gates only a pursuit's own turn-out.
    /// </summary>
    [Fact]
    public void RightBaseFollower_BeyondTurnOutRange_BreaksOffAndTurnsOutWithOneCall()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AddFinalLead(engine, 7.5);
        AircraftState follower = AddBaseFollower(engine, 5.2, 1.6);
        Assert.True(GeoMath.DistanceNm(follower.Position, Threshold(rwy)) > VfrFollowPhase.TurnOutRangeNm);

        TickSeconds(engine, 1);

        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.True(pursuit.TurningOut);
        Assert.Equal(1, TurnOutCalls(follower));

        TickSeconds(engine, 30);
        Assert.Equal(1, TurnOutCalls(follower));
    }

    /// <summary>
    /// The lead on its base ahead, the follower's base farther out and rolling out ahead of it: the break-off's turn-out is
    /// taken before the pursuit's joins, so the follower turns out with one call instead of joining the lead's base.
    /// </summary>
    [Fact]
    public void LeadOnBaseAhead_FollowerBaseFartherOut_BreaksOffIntoTurnOutWithoutAJoin()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState lead = AddBaseLead(engine, 1.8, 2.0);
        AircraftState follower = AddBaseFollower(engine, 3.0, 1.0);

        bool joined = false;
        bool brokeOff = false;
        for (int i = 0; i < 20 * 4; i++)
        {
            engine.TickPhysics(0.25);
            brokeOff |= follower.Phases?.CurrentPhase is VfrFollowPhase;
            joined |= brokeOff && (follower.Phases?.CurrentPhase is not VfrFollowPhase);
            if (i % 20 == 0)
            {
                TraceFollowAndLead(i, follower, lead);
            }
        }

        Assert.True(brokeOff, "the follower never broke off its base");
        Assert.False(joined, "the pursuit joined a circuit instead of turning out");
        Assert.True(Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase).TurningOut);
        Assert.Equal(1, TurnOutCalls(follower));
    }

    /// <summary>
    /// A lead on the extended centerline flying an instrument approach's fix sequence (<see cref="ApproachNavigationPhase"/>)
    /// is on the final by geometry: the base follower rolling out ahead of it breaks off and turns out.
    /// </summary>
    [Fact]
    public void LeadOnFinalInApproachNavigation_IsInScope_BreaksOffAndTurnsOut()
    {
        SimulationEngine engine = BuildEngine();
        AddApproachNavigationLead(engine, 5.0);
        AircraftState follower = AddBaseFollower(engine, 3.0, 2.0);

        TickSeconds(engine, 1);

        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.True(pursuit.TurningOut);
        Assert.Equal(1, TurnOutCalls(follower));
    }

    /// <summary>
    /// The lead slowing on the final while the follower is already late on its base, under 0.4 nm from its final-turn point,
    /// and the lead not yet abeam: no room is left to turn out, so the follower goes around as the structural path does.
    /// </summary>
    [Fact]
    public void LateBreakOffNearTheCenterline_LeadNotAbeam_GoesAround()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState lead = AddFinalLead(engine, 3.3);
        AircraftState follower = AddBaseFollower(engine, 3.0, 0.55);

        bool brokeOff = false;
        for (int i = 0; (i < 10 * 4) && (follower.Phases?.CurrentPhase is not GoAroundPhase); i++)
        {
            lead.IndicatedAirspeed = ApproachSpeedKt - 5.0;
            engine.TickPhysics(0.25);
            brokeOff |= follower.Phases?.CurrentPhase is VfrFollowPhase;
        }

        Assert.False(brokeOff, "a late break-off must not turn out");
        Assert.IsType<GoAroundPhase>(follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Equal(0, TurnOutCalls(follower));
    }

    // ─── Widen gain arithmetic ───

    [Theory]
    [InlineData(1.27)]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void WidenGain_AtOrInsideTheFloor_IsZero(double crossTrackNm) =>
        Assert.Equal(0.0, BaseFollowSpacing.WidenGapGainNm(crossTrackNm, 1.27, 90.0, 60.0));

    /// <summary>One mile to close at 30° off the base: the final lengthens by tan 30° and the leg by (1/cos 30° − 1), √3 − 1 in all.</summary>
    [Fact]
    public void WidenGain_EqualSpeeds_IsTheLongerFinalPlusTheLongerLeg() =>
        Assert.Equal(Math.Sqrt(3.0) - 1.0, BaseFollowSpacing.WidenGapGainNm(2.27, 1.27, 80.0, 80.0), 9);

    [Fact]
    public void WidenGain_SlowerLead_ScalesOnlyTheLongerLeg()
    {
        // The longer final puts the rollout that much farther back whatever the speeds; the longer leg delays the follower by
        // its length / 90 kt, time the lead gains at 60 kt.
        double closingNm = 2.0;
        double longerFinalNm = closingNm * Math.Tan(Math.PI / 6.0);
        double longerLegNm = closingNm * ((1.0 / Math.Cos(Math.PI / 6.0)) - 1.0);
        Assert.Equal(longerFinalNm + (longerLegNm * 60.0 / 90.0), BaseFollowSpacing.WidenGapGainNm(3.0, 1.0, 90.0, 60.0), 9);
    }

    // ─── Parallel finals ───

    /// <summary>
    /// OAK 28L lies just left (south) of 28R: a widen toward one centerline comes within the margin of the other's final only
    /// from the side it lies on; from the other side the runway's own final lies between, however low the floor.
    /// </summary>
    [Theory]
    [InlineData("28R", -2.0, 0.45, true)]
    [InlineData("28R", 2.0, 0.45, false)]
    [InlineData("28L", 2.0, 0.45, true)]
    [InlineData("28L", -2.0, 0.45, false)]
    [InlineData("28R", 0.6, 0.2, false)]
    [InlineData("28L", -0.6, 0.2, false)]
    public void WidenMeetsParallelFinal_OnlyFromTheParallelsSide(string designator, double crossNm, double floorNm, bool expected)
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak(designator);

        Assert.Equal(expected, BaseFollowSpacing.WidenMeetsParallelFinal(OffFinal(rwy, 3.0, crossNm), rwy, Math.Abs(crossNm), floorNm));
    }

    // ─── Snapshot ───

    private static BasePhase RoundTrip(BasePhase phase)
    {
        string json = JsonSerializer.Serialize<PhaseDto>(phase.ToSnapshot(), RecordingJsonOptions.Default);
        BasePhaseDto dto = Assert.IsType<BasePhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        return BasePhase.FromSnapshot(dto);
    }

    [Fact]
    public void BasePhase_FollowWidenActive_SurvivesSnapshotRoundTrip()
    {
        SimulationEngine engine = BuildEngine();
        AddFinalLead(engine, 5.0);
        AircraftState follower = AddBaseFollower(engine, 3.0, 3.0);
        BasePhase basePhase = Assert.IsType<BasePhase>(follower.Phases!.CurrentPhase);
        TickSeconds(engine, 1);
        Assert.True(basePhase.FollowWidenActive);

        Assert.True(RoundTrip(basePhase).FollowWidenActive);

        var olderSnapshot = new BasePhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 1.0,
            ThresholdLat = 37.7,
            ThresholdLon = -122.2,
            FinalHeadingDeg = 292.0,
        };
        Assert.Null(olderSnapshot.FollowWidenActive);
        Assert.False(BasePhase.FromSnapshot(olderSnapshot).FollowWidenActive);
    }
}
