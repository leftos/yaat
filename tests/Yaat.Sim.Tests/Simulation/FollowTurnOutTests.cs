using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A pursuing follower level with or ahead of a lead on base or final (or stuck alongside it at the excursion's offset cap)
/// turns out to the downwind heading with one call, holds a downwind offset band, and turns base once the lead has passed
/// abeam and the base-now path leaves the pattern spacing behind it. Real OAK navdata, C172s on 28R right traffic.
/// </summary>
public class FollowTurnOutTests(ITestOutputHelper output)
{
    private const string Leader = "LEAD1";
    private const string Follower = "FOLL1";
    private const string TurnOutCall = "turning downwind for spacing behind LEAD1, request base turn.";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("VfrFollowPhase", LogLevel.Debug)
            .EnableCategory("BaseFollowSpacing", LogLevel.Debug)
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

    private static double ApproachSpeedKt => AircraftPerformance.ApproachSpeed("C172", AircraftCategory.Piston);

    private static double PatternAltitudeFt(RunwayInfo rwy) =>
        rwy.AirportElevationFt + CategoryPerformance.PatternAltitudeAgl(AircraftCategory.Piston);

    private static AircraftState MakeVfr(string callsign, LatLon position, TrueHeading heading, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = ApproachSpeedKt,
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

    /// <summary>The lead on the 28R right-traffic final <paramref name="alongNm"/> out, on the glidepath at its approach speed.</summary>
    private static AircraftState AddFinalLead(SimulationEngine engine, double alongNm)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, alongNm, 0.0), rwy.TrueHeading, rwy.ElevationFt + (318.0 * alongNm));
        engine.World.AddAircraft(lead);
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategory.Piston,
            "C172",
            windSpeedKt: 0,
            PatternDirection.Right,
            PatternEntryLeg.Final,
            touchAndGo: false,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: null,
            NavigationDatabase.Instance.GetRunways(rwy.AirportId),
            authoredRunway: null
        );
        lead.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = PatternDirection.Right,
            LandingClearance = ClearanceType.ClearedToLand,
            ClearedRunwayId = rwy.Designator,
        };
        foreach (Phase phase in circuit)
        {
            lead.Phases.Add(phase);
        }

        lead.Phases.Start(CommandDispatcher.BuildMinimalContext(lead));
        Assert.IsType<FinalApproachPhase>(lead.Phases.CurrentPhase);
        return lead;
    }

    /// <summary>
    /// A follower pursuing the lead (<see cref="VfrFollowPhase"/>) at <paramref name="alongNm"/> out and
    /// <paramref name="crossNm"/> right of the 28R centerline, flying <paramref name="heading"/>, cleared to land, returning
    /// to the 28R right-traffic circuit when the follow ends.
    /// </summary>
    private static AircraftState AddPursuer(SimulationEngine engine, double alongNm, double crossNm, TrueHeading heading, double altitudeFt)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, alongNm, crossNm), heading, altitudeFt);
        engine.World.AddAircraft(follower);
        follower.Phases = new PhaseList { LandingClearance = ClearanceType.ClearedToLand, ClearedRunwayId = rwy.Designator };
        follower.Approach.HasReportedTrafficInSight = true;
        CommandDispatcher.InstallVfrFollowPhase(
            follower,
            Leader,
            new FollowPatternReturn(rwy, PatternDirection.Right, PatternAltitudeFt(rwy), false)
        );
        return follower;
    }

    private static void TickSeconds(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds * 4; i++)
        {
            engine.TickPhysics(0.25);
        }
    }

    private static int TurnOutCalls(AircraftState follower) => follower.PendingWarnings.Count(w => w.Contains(TurnOutCall, StringComparison.Ordinal));

    private void Trace(int subTick, AircraftState follower, AircraftState lead)
    {
        RunwayInfo rwy = Oak("28R");
        output.WriteLine(
            $"t={subTick / 4.0:F0}s {follower.Phases?.CurrentPhase?.Name} along={AlongFinalNm(rwy, follower.Position):F2} "
                + $"cross={GeoMath.SignedCrossTrackDistanceNm(follower.Position, Threshold(rwy), rwy.TrueHeading):F2} "
                + $"hdg={follower.TrueHeading.Degrees:F0} gs={follower.GroundSpeed:F0} | lead {lead.Phases?.CurrentPhase?.Name} "
                + $"along={AlongFinalNm(rwy, lead.Position):F2} gs={lead.GroundSpeed:F0}"
        );
    }

    // ─── Trigger ───

    /// <summary>Alongside the lead on final and nearer the threshold: the follower turns outward to the downwind heading, once.</summary>
    [Fact]
    public void PursuitAheadOfLeadOnFinal_TurnsOutToTheDownwindWithOneCall()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AddFinalLead(engine, 3.0);
        AircraftState follower = AddPursuer(engine, 2.5, 1.2, rwy.TrueHeading, 1000);

        TickSeconds(engine, 1);

        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(1, TurnOutCalls(follower));
        Assert.Equal(rwy.TrueHeading.ToReciprocal().Degrees, follower.Targets.TargetTrueHeading!.Value.Degrees, 1);
        // Right traffic, flying the final heading: the outward turn is to the right, away from the centerline.
        Assert.Equal(TurnDirection.Right, follower.Targets.PreferredTurnDirection);

        TickSeconds(engine, 60);
        Assert.Equal(1, TurnOutCalls(follower));
        Assert.True(follower.TrueHeading.AbsAngleTo(rwy.TrueHeading.ToReciprocal()) <= 35.0, $"heading {follower.TrueHeading.Degrees:F0}");
    }

    /// <summary>
    /// Behind the lead but alongside it at the excursion's offset cap, at the same speed: the gap stops growing, and after 20 s
    /// of that the follower turns out.
    /// </summary>
    [Fact]
    public void StalledParallelHoldAtTheOffsetCap_TurnsOut()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddFinalLead(engine, 4.0);
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 4.2, 1.3), rwy.TrueHeading, 1000);
        engine.World.AddAircraft(follower);
        follower.Approach.HasReportedTrafficInSight = true;
        follower.Approach.FollowingCallsign = Leader;
        var widening = new VfrFollowPhaseDto
        {
            Status = (int)PhaseStatus.Pending,
            ElapsedSeconds = 0.0,
            TargetCallsign = Leader,
            WidenActive = true,
            WidenSide = 1,
            PatternReturn = new FollowPatternReturnDto
            {
                Runway = rwy.ToSnapshot(),
                Direction = (int)PatternDirection.Right,
                PatternAltitudeFt = PatternAltitudeFt(rwy),
                FromBase = false,
            },
        };
        follower.Phases = new PhaseList();
        follower.Phases.Add(VfrFollowPhase.FromSnapshot(widening));
        follower.Phases.Start(CommandDispatcher.BuildMinimalContext(follower));

        int turnedOutAt = -1;
        double followerPathNm = 0.0;
        double leadPathNm = 0.0;
        for (int i = 0; (i < 120 * 4) && (turnedOutAt < 0); i++)
        {
            engine.TickPhysics(0.25);
            turnedOutAt = TurnOutCalls(follower) > 0 ? i : -1;
            if (i % 20 == 0)
            {
                Trace(i, follower, lead);
            }

            // Where the level-or-ahead trigger would judge the pair on the tick of the call.
            FinalFramePosition frame = VfrFollowPhase.FinalFrameOf(follower.Position, follower.TrueHeading, rwy, PatternDirection.Right);
            followerPathNm = VfrFollowPhase.ShortestPathToThresholdNm(frame, BasePhase.TurnRadiusNm(follower.GroundSpeed, AircraftCategory.Piston));
            leadPathNm = AlongFinalNm(rwy, lead.Position);
        }

        output.WriteLine($"turned out after {turnedOutAt / 4.0:F1} s, follower path {followerPathNm:F2} nm, lead {leadPathNm:F2} nm");
        Assert.True(turnedOutAt >= 20 * 4, $"turned out after {turnedOutAt / 4.0:F1} s, want a stalled hold of at least 20 s");
        // Still behind the lead by the level-or-ahead measure: the stalled hold, not level-or-ahead, fired the turn-out.
        Assert.True(followerPathNm > leadPathNm, $"follower path {followerPathNm:F2} nm is not longer than the lead's {leadPathNm:F2} nm");
    }

    // ─── Distance limit ───

    /// <summary>
    /// A lead so far out and so slow that it never passes abeam before the follower is 2 nm past its turn-out point: the
    /// follow ends into an extended downwind, holding the heading for a base turn, with no second call.
    /// </summary>
    [Fact]
    public void DistanceLimit_HoldsTheDownwindHeadingWithoutASecondCall()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddFinalLead(engine, 9.0);
        AircraftState follower = AddPursuer(engine, 4.0, 1.2, rwy.TrueHeading, 1000);

        for (int i = 0; (i < 300 * 4) && (follower.Phases?.CurrentPhase is VfrFollowPhase); i++)
        {
            lead.IndicatedAirspeed = 40;
            engine.TickPhysics(0.25);
            if (i % 40 == 0)
            {
                Trace(i, follower, lead);
            }
        }

        DownwindPhase downwind = Assert.IsType<DownwindPhase>(follower.Phases!.CurrentPhase);
        Assert.True(downwind.IsExtended);
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        Assert.Equal(1, TurnOutCalls(follower));
        Assert.DoesNotContain(follower.PendingWarnings, w => w.Contains("unable to follow", StringComparison.Ordinal));
        Assert.Contains(follower.PendingWarnings, w => w.Contains("awaiting a base turn", StringComparison.Ordinal));
        double alongNm = AlongFinalNm(rwy, follower.Position);
        output.WriteLine($"held at {alongNm:F2} nm out");
        Assert.InRange(alongNm, 5.5, 6.5);

        TickSeconds(engine, 20);
        Assert.True(follower.TrueHeading.AbsAngleTo(rwy.TrueHeading.ToReciprocal()) <= 5.0, $"heading {follower.TrueHeading.Degrees:F0}");
    }

    // ─── Lead leaves the final ───

    /// <summary>The lead goes around mid turn-out: the follow ends into a downwind entry for 28R, still cleared to land, with no call.</summary>
    [Fact]
    public void LeadGoesAround_TurnOutEndsIntoADownwindEntryWithoutACall()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddFinalLead(engine, 3.0);
        AircraftState follower = AddPursuer(engine, 2.5, 1.2, rwy.TrueHeading, 1000);
        TickSeconds(engine, 5);
        Assert.Equal(1, TurnOutCalls(follower));
        int warningsBefore = follower.PendingWarnings.Count;

        GoAroundHelper.InstallGoAroundPhases(CommandDispatcher.BuildMinimalContext(lead), new GoAroundPhase(), []);
        TickSeconds(engine, 1);

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Equal("28R", follower.Phases!.AssignedRunway?.Designator);
        Assert.Contains(follower.Phases.Phases, phase => phase is DownwindPhase);
        Assert.IsNotType<VfrFollowPhase>(follower.Phases.CurrentPhase);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        List<string> newWarnings = [.. follower.PendingWarnings.Skip(warningsBefore)];
        output.WriteLine(string.Join(Environment.NewLine, newWarnings));
        Assert.All(newWarnings, w => Assert.Contains("follow ended", w, StringComparison.Ordinal));
    }

    // ─── Landed lead ───

    /// <summary>
    /// The lead lands while the follower is already past the threshold on the pattern side: the follower never turns back
    /// onto the final from there, but re-enters the pattern by the upwind, and reaches the final at least
    /// <see cref="VfrFollowPhase.MinFinalJoinDistNm"/> out.
    /// </summary>
    [Fact]
    public void LandedLead_FollowerBeyondTheThreshold_ReentersByTheUpwindNeverJoinsTheFinal()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddFinalLead(engine, 0.3);
        AircraftState follower = AddPursuer(engine, -0.3, 0.4, rwy.TrueHeading, 800);
        engine.TickPhysics(0.25);

        lead.IsOnGround = true;
        lead.Phases = null;
        engine.TickPhysics(0.25);

        Assert.DoesNotContain(follower.Phases!.Phases, phase => phase is PatternEntryPhase { Kind: PatternEntryKind.Final });
        Assert.IsNotType<VfrFollowPhase>(follower.Phases.CurrentPhase);
        Assert.Contains(follower.Phases.Phases, phase => phase is UpwindPhase);
        Assert.Contains(follower.Phases.Phases, phase => phase is DownwindPhase);

        for (int i = 0; (i < 600 * 4) && (follower.Phases?.CurrentPhase is not FinalApproachPhase); i++)
        {
            engine.TickPhysics(0.25);
            if (i % 40 == 0)
            {
                Trace(i, follower, lead);
            }
        }

        Assert.IsType<FinalApproachPhase>(follower.Phases!.CurrentPhase);
        Assert.True(AlongFinalNm(rwy, follower.Position) >= VfrFollowPhase.MinFinalJoinDistNm);
    }

    [Fact]
    public void LandedFinalGate_RefusesBeyondTheThresholdAndAcceptsAnAlignedFinal()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak("28R");
        AircraftState beyond = MakeVfr(Follower, OffFinal(rwy, -0.3, 0.2), rwy.TrueHeading, 800);
        AircraftState aligned = MakeVfr(Follower, OffFinal(rwy, 1.5, 0.2), rwy.TrueHeading, 800);
        AircraftState tooClose = MakeVfr(Follower, OffFinal(rwy, 0.3, 0.0), rwy.TrueHeading, 300);

        Assert.False(VfrFollowPhase.CanSequenceOntoLandedFinal(beyond, rwy));
        Assert.True(VfrFollowPhase.CanSequenceOntoLandedFinal(aligned, rwy));
        Assert.False(VfrFollowPhase.CanSequenceOntoLandedFinal(tooClose, rwy));
    }

    // ─── Snapshot ───

    private static VfrFollowPhase RoundTrip(VfrFollowPhase phase)
    {
        string json = JsonSerializer.Serialize<PhaseDto>(phase.ToSnapshot(), RecordingJsonOptions.Default);
        VfrFollowPhaseDto dto = Assert.IsType<VfrFollowPhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        return VfrFollowPhase.FromSnapshot(dto);
    }

    [Fact]
    public void TurnOut_SurvivesSnapshotRoundTrip()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AddFinalLead(engine, 3.0);
        AircraftState follower = AddPursuer(engine, 2.5, 1.2, rwy.TrueHeading, 1000);
        TickSeconds(engine, 1);
        VfrFollowPhase phase = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.True(phase.TurningOut);

        VfrFollowPhase restored = RoundTrip(phase);

        Assert.True(restored.TurningOut);
        FollowTurnOutDto original = Assert.IsType<FollowTurnOutDto>(((VfrFollowPhaseDto)phase.ToSnapshot()).TurnOut);
        FollowTurnOutDto copy = Assert.IsType<FollowTurnOutDto>(((VfrFollowPhaseDto)restored.ToSnapshot()).TurnOut);
        Assert.Equal("28R", copy.Runway.Designator);
        Assert.Equal(original.Direction, copy.Direction);
        Assert.Equal(original.PatternAltitudeFt, copy.PatternAltitudeFt);
        Assert.Equal(original.StartLat, copy.StartLat);
        Assert.Equal(original.StartLon, copy.StartLon);

        var older = new VfrFollowPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 1.0,
            TargetCallsign = Leader,
        };
        Assert.False(VfrFollowPhase.FromSnapshot(older).TurningOut);
    }

    [Fact]
    public void ParallelHoldWindow_SurvivesSnapshotRoundTrip()
    {
        var dto = new VfrFollowPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 1.0,
            TargetCallsign = Leader,
            StallWindowStartGapNm = 1.2,
            StallWindowSeconds = 7.5,
            TurnOutRequested = true,
        };

        VfrFollowPhaseDto copy = Assert.IsType<VfrFollowPhaseDto>(VfrFollowPhase.FromSnapshot(dto).ToSnapshot());

        Assert.Equal(1.2, copy.StallWindowStartGapNm);
        Assert.Equal(7.5, copy.StallWindowSeconds);
        Assert.True(copy.TurnOutRequested);
    }

    // ─── Geometry ───

    private const double R = 0.3;

    [Fact]
    public void ShortestPath_OnBase_IsTheBaseTheFinalTurnAndTheFinal() =>
        Assert.Equal(
            (2.0 - R) + (Math.PI / 2.0 * R) + (3.0 - R),
            VfrFollowPhase.ShortestPathToThresholdNm(new FinalFramePosition(3.0, 2.0, -90.0), R),
            9
        );

    [Fact]
    public void ShortestPath_OnTheDownwindHeading_IsTheBaseNowPath() =>
        // Base turn (one radius out along the final, one radius in), the base, the final turn, the final back to where it began.
        Assert.Equal(
            (Math.PI / 2.0 * R) + (2.0 - (2.0 * R)) + (Math.PI / 2.0 * R) + 3.0,
            VfrFollowPhase.ShortestPathToThresholdNm(new FinalFramePosition(3.0, 2.0, 0.0), R),
            9
        );

    [Fact]
    public void ShortestPath_ParallelToTheFinal_IsStraightToTheThreshold() =>
        Assert.Equal(Math.Sqrt((3.0 * 3.0) + (1.2 * 1.2)), VfrFollowPhase.ShortestPathToThresholdNm(new FinalFramePosition(3.0, 1.2, 180.0), R), 9);

    [Fact]
    public void ShortestPath_IsContinuousAcrossTheBaseHeading()
    {
        double onBase = VfrFollowPhase.ShortestPathToThresholdNm(new FinalFramePosition(3.0, 2.0, -90.0), R);
        double justInside = VfrFollowPhase.ShortestPathToThresholdNm(new FinalFramePosition(3.0, 2.0, -90.01), R);
        double justOutside = VfrFollowPhase.ShortestPathToThresholdNm(new FinalFramePosition(3.0, 2.0, -89.99), R);
        Assert.True(Math.Abs(onBase - justInside) < 0.002, $"{onBase:F4} vs {justInside:F4}");
        Assert.True(Math.Abs(onBase - justOutside) < 0.002, $"{onBase:F4} vs {justOutside:F4}");
    }

    [Fact]
    public void FinalFrame_RightTraffic_MeasuresHeadingsFromTheDownwindTowardThePatternSide()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak("28R");
        LatLon position = OffFinal(rwy, 3.0, 1.5);

        FinalFramePosition onBase = VfrFollowPhase.FinalFrameOf(position, rwy.TrueHeading - 90.0, rwy, PatternDirection.Right);
        FinalFramePosition onFinal = VfrFollowPhase.FinalFrameOf(position, rwy.TrueHeading, rwy, PatternDirection.Right);
        FinalFramePosition outward = VfrFollowPhase.FinalFrameOf(position, rwy.TrueHeading.ToReciprocal() - 30.0, rwy, PatternDirection.Right);

        Assert.Equal(3.0, onBase.AlongNm, 2);
        Assert.Equal(1.5, onBase.PatternSideNm, 2);
        Assert.Equal(-90.0, onBase.HeadingDeg, 6);
        Assert.Equal(180.0, onFinal.HeadingDeg, 6);
        Assert.Equal(30.0, outward.HeadingDeg, 6);
    }

    [Fact]
    public void FinalFrame_LeftTraffic_MirrorsTheSideAndTheHeading()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak("28R");
        FinalFramePosition onBase = VfrFollowPhase.FinalFrameOf(OffFinal(rwy, 3.0, -1.5), rwy.TrueHeading + 90.0, rwy, PatternDirection.Left);

        Assert.Equal(1.5, onBase.PatternSideNm, 2);
        Assert.Equal(-90.0, onBase.HeadingDeg, 6);
    }

    [Theory]
    [InlineData(-90.0, PatternDirection.Right, TurnDirection.Left)]
    [InlineData(180.0, PatternDirection.Right, TurnDirection.Right)]
    [InlineData(-90.0, PatternDirection.Left, TurnDirection.Right)]
    [InlineData(180.0, PatternDirection.Left, TurnDirection.Left)]
    public void ReversalTurn_FirstMovesAwayFromTheFinal(double headingDeg, PatternDirection direction, TurnDirection expected) =>
        Assert.Equal(expected, VfrFollowPhase.ReversalTurn(headingDeg, direction));

    [Theory]
    [InlineData(0.8, 1)]
    [InlineData(1.0, 0)]
    [InlineData(1.6, 0)]
    [InlineData(2.0, 0)]
    [InlineData(2.3, -1)]
    public void OffsetBand_CorrectsOutwardBelowTheFloorAndInwardAboveTheCeiling(double patternSideNm, int expected) =>
        Assert.Equal(expected, VfrFollowPhase.TurnOutOffsetCorrection(patternSideNm, 1.0, 2.0));

    [Fact]
    public void OffsetBand_RightTraffic_OutwardIsLeftOfTheDownwindHeading()
    {
        var downwind = new TrueHeading(112.0);
        Assert.Equal(82.0, VfrFollowPhase.TurnOutHeading(downwind, PatternDirection.Right, 1).Degrees, 6);
        Assert.Equal(142.0, VfrFollowPhase.TurnOutHeading(downwind, PatternDirection.Right, -1).Degrees, 6);
        Assert.Equal(142.0, VfrFollowPhase.TurnOutHeading(downwind, PatternDirection.Left, 1).Degrees, 6);
        Assert.Equal(112.0, VfrFollowPhase.TurnOutHeading(downwind, PatternDirection.Left, 0).Degrees, 6);
    }

    [Theory]
    [InlineData(false, 5.0, false)]
    [InlineData(true, 1.05, false)]
    [InlineData(true, 1.1, true)]
    [InlineData(true, 3.0, true)]
    public void Exit_NeedsTheLeadAbeamAndTheSpacingBehindIt(bool leadPassedAbeam, double exitGapNm, bool expected) =>
        Assert.Equal(expected, VfrFollowPhase.ShouldExitTurnOut(leadPassedAbeam, exitGapNm, 1.0));

    /// <summary>
    /// OAK 28L lies just left (south) of 28R: an outward correction from 0.1 nm off one centerline meets the other's final
    /// only on the side it lies on; an inward correction from above the band stops at its ceiling, far short of either.
    /// </summary>
    [Theory]
    [InlineData("28R", PatternDirection.Left, 1, 0.1, true)]
    [InlineData("28R", PatternDirection.Right, 1, 0.1, false)]
    [InlineData("28L", PatternDirection.Right, 1, 0.1, true)]
    [InlineData("28L", PatternDirection.Left, 1, 0.1, false)]
    [InlineData("28L", PatternDirection.Right, -1, 2.5, false)]
    [InlineData("28R", PatternDirection.Left, -1, 2.5, false)]
    public void OffsetCorrection_MeetsTheParallelFinalOnlyOnItsSide(
        string designator,
        PatternDirection direction,
        int correction,
        double patternSideNm,
        bool expected
    )
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak(designator);
        double crossNm = direction == PatternDirection.Right ? patternSideNm : -patternSideNm;
        LatLon position = OffFinal(rwy, 3.0, crossNm);

        Assert.Equal(expected, VfrFollowPhase.CorrectionMeetsParallelFinal(position, rwy, direction, correction, patternSideNm, 1.0));
    }

    [Theory]
    [InlineData(4.9, 3.0, false)]
    [InlineData(5.0, 3.0, true)]
    [InlineData(6.0, 5.5, true)]
    [InlineData(5.9, 5.5, false)]
    public void DistanceLimit_TwoMilesPastTheTurnOutOrSixOut(double alongNm, double startAlongNm, bool expected) =>
        Assert.Equal(expected, VfrFollowPhase.TurnOutPastLimit(alongNm, startAlongNm));

    [Fact]
    public void ParallelHoldWindow_StalledOnlyAfterTwentySecondsOfLittleGrowth()
    {
        ParallelHoldWindow? window = null;
        bool stalled = false;
        for (int i = 0; (i < 80) && !stalled; i++)
        {
            window = VfrFollowPhase.AdvanceParallelHoldWindow(window, holding: true, 1.0 + (i * 0.0005), 0.25, out stalled);
        }

        Assert.False(stalled, "stalled before 20 s");
        _ = VfrFollowPhase.AdvanceParallelHoldWindow(window, holding: true, 1.04, 0.25, out stalled);
        Assert.True(stalled);
    }

    [Fact]
    public void ParallelHoldWindow_GrowingGap_IsNotStalledAndReopens()
    {
        ParallelHoldWindow? window = VfrFollowPhase.AdvanceParallelHoldWindow(null, holding: true, 1.0, 0.25, out _);
        window = VfrFollowPhase.AdvanceParallelHoldWindow(window, holding: true, 1.02, 19.9, out bool stalled);
        Assert.False(stalled);
        window = VfrFollowPhase.AdvanceParallelHoldWindow(window, holding: true, 1.06, 0.25, out stalled);

        Assert.False(stalled);
        Assert.Equal(new ParallelHoldWindow(1.06, 0.0), window);
    }

    [Fact]
    public void ParallelHoldWindow_ClosesWhenTheHoldEnds() =>
        Assert.Null(VfrFollowPhase.AdvanceParallelHoldWindow(new ParallelHoldWindow(1.0, 10.0), holding: false, 1.0, 0.25, out _));
}
