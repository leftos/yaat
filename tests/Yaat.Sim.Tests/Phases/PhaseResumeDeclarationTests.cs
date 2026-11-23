using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Phases;

/// <summary>
/// Each phase's own answer to a Class B boundary hold put in front of it (<see cref="Phase.CanSuspendForBoundaryHold"/>) and
/// how it carries on when the hold ends (<see cref="Phase.ResumeAfterBoundaryHold"/>): in place, or a new Pending copy that
/// re-plans the leg from where the orbit left the aircraft. A VFR C172 in the OAK 28R left-traffic pattern and on the KOAK
/// ILS 28R.
/// </summary>
public class PhaseResumeDeclarationTests
{
    public static TheoryData<string> InPlacePhases =>
        [
            "PatternEntry",
            "MidfieldCrossing",
            "PatternExit",
            "ApproachNavigation",
            "MissedApproachNavigation",
            "GoAround",
            "InitialClimb",
            "DepartureProcedure",
        ];

    public static TheoryData<string> NotSuspendablePhases =>
        [
            "VfrFollowPhase",
            "HelicopterApproachPhase",
            "MakeTurnPhase",
            "STurnPhase",
            "VfrHoldPhase",
            "TouchAndGoPhase",
            "StopAndGoPhase",
            "LowApproachPhase",
            "LandingPhase",
            "TakeoffPhase",
            "MilitaryRoutePhase",
            "AerialRefuelingAnchorPhase",
            "AirspaceBoundaryHoldPhase",
        ];

    [Theory]
    [MemberData(nameof(InPlacePhases))]
    public void InPlacePhase_IsSuspendable_AndResumesInPlace(string name)
    {
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), DownwindLinePoint(-0.8));
        Phase phase = InPlacePhase(name);
        Start(ac, ctx, phase);

        Assert.True(phase.CanSuspendForBoundaryHold(ctx));
        HoldResume resume = phase.ResumeAfterBoundaryHold(ctx);
        Assert.Equal(HoldResumeKind.InPlace, resume.Kind);
        Assert.Null(resume.Copy);
    }

    [Theory]
    [MemberData(nameof(NotSuspendablePhases))]
    public void NotSuspendablePhase_AnswersFalse_AndHasNoResume(string typeName)
    {
        (_, PhaseContext ctx) = AircraftAt(Oak28R(), DownwindLinePoint(-0.8));
        Type type = typeof(Phase).Assembly.GetTypes().Single(t => (t.Name == typeName) && t.IsSubclassOf(typeof(Phase)));
        Phase phase = PhaseAcceptanceAuditTests.ConstructWithDefaults(type);

        Assert.False(phase.CanSuspendForBoundaryHold(ctx));
        Assert.Throws<InvalidOperationException>(() => phase.ResumeAfterBoundaryHold(ctx));
    }

    [Fact]
    public void Teardrop_BeforeAbeam_ResumesInPlace_AndFliesItsRemainingFixes()
    {
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), DownwindLinePoint(-0.8));
        var teardrop = new TeardropReentryPhase { Waypoints = Pattern() };
        Start(ac, ctx, teardrop);

        Assert.True(teardrop.CanSuspendForBoundaryHold(ctx));
        Assert.Equal(HoldResumeKind.InPlace, teardrop.ResumeAfterBoundaryHold(ctx).Kind);
        Assert.Null(Assert.IsType<TeardropReentryPhaseDto>(teardrop.ToSnapshot()).CompleteAtAbeamOnResume);
        Assert.False(PhaseRunner_TickCompletes(ac, ctx, teardrop));
    }

    [Fact]
    public void Teardrop_HeldPastAbeam_WithOnlyTheAbeamPointLeft_CompletesAndTheDownwindRejoins()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), DownwindLinePoint(-0.8));
        ac.Altitude = wp.PatternAltitude;
        var teardrop = new TeardropReentryPhase { Waypoints = wp };
        var downwind = new DownwindPhase { Waypoints = wp, RejoinTrack = false };
        Start(ac, ctx, teardrop, downwind);
        ac.Targets.NavigationRoute.RemoveRange(0, 2);
        ac.Phases!.InsertHoldInFront(SfoBravoHold());
        PhaseRunner.Tick(ac, ctx);
        ac.Position = DownwindLinePoint(0.2);
        ac.TrueHeading = wp.DownwindHeading + 180;

        HoldResume resume = teardrop.ResumeAfterBoundaryHold(ctx);
        Assert.Equal(HoldResumeKind.InPlace, resume.Kind);
        Assert.True(Assert.IsType<TeardropReentryPhaseDto>(teardrop.ToSnapshot()).CompleteAtAbeamOnResume);
        PhaseRunner.EndHold(ac, ctx, resume);
        Assert.Equal(TeardropReentryPhase.AbeamTargetName, Assert.Single(ac.Targets.NavigationRoute).Name);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, teardrop.Status);
        Assert.Same(downwind, ac.Phases.CurrentPhase);
        Assert.True(downwind.RejoinTrack);
        Assert.DoesNotContain(ac.Targets.NavigationRoute, t => t.Name == TeardropReentryPhase.TeardropOutboundTargetName);
        PhaseRunner.Tick(ac, ctx);
        TrueHeading steer = Assert.IsType<TrueHeading>(ac.Targets.TargetTrueHeading);
        double offDownwind = Math.Abs(GeoMath.SignedBearingDifference(steer.Degrees, wp.DownwindHeading.Degrees));
        Assert.True(offDownwind <= 45.0, $"steering {steer.Degrees:F0} turns back from the downwind {wp.DownwindHeading.Degrees:F0}");
        Assert.True(ac.Targets.TargetAltitude < wp.PatternAltitude, $"past abeam with no descent: target {ac.Targets.TargetAltitude}");
    }

    [Fact]
    public void Upwind_CopyCarriesItsLegConfiguration_AndReacquiresTheOffset()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), UpwindLinePoint(wp, 0.0));
        var offset = new PatternLateralOffsetState { TargetNm = 0.5, Direction = TurnDirection.Right };
        var upwind = new UpwindPhase
        {
            Waypoints = wp,
            IsExtended = true,
            TurnCrosswindArmed = true,
            LateralOffset = offset,
            DepartureClimbTargetFt = 5500,
        };
        Start(ac, ctx, upwind);
        offset.Acquired = true;

        Assert.True(upwind.CanSuspendForBoundaryHold(ctx));
        UpwindPhase copy = RestartCopy<UpwindPhase>(upwind, ctx);

        Assert.Same(wp, copy.Waypoints);
        Assert.True(copy.IsExtended);
        Assert.True(copy.TurnCrosswindArmed);
        Assert.Equal(5500, copy.DepartureClimbTargetFt);
        PatternLateralOffsetState copyOffset = Assert.IsType<PatternLateralOffsetState>(copy.LateralOffset);
        Assert.NotSame(offset, copyOffset);
        Assert.Equal(0.5, copyOffset.TargetNm);
        Assert.Equal(TurnDirection.Right, copyOffset.Direction);
        Assert.False(copyOffset.Acquired);
    }

    [Fact]
    public void Upwind_Copy_SteersBackOntoTheExtendedCenterline_AfterALateralDisplacement()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), UpwindLinePoint(wp, 0.0));
        ac.Altitude = Oak28R().ElevationFt + 600;
        var upwind = new UpwindPhase { Waypoints = wp };
        Start(ac, ctx, upwind);
        ac.Position = UpwindLinePoint(wp, 0.3);
        UpwindPhase copy = RestartCopy<UpwindPhase>(upwind, ctx);

        Start(ac, ctx, copy);
        PhaseRunner.Tick(ac, ctx);

        var departureEnd = new LatLon(wp.DepartureEndLat, wp.DepartureEndLon);
        double offNow = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, departureEnd, wp.UpwindHeading));
        TrueHeading steer = Assert.IsType<TrueHeading>(ac.Targets.TargetTrueHeading);
        LatLon ahead = GeoMath.ProjectPoint(ac.Position, steer, 0.1);
        double offAhead = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ahead, departureEnd, wp.UpwindHeading));
        Assert.True(offAhead < offNow - 0.01, $"steering {steer.Degrees:F0} does not close the centerline: {offNow:F3} -> {offAhead:F3} nm");
    }

    [Fact]
    public void Upwind_StartedCopy_KeepsSteeringOntoTheCenterline_AcrossASnapshotRoundTrip()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), UpwindLinePoint(wp, 0.3));
        ac.Altitude = Oak28R().ElevationFt + 600;
        var upwind = new UpwindPhase { Waypoints = wp };
        Assert.Null(Assert.IsType<UpwindPhaseDto>(upwind.ToSnapshot()).RejoinCenterline);
        UpwindPhase copy = RestartCopy<UpwindPhase>(upwind, ctx);
        Start(ac, ctx, copy);

        string json = JsonSerializer.Serialize(ac.Phases!.ToSnapshot(), RecordingJsonOptions.Default);
        ac.Phases = PhaseList.FromSnapshot(JsonSerializer.Deserialize<PhaseListDto>(json, RecordingJsonOptions.Default)!, null);
        UpwindPhase restored = Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
        Assert.True(Assert.IsType<UpwindPhaseDto>(restored.ToSnapshot()).RejoinCenterline);
        ac.Targets.TargetTrueHeading = wp.UpwindHeading;
        PhaseRunner.Tick(ac, ctx);

        TrueHeading steer = Assert.IsType<TrueHeading>(ac.Targets.TargetTrueHeading);
        Assert.True(
            Math.Abs(GeoMath.SignedBearingDifference(steer.Degrees, wp.UpwindHeading.Degrees)) > 1.0,
            $"still on the upwind heading: {steer.Degrees:F0}"
        );
    }

    [Fact]
    public void Upwind_Copy_KeepsACmIssuedOnTheLeg()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), UpwindLinePoint(wp, 0.0));
        ac.Altitude = Oak28R().ElevationFt + 600;
        var upwind = new UpwindPhase { Waypoints = wp };
        Start(ac, ctx, upwind);
        ac.Targets.AssignedAltitude = 2500;
        ac.Targets.TargetAltitude = 2500;
        UpwindPhase copy = RestartCopy<UpwindPhase>(upwind, ctx);

        Start(ac, ctx, copy);

        Assert.Equal(2500, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void Upwind_Copy_WithNoCmOnTheLeg_ClimbsToPatternAltitude()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), UpwindLinePoint(wp, 0.0));
        ac.Altitude = Oak28R().ElevationFt + 600;
        ac.Targets.AssignedAltitude = 2500;
        var upwind = new UpwindPhase { Waypoints = wp };
        Start(ac, ctx, upwind);
        UpwindPhase copy = RestartCopy<UpwindPhase>(upwind, ctx);

        Start(ac, ctx, copy);

        Assert.Equal(wp.PatternAltitude, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void Crosswind_CopyCarriesItsLegConfiguration()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon));
        var offset = new PatternLateralOffsetState { TargetNm = 0.5, Direction = TurnDirection.Left };
        var crosswind = new CrosswindPhase
        {
            Waypoints = wp,
            IsExtended = true,
            LateralOffset = offset,
            DepartureClimbTargetFt = 4500,
        };
        Start(ac, ctx, crosswind);

        Assert.True(crosswind.CanSuspendForBoundaryHold(ctx));
        CrosswindPhase copy = RestartCopy<CrosswindPhase>(crosswind, ctx);

        Assert.Same(wp, copy.Waypoints);
        Assert.True(copy.IsExtended);
        Assert.NotSame(offset, copy.LateralOffset);
        Assert.Equal(offset.TargetNm, copy.LateralOffset!.TargetNm);
        Assert.Equal(4500, copy.DepartureClimbTargetFt);
    }

    [Fact]
    public void Downwind_CopyCarriesItsLatches_AndRejoinsTheTrack()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), DownwindLinePoint(-0.8));
        var offset = new PatternLateralOffsetState { TargetNm = 0.5, Direction = TurnDirection.Left };
        var downwind = new DownwindPhase
        {
            Waypoints = wp,
            IsExtended = true,
            MidfieldReportIssued = true,
            ShortApproachArmed = true,
            ExitAtMidfield = true,
            LateralOffset = offset,
        };
        Start(ac, ctx, downwind);

        Assert.True(downwind.CanSuspendForBoundaryHold(ctx));
        DownwindPhase copy = RestartCopy<DownwindPhase>(downwind, ctx);

        Assert.Same(wp, copy.Waypoints);
        Assert.True(copy.IsExtended);
        Assert.True(copy.MidfieldReportIssued);
        Assert.True(copy.ShortApproachArmed);
        Assert.True(copy.ExitAtMidfield);
        Assert.True(copy.RejoinTrack);
        Assert.NotSame(offset, copy.LateralOffset);
        Assert.Equal(offset.TargetNm, copy.LateralOffset!.TargetNm);
    }

    [Fact]
    public void Downwind_CopyCarriesTheMidfieldReminderTheLegGave()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), MidfieldPoint(wp, 0.2));
        ac.Altitude = wp.PatternAltitude;
        var downwind = new DownwindPhase { Waypoints = wp };
        Start(ac, ctx, downwind);
        PhaseRunner.Tick(ac, ctx);
        Assert.True(Assert.IsType<DownwindPhaseDto>(downwind.ToSnapshot()).MidfieldBroadcastIssued);

        DownwindPhase copy = RestartCopy<DownwindPhase>(downwind, ctx);

        Assert.True(Assert.IsType<DownwindPhaseDto>(copy.ToSnapshot()).MidfieldBroadcastIssued);
    }

    [Fact]
    public void Downwind_CopyResumedPastMidfield_GivesNoMidfieldReminder()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), MidfieldPoint(wp, -0.8));
        ac.Altitude = wp.PatternAltitude;
        var downwind = new DownwindPhase { Waypoints = wp };
        Start(ac, ctx, downwind);
        ac.Position = MidfieldPoint(wp, 0.2);
        DownwindPhase copy = RestartCopy<DownwindPhase>(downwind, ctx);

        Start(ac, ctx, copy);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(0, Voiced(ac));
    }

    [Fact]
    public void Downwind_CopyResumedBeforeMidfield_GivesTheMidfieldReminderThere()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), MidfieldPoint(wp, -0.8));
        ac.Altitude = wp.PatternAltitude;
        var downwind = new DownwindPhase { Waypoints = wp };
        Start(ac, ctx, downwind);
        ac.Position = MidfieldPoint(wp, -0.5);
        DownwindPhase copy = RestartCopy<DownwindPhase>(downwind, ctx);
        Start(ac, ctx, copy);
        PhaseRunner.Tick(ac, ctx);
        Assert.Equal(0, Voiced(ac));

        ac.Position = MidfieldPoint(wp, 0.1);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(1, Voiced(ac));
    }

    [Fact]
    public void Downwind_CopyMadeBeforeAbeam_HoldsPatternAltitude()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), DownwindLinePoint(-0.8));
        ac.Altitude = wp.PatternAltitude;
        var downwind = new DownwindPhase { Waypoints = wp };
        Start(ac, ctx, downwind);
        DownwindPhase copy = RestartCopy<DownwindPhase>(downwind, ctx);

        Start(ac, ctx, copy);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Active, copy.Status);
        Assert.Equal(wp.PatternAltitude, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void Downwind_CopyMadePastAbeam_IsPastAbeam_AndDescends()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), DownwindLinePoint(-0.8));
        ac.Altitude = wp.PatternAltitude;
        var downwind = new DownwindPhase { Waypoints = wp };
        Start(ac, ctx, downwind);
        ac.Position = DownwindLinePoint(0.2);
        DownwindPhase copy = RestartCopy<DownwindPhase>(downwind, ctx);

        Start(ac, ctx, copy);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Active, copy.Status);
        Assert.True(ac.Targets.TargetAltitude < wp.PatternAltitude, $"target {ac.Targets.TargetAltitude} is not below TPA {wp.PatternAltitude}");
    }

    [Fact]
    public void Downwind_Copy_KeepsADmIssuedOnTheLeg()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), DownwindLinePoint(-1.5));
        ac.Altitude = wp.PatternAltitude;
        var downwind = new DownwindPhase { Waypoints = wp };
        Start(ac, ctx, downwind);
        double controllerAltitudeFt = wp.PatternAltitude - 300;
        ac.Targets.AssignedAltitude = controllerAltitudeFt;
        ac.Targets.TargetAltitude = controllerAltitudeFt;
        PhaseRunner.Tick(ac, ctx);
        DownwindPhase copy = RestartCopy<DownwindPhase>(downwind, ctx);

        Start(ac, ctx, copy);
        Assert.Equal(controllerAltitudeFt, ac.Targets.TargetAltitude);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(controllerAltitudeFt, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void RestartCopies_MakeNoTurningLegCall_WhileTheLegsThemselvesDo()
    {
        PatternWaypoints wp = Pattern();
        RunwayInfo runway = Oak28R();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(runway, DownwindLinePoint(-1.5));
        ac.Approach.ReportArmedCrosswind = true;
        ac.Approach.ReportArmedDownwind = true;
        ac.Approach.ReportArmedBase = true;
        ac.Approach.ReportArmedFinal = true;
        var final = new FinalApproachPhase();
        Phase[] legs = [new CrosswindPhase { Waypoints = wp }, new DownwindPhase { Waypoints = wp }, new BasePhase { Waypoints = wp }, final];

        foreach (Phase leg in legs)
        {
            ClearVoiced(ac);
            Start(ac, ctx, leg);
            Assert.True(Voiced(ac) > 0, $"{leg.Name} made no turning call as a new leg");

            Phase copy = leg.ResumeAfterBoundaryHold(ctx).Copy!;
            ClearVoiced(ac);
            Start(ac, ctx, copy);
            Assert.True(Voiced(ac) == 0, $"{leg.Name}'s restart copy made a turning call");
        }

        // A 360's resume copies are not boundary-hold copies: they still make the call.
        foreach (Phase resume in new Phase[] { new DownwindPhase { Waypoints = wp }.CloneForResume(), final.CloneForResume() })
        {
            ClearVoiced(ac);
            Start(ac, ctx, resume);
            Assert.True(Voiced(ac) > 0, $"{resume.Name}'s 360 resume made no turning call");
        }
    }

    [Fact]
    public void Base_CopyCarriesItsLegConfiguration_AndReplansTheDescentFromWhereItIs()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), new LatLon(wp.BaseTurnLat, wp.BaseTurnLon));
        ac.Altitude = wp.PatternAltitude - 200;
        var offset = new PatternLateralOffsetState { TargetNm = 0.3, Direction = TurnDirection.Left };
        var basePhase = new BasePhase
        {
            Waypoints = wp,
            FinalDistanceNm = 1.2,
            LateralOffset = offset,
        };
        Start(ac, ctx, basePhase);

        Assert.True(basePhase.CanSuspendForBoundaryHold(ctx));
        ac.Position = GeoMath.ProjectPoint(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon), wp.DownwindHeading + 180, 0.6);
        ac.Altitude = wp.PatternAltitude - 100;
        BasePhase copy = RestartCopy<BasePhase>(basePhase, ctx);

        Assert.Same(wp, copy.Waypoints);
        Assert.Equal(1.2, copy.FinalDistanceNm);
        Assert.NotSame(offset, copy.LateralOffset);
        Assert.Equal(offset.TargetNm, copy.LateralOffset!.TargetNm);

        Start(ac, ctx, copy);
        Assert.Equal(ac.Position, copy.StartPoint);
        Assert.True(ac.Targets.TargetAltitude < ac.Altitude, $"no descent planned from {ac.Altitude}: target {ac.Targets.TargetAltitude}");
    }

    [Fact]
    public void FinalApproach_CopyKeepsTheGoAroundRollConsumed_AndStartsTheGatesFresh()
    {
        RunwayInfo runway = Oak28R();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(runway, OnFinal(runway, 4.0));
        var final = new FinalApproachPhase();
        Start(ac, ctx, final);
        PhaseRunner.Tick(ac, ctx);

        Assert.True(final.CanSuspendForBoundaryHold(ctx));
        FinalApproachPhase copy = RestartCopy<FinalApproachPhase>(final, ctx);

        FinalApproachPhaseDto dto = Assert.IsType<FinalApproachPhaseDto>(copy.ToSnapshot());
        Assert.True(dto.GoAroundRolled);
        Assert.False(dto.GsCaptured);
        Assert.False(dto.FasSet);
        Assert.False(dto.ConfigSet);
        Assert.Equal(0, dto.LateralOffCourseSeconds ?? 0);
        Assert.Equal(0, dto.LateralGateGraceSeconds ?? 0);
    }

    [Fact]
    public void InterceptCourse_CopyCarriesItsClearance_AndForgetsTheCrossTrackHistory()
    {
        RunwayInfo runway = Oak28R();
        LatLon offCourse = GeoMath.ProjectPoint(OnFinal(runway, 8.0), runway.TrueHeading + 90, 2.0);
        (AircraftState ac, PhaseContext ctx) = AircraftAt(runway, offCourse);
        ac.TrueHeading = runway.TrueHeading + (-30);
        var intercept = new InterceptCoursePhase
        {
            FinalApproachCourse = runway.TrueHeading,
            ThresholdLat = runway.ThresholdLatitude,
            ThresholdLon = runway.ThresholdLongitude,
            ApproachId = "I28R",
            AssignedInterceptHeading = new MagneticHeading(250),
            ForcedIntercept = true,
            RelaxedJoin = true,
        };
        Start(ac, ctx, intercept);
        PhaseRunner.Tick(ac, ctx);
        Assert.NotNull(Assert.IsType<InterceptCoursePhaseDto>(intercept.ToSnapshot()).PreviousSignedCrossTrack);

        Assert.True(intercept.CanSuspendForBoundaryHold(ctx));
        InterceptCoursePhase copy = RestartCopy<InterceptCoursePhase>(intercept, ctx);

        Assert.Equal(intercept.FinalApproachCourse, copy.FinalApproachCourse);
        Assert.Equal(intercept.ThresholdLat, copy.ThresholdLat);
        Assert.Equal(intercept.ThresholdLon, copy.ThresholdLon);
        Assert.Equal("I28R", copy.ApproachId);
        Assert.Equal(intercept.AssignedInterceptHeading, copy.AssignedInterceptHeading);
        Assert.True(copy.ForcedIntercept);
        Assert.True(copy.RelaxedJoin);
        InterceptCoursePhaseDto dto = Assert.IsType<InterceptCoursePhaseDto>(copy.ToSnapshot());
        Assert.Null(dto.PreviousSignedCrossTrack);
        Assert.False(dto.ApproachSpeedSet);
    }

    [Fact]
    public void ProcedureTurn_CopyRestartsAtTheFix()
    {
        ApproachFix fix = I28RFixes()[0];
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), new LatLon(fix.Latitude, fix.Longitude));
        var pt = new ProcedureTurnPhase
        {
            FixName = fix.Name,
            FixLat = fix.Latitude,
            FixLon = fix.Longitude,
            InboundCourseDeg = Oak28R().TrueHeading.Degrees,
            PtOutboundCourseDeg = Oak28R().TrueHeading.Degrees + 180 - 45,
            MaxOutboundDistanceNm = 10,
            OneEightyTurnDirection = TurnDirection.Right,
            MinAltitudeFt = 2000,
        };
        int atTheFix = Assert.IsType<ProcedureTurnPhaseDto>(pt.ToSnapshot()).State;
        Start(ac, ctx, pt);
        PhaseRunner.Tick(ac, ctx);
        Assert.NotEqual(atTheFix, Assert.IsType<ProcedureTurnPhaseDto>(pt.ToSnapshot()).State);

        Assert.True(pt.CanSuspendForBoundaryHold(ctx));
        ProcedureTurnPhase copy = RestartCopy<ProcedureTurnPhase>(pt, ctx);

        Assert.Equal(atTheFix, Assert.IsType<ProcedureTurnPhaseDto>(copy.ToSnapshot()).State);
        Assert.Equal(pt.FixName, copy.FixName);
        Assert.Equal(pt.FixLat, copy.FixLat);
        Assert.Equal(pt.FixLon, copy.FixLon);
        Assert.Equal(pt.InboundCourseDeg, copy.InboundCourseDeg);
        Assert.Equal(pt.PtOutboundCourseDeg, copy.PtOutboundCourseDeg);
        Assert.Equal(pt.MaxOutboundDistanceNm, copy.MaxOutboundDistanceNm);
        Assert.Equal(pt.OneEightyTurnDirection, copy.OneEightyTurnDirection);
        Assert.Equal(pt.MinAltitudeFt, copy.MinAltitudeFt);
    }

    [Fact]
    public void HoldingPattern_CopyEntersAfreshFromWhereItIs()
    {
        ApproachFix fix = I28RFixes()[0];
        (AircraftState ac, PhaseContext ctx) = AircraftAt(
            Oak28R(),
            GeoMath.ProjectPoint(new LatLon(fix.Latitude, fix.Longitude), Oak28R().TrueHeading, 3.0)
        );
        var hold = new HoldingPatternPhase
        {
            FixName = fix.Name,
            FixLat = fix.Latitude,
            FixLon = fix.Longitude,
            InboundCourse = 280,
            LegLength = 1,
            IsMinuteBased = true,
            Direction = TurnDirection.Right,
            Entry = HoldingEntry.Teardrop,
            MaxCircuits = 1,
            IsHoldInLieu = true,
        };
        Start(ac, ctx, hold);

        Assert.True(hold.CanSuspendForBoundaryHold(ctx));
        HoldingPatternPhase copy = RestartCopy<HoldingPatternPhase>(hold, ctx);

        Assert.Null(copy.Entry);
        Assert.Equal(hold.FixName, copy.FixName);
        Assert.Equal(hold.FixLat, copy.FixLat);
        Assert.Equal(hold.FixLon, copy.FixLon);
        Assert.Equal(280, copy.InboundCourse);
        Assert.Equal(1, copy.LegLength);
        Assert.True(copy.IsMinuteBased);
        Assert.Equal(TurnDirection.Right, copy.Direction);
        Assert.Equal(1, copy.MaxCircuits);
        Assert.True(copy.IsHoldInLieu);
        Assert.False(copy.IsMissedApproachHold);
        Assert.False(copy.IsClearanceLimitHold);
    }

    [Fact]
    public void HoldingPattern_Copy_PicksItsEntryFromTheCourseToTheFix_NotTheHeadingTheOrbitLeft()
    {
        ApproachFix fix = I28RFixes()[0];
        var fixPosition = new LatLon(fix.Latitude, fix.Longitude);
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), GeoMath.ProjectPoint(fixPosition, Oak28R().TrueHeading, 3.0));
        var hold = new HoldingPatternPhase
        {
            FixName = fix.Name,
            FixLat = fix.Latitude,
            FixLon = fix.Longitude,
            InboundCourse = 280,
            LegLength = 1,
            IsMinuteBased = true,
            Direction = TurnDirection.Right,
        };
        Start(ac, ctx, hold);
        var courseToFix = new TrueHeading(GeoMath.BearingTo(ac.Position, fixPosition));
        ac.TrueHeading = courseToFix + 180;
        HoldingPatternPhase copy = RestartCopy<HoldingPatternPhase>(hold, ctx);

        Start(ac, ctx, copy);

        HoldingEntry expected = HoldingEntryCalculator.ComputeEntry(courseToFix, 280, TurnDirection.Right);
        Assert.Equal((int)expected, Assert.IsType<HoldingPatternPhaseDto>(copy.ToSnapshot()).ResolvedEntry);
    }

    [Fact]
    public void EndHold_WithThePatternEntrysOwnResume_ResumesItInPlace_AndItTicksOn()
    {
        RunwayInfo runway = Oak28R();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(runway, DownwindLinePoint(-3.0));
        // Its entry point inside the SFO Class B surface area keeps the held path blocked, so the hold never ends by itself.
        PatternEntryPhase entry = new()
        {
            EntryLat = SfoClassBTestPoints.InsideTheSfoSurfaceArea.Lat,
            EntryLon = SfoClassBTestPoints.InsideTheSfoSurfaceArea.Lon,
            PatternAltitude = PatternEntryTo().PatternAltitude,
            Kind = PatternEntryKind.Direct,
        };
        Start(ac, ctx, entry);
        PhaseRunner.Tick(ac, ctx);
        double elapsed = entry.ElapsedSeconds;
        ac.Phases!.InsertHoldInFront(SfoBravoHold());
        PhaseRunner.Tick(ac, ctx);

        PhaseRunner.EndHold(ac, ctx, entry.ResumeAfterBoundaryHold(ctx));

        Assert.Same(entry, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, entry.Status);
        Assert.Equal(PatternEntryPhase.PatternEntryTargetName, Assert.Single(ac.Targets.NavigationRoute).Name);
        PhaseRunner.Tick(ac, ctx);
        Assert.Equal(elapsed + 1.0, entry.ElapsedSeconds);
    }

    [Fact]
    public void EndHold_WithTheDownwindsOwnResume_SwapsInTheCopy_AndItTicksOn()
    {
        PatternWaypoints wp = Pattern();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), DownwindLinePoint(-0.8));
        ac.Altitude = wp.PatternAltitude;
        var downwind = new DownwindPhase { Waypoints = wp };
        Start(ac, ctx, downwind);
        PhaseRunner.Tick(ac, ctx);
        ac.Phases!.InsertHoldInFront(SfoBravoHold());
        // At 7500 ft over the OAK pattern the held path runs inside the SFO Class B, so the hold's first tick does not end it
        // by itself; back at pattern altitude for the resume.
        ac.Altitude = 7500;
        PhaseRunner.Tick(ac, ctx);
        ac.Altitude = wp.PatternAltitude;

        PhaseRunner.EndHold(ac, ctx, downwind.ResumeAfterBoundaryHold(ctx));

        Assert.Equal(PhaseStatus.Completed, downwind.Status);
        DownwindPhase copy = Assert.IsType<DownwindPhase>(ac.Phases.CurrentPhase);
        Assert.NotSame(downwind, copy);
        Assert.Equal(PhaseStatus.Active, copy.Status);
        Assert.Equal(wp.PatternAltitude, ac.Targets.TargetAltitude);
        PhaseRunner.Tick(ac, ctx);
        Assert.Equal(1.0, copy.ElapsedSeconds);
        Assert.Equal(PhaseStatus.Active, copy.Status);
        Assert.Equal(wp.PatternAltitude, ac.Targets.TargetAltitude);
    }

    private static bool PhaseRunner_TickCompletes(AircraftState ac, PhaseContext ctx, Phase phase)
    {
        PhaseRunner.Tick(ac, ctx);
        return phase.Status == PhaseStatus.Completed;
    }

    private static int Voiced(AircraftState ac) => ac.PendingPilotTransmissions.Count + ac.PendingWarnings.Count;

    private static void ClearVoiced(AircraftState ac)
    {
        ac.PendingPilotTransmissions.Clear();
        ac.PendingWarnings.Clear();
    }

    private static T RestartCopy<T>(Phase original, PhaseContext ctx)
        where T : Phase
    {
        HoldResume resume = original.ResumeAfterBoundaryHold(ctx);
        Assert.Equal(HoldResumeKind.RestartCopy, resume.Kind);
        T copy = Assert.IsType<T>(resume.Copy);
        Assert.NotSame(original, copy);
        Assert.Equal(PhaseStatus.Pending, copy.Status);
        return copy;
    }

    private static Phase InPlacePhase(string name) =>
        name switch
        {
            "PatternEntry" => PatternEntryTo(),
            "MidfieldCrossing" => new MidfieldCrossingPhase { Waypoints = Pattern() },
            "PatternExit" => new PatternExitPhase
            {
                ExitHeading = Oak28R().TrueHeading,
                Direction = PatternDirection.Left,
                ClimbTargetFt = 3500,
            },
            "ApproachNavigation" => new ApproachNavigationPhase { Fixes = I28RFixes() },
            "MissedApproachNavigation" => new ApproachNavigationPhase { Fixes = I28RFixes(), IsMissedApproach = true },
            "GoAround" => new GoAroundPhase { TargetAltitude = 2000 },
            "InitialClimb" => new InitialClimbPhase { AssignedAltitude = 3500 },
            "DepartureProcedure" => new DepartureProcedurePhase
            {
                Legs =
                [
                    new ProcedureLeg
                    {
                        Type = ProcedureLegType.HeadingToAltitude,
                        CourseMagnetic = 290,
                        TargetAltitudeFt = 3000,
                    },
                ],
                PostRoute = [],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };

    private static void Start(AircraftState ac, PhaseContext ctx, params Phase[] phases)
    {
        ac.Phases = new PhaseList { AssignedRunway = Oak28R(), TrafficDirection = PatternDirection.Left };
        foreach (Phase phase in phases)
        {
            ac.Phases.Add(phase);
        }

        ac.Phases.Start(ctx);
    }

    private static RunwayInfo Oak28R() =>
        NavigationDatabase.Instance.GetRunway("OAK", "28R") ?? throw new InvalidOperationException("OAK 28R missing");

    private static PatternWaypoints Pattern() =>
        PatternGeometry.Compute(
            new PatternGeometryRequest(Oak28R(), AircraftCategory.Piston, "C172", 0, PatternDirection.Left)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = null,
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );

    /// <summary>A point on the downwind line, <paramref name="pastAbeamNm"/> along the downwind heading from the abeam point.</summary>
    private static LatLon DownwindLinePoint(double pastAbeamNm)
    {
        PatternWaypoints wp = Pattern();
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        return pastAbeamNm >= 0
            ? GeoMath.ProjectPoint(abeam, wp.DownwindHeading, pastAbeamNm)
            : GeoMath.ProjectPoint(abeam, wp.DownwindHeading + 180, -pastAbeamNm);
    }

    /// <summary>A point on the downwind line, <paramref name="pastMidfieldNm"/> along the downwind heading from midfield.</summary>
    private static LatLon MidfieldPoint(PatternWaypoints wp, double pastMidfieldNm)
    {
        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        double abeamAlongTrack = GeoMath.AlongTrackDistanceNm(new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon), threshold, wp.DownwindHeading);
        return DownwindLinePoint(PatternGeometry.MidfieldAlongTrackNm(wp) - abeamAlongTrack + pastMidfieldNm);
    }

    /// <summary>Half a mile short of the departure end, <paramref name="rightOfCenterlineNm"/> right of the extended centerline.</summary>
    private static LatLon UpwindLinePoint(PatternWaypoints wp, double rightOfCenterlineNm)
    {
        LatLon onCenterline = GeoMath.ProjectPoint(new LatLon(wp.DepartureEndLat, wp.DepartureEndLon), wp.UpwindHeading + 180, 0.5);
        return GeoMath.ProjectPoint(onCenterline, wp.UpwindHeading + 90, rightOfCenterlineNm);
    }

    private static LatLon OnFinal(RunwayInfo runway, double outNm) =>
        GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading + 180, outNm);

    /// <summary>The KOAK ILS 28R's common-leg fixes, resolved on the real navdata.</summary>
    private static List<ApproachFix> I28RFixes()
    {
        CifpApproachProcedure approach =
            NavigationDatabase.Instance.GetApproach("KOAK", "I28R") ?? throw new InvalidOperationException("KOAK I28R missing");
        List<ApproachFix> fixes = [];
        foreach (CifpLeg leg in approach.CommonLegs)
        {
            if (NavigationDatabase.Instance.GetFixPosition(leg.FixIdentifier) is { } position)
            {
                fixes.Add(new ApproachFix(leg.FixIdentifier, position.Lat, position.Lon));
            }
        }

        Assert.NotEmpty(fixes);
        return fixes;
    }

    private static PatternEntryPhase PatternEntryTo()
    {
        PatternWaypoints wp = Pattern();
        return new PatternEntryPhase
        {
            EntryLat = wp.DownwindAbeamLat,
            EntryLon = wp.DownwindAbeamLon,
            PatternAltitude = wp.PatternAltitude,
            Kind = PatternEntryKind.Direct,
        };
    }

    private static (AircraftState Aircraft, PhaseContext Context) AircraftAt(RunwayInfo runway, LatLon position)
    {
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            AirportId = "OAK",
            Position = position,
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt + 1000,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", Destination = "KOAK" },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
            SoloTrainingMode = true,
            StudentPositionType = "TWR",
        };
        return (ac, ctx);
    }

    private static AirspaceBoundaryHoldPhase SfoBravoHold() =>
        new()
        {
            AirspaceClass = AirspaceClass.Bravo,
            Ident = "SFO",
            NameText = "SFO",
            ReferencePosition = new LatLon(37.6213, -122.3790),
            OrbitDirection = TurnDirection.Left,
            Mode = AirspaceHoldMode.Orbit,
        };
}
