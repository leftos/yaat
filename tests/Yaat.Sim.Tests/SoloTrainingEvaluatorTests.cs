using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests;

public sealed class SoloTrainingEvaluatorTests
{
    public SoloTrainingEvaluatorTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Evaluate_IfrPairInsideThreeMiles_RecordsSafetyEvent()
    {
        var a = CreateAircraft("AAL1", "B738", flightRules: "IFR", new LatLon(37.6213, -122.3790), altitude: 5000, isOnGround: false);
        var b = CreateAircraft(
            "UAL2",
            "A320",
            flightRules: "IFR",
            GeoMath.ProjectPoint(a.Position, new TrueHeading(90), 2.5),
            altitude: 5000,
            isOnGround: false
        );
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 100, new ApproachReportData([], [], 100, "N/A"));

        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.Separation);
        Assert.Equal(SoloTrainingEventSeverity.Safety, notice.Severity);
        Assert.Contains("7110.65 §5-5-4", notice.RuleReference);
        Assert.Single(report.ActiveEvents, e => e.Category == SoloTrainingEventCategory.Separation);
        Assert.True(report.Score < 100);
    }

    [Fact]
    public void Evaluate_ClassBVfrBehindTurbojet_UsesOnePointFiveMilesOrFiveHundredFeet()
    {
        var jet = CreateAircraft("SWA1", "B738", flightRules: "IFR", new LatLon(37.6213, -122.3790), altitude: 3000, isOnGround: false);
        var vfr = CreateAircraft(
            "N123AB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(jet.Position, new TrueHeading(90), 1.4),
            altitude: 3000,
            isOnGround: false
        );
        var requirement = SoloTrainingEvaluator.ResolveRequirement(jet, vfr, AirspaceDatabase.Default);

        Assert.NotNull(requirement);
        Assert.Equal(1.5, requirement.RequiredHorizontalNm);
        Assert.Equal(500.0, requirement.RequiredVerticalFt);
        Assert.Contains("7110.65 §7-9-4", requirement.RuleReference);
    }

    [Fact]
    public void Evaluate_ClassBSmallVfrPair_UsesTargetResolutionOrFiveHundredFeet()
    {
        var first = CreateAircraft("N123AB", "C172", flightRules: "VFR", new LatLon(37.6213, -122.3790), altitude: 2500, isOnGround: false);
        var second = CreateAircraft(
            "N456CD",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(first.Position, new TrueHeading(90), 0.2),
            altitude: 2500,
            isOnGround: false
        );
        var requirement = SoloTrainingEvaluator.ResolveRequirement(first, second, AirspaceDatabase.Default);

        Assert.NotNull(requirement);
        Assert.Equal(0.25, requirement.RequiredHorizontalNm);
        Assert.Equal(500.0, requirement.RequiredVerticalFt);
        Assert.Contains("target-resolution", requirement.Name);
    }

    [Fact]
    public void Evaluate_ClassCIfrVfrPairInsideCharlie_UsesTargetResolutionOrFiveHundredFeet()
    {
        var ifr = CreateAircraft("AAL1", "C172", flightRules: "IFR", new LatLon(37.7213, -122.2208), altitude: 1000, isOnGround: false);
        var vfr = CreateAircraft(
            "N123AB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(ifr.Position, new TrueHeading(90), 0.2),
            altitude: 1000,
            isOnGround: false
        );
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([ifr, vfr], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);

        var separation = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.Separation);
        Assert.Equal(SoloTrainingEventSeverity.Safety, separation.Severity);
        Assert.Contains("7110.65 §7-8-3", separation.RuleReference);
        Assert.Equal(0.25, separation.RequiredHorizontalNm);
        Assert.Equal(500.0, separation.RequiredVerticalFt);
    }

    [Fact]
    public void Evaluate_ClassCIfrVfrPairOutsideCharlie_DoesNotRecordSeparationEvent()
    {
        var ifr = CreateAircraft("AAL1", "C172", flightRules: "IFR", new LatLon(37.0000, -121.0000), altitude: 2500, isOnGround: false);
        var vfr = CreateAircraft(
            "N123AB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(ifr.Position, new TrueHeading(90), 0.2),
            altitude: 2500,
            isOnGround: false
        );
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([ifr, vfr], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 20, new ApproachReportData([], [], 20, "N/A"));

        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_ClassCVfrPairInsideCharlie_DoesNotRecordSeparationEvent()
    {
        var first = CreateAircraft("N123AB", "C172", flightRules: "VFR", new LatLon(37.7213, -122.2208), altitude: 1000, isOnGround: false);
        var second = CreateAircraft(
            "N456CD",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(first.Position, new TrueHeading(90), 0.2),
            altitude: 1000,
            isOnGround: false
        );
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([first, second], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 20, new ApproachReportData([], [], 20, "N/A"));

        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_ProjectedClassCEntryUsesProjectedAirspaceForCoach()
    {
        var ifr = CreateAircraft("AAL1", "C172", flightRules: "IFR", new LatLon(37.8300, -122.4200), altitude: 2000, isOnGround: false);
        var vfr = CreateAircraft(
            "N123AB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(ifr.Position, new TrueHeading(90), 0.2),
            altitude: 2000,
            isOnGround: false
        );
        ifr.TrueTrack = new TrueHeading(90);
        ifr.IndicatedAirspeed = 120;
        vfr.TrueTrack = new TrueHeading(90);
        vfr.IndicatedAirspeed = 120;
        var evaluator = new SoloTrainingEvaluator();

        Assert.Null(SoloTrainingEvaluator.ResolveRequirement(ifr, vfr, AirspaceDatabase.Default));
        Assert.NotNull(SoloTrainingEvaluator.ResolveRequirement(ifr, vfr, AirspaceDatabase.Default, lookaheadSeconds: 60));

        var notices = evaluator.Evaluate([ifr, vfr], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);

        var separation = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.Separation);
        Assert.Equal(SoloTrainingEventSeverity.Coach, separation.Severity);
        Assert.Contains("7110.65 §7-8-3", separation.RuleReference);
    }

    [Fact]
    public void ResolveRequirement_SkipsClassCShelfUntilProjectedAltitudeEntersVolume()
    {
        var ifr = CreateAircraft("AAL1", "C172", flightRules: "IFR", new LatLon(37.8400, -122.3000), altitude: 1000, isOnGround: false);
        var vfr = CreateAircraft(
            "N123AB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(ifr.Position, new TrueHeading(90), 0.2),
            altitude: 1000,
            isOnGround: false
        );

        Assert.Null(SoloTrainingEvaluator.ResolveRequirement(ifr, vfr, AirspaceDatabase.Default));

        ifr.VerticalSpeed = 600;
        vfr.VerticalSpeed = 600;

        var projectedRequirement = SoloTrainingEvaluator.ResolveRequirement(ifr, vfr, AirspaceDatabase.Default, lookaheadSeconds: 60);
        Assert.NotNull(projectedRequirement);
        Assert.Contains("7110.65 §7-8-3", projectedRequirement.RuleReference);
    }

    [Fact]
    public void Evaluate_ClassCMissingTrafficAdvisoryUsesDirectedRtisProof()
    {
        var ifr = CreateAircraft("AAL1", "C172", flightRules: "IFR", new LatLon(37.7213, -122.2208), altitude: 1000, isOnGround: false);
        var vfr = CreateAircraft(
            "N123AB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(ifr.Position, new TrueHeading(90), 0.2),
            altitude: 1000,
            isOnGround: false
        );
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(ifr, SingleCommand(new ReportTrafficInSightCommand(vfr.Callsign)), scenarioElapsedSeconds: 19);

        var notices = evaluator.Evaluate([ifr, vfr], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);

        Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.Separation);
        var advisory = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual);
        Assert.Equal(vfr.Callsign, advisory.Callsigns[0]);
        Assert.Equal(ifr.Callsign, advisory.Callsigns[1]);
        Assert.Contains("7110.65 §7-8-2", advisory.RuleReference);
    }

    [Fact]
    public void Evaluate_VisualFollowPair_DoesNotRecordSeparationEvent()
    {
        var lead = CreateAircraft("N111AA", "C172", flightRules: "VFR", new LatLon(37.6213, -122.3790), altitude: 2500, isOnGround: false);
        var follower = CreateAircraft(
            "N222BB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(lead.Position, new TrueHeading(90), 0.1),
            altitude: 2500,
            isOnGround: false
        );
        follower.Approach.HasReportedTrafficInSight = true;
        follower.Approach.FollowingCallsign = lead.Callsign;
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 30, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 30, new ApproachReportData([], [], 30, "N/A"));

        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_MissingTrafficAdvisoryForProjectedSeparationLoss_RecordsAdvisoryEvents()
    {
        var (a, b) = CreateClosingIfrPair();
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        var advisories = notices.Where(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual).ToList();
        Assert.Equal(2, advisories.Count);
        Assert.All(advisories, e => Assert.Equal(SoloTrainingEventSeverity.Warning, e.Severity));
        Assert.All(advisories, e => Assert.Contains("7110.65 §2-1-21", e.RuleReference));
        Assert.All(advisories, e => Assert.Contains("RTIS/RTISF", e.ActualText));
    }

    [Fact]
    public void Evaluate_RtisProofSuppressesOnlyDirectedAdvisory()
    {
        var (a, b) = CreateConflictingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(a, SingleCommand(new ReportTrafficInSightCommand(b.Callsign)), scenarioElapsedSeconds: 5);

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        var advisory = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual);
        Assert.Equal(b.Callsign, advisory.Callsigns[0]);
        Assert.Equal(a.Callsign, advisory.Callsigns[1]);
    }

    [Fact]
    public void Evaluate_RtisfProofSuppressesAdvisoryScoring()
    {
        var (a, b) = CreateConflictingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(a, SingleCommand(new ReportTrafficInSightForcedCommand(b.Callsign)), scenarioElapsedSeconds: 5);
        evaluator.RecordControllerCommand(b, SingleCommand(new ReportTrafficInSightForcedCommand(a.Callsign)), scenarioElapsedSeconds: 6);

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        Assert.DoesNotContain(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual);
        Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.Separation);
    }

    [Fact]
    public void Evaluate_BareRtisCountsWhenLastReportedTrafficCallsignResolves()
    {
        var (a, b) = CreateConflictingIfrPair();
        a.Approach.LastReportedTrafficCallsign = b.Callsign;
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(a, SingleCommand(new ReportTrafficInSightCommand(null)), scenarioElapsedSeconds: 5);

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        var advisory = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual);
        Assert.Equal(b.Callsign, advisory.Callsigns[0]);
        Assert.Equal(a.Callsign, advisory.Callsigns[1]);
    }

    [Fact]
    public void Evaluate_QueuedRtisDoesNotCountBeforeDispatch()
    {
        var (a, b) = CreateConflictingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        var queuedCommand = new CompoundCommand([
            new ParsedBlock(null, [new WaitCommand(10)]),
            new ParsedBlock(null, [new ReportTrafficInSightCommand(b.Callsign)]),
        ]);
        evaluator.RecordControllerCommand(a, queuedCommand, scenarioElapsedSeconds: 5);

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        Assert.Equal(2, notices.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual));
    }

    [Fact]
    public void Evaluate_AdvisoryProofAfterActiveEventClearsOnNextEvaluation()
    {
        var (a, b) = CreateConflictingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        evaluator.RecordControllerCommand(a, SingleCommand(new ReportTrafficInSightCommand(b.Callsign)), scenarioElapsedSeconds: 11);
        evaluator.RecordControllerCommand(b, SingleCommand(new ReportTrafficInSightCommand(a.Callsign)), scenarioElapsedSeconds: 11);
        evaluator.Evaluate([a, b], scenarioElapsedSeconds: 12, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 12, new ApproachReportData([], [], 12, "N/A"));

        Assert.DoesNotContain(report.ActiveEvents, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual);
        Assert.Equal(2, report.Timeline.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual));
    }

    [Fact]
    public void Evaluate_WrongRtisTargetDoesNotSuppressAdvisory()
    {
        var (a, b) = CreateConflictingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(a, SingleCommand(new ReportTrafficInSightForcedCommand("DAL9")), scenarioElapsedSeconds: 5);
        evaluator.RecordControllerCommand(b, SingleCommand(new ReportTrafficInSightForcedCommand(a.Callsign)), scenarioElapsedSeconds: 6);

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        var advisory = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual);
        Assert.Equal(a.Callsign, advisory.Callsigns[0]);
        Assert.Equal(b.Callsign, advisory.Callsigns[1]);
    }

    [Fact]
    public void Evaluate_MissingTrafficAdvisory_DedupesAcrossRepeatedTicks()
    {
        var (a, b) = CreateConflictingIfrPair();
        var evaluator = new SoloTrainingEvaluator();

        var first = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);
        var second = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 11, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 11, new ApproachReportData([], [], 11, "N/A"));

        Assert.Equal(2, first.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual));
        Assert.Empty(second);
        Assert.Equal(2, report.Timeline.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual));
    }

    [Fact]
    public void Reset_ClearsTrafficAdvisoryProofAndEvents()
    {
        var (a, b) = CreateConflictingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(a, SingleCommand(new ReportTrafficInSightForcedCommand(b.Callsign)), scenarioElapsedSeconds: 5);
        evaluator.RecordControllerCommand(b, SingleCommand(new ReportTrafficInSightForcedCommand(a.Callsign)), scenarioElapsedSeconds: 6);
        Assert.DoesNotContain(
            evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default),
            e => e.Category == SoloTrainingEventCategory.AdvisoryVisual
        );

        evaluator.Reset();
        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 11, AirspaceDatabase.Default);

        Assert.Equal(2, notices.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual));
    }

    [Fact]
    public void SendCommand_RtisfRecordsTrafficAdvisoryProofIntoEvaluator()
    {
        var (a, b) = CreateConflictingIfrPair();
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 5) };
        engine.World.AddAircraft(a);
        engine.World.AddAircraft(b);

        var result = engine.SendCommand(a.Callsign, $"RTISF {b.Callsign}");
        var notices = engine.SoloTrainingEvaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        Assert.True(result.Success, result.Message);
        var advisory = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual);
        Assert.Equal(b.Callsign, advisory.Callsigns[0]);
        Assert.Equal(a.Callsign, advisory.Callsigns[1]);
    }

    [Fact]
    public void Evaluate_DepartureBehindDepartureBeforeDerOrSpacing_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 2500);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 11, AirspaceDatabase.Default);
        var notice = Assert.Single(notices);
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, notice.Category);
        Assert.Equal(SoloTrainingEventSeverity.Safety, notice.Severity);
        Assert.Equal("28R", notice.RunwayId);
        Assert.Contains("7110.65 §3-9-6(a)", notice.RuleReference);
        Assert.Contains("6,000 ft", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_DepartureBehindDepartureAfterDer_DoesNotRecordViolation()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 11, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 11, new ApproachReportData([], [], 11, "N/A"));
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_DepartureBehindLandingStillOnRunway_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("N111AA", "C172", flightRules: "VFR", PositionOnRunway(runway, 1500), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new LandingPhase());
        var follower = CreateAircraft("N222BB", "C172", flightRules: "VFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);

        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 21, AirspaceDatabase.Default);
        var notice = Assert.Single(notices);
        Assert.Contains("7110.65 §3-9-6(b)", notice.RuleReference);
        Assert.Contains("clear of the runway", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_ArrivalBehindDepartureInsideRequiredThresholdDistance_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 2500), altitude: 500, isOnGround: false);
        SetPhase(lead, runway, new InitialClimbPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(runway, -500), altitude: 100, isOnGround: false);
        SetPhase(follower, runway, new FinalApproachPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 30, AirspaceDatabase.Default);

        follower.Position = PositionOnRunway(runway, 100);
        SetPhase(follower, runway, new LandingPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 31, AirspaceDatabase.Default);
        var notice = Assert.Single(notices);
        Assert.Contains("7110.65 §3-10-3(a)(2)", notice.RuleReference);
        Assert.Contains("6,000 ft", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_ArrivalBehindLandedAircraft_AppliesCategoryOneDistanceException()
    {
        var runway = CreateRunway();
        var evaluator = new SoloTrainingEvaluator();
        var lead = CreateAircraft("N111AA", "C172", flightRules: "VFR", PositionOnRunway(runway, 2500), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new LandingPhase());
        var follower = CreateAircraft("N222BB", "C172", flightRules: "VFR", PositionOnRunway(runway, -500), altitude: 100, isOnGround: false);
        SetPhase(follower, runway, new FinalApproachPhase());
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 40, AirspaceDatabase.Default);

        follower.Position = PositionOnRunway(runway, 100);
        SetPhase(follower, runway, new LandingPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 41, AirspaceDatabase.Default);
        var notice = Assert.Single(notices);
        Assert.Contains("7110.65 §3-10-3(a)(1)", notice.RuleReference);
        Assert.Contains("3,000 ft", notice.RequiredText);

        evaluator.Reset();
        lead.Position = PositionOnRunway(runway, 3500);
        follower.Position = PositionOnRunway(runway, -500);
        SetPhase(follower, runway, new FinalApproachPhase());
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 50, AirspaceDatabase.Default);

        follower.Position = PositionOnRunway(runway, 100);
        SetPhase(follower, runway, new LandingPhase());

        notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 51, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 51, new ApproachReportData([], [], 51, "N/A"));
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_SameRunwayViolation_DedupesAcrossRepeatedTicks()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 60, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 2500);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var first = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 61, AirspaceDatabase.Default);
        var second = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 62, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 62, new ApproachReportData([], [], 62, "N/A"));

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(report.Timeline);
        Assert.Single(report.ActiveEvents);
    }

    [Fact]
    public void Reset_ClearsSameRunwayTrackerState()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 70, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 2500);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());
        Assert.Single(evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 71, AirspaceDatabase.Default));

        evaluator.Reset();

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 72, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 72, new ApproachReportData([], [], 72, "N/A"));
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_DepartureWakeIntervalBeforeRequiredTime_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default);
        var notice = Assert.Single(notices);
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, notice.Category);
        Assert.Contains("7110.65 §3-9-6(f)", notice.RuleReference);
        Assert.Contains("2 minutes", notice.RequiredText);
        Assert.Contains("60 seconds", notice.ActualText);
    }

    [Fact]
    public void Evaluate_DepartureWakeIntervalAfterRequiredTime_DoesNotRecordViolation()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 221, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 221, new ApproachReportData([], [], 221, "N/A"));
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_DepartureWakeIntervalBeforeRequiredTime_DoesNotRecordWhenCwtDistanceExists()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 6.0 * GeoMath.FeetPerNm);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 160, new ApproachReportData([], [], 160, "N/A"));
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_ParallelRunwayUnderWakeThreshold_AppliesDepartureWakeInterval()
    {
        var leadRunway = CreateRunway();
        var followerRunway = CreateParallelRunway("28L", offsetFt: 1000);
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(leadRunway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, leadRunway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(followerRunway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, followerRunway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(leadRunway, leadRunway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, leadRunway, new InitialClimbPhase());
        SetPhase(follower, followerRunway, new TakeoffPhase());

        var notice = Assert.Single(evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default));
        Assert.Contains("7110.65 §3-9-6(f)", notice.RuleReference);
        Assert.Contains("parallel runways less than 2,500 ft", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_ParallelRunwayOutsideWakeThreshold_DoesNotApplyDepartureWakeInterval()
    {
        var leadRunway = CreateRunway();
        var followerRunway = CreateParallelRunway("28L", offsetFt: 3000);
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(leadRunway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, leadRunway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(followerRunway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, followerRunway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(leadRunway, leadRunway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, leadRunway, new InitialClimbPhase());
        SetPhase(follower, followerRunway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 160, new ApproachReportData([], [], 160, "N/A"));
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_IntersectionDepartureWakeInterval_UsesSection397()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("UAL1", "B752", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("N222BB", "C172", flightRules: "VFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notice = Assert.Single(evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 220, AirspaceDatabase.Default));
        Assert.Contains("7110.65 §3-9-7(a)", notice.RuleReference);
        Assert.Contains("3 minutes", notice.RequiredText);
        Assert.Contains("120 seconds", notice.ActualText);
    }

    [Fact]
    public void Evaluate_IntersectionDepartureWakeIntervalWithoutMileAlternative_ReportsTimeOnly()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("FFT1", "A320", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("N222BB", "C172", flightRules: "VFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notice = Assert.Single(evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default));
        Assert.Contains("7110.65 §3-9-7(a)", notice.RuleReference);
        Assert.Contains("3 minutes", notice.RequiredText);
        Assert.DoesNotContain("CWT spacing", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_TerminalApproachCwtSpacingInsideRequiredDistance_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("UAE1", "A388", flightRules: "IFR", PositionOnRunway(runway, -100), altitude: 100, isOnGround: false);
        SetPhase(lead, runway, new FinalApproachPhase());
        var follower = CreateAircraft(
            "SWA2",
            "B738",
            flightRules: "IFR",
            PositionOnRunway(runway, -6.0 * GeoMath.FeetPerNm),
            altitude: 700,
            isOnGround: false
        );
        SetPhase(follower, runway, new FinalApproachPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 300, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 0);
        SetPhase(lead, runway, new LandingPhase());

        var notice = Assert.Single(evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 301, AirspaceDatabase.Default));
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, notice.Category);
        Assert.Contains("7110.65 §5-5-4(h)", notice.RuleReference);
        Assert.Contains("7 NM", notice.RequiredText);
        Assert.Contains("6.0 NM", notice.ActualText);
    }

    [Fact]
    public void Evaluate_TerminalApproachCwtSpacingAtRequiredDistance_DoesNotRecordViolation()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("UAE1", "A388", flightRules: "IFR", PositionOnRunway(runway, -100), altitude: 100, isOnGround: false);
        SetPhase(lead, runway, new FinalApproachPhase());
        var follower = CreateAircraft(
            "SWA2",
            "B738",
            flightRules: "IFR",
            PositionOnRunway(runway, -8.0 * GeoMath.FeetPerNm),
            altitude: 700,
            isOnGround: false
        );
        SetPhase(follower, runway, new FinalApproachPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 300, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 0);
        SetPhase(lead, runway, new LandingPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 301, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 301, new ApproachReportData([], [], 301, "N/A"));
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_WakeIntervalViolation_DedupesAcrossRepeatedTicks()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var first = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default);
        var second = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 161, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 161, new ApproachReportData([], [], 161, "N/A"));

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(report.Timeline);
        Assert.Single(report.ActiveEvents);
    }

    private static AircraftState CreateAircraft(
        string callsign,
        string type,
        string flightRules,
        LatLon position,
        double altitude,
        bool isOnGround
    ) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = position,
            TrueHeading = new TrueHeading(90),
            TrueTrack = new TrueHeading(90),
            Altitude = altitude,
            IndicatedAirspeed = 180,
            IsOnGround = isOnGround,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                AircraftType = type,
                FlightRules = flightRules,
            },
        };

    private static (AircraftState A, AircraftState B) CreateConflictingIfrPair()
    {
        var a = CreateAircraft("AAL1", "B738", flightRules: "IFR", new LatLon(37.6213, -122.3790), altitude: 5000, isOnGround: false);
        var b = CreateAircraft(
            "UAL2",
            "A320",
            flightRules: "IFR",
            GeoMath.ProjectPoint(a.Position, new TrueHeading(90), 2.5),
            altitude: 5000,
            isOnGround: false
        );
        return (a, b);
    }

    private static (AircraftState A, AircraftState B) CreateClosingIfrPair()
    {
        var a = CreateAircraft("AAL1", "B738", flightRules: "IFR", new LatLon(37.6213, -122.3790), altitude: 5000, isOnGround: false);
        var b = CreateAircraft(
            "UAL2",
            "A320",
            flightRules: "IFR",
            GeoMath.ProjectPoint(a.Position, new TrueHeading(90), 5.0),
            altitude: 5000,
            isOnGround: false
        );
        a.TrueHeading = new TrueHeading(90);
        a.TrueTrack = new TrueHeading(90);
        b.TrueHeading = new TrueHeading(270);
        b.TrueTrack = new TrueHeading(270);
        return (a, b);
    }

    private static CompoundCommand SingleCommand(ParsedCommand command) => new([new ParsedBlock(null, [command])]);

    private static SimScenarioState NewScenario(bool soloTrainingMode, double elapsedSeconds) =>
        new()
        {
            ScenarioId = "solo-training-evaluator-test",
            ScenarioName = "Solo Training Evaluator Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            ElapsedSeconds = elapsedSeconds,
            SoloTrainingMode = soloTrainingMode,
        };

    private static RunwayInfo CreateRunway() =>
        new()
        {
            AirportId = "KOAK",
            Id = new RunwayIdentifier("28R", "10L"),
            Designator = "28R",
            Lat1 = 37.721300,
            Lon1 = -122.220000,
            Elevation1Ft = 10,
            TrueHeading1 = new TrueHeading(90),
            Lat2 = 37.721300,
            Lon2 = -122.190000,
            Elevation2Ft = 10,
            TrueHeading2 = new TrueHeading(270),
            LengthFt = 10000,
            WidthFt = 150,
        };

    private static RunwayInfo CreateParallelRunway(string designator, double offsetFt)
    {
        var baseRunway = CreateRunway();
        var start = GeoMath.ProjectPoint(new LatLon(baseRunway.Lat1, baseRunway.Lon1), new TrueHeading(0), offsetFt / GeoMath.FeetPerNm);
        var end = GeoMath.ProjectPoint(new LatLon(baseRunway.Lat2, baseRunway.Lon2), new TrueHeading(0), offsetFt / GeoMath.FeetPerNm);
        return new RunwayInfo
        {
            AirportId = baseRunway.AirportId,
            Id = new RunwayIdentifier(designator, "10R"),
            Designator = designator,
            Lat1 = start.Lat,
            Lon1 = start.Lon,
            Elevation1Ft = baseRunway.Elevation1Ft,
            TrueHeading1 = baseRunway.TrueHeading1,
            Lat2 = end.Lat,
            Lon2 = end.Lon,
            Elevation2Ft = baseRunway.Elevation2Ft,
            TrueHeading2 = baseRunway.TrueHeading2,
            LengthFt = baseRunway.LengthFt,
            WidthFt = baseRunway.WidthFt,
        };
    }

    private static LatLon PositionOnRunway(RunwayInfo runway, double alongFt) =>
        GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading, alongFt / GeoMath.FeetPerNm);

    private static void SetPhase(AircraftState aircraft, RunwayInfo runway, Phase phase)
    {
        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(phase);
    }
}
